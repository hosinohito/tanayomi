using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Tanayomi;

public sealed class TwitchDeviceAuthorization
{
    public required string ClientId { get; init; }
    public required string DeviceCode { get; init; }
    public required string UserCode { get; init; }
    public required Uri VerificationUri { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public required TimeSpan Interval { get; init; }
}

// Classes deliberately retain Object.ToString(): diagnostic output must not contain tokens.
public sealed class TwitchAuthorization
{
    public required string AccessToken { get; init; }
    public required string RefreshToken { get; init; }
    public required TwitchIdentity Identity { get; init; }
}

public sealed record TwitchIdentity(string Login, string UserId, string ClientId,
    IReadOnlyList<string> Scopes, DateTimeOffset ExpiresAt);

public sealed class TwitchAuthentication : IDisposable
{
    public const string DefaultClientId = "8p20bi4q3itcul007gyl6r6k6p3igr";
    private const string Scopes = "chat:read user:read:chat";
    private const string DeviceGrant = "urn:ietf:params:oauth:grant-type:device_code";
    private readonly HttpClient http;
    private readonly bool ownsHttp;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public TwitchAuthentication(HttpClient? http = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ownsHttp = http is null;
        this.http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        this.http.Timeout = TimeSpan.FromSeconds(15);
        this.http.MaxResponseContentBufferSize = 64 * 1024;
        this.delay = delay ?? Task.Delay;
    }

    public static string NormalizeToken(string input)
    {
        if (string.IsNullOrEmpty(input) || input.Length > 8192 || input.Any(char.IsControl)) throw InvalidToken();
        var value = input.Trim();
        if (value.StartsWith("oauth:", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        else if (value.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Host.Length == 0) throw InvalidToken();
            // Read the pasted callback locally; never navigate to a URL containing credentials.
            var query = ParseParameters(uri.Query);
            if (query.ContainsKey("error") || query.ContainsKey("access_token")) throw InvalidToken();
            value = TokenFromFragment(uri.Fragment);
        }
        else if (value.StartsWith('#') || value.StartsWith("access_token=", StringComparison.OrdinalIgnoreCase))
            value = TokenFromFragment(value);
        if (!IsAlphaNumeric(value, 512)) throw InvalidToken();
        return value;
    }

    public async Task<TwitchDeviceAuthorization> StartAsync(string clientId, CancellationToken ct)
    {
        clientId = NormalizeClientId(clientId);
        using var result = await PostAsync("device", new() { ["client_id"] = clientId, ["scopes"] = Scopes }, ct);
        EnsureSuccess(result);
        var root = result.Document.RootElement;
        var verification = RequiredString(root, "verification_uri");
        if (!Uri.TryCreate(verification, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
            !(uri.Host.Equals("www.twitch.tv", StringComparison.OrdinalIgnoreCase) || uri.Host.Equals("twitch.tv", StringComparison.OrdinalIgnoreCase)) ||
            !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.AbsolutePath != "/activate" || uri.Fragment.Length != 0)
            throw InvalidResponse();
        return new()
        {
            ClientId = clientId, DeviceCode = RequiredString(root, "device_code"),
            UserCode = RequiredString(root, "user_code"), VerificationUri = uri,
            ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(PositiveSeconds(root, "expires_in", 86400)),
            Interval = TimeSpan.FromSeconds(PositiveSeconds(root, "interval", 300))
        };
    }

    public async Task<TwitchAuthorization> CompleteAsync(TwitchDeviceAuthorization device, CancellationToken ct)
    {
        var clientId = NormalizeClientId(device.ClientId);
        if (string.IsNullOrWhiteSpace(device.DeviceCode) || device.DeviceCode.Length > 4096) throw InvalidResponse();
        var remaining = device.ExpiresAt - DateTimeOffset.UtcNow;
        var started = Stopwatch.GetTimestamp();
        var interval = device.Interval < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : device.Interval;
        if (interval > TimeSpan.FromMinutes(5)) throw InvalidResponse();
        var networkFailures = 0;
        while (remaining > Stopwatch.GetElapsedTime(started))
        {
            ct.ThrowIfCancellationRequested();
            if (interval >= remaining - Stopwatch.GetElapsedTime(started)) break;
            await delay(interval, ct);
            ct.ThrowIfCancellationRequested();
            if (remaining <= Stopwatch.GetElapsedTime(started)) break;
            Response result;
            try
            {
                result = await PostAsync("token", new()
                {
                    ["client_id"] = clientId, ["scopes"] = Scopes,
                    ["device_code"] = device.DeviceCode, ["grant_type"] = DeviceGrant
                }, ct);
            }
            catch (HttpRequestException) when (++networkFailures < 3)
            {
                interval = TimeSpan.FromSeconds(Math.Min(60, interval.TotalSeconds * 2) + Random.Shared.NextDouble());
                continue;
            }
            using (result)
            {
                if (result.IsSuccess) return await ReadAuthorizationAsync(result.Document.RootElement, clientId, ct);
                var error = ErrorCode(result.Document.RootElement);
                if (error == "authorization_pending") { networkFailures = 0; continue; }
                if (error == "slow_down") { interval += TimeSpan.FromSeconds(5); continue; }
                EnsureSuccess(result);
            }
        }
        ct.ThrowIfCancellationRequested();
        throw new SourceConfigurationException("Twitchの認証コードが期限切れです。もう一度ログインしてください。");
    }

    public async Task<TwitchIdentity> ValidateAsync(string token, CancellationToken ct)
    {
        token = NormalizeToken(token);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://id.twitch.tv/oauth2/validate");
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        using var result = await SendAsync(request, ct);
        EnsureSuccess(result);
        var root = result.Document.RootElement;
        var login = RequiredString(root, "login");
        var userId = RequiredString(root, "user_id");
        var clientId = RequiredString(root, "client_id");
        if (!IsAlphaNumeric(login.Replace("_", "", StringComparison.Ordinal), 100) || !userId.All(char.IsAsciiDigit) || !IsAlphaNumeric(clientId, 128))
            throw InvalidResponse();
        if (!root.TryGetProperty("scopes", out var scopes) || scopes.ValueKind != JsonValueKind.Array || scopes.GetArrayLength() > 100)
            throw InvalidResponse();
        var granted = scopes.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String ? item.GetString()! : throw InvalidResponse()).ToArray();
        if (!granted.Contains("chat:read", StringComparer.Ordinal) && !granted.Contains("user:read:chat", StringComparer.Ordinal))
            throw new SourceConfigurationException("Twitchチャットの読み取り権限がありません。もう一度ログインしてください。");
        return new(login, userId, clientId, Array.AsReadOnly(granted), DateTimeOffset.UtcNow.AddSeconds(PositiveSeconds(root, "expires_in", 315360000)));
    }

    public async Task<TwitchAuthorization> RefreshAsync(string refreshToken, string clientId, CancellationToken ct)
    {
        clientId = NormalizeClientId(clientId);
        if (string.IsNullOrEmpty(refreshToken) || refreshToken.Length > 4096 || refreshToken.Any(c => char.IsControl(c) || char.IsWhiteSpace(c)))
            throw new SourceConfigurationException("Twitchの更新用トークンが無効です。もう一度ログインしてください。");
        // Public-client refresh tokens are single use. A lost exchange response or a
        // failed validation after exchange leaves the old refresh token unsafe to retry.
        try
        {
            using var result = await PostAsync("token", new()
            { ["client_id"] = clientId, ["refresh_token"] = refreshToken, ["grant_type"] = "refresh_token" }, ct);
            EnsureSuccess(result);
            return await ReadAuthorizationAsync(result.Document.RootElement, clientId, ct);
        }
        catch (HttpRequestException)
        {
            throw new SourceConfigurationException("Twitch認証の更新結果を確認できませんでした。更新用トークンの再使用を避けるため、もう一度ログインしてください。");
        }
    }

    private async Task<TwitchAuthorization> ReadAuthorizationAsync(JsonElement root, string clientId, CancellationToken ct)
    {
        var access = NormalizeToken(RequiredString(root, "access_token"));
        var refresh = RequiredString(root, "refresh_token");
        if (refresh.Any(char.IsWhiteSpace)) throw InvalidResponse();
        if (!RequiredString(root, "token_type").Equals("bearer", StringComparison.OrdinalIgnoreCase)) throw InvalidResponse();
        var identity = await ValidateAsync(access, ct);
        if (!identity.ClientId.Equals(clientId, StringComparison.Ordinal))
            throw new SourceConfigurationException("Twitch認証のクライアントIDが一致しません。もう一度ログインしてください。");
        return new() { AccessToken = access, RefreshToken = refresh, Identity = identity };
    }

    private Task<Response> PostAsync(string endpoint, Dictionary<string, string> fields, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/" + endpoint)
        { Content = new FormUrlEncodedContent(fields) };
        return SendOwnedAsync(request, ct);
    }

    private async Task<Response> SendOwnedAsync(HttpRequestMessage request, CancellationToken ct)
    { using (request) return await SendAsync(request, ct); }

    private async Task<Response> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct);
            var bytes = await response.Content.ReadAsByteArrayAsync(ct);
            if (bytes.Length > 64 * 1024) throw InvalidResponse();
            var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw InvalidResponse(); }
            return new(response.StatusCode, document);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (OperationCanceledException) { throw new HttpRequestException("Twitch認証の通信が時間切れになりました。再試行してください。"); }
        catch (HttpRequestException) { throw new HttpRequestException("Twitch認証に接続できませんでした。接続状態を確認して再試行してください。"); }
        catch (JsonException) { throw InvalidResponse(); }
        catch (IOException) { throw new HttpRequestException("Twitch認証の応答を受信できませんでした。再試行してください。"); }
    }

    private static void EnsureSuccess(Response result)
    {
        if (result.IsSuccess) return;
        if (result.Status == HttpStatusCode.Unauthorized)
            throw new SourceConfigurationException("Twitchの認証が無効です。もう一度ログインしてください。");
        var code = ErrorCode(result.Document.RootElement);
        if (code is "access_denied" or "authorization_declined")
            throw new SourceConfigurationException("Twitchの認証が許可されませんでした。必要な場合はもう一度ログインしてください。");
        if (code is "expired_token" or "expired device code" or "invalid device code" or "invalid_grant" or "invalid refresh token")
            throw new SourceConfigurationException("Twitchの認証コードまたは更新用トークンが無効・期限切れです。もう一度ログインしてください。");
        if ((int)result.Status >= 500 || result.Status == HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("Twitch認証が一時的に利用できません。しばらく待って再試行してください。");
        throw new SourceConfigurationException("Twitch認証を完了できませんでした。クライアントIDが公開クライアントとして登録されているか確認してください。");
    }

    private static string ErrorCode(JsonElement root)
    {
        foreach (var name in new[] { "error", "message" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text)
                return text.ToLowerInvariant();
        return "";
    }

    private static string RequiredString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 and <= 4096 } text || text.Any(char.IsControl)) throw InvalidResponse();
        return text;
    }

    private static int PositiveSeconds(JsonElement root, string name, int max)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var seconds) || seconds <= 0 || seconds > max) throw InvalidResponse();
        return seconds;
    }

    private static string NormalizeClientId(string value)
    {
        value = value.Trim();
        if (!IsAlphaNumeric(value, 128)) throw new SourceConfigurationException("TwitchのクライアントIDを確認してください。");
        return value;
    }

    private static string TokenFromFragment(string fragment)
    {
        var values = ParseParameters(fragment);
        if (values.ContainsKey("error") || !values.TryGetValue("access_token", out var token)) throw InvalidToken();
        return token;
    }

    private static Dictionary<string, string> ParseParameters(string fragment)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in fragment.TrimStart('#', '?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2) throw InvalidToken();
            var key = Decode(pair[0]);
            if (!values.TryAdd(key, Decode(pair[1]))) throw InvalidToken();
        }
        return values;
    }

    private static string Decode(string value)
    {
        for (var i = 0; i < value.Length; i++)
            if (value[i] == '%' && (i + 2 >= value.Length || !Uri.IsHexDigit(value[i + 1]) || !Uri.IsHexDigit(value[i + 2]))) throw InvalidToken();
        return Uri.UnescapeDataString(value.Replace('+', ' '));
    }

    private static bool IsAlphaNumeric(string value, int max) => value.Length is > 0 && value.Length <= max && value.All(char.IsAsciiLetterOrDigit);
    private static SourceConfigurationException InvalidToken() => new("Twitchトークンの形式を確認してください。アクセストークン、oauth:付きトークン、または認証後のURLを入力できます。");
    private static SourceConfigurationException InvalidResponse() => new("Twitch認証の応答形式が不正です。もう一度ログインしてください。");
    public void Dispose() { if (ownsHttp) http.Dispose(); }

    private sealed class Response(HttpStatusCode status, JsonDocument document) : IDisposable
    {
        public HttpStatusCode Status => status;
        public bool IsSuccess => (int)status is >= 200 and <= 299;
        public JsonDocument Document => document;
        public void Dispose() => document.Dispose();
    }
}
