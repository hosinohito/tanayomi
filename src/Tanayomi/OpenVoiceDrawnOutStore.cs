using System.Text;

namespace Tanayomi;

// Standalone storage only. UI adoption and when a changed value affects an
// active utterance are deliberately left to the caller.
public sealed class OpenVoiceDrawnOutStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public OpenVoiceDrawnOutStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        path = Path.Combine(Path.GetFullPath(dataDirectory), "settings", "openvoice-drawn-out.txt");
    }

    public async Task<bool> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckPath(path);
            if (!File.Exists(path))
            {
                if (Directory.Exists(path)) throw InvalidSetting();
                return false;
            }
            await using var file = File.OpenRead(path);
            if (file.Length != 1) throw InvalidSetting();
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (file.ReadByte() != -1 || (bytes[0] != (byte)'0' && bytes[0] != (byte)'1')) throw InvalidSetting();
            var value = bytes[0] == (byte)'1';
            return value;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(bool enabled, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        var temporaryCreated = false;
        try
        {
            CheckPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CheckPath(path);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 4096, FileOptions.Asynchronous))
            {
                temporaryCreated = true;
                await file.WriteAsync(Utf8.GetBytes(enabled ? "1" : "0"), ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            CheckPath(path);
            File.Move(temporary, path, true);
            temporary = null;
        }
        finally
        {
            try
            {
                if (temporaryCreated && temporary is not null)
                {
                    CheckPath(temporary);
                    File.Delete(temporary);
                }
            }
            finally { gate.Release(); }
        }
    }

    private static InvalidDataException InvalidSetting() => new("間延びモードの設定が不正です。");

    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクされた間延びモード設定保存先は使えません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

