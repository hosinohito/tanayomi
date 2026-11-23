using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Generic-password items in the local macOS keychain; no shell or plaintext file.</summary>
internal sealed class MacCredentialStore(string installation) : ICredentialStore
{
    private const string Security = "/System/Library/Frameworks/Security.framework/Security";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const int Missing = -25300, Duplicate = -25299, MaximumBytes = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly Lazy<nint> SecurityHandle = new(() => NativeLibrary.Load(Security));
    private static readonly Lazy<nint> CoreHandle = new(() => NativeLibrary.Load(Core));
    private static readonly object NativeGate = new();
    private readonly SemaphoreSlim gate = new(1, 1);

    public Task<string?> ReadAsync(CancellationToken ct) => ExecuteAsync(() =>
    {
        using var query = Query();
        query.SetConstant("kSecReturnData", Boolean(true));
        query.SetConstant("kSecMatchLimit", Symbol("kSecMatchLimitOne"));
        var status = SecItemCopyMatching(query.Handle, out var result);
        try
        {
            if (status == Missing) return null;
            Check(status);
            if (result == 0 || CFGetTypeID(result) != CFDataGetTypeID()) throw Failed();
            var length = CFDataGetLength(result);
            if (length is < 1 or > MaximumBytes) throw Failed();
            var bytes = new byte[(int)length];
            try { Marshal.Copy(CFDataGetBytePtr(result), bytes, 0, bytes.Length); return Utf8.GetString(bytes); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { if (result != 0) CFRelease(result); }
    }, ct);

    public async Task WriteAsync(string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Utf8.GetByteCount(value) is < 1 or > MaximumBytes) throw Failed();
        await ExecuteAsync(() =>
        {
            var bytes = Utf8.GetBytes(value);
            try
            {
                using var query = Query();
                using var updates = new Dictionary();
                updates.SetData("kSecValueData", bytes);
                var status = SecItemUpdate(query.Handle, updates.Handle);
                if (status == Missing)
                {
                    query.SetData("kSecValueData", bytes);
                    query.SetString("kSecAttrLabel", "たなよみ Twitch認証");
                    status = SecItemAdd(query.Handle, 0);
                    if (status == Duplicate)
                    {
                        using var retry = Query();
                        status = SecItemUpdate(retry.Handle, updates.Handle);
                    }
                }
                Check(status);
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }, ct).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken ct) => await ExecuteAsync(() =>
    {
        using var query = Query();
        var status = SecItemDelete(query.Handle);
        if (status != Missing) Check(status);
        return true;
    }, ct).ConfigureAwait(false);

    private async Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            // No native call runs on the UI thread, and no delayed write may overtake
            // a subsequent delete. Authentication UI is disabled in the query.
            return await Task.Run(() =>
            {
                // Legacy file-based keychains also need their process-wide UI flag.
                // Keep its change scoped to the native calls and restore the prior state.
                lock (NativeGate)
                {
                    ct.ThrowIfCancellationRequested();
                    Check(SecKeychainGetUserInteractionAllowed(out var allowed));
                    if (allowed == 0) return action();
                    Check(SecKeychainSetUserInteractionAllowed(false));
                    try { return action(); }
                    finally { Check(SecKeychainSetUserInteractionAllowed(true)); }
                }
            }, ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private Dictionary Query()
    {
        var query = new Dictionary();
        try
        {
            query.SetConstant("kSecClass", Symbol("kSecClassGenericPassword"));
            query.SetString("kSecAttrService", "app.tanayomi.twitch.v1");
            query.SetString("kSecAttrAccount", installation);
            // Local keychain only: synchronizable items are not requested.
            query.SetConstant("kSecUseAuthenticationUI", Symbol("kSecUseAuthenticationUIFail"));
            return query;
        }
        catch { query.Dispose(); throw; }
    }

    private static void Check(int status) { if (status != 0) throw Failed(status); }
    private static IOException Failed(int status = 0) => new($"macOSのキーチェーンを利用できません（OS状態{status}）。ロック状態とアクセス許可を確認してください。平文には保存しません。");
    private static nint Symbol(string name) => Marshal.ReadIntPtr(NativeLibrary.GetExport(SecurityHandle.Value, name));
    private static nint Boolean(bool value) => Marshal.ReadIntPtr(NativeLibrary.GetExport(CoreHandle.Value, value ? "kCFBooleanTrue" : "kCFBooleanFalse"));

    private sealed class Dictionary : IDisposable
    {
        public nint Handle { get; } = CFDictionaryCreateMutable(0, 0,
            NativeLibrary.GetExport(CoreHandle.Value, "kCFTypeDictionaryKeyCallBacks"),
            NativeLibrary.GetExport(CoreHandle.Value, "kCFTypeDictionaryValueCallBacks"));
        public Dictionary() { if (Handle == 0) throw Failed(); }
        public void SetConstant(string key, nint value) => CFDictionarySetValue(Handle, Symbol(key), value);
        public void SetString(string key, string value)
        {
            var bytes = Utf8.GetBytes(value);
            var text = CFStringCreateWithBytes(0, bytes, bytes.Length, 0x08000100, false);
            if (text == 0) throw Failed();
            try { SetConstant(key, text); }
            finally { CFRelease(text); }
        }
        public void SetData(string key, byte[] value)
        {
            var data = CFDataCreate(0, value, value.Length);
            if (data == 0) throw Failed();
            try { SetConstant(key, data); }
            finally { CFRelease(data); }
        }
        public void Dispose() => CFRelease(Handle);
    }

    [DllImport(Security)] private static extern int SecItemCopyMatching(nint query, out nint result);
    [DllImport(Security)] private static extern int SecItemAdd(nint attributes, nint result);
    [DllImport(Security)] private static extern int SecItemUpdate(nint query, nint attributes);
    [DllImport(Security)] private static extern int SecItemDelete(nint query);
    [DllImport(Security)] private static extern int SecKeychainGetUserInteractionAllowed(out byte allowed);
    [DllImport(Security)] private static extern int SecKeychainSetUserInteractionAllowed([MarshalAs(UnmanagedType.I1)] bool allowed);
    [DllImport(Core)] private static extern nint CFDictionaryCreateMutable(nint allocator, nint capacity, nint keyCallbacks, nint valueCallbacks);
    [DllImport(Core)] private static extern void CFDictionarySetValue(nint dictionary, nint key, nint value);
    [DllImport(Core)] private static extern nint CFStringCreateWithBytes(nint allocator, byte[] bytes, nint length, uint encoding, [MarshalAs(UnmanagedType.I1)] bool externalRepresentation);
    [DllImport(Core)] private static extern nint CFDataCreate(nint allocator, byte[] bytes, nint length);
    [DllImport(Core)] private static extern nint CFDataGetLength(nint data);
    [DllImport(Core)] private static extern nint CFDataGetBytePtr(nint data);
    [DllImport(Core)] private static extern nuint CFGetTypeID(nint value);
    [DllImport(Core)] private static extern nuint CFDataGetTypeID();
    [DllImport(Core)] private static extern void CFRelease(nint value);
}
