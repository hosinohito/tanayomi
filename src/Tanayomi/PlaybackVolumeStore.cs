using System.Globalization;
using System.Text;

namespace Tanayomi;

// Standalone storage only. UI adoption and when a changed value affects an
// active utterance are deliberately left to the caller.
public sealed class PlaybackVolumeStore
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string path;
    private readonly SemaphoreSlim gate = new(1, 1);

    public PlaybackVolumeStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        path = Path.Combine(Path.GetFullPath(dataDirectory), "settings", "playback-volume.txt");
    }

    public async Task<int> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckPath(path);
            if (!File.Exists(path))
            {
                if (Directory.Exists(path)) throw InvalidSetting();
                return 100;
            }
            await using var file = File.OpenRead(path);
            if (file.Length is < 1 or > 3) throw InvalidSetting();
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (file.ReadByte() != -1 || bytes.Any(x => x is < (byte)'0' or > (byte)'9') ||
                !int.TryParse(Utf8.GetString(bytes), NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
                value is < 0 or > 400) throw InvalidSetting();
            return value;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(int percent, CancellationToken ct = default)
    {
        if (percent is < 0 or > 400) throw new ArgumentOutOfRangeException(nameof(percent));
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
                await file.WriteAsync(Utf8.GetBytes(percent.ToString(CultureInfo.InvariantCulture)), ct).ConfigureAwait(false);
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

    private static InvalidDataException InvalidSetting() => new("読み上げ音量の設定が不正です。");

    private static void CheckPath(string path)
    {
        for (string? current = path; current is not null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("リンクされた音量設定保存先は使えません。");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}

