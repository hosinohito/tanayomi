using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Windows uses an executable-local DPAPI file; macOS/Linux use the OS credential service.</summary>
public sealed class DesktopCredentialStore : ICredentialStore
{
    private const int MaximumBytes = 16384;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly byte[] Entropy = "Tanayomi/Twitch/v1"u8.ToArray();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly string path;
    private readonly ICredentialStore? systemStore;
    public DesktopCredentialStore(string? path = null)
    {
        this.path = Path.GetFullPath(path ?? Path.Combine(AppPaths.DataDirectory, "account", "twitch.dat"));
        // Separate installations must not erase or rotate one another's credentials.
        var installation = Convert.ToHexString(SHA256.HashData(Utf8.GetBytes(this.path)));
        if (OperatingSystem.IsMacOS()) systemStore = new MacCredentialStore(installation);
        else if (OperatingSystem.IsLinux()) systemStore = new LinuxCredentialStore(installation);
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        if (systemStore is not null) return await systemStore.ReadAsync(ct).ConfigureAwait(false);
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckPlatform(); ct.ThrowIfCancellationRequested();
            byte[] encrypted;
            try
            {
                RejectLinkedFile();
                await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
                if (input.Length is < 1 or > 65536) throw new StoreFailure("保存された認証ファイルのサイズが不正です。ログインし直してください。");
                encrypted = new byte[(int)input.Length];
                await input.ReadExactlyAsync(encrypted, ct).ConfigureAwait(false);
            }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            byte[]? clear = null;
            try
            {
                clear = Transform(encrypted, false);
                if (clear.Length > MaximumBytes) throw new StoreFailure("保存された認証情報が大きすぎます。");
                return Utf8.GetString(clear);
            }
            finally { if (clear is not null) CryptographicOperations.ZeroMemory(clear); CryptographicOperations.ZeroMemory(encrypted); }
        }
        catch (StoreFailure) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or DecoderFallbackException)
        { throw new IOException("実行ファイル横の認証ファイルを読み込めません。保存先のアクセス権とWindowsのログイン状態を確認してください。"); }
        finally { serial.Release(); }
    }

    public async Task WriteAsync(string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (Utf8.GetByteCount(value) > MaximumBytes) throw new IOException("保存する認証情報が大きすぎます。");
        if (systemStore is not null) { await systemStore.WriteAsync(value, ct).ConfigureAwait(false); return; }
        await serial.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        byte[]? clear = null, encrypted = null;
        try
        {
            CheckPlatform(); ct.ThrowIfCancellationRequested();
            clear = Utf8.GetBytes(value);
            encrypted = Transform(clear, true);
            var directory = Path.GetDirectoryName(path)!;
            RejectLinkedFile();
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, ".twitch-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(encrypted, ct).ConfigureAwait(false);
                await output.FlushAsync(ct).ConfigureAwait(false);
                output.Flush(flushToDisk: true);
            }
            if (OperatingSystem.IsWindows()) RestrictFile(temporary);
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, true);
            temporary = null;
        }
        catch (StoreFailure) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or EncoderFallbackException)
        { throw new IOException("実行ファイル横へ認証情報を保存できません。保存先の書き込み権限を確認してください。今回の認証は引き続き利用できます。"); }
        finally
        {
            try
            {
                if (temporary is not null)
                    try { File.Delete(temporary); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { throw new IOException("認証情報の暗号化一時ファイルを削除できません。data/accountフォルダーの書き込み権限を確認してください。"); }
            }
            finally
            {
                if (clear is not null) CryptographicOperations.ZeroMemory(clear);
                if (encrypted is not null) CryptographicOperations.ZeroMemory(encrypted);
                serial.Release();
            }
        }
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        if (systemStore is not null) { await systemStore.DeleteAsync(ct).ConfigureAwait(false); return; }
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try { CheckPlatform(); ct.ThrowIfCancellationRequested(); RejectLinkedFile(); File.Delete(path); }
        catch (DirectoryNotFoundException) { }
        catch (StoreFailure) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new IOException("実行ファイル横の認証ファイルを削除できません。保存先の書き込み権限を確認してください。"); }
        finally { serial.Release(); }
    }

    private void RejectLinkedFile()
    {
        for (var current = Path.GetDirectoryName(path); current is not null; current = Path.GetDirectoryName(current))
            if (Directory.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new StoreFailure("リンク先への認証情報の保存は行いません。実行ファイル横のdataフォルダーを確認してください。");
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new StoreFailure("リンクされた認証ファイルは利用できません。");
    }
    private static void CheckPlatform()
    {
        if (!OperatingSystem.IsWindows()) throw new StoreFailure("このOSでは実行ファイル横への暗号化保存は未対応です。今回の認証は利用できますが、次回はログインが必要です。");
    }
    [SupportedOSPlatform("windows")]
    private static void RestrictFile(string file)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new StoreFailure("Windows利用者を確認できないため認証情報を保存できません。");
        var security = new FileSecurity();
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }
    private static byte[] Transform(byte[] value, bool protect)
    {
        var inputHandle = GCHandle.Alloc(value, GCHandleType.Pinned);
        var entropyHandle = GCHandle.Alloc(Entropy, GCHandleType.Pinned);
        var result = new DataBlob();
        try
        {
            var input = new DataBlob { Length = value.Length, Data = inputHandle.AddrOfPinnedObject() };
            var entropy = new DataBlob { Length = Entropy.Length, Data = entropyHandle.AddrOfPinnedObject() };
            var ok = protect ? CryptProtectData(ref input, null, ref entropy, 0, 0, 1, out result)
                : CryptUnprotectData(ref input, 0, ref entropy, 0, 0, 1, out result);
            if (!ok) throw new StoreFailure($"Windows利用者専用の暗号化処理に失敗しました（OSエラー{Marshal.GetLastPInvokeError()}）。同じWindows利用者でログインし直してください。");
            if (result.Length is < 0 or > 65536) throw new StoreFailure("暗号化した認証情報のサイズが不正です。");
            var bytes = new byte[result.Length];
            Marshal.Copy(result.Data, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            if (result.Data != 0)
            {
                if (!protect) for (var i = 0; i < result.Length; i++) Marshal.WriteByte(result.Data, i, 0);
                LocalFree(result.Data);
            }
            inputHandle.Free(); entropyHandle.Free();
        }
    }
    private sealed class StoreFailure(string message) : IOException(message);
    [StructLayout(LayoutKind.Sequential)] private struct DataBlob { public int Length; public nint Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, nint description, ref DataBlob entropy, nint reserved, nint prompt, uint flags, out DataBlob output);
    [DllImport("kernel32.dll")] private static extern nint LocalFree(nint memory);
}
