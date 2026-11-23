using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tanayomi.Desktop;

public sealed record WorldComparisonResult(int SampleCount, int FrameCount, int FftSize,
    int VoicedFrames, double FlatF0, double OriginalPeak, double FlatPeak, double Gain);

/// <summary>Explicit research comparison only. Native WORLD is loaded exclusively
/// by the dedicated disposable process, never by the UI or this launcher.</summary>
public static partial class WorldComparison
{
    private const int Rate = 44100;
    private const int MaximumSamples = Rate * 30;
    private const int MinimumSamples = Rate / 10;
    private const int MaximumFrames = 6001;
    private const int MaximumResponseBytes = 4096;
    private const int MinimumVoicedFrames = 10; // At least 50 ms of detected voicing.

    // A fixed-text research path, separate from the production binary worker.
    // The 256 MiB cap counts live managed double buffers, not native/OS memory.
    private const long ProsodyBufferLimit = 256L * 1024 * 1024;
    internal const double ProsodyAlpha = 3;
    internal const double ProsodyBeta = 20;
    internal const double ProsodyCeiling = 0.9;
    internal static readonly double ProsodyPhraseMagnitude = Math.Log(2) / 24 * Math.E / ProsodyAlpha;
    internal static readonly double ProsodyAccentMagnitude = Math.Log(2) / (12 * ProsodyCeiling);

    internal sealed record PhraseCommand(double Time, double Magnitude);
    internal sealed record AccentCommand(double Start, double End, double Magnitude);
    internal sealed record TrackStatistics(double MeanHz, double GeometricMeanHz, double MedianHz,
        double MinimumHz, double MaximumHz, double MaximumAdjacentSemitones);
    internal sealed record ProsodyCondition(string Name, TrackStatistics Track, double RawRms,
        double RawPeak, double RmsGain, double OutputRms, double OutputPeak, double PcmRms, double PcmPeak, int ClippedSamples);
    internal sealed record ProsodyComparisonResult(int SampleCount, int FrameCount, int FftSize,
        int VoicedFrames, double FlatF0, double Gain, double ContourScale, long ManagedDoubleBufferBytes,
        string SpectrumSha256, string AperiodicitySha256, PhraseCommand[] PhraseCommands,
        AccentCommand[] AccentCommands, IReadOnlyList<UtauTimingAnchor> Anchors, ProsodyCondition[] Conditions);
    internal sealed record ProsodyTracks(double[][] Values, TrackStatistics[] Statistics, double Scale);
    internal sealed record LevelMatch(double[] Rms, double[] Peaks, double[] RmsGains, double Headroom);
    internal const double TimingTargetF0 = 259.4256;
    internal sealed record TimingAnalysis(int MoraDurationMilliseconds, int SampleCount, int FrameCount,
        int FftSize, int VoicedFrames, long ManagedDoubleBufferBytes, string SpectrumSha256,
        string AperiodicitySha256, string VoicingSha256, TrackStatistics Track, IReadOnlyList<UtauTimingAnchor> Anchors,
        bool CarryPureVowels = false);
    internal sealed record TimingCondition(string Name, TimingAnalysis Analysis, double RawRms,
        double RawPeak, double RmsGain, double OutputRms, double OutputPeak, double PcmRms,
        double PcmPeak, int ClippedSamples);
    internal sealed record TimingComparisonResult(double FlatF0, double Gain, long ManagedDoubleBufferBytes,
        int LongVowelAdditionalMilliseconds, int ClosureMilliseconds, int PauseMilliseconds, TimingCondition[] Conditions,
        string Comparison = "timing");
    private sealed record TimingRender(double[] Samples, TimingAnalysis Analysis);

    // Only the comparison instance's mora duration changes. The production
    // worker, pitch policy, lead/transition, long vowels and pauses are unchanged.
    internal static int RunTimingWorker(string[] args) => RunPairWorker(args, join: false);

    internal static int RunJoinWorker(string[] args) => RunPairWorker(args, join: true);

    private static int RunPairWorker(string[] args, bool join)
    {
        var owned = new List<string>();
        var success = false;
        try
        {
            ConfigureWorker();
            if (args.Length != 5 || args[0] != (join ? "--world-join-worker" : "--world-timing-worker") ||
                !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var target) ||
                target != TimingTargetF0) throw new ArgumentException();
            var paths = ValidateJobOutputs(args[1], 2, args[2], args[3]);
            var library = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
            RejectLinks(library);
            var bank = Voicebanks.LoadInstalledAsync(Path.Combine(paths[0], "banks")).GetAwaiter().GetResult()
                .Single(value => value.Name == "hosinohito");
            using var native = new NativeApi(library);
            // Analysis matrices stay inside the helper. Only the two final
            // signals and small metadata survive between the sequential runs.
            var baseline = RenderTiming(bank, 180, native, 0);
            var alternative = RenderTiming(bank, join ? 180 : 140, native, baseline.Samples.Length, carryPureVowels: join);
            if (join) ValidateJoinPair(baseline.Analysis, alternative.Analysis);
            var levels = MatchLevels([baseline.Samples, alternative.Samples], allowDifferentLengths: !join);
            var conditions = new TimingCondition[2];
            var renders = new[] { baseline, alternative };
            for (var i = 0; i < renders.Length; i++)
            {
                var gain = levels.RmsGains[i] * levels.Headroom;
                var pcm = WriteWave(paths[i + 1], renders[i].Samples, gain, owned);
                conditions[i] = new(join ? (i == 0 ? "baseline" : "vowelcarry") : (i == 0 ? "mora-180" : "mora-140"), renders[i].Analysis,
                    levels.Rms[i], levels.Peaks[i], levels.RmsGains[i], levels.Rms[i] * gain,
                    levels.Peaks[i] * gain,
                    Math.Sqrt(pcm.Samples.Sum(value => Math.Pow(value / 32768.0, 2)) / pcm.Samples.Length),
                    pcm.Samples.Max(value => Math.Abs((double)value)) / 32768.0,
                    pcm.Samples.Count(value => value is short.MinValue or short.MaxValue));
            }
            var result = new TimingComparisonResult(TimingTargetF0, levels.Headroom,
                Math.Max(baseline.Analysis.ManagedDoubleBufferBytes, alternative.Analysis.ManagedDoubleBufferBytes),
                160, 85, 110, conditions, join ? "join" : "timing");
            var json = JsonSerializer.Serialize(result);
            if (Encoding.UTF8.GetByteCount(json) > 32768) throw new InvalidDataException();
            Console.WriteLine(json); Console.Out.Flush();
            success = true;
            return 0;
        }
        catch (Exception error) { ReportFailure(error); return 1; }
        finally
        {
            if (!success)
                foreach (var path in owned)
                    try { RejectLinks(path); File.Delete(path); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    internal static void ValidateJoinPair(TimingAnalysis baseline, TimingAnalysis alternative)
    {
        if (baseline.MoraDurationMilliseconds != 180 || alternative.MoraDurationMilliseconds != 180 ||
            baseline.CarryPureVowels || !alternative.CarryPureVowels || baseline.SampleCount != alternative.SampleCount ||
            !baseline.Anchors.SequenceEqual(alternative.Anchors)) throw new InvalidDataException();
    }

    private static TimingRender RenderTiming(Voicebank bank, int duration, NativeApi native, int retainedSamples,
        bool carryPureVowels = false)
    {
        var reference = UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank,
            moraDurationMilliseconds: duration, carryPureVowels: carryPureVowels).GetAwaiter().GetResult();
        var audio = reference.Audio;
        if (audio.SampleRate != Rate || audio.Samples.Length is < MinimumSamples or > Rate * 45 ||
            reference.Anchors.Count is < 1 or > 128 || retainedSamples is < 0 or > Rate * 45)
            throw new InvalidDataException();
        var samples = audio.Samples.Select(value => value / 32768.0).ToArray();
        Check(native.Layout(samples.Length, out var frames, out var fft));
        if (fft != 2048 || frames is < 2 or > 9001 ||
            frames != (int)(1000.0 * samples.Length / Rate / 5.0) + 1) throw new InvalidDataException();
        var cells = checked(frames * (fft / 2 + 1));
        // Include the first output retained during the second analysis and all
        // small temporary tracks made by the existing validated flat generator.
        var bytes = checked((2L * cells + 9L * frames + 2L * samples.Length + retainedSamples) * sizeof(double));
        if (bytes > ProsodyBufferLimit) throw new InvalidDataException();
        var sourceF0 = new double[frames]; var times = new double[frames];
        var spectrum = new double[cells]; var ap = new double[cells];
        Check(native.Analyze(samples, samples.Length, sourceF0, frames, times, frames, spectrum, cells, ap, cells, fft));
        var tracks = BuildProsodyTracks(sourceF0, times, samples.Length, TimingTargetF0, [], []);
        var flat = tracks.Values[0];
        var spectrumHash = Hash(spectrum); var apHash = Hash(ap);
        var voicing = sourceF0.Select(value => value > 0 ? (byte)1 : (byte)0).ToArray();
        if (!flat.Select((value, i) => value == (voicing[i] == 1 ? TimingTargetF0 : 0)).All(value => value))
            throw new InvalidDataException();
        var output = new double[samples.Length];
        Check(native.Synthesize(flat, frames, spectrum, cells, ap, cells, fft, output, output.Length));
        if (Hash(spectrum) != spectrumHash || Hash(ap) != apHash) throw new InvalidDataException();
        return new(output, new(duration, samples.Length, frames, fft, voicing.Count(value => value == 1), bytes,
            spectrumHash, apHash, Convert.ToHexString(SHA256.HashData(voicing)), tracks.Statistics[0], reference.Anchors, carryPureVowels));
    }

    /// <summary>Fixed B PCM -> one shared WORLD analysis -> flat/phrase/accent
    /// controls. Manual command positions are experiments, not lexical inference.</summary>
    internal static int RunProsodyWorker(string[] args)
    {
        var owned = new List<string>();
        var success = false;
        try
        {
            ConfigureWorker();
            if (args.Length != 6 || args[0] != "--world-prosody-worker" ||
                !double.TryParse(args[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var target) ||
                !double.IsFinite(target) || target is < 40 or > 1200) throw new ArgumentException();
            var paths = ValidateProsodyPaths(args[1], args[2], args[3], args[4]);
            var library = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
            RejectLinks(library);
            // A unique non-default root cannot migrate or edit the user's bank.
            var banks = Voicebanks.LoadInstalledAsync(Path.Combine(paths[0], "banks")).GetAwaiter().GetResult();
            var bank = banks.Single(value => value.Name == "hosinohito");
            var reference = UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank).GetAwaiter().GetResult();
            using var native = new NativeApi(library);
            var result = RenderProsody(reference, target, native, paths, owned);
            var json = JsonSerializer.Serialize(result);
            if (Encoding.UTF8.GetByteCount(json) > 16384) throw new InvalidDataException();
            Console.WriteLine(json); Console.Out.Flush();
            success = true;
            return 0;
        }
        catch (Exception error) { ReportFailure(error); return 1; }
        finally
        {
            if (!success)
                foreach (var path in owned)
                    try { RejectLinks(path); File.Delete(path); }
                    catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private static ProsodyComparisonResult RenderProsody(UtauReferenceTiming reference, double target,
        NativeApi native, string[] paths, List<string> owned)
    {
        var audio = reference.Audio;
        if (audio.SampleRate != Rate || audio.Samples.Length is < MinimumSamples or > Rate * 45) throw new InvalidDataException();
        var samples = audio.Samples.Select(sample => sample / 32768.0).ToArray();
        Check(native.Layout(samples.Length, out var frames, out var fft));
        if (fft != 2048 || frames is < 2 or > 9001 || frames != (int)(1000.0 * samples.Length / Rate / 5.0) + 1)
            throw new InvalidDataException();
        var cells = checked(frames * (fft / 2 + 1));
        var bufferBytes = checked((2L * cells + 5L * frames + 4L * samples.Length) * sizeof(double));
        if (bufferBytes > ProsodyBufferLimit) throw new InvalidDataException();
        var f0 = new double[frames]; var times = new double[frames];
        var spectrum = new double[cells]; var aperiodicity = new double[cells];
        Check(native.Analyze(samples, samples.Length, f0, frames, times, frames, spectrum, cells, aperiodicity, cells, fft));
        var commands = ManualCommands(reference.Anchors, samples.Length);
        var tracks = BuildProsodyTracks(f0, times, samples.Length, target, commands.Phrases, commands.Accents);
        var spectrumHash = Hash(spectrum); var apHash = Hash(aperiodicity);
        var rendered = new double[3][];
        for (var i = 0; i < rendered.Length; i++)
        {
            rendered[i] = new double[samples.Length];
            Check(native.Synthesize(tracks.Values[i], frames, spectrum, cells, aperiodicity, cells, fft,
                rendered[i], rendered[i].Length));
            if (Hash(spectrum) != spectrumHash || Hash(aperiodicity) != apHash) throw new InvalidDataException();
        }
        var levels = MatchLevels(rendered);
        var names = new[] { "flat", "phrase", "phrase-accent" };
        var conditions = new ProsodyCondition[3];
        for (var i = 0; i < conditions.Length; i++)
        {
            var gain = levels.RmsGains[i] * levels.Headroom;
            var pcm = WriteWave(paths[i + 1], rendered[i], gain, owned);
            var pcmRms = Math.Sqrt(pcm.Samples.Sum(value => Math.Pow(value / 32768.0, 2)) / pcm.Samples.Length);
            var pcmPeak = pcm.Samples.Max(value => Math.Abs((double)value)) / 32768.0;
            conditions[i] = new(names[i], tracks.Statistics[i], levels.Rms[i], levels.Peaks[i],
                levels.RmsGains[i], levels.Rms[i] * gain, levels.Peaks[i] * gain, pcmRms, pcmPeak,
                pcm.Samples.Count(value => value is short.MinValue or short.MaxValue));
        }
        return new(samples.Length, frames, fft, f0.Count(value => value > 0), target, levels.Headroom,
            tracks.Scale, bufferBytes, spectrumHash, apHash, commands.Phrases, commands.Accents, reference.Anchors, conditions);
    }

    private static string Hash(double[] values) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(values.AsSpan())));

    private static string[] ValidateProsodyPaths(string requestedJob, params string[] outputs)
        => ValidateJobOutputs(requestedJob, 3, outputs);

    private static string[] ValidateJobOutputs(string requestedJob, int expectedOutputs, params string[] outputs)
    {
        if (string.IsNullOrWhiteSpace(requestedJob)) throw new ArgumentException();
        var root = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp")).TrimEnd(Path.DirectorySeparatorChar);
        var job = Path.GetFullPath(requestedJob).TrimEnd(Path.DirectorySeparatorChar);
        if (!job.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetRelativePath(root, job).Contains(':') || !Directory.Exists(job)) throw new ArgumentException();
        RejectLinks(job);
        if (expectedOutputs is < 2 or > 3 || outputs.Length != expectedOutputs || outputs.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException();
        var paths = outputs.Select(Path.GetFullPath).ToArray();
        if (paths.Distinct(StringComparer.OrdinalIgnoreCase).Count() != expectedOutputs) throw new ArgumentException();
        foreach (var path in paths)
        {
            if (!string.Equals(Path.GetDirectoryName(path), job, StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(job, path).Contains(':') ||
                !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase) || File.Exists(path)) throw new ArgumentException();
            RejectLinks(path);
        }
        return [job, .. paths];
    }

    internal static (PhraseCommand[] Phrases, AccentCommand[] Accents) ManualCommands(
        IReadOnlyList<UtauTimingAnchor> anchors, int sampleCount)
    {
        if (anchors.Count is < 5 or > 128 || sampleCount is < MinimumSamples or > Rate * 45) throw new InvalidDataException();
        var phrases = new List<PhraseCommand>();
        var afterPause = true;
        for (var i = 0; i < anchors.Count; i++)
        {
            var anchor = anchors[i];
            if (anchor.UnitIndex != i || anchor.StartSample < 0 || anchor.EndSample > sampleCount ||
                anchor.EndSample <= anchor.StartSample) throw new InvalidDataException();
            if (anchor.Alias is null)
            {
                if (anchor.VowelOnsetSample is not null) throw new InvalidDataException();
                // A closure is inside its word, never a new phrase command.
                if (anchor.Pause != 'っ') afterPause = true;
                continue;
            }
            if (anchor.VowelOnsetSample is not int onset || onset < anchor.StartSample || onset >= anchor.EndSample)
                throw new InvalidDataException();
            if (afterPause) phrases.Add(new(onset / (double)Rate - 0.100, ProsodyPhraseMagnitude));
            afterPause = false;
        }
        // Only this review phrase/profile is supported. Do not silently move a
        // presumed lexical accent when a future reading dictionary changes it.
        var spoken = anchors.Where(anchor => anchor.Alias is not null).ToArray();
        // The installed pronunciation dictionary says アリガトー: five moras,
        // four renderer units, with the final long vowel attached to と.
        if (phrases.Count != 4 || spoken.Length < 4 ||
            !spoken.TakeLast(4).Select(anchor => anchor.Alias).SequenceEqual(new[] { "あ", "り", "が", "と" }) ||
            spoken.TakeLast(4).Take(3).Any(anchor => anchor.Extensions != 0) || spoken[^1].Extensions != 1)
            throw new InvalidDataException();
        var start = spoken[^4].VowelOnsetSample!.Value / (double)Rate - 0.050;
        var end = spoken[^2].VowelOnsetSample!.Value / (double)Rate - 0.050;
        if (end <= start) throw new InvalidDataException();
        return (phrases.ToArray(), [new(start, end, ProsodyAccentMagnitude)]);
    }

    // Fujisaki/Hirose's response functions. Values below zero time are zero.
    // The ceiling is the model's original saturation, not F0 hard clipping.
    internal static double PhraseResponse(double t) => t < 0 ? 0 : ProsodyAlpha * ProsodyAlpha * t * Math.Exp(-ProsodyAlpha * t);
    internal static double AccentResponse(double t) => t < 0 ? 0 : Math.Min(1 - (1 + ProsodyBeta * t) * Math.Exp(-ProsodyBeta * t), ProsodyCeiling);

    internal static ProsodyTracks BuildProsodyTracks(double[] sourceF0, double[] times, int sampleCount,
        double target, PhraseCommand[] phrases, AccentCommand[] accents)
    {
        if (sourceF0.Length != times.Length || sourceF0.Length is < 2 or > 9001 ||
            sampleCount is < MinimumSamples or > Rate * 45 || sourceF0.Length != (int)(1000.0 * sampleCount / Rate / 5.0) + 1 ||
            !double.IsFinite(target) || target is < 40 or > 1200 || phrases.Length > 16 || accents.Length > 16)
            throw new InvalidDataException();
        var duration = sampleCount / (double)Rate;
        if (phrases.Any(command => !double.IsFinite(command.Time) || command.Time < -1 || command.Time > duration ||
                !double.IsFinite(command.Magnitude) || command.Magnitude is < 0 or > 1) ||
            accents.Any(command => !double.IsFinite(command.Start) || !double.IsFinite(command.End) ||
                command.Start < -1 || command.End > duration || command.End <= command.Start ||
                !double.IsFinite(command.Magnitude) || command.Magnitude is < 0 or > 1)) throw new InvalidDataException();
        var raw = new[] { new double[times.Length], new double[times.Length], new double[times.Length] };
        var weights = new double[times.Length];
        var voiced = 0;
        for (var i = 0; i < times.Length; i++)
        {
            if (!double.IsFinite(times[i]) || Math.Abs(times[i] - i * 0.005) > 1e-9 ||
                !double.IsFinite(sourceF0[i]) || sourceF0[i] != 0 && sourceF0[i] is not (>= 40 and <= 1200))
                throw new InvalidDataException();
            if (sourceF0[i] > 0)
            {
                voiced++;
                weights[i] = Math.Clamp(duration - times[i], 0, 0.005);
            }
            raw[1][i] = phrases.Sum(command => command.Magnitude * PhraseResponse(times[i] - command.Time));
            raw[2][i] = raw[1][i] + accents.Sum(command => command.Magnitude *
                (AccentResponse(times[i] - command.Start) - AccentResponse(times[i] - command.End)));
        }
        if (voiced < MinimumVoicedFrames || weights.Sum() <= 0) throw new InvalidDataException();
        // A common coefficient preserves the phrase-only vs added-accent contrast.
        // Limiting the entire log range keeps the centered contour within ±2 st.
        var range = raw.Max(values => values.Max()) - raw.Min(values => values.Min());
        var scale = range > 0 ? Math.Min(1, Math.Log(2) / 6 / range) : 1;
        var result = new double[3][];
        var statistics = new TrackStatistics[3];
        for (var condition = 0; condition < 3; condition++)
        {
            var denominator = weights.Sum();
            var center = raw[condition].All(value => value == 0) ? 0 :
                Math.Log(raw[condition].Select((value, i) => weights[i] * Math.Exp(value * scale)).Sum() / denominator);
            result[condition] = new double[times.Length];
            for (var i = 0; i < times.Length; i++)
            {
                if (sourceF0[i] == 0) continue;
                var value = target * Math.Exp(raw[condition][i] * scale - center);
                if (!double.IsFinite(value) || value is < 40 or > 1200 || Math.Abs(12 * Math.Log2(value / target)) > 2 + 1e-9)
                    throw new InvalidDataException();
                result[condition][i] = value;
            }
            statistics[condition] = Statistics(result[condition], weights);
        }
        return new(result, statistics, scale);
    }

    private static TrackStatistics Statistics(double[] values, double[] weights)
    {
        var sorted = values.Where(value => value > 0).Order().ToArray();
        var duration = weights.Sum();
        var mean = values.Select((value, i) => value * weights[i]).Sum() / duration;
        var geometric = Math.Exp(values.Select((value, i) => value > 0 ? Math.Log(value) * weights[i] : 0).Sum() / duration);
        var step = 0.0;
        for (var i = 1; i < values.Length; i++)
            if (values[i] > 0 && values[i - 1] > 0) step = Math.Max(step, Math.Abs(12 * Math.Log2(values[i] / values[i - 1])));
        return new(mean, geometric, (sorted[(sorted.Length - 1) / 2] + sorted[sorted.Length / 2]) / 2,
            sorted[0], sorted[^1], step);
    }

    internal static LevelMatch MatchLevels(double[][] signals, bool allowDifferentLengths = false)
    {
        if (signals.Length is < 2 or > 3 || signals.Any(signal => signal is null || signal.Length is < 1 or > Rate * 45) ||
            !allowDifferentLengths && signals.Any(signal => signal.Length != signals[0].Length))
            throw new InvalidDataException();
        var peaks = signals.Select(Peak).ToArray();
        if (peaks.Any(peak => peak < 1e-10)) throw new InvalidDataException();
        var rms = signals.Select((signal, i) => peaks[i] * Math.Sqrt(signal.Sum(value => Math.Pow(value / peaks[i], 2)) / signal.Length)).ToArray();
        if (rms.Any(value => !double.IsFinite(value) || value < 1e-10)) throw new InvalidDataException();
        // Whole-utterance constants only: no envelope/per-mora gain pumping.
        var rmsGains = rms.Select(value => rms.Min() / value).ToArray();
        var maximumPeak = peaks.Select((peak, i) => peak * rmsGains[i]).Max();
        return new(rms, peaks, rmsGains, Math.Min(1, 0.92 / maximumPeak));
    }

    // The caller owns a unique data/tmp job directory and cleans it in finally,
    // including incomplete output left when a timed-out native process is killed.
    public static async Task<WorldComparisonResult> CompareAsync(string desktopExecutable, string inputWave,
        string originalWave, string flatWave, double flatF0 = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = WorldNativePlatform.CurrentRid;
        var executable = Path.GetFullPath(desktopExecutable);
        if (!Path.GetFileName(executable).Equals(OperatingSystem.IsWindows() ? "Tanayomi.Desktop.exe" : "Tanayomi.Desktop", OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || !File.Exists(executable))
            throw new ArgumentException("既存のTanayomi.Desktop.exeを指定してください。", nameof(desktopExecutable));
        RejectLinks(executable);
        var root = Path.Combine(Path.GetDirectoryName(executable)!, "data", "tmp");
        var paths = ValidatePaths(root, inputWave, originalWave, flatWave);
        ValidateFlatF0(flatF0);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(120));
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--world-compare-worker");
        foreach (var path in paths) start.ArgumentList.Add(path);
        start.ArgumentList.Add(flatF0.ToString("R", CultureInfo.InvariantCulture));
        start.Environment["TEMP"] = root; start.Environment["TMP"] = root; start.Environment["TMPDIR"] = root;
        using var process = NativeChildProcess.Start(start) ?? throw new IOException("WORLD比較ワーカーを開始できませんでした。");
        process.StandardInput.Close();
        using var registration = timeout.Token.Register(() => Kill(process));
        var response = ReadResponseAsync(process.StandardOutput.BaseStream, timeout.Token);
        var errors = DrainAsync(process.StandardError.BaseStream, timeout.Token);
        try
        {
            var exited = process.WaitForExitAsync(timeout.Token);
            // A rejected oversized response must terminate its worker immediately,
            // rather than waiting while the now-unread pipe fills up.
            var remaining = new List<Task> { exited, response, errors };
            while (remaining.Count != 0)
            {
                var finished = await Task.WhenAny(remaining).ConfigureAwait(false);
                await finished.ConfigureAwait(false);
                remaining.Remove(finished);
            }
            var text = await response.ConfigureAwait(false);
            await errors.ConfigureAwait(false);
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException($"WORLD比較ワーカーが失敗しました（終了コード{process.ExitCode}）。");
            var result = JsonSerializer.Deserialize<WorldComparisonResult>(text)
                ?? throw new InvalidDataException("WORLD比較ワーカーの応答が不正です。");
            ValidateResult(result);
            return result;
        }
        catch
        {
            var deadlineElapsed = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
            Kill(process);
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException) { }
            // Observe pending pipes even on failure without waiting beyond cancellation.
            timeout.Cancel();
            try { await Task.WhenAll(response, errors).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception) { }
            cancellationToken.ThrowIfCancellationRequested();
            if (deadlineElapsed) throw new TimeoutException("WORLD比較ワーカーが制限時間を超えました。");
            throw;
        }
    }

    internal static int RunWorker(string[] args)
    {
        var ownedOutputs = new List<string>();
        var success = false;
        try
        {
            ConfigureWorker();
            if (args.Length != 5 || args[0] != "--world-compare-worker" ||
                !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var requestedF0)) throw new ArgumentException();
            ValidateFlatF0(requestedF0);
            var paths = ValidatePaths(Path.Combine(AppPaths.DataDirectory, "tmp"), args[1], args[2], args[3]);
            var samples = ReadWave(paths[0]);
            var libraryPath = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
            RejectLinks(libraryPath);
            using var native = new NativeApi(libraryPath);
            Check(native.Layout(samples.Length, out var frames, out var fft));
            if (frames is < 2 or > MaximumFrames || fft != 2048) throw new InvalidDataException();
            var cells = checked(frames * (fft / 2 + 1));
            var f0 = new double[frames]; var times = new double[frames];
            var spectrum = new double[cells]; var aperiodicity = new double[cells];
            Check(native.Analyze(samples, samples.Length, f0, frames, times, frames, spectrum, cells, aperiodicity, cells, fft));
            var voiced = f0.Where(x => x > 0).Order().ToArray();
            if (voiced.Length < MinimumVoicedFrames) throw new InvalidDataException("有声フレームが不足しています。");
            var target = requestedF0 > 0 ? requestedF0 :
                (voiced[(voiced.Length - 1) / 2] + voiced[voiced.Length / 2]) / 2;
            var flatF0 = f0.Select(x => x > 0 ? target : 0).ToArray();
            var original = new double[samples.Length]; var flattened = new double[samples.Length];
            Check(native.Synthesize(f0, frames, spectrum, cells, aperiodicity, cells, fft, original, original.Length));
            Check(native.Synthesize(flatF0, frames, spectrum, cells, aperiodicity, cells, fft, flattened, flattened.Length));
            var originalPeak = Peak(original); var flatPeak = Peak(flattened);
            var peak = Math.Max(originalPeak, flatPeak);
            if (peak < 1e-10) throw new InvalidDataException("再合成された音声が無音です。");
            var gain = Math.Min(1.0, 0.92 / peak);
            WriteWave(paths[1], original, gain, ownedOutputs);
            WriteWave(paths[2], flattened, gain, ownedOutputs);
            var result = new WorldComparisonResult(samples.Length, frames, fft, voiced.Length, target, originalPeak, flatPeak, gain);
            Console.WriteLine(JsonSerializer.Serialize(result));
            Console.Out.Flush();
            success = true;
            return 0;
        }
        catch (Exception ex)
        {
            // Never print a native error string, path, user input or arbitrary message.
            ReportFailure(ex);
            return 1;
        }
        finally
        {
            if (!success)
                foreach (var path in ownedOutputs)
                    try { RejectLinks(path); File.Delete(path); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        }
    }

    private static string[] ValidatePaths(string root, params string[] requested)
    {
        root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        RejectLinks(root);
        var result = requested.Select(path => Path.GetFullPath(path)).ToArray();
        if (result.Length != 3 || result.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) throw new ArgumentException("比較の入出力は別ファイルにしてください。");
        foreach (var path in result)
        {
            if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(root, path).Contains(':') || !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("比較の入出力は実行ファイル横data/tmp内のWAVに限ります。");
            RejectLinks(path);
        }
        if (!File.Exists(result[0])) throw new FileNotFoundException("比較入力がありません。");
        if (File.Exists(result[1]) || File.Exists(result[2])) throw new IOException("比較出力は既存ファイルへ上書きできません。");
        if (result.Any(path => !Directory.Exists(Path.GetDirectoryName(path)))) throw new DirectoryNotFoundException("比較用の一意な作業フォルダーを先に作成してください。");
        return result;
    }
    internal static void ConfigureWorker()
    {
        _ = WorldNativePlatform.CurrentRid;
        // Changes only the disposable worker, never its launching/UI process.
        if (OperatingSystem.IsWindows()) SetErrorMode(GetErrorMode() | NativeChildProcess.RequiredErrorMode);
    }
    internal static void ReportFailure(Exception error)
    {
        try
        {
            Console.Error.WriteLine(error is NativeWorldException nativeError
                ? $"world-error:native:{nativeError.Status}"
                : $"world-error:{error.GetType().Name}:{error.HResult:X8}");
        }
        catch (Exception outputError) when (outputError is IOException or ObjectDisposedException) { }
    }
    internal static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクを含む比較パスは使えません。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
    internal static void ValidateFlatF0(double value)
    {
        if (!double.IsFinite(value) || value != 0 && (value < 40 || value > 1200)) throw new ArgumentOutOfRangeException(nameof(value));
    }
    private static void ValidateResult(WorldComparisonResult result)
    {
        if (result.SampleCount is < MinimumSamples or > MaximumSamples || result.FrameCount is < 2 or > MaximumFrames ||
            result.FrameCount != (int)(1000.0 * result.SampleCount / Rate / 5.0) + 1 ||
            result.FftSize != 2048 || result.VoicedFrames < MinimumVoicedFrames || result.VoicedFrames > result.FrameCount ||
            !double.IsFinite(result.FlatF0) || result.FlatF0 is < 40 or > 1200 ||
            !double.IsFinite(result.OriginalPeak) || result.OriginalPeak < 0 || !double.IsFinite(result.FlatPeak) || result.FlatPeak < 0 ||
            !double.IsFinite(result.Gain) || result.Gain is <= 0 or > 1) throw new InvalidDataException("WORLD比較ワーカーの結果が不正です。");
    }
    internal static double[] ReadWave(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length is < 44 or > 4 * 1024 * 1024) throw new InvalidDataException();
        var bytes = new byte[(int)input.Length]; input.ReadExactly(bytes);
        if (!bytes.AsSpan(0, 4).SequenceEqual("RIFF"u8) || !bytes.AsSpan(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException();
        var end = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)) + 8;
        if (end != bytes.Length) throw new InvalidDataException();
        var format = false; var dataStart = -1; var dataLength = 0;
        long position = 12;
        for (; position + 8 <= end;)
        {
            var p = (int)position;
            var length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(p + 4));
            if (position + 8 + length > end) throw new InvalidDataException();
            if (bytes.AsSpan(p, 4).SequenceEqual("fmt "u8))
            {
                if (format || length < 16 || BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 8)) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 10)) != 1 ||
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 12)) != Rate ||
                    BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(p + 16)) != Rate * 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 20)) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(p + 22)) != 16) throw new InvalidDataException("比較入力は44.1kHz mono PCM16に限ります。");
                format = true;
            }
            else if (bytes.AsSpan(p, 4).SequenceEqual("data"u8))
            {
                if (dataStart >= 0) throw new InvalidDataException();
                dataStart = p + 8; dataLength = checked((int)length);
            }
            position += 8L + length + (length & 1);
        }
        var count = dataLength / 2;
        if (position != end || !format || dataStart < 0 || (dataLength & 1) != 0 || count is < MinimumSamples or > MaximumSamples) throw new InvalidDataException();
        var samples = new double[count];
        for (var i = 0; i < count; ++i) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(dataStart + i * 2)) / 32768.0;
        return samples;
    }
    internal static double Peak(double[] samples)
    {
        var peak = 0.0;
        foreach (var sample in samples) { if (!double.IsFinite(sample)) throw new InvalidDataException(); peak = Math.Max(peak, Math.Abs(sample)); }
        return peak;
    }
    internal static PcmAudio WriteWave(string path, double[] samples, double gain, List<string> owned)
    {
        RejectLinks(path);
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        owned.Add(path);
        var pcm = samples.Select(x => (short)Math.Clamp((int)Math.Round(x * gain * 32767), short.MinValue, short.MaxValue)).ToArray();
        var audio = new PcmAudio(pcm, Rate);
        output.Write(audio.ToWaveBytes());
        return audio;
    }
    internal static void Check(int status)
    {
        if (status != 0) throw new NativeWorldException(status);
    }
    private sealed class NativeWorldException(int status) : IOException("WORLD C ABI rejected the operation.")
    { internal int Status { get; } = status; }
    private static async Task<string> ReadResponseAsync(Stream stream, CancellationToken ct)
    {
        using var result = new MemoryStream(); var buffer = new byte[512]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (result.Length + count > MaximumResponseBytes) throw new InvalidDataException("WORLDワーカーの応答が長すぎます。");
            result.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(result.ToArray());
    }
    private static async Task DrainAsync(Stream stream, CancellationToken ct)
    { var buffer = new byte[1024]; while (await stream.ReadAsync(buffer, ct).ConfigureAwait(false) != 0) { } }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint SetErrorMode(uint mode);
    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern uint GetErrorMode();

    internal sealed class NativeApi : IDisposable
    {
        private readonly nint library;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int VersionCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LayoutCall(int sampleCount, out int frames, out int fft);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AnalyzeCall(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] double[] samples, int sampleCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] double[] f0, int f0Count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] double[] times, int timeCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 7)] double[] spectrum, int spectrumCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 9)] double[] aperiodicity, int aperiodicityCount, int fft);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SynthesizeCall(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] double[] f0, int frames,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] double[] spectrum, int spectrumCount,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] double[] aperiodicity, int aperiodicityCount, int fft,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] double[] output, int outputCount);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int AnalyzeF0Call(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] double[] samples, int sampleCount,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] double[] f0, int f0Count,
            [Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 5)] double[] times, int timeCount);
        internal AnalyzeF0Call? AnalyzeF0 { get; }
        internal AnalyzeF0Call? AnalyzeFastF0 { get; }
        internal AnalyzeCall? AnalyzeParallel { get; }
        internal AnalyzeCall? AnalyzeFastParallel { get; }
        internal LayoutCall Layout { get; }
        internal AnalyzeCall Analyze { get; }
        internal AnalyzeCall AnalyzeFast => Load<AnalyzeCall>("tw_world_analyze_dio");
        internal SynthesizeCall Synthesize { get; }
        internal NativeApi(string path, bool disableOptionalF0ForComparison = false)
        {
            library = NativeLibrary.Load(path);
            try
            {
                if (Load<VersionCall>("tw_world_abi_version")() != 1) throw new InvalidDataException("WORLD C ABI版が一致しません。");
                Layout = Load<LayoutCall>("tw_world_layout");
                Analyze = Load<AnalyzeCall>("tw_world_analyze");
                if (!disableOptionalF0ForComparison && NativeLibrary.TryGetExport(library, "tw_world_analyze_f0", out var f0Export))
                    AnalyzeF0 = Marshal.GetDelegateForFunctionPointer<AnalyzeF0Call>(f0Export);
                if (!disableOptionalF0ForComparison && NativeLibrary.TryGetExport(library, "tw_world_analyze_dio_f0", out var fastF0Export))
                    AnalyzeFastF0 = Marshal.GetDelegateForFunctionPointer<AnalyzeF0Call>(fastF0Export);
                if (NativeLibrary.TryGetExport(library, "tw_world_analyze_parallel", out var parallelExport))
                    AnalyzeParallel = Marshal.GetDelegateForFunctionPointer<AnalyzeCall>(parallelExport);
                if (NativeLibrary.TryGetExport(library, "tw_world_analyze_dio_parallel", out var fastParallelExport))
                    AnalyzeFastParallel = Marshal.GetDelegateForFunctionPointer<AnalyzeCall>(fastParallelExport);
                Synthesize = Load<SynthesizeCall>("tw_world_synthesize");
            }
            catch { NativeLibrary.Free(library); throw; }
        }
        private T Load<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        public void Dispose() => NativeLibrary.Free(library);
    }
}


