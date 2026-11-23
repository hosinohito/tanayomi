using System.Diagnostics;
using Tanayomi;
using Tanayomi.Desktop;

// Explicit opt-in only: requires the separately audited native DLL beside this
// runner and its --world-pcm-worker dispatch. No playback or saved audio.
public static class WorldIntegrationTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Native WORLD integration requires Windows x64.");
        var library = Path.Combine(AppContext.BaseDirectory, "tanayomi-world.dll");
        WorldComparison.RejectLinks(library);
        if (!File.Exists(library)) throw new FileNotFoundException("Explicit native integration requires the audited WORLD DLL beside the test runner.");

        using var temporary = new TestDirectory();
        var banks = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).ConfigureAwait(false);
        var bank = banks.Single(b => b.Name == "hosinohito");
        var preparation = Stopwatch.StartNew();
        var input = await WorldReference.CreateAsync(bank, CancellationToken.None).ConfigureAwait(false);
        var referenceMilliseconds = preparation.Elapsed.TotalMilliseconds;
        await RunF0AuditAsync(check).ConfigureAwait(false);
        var referenceCalls = 0;
        var processor = new DesktopWorldPostprocessor((selected, ct) =>
        {
            Interlocked.Increment(ref referenceCalls);
            return WorldReference.CreateAsync(selected, ct);
        });
        try
        {
            var watch = Stopwatch.StartNew();
            await processor.PrepareAsync(bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            var calibration = watch.Elapsed.TotalMilliseconds;
            check(Volatile.Read(ref referenceCalls) == 1,
                "explicit real WORLD preparation completes one inaudible bank-reference calibration before speech");
            await processor.PrepareAsync(bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            check(Volatile.Read(ref referenceCalls) == 1,
                "repeated real WORLD preparation reuses the existing bank calibration");
            watch.Restart();
            var first = await processor.ProcessAsync(input, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            var firstSpeech = watch.Elapsed.TotalMilliseconds;
            watch.Restart();
            var second = await processor.ProcessAsync(input, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            var warm = watch.Elapsed.TotalMilliseconds;
            check(first.SampleRate == 44100 && first.Samples.Length == input.Samples.Length &&
                second.SampleRate == 44100 && second.Samples.Length == input.Samples.Length,
                "real WORLD worker preserves complete B PCM length and sample rate after preparation and on repeated speech");
            check(Peak(first) is > 500 and <= 30147 && Peak(second) is > 500 and <= 30147,
                "real WORLD postprocessing returns non-silent speech with global headroom and no PCM saturation");
            check(Volatile.Read(ref referenceCalls) == 1,
                "prepared real WORLD speech reuses its one bank calibration rather than measuring the utterance for its target");
            Console.WriteLine($"WORLD explicit integration: B input generation {referenceMilliseconds:F1} ms; PrepareAsync calibration {calibration:F1} ms; first prepared speech {firstSpeech:F1} ms; repeated speech {warm:F1} ms; PCM {input.Samples.Length / 44100.0:F6} s.");

            // Independent time-domain observer, not another WORLD/Harvest call.
            // Pick two naturally separated source vowels so equal output pitch
            // cannot pass merely because the inputs already shared one F0.
            var vowels = new List<(PcmAudio Audio, double Pitch)>();
            foreach (var vowel in new[] { "あ", "い", "う", "え", "お" })
            {
                var audio = await UtauSpeech.SynthesizeReferenceAsync(vowel + "ーー", bank).ConfigureAwait(false);
                var pitch = EstimateVowelPitch(audio);
                if (double.IsFinite(pitch)) vowels.Add((audio, pitch));
            }
            var ordered = vowels.OrderBy(vowel => vowel.Pitch).ToArray();
            check(ordered.Length >= 2 && ordered[^1].Pitch / ordered[0].Pitch > 1.08,
                "real-bank integration supplies two independently observed different source vowel pitches");
            var low = await processor.ProcessAsync(ordered[0].Audio, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            var high = await processor.ProcessAsync(ordered[^1].Audio, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            var lowPitch = EstimateVowelPitch(low); var highPitch = EstimateVowelPitch(high);
            check(double.IsFinite(lowPitch) && double.IsFinite(highPitch) && Math.Abs(Math.Log(lowPitch / highPitch)) < Math.Log(1.04),
                "real WORLD fixed-bank calibration gives different spoken vowels the same independently observed stable F0");
            check(Volatile.Read(ref referenceCalls) == 1,
                "changing the real spoken PCM does not replace the retained bank F0 reference");
            Console.WriteLine($"WORLD independent autocorrelation: source vowels {ordered[0].Pitch:F2}/{ordered[^1].Pitch:F2} Hz; processed {lowPitch:F2}/{highPitch:F2} Hz.");

            // A maximum-length body keeps an already calibrated real exchange
            // outstanding when cancellation is requested. It is never played.
            var longSamples = new short[44100 * 45];
            for (var i = 0; i < longSamples.Length; ++i) longSamples[i] = input.Samples[i % input.Samples.Length];
            using var stop = new CancellationTokenSource();
            var pending = processor.ProcessAsync(new(longSamples, 44100), bank, stop.Token);
            var wasOutstanding = !pending.IsCompleted;
            stop.Cancel();
            var cancelled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
            catch (OperationCanceledException) { cancelled = true; }
            check(wasOutstanding && cancelled, "cancellation interrupts an outstanding real WORLD exchange without accepting partial PCM");
            var recovered = await processor.ProcessAsync(input, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65)).ConfigureAwait(false);
            check(recovered.SampleRate == input.SampleRate && recovered.Samples.Length == input.Samples.Length && Peak(recovered) is > 500 and <= 30147,
                "the next real WORLD utterance succeeds after the cancelled worker has been stopped and replaced");
            check(Volatile.Read(ref referenceCalls) == 1,
                "worker replacement retains the completed bank calibration without caching utterance audio");

            await processor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            var disposed = false;
            try { await processor.ProcessAsync(input, bank, CancellationToken.None).ConfigureAwait(false); }
            catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "real WORLD disposal confirms worker exit and prevents any subsequent processing");
            disposed = false;
            try { await processor.PrepareAsync(bank, CancellationToken.None).ConfigureAwait(false); }
            catch (ObjectDisposedException) { disposed = true; }
            check(disposed, "disposed real WORLD preparation cannot restart a worker or reference calibration");
        }
        finally { await processor.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false); }
        await using var fast = new DesktopWorldPostprocessor(WorldReference.CreateAsync, fastAnalysis: true);
        await fast.PrepareAsync(bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65));
        var fastWatch = Stopwatch.StartNew();
        var fastAudio = await fast.ProcessAsync(input, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65));
        check(fastAudio.SampleRate == input.SampleRate && fastAudio.Samples.Length == input.Samples.Length && Peak(fastAudio) is > 500 and <= 30147,
            "DIO and StoneMask worker preserves real speech length and headroom");
        Console.WriteLine($"DIO prepared synthesis: {fastWatch.Elapsed.TotalMilliseconds:F1} ms.");
        using (var stop = new CancellationTokenSource())
        {
            var pending = fast.ProcessAsync(input, bank, stop.Token); stop.CancelAfter(20);
            var canceled = false;
            try { await pending.WaitAsync(TimeSpan.FromSeconds(10)); } catch (OperationCanceledException) { canceled = true; }
            check(canceled, "DIO worker cancellation returns no partial PCM");
        }
        check((await fast.ProcessAsync(input, bank, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(65))).Samples.Length == input.Samples.Length,
            "DIO worker recovers after cancellation");
    }

    private static async Task RunF0AuditAsync(Action<bool, string> check) =>
        _ = await LaunchAuditAsync(check, null).ConfigureAwait(false);

    internal static async Task RunParallelResourceAuditAsync(Action<bool, string> check)
    {
        var serial = await LaunchAuditAsync(check, "serial").ConfigureAwait(false);
        var parallel = await LaunchAuditAsync(check, "parallel").ConfigureAwait(false);
        static string Digest(string report)
        {
            var line = report.Split('\n').Single(item => item.StartsWith("WORLD_RESOURCE ", StringComparison.Ordinal));
            return line.Split(' ').Single(item => item.StartsWith("digest=", StringComparison.Ordinal));
        }
        check(Digest(serial) == Digest(parallel), "isolated serial/parallel WORLD resource processes preserve all analysis array bits");
    }

    internal static async Task RunLifterAuditAsync(Action<bool, string> check) =>
        _ = await LaunchAuditAsync(check, "lifter").ConfigureAwait(false);

    private static async Task<string> LaunchAuditAsync(Action<bool, string> check, string? resourceMode)
    {
        var executable = Environment.ProcessPath ?? throw new IOException("Missing test executable.");
        var temporary = new TestDirectory();
        var start = new ProcessStartInfo(executable)
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary.DirectoryPath,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(WorldIntegrationTests).Assembly.Location);
        start.ArgumentList.Add(resourceMode is null ? "--world-f0-audit-worker" : "--world-resource-audit-worker");
        if (resourceMode is not null) start.ArgumentList.Add(resourceMode);
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[name] = temporary.DirectoryPath;
        Process? child = null; var reaped = true;
        Task<string>? stdout = null, stderr = null;
        try
        {
            child = NativeChildProcess.Start(start); reaped = false; child.StandardInput.Close();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            stdout = ReadBoundedAsync(child.StandardOutput, deadline.Token);
            stderr = ReadBoundedAsync(child.StandardError, deadline.Token);
            await child.WaitForExitAsync(deadline.Token).ConfigureAwait(false); reaped = true;
            var report = await stdout.ConfigureAwait(false); var errors = await stderr.ConfigureAwait(false);
            check(child.ExitCode == 0, "isolated WORLD F0 audit completes without a native failure: " + errors);
            Console.Write(report);
            if (child.ExitCode != 0) throw new InvalidDataException("WORLD isolated audit failed.");
            return report;
        }
        finally
        {
            if (child is not null && !reaped)
            {
                try
                {
                    if (!child.HasExited) child.Kill(entireProcessTree: true);
                    await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                    reaped = true;
                }
                catch (Exception) { Console.Error.WriteLine("WORLD audit worker exit unconfirmed; fixture retained: " + temporary.DirectoryPath); }
            }
            if (reaped)
            {
                if (stdout is not null) try { await stdout.ConfigureAwait(false); } catch (Exception) { }
                if (stderr is not null) try { await stderr.ConfigureAwait(false); } catch (Exception) { }
            }
            child?.Dispose();
            if (reaped) temporary.Dispose();
        }
        static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
        {
            var result = new System.Text.StringBuilder(); var buffer = new char[1024]; int count;
            while ((count = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false)) != 0)
            {
                if (result.Length + count > 16384) throw new InvalidDataException("WORLD audit output limit.");
                result.Append(buffer, 0, count);
            }
            return result.ToString();
        }
    }

    internal static async Task RunF0AuditWorkerAsync(Action<bool, string> check, string? resourceMode = null)
    {
        if (resourceMode is not null and not ("serial" or "parallel" or "lifter")) throw new InvalidDataException("WORLD resource mode");
        WorldComparison.ConfigureWorker();
        var root = Path.GetFullPath(Environment.CurrentDirectory); var name = Path.GetFileName(root);
        if (name.Length != 38 || !name.StartsWith("tests-", StringComparison.Ordinal) || !name.AsSpan(6).ToString().All(Uri.IsHexDigit) ||
            Path.GetFileName(Path.GetDirectoryName(root)) != "tmp" ||
            Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(root))) != ".local" || Directory.EnumerateFileSystemEntries(root).Any())
            throw new InvalidDataException("WORLD audit requires a fresh owned test root.");
        WorldComparison.RejectLinks(root);
        var library = Path.Combine(AppContext.BaseDirectory, "tanayomi-world.dll");
        WorldComparison.RejectLinks(library);
        var banks = await Voicebanks.LoadInstalledAsync(Environment.CurrentDirectory).ConfigureAwait(false);
        var bank = banks.Single(item => item.Name == "hosinohito");
        var input = await WorldReference.CreateAsync(bank, default).ConfigureAwait(false);
        if (resourceMode == "lifter")
        {
            using var native = new WorldComparison.NativeApi(library);
            var samples = input.Samples.Select(value => value / 32768.0).ToArray();
            if (native.Layout(samples.Length, out var frames, out var fft) != 0 || fft != 2048)
                throw new InvalidDataException("Lifter diagnostic layout");
            var floor = 3.0 * 44100 / (fft - 3.0); // Fixed CheapTrick effective-F0 rule.
            foreach (var fast in new[] { false, true })
            {
                var analyze = (fast ? native.AnalyzeFastF0 : native.AnalyzeF0)
                    ?? throw new InvalidDataException("Lifter diagnostic requires F0-only export");
                var f0 = new double[frames]; var times = new double[frames];
                if (analyze(samples, samples.Length, f0, frames, times, frames) != 0)
                    throw new InvalidDataException("Lifter diagnostic analysis");
                var repeats = 0; var defaultFrames = 0; var longest = 0; var run = 0;
                double previous = double.NaN;
                foreach (var value in f0)
                {
                    if (!double.IsFinite(value) || value != 0 && (value < 40 || value > 1200))
                        throw new InvalidDataException("Lifter diagnostic F0 range");
                    var effective = value <= floor ? 500.0 : value; // WORLD kDefaultF0.
                    if (value <= floor) defaultFrames++;
                    if (effective == previous) { repeats++; run++; } else run = 1;
                    longest = Math.Max(longest, run); previous = effective;
                }
                Console.WriteLine($"WORLD_LIFTER mode={(fast ? "dio" : "harvest")} samples={samples.Length} frames={frames} repeated={repeats} defaultFrames={defaultFrames} longestRun={longest} scope=bank-reference-not-ordinary-speech");
            }
            return;
        }
        if (resourceMode is not null)
        {
            RunResourceAnalysis(library, input, resourceMode);
            return;
        }
        // Same reference-rendering settings as SynthesizeReferenceAsync:
        // no postprocessor, ApplyPitch=false, default mode and timing. The
        // output factory rejects any accidental attempt to start playback.
        await using (var cached = new UtauSpeech(new DesktopPcmOutput(_ => throw new InvalidOperationException("Audit cannot play audio.")))
            { Voicebank = bank, ApplyPitch = false, EnableSourceInspectionDiagnostics = true })
        await using (var uncached = new UtauSpeech(new DesktopPcmOutput(_ => throw new InvalidOperationException("Audit cannot play audio.")))
            { Voicebank = bank, ApplyPitch = false, EnableSourceInspectionDiagnostics = true, DisableSourceInspectionCacheForComparison = true })
        {
            var cachedWarmup = await cached.SynthesizeAsync(WorldReference.ReviewText).ConfigureAwait(false);
            var uncachedWarmup = await uncached.SynthesizeAsync(WorldReference.ReviewText).ConfigureAwait(false);
            check(cachedWarmup.Samples.SequenceEqual(input.Samples) && uncachedWarmup.Samples.SequenceEqual(input.Samples),
                "source inspection audit warmups match the ordinary bank reference PCM");
            var cachedAnalyses = cached.SourceInspectionAnalysisCount; var cachedHits = cached.SourceInspectionCacheHitCount;
            var uncachedAnalyses = uncached.SourceInspectionAnalysisCount; var uncachedHits = uncached.SourceInspectionCacheHitCount;
            var cachedMs = new double[3]; var uncachedMs = new double[3];
            for (var trial = 0; trial < 3; trial++)
            {
                for (var position = 0; position < 2; position++)
                {
                    var useCache = (trial + position) % 2 == 1;
                    var voice = useCache ? cached : uncached;
                    var started = Stopwatch.GetTimestamp();
                    var audio = await voice.SynthesizeAsync(WorldReference.ReviewText).ConfigureAwait(false);
                    var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    (useCache ? cachedMs : uncachedMs)[trial] = elapsed;
                    check(audio.SampleRate == input.SampleRate && audio.Samples.SequenceEqual(input.Samples),
                        $"prepared reference source inspection cache {(useCache ? "on" : "off")} trial {trial + 1} preserves every PCM sample");
                }
            }
            Array.Sort(cachedMs); Array.Sort(uncachedMs);
            Console.WriteLine($"UTAU_SOURCE_INSPECTION scope=prepared-reference-render-only warmupEach=1 pairs=3 order=off-on,on-off,off-on samples={input.Samples.Length} cacheOnMedianMs={cachedMs[1]:F2} cacheOffMedianMs={uncachedMs[1]:F2} cacheOnAnalyses={cached.SourceInspectionAnalysisCount - cachedAnalyses} cacheOnHits={cached.SourceInspectionCacheHitCount - cachedHits} cacheOffAnalyses={uncached.SourceInspectionAnalysisCount - uncachedAnalyses} cacheOffHits={uncached.SourceInspectionCacheHitCount - uncachedHits}");
        }
        CheckF0Only(library, input, check);
        CheckFastF0Only(library, input, check);
        CheckParallelAnalysis(library, input, check);
    }

    private static void RunResourceAnalysis(string library, PcmAudio input, string mode)
    {
        if (mode is not ("serial" or "parallel")) throw new InvalidDataException("WORLD resource mode");
        if (input.SampleRate != 44100 || input.Samples.Length != 285281) throw new InvalidDataException("WORLD resource reference geometry changed");
        using var native = new WorldComparison.NativeApi(library);
        var analyze = mode == "serial" ? native.Analyze : native.AnalyzeParallel ?? throw new InvalidDataException("WORLD parallel export absent");
        var samples = input.Samples.Select(value => value / 32768.0).ToArray();
        if (native.Layout(samples.Length, out var frames, out var fft) != 0) throw new InvalidDataException("WORLD resource layout");
        var cells = checked(frames * (fft / 2 + 1));
        var f0 = new double[frames]; var times = new double[frames]; var sp = new double[cells]; var ap = new double[cells];
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var beforePrivate = process.PrivateMemorySize64; var beforeWorking = process.WorkingSet64; var beforePeak = process.PeakWorkingSet64;
        var started = Stopwatch.GetTimestamp();
        var status = analyze(samples, samples.Length, f0, frames, times, frames, sp, cells, ap, cells, fft);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        process.Refresh();
        var afterPrivate = process.PrivateMemorySize64; var afterWorking = process.WorkingSet64; var afterPeak = process.PeakWorkingSet64;
        if (status != 0 || new[] { f0, times, sp, ap }.Any(values => values.Any(value => !double.IsFinite(value))))
            throw new InvalidDataException("WORLD resource analysis failed");
        using var hash = System.Security.Cryptography.IncrementalHash.CreateHash(System.Security.Cryptography.HashAlgorithmName.SHA256);
        foreach (var values in new[] { f0, times, sp, ap })
            hash.AppendData(System.Runtime.InteropServices.MemoryMarshal.AsBytes(values.AsSpan()));
        var digest = Convert.ToHexString(hash.GetHashAndReset());
        Console.WriteLine($"WORLD_RESOURCE mode={mode} samples={samples.Length} frames={frames} fft={fft} trials=1 analysisMs={elapsed:F2} privateBefore={beforePrivate} privateAfter={afterPrivate} workingBefore={beforeWorking} workingAfter={afterWorking} peakWsBefore={beforePeak} peakWsAfter={afterPeak} managedResultBytes={checked(16L * (frames + cells))} digest={digest} memoryScope=fresh-process-highwater-includes-reference-preparation");
    }

    private static void CheckFastF0Only(string library, PcmAudio reference, Action<bool, string> check)
    {
        using var native = new WorldComparison.NativeApi(library);
        using var fallback = new WorldComparison.NativeApi(library, disableOptionalF0ForComparison: true);
        check(fallback.AnalyzeF0 is null && fallback.AnalyzeFastF0 is null, "optional F0 disable preserves both Harvest and DIO full fallbacks");
        if (native.AnalyzeFastF0 is not { } analyze)
        { Console.WriteLine("WORLD_DIO_F0_ONLY optionalExport=absent"); return; }
        var samples = reference.Samples.Select(value => value / 32768.0).ToArray();
        if (native.Layout(samples.Length, out var frames, out var fft) != 0) throw new InvalidDataException("DIO F0 audit layout");
        var cells = checked(frames * (fft / 2 + 1));
        var f0 = new double[frames]; var times = new double[frames]; var sp = new double[cells]; var ap = new double[cells];
        var onlyF0 = new double[frames]; var onlyTimes = new double[frames];
        var full = native.AnalyzeFastParallel ?? native.AnalyzeFast;
        var started = Stopwatch.GetTimestamp();
        var status = full(samples, samples.Length, f0, frames, times, frames, sp, cells, ap, cells, fft);
        var fullMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        var onlyStatus = analyze(samples, samples.Length, onlyF0, frames, onlyTimes, frames);
        var onlyMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var equal = status == 0 && onlyStatus == 0 && Bits(f0, onlyF0) && Bits(times, onlyTimes);
        check(equal, "DIO F0-only preserves all F0/time bits of adopted full analysis");
        if (!equal) throw new InvalidDataException("DIO F0-only numerical mismatch");
        check(BitConverter.DoubleToInt64Bits(Median(f0)) == BitConverter.DoubleToInt64Bits(Median(onlyF0)),
            "DIO F0-only preserves voiced median exactly");
        Console.WriteLine($"WORLD_DIO_F0_ONLY scope=bank-reference trials=1 order=full-then-f0 samples={samples.Length} fullMs={fullMs:F2} f0OnlyMs={onlyMs:F2} scopeLimit=calibration-not-ordinary-speech");
        var bytes = new byte[reference.Samples.Length * 2];
        for (var i = 0; i < reference.Samples.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), reference.Samples[i]);
        using var optimized = new MemoryStream(); using var old = new MemoryStream();
        var request = new WorldPcmProtocol.Request(reference.Samples.Length, 0, bytes);
        WorldPcmWorker.Process(request, optimized, native, fast: true);
        WorldPcmWorker.Process(request, old, fallback, fast: true);
        check(optimized.ToArray().SequenceEqual(old.ToArray()), "DIO calibration response matches full fallback byte-for-byte");
        var silence = new double[WorldPcmProtocol.MinimumSamples];
        native.Layout(silence.Length, out var silentFrames, out _);
        var sf0 = new double[silentFrames]; var st = new double[silentFrames];
        check(analyze(silence, silence.Length, sf0, silentFrames, st, silentFrames) == 0 && sf0.All(value => value == 0),
            "DIO F0-only preserves all-unvoiced minimum input");
        using var silent = new MemoryStream(); using var silentFallback = new MemoryStream();
        var silentRequest = new WorldPcmProtocol.Request(silence.Length, 0, new byte[silence.Length * 2]);
        WorldPcmWorker.Process(silentRequest, silent, native, fast: true);
        WorldPcmWorker.Process(silentRequest, silentFallback, fallback, fast: true);
        using var expectedSilence = new MemoryStream();
        WorldPcmProtocol.WriteResponse(expectedSilence, WorldPcmProtocol.InsufficientVoicing, 0, []);
        check(silent.ToArray().SequenceEqual(silentFallback.ToArray()) && silent.ToArray().SequenceEqual(expectedSilence.ToArray()),
            "DIO insufficient voicing calibration response matches full fallback and expected terminal status");
        check(analyze(silence, silence.Length - 1, sf0, silentFrames, st, silentFrames) == 2 &&
            analyze(silence, silence.Length, sf0, silentFrames - 1, st, silentFrames) == 2,
            "DIO F0-only rejects minimum length and frame geometry before analysis");
        check(analyze(silence, silence.Length, sf0, silentFrames, sf0, silentFrames) == 1 &&
            analyze(silence, silence.Length, silence, silentFrames, st, silentFrames) == 1,
            "DIO F0-only rejects output/output and input/output alias with valid allocated arrays");
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, 1.001 })
        {
            silence[0] = invalid;
            check(analyze(silence, silence.Length, sf0, silentFrames, st, silentFrames) == 3,
                "DIO F0-only rejects nonfinite/out-of-range input");
        }
        static bool Bits(double[] a, double[] b) => a.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.Select(BitConverter.DoubleToInt64Bits));
        static double Median(double[] values)
        {
            var voiced = values.Where(value => value > 0).Order().ToArray();
            return voiced.Length == 0 ? 0 : (voiced[(voiced.Length - 1) / 2] + voiced[voiced.Length / 2]) / 2;
        }
    }

    private static void CheckParallelAnalysis(string library, PcmAudio reference, Action<bool, string> check)
    {
        using var native = new WorldComparison.NativeApi(library);
        var tone = new PcmAudio(Enumerable.Range(0, 44100).Select(i => (short)(10000 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray(), 44100);
        foreach (var mode in new[] { "harvest", "dio" })
        {
            var candidate = mode == "harvest" ? native.AnalyzeParallel : native.AnalyzeFastParallel;
            if (candidate is null) { Console.WriteLine($"WORLD_PARALLEL mode={mode} optionalExport=absent"); continue; }
            var serial = mode == "harvest" ? native.Analyze : native.AnalyzeFast;
            foreach (var item in new[] { (Name: "tone", Audio: tone), (Name: "bank-reference", Audio: reference) })
            {
                var samples = item.Audio.Samples.Select(value => value / 32768.0).ToArray();
                if (native.Layout(samples.Length, out var frames, out var fft) != 0) throw new InvalidDataException("WORLD parallel layout failed");
                var cells = checked(frames * (fft / 2 + 1));
                var f0 = new double[frames]; var times = new double[frames]; var sp = new double[cells]; var ap = new double[cells];
                var pf0 = new double[frames]; var ptimes = new double[frames]; var psp = new double[cells]; var pap = new double[cells];
                using var process = Process.GetCurrentProcess(); process.Refresh();
                var beforePrivate = process.PrivateMemorySize64; var beforePeak = process.PeakWorkingSet64;
                var started = Stopwatch.GetTimestamp();
                var status = serial(samples, samples.Length, f0, frames, times, frames, sp, cells, ap, cells, fft);
                var serialMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                started = Stopwatch.GetTimestamp();
                var parallelStatus = candidate(samples, samples.Length, pf0, frames, ptimes, frames, psp, cells, pap, cells, fft);
                var parallelMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var identical = status == 0 && parallelStatus == 0 && Bits(f0, pf0) && Bits(times, ptimes) && Bits(sp, psp) && Bits(ap, pap);
                check(identical, $"WORLD parallel {mode}/{item.Name} preserves every F0/time/SP/AP bit");
                if (!identical) throw new InvalidDataException("WORLD parallel analysis mismatch");
                var output = new double[samples.Length]; var parallelOutput = new double[samples.Length];
                var a = native.Synthesize(f0, frames, sp, cells, ap, cells, fft, output, output.Length);
                var b = native.Synthesize(pf0, frames, psp, cells, pap, cells, fft, parallelOutput, parallelOutput.Length);
                var pcmEqual = a == 0 && b == 0 && Bits(output, parallelOutput) && Pcm(output).SequenceEqual(Pcm(parallelOutput));
                check(pcmEqual, $"WORLD parallel {mode}/{item.Name} preserves full synthesis and final PCM");
                if (!pcmEqual) throw new InvalidDataException("WORLD parallel PCM mismatch");
                process.Refresh();
                // Peak is process-lifetime high water, not an isolated native
                // allocation delta. Both result sets are deliberately live here.
                Console.WriteLine($"WORLD_PARALLEL mode={mode} scope={item.Name} trials=1 order=serial-parallel samples={samples.Length} serialMs={serialMs:F2} parallelMs={parallelMs:F2} privateBefore={beforePrivate} privateAfter={process.PrivateMemorySize64} peakWsBefore={beforePeak} peakWsAfter={process.PeakWorkingSet64} managedResultBytes={checked(8L * (4L * frames + 4L * cells + 2L * samples.Length))} memoryScope=process-highwater-not-native-delta");
                if (item.Name == "tone")
                {
                    check(candidate(samples, samples.Length, pf0, frames - 1, ptimes, frames, psp, cells, pap, cells, fft) != 0,
                        "parallel analysis rejects inconsistent output geometry: " + mode);
                    check(candidate(samples, samples.Length, pf0, frames, ptimes, frames, psp, cells, psp, cells, fft) != 0,
                        "parallel analysis rejects aliased SP/AP with adequately allocated buffers: " + mode);
                    check(candidate(samples, samples.Length, samples, frames, ptimes, frames, psp, cells, pap, cells, fft) != 0 &&
                        candidate(samples, samples.Length, pf0, frames, pf0, frames, psp, cells, pap, cells, fft) != 0,
                        "parallel analysis rejects input/F0 and F0/time aliases in valid allocated buffers: " + mode);
                    samples[0] = double.NaN;
                    check(candidate(samples, samples.Length, pf0, frames, ptimes, frames, psp, cells, pap, cells, fft) != 0,
                        "parallel analysis rejects nonfinite input: " + mode);
                }
            }
        }
        static bool Bits(double[] a, double[] b) => a.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(b.Select(BitConverter.DoubleToInt64Bits));
        static short[] Pcm(double[] values)
        {
            var peak = values.Max(Math.Abs); var gain = peak > 0 ? Math.Min(1.0, 0.92 / peak) : 1.0;
            return values.Select(value => (short)Math.Clamp((int)Math.Round(value * gain * 32767), short.MinValue, short.MaxValue)).ToArray();
        }
    }

    // This explicit native-test runner is an isolated, opt-in executable.
    // Invalid cases use real, adequately sized arrays, never bogus pointers.
    private static void CheckF0Only(string library, PcmAudio reference, Action<bool, string> check)
    {
        using var native = new WorldComparison.NativeApi(library);
        using var fallback = new WorldComparison.NativeApi(library, disableOptionalF0ForComparison: true);
        check(fallback.AnalyzeF0 is null, "WORLD optional-export absence can retain the full analysis path");
        var tone = new PcmAudio(Enumerable.Range(0, 44100).Select(i => (short)(10000 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray(), 44100);
        foreach (var item in new[] { (Name: "tone", Audio: tone), (Name: "bank-reference", Audio: reference) })
        {
            var samples = item.Audio.Samples.Select(value => value / 32768.0).ToArray();
            if (native.Layout(samples.Length, out var frames, out var fft) != 0) throw new InvalidDataException("WORLD audit layout failed.");
            var cells = checked(frames * (fft / 2 + 1));
            var fullF0 = new double[frames]; var fullTimes = new double[frames];
            var spectrum = new double[cells]; var ap = new double[cells];
            var started = Stopwatch.GetTimestamp();
            var fullStatus = native.Analyze(samples, samples.Length, fullF0, frames, fullTimes, frames, spectrum, cells, ap, cells, fft);
            var fullMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            check(fullStatus == 0, "WORLD full analysis remains available for " + item.Name);
            if (native.AnalyzeF0 is { } analyzeF0)
            {
                var f0 = new double[frames]; var times = new double[frames];
                started = Stopwatch.GetTimestamp();
                var status = analyzeF0(samples, samples.Length, f0, frames, times, frames);
                var f0Ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                check(status == 0 && f0.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(fullF0.Select(BitConverter.DoubleToInt64Bits)) &&
                    times.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(fullTimes.Select(BitConverter.DoubleToInt64Bits)),
                    "optional WORLD F0/time arrays are bit-identical to full Harvest for " + item.Name);
                check(BitConverter.DoubleToInt64Bits(Median(f0)) == BitConverter.DoubleToInt64Bits(Median(fullF0)),
                    "optional WORLD median is bit-identical for " + item.Name);
                Console.WriteLine($"WORLD_F0_ONLY scope={item.Name} trials=1 order=full-then-f0 samples={samples.Length} fullMs={fullMs:F2} f0OnlyMs={f0Ms:F2}");
            }
            else Console.WriteLine($"WORLD_F0_ONLY scope={item.Name} optionalExport=absent fullMs={fullMs:F2}");
            if (item.Name == "bank-reference")
            {
                // Runs only inside the existing bounded, isolated F0 audit.
                // Use actual voiced speech: Harvest can classify a pure sine
                // as unvoiced, which intentionally cannot enter Context.
                check(fullF0.Count(value => value > 0) >= 10,
                    "WORLD buffer-reuse comparison has sufficient detected speech voicing for Context");
                // The reused array holds actual analyzed input on the first
                // pass and the prior synthesis output on subsequent passes.
                var reused = samples;
                foreach (var mode in new[] { "flat", "context", "unvoiced" })
                {
                    var track = (double[])fullF0.Clone();
                    if (mode == "unvoiced") Array.Clear(track);
                    else if (mode == "flat")
                    {
                        for (var i = 0; i < track.Length; i++) if (track[i] > 0) track[i] = 220;
                    }
                    else
                    {
                        var contour = Enumerable.Range(0, track.Length).Select(i => 200.0 + 10 * Math.Sin(i * 0.05)).ToArray();
                        WorldPcmWorker.ApplyContextContour(track, contour, reused.Length, 220);
                    }
                    var fresh = new double[reused.Length];
                    var freshStatus = native.Synthesize(track, frames, spectrum, cells, ap, cells, fft, fresh, fresh.Length);
                    var reusedStatus = native.Synthesize(track, frames, spectrum, cells, ap, cells, fft, reused, reused.Length);
                    check(freshStatus == 0 && reusedStatus == 0 &&
                        fresh.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(reused.Select(BitConverter.DoubleToInt64Bits)),
                        "WORLD fresh and used-input synthesis buffers preserve every output double for " + mode);
                    var freshGain = Math.Min(1.0, 0.92 / fresh.Max(Math.Abs));
                    var reusedGain = Math.Min(1.0, 0.92 / reused.Max(Math.Abs));
                    var freshPcm = fresh.Select(value => (short)Math.Clamp((int)Math.Round(value * freshGain * 32767), short.MinValue, short.MaxValue)).ToArray();
                    var reusedPcm = reused.Select(value => (short)Math.Clamp((int)Math.Round(value * reusedGain * 32767), short.MinValue, short.MaxValue)).ToArray();
                    check(freshPcm.SequenceEqual(reusedPcm),
                        "WORLD worker quantization keeps exact full PCM with a reused synthesis buffer for " + mode);
                }
                var beforeFailure = (double[])reused.Clone();
                var invalidTrack = (double[])fullF0.Clone(); invalidTrack[0] = double.NaN;
                var failedStatus = native.Synthesize(invalidTrack, frames, spectrum, cells, ap, cells, fft, reused, reused.Length);
                check(failedStatus != 0 &&
                    reused.Select(BitConverter.DoubleToInt64Bits).SequenceEqual(beforeFailure.Select(BitConverter.DoubleToInt64Bits)),
                    "WORLD invalid synthesis is rejected before modifying the used-input output buffer; a nonzero status supplies no PCM");
            }
        }
        var silentPcm = new byte[WorldPcmProtocol.MinimumSamples * 2];
        using (var silentResponse = new MemoryStream())
        using (var silentExpected = new MemoryStream())
        {
            WorldPcmWorker.Process(new(WorldPcmProtocol.MinimumSamples, 220, silentPcm), silentResponse, native, fast: false);
            WorldPcmProtocol.WriteResponse(silentExpected, WorldPcmProtocol.Success, 0, silentPcm);
            check(silentResponse.ToArray().SequenceEqual(silentExpected.ToArray()) && silentPcm.All(value => value == 0),
                "WORLD wholly unvoiced worker passthrough preserves exact request PCM without reusing the caller-owned byte buffer");
        }
        var request = new WorldPcmProtocol.Request(reference.Samples.Length, 0, PcmBytes(reference));
        using var optimizedResponse = new MemoryStream(); using var oldResponse = new MemoryStream();
        WorldPcmWorker.Process(request, optimizedResponse, native, fast: false);
        WorldPcmWorker.Process(request, oldResponse, fallback, fast: false);
        check(optimizedResponse.ToArray().SequenceEqual(oldResponse.ToArray()),
            "calibration response status/median/empty PCM match the missing-export full fallback byte-for-byte");
        if (native.AnalyzeF0 is not { } analyze) return; // Old audited DLL remains a supported test input.
        var silence = new double[4410]; native.Layout(silence.Length, out var minimumFrames, out _);
        var first = new double[minimumFrames]; var second = new double[minimumFrames];
        check(analyze(silence, silence.Length, first, first.Length, second, second.Length) == 0 && first.All(value => value == 0),
            "F0-only minimum-length silence retains an all-unvoiced F0 result");
        check(analyze(silence, silence.Length, first, first.Length - 1, second, second.Length) == 2 &&
            analyze(silence, silence.Length - 1, first, first.Length, second, second.Length) == 2,
            "F0-only rejects short input and inconsistent frame counts before native analysis");
        check(analyze(silence, silence.Length, first, first.Length, first, first.Length) == 1 &&
            analyze(silence, silence.Length, silence, first.Length, second, second.Length) == 1,
            "F0-only rejects output/output and input/output aliases using valid allocated buffers");
        foreach (var value in new[] { double.NaN, double.PositiveInfinity, 1.001 })
        {
            silence[0] = value;
            check(analyze(silence, silence.Length, first, first.Length, second, second.Length) == 3,
                "F0-only rejects nonfinite or out-of-range samples before Harvest");
        }
        var maximum = new double[44100 * 45];
        check(native.Layout(maximum.Length, out var maxFrames, out _) == 0 && maxFrames == 9001 &&
            analyze(maximum, maximum.Length, first, first.Length, second, second.Length) == 2,
            "F0-only validates maximum-input geometry before using mismatched output buffers");
        static byte[] PcmBytes(PcmAudio audio)
        {
            var bytes = new byte[audio.Samples.Length * 2];
            for (var i = 0; i < audio.Samples.Length; i++) System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2, 2), audio.Samples[i]);
            return bytes;
        }
        static double Median(double[] f0)
        {
            var voiced = f0.Where(value => value > 0).Order().ToArray();
            return voiced.Length == 0 ? 0 : (voiced[(voiced.Length - 1) / 2] + voiced[voiced.Length / 2]) / 2;
        }
    }

    private static int Peak(PcmAudio audio) => audio.Samples.Max(value => Math.Abs((int)value));

    private static double EstimateVowelPitch(PcmAudio audio)
    {
        const int rate = 44100;
        var samples = audio.Samples.AsSpan(rate * 150 / 1000, rate * 160 / 1000);
        var minimumLag = rate / 700;
        var maximumLag = rate / 70;
        var correlations = new double[maximumLag + 2];
        var maximum = 0.0;
        for (var lag = minimumLag - 1; lag <= maximumLag + 1; ++lag)
        {
            double dot = 0, left = 0, right = 0, sumLeft = 0, sumRight = 0;
            var count = 0;
            for (var i = 0; i + lag < samples.Length; i += 3)
            {
                var a = (double)samples[i]; var b = samples[i + lag];
                dot += a * b; left += a * a; right += (double)b * b;
                sumLeft += a; sumRight += b; ++count;
            }
            dot -= sumLeft * sumRight / count;
            left -= sumLeft * sumLeft / count; right -= sumRight * sumRight / count;
            correlations[lag] = dot / Math.Sqrt(Math.Max(1e-20, left * right));
            maximum = Math.Max(maximum, correlations[lag]);
        }
        for (var lag = minimumLag; lag <= maximumLag; ++lag)
        {
            var value = correlations[lag];
            if (value < Math.Max(0.8, maximum * 0.92) || value < correlations[lag - 1] || value < correlations[lag + 1]) continue;
            var curvature = correlations[lag - 1] - 2 * value + correlations[lag + 1];
            var adjustment = Math.Abs(curvature) < 1e-10 ? 0 : 0.5 * (correlations[lag - 1] - correlations[lag + 1]) / curvature;
            return rate / (lag + Math.Clamp(adjustment, -0.5, 0.5));
        }
        return double.NaN;
    }
}
