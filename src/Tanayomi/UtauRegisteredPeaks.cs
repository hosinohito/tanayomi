using System.Globalization;
using System.Text;

namespace Tanayomi;

internal static class UtauRegisteredPeaks
{
    private const string FileName = ".tanayomi-peaks-v1.tsv";
    private const int MaxWave = 32 * 1024 * 1024;
    private const int MaxMetadata = 2 * 1024 * 1024;
    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private readonly record struct Source(string Path, string Key, long Size, long Modified);

    internal static Voicebank Attach(Voicebank bank, CancellationToken ct, bool replace = false)
    {
        var path = Path.Combine(bank.Directory, FileName);
        var seen = new HashSet<string>(PathComparer);
        var sources = new List<Source>();
        foreach (var entry in bank.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(entry.WavePath)) continue;
            var info = new FileInfo(entry.WavePath);
            if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) continue;
            var relative = Path.GetRelativePath(bank.Directory, info.FullName);
            var key = Convert.ToBase64String(Encoding.UTF8.GetBytes(relative));
            sources.Add(new(entry.WavePath, key, info.Length, info.LastWriteTimeUtc.Ticks));
        }
        var peaks = replace ? null : Load(path, sources);
        if (peaks is null)
        {
            peaks = new float[sources.Count];
            var total = 0L;
            for (int i = 0; i < sources.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var s = sources[i];
                if (s.Size is < 44 or > MaxWave) continue;
                if (total + s.Size > 256 * 1024 * 1024) return bank; // Same total limit as ZIP registration.
                total += s.Size;
                try
                {
                    using var file = new FileStream(s.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (file.Length != s.Size) continue;
                    var bytes = new byte[(int)s.Size];
                    file.ReadExactly(bytes);
                    var pcm = UtauSpeech.DecodeWave(bytes, ct);
                    var peak = 0f;
                    foreach (var sample in pcm) peak = Math.Max(peak, Math.Abs(sample));
                    if (peak >= .01f) peaks[i] = peak;
                }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
            }
            ct.ThrowIfCancellationRequested();
            try { Save(path, sources, peaks, ct); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { /* Read-only banks still work. */ }
        }
        var usable = peaks.Where(p => p > 0).ToArray();
        if (usable.Length == 0) return bank;
        var reference = Math.Min(.72f, usable.Average());
        var gain = new Dictionary<string, float>(PathComparer);
        for (int i = 0; i < peaks.Length; i++)
            if (peaks[i] > 0) gain[sources[i].Path] = Math.Min(4f, reference / peaks[i]);
        return bank with { RegisteredPeakGains = gain };
    }

    private static string Identity(Source s) => FormattableString.Invariant($"{s.Key}\t{s.Size}\t{s.Modified}\t");

    private static float[]? Load(string path, List<Source> sources)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > MaxMetadata || (file.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            if (stream.Length is < 1 or > MaxMetadata) return null;
            var bytes = new byte[(int)stream.Length];
            stream.ReadExactly(bytes);
            var lines = Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (lines.Length != sources.Count) return null;
            var values = new float[lines.Length];
            for (int i = 0; i < lines.Length; i++)
            {
                var id = Identity(sources[i]);
                if (!lines[i].StartsWith(id, StringComparison.Ordinal) ||
                    !float.TryParse(lines[i].AsSpan(id.Length), NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]) ||
                    !float.IsFinite(values[i]) || values[i] < 0 || values[i] > 2 || values[i] is > 0 and < .01f) return null;
            }
            return values;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void Save(string path, List<Source> sources, float[] peaks, CancellationToken ct)
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                for (int i = 0; i < peaks.Length; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    writer.Write(Identity(sources[i]));
                    writer.Write(peaks[i].ToString("R", CultureInfo.InvariantCulture));
                    writer.Write('\n');
                }
            File.Move(temp, path, true);
        }
        finally { File.Delete(temp); }
    }
}
