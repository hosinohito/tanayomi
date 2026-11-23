using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Tanayomi;

public sealed class SourceConfigurationException(string message) : Exception(message);
public sealed class SourceEndedException : Exception;

public static class ChatSources
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(25), MaxResponseContentBufferSize = 2 * 1024 * 1024 };

    public static async Task SuperviseAsync(Func<CancellationToken, Task> connect,
        Action<string> status, CancellationToken cancellationToken)
    {
        var failures = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            try { await connect(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (SourceConfigurationException ex) { status(ex.Message); return; }
            catch (SourceEndedException) { status("配信またはチャットが終了しました。"); return; }
            catch (Exception ex) { status($"接続が中断しました（{ex.GetType().Name}）。再接続します。"); }
            if (Stopwatch.GetElapsedTime(started) > TimeSpan.FromMinutes(1)) failures = 0;
            var delay = Math.Min(30, Math.Pow(2, Math.Min(failures++, 5))) + Random.Shared.NextDouble();
            try { await Task.Delay(TimeSpan.FromSeconds(delay), cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
        }
    }

    public static async Task TwitchIrcAsync(string channel, string login, string token,
        Action<ChatMessage> receive, Action<string> status, CancellationToken cancellationToken, bool anonymous = false)
    {
        // Reject malformed configuration before opening a network connection.
        (channel, login, token) = PrepareTwitchIrc(channel, login, token, anonymous);
        status(anonymous ? "Twitchへ匿名で接続中…" : "Twitchへ接続中…");
        using var tcp = new TcpClient { NoDelay = true };
        await tcp.ConnectAsync("irc.chat.twitch.tv", 6697, cancellationToken);
        await using var tls = new SslStream(tcp.GetStream());
        await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "irc.chat.twitch.tv" }, cancellationToken);
        await TwitchIrcSessionAsync(tls, channel, login, token, receive, status, cancellationToken, anonymous);
    }

    // The same protocol is used for real TLS streams and bounded, offline protocol tests.
    // The caller owns the stream; this method never sends channel chat messages.
    public static async Task TwitchIrcSessionAsync(Stream stream, string channel, string login, string token,
        Action<ChatMessage> receive, Action<string> status, CancellationToken cancellationToken, bool anonymous = false)
    {
        (channel, login, token) = PrepareTwitchIrc(channel, login, token, anonymous);
        using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
        if (!anonymous) await writer.WriteLineAsync($"PASS oauth:{token}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync($"NICK {login}".AsMemory(), cancellationToken);
        await writer.WriteLineAsync("CAP REQ :twitch.tv/tags twitch.tv/commands".AsMemory(), cancellationToken);
        await writer.WriteLineAsync($"JOIN #{channel}".AsMemory(), cancellationToken);
        while (!cancellationToken.IsCancellationRequested)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromMinutes(5));
            var line = await ReadBoundedLineAsync(reader, timeout.Token);
            if (line is null) throw new IOException();
            var command = IrcCommand(line);
            if (line.StartsWith("PING ", StringComparison.Ordinal))
                await writer.WriteLineAsync(("PONG " + line[5..]).AsMemory(), cancellationToken);
            else if (command == "RECONNECT")
                return;
            else if (command == "001") status(anonymous
                ? "Twitchに匿名接続しました · IRC / TLS（読み取り専用）"
                : "Twitchに接続しました · IRC / TLS");
            else if ((command == "366" && line.Contains($" 366 {login} #{channel} ", StringComparison.Ordinal)) ||
                     (command == "ROOMSTATE" && line.EndsWith($" #{channel}", StringComparison.Ordinal)))
                status(anonymous ? $"#{channel} のチャットを匿名で受信中 · IRC / TLS" : $"#{channel} のチャットを受信中 · IRC / TLS");
            else if (command == "NOTICE" && line.Contains("NOTICE * :", StringComparison.Ordinal) &&
                     (line.Contains("authentication", StringComparison.OrdinalIgnoreCase) || line.Contains("Login unsuccessful", StringComparison.OrdinalIgnoreCase) || line.Contains("Improperly formatted auth", StringComparison.OrdinalIgnoreCase)))
                throw new SourceConfigurationException(anonymous
                    ? "Twitchの匿名接続が拒否されました。認証して接続してください。"
                    : "IRC認証に失敗しました。「Twitchで認証して接続」から再認証してください。");
            else if (ParseIrc(line) is { } message) receive(message);
        }
    }

    private static string IrcCommand(string line)
    {
        var start = line.StartsWith('@') ? line.IndexOf(' ') + 1 : 0;
        if (start >= line.Length || (line.StartsWith('@') && start == 0)) return "";
        if (line[start] == ':')
        {
            var prefixEnd = line.IndexOf(' ', start);
            if (prefixEnd < 0) return "";
            start = prefixEnd + 1;
        }
        if (start >= line.Length) return "";
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }

    private static (string Channel, string Login, string Token) PrepareTwitchIrc(string channel, string login, string token, bool anonymous)
    {
        channel = channel.Trim().TrimStart('#').ToLowerInvariant();
        if (!ValidLogin(channel))
            throw new SourceConfigurationException("チャンネル名は英数字・アンダースコアで指定してください。匿名接続でもチャンネルの指定が必要です。");
        if (anonymous)
            return (channel, "justinfan" + RandomNumberGenerator.GetInt32(10000, int.MaxValue).ToString(System.Globalization.CultureInfo.InvariantCulture), "");
        login = login.Trim().ToLowerInvariant();
        token = TwitchAuthentication.NormalizeToken(token);
        if (!ValidLogin(login)) throw new SourceConfigurationException("ユーザー名は英数字・アンダースコアで指定してください。");
        return (channel, login, token);
    }

    private static bool ValidLogin(string value) => Regex.IsMatch(value, "^[a-z0-9_]{1,25}$");

    public static ChatMessage? ParseIrc(string line)
    {
        if (line.Length > 16384 || !line.StartsWith('@')) return null;
        var tagEnd = line.IndexOf(' ');
        var command = line.IndexOf(" PRIVMSG #", StringComparison.Ordinal);
        if (tagEnd < 0 || command < tagEnd) return null;
        var textStart = line.IndexOf(" :", command + 10, StringComparison.Ordinal);
        if (textStart < 0) return null;
        var tags = new Dictionary<string, string>();
        foreach (var tag in line[1..tagEnd].Split(';'))
        {
            var equals = tag.IndexOf('=');
            if (equals > 0) tags[tag[..equals]] = UnescapeTag(tag[(equals + 1)..]);
        }
        if (!tags.TryGetValue("id", out var id) || string.IsNullOrEmpty(id)) return null;
        // room-id is shared with EventSub broadcaster_user_id, even if the display name changes.
        var room = tags.GetValueOrDefault("room-id", line[(command + 10)..textStart]);
        return new("Twitch", room, id, tags.GetValueOrDefault("display-name", ""),
            line[(textStart + 2)..], "IRC", Stopwatch.GetTimestamp()) { AuthorId = tags.GetValueOrDefault("user-id", "") };
    }

    private static string UnescapeTag(string value) => Regex.Replace(value, @"\\(.)", m => m.Groups[1].Value switch
    { "s" => " ", ":" => ";", "r" => "\r", "n" => "\n", var c => c });

    private static async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken ct)
    {
        // IRC payloads are small; avoid ReadLineAsync's unbounded allocation on malformed input.
        var line = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), ct) != 0)
        {
            if (one[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length == 16384) throw new InvalidDataException();
            line.Append(one[0]);
        }
        return line.Length == 0 ? null : line.ToString();
    }

    public static async Task YouTubeAsync(string videoId, string key, Action<ChatMessage> receive,
        Action<string> status, CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(videoId, "^[A-Za-z0-9_-]{11}$") || string.IsNullOrWhiteSpace(key))
            throw new SourceConfigurationException("11文字の動画IDとAPIキーを確認してください。");
        var encodedKey = Uri.EscapeDataString(key);
        using var video = await GetYouTubeJsonAsync($"videos?part=liveStreamingDetails&id={videoId}&key={encodedKey}", cancellationToken);
        var items = video.RootElement.GetProperty("items");
        if (items.GetArrayLength() == 0 || !items[0].TryGetProperty("liveStreamingDetails", out var details) ||
            !details.TryGetProperty("activeLiveChatId", out var chatId)) throw new SourceConfigurationException("配信中の公開チャットが見つかりません。");
        var chat = chatId.GetString()!;
        string? page = null;
        var initial = true;
        while (!cancellationToken.IsCancellationRequested)
        {
            var path = $"liveChat/messages?part=snippet,authorDetails&liveChatId={Uri.EscapeDataString(chat)}&maxResults=200&key={encodedKey}";
            if (page is not null) path += "&pageToken=" + Uri.EscapeDataString(page);
            using var data = await GetYouTubeJsonAsync(path, cancellationToken);
            foreach (var message in ParseYouTube(data.RootElement, chat, initial)) receive(message);
            initial = false;
            page = data.RootElement.TryGetProperty("nextPageToken", out var next) ? next.GetString() : null;
            if (page is null) throw new SourceEndedException();
            status("YouTubeに接続しました · API指定の間隔で受信");
            var interval = data.RootElement.GetProperty("pollingIntervalMillis").GetInt64();
            // Never shorten the server's minimum polling interval.
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(1000, interval)), cancellationToken);
        }
    }

    public static IReadOnlyList<ChatMessage> ParseYouTube(JsonElement root, string chat, bool initial)
    {
        var messages = new List<ChatMessage>();
        if (initial) return messages;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException();
        if (!root.TryGetProperty("items", out var items)) return messages;
        if (items.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("snippet", out var snippet) ||
                snippet.ValueKind != JsonValueKind.Object ||
                !snippet.TryGetProperty("displayMessage", out var body) ||
                body.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(body.GetString()) ||
                !item.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(id.GetString())) continue;
            var author = item.TryGetProperty("authorDetails", out var details) && details.ValueKind == JsonValueKind.Object &&
                details.TryGetProperty("displayName", out var name) && name.ValueKind == JsonValueKind.String ? name.GetString() ?? "" : "";
            var authorId = details.ValueKind == JsonValueKind.Object && details.TryGetProperty("channelId", out var channelId)
                && channelId.ValueKind == JsonValueKind.String
                ? channelId.GetString() ?? "" : "";
            messages.Add(new("YouTube", chat, id.GetString() ?? "", author, body.GetString() ?? "", "list", Stopwatch.GetTimestamp())
                { AuthorId = authorId });
        }
        return messages;
    }

    private static async Task<JsonDocument> GetYouTubeJsonAsync(string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync("https://www.googleapis.com/youtube/v3/" + path, ct);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.BadRequest)
        {
            // Error reasons are safe to classify; never display the request URL containing the key.
            var error = await response.Content.ReadAsStringAsync(ct);
            if (error.Contains("liveChatEnded", StringComparison.Ordinal)) throw new SourceEndedException();
            throw new SourceConfigurationException("YouTubeの認証・利用枠・チャット公開状態を確認してください。");
        }
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
}
