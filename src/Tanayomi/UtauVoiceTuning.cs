using System.Globalization;

namespace Tanayomi;

/// <summary>Five phrase-relative offsets, stored in half-semitone steps.</summary>
public readonly record struct UtauPitchCurve(int Start, int Early, int Middle, int Late, int End)
{
    public const int Minimum = -6, Maximum = 6;
    public static UtauPitchCurve Default { get; } = new(1, 1, 0, -1, -2);

    internal UtauPitchCurve FromLegacyOffsets(int range)
    {
        int Merge(int offset, int point) => Math.Clamp(offset + (int)Math.Round(point * range / 100.0), Minimum, Maximum);
        return new(Merge(Start, Default.Start), Merge(Early, Default.Early), Merge(Middle, Default.Middle),
            Merge(Late, Default.Late), Merge(End, Default.End));
    }
    internal bool IsValid => IsPoint(Start) && IsPoint(Early) && IsPoint(Middle) && IsPoint(Late) && IsPoint(End);
    private static bool IsPoint(int value) => value is >= Minimum and <= Maximum;

    internal double SemitonesAt(double t)
    {
        var scaled = Math.Clamp(t, 0, 1) * 4;
        var segment = Math.Min((int)scaled, 3);
        var (a, b) = segment switch
        {
            0 => (Start, Early), 1 => (Early, Middle), 2 => (Middle, Late), _ => (Late, End)
        };
        var x = scaled - segment;
        return (a + (b - a) * (x * x * (3 - 2 * x))) * 0.5;
    }
}

/// <summary>Relative to the selected voicebank; not an absolute MIDI range.</summary>
public sealed record UtauVoiceTuning
{
    public const int MinimumPitch = -6, MaximumPitch = 6, MinimumRange = 0, MaximumRange = 200;
    public const int MinimumFormant = -100, MaximumFormant = 100, MinimumBreath = 0, MaximumBreath = 100;
    public static UtauVoiceTuning Default { get; } = new(0, 100);
    public UtauVoiceTuning(int pitch, int range, int formant = 0, int breath = 0, UtauPitchCurve? curve = null, int? boundaryRange = null, bool curveEnabled = true, bool levelJoins = false, int maximumHeadCut = 15)
    {
        if (pitch is < MinimumPitch or > MaximumPitch) throw new ArgumentOutOfRangeException(nameof(pitch));
        if (range is < MinimumRange or > MaximumRange) throw new ArgumentOutOfRangeException(nameof(range));
        if (formant is < MinimumFormant or > MaximumFormant) throw new ArgumentOutOfRangeException(nameof(formant));
        if (breath is < MinimumBreath or > MaximumBreath) throw new ArgumentOutOfRangeException(nameof(breath));
        Curve = curve ?? UtauPitchCurve.Default;
        CurveEnabled = curveEnabled;
        if (maximumHeadCut is < 0 or > 50) throw new ArgumentOutOfRangeException(nameof(maximumHeadCut));
        LevelJoins = levelJoins;
        MaximumHeadCut = maximumHeadCut;
        if (!Curve.IsValid) throw new ArgumentOutOfRangeException(nameof(curve));
        Formant = formant;
        Breath = breath;
        Pitch = pitch;
        Range = range;
        BoundaryRange = boundaryRange ?? range;
        if (BoundaryRange is < MinimumRange or > MaximumRange) throw new ArgumentOutOfRangeException(nameof(boundaryRange));
    }
    public UtauPitchCurve Curve { get; }
    public bool CurveEnabled { get; }
    public bool LevelJoins { get; init; }
    public int MaximumHeadCut { get; }
    public int Pitch { get; }
    public int Range { get; }
    public int BoundaryRange { get; }
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
        if (file.Length is < 1 or > 64) throw new InvalidDataException("声の調整設定が不正です。");
        var bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, ct);
        if (file.ReadByte() != -1) throw new InvalidDataException("声の調整設定が不正です。");
        var parts = System.Text.Encoding.UTF8.GetString(bytes).Split(' ');
        var formant = 0;
        var breath = 0;
        // Read the old trial format too, but discard its retired mode and save only supported settings.
        if ((parts.Length is not (2 or 4 or 9 or 10 or 11 or 12 or 14 or 15) ||
                parts.Length == 10 && parts[9] != "v2" || parts.Length == 11 && parts[10] != "v3" ||
                parts.Length == 12 && (parts[11] != "v4" || parts[10] is not ("0" or "1")) ||
                parts.Length == 14 && (parts[13] != "v5" || parts[10] is not ("0" or "1") || parts[11] is not ("0" or "1")) ||
                parts.Length == 15 && (parts[14] != "v6" || parts[10] is not ("0" or "1") || parts[11] is not ("0" or "1") || parts[13] is not ("0" or "1" or "2" or "3"))) ||
            !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var pitch) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var range) ||
            pitch is < UtauVoiceTuning.MinimumPitch or > UtauVoiceTuning.MaximumPitch ||
            range is < UtauVoiceTuning.MinimumRange or > UtauVoiceTuning.MaximumRange ||
            parts.Length >= 4 &&
            (!int.TryParse(parts[2], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out formant) ||
             !int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out breath) ||
             formant is < UtauVoiceTuning.MinimumFormant or > UtauVoiceTuning.MaximumFormant ||
             breath is < UtauVoiceTuning.MinimumBreath or > UtauVoiceTuning.MaximumBreath))
            throw new InvalidDataException("声の調整設定が不正です。");
        var curve = UtauPitchCurve.Default;
        if (parts.Length >= 9)
        {
            Span<int> points = stackalloc int[5];
            for (var i = 0; i < points.Length; i++)
                if (!int.TryParse(parts[i + 4], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out points[i]) ||
                    points[i] is < UtauPitchCurve.Minimum or > UtauPitchCurve.Maximum)
                    throw new InvalidDataException("抑揚カーブの設定が不正です。");
            curve = new(points[0], points[1], points[2], points[3], points[4]);
            if (parts.Length == 9) curve = curve.FromLegacyOffsets(range);
        }
        var boundaryRange = range;
        if (parts.Length >= 11 && (!int.TryParse(parts[9], NumberStyles.None, CultureInfo.InvariantCulture, out boundaryRange) ||
            boundaryRange is < UtauVoiceTuning.MinimumRange or > UtauVoiceTuning.MaximumRange))
            throw new InvalidDataException("疑問文の語尾の設定が不正です。");
        var head = 15;
        if (parts.Length >= 14 && (!int.TryParse(parts[12], NumberStyles.None, CultureInfo.InvariantCulture, out head) || head is < 0 or > 50))
            throw new InvalidDataException("接合時の頭の減衰設定が不正です。");
        return new UtauVoiceTuning(pitch, range, formant, breath, curve, boundaryRange, parts.Length < 12 || parts[10] == "1",
            parts.Length >= 14 && parts[11] == "1", head);
    }
    internal async Task SaveAsync(UtauVoiceTuning value, CancellationToken ct)
    {
        Check();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var text = FormattableString.Invariant($"{value.Pitch} {value.Range} {value.Formant} {value.Breath}");
            var curve = value.Curve;
            text += FormattableString.Invariant($" {curve.Start} {curve.Early} {curve.Middle} {curve.Late} {curve.End} {value.BoundaryRange} {(value.CurveEnabled ? 1 : 0)} {(value.LevelJoins ? 1 : 0)} {value.MaximumHeadCut} v5");
            await File.WriteAllTextAsync(temporary, text, ct);
            ct.ThrowIfCancellationRequested();
            Check();
            File.Move(temporary, path, true);
        }
        finally { File.Delete(temporary); }
    }
}
