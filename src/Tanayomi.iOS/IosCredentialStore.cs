using Foundation;
using Security;
using System.Security.Cryptography;
using System.Text;

namespace Tanayomi.iOS;

/// <summary>Device-bound, nonsynchronizing app Keychain item. No credential file
/// is created, and an inaccessible Keychain never falls back to plain storage.</summary>
public sealed class IosCredentialStore : ICredentialStore
{
    private const int MaximumBytes = 16384;
    private const string Account = "twitch-device-oauth-v1";
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim Serial = new(1, 1);
    private readonly string service = (NSBundle.MainBundle.BundleIdentifier ?? throw Failure()) + ".Tanayomi.Twitch.v1";

    public Task<string?> ReadAsync(CancellationToken ct) => RunAsync<string?>(() =>
    {
        using var query = Query();
        using var data = SecKeyChain.QueryAsData(query, false, out var status);
        if (status == SecStatusCode.ItemNotFound) return null;
        if (status != SecStatusCode.Success || data is null || data.Length == 0 || data.Length > MaximumBytes) throw Failure();
        byte[]? clear = null;
        try
        {
            clear = data.ToArray();
            ct.ThrowIfCancellationRequested();
            return Utf8.GetString(clear);
        }
        finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); }
    }, ct);

    public Task WriteAsync(string value, CancellationToken ct) => RunAsync(() =>
    {
        if (string.IsNullOrEmpty(value) || Utf8.GetByteCount(value) > MaximumBytes) throw Failure();
        var clear = Utf8.GetBytes(value);
        try
        {
            using var data = NSData.FromArray(clear) ?? throw Failure();
            using var query = Query();
            using var changes = new SecRecord
            {
                ValueData = data,
                Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly,
                Synchronizable = false
            };
            ct.ThrowIfCancellationRequested();
            var status = SecKeyChain.Update(query, changes);
            if (status == SecStatusCode.ItemNotFound)
            {
                using var item = Identity();
                item.ValueData = data;
                item.Accessible = SecAccessible.AfterFirstUnlockThisDeviceOnly;
                ct.ThrowIfCancellationRequested();
                status = SecKeyChain.Add(item);
                if (status == SecStatusCode.DuplicateItem) status = SecKeyChain.Update(query, changes);
            }
            if (status != SecStatusCode.Success) throw Failure();
            return true;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }, ct);

    public Task DeleteAsync(CancellationToken ct) => RunAsync(() =>
    {
        using var query = Query();
        var status = SecKeyChain.Remove(query);
        if (status is not (SecStatusCode.Success or SecStatusCode.ItemNotFound)) throw Failure();
        return true;
    }, ct);

    private SecRecord Identity() => new(SecKind.GenericPassword)
    {
        Service = service,
        Account = Account,
        Synchronizable = false
        // No shared AccessGroup: use this signed application's default group.
    };

    private SecRecord Query()
    {
        var record = Identity();
        record.AuthenticationUI = SecAuthenticationUI.Fail;
        return record;
    }

    private static async Task<T> RunAsync<T>(Func<T> operation, CancellationToken ct)
    {
        await Serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return operation(); }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failure(); }
        finally { Serial.Release(); }
    }

    private static IOException Failure() => new("iOSのKeychainで認証情報を安全に読み書き・削除できません。端末のロック状態を確認してください。平文保存は行いません。");
}
