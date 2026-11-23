using System.Globalization;
using System.Text.Json;

namespace Tanayomi.Desktop;

public sealed record WorldUtauComparisonResult(int SampleCount, int FrameCount, int FftSize,
    int VoicedFrames, double FlatF0, double OriginalPeak, double FlatPeak, double Gain,
    int SourceCount, int AnalysisFrames, long MaximumParameterBytes);

/// <summary>Fixed-text/bundled-bank experiment. Original recordings are analyzed
/// before any time/pitch processing, then each complete utterance is synthesized
/// once. This is deliberately separate from the production UTAU renderer.</summary>
internal static class WorldUtauComparison
{
    internal const string ReviewText = "すーっと、しーっと、こんにちは。今日は配信を見に来てくれてありがとう。";
    private const int Rate = 44100;
    private const double Hop = Rate * 0.005;
    private const int Fft = 2048;
    private const int Width = Fft / 2 + 1;
    private const int MaximumSamples = Rate * 45;
    // Bounds live managed parameter buffers, not total OS process working set.
    // Two utterance matrices plus two reusable source matrices fit in 256 MiB.
    private const long MaximumParameterBytes = 256L * 1024 * 1024;
    private const double SpectralFloor = 1e-12;

    private sealed record Unit(OtoEntry? Entry, int Extensions, char Pause);
    private sealed record Timing(int Start, int Pre, int Fixed, int End, int Lead, int Transition, int Overlap);
    private sealed record Source(string Path, int Samples, int Frames, DateTime Modified, long Length);
    private sealed record Note(OtoEntry Entry, Timing Timing, int Start, int Duration, int Index)
    {
        internal int Length => checked(Timing.Lead + Duration);
        internal int End => checked(Start + Length);
        internal int FadeStart { get; set; } = int.MaxValue;
        internal int FadeLength { get; set; }
    }
    private sealed record Plan(Note[] Notes, (int Start, int End)[] Pauses, int Samples);

    internal static int RunWorker(string[] args)
    {
        var owned = new List<string>();
        var success = false;
        try
        {
            WorldComparison.ConfigureWorker();
            if (args.Length != 5 || args[0] != "--world-utau-worker" ||
                !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var target)) throw new ArgumentException();
            WorldComparison.ValidateFlatF0(target);
            var job = ValidateJob(args[1]);
            var paths = ValidateOutputs(job, args[2], args[3]);
            var library = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
            WorldComparison.RejectLinks(library);
            using var native = new WorldComparison.NativeApi(library);
            // A unique non-default bank root cannot trigger legacy/user-data migration.
            var banks = Voicebanks.LoadInstalledAsync(Path.Combine(job, "banks")).GetAwaiter().GetResult();
            var bank = banks.Single(b => b.Name == "hosinohito");
            var result = Render(bank, target, native, paths, owned);
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.Out.Flush();
            success = true;
            return 0;
        }
        catch (Exception error) { WorldComparison.ReportFailure(error); return 1; }
        finally
        {
            // The parent owns/removes the entire unique job, including copied
            // source recordings, even after a native fault or timeout kill.
            if (!success)
                foreach (var path in owned)
                    try { WorldComparison.RejectLinks(path); File.Delete(path); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private static WorldUtauComparisonResult Render(Voicebank bank, double requestedF0,
        WorldComparison.NativeApi native, string[] paths, List<string> owned)
    {
        var aliases = bank.Entries.GroupBy(e => Hiragana(e.Alias.Normalize()), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var units = Split(JapaneseReading.ToProsodicReading(ReviewText), aliases);
        var references = requestedF0 == 0 ? new[] { "あ", "い", "う", "え", "お" }
            .Where(aliases.ContainsKey).Select(a => aliases[a]).ToArray() : [];
        var used = units.Where(u => u.Entry is not null).Select(u => u.Entry!).Concat(references).Distinct().ToArray();
        var timings = new Dictionary<OtoEntry, Timing>();
        var sources = new List<Source>();
        foreach (var group in used.GroupBy(e => e.WavePath, StringComparer.OrdinalIgnoreCase))
        {
            if (sources.Count >= 64) throw new InvalidDataException("比較の原音数が多すぎます。");
            var path = Path.GetFullPath(group.Key);
            if (!path.StartsWith(Path.GetFullPath(bank.Directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            WorldComparison.RejectLinks(path);
            var samples = WorldComparison.ReadWave(path);
            WorldComparison.Check(native.Layout(samples.Length, out var frames, out var fft));
            if (fft != Fft || frames is < 2 or > 6001) throw new InvalidDataException();
            foreach (var entry in group) timings.Add(entry, Crop(samples, entry));
            var info = new FileInfo(path);
            sources.Add(new(path, samples.Length, frames, info.LastWriteTimeUtc, info.Length));
        }
        if (sources.Count == 0) throw new InvalidDataException();
        var plan = Place(units, timings);
        var framesOut = (int)(plan.Samples / Hop) + 1;
        if (framesOut is < 2 or > 9001) throw new InvalidDataException();
        var cellsOut = checked(framesOut * Width);
        var maximumFramesIn = sources.Max(s => s.Frames);
        var cellsIn = checked(maximumFramesIn * Width);
        var parameterBytes = checked(sizeof(double) * (2L * cellsOut + 2L * cellsIn + 2L * framesOut + 2L * maximumFramesIn));
        if (parameterBytes > MaximumParameterBytes) throw new InvalidDataException("比較パラメーターのメモリー上限を超えています。");

        var spectrum = new double[cellsOut];
        var noisePower = new double[cellsOut]; // Reused as final amplitude AP.
        var f0 = new double[framesOut];
        var owner = Enumerable.Repeat(-1, framesOut).ToArray();
        var sourceF0 = new double[maximumFramesIn];
        var times = new double[maximumFramesIn];
        var sourceSpectrum = new double[cellsIn];
        var sourceAp = new double[cellsIn];
        var referenceF0 = new List<double>();
        var analysisFrames = 0;
        foreach (var source in sources)
        {
            var info = new FileInfo(source.Path);
            WorldComparison.RejectLinks(source.Path);
            if (info.Length != source.Length || info.LastWriteTimeUtc != source.Modified) throw new InvalidDataException("比較中に原音が変更されました。");
            var samples = WorldComparison.ReadWave(source.Path);
            if (samples.Length != source.Samples) throw new InvalidDataException();
            var cells = checked(source.Frames * Width);
            WorldComparison.Check(native.Analyze(samples, samples.Length, sourceF0, source.Frames,
                times, source.Frames, sourceSpectrum, cells, sourceAp, cells, Fft));
            analysisFrames = checked(analysisFrames + source.Frames);
            foreach (var entry in references.Where(e => e.WavePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
            {
                var timing = timings[entry];
                var first = (int)Math.Ceiling((timing.Start + timing.Fixed) / Hop);
                var last = Math.Min(source.Frames - 1, (int)((timing.End - 1) / Hop));
                for (var frame = first; frame <= last; ++frame) if (sourceF0[frame] > 0) referenceF0.Add(sourceF0[frame]);
            }
            foreach (var note in plan.Notes.Where(n => n.Entry.WavePath.Equals(source.Path, StringComparison.OrdinalIgnoreCase)))
                Transfer(note, source.Frames, sourceF0, sourceSpectrum, sourceAp, spectrum, noisePower, f0, owner);
        }
        // Zero power would produce log(0) in WORLD's unvoiced synthesis. This
        // numerical floor is far below PCM16; explicit pauses are muted below.
        for (var i = 0; i < cellsOut; ++i)
        {
            var power = spectrum[i];
            if (!double.IsFinite(power) || !double.IsFinite(noisePower[i]) || power < 0 || noisePower[i] < 0) throw new InvalidDataException();
            noisePower[i] = power > SpectralFloor ? Math.Sqrt(Math.Clamp(noisePower[i] / power, 0, 1)) : 1;
            spectrum[i] = Math.Max(SpectralFloor, power);
        }
        var voiced = f0.Count(value => value > 0);
        if (voiced < 10) throw new InvalidDataException("比較の有声フレームが不足しています。");
        var target = requestedF0;
        if (target == 0)
        {
            if (referenceF0.Count < 10) throw new InvalidDataException("音源基準F0の有声フレームが不足しています。");
            referenceF0.Sort();
            target = (referenceF0[(referenceF0.Count - 1) / 2] + referenceF0[referenceF0.Count / 2]) / 2;
        }
        var flatF0 = f0.Select(value => value > 0 ? target : 0).ToArray();
        var original = new double[plan.Samples];
        var flat = new double[plan.Samples];
        // Each result is synthesized once from the complete final time axis.
        WorldComparison.Check(native.Synthesize(f0, framesOut, spectrum, cellsOut, noisePower, cellsOut, Fft, original, original.Length));
        WorldComparison.Check(native.Synthesize(flatF0, framesOut, spectrum, cellsOut, noisePower, cellsOut, Fft, flat, flat.Length));
        Envelope(original, plan.Pauses);
        Envelope(flat, plan.Pauses);
        var originalPeak = WorldComparison.Peak(original);
        var flatPeak = WorldComparison.Peak(flat);
        var peak = Math.Max(originalPeak, flatPeak);
        if (peak < 1e-10) throw new InvalidDataException();
        var gain = Math.Min(1.0, 0.92 / peak);
        WorldComparison.WriteWave(paths[0], original, gain, owned);
        WorldComparison.WriteWave(paths[1], flat, gain, owned);
        return new(plan.Samples, framesOut, Fft, voiced, target, originalPeak, flatPeak, gain,
            sources.Count, analysisFrames, parameterBytes);
    }

    private static Unit[] Split(string reading, Dictionary<string, OtoEntry> aliases)
    {
        var units = new List<Unit>();
        for (var i = 0; i < reading.Length; ++i)
        {
            var c = reading[i];
            if (c is '、' or '。' or '?' or '・' or ' ' or 'っ') { units.Add(new(null, 0, c)); continue; }
            if (c == 'ー') throw new InvalidDataException();
            var alias = c.ToString();
            if (i + 1 < reading.Length && "ゃゅょぁぃぅぇぉゎ".Contains(reading[i + 1])) alias += reading[++i];
            alias = alias switch { "ぢ" => "じ", "づ" => "ず", "ぁ" => "あ", "ぃ" => "い", "ぅ" => "う", "ぇ" => "え", "ぉ" => "お", "ゎ" => "わ", _ => alias };
            var extensions = 0;
            while (i + 1 < reading.Length && reading[i + 1] == 'ー')
            {
                if (!HasVowel(alias)) throw new InvalidDataException();
                ++extensions; ++i;
            }
            if (!aliases.TryGetValue(alias, out var entry)) throw new InvalidDataException("比較音源に必要な単独音がありません。");
            units.Add(new(entry, extensions, '\0'));
        }
        if (units.Count is < 1 or > 640) throw new InvalidDataException();
        return units.ToArray();
    }
    private static string Hiragana(string value) => string.Concat(value.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c));
    private static bool HasVowel(string alias) => "あいうえおかきくけこがぎぐげござじずぜぞさしすせそたちつてとだぢづでどなにぬねのはひふへほばびぶべぼぱぴぷぺぽまみむめもやゆよらりるれろわをゔゃゅょぁぃぅぇぉゎ".Contains(alias[^1]);
    private static int Position(double milliseconds)
    {
        var position = milliseconds * Rate / 1000;
        if (!double.IsFinite(position) || position < -MaximumSamples || position > MaximumSamples) throw new InvalidDataException();
        return (int)Math.Round(position);
    }
    private static Timing Crop(double[] samples, OtoEntry entry)
    {
        if (!double.IsFinite(entry.Offset) || !double.IsFinite(entry.Cutoff) || !double.IsFinite(entry.Preutterance) ||
            !double.IsFinite(entry.Consonant) || !double.IsFinite(entry.Overlap) || entry.Offset < 0 || entry.Preutterance < 0 || entry.Consonant < 0) throw new InvalidDataException();
        var start = Position(entry.Offset);
        var end = entry.Cutoff < 0 ? start - Position(entry.Cutoff) : samples.Length - Position(entry.Cutoff);
        if (start < 0 || end > samples.Length || end - start < Position(25)) throw new InvalidDataException();
        var pre = Position(entry.Preutterance);
        var fixedEnd = Math.Max(pre, Position(entry.Consonant));
        if (pre > Position(700) || pre > end - start - Position(15) || fixedEnd > end - start - Position(15)) throw new InvalidDataException();
        var peak = 0.0;
        for (var i = start; i < end; ++i) peak = Math.Max(peak, Math.Abs(samples[i]));
        if (peak < 0.0001) throw new InvalidDataException();
        var threshold = Math.Max(0.00008, peak * 0.001);
        var leading = 0;
        while (leading < pre && Math.Abs(samples[start + leading]) < threshold) ++leading;
        leading = Math.Max(0, leading - Position(2));
        start += leading; pre -= leading; fixedEnd -= leading;
        while (end > start + fixedEnd + Position(20) && Math.Abs(samples[end - 1]) < threshold) --end;
        pre = Math.Max(pre, Math.Min(Position(8), end - start - Position(15)));
        fixedEnd = Math.Max(fixedEnd, pre);
        var lead = Math.Min(pre, Position(90));
        var transition = Math.Min(fixedEnd - pre, Position(70));
        var sourceEnd = start + fixedEnd + Math.Min(end - start - fixedEnd, Position(160));
        var overlap = Math.Max(Position(8), (int)Math.Round(Position(entry.Overlap) * (double)lead / pre));
        return new(start, pre, fixedEnd, sourceEnd, lead, transition, overlap);
    }
    private static Plan Place(Unit[] units, Dictionary<OtoEntry, Timing> timings)
    {
        var notes = new List<Note>();
        var pauses = new List<(int, int)>();
        var cursor = 0;
        Note? previous = null;
        foreach (var unit in units)
        {
            if (unit.Entry is null)
            {
                var length = Position(unit.Pause == 'っ' ? 85 : 110);
                pauses.Add((cursor, checked(cursor + length)));
                cursor = checked(cursor + length); previous = null;
            }
            else
            {
                var timing = timings[unit.Entry];
                var duration = Position(180 + unit.Extensions * 160.0);
                var advance = previous is null ? 0 : Math.Min(previous.Duration, timing.Lead);
                var note = new Note(unit.Entry, timing, cursor - advance, duration, notes.Count);
                if (previous is not null && advance > 0)
                {
                    previous.FadeStart = note.Start;
                    previous.FadeLength = Math.Min(advance, timing.Overlap);
                }
                notes.Add(note); cursor = note.End; previous = note;
            }
            if (cursor > MaximumSamples) throw new InvalidDataException("比較発話が45秒を超えています。");
        }
        if (notes.Count == 0 || cursor < Rate / 10) throw new InvalidDataException();
        return new(notes.ToArray(), pauses.ToArray(), cursor);
    }

    private static double SourcePosition(Note note, double local)
    {
        var timing = note.Timing;
        double position;
        if (local < timing.Lead) position = local * timing.Pre / timing.Lead;
        else if (timing.Transition > 0 && local < timing.Lead + timing.Transition)
            position = timing.Pre + (local - timing.Lead) * (timing.Fixed - timing.Pre) / timing.Transition;
        else position = timing.Fixed + (local - timing.Lead - timing.Transition) *
            (timing.End - timing.Start - timing.Fixed) / (note.Duration - timing.Transition);
        return Math.Clamp(timing.Start + position, timing.Start, timing.End - 1);
    }
    private static double Fade(double position, double length) => 0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(position / Math.Max(1, length), 0, 1));
    private static void Transfer(Note note, int sourceFrames, double[] sourceF0, double[] sourceSpectrum, double[] sourceAp,
        double[] spectrum, double[] noisePower, double[] f0, int[] owner)
    {
        var from = Math.Max(0, (int)Math.Ceiling(note.Start / Hop));
        var to = Math.Min(f0.Length - 1, (int)Math.Ceiling(note.End / Hop) - 1);
        for (var frame = from; frame <= to; ++frame)
        {
            var global = frame * Hop;
            var local = global - note.Start;
            var gain = Fade(local, Position(2));
            if (global >= note.FadeStart) gain *= 1 - Fade(global - note.FadeStart, note.FadeLength);
            if (gain <= 0) continue;
            var sourceAt = SourcePosition(note, local) / Hop;
            var left = Math.Clamp((int)Math.Floor(sourceAt), 0, sourceFrames - 1);
            var right = Math.Min(sourceFrames - 1, left + 1);
            var fraction = Math.Clamp(sourceAt - left, 0, 1);
            var nearest = fraction < 0.5 ? left : right;
            // An incoming unvoiced region retains its V/UV mask, even while
            // the outgoing spectrum fades. Never interpolate zero into a tone.
            if (note.Index >= owner[frame])
            {
                owner[frame] = note.Index;
                f0[frame] = sourceF0[nearest] <= 0 ? 0 : sourceF0[left] > 0 && sourceF0[right] > 0
                    ? Math.Exp(Math.Log(sourceF0[left]) * (1 - fraction) + Math.Log(sourceF0[right]) * fraction)
                    : sourceF0[nearest];
            }
            var weight = gain * gain;
            var destination = frame * Width;
            var a = left * Width; var b = right * Width;
            for (var bin = 0; bin < Width; ++bin)
            {
                var power = sourceSpectrum[a + bin] * (1 - fraction) + sourceSpectrum[b + bin] * fraction;
                var ap = sourceAp[a + bin] * (1 - fraction) + sourceAp[b + bin] * fraction;
                spectrum[destination + bin] += weight * power;
                // WORLD squares amplitude AP during synthesis. Mix noise power,
                // then convert back to amplitude AP after all notes are placed.
                noisePower[destination + bin] += weight * power * ap * ap;
            }
        }
    }
    private static void Envelope(double[] samples, (int Start, int End)[] pauses)
    {
        var fade = Position(5);
        for (var i = 0; i < Math.Min(fade, samples.Length / 2); ++i)
        { samples[i] *= Fade(i, fade); samples[^(i + 1)] *= Fade(i, fade); }
        foreach (var (start, end) in pauses)
        {
            Array.Clear(samples, start, end - start);
            for (var i = 1; i <= fade; ++i)
            {
                if (start - i >= 0) samples[start - i] *= Fade(i, fade);
                if (end + i - 1 < samples.Length) samples[end + i - 1] *= Fade(i, fade);
            }
        }
    }
    private static string ValidateJob(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested)) throw new ArgumentException();
        var job = Path.GetFullPath(requested).TrimEnd(Path.DirectorySeparatorChar);
        var root = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp"));
        WorldComparison.RejectLinks(job);
        if (!job.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetRelativePath(root, job).Contains(':') ||
            !Directory.Exists(job) || Directory.EnumerateFileSystemEntries(job).Any()) throw new ArgumentException("比較にはdata/tmp内の空の一意なjobを指定してください。");
        return job;
    }
    private static string[] ValidateOutputs(string job, params string[] requested)
    {
        var paths = requested.Select(path => Path.GetFullPath(path)).ToArray();
        if (paths.Length != 2 || paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2) throw new ArgumentException();
        foreach (var path in paths)
        {
            WorldComparison.RejectLinks(path);
            if (!path.StartsWith(job + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || Path.GetRelativePath(job, path).Contains(':') ||
                !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) || File.Exists(path) ||
                !Directory.Exists(Path.GetDirectoryName(path))) throw new ArgumentException();
        }
        return paths;
    }
}
