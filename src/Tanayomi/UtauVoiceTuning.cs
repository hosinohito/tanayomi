using System.Globalization;

namespace Tanayomi;

/// <summary>Relative to the selected voicebank; not an absolute MIDI range.</summary>
public sealed record UtauVoiceTuning
{
    public const int MinimumPitch = -6, MaximumPitch = 6, MinimumRange = 0, MaximumRange = 200;
    public const int MinimumFormant = -100, MaximumFormant = 100, MinimumBreath = 0, MaximumBreath = 100;
    public static UtauVoiceTuning Default { get; } = new(0, 100);
    public UtauVoiceTuning(int pitch, int range, int formant = 0, int breath = 0)
    {
        if (pitch is < MinimumPitch or > MaximumPitch) throw new ArgumentOutOfRangeException(nameof(pitch));
        if (range is < MinimumRange or > MaximumRange) throw new ArgumentOutOfRangeException(nameof(range));
        if (formant is < MinimumFormant or > MaximumFormant) throw new ArgumentOutOfRangeException(nameof(formant));
        if (breath is < MinimumBreath or > MaximumBreath) throw new ArgumentOutOfRangeException(nameof(breath));
        Formant = formant;
        Breath = breath;
        Pitch = pitch;
        Range = range;
    }
    public int Pitch { get; }
    public int Range { get; }
    public int Formant { get; }
    public int Breath { get; }
    internal bool HasTimbre => Formant != 0 || Breath != 0;
}

internal sealed class UtauVoiceTuningStore(string directory)
{
    private readonly string path = Path.Combine(Path.GetFullPath(directory), "settings", "utau-voice-tuning.txt");
    private void Check()
    {
        for (var p = path; p is not null; p = Path.GetDirectoryName(p))
            if (Path.Exists(p) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクされた設定保存先は使えません。");
    }
    internal async Task<UtauVoiceTuning> LoadAsync(CancellationToken ct)
    {
        Check();
        if (!File.Exists(path)) return UtauVoiceTuning.Default;
        await using var file = File.OpenRead(path);
        if (file.Length is < 1 or > 32) throw new InvalidDataException("声の調整設定が不正です。");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, ct);
        if (file.ReadByte() != -1) throw new InvalidDataException("声の調整設定が不正です。");
        var parts = System.Text.Encoding.UTF8.GetString(bytes).Split(' ');
        var formant = 0;
        var breath = 0;
        if (parts.Length is not (2 or 4) ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pitch) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var range) ||
            pitch is < UtauVoiceTuning.MinimumPitch or > UtauVoiceTuning.MaximumPitch ||
            range is < UtauVoiceTuning.MinimumRange or > UtauVoiceTuning.MaximumRange ||
            parts.Length == 4 &&
            (!int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out formant) ||
             !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out breath) ||
             formant is < UtauVoiceTuning.MinimumFormant or > UtauVoiceTuning.MaximumFormant ||
             breath is < UtauVoiceTuning.MinimumBreath or > UtauVoiceTuning.MaximumBreath))
            throw new InvalidDataException("声の調整設定が不正です。");
        return new(pitch, range, formant, breath);
    }
    internal async Task SaveAsync(UtauVoiceTuning value, CancellationToken ct)
    {
        Check();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, FormattableString.Invariant($"{value.Pitch} {value.Range} {value.Formant} {value.Breath}"), ct);
            ct.ThrowIfCancellationRequested();
            Check();
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
