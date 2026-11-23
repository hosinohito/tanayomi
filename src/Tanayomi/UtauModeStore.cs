namespace Tanayomi;

public sealed class UtauModeStore(string directory)
{
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "settings", "utau-mode.txt");
    private readonly SemaphoreSlim gate = new(1, 1);
    private void Check()
    {
        for (var p = path; p is not null; p = Path.GetDirectoryName(p))
            if (Path.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクされた設定保存先は使えません。");
    }
    public async Task<UtauSynthesisMode> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            Check();
            if (!File.Exists(path)) return UtauSynthesisMode.Context;
            await using var file = File.OpenRead(path);
            if (file.Length is < 1 or > 32) throw new InvalidDataException("合成方式の設定が不正です。");
            var bytes = new byte[(int)file.Length];
            await file.ReadExactlyAsync(bytes, ct);
            if (file.ReadByte() != -1) throw new InvalidDataException("合成方式の設定が不正です。");
            var saved = System.Text.Encoding.UTF8.GetString(bytes);
            if (saved.Trim() is "ExternalResampler" or "ExternalChain" or "7" or "9") return UtauSynthesisMode.Context;
            if (!Enum.TryParse<UtauSynthesisMode>(saved, out var mode) || !Enum.IsDefined(mode))
                throw new InvalidDataException("合成方式の設定が不正です。");
            return mode;
        }
        finally { gate.Release(); }
    }
    public async Task SaveAsync(UtauSynthesisMode mode, CancellationToken ct = default)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        await gate.WaitAsync(ct);
        string? temporary = null;
        try
        {
            Check(); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(temporary, mode.ToString(), ct);
            ct.ThrowIfCancellationRequested(); Check();
            File.Move(temporary, path, true); temporary = null;
        }
        finally { try { if (temporary is not null) File.Delete(temporary); } finally { gate.Release(); } }
    }
}
