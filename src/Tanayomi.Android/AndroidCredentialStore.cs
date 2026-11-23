using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Security.Cryptography;
using System.Text;

namespace Tanayomi.Android;

/// <summary>Only authenticated ciphertext enters app-private, non-backup storage.
/// The AES key stays in AndroidKeyStore; unavailable protection has no fallback.</summary>
public sealed class AndroidCredentialStore : ICredentialStore
{
    private const int MaximumBytes = 16384;
    private const string Alias = "Tanayomi.Twitch.Credential.v1";
    private static readonly byte[] AssociatedData = "Tanayomi/Twitch/Android/v1"u8.ToArray();
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly SemaphoreSlim Serial = new(1, 1);
    private readonly string root;
    private readonly string path;

    public AndroidCredentialStore(Context context)
    {
        // Canonicalize the OS-provided root, which may have system-managed path
        // aliases. Only app-controlled descendants are rejected as links below.
        using var directory = context.NoBackupFilesDir ?? throw Failure("初期化できませんでした");
        var canonical = directory.CanonicalPath;
        if (string.IsNullOrEmpty(canonical) || !Path.IsPathFullyQualified(canonical)) throw Failure("初期化できませんでした");
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(canonical));
        path = Path.Combine(root, "Tanayomi", "account", "twitch.dat");
    }

    public Task<string?> ReadAsync(CancellationToken ct) => RunAsync<string?>(() =>
    {
        RejectLinks();
        byte[] packet;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 33 or > MaximumBytes + 32) throw Failure("読み出せませんでした");
            packet = new byte[(int)file.Length];
            file.ReadExactly(packet);
            if (file.ReadByte() != -1) throw Failure("読み出せませんでした");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        byte[]? clear = null;
        try
        {
            if (!packet.AsSpan(0, 4).SequenceEqual("TYA1"u8)) throw Failure("読み出せませんでした");
            ct.ThrowIfCancellationRequested();
            using var store = OpenStore();
            // Reading never creates/replaces a missing key or a corrupt record.
            using var key = store.GetKey(Alias, null) ?? throw Failure("読み出せませんでした");
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw Failure("読み出せませんでした");
            using var parameters = new GCMParameterSpec(128, packet, 4, 12);
            cipher.Init(global::Javax.Crypto.CipherMode.DecryptMode, key, parameters);
            cipher.UpdateAAD(AssociatedData);
            clear = cipher.DoFinal(packet, 16, packet.Length - 16) ?? throw Failure("読み出せませんでした");
            if (clear.Length is < 1 or > MaximumBytes) throw Failure("読み出せませんでした");
            ct.ThrowIfCancellationRequested();
            return Utf8.GetString(clear);
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            CryptographicOperations.ZeroMemory(packet);
        }
    }, ct, "読み出せませんでした");

    public Task WriteAsync(string value, CancellationToken ct) => RunAsync(() =>
    {
        if (string.IsNullOrEmpty(value) || Utf8.GetByteCount(value) > MaximumBytes) throw Failure("保存できませんでした");
        byte[]? clear = null;
        string? temporary = null;
        try
        {
            clear = Utf8.GetBytes(value);
            RejectLinks();
            using var store = OpenStore();
            using var key = GetOrCreateKey(store);
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding") ?? throw Failure("保存できませんでした");
            // Let AndroidKeyStore generate a fresh randomized nonce on every write.
            cipher.Init(global::Javax.Crypto.CipherMode.EncryptMode, key);
            cipher.UpdateAAD(AssociatedData);
            var encrypted = cipher.DoFinal(clear) ?? throw Failure("保存できませんでした");
            var iv = cipher.GetIV();
            if (iv is not { Length: 12 } || encrypted.Length != clear.Length + 16) throw Failure("保存できませんでした");
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            RejectLinks();
            temporary = Path.Combine(directory, ".twitch-" + Guid.NewGuid().ToString("N") + ".tmp");
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                file.Write("TYA1"u8); file.Write(iv); file.Write(encrypted);
                file.Flush(flushToDisk: true);
            }
            ct.ThrowIfCancellationRequested();
            RejectLinks();
            File.Move(temporary, path, overwrite: true);
            temporary = null;
            return true;
        }
        finally
        {
            if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (temporary is not null) File.Delete(temporary);
        }
    }, ct, "保存できませんでした");

    public Task DeleteAsync(CancellationToken ct) => RunAsync(() =>
    {
        var failed = false;
        // Attempt both removals even if one fails. A stranded ciphertext must not
        // retain a usable key merely because its file could not be removed.
        try { RejectLinks(); File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        catch (Exception) { failed = true; }
        try
        {
            using var store = OpenStore();
            if (store.ContainsAlias(Alias)) store.DeleteEntry(Alias);
            if (store.ContainsAlias(Alias)) failed = true;
        }
        catch (Exception) { failed = true; }
        if (failed) throw Failure("削除できませんでした");
        return true;
    }, ct, "削除できませんでした");

    private static KeyStore OpenStore()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore") ?? throw Failure("利用できませんでした");
        try { store.Load(null); return store; }
        catch { store.Dispose(); throw; }
    }

    private static IKey GetOrCreateKey(KeyStore store)
    {
        if (store.GetKey(Alias, null) is { } existing) return existing;
        using var generator = KeyGenerator.GetInstance("AES", "AndroidKeyStore") ?? throw Failure("利用できませんでした");
        using var builder = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt);
        builder.SetBlockModes(KeyProperties.BlockModeGcm);
        builder.SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone);
        builder.SetKeySize(256);
        builder.SetRandomizedEncryptionRequired(true);
        // Background token rotation must not trigger an authentication prompt.
        builder.SetUserAuthenticationRequired(false);
        using var specification = builder.Build() ?? throw Failure("利用できませんでした");
        generator.Init(specification);
        return generator.GenerateKey() ?? throw Failure("利用できませんでした");
    }

    private void RejectLinks()
    {
        for (var current = path; current is not null &&
            (current == root || current.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
            current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw Failure("利用できませんでした");
    }

    private static async Task<T> RunAsync<T>(Func<T> operation, CancellationToken ct, string action)
    {
        await Serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => { ct.ThrowIfCancellationRequested(); return operation(); }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failure(action); }
        finally { Serial.Release(); }
    }

    private static IOException Failure(string action) => new($"Androidの認証情報を安全に{action}。平文保存は行いません。再認証または保存認証の削除を試してください。");
}
