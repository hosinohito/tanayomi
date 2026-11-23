using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Tanayomi;

public interface ICredentialStore
{
    Task<string?> ReadAsync(CancellationToken ct);
    Task WriteAsync(string value, CancellationToken ct);
    Task DeleteAsync(CancellationToken ct);
}

/// <summary>Persists only OAuth credentials in an OS-protected store. Identity and
/// permissions are obtained from Twitch again on every restore.</summary>
public sealed class TwitchAccount(TwitchAuthentication authentication, ICredentialStore? store)
{
    private const int MaximumBytes = 16 * 1024;
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task<TwitchAuthorization?> RestoreAsync(CancellationToken ct)
    {
        if (store is null) return null;
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var refreshing = false;
        try
        {
            string? value;
            try { value = await store.ReadAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { throw StorageError("読み出せませんでした"); }
            if (value is null) return null;
            var saved = Read(value);
            ct.ThrowIfCancellationRequested();
            if (saved.ExpiresAt <= DateTimeOffset.UtcNow.AddMinutes(2))
            {
                refreshing = true;
                var refreshed = await authentication.RefreshAsync(saved.RefreshToken, saved.ClientId, ct).ConfigureAwait(false);
                // Device-flow refresh credentials are single use. Replace the saved
                // credential before allowing a caller to use the returned session.
                try { await WriteCoreAsync(refreshed, ct).ConfigureAwait(false); }
                catch
                {
                    await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    throw;
                }
                refreshing = false;
                return refreshed;
            }
            var identity = await authentication.ValidateAsync(saved.AccessToken, ct).ConfigureAwait(false);
            if (!identity.ClientId.Equals(saved.ClientId, StringComparison.Ordinal)) throw InvalidSaved();
            return new() { AccessToken = saved.AccessToken, RefreshToken = saved.RefreshToken, Identity = identity };
        }
        catch (SourceConfigurationException)
        {
            await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw InvalidSaved();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested && refreshing)
        {
            // The exchange might have consumed the token even if its response was
            // cancelled. Never leave that refresh credential available for a retry.
            await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        catch (HttpRequestException) when (refreshing)
        {
            await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
            throw InvalidSaved();
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(TwitchAuthorization authorization, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(authorization);
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            try { await WriteCoreAsync(authorization, ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException || ex is OperationCanceledException && ct.IsCancellationRequested)
            {
                // A caller may be saving a freshly rotated token. An older saved
                // refresh credential is unsafe if its replacement cannot be saved.
                await DeleteCoreAsync(CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task ForgetAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { await DeleteCoreAsync(ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private async Task WriteCoreAsync(TwitchAuthorization authorization, CancellationToken ct)
    {
        if (store is null) throw new IOException("この環境では認証情報を安全に保存できません。今回の認証は利用できますが、次回はログインが必要です。");
        CheckCredentials(authorization.AccessToken, authorization.RefreshToken, authorization.Identity.ClientId);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("version", 1);
            writer.WriteString("accessToken", authorization.AccessToken);
            writer.WriteString("refreshToken", authorization.RefreshToken);
            writer.WriteString("clientId", authorization.Identity.ClientId);
            writer.WriteString("expiresAt", authorization.Identity.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteEndObject();
        }
        if (buffer.Length > MaximumBytes) throw InvalidSaved();
        try { await store.WriteAsync(Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length), ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw StorageError("保存できませんでした"); }
    }

    private async Task DeleteCoreAsync(CancellationToken ct)
    {
        if (store is null) return;
        try { await store.DeleteAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw StorageError("削除できませんでした"); }
    }

    private static SavedCredential Read(string value)
    {
        try
        {
            if (value.Length > MaximumBytes || Encoding.UTF8.GetByteCount(value) > MaximumBytes) throw InvalidSaved();
            using var document = JsonDocument.Parse(value, new JsonDocumentOptions { MaxDepth = 4 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().GroupBy(p => p.Name, StringComparer.Ordinal).Any(g => g.Count() != 1) ||
                !root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var number) || number != 1) throw InvalidSaved();
            string Required(string name)
            {
                if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || property.GetString() is not { Length: > 0 } text) throw InvalidSaved();
                return text;
            }
            var access = Required("accessToken"); var refresh = Required("refreshToken"); var client = Required("clientId");
            CheckCredentials(access, refresh, client);
            if (!DateTimeOffset.TryParseExact(Required("expiresAt"), "O", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expires)) throw InvalidSaved();
            return new() { AccessToken = access, RefreshToken = refresh, ClientId = client, ExpiresAt = expires };
        }
        catch (JsonException) { throw InvalidSaved(); }
        catch (InvalidOperationException) { throw InvalidSaved(); }
    }

    private static void CheckCredentials(string access, string refresh, string client)
    {
        if (access != TwitchAuthentication.NormalizeToken(access) || refresh.Length is < 1 or > 4096 || refresh.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)) ||
            client.Length is < 1 or > 128 || !client.All(char.IsAsciiLetterOrDigit)) throw InvalidSaved();
    }

    private static SourceConfigurationException InvalidSaved() => new("保存されたTwitch認証を利用できません。もう一度ログインしてください。");
    private static IOException StorageError(string action) => new($"認証情報を安全に{action}。今回の認証は利用できますが、次回はログインが必要になる場合があります。");

    // Object.ToString() exposes only this type name, never tokens.
    private sealed class SavedCredential
    {
        public required string AccessToken { get; init; }
        public required string RefreshToken { get; init; }
        public required string ClientId { get; init; }
        public required DateTimeOffset ExpiresAt { get; init; }
    }
}
