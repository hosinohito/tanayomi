using Tanayomi;
using Tanayomi.Desktop;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

internal static class NeuralOpenVoiceSessionTests
{
    internal static async Task RunReferenceSnapshotAuditAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var banks = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath);
        if (banks.Count != 1 || banks[0].Name != "hosinohito") throw new InvalidDataException("Neural reference fixture mismatch.");
        var cache = new NeuralReferenceCache();
        var coldAllocated = GC.GetTotalAllocatedBytes(precise: true); var coldStarted = Stopwatch.GetTimestamp();
        var warmed = await NeuralVoiceReferences.ReadAsync(banks[0], temporary.DirectoryPath, default, cache);
        var coldReadMs = Stopwatch.GetElapsedTime(coldStarted).TotalMilliseconds;
        coldAllocated = GC.GetTotalAllocatedBytes(precise: true) - coldAllocated;
        var referencePaths = banks[0].Entries.Select(entry => Path.GetFullPath(entry.WavePath))
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        for (var index = 0; index < referencePaths.Length; index++)
        {
            var decoded = UtauSpeech.DecodeWave(await File.ReadAllBytesAsync(referencePaths[index]), default);
            var gain = Math.Min(1, 0.98f / decoded.Max(value => Math.Abs(value)));
            check(warmed.Clips[index].Samples.SequenceEqual(decoded.Select(value => (short)Math.Round(value * gain * 32767))),
                "bundled reference PCM preserves original scalar rounding and reference level policy");
        }
        await using var owned = new DesktopNeuralUtau();
        await using var external = new DesktopNeuralUtau((_, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(), (_, _, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());
        // Inspect the private boundary without adding a caller-selectable
        // ownership switch to the product's provider API.
        var method = typeof(DesktopNeuralUtau).GetMethod("SnapshotReferences",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new MissingMethodException("SnapshotReferences");
        var clone = method.CreateDelegate<Func<IReadOnlyList<PcmAudio>, CancellationToken, IReadOnlyList<PcmAudio>>>(external);
        var transfer = method.CreateDelegate<Func<IReadOnlyList<PcmAudio>, CancellationToken, IReadOnlyList<PcmAudio>>>(owned);
        _ = clone(warmed.Clips, default); _ = transfer(warmed.Clips, default);
        _ = await NeuralVoiceReferences.ReadAsync(banks[0], temporary.DirectoryPath, default, cache, readConcurrency: 2);
        var previous = await MeasureAsync(clone);
        var current = await MeasureAsync(transfer, diagnostics: true);
        var parallel = await MeasureAsync(transfer, readConcurrency: 2);
        var sampleCount = current.Clips.Sum(clip => (long)clip.Samples.Length);
        check(current.Clips.Count == 149 && previous.Clips.Count == 149,
            "reference snapshot audit uses all 149 bundled WAVs after a warm cache read");
        check(previous.ContentHash == current.ContentHash && previous.Clips.Zip(current.Clips).All(pair =>
            pair.First.SampleRate == pair.Second.SampleRate && pair.First.Samples.SequenceEqual(pair.Second.Samples)),
            "owned and cloned reference snapshots preserve every PCM sample and ordered boundary");
        check(previous.AllocatedBytes - current.AllocatedBytes >= sampleCount * sizeof(short),
            "owned reference snapshots remove one complete PCM allocation without skipping validation");
        check(current.Clips.Zip(current.Selected).All(pair => ReferenceEquals(pair.First.Samples, pair.Second.Samples)) &&
            previous.Clips.Zip(previous.Selected).All(pair => !ReferenceEquals(pair.First.Samples, pair.Second.Samples)),
            "only the internal-owned snapshot transfers arrays while external snapshots remain isolated");
        var uncached = await NeuralVoiceReferences.ReadAsync(banks[0], temporary.DirectoryPath, default);
        check(uncached.ContentSha256 == current.ContentHash && uncached.Clips.Count == current.Clips.Count &&
            uncached.Clips.Zip(current.Clips).All(pair => pair.First.SampleRate == pair.Second.SampleRate &&
                pair.First.Samples.SequenceEqual(pair.Second.Samples)),
            "bounded raw-buffer reuse matches exact-length uncached decoding for every bundled reference");
        check(previous.Diagnostics is null && current.Diagnostics is { FileCount: 149, CacheHits: 149, CacheMisses: 0 } &&
            current.Diagnostics.RawBytes >= sampleCount * sizeof(short),
            "read-stage diagnostics are opt-in and account for every warm reference hit without recording source paths");
        check(parallel.Diagnostics is null && parallel.ContentHash == current.ContentHash && parallel.Clips.Count == current.Clips.Count &&
            parallel.Clips.Zip(current.Clips).All(pair => pair.First.SampleRate == pair.Second.SampleRate &&
                pair.First.Samples.SequenceEqual(pair.Second.Samples)),
            "bounded two-slot raw prefetch preserves every ordered reference PCM sample and content hash");
        var rendererCache = NeuralPcmWorkerTests.GetRendererReferenceCache(owned);
        var rendererCold = await NeuralVoiceReferences.ReadAsync(banks[0], temporary.DirectoryPath, default, rendererCache, readConcurrency: 2);
        var rendererWarm = await MeasureAsync(transfer, readConcurrency: 2, readCache: rendererCache);
        check(rendererWarm.ContentHash == parallel.ContentHash && rendererCold.ContentSha256 == parallel.ContentHash &&
            rendererWarm.Clips.Count == parallel.Clips.Count && rendererCold.Clips.Count == parallel.Clips.Count &&
            rendererWarm.Clips.Zip(parallel.Clips).All(pair => pair.First.SampleRate == pair.Second.SampleRate &&
                pair.First.Samples.SequenceEqual(pair.Second.Samples)) &&
            rendererWarm.Clips.Zip(rendererCold.Clips).All(pair => ReferenceEquals(pair.First.Samples, pair.Second.Samples)) &&
            NeuralOpenVoiceSession.FingerprintReferences(rendererWarm.Clips).AsSpan().SequenceEqual(
                NeuralOpenVoiceSession.FingerprintReferences(parallel.Clips)),
            "renderer cache borrowing preserves all bundled PCM, ordered boundaries and both reference hashes");
        check(parallel.ReadAllocatedBytes - rendererWarm.ReadAllocatedBytes >= sampleCount * sizeof(short),
            "warm renderer cache borrowing avoids allocating a copy of the bundled reference PCM");
        Console.WriteLine("NEURAL_REFERENCE_RENDERER_CACHE " + JsonSerializer.Serialize(new
        {
            scope = "bundled149-two-slot-warm-one-pair", samples = sampleCount,
            cloned = new { parallel.ReadMilliseconds, parallel.ReadAllocatedBytes },
            borrowed = new { rendererWarm.ReadMilliseconds, rendererWarm.ReadAllocatedBytes }
        }));
        Console.WriteLine("NEURAL_REFERENCE_SNAPSHOT " + JsonSerializer.Serialize(new
        {
            scope = "bundled149-warm-cache-one-pair", samples = sampleCount, pcmBytes = sampleCount * sizeof(short),
            coldRead = new { Milliseconds = coldReadMs, AllocatedBytes = coldAllocated },
            previous = new { previous.ReadMilliseconds, previous.ReadAllocatedBytes, previous.SnapshotMilliseconds, previous.AllocatedBytes },
            current = new { current.ReadMilliseconds, current.ReadAllocatedBytes, current.SnapshotMilliseconds, current.AllocatedBytes,
                ReadStages = current.Diagnostics }
        }));
        Console.WriteLine("NEURAL_REFERENCE_PREFETCH " + JsonSerializer.Serialize(new
        {
            scope = "bundled149-warm-cache-one-measurement-each-diagnostics-off", processors = Environment.ProcessorCount,
            serial = new { previous.ReadMilliseconds, previous.ReadAllocatedBytes },
            twoSlots = new { parallel.ReadMilliseconds, parallel.ReadAllocatedBytes }
        }));
        var source = new PcmAudio(Enumerable.Range(0, 4800).Select(i => (short)(i % 200 - 100)).ToArray(), 48000);
        var sourceAndReferences = current.Clips.Prepend(source).ToArray();
        var verified = new NeuralReferenceSnapshot(current.Clips, current.ContentHash);
        var memo = new NeuralReferenceFingerprintMemo();
        var referenceFingerprint = memo.Get(verified, default);
        _ = NeuralOpenVoiceSession.FingerprintReferences(sourceAndReferences);
        var expectedKey = DesktopNeuralUtau.CreateConversionKey(source, referenceFingerprint);
        var oldStarted = Stopwatch.GetTimestamp();
        _ = NeuralOpenVoiceSession.FingerprintReferences(sourceAndReferences);
        var oldMilliseconds = Stopwatch.GetElapsedTime(oldStarted).TotalMilliseconds;
        var newStarted = Stopwatch.GetTimestamp();
        var actualKey = DesktopNeuralUtau.CreateConversionKey(source, memo.Get(verified, default));
        var newMilliseconds = Stopwatch.GetElapsedTime(newStarted).TotalMilliseconds;
        check(memo.LastHit && actualKey.AsSpan().SequenceEqual(expectedKey) &&
            referenceFingerprint.AsSpan().SequenceEqual(NeuralOpenVoiceSession.FingerprintReferences(current.Clips)),
            "verified-content memo preserves the reference fingerprint and stable source/reference conversion identity");
        Console.WriteLine("NEURAL_REFERENCE_KEY " + JsonSerializer.Serialize(new
        {
            scope = "bundled149-one-pair-after-raw-content-validation", sourceSamples = source.Samples.Length,
            referenceSamples = sampleCount, fullPcmHashMilliseconds = oldMilliseconds, memoKeyMilliseconds = newMilliseconds
        }));
        memo.Clear();
        cache.Clear();

        async Task<(IReadOnlyList<PcmAudio> Clips, IReadOnlyList<PcmAudio> Selected, string ContentHash,
            double ReadMilliseconds, long ReadAllocatedBytes, double SnapshotMilliseconds, long AllocatedBytes,
            NeuralReferenceReadDiagnostics? Diagnostics)> MeasureAsync(
            Func<IReadOnlyList<PcmAudio>, CancellationToken, IReadOnlyList<PcmAudio>> snapshot, bool diagnostics = false,
            int readConcurrency = 1, NeuralReferenceCache? readCache = null)
        {
            var readAllocated = GC.GetTotalAllocatedBytes(precise: true); var started = Stopwatch.GetTimestamp();
            var selected = await NeuralVoiceReferences.ReadAsync(banks[0], temporary.DirectoryPath, default, readCache ?? cache, diagnostics, readConcurrency);
            var readMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            readAllocated = GC.GetTotalAllocatedBytes(precise: true) - readAllocated;
            var allocated = GC.GetAllocatedBytesForCurrentThread(); started = Stopwatch.GetTimestamp();
            var clips = snapshot(selected.Clips, default);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            return (clips, selected.Clips, selected.ContentSha256, readMs, readAllocated, elapsed, allocated, selected.Diagnostics);
        }
    }

    // The default follows the approved product runtime. The prior audited DLL
    // remains available only through an explicit isolated comparison option.
    private const int PreviousRuntimeNativeBytes = 10116096;
    private const string PreviousRuntimeNativeSha256 = "F6994D068BC520FD74BA2AEDCE0A55DB4192D54D4764B14D5D62A23DF95E56E4";
    // Opt-in, isolated-process check. The ordinary Run method stays native-free.
    // The caller owns a finite process deadline and catches top-level errors.
    internal static void RunNative(string assetRoot, string fixturePath, Action<bool, string> check, bool profile = false,
        bool previousRuntime = false, bool runtimeBenchmark = false, bool runtimeLifetime = false, bool runtimeCancel = false)
    {
        if (profile && runtimeBenchmark) throw new ArgumentException("Runtime benchmark excludes profiling.");
        if (runtimeLifetime && (profile || runtimeBenchmark)) throw new ArgumentException("Runtime lifetime excludes benchmarking and profiling.");
        if (runtimeCancel && (profile || runtimeBenchmark || runtimeLifetime)) throw new ArgumentException("Runtime cancellation excludes other runtime scopes and profiling.");
        var runtimeBytes = previousRuntime ? PreviousRuntimeNativeBytes : NeuralOpenVoiceAssets.ApprovedNativeBytes;
        var runtimeHash = previousRuntime ? PreviousRuntimeNativeSha256 : NeuralOpenVoiceAssets.ApprovedNativeSha256;
        if (runtimeBytes <= 0 || runtimeHash.Length != 64 || !runtimeHash.All(Uri.IsHexDigit))
            throw new InvalidDataException("Selected neural runtime pin is invalid.");
        if (!profile && !runtimeBenchmark && !runtimeLifetime && !runtimeCancel) NeuralSpectrogramTests.MeasureResample(check);
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Neural numeric audit requires Windows x64.");
        assetRoot = Path.GetFullPath(assetRoot); fixturePath = Path.GetFullPath(fixturePath);
        WorldComparison.RejectLinks(assetRoot); WorldComparison.RejectLinks(fixturePath);
        var fixtureBytes = File.ReadAllBytes(fixturePath);
        if (fixtureBytes.Length != 320116 || Convert.ToHexString(SHA256.HashData(fixtureBytes)) !=
            "FCD7FD6D2D9BB36AEF2AABC3EDBFA1F74602C8DE193D365C557A84D5A205211E")
            throw new InvalidDataException("Neural numeric fixture hash mismatch.");
        using var fixture = JsonDocument.Parse(fixtureBytes);
        var json = fixture.RootElement;
        if (json.GetProperty("schema").GetInt32() != 1 || json.GetProperty("sampleRate").GetInt32() != 22050 ||
            json.GetProperty("frames").GetInt32() != 32) throw new InvalidDataException("Neural numeric fixture schema mismatch.");
        var source = Shorts("sourcePcm", 8192); var target = Shorts("referencePcm", 8192);
        var sourceSpectrum = Floats("sourceSpectrum", 513 * 32); var targetSpectrum = Floats("referenceSpectrum", 513 * 32);
        var sourceEmbedding = Floats("sourceEmbedding", 256); var targetEmbedding = Floats("targetEmbedding", 256);
        var noise = Floats("noise", 192 * 32); var modelPcm = Floats("modelPcm", 8192); var finalPcm = Shorts("finalPcm", 8192);
        Near(NeuralSpectrogram.Create(NeuralSpectrogram.Resample(source, 22050)).Magnitudes, sourceSpectrum, 0.00005,
            "managed source STFT versus independent PyTorch STFT");
        Near(NeuralSpectrogram.Create(NeuralSpectrogram.Resample(target, 22050)).Magnitudes, targetSpectrum, 0.00005,
            "managed reference STFT versus independent PyTorch STFT");
        Near(NeuralOpenVoiceSession.CreateNoise(noise.Length), noise, 0.0000001, "managed explicit noise versus independent Python oracle");
        var pins = new List<FileStream>();
        var modelReadMs = 0.0; var modelHashMs = 0.0; var runtimeReadMs = 0.0; var runtimeHashMs = 0.0;
        try
        {
            var referenceModel = ReadPinned("reference.onnx", NeuralOpenVoiceSession.ReferenceBytes, NeuralOpenVoiceSession.ReferenceSha256);
            var converterModel = ReadPinned("converter.onnx", NeuralOpenVoiceSession.ConverterBytes, NeuralOpenVoiceSession.ConverterSha256);
            ReadPinned("onnxruntime.dll", runtimeBytes, runtimeHash);
            ReadPinned("msvcp140.dll", 565648, "43A9666C2F405258F165B53082860AC7838039A75FD2E85286085EDE0DA0273B");
            ReadPinned("vcruntime140.dll", 97160, "C865C3366A98431EC3A5959CB5AC3966081A43B82DFCD8BFEFAFE0146B1508DB");
            ReadPinned("vcruntime140_1.dll", 37256, "6C1E531C25AB2934A4EA9970598BC751D924D7CC5650DF3E1282B61D6CD24F42");
            Console.WriteLine($"NEURAL_ASSET_AUDIT scope=isolated-pinned-read modelReadMs={modelReadMs:F2} modelHashMs={modelHashMs:F2} runtimeReadMs={runtimeReadMs:F2} runtimeHashMs={runtimeHashMs:F2}");
            var native = NativeLibrary.Load(Path.Combine(assetRoot, "onnxruntime.dll"), typeof(InferenceSession).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
                name is "onnxruntime" or "onnxruntime.dll" ? native : throw new DllNotFoundException("Neural audit native name mismatch."));
            if (profile)
            {
                RunConverterProfile(referenceModel, converterModel, source, target, check, previousRuntime);
                return;
            }
            if (runtimeBenchmark)
            {
                RunRuntimeBenchmark(referenceModel, converterModel, source, target, check, previousRuntime);
                return;
            }
            if (runtimeLifetime)
            {
                RunRuntimeLifetime(referenceModel, converterModel, source, target, check, previousRuntime);
                return;
            }
            if (runtimeCancel)
            {
                RunRuntimeCancel(converterModel, source, sourceSpectrum, sourceEmbedding, targetEmbedding, noise, modelPcm,
                    check, previousRuntime);
                return;
            }
            // Deliberately retain native until isolated process exit: ORT has a
            // process-wide managed environment beyond individual session life.
            using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableCpuMemArena = false, EnableMemoryPattern = false,
                LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR };
            options.AppendExecutionProvider_CPU(0);
            using var encoder = new InferenceSession(referenceModel, options);
            using var converter = new InferenceSession(converterModel, options);
            using var run = new RunOptions();
            var actualSource = Invoke(encoder, [Value("spectrum", sourceSpectrum, [1, 513, 32])], "embedding");
            var actualTarget = Invoke(encoder, [Value("spectrum", targetSpectrum, [1, 513, 32])], "embedding");
            Near(actualSource, sourceEmbedding, 0.00003, "self-built native source embedding versus PyTorch");
            Near(actualTarget, targetEmbedding, 0.00003, "self-built native target embedding versus PyTorch");
            var actualModel = Invoke(converter, [Value("spectrum", sourceSpectrum, [1, 513, 32]),
                Value("source_embedding", sourceEmbedding, [1, 256, 1]), Value("target_embedding", targetEmbedding, [1, 256, 1]),
                Value("noise", noise, [1, 192, 32])], "pcm");
            Near(actualModel, modelPcm, 0.0001, "self-built native converter versus PyTorch with fixed graph inputs");
            using var session = new NeuralOpenVoiceSession(referenceModel, converterModel, collectTimings: true);
            session.PrepareReferences([new PcmAudio(target, 22050)]);
            var output = session.ConvertAudio(new PcmAudio(source, 22050), [new PcmAudio(target, 22050)]);
            check(session.LastReferenceCacheHit, "explicit reference preparation warms the real session without synthesizing speech");
            check(output.SampleRate == 22050 && output.Samples.Length == finalPcm.Length, "managed full pipeline keeps the oracle output format");
            var differences = output.Samples.Zip(finalPcm, (a, b) => (double)a - b).ToArray();
            var max = differences.Max(Math.Abs); var rms = Math.Sqrt(differences.Average(x => x * x));
            check(max <= 8 && rms <= 1.5, $"managed STFT plus graph plus PCM versus PyTorch: max={max:G6}, rms={rms:G6} PCM units");
            var cached = session.ConvertAudio(new PcmAudio(source, 22050), [new PcmAudio(target, 22050)]);
            check(session.LastReferenceCacheHit && cached.Samples.SequenceEqual(output.Samples), "real native session reuses reference embedding without changing PCM");

            // The verified boundary must own its copy: changing the caller's
            // backing arrays after verification cannot change native input.
            var verificationStarted = Stopwatch.GetTimestamp();
            using var verifiedReference = NeuralVerifiedModel.ReadVerified(new MemoryStream(referenceModel, false), NeuralModelKind.Reference);
            using var verifiedConverter = NeuralVerifiedModel.ReadVerified(new MemoryStream(converterModel, false), NeuralModelKind.Converter);
            var verificationMs = Stopwatch.GetElapsedTime(verificationStarted).TotalMilliseconds;
            referenceModel[0] ^= 255; converterModel[0] ^= 255;
            try
            {
                using var verifiedSession = new NeuralOpenVoiceSession(verifiedReference, verifiedConverter, collectTimings: true);
                var verifiedOutput = verifiedSession.ConvertAudio(new PcmAudio(source, 22050), [new PcmAudio(target, 22050)]);
                check(verifiedOutput.SampleRate == output.SampleRate && verifiedOutput.Samples.SequenceEqual(output.Samples),
                    "verified private model ownership preserves exact PCM despite caller array mutation");
                Console.WriteLine($"NEURAL_VERIFIED_MODEL scope=same-process-single-construction verifiedReadAndHashMs={verificationMs:F2} rawConstructorRehashMs={session.InitializationTimings!.ModelValidationMilliseconds:F3} verifiedConstructorIdentityMs={verifiedSession.InitializationTimings!.ModelValidationMilliseconds:F3}");
            }
            finally { referenceModel[0] ^= 255; converterModel[0] ^= 255; }
            Rejects(() => verifiedReference.ValidateKind(NeuralModelKind.Converter), check, "verified reference rejects converter use");
            Rejects(() => verifiedConverter.ValidateKind(NeuralModelKind.Reference), check, "verified converter rejects reference use");

            // A bounded CPU comparison on identical, longer PCM; it does not
            // select a production default or claim end-to-end chat latency.
            var longer = new PcmAudio(Enumerable.Range(0, 4).SelectMany(_ => source).ToArray(), 22050);
            PcmAudio? baseline = null;
            foreach (var threads in new[] { 2, 1, 3, 4 })
            {
                using var candidate = new NeuralOpenVoiceSession(referenceModel, converterModel, threads);
                candidate.PrepareReferences([new PcmAudio(target, 22050)]);
                var timings = new List<double>();
                PcmAudio? latest = null;
                for (var trial = 0; trial < 3; trial++)
                {
                    var started = System.Diagnostics.Stopwatch.GetTimestamp();
                    latest = candidate.ConvertAudio(longer, [new PcmAudio(target, 22050)]);
                    timings.Add(System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                }
                baseline ??= latest!;
                var sameLength = latest!.Samples.Length == baseline.Samples.Length;
                var delta = sameLength ? latest.Samples.Zip(baseline.Samples, (a, b) => Math.Abs((int)a - b)).Max() : int.MaxValue;
                check(sameLength && delta <= 8, $"neural CPU thread candidate {threads} preserves PCM within 8 quantization units (max={delta})");
                Console.WriteLine($"NEURAL_CPU threads={threads} samples={longer.Samples.Length} rate={longer.SampleRate} trials=3 medianMs={timings.Order().ElementAt(1):F2} minMs={timings.Min():F2} maxMs={timings.Max():F2}");
            }

            // A separate, single preparation on the real bundled bank. Import
            // into a uniquely owned fixture; never inspect or change user data.
            // This is deliberately distinct from the 1-clip synthetic oracle
            // above and does not claim an end-to-end product warmup breakdown.
            using var bankFixture = new TestDirectory();
            var banks = Voicebanks.LoadInstalledAsync(bankFixture.DirectoryPath).GetAwaiter().GetResult();
            if (banks.Count != 1 || banks[0].Name != "hosinohito") throw new InvalidDataException("Neural timing fixture bank mismatch.");
            var referenceStarted = Stopwatch.GetTimestamp();
            var bankReferences = NeuralVoiceReferences.ReadAsync(banks[0], bankFixture.DirectoryPath, default).GetAwaiter().GetResult();
            var referenceReadMs = Stopwatch.GetElapsedTime(referenceStarted).TotalMilliseconds;
            check(bankReferences.Clips.Count == 149, "neural preparation timing uses all 149 actual bundled reference WAVs");
            var bankThreads = NeuralOpenVoiceSession.WorkerThreads(Environment.ProcessorCount);
            using var bankSession = new NeuralOpenVoiceSession(verifiedReference, verifiedConverter, bankThreads, collectTimings: true);
            var preparationStarted = Stopwatch.GetTimestamp();
            bankSession.PrepareReferences(bankReferences.Clips);
            var preparationMs = Stopwatch.GetElapsedTime(preparationStarted).TotalMilliseconds;
            var initialization = bankSession.InitializationTimings!; var preparation = bankSession.LastPreparationTimings!;
            check(!preparation.CacheHit && preparation.ReferenceCount == 149 && preparation.ReferenceSamples == bankReferences.Clips.Sum(item => (long)item.Samples.Length) &&
                new[] { initialization.ModelValidationMilliseconds, initialization.OptionsMilliseconds, initialization.ReferenceSessionMilliseconds,
                    initialization.ConverterSessionMilliseconds, preparation.FingerprintMilliseconds, preparation.FeaturesMilliseconds, preparation.EncoderMilliseconds }
                    .All(value => double.IsFinite(value) && value >= 0),
                "neural opt-in preparation timings preserve actual reference boundaries and finite per-stage measurements");
            Console.WriteLine($"NEURAL_PREPARE_INIT scope=bundled149-warm-process threads={bankThreads} verifiedModelIdentityMs={initialization.ModelValidationMilliseconds:F2} optionsMs={initialization.OptionsMilliseconds:F2} referenceSessionMs={initialization.ReferenceSessionMilliseconds:F2} converterSessionMs={initialization.ConverterSessionMilliseconds:F2}");
            Console.WriteLine($"NEURAL_PREPARE_REFERENCE scope=bundled149-warm-process count={preparation.ReferenceCount} samples={preparation.ReferenceSamples} decodedReadMs={referenceReadMs:F2} prepareMs={preparationMs:F2} fingerprintMs={preparation.FingerprintMilliseconds:F2} featuresMs={preparation.FeaturesMilliseconds:F2} encoderMs={preparation.EncoderMilliseconds:F2}");
            CheckOwnedReferencesNative(bankSession, bankReferences.Clips, new PcmAudio(source, 22050), check);

            // Fresh sessions prevent reference-cache hits from masquerading as
            // feature parallelism. Construction and conversion are not timed.
            var serialPrepare = new List<double>(); var parallelPrepare = new List<double>();
            var serialFeatures = new List<double>(); var parallelFeatures = new List<double>();
            short[]? pairedBaseline = null;
            for (var trial = -1; trial < 3; trial++)
            for (var order = 0; order < 2; order++)
            {
                var parallel = trial < 0 ? order == 1 : (trial + order) % 2 == 1;
                using var pairSession = new NeuralOpenVoiceSession(verifiedReference, verifiedConverter, bankThreads, collectTimings: true)
                    { ParallelReferenceFeatures = parallel };
                var prepareStart = Stopwatch.GetTimestamp();
                pairSession.PrepareReferences(bankReferences.Clips);
                var elapsed = Stopwatch.GetElapsedTime(prepareStart).TotalMilliseconds;
                var measured = pairSession.LastPreparationTimings!;
                check(!measured.CacheHit && measured.ReferenceCount == 149,
                    "serial/parallel feature timing uses a fresh session and every bundled reference");
                var pairedOutput = pairSession.ConvertAudio(new PcmAudio(source, 22050), bankReferences.Clips);
                pairedBaseline ??= pairedOutput.Samples;
                check(pairSession.LastReferenceCacheHit && pairedOutput.Samples.SequenceEqual(pairedBaseline),
                    "paired feature extraction preserves exact ordered-embedding source conversion PCM");
                if (trial >= 0)
                {
                    (parallel ? parallelPrepare : serialPrepare).Add(elapsed);
                    (parallel ? parallelFeatures : serialFeatures).Add(measured.FeaturesMilliseconds);
                }
            }
            Console.WriteLine($"NEURAL_FEATURE_PAIR scope=bundled149-fresh-session trials=3 warmupsPerMode=1 threads={bankThreads} serialPrepareMedianMs={serialPrepare.Order().ElementAt(1):F2} parallelPrepareMedianMs={parallelPrepare.Order().ElementAt(1):F2} serialFeatureWallMedianMs={serialFeatures.Order().ElementAt(1):F2} parallelFeatureWallMedianMs={parallelFeatures.Order().ElementAt(1):F2} serialTrialsMs={string.Join(",", serialPrepare.Select(x => x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))} parallelTrialsMs={string.Join(",", parallelPrepare.Select(x => x.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}");

            verifiedReference.Dispose();
            var disposedRejected = false;
            try { using var rejected = verifiedReference.CreateNativeSession(options); }
            catch (ObjectDisposedException) { disposedRejected = true; }
            check(disposedRejected, "disposed verified model cannot construct another native session");

            float[] Invoke(InferenceSession graph, NamedOnnxValue[] inputs, string name)
            { using var values = graph.Run(inputs, [name], run); return values.Single().AsTensor<float>().ToArray(); }
        }
        finally { foreach (var pin in pins) pin.Dispose(); }

        byte[] ReadPinned(string name, int length, string hash)
        {
            var path = Path.Combine(assetRoot, name); WorldComparison.RejectLinks(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(stream);
            if (stream.Length != length) throw new InvalidDataException("Neural audit asset size mismatch.");
            var started = Stopwatch.GetTimestamp();
            var bytes = new byte[length]; stream.ReadExactly(bytes);
            var readMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds; started = Stopwatch.GetTimestamp();
            var actualHash = Convert.ToHexString(SHA256.HashData(bytes));
            var hashMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (name.EndsWith(".onnx", StringComparison.Ordinal)) { modelReadMs += readMs; modelHashMs += hashMs; }
            else { runtimeReadMs += readMs; runtimeHashMs += hashMs; }
            if (actualHash != hash) throw new InvalidDataException("Neural audit asset hash mismatch.");
            return bytes;
        }
        short[] Shorts(string key, int count)
        {
            var bytes = json.GetProperty(key).GetBytesFromBase64();
            if (bytes.Length != count * 2) throw new InvalidDataException("Neural oracle PCM size mismatch.");
            return Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2))).ToArray();
        }
        float[] Floats(string key, int count)
        {
            var bytes = json.GetProperty(key).GetBytesFromBase64();
            if (bytes.Length != count * 4) throw new InvalidDataException("Neural oracle tensor size mismatch.");
            var result = Enumerable.Range(0, count).Select(i => BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4, 4))).ToArray();
            if (result.Any(x => !float.IsFinite(x))) throw new InvalidDataException("Neural oracle nonfinite tensor.");
            return result;
        }
        void Near(float[] actual, float[] expected, double limit, string name)
        {
            var max = actual.Length == expected.Length && actual.All(float.IsFinite)
                ? actual.Zip(expected, (a, b) => Math.Abs((double)a - b)).Max() : double.PositiveInfinity;
            check(max <= limit, $"{name}: maxAbs={max:G6}, limit={limit:G6}");
        }
        static NamedOnnxValue Value(string name, float[] values, int[] shape)
            => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, shape));
    }

    private static void CheckOwnedReferencesNative(NeuralOpenVoiceSession session, IReadOnlyList<PcmAudio> clips,
        PcmAudio source, Action<bool, string> check)
    {
        using var full = new MemoryStream();
        NeuralPcmProtocol.WritePreparationAsync(full, clips, default).GetAwaiter().GetResult();
        full.Position = 0; var request = NeuralPcmProtocol.ReadRequest(full)!;
        var key = NeuralOpenVoiceSession.FingerprintReferences(clips);
        // The serialized input and explicit inspection copy remain caller-owned.
        full.GetBuffer()[24] ^= 1;
        var inspection = request.References.CopyForInspection(); inspection[0].Samples[0] ^= 127;
        check(request.References.Matches(key, clips.Count, clips.Sum(clip => clip.Samples.Length)),
            "verified reference ownership survives caller header and inspection-array mutation before native preparation");
        using var cache = new NeuralPcmReferenceCache(); cache.Commit(request);
        using var cached = new MemoryStream();
        NeuralPcmProtocol.WritePreparationAsync(cached, clips, default, key, reuseReferences: true).GetAwaiter().GetResult();
        cached.Position = 0; var reused = NeuralPcmProtocol.ReadRequest(cached, cache)!;
        check(ReferenceEquals(request.References, reused.References), "native ACK path resolves the same opaque verified bank");
        var fingerprints = new double[4]; var elapsed = new double[4];
        for (var order = 0; order < 4; order++)
        {
            var started = Stopwatch.GetTimestamp();
            if (order is 0 or 3) session.PrepareReferences(clips);
            else (order == 1 ? request.References : reused.References).Prepare(session);
            elapsed[order] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var timing = session.LastPreparationTimings ?? throw new InvalidDataException("Missing owned reference timings.");
            fingerprints[order] = timing.FingerprintMilliseconds;
            check(timing.CacheHit && timing.ReferenceCount == clips.Count,
                "raw/full-owned/ACK-owned/raw preparation reuses the same embedding after complete boundary validation");
        }
        var baseline = session.ConvertAudio(source, clips);
        var owned = request.References.Convert(session, source);
        check(session.LastReferenceCacheHit && owned.SampleRate == baseline.SampleRate && owned.Samples.SequenceEqual(baseline.Samples),
            "full protocol-owned references retain every native output PCM sample against the ordinary rehashing entry");
        var acknowledged = reused.References.Convert(session, source);
        check(session.LastReferenceCacheHit && acknowledged.Samples.SequenceEqual(baseline.Samples),
            "ACK-owned references preserve every native PCM sample without rehashing the immutable bank");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var canceled = false;
        try { reused.References.Prepare(session, stopped.Token); } catch (OperationCanceledException) { canceled = true; }
        check(canceled, "owned preparation retains cancellation before native work");
        // Ordinary caller arrays are deliberately outside the owned boundary.
        var ordinary = new[] { new PcmAudio((short[])clips[0].Samples.Clone(), clips[0].SampleRate) };
        session.PrepareReferences(ordinary); ordinary[0].Samples[0] ^= 1;
        session.PrepareReferences(ordinary);
        check(!session.LastReferenceCacheHit, "ordinary session entry rehashes caller-array changes rather than accepting an old verified fingerprint");
        Console.WriteLine("NEURAL_OWNED_REFERENCES " + JsonSerializer.Serialize(new { scope = "bundled149-same-session-raw-owned-ACK-owned-raw",
            count = clips.Count, samples = clips.Sum(clip => (long)clip.Samples.Length), fingerprintMilliseconds = fingerprints,
            preparationMilliseconds = elapsed, comparison = "single old/new/new/old preparation only; not whole synthesis latency" }));
    }

    private static void RunRuntimeCancel(byte[] converterModel, short[] source, float[] sourceSpectrum,
        float[] sourceEmbedding, float[] targetEmbedding, float[] noise, float[] modelPcm,
        Action<bool, string> check, bool previousRuntime)
    {
        using var options = new SessionOptions
        {
            IntraOpNumThreads = 4, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false,
            EnableMemoryPattern = false, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        options.AppendExecutionProvider_CPU(0);
        using var converter = new InferenceSession(converterModel, options);
        var shortInputs = Inputs(sourceSpectrum, 32, noise);
        var before = ShortRun();
        var longSamples = new short[992250];
        for (var i = 0; i < longSamples.Length; i++) longSamples[i] = source[i % source.Length];
        var features = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(longSamples, 22050));
        var longNoise = NeuralOpenVoiceSession.CreateNoise(checked(192 * features.Frames));
        var longInputs = Inputs(features.Magnitudes, features.Frames, longNoise);
        using var cancellation = new CancellationTokenSource();
        var entered = 0; var returned = 0; var firedDuringRun = 0; var signaled = 0; var signalFailed = 0;
        var completed = false; var elapsedMs = 0.0; OnnxRuntimeException? interrupted = null;
        var wasCanceledBeforeRun = cancellation.IsCancellationRequested;
        using (var cancelOptions = new RunOptions())
        {
            var registration = cancellation.Token.Register(() =>
            {
                if (Volatile.Read(ref entered) == 1 && Volatile.Read(ref returned) == 0)
                    Volatile.Write(ref firedDuringRun, 1);
                try { cancelOptions.Terminate = true; Volatile.Write(ref signaled, 1); }
                catch (Exception) { Volatile.Write(ref signalFailed, 1); }
            });
            try
            {
                var started = Stopwatch.GetTimestamp();
                cancellation.CancelAfter(100);
                Volatile.Write(ref entered, 1);
                try
                {
                    using var unexpected = converter.Run(longInputs, ["pcm"], cancelOptions);
                    completed = true;
                }
                catch (OnnxRuntimeException error) { interrupted = error; }
                finally
                {
                    elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    Volatile.Write(ref returned, 1);
                }
            }
            finally
            {
                cancellation.CancelAfter(Timeout.Infinite);
                registration.Dispose(); // Joins any callback before RunOptions is disposed.
            }
        }
        var terminateException = interrupted is not null &&
            interrupted.Message.Contains("Exiting due to terminate flag being set to true", StringComparison.Ordinal);
        check(!wasCanceledBeforeRun && cancellation.IsCancellationRequested && Volatile.Read(ref firedDuringRun) == 1 &&
            Volatile.Read(ref signaled) == 1 && Volatile.Read(ref signalFailed) == 0 && !completed && terminateException &&
            elapsedMs is >= 75 and < 5000,
            "delayed in-Run cancellation signals Terminate and returns the native termination exception without PCM");
        var after = ShortRun(); // A new RunOptions on the exact same converter.
        check(before.Samples.SequenceEqual(after.Samples),
            "the same converter recovers after native Run cancellation with bit-identical fixed short PCM");
        Console.WriteLine("NEURAL_RUNTIME_CANCEL " + JsonSerializer.Serialize(new
        {
            runtime = previousRuntime ? "previous-baseline" : "approved-fused-conv-col2im1d",
            scope = "delayed-native-run-cancel-and-same-converter-recovery", threads = 4, cancelAfterMs = 100,
            longInputSamples = longSamples.Length, longFrames = features.Frames, canceledRunMs = elapsedMs,
            wasCanceledBeforeRun, cancellationFired = cancellation.IsCancellationRequested,
            firedDuringRun = Volatile.Read(ref firedDuringRun) == 1, terminateSignaled = Volatile.Read(ref signaled) == 1,
            signalFailed = Volatile.Read(ref signalFailed) != 0, terminateException, completed,
            recoveredSamples = after.Samples.Length,
            beforePcmSha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(before.Samples.AsSpan()))),
            afterPcmSha256 = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(after.Samples.AsSpan()))),
            limitation = "Cooperative native cancellation only; the existing parent owns the finite process deadline and kill/reap boundary."
        }));
        NamedOnnxValue[] Inputs(float[] magnitudes, int frames, float[] latentNoise) =>
        [
            Tensor("spectrum", magnitudes, [1, 513, frames]),
            Tensor("source_embedding", sourceEmbedding, [1, 256, 1]),
            Tensor("target_embedding", targetEmbedding, [1, 256, 1]),
            Tensor("noise", latentNoise, [1, 192, frames])
        ];
        PcmAudio ShortRun()
        {
            using var run = new RunOptions();
            using var output = converter.Run(shortInputs, ["pcm"], run);
            var values = output.Single().AsTensor<float>().ToArray();
            var max = values.Length == modelPcm.Length && values.All(float.IsFinite)
                ? values.Zip(modelPcm, (actual, expected) => Math.Abs((double)actual - expected)).Max() : double.PositiveInfinity;
            check(max <= 0.0001, $"native cancellation boundary short converter matches the fixed float oracle: maxAbs={max:G6}");
            return NeuralOpenVoiceSession.Quantize(values);
        }
        static NamedOnnxValue Tensor(string name, float[] values, int[] shape)
            => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, shape));
    }

    private static void RunRuntimeLifetime(byte[] referenceModel, byte[] converterModel, short[] source, short[] target,
        Action<bool, string> check, bool previousRuntime)
    {
        const string expectedShortHash = "CC9C64EAA6B11D89BB4A1AEF23022E0E4309D607D7EFBC0DFF6EABD75C163ECA";
        const string expectedLongHash = "62066FE4410D79F9AB139066BA4374988B3946B644C7468ED0F1E1D601C576F7";
        var references = new[] { new PcmAudio(target, 22050) };
        var started = Stopwatch.GetTimestamp();
        using var session = new NeuralOpenVoiceSession(referenceModel, converterModel, threads: 4);
        var constructorMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        session.PrepareReferences(references);
        var preparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var first = MeasureLifetimeConversion(session, source, references, 8192);
        check(first.OutputSamples == 8192 && first.OutputSampleRate == 22050 && first.CacheHit && first.OutputHash == expectedShortHash,
            "runtime lifetime first short matches the fixed prepared-reference PCM");
        // No large PCM/spectrum arrays escape the non-inlined helper. GC is
        // outside synthesis timing; retain both model arrays and the session.
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        using var process = Process.GetCurrentProcess(); process.Refresh();
        var beforePrivate = process.PrivateMemorySize64; var beforeWorkingSet = process.WorkingSet64;
        var beforePeakWorkingSet = process.PeakWorkingSet64;
        var longest = MeasureLifetimeConversion(session, source, references, 992250);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        process.Refresh();
        var afterPrivate = process.PrivateMemorySize64; var afterWorkingSet = process.WorkingSet64;
        var afterPeakWorkingSet = process.PeakWorkingSet64;
        check(longest.InputSamples == 992250 && longest.OutputSamples == 992000 && longest.OutputSampleRate == 22050 &&
            longest.CacheHit && longest.OutputHash == expectedLongHash,
            "runtime lifetime maximum source preserves fixed length and exact baseline PCM");
        var last = MeasureLifetimeConversion(session, source, references, 8192);
        check(last.OutputSamples == first.OutputSamples && last.OutputSampleRate == first.OutputSampleRate &&
            last.CacheHit && last.OutputHash == first.OutputHash && last.OutputHash == expectedShortHash,
            "runtime lifetime short after the maximum input preserves fixed PCM and reference reuse");
        Console.WriteLine("NEURAL_RUNTIME_LIFETIME " + JsonSerializer.Serialize(new
        {
            runtime = previousRuntime ? "previous-baseline" : "approved-fused-conv-col2im1d",
            scope = "short-maximum-short-same-session", threads = 4, cpuArena = false, memoryPattern = false,
            longRuns = 1, constructorMs, preparationMs,
            firstShort = new { inputSamples = first.InputSamples, outputSamples = first.OutputSamples,
                inputPcmSha256 = first.InputHash, outputPcmSha256 = first.OutputHash, synthesisMs = first.Milliseconds },
            maximum = new { inputSamples = longest.InputSamples, outputSamples = longest.OutputSamples,
                inputPcmSha256 = longest.InputHash, outputPcmSha256 = longest.OutputHash, synthesisMs = longest.Milliseconds },
            lastShort = new { inputSamples = last.InputSamples, outputSamples = last.OutputSamples,
                inputPcmSha256 = last.InputHash, outputPcmSha256 = last.OutputHash, synthesisMs = last.Milliseconds },
            beforeLong = new { privateBytes = beforePrivate, workingSetBytes = beforeWorkingSet, peakWorkingSetBytes = beforePeakWorkingSet },
            afterLong = new { privateBytes = afterPrivate, workingSetBytes = afterWorkingSet, peakWorkingSetBytes = afterPeakWorkingSet },
            limitation = "Separate runtime processes; process peak includes initialization; memory samples follow out-of-timing GC without working-set trimming."
        }));
        GC.KeepAlive(referenceModel); GC.KeepAlive(converterModel); GC.KeepAlive(source); GC.KeepAlive(target);
        GC.KeepAlive(references); GC.KeepAlive(session);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (int InputSamples, int OutputSamples, int OutputSampleRate, string InputHash, string OutputHash,
        double Milliseconds, bool CacheHit) MeasureLifetimeConversion(NeuralOpenVoiceSession session, short[] source,
        IReadOnlyList<PcmAudio> references, int count)
    {
        var samples = new short[count];
        for (var i = 0; i < samples.Length; i++) samples[i] = source[i % source.Length];
        var input = new PcmAudio(samples, 22050);
        var started = Stopwatch.GetTimestamp();
        var output = session.ConvertAudio(input, references);
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        return (samples.Length, output.Samples.Length, output.SampleRate,
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples.AsSpan()))),
            Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(output.Samples.AsSpan()))),
            elapsed, session.LastReferenceCacheHit);
    }

    private static void RunRuntimeBenchmark(byte[] referenceModel, byte[] converterModel, short[] source, short[] target,
        Action<bool, string> check, bool previousRuntime)
    {
        var references = new[] { new PcmAudio(target, 22050) };
        var started = Stopwatch.GetTimestamp();
        using var session = new NeuralOpenVoiceSession(referenceModel, converterModel, threads: 4, collectTimings: true);
        var constructorMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        started = Stopwatch.GetTimestamp();
        session.PrepareReferences(references);
        var preparationMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        foreach (var count in new[] { 8192, 32768, 131072 })
        {
            var samples = new short[count];
            for (var i = 0; i < samples.Length; i++) samples[i] = source[i % source.Length];
            var input = new PcmAudio(samples, 22050);
            started = Stopwatch.GetTimestamp();
            var warmup = session.ConvertAudio(input, references);
            var warmupMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var times = new double[3]; var hashes = new string[3];
            var stages = new NeuralConversionTimings[3];
            for (var trial = 0; trial < times.Length; trial++)
            {
                started = Stopwatch.GetTimestamp();
                var output = session.ConvertAudio(input, references);
                times[trial] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                hashes[trial] = PcmHash(output);
                stages[trial] = session.LastConversionTimings ?? throw new InvalidDataException("Missing conversion timings");
                var t = stages[trial];
                double[] parts = [t.ReferenceMilliseconds, t.SourceFeaturesMilliseconds, t.SourceEncoderMilliseconds,
                    t.NoiseAndInputsMilliseconds, t.ConverterMilliseconds, t.QuantizeMilliseconds];
                check(parts.All(value => double.IsFinite(value) && value >= 0) && parts.Sum() <= times[trial],
                    "successful conversion stage intervals are finite, non-overlapping and within total wall time");
                check(session.LastReferenceCacheHit && output.SampleRate == warmup.SampleRate && output.Samples.SequenceEqual(warmup.Samples),
                    $"runtime benchmark shape {count} trial {trial + 1} retains prepared references and exact warmup PCM");
            }
            Console.WriteLine("NEURAL_RUNTIME_BENCHMARK " + JsonSerializer.Serialize(new
            {
                runtime = previousRuntime ? "previous-baseline" : "approved-fused-conv-col2im1d",
                scope = "unprofiled-synthesis-with-prepared-reference", inputSamples = count, outputSamples = warmup.Samples.Length,
                sampleRate = input.SampleRate, threads = 4, cpuArena = false, memoryPattern = false,
                warmups = 1, trials = 3, constructorMs, preparationMs, warmupMs,
                medianMs = times.Order().ElementAt(1), trialMs = times, conversionStages = stages,
                inputPcmSha256 = PcmHash(input), referencePcmSha256 = PcmHash(references[0]),
                warmupPcmSha256 = PcmHash(warmup), trialPcmSha256 = hashes,
                limitation = "Same session across shapes; excludes constructor/preparation from synthesis times; compare runtimes in separate processes."
            }));
        }
        static string PcmHash(PcmAudio audio) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(audio.Samples.AsSpan())));
    }

    private static void RunConverterProfile(byte[] referenceModel, byte[] converterModel, short[] source, short[] target,
        Action<bool, string> check, bool previousRuntime)
    {
        using var temporary = new TestDirectory();
        var root = Path.GetFullPath(temporary.DirectoryPath);
        var input = new PcmAudio(Enumerable.Range(0, 4).SelectMany(_ => source).ToArray(), 22050);
        var references = new[] { new PcmAudio(target, 22050) };
        using var encodingOptions = Options(false);
        using var profilingOptions = Options(true);
        using var encoder = new InferenceSession(referenceModel, encodingOptions);
        using var converter = new InferenceSession(converterModel, profilingOptions);
        using var run = new RunOptions();
        var sourceFeatures = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(input.Samples, input.SampleRate));
        var targetFeatures = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(target, 22050));
        var sourceIdentity = Encode(sourceFeatures); var targetIdentity = Encode(targetFeatures);
        var explicitNoise = NeuralOpenVoiceSession.CreateNoise(checked(192 * sourceFeatures.Frames));
        var inputs = new[]
        {
            Tensor("spectrum", sourceFeatures.Magnitudes, [1, 513, sourceFeatures.Frames]),
            Tensor("source_embedding", sourceIdentity, [1, 256, 1]),
            Tensor("target_embedding", targetIdentity, [1, 256, 1]),
            Tensor("noise", explicitNoise, [1, 192, sourceFeatures.Frames])
        };
        var warmupStarted = Stopwatch.GetTimestamp();
        _ = ConvertProfiled();
        var warmupMs = Stopwatch.GetElapsedTime(warmupStarted).TotalMilliseconds;
        var started = Stopwatch.GetTimestamp();
        var profiled = ConvertProfiled();
        var profiledRunMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var profilePath = converter.EndProfiling();
        if (!Path.IsPathFullyQualified(profilePath)) throw new InvalidDataException("Profiler returned a relative path.");
        profilePath = Path.GetFullPath(profilePath);
        if (!string.Equals(Path.GetDirectoryName(profilePath), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(profilePath).StartsWith("converter_", StringComparison.Ordinal) || Path.GetExtension(profilePath) != ".json")
            throw new InvalidDataException("Profiler path escaped its owned root.");
        WorldComparison.RejectLinks(profilePath);
        using var profileFile = new FileStream(profilePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (profileFile.Length is < 1 or > 64 * 1024 * 1024) throw new InvalidDataException("Profiler output size limit.");
        var bytes = new byte[checked((int)profileFile.Length)]; profileFile.ReadExactly(bytes);
        if (profileFile.ReadByte() != -1) throw new InvalidDataException("Profiler output changed while reading.");
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        var events = document.RootElement;
        if (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 40000) throw new InvalidDataException("Profiler event limit.");
        var modelRuns = events.EnumerateArray().Where(item => IsEvent(item, "Session", "model_run"))
            .Select(item => (Start: Number(item, "ts"), Duration: Number(item, "dur"))).OrderBy(item => item.Start).ToArray();
        var initialization = events.EnumerateArray()
            .Where(item => IsEvent(item, "Session", "model_loading_array") || IsEvent(item, "Session", "session_initialization"))
            .Select(item => new { name = item.GetProperty("name").GetString(), durationMs = Number(item, "dur") / 1000.0 })
            .ToArray();
        if (modelRuns.Length != 2 || modelRuns[1].Duration <= 0) throw new InvalidDataException("Expected exactly warmup and measured converter runs.");
        var selected = modelRuns[1]; var end = selected.Start + selected.Duration;
        var grouped = new Dictionary<string, (int Count, double Microseconds)>(StringComparer.Ordinal);
        var longestNodes = new List<(string Name, string Op, double Microseconds)>(6);
        foreach (var item in events.EnumerateArray())
        {
            if (!item.TryGetProperty("cat", out var category) || category.GetString() != "Node" ||
                !item.TryGetProperty("name", out var eventName) || eventName.GetString() is not { } name ||
                !name.EndsWith("_kernel_time", StringComparison.Ordinal)) continue;
            var at = Number(item, "ts"); var duration = Number(item, "dur");
            if (at < selected.Start || at + duration > end) continue;
            var op = item.GetProperty("args").GetProperty("op_name").GetString();
            if (op is null || op.Length is < 1 or > 128 || !op.All(character => character is >= '!' and <= '~'))
                throw new InvalidDataException("Invalid profiler operator name.");
            if (name.Length > 1024) throw new InvalidDataException("Profiler node-name limit.");
            longestNodes.Add((name, op, duration));
            longestNodes.Sort((left, right) => left.Microseconds != right.Microseconds
                ? right.Microseconds.CompareTo(left.Microseconds) : StringComparer.Ordinal.Compare(left.Name, right.Name));
            if (longestNodes.Count > 5) longestNodes.RemoveAt(5);
            var prior = grouped.GetValueOrDefault(op);
            grouped[op] = (prior.Count + 1, prior.Microseconds + duration);
            if (grouped.Count > 512) throw new InvalidDataException("Profiler operator-kind limit.");
        }
        check(grouped.Count > 0, "converter profile contains measured-run kernel events after excluding warmup");
        var fusedConvCount = grouped.GetValueOrDefault("FusedConv").Count;
        check(previousRuntime ? fusedConvCount == 0 : fusedConvCount > 0,
            "converter profile observes the explicitly selected runtime FusedConv registration");
        using var ordinary = new NeuralOpenVoiceSession(referenceModel, converterModel, threads: 4);
        var ordinaryPcm = ordinary.ConvertAudio(input, references);
        check(profiled.SampleRate == ordinaryPcm.SampleRate && profiled.Samples.SequenceEqual(ordinaryPcm.Samples),
            "profiling converter preserves exact final PCM against the ordinary pipeline with identical input and threads");
        Console.WriteLine("NEURAL_OPERATOR_PROFILE " + JsonSerializer.Serialize(new
        {
            samples = input.Samples.Length, sampleRate = input.SampleRate, threads = 4, runs = 2, warmupExcluded = 1,
            runtime = previousRuntime ? "previous-baseline" : "approved-fused-conv-col2im1d", fusedConvCount,
            convCount = grouped.GetValueOrDefault("Conv").Count, leakyReluCount = grouped.GetValueOrDefault("LeakyRelu").Count,
            sourcePcmSha256 = PcmHash(input), referencePcmSha256 = PcmHash(references[0]), outputPcmSha256 = PcmHash(profiled),
            outputSamples = profiled.Samples.Length, profileBytes = bytes.Length,
            initialization,
            warmupProfiledRunAndQuantizeMs = warmupMs, measuredProfiledRunAndQuantizeMs = profiledRunMs,
            modelRunMs = selected.Duration / 1000.0, nodeKernelRawSumMs = grouped.Values.Sum(value => value.Microseconds) / 1000.0,
            nodeKernelCount = grouped.Values.Sum(value => value.Count), operatorKinds = grouped.Count,
            limitation = "Profiling adds overhead; node raw duration sums are not wall time or an unprofiled latency estimate.",
            longestNodes = longestNodes.Select(node => new { name = node.Name, op = node.Op, durationMs = node.Microseconds / 1000.0 }).ToArray(),
            top = grouped.OrderByDescending(pair => pair.Value.Microseconds).ThenBy(pair => pair.Key, StringComparer.Ordinal).Take(10)
                .Select(pair => new { op = pair.Key, count = pair.Value.Count, rawSumMs = pair.Value.Microseconds / 1000.0 }).ToArray()
        }));
        // All using declarations end before the enclosing TestDirectory removes
        // its profile file, on both success and exception. No audio is saved.
        SessionOptions Options(bool enableProfile)
        {
            var options = new SessionOptions
            {
                IntraOpNumThreads = 4, InterOpNumThreads = 1, ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL, EnableCpuMemArena = false,
                EnableMemoryPattern = false, LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
            };
            try
            {
                if (enableProfile)
                {
                    options.ProfileOutputPathPrefix = Path.Combine(root, "converter");
                    options.EnableProfiling = true;
                }
                options.AppendExecutionProvider_CPU(0); return options;
            }
            catch { options.Dispose(); throw; }
        }
        float[] Encode(NeuralSpectrogram.Spectrum features)
        {
            using var values = encoder.Run([Tensor("spectrum", features.Magnitudes, features.Shape)], ["embedding"], run);
            var embedding = values.Single().AsTensor<float>().ToArray();
            if (embedding.Length != 256 || embedding.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Invalid profile embedding.");
            return embedding;
        }
        PcmAudio ConvertProfiled()
        {
            using var output = converter.Run(inputs, ["pcm"], run);
            var pcm = output.Single().AsTensor<float>().ToArray();
            if (pcm.Length != sourceFeatures.Frames * NeuralSpectrogram.HopLength) throw new InvalidDataException("Invalid profile PCM length.");
            return NeuralOpenVoiceSession.Quantize(pcm);
        }
        static NamedOnnxValue Tensor(string name, float[] values, int[] shape)
            => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, shape));
        static bool IsEvent(JsonElement item, string category, string name)
            => item.TryGetProperty("cat", out var cat) && cat.GetString() == category &&
                item.TryGetProperty("name", out var eventName) && eventName.GetString() == name;
        static double Number(JsonElement item, string property)
        {
            var value = item.GetProperty(property).GetDouble();
            if (!double.IsFinite(value) || value < 0) throw new InvalidDataException("Invalid profiler event time.");
            return value;
        }
        static string PcmHash(PcmAudio audio) => Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(audio.Samples.AsSpan())));
    }

    internal static void Run(Action<bool, string> check)
    {
        CheckReferenceFeaturePairs(check);
        check(new[] { 0, 1, 2, 4, 6, 8, 64 }.Select(NeuralOpenVoiceSession.WorkerThreads)
            .SequenceEqual(new[] { 1, 1, 2, 2, 2, 4, 4 }),
            "neural worker bounds CPU threads and keeps lower-core machines at their previous cap");
        check(new[] { -1, 0, 1, 2, 4, 6, 7, 8, 9, 64 }.Select(NeuralOpenVoiceSession.WorkerParallelReferenceFeatures)
            .SequenceEqual(new[] { false, false, false, false, false, false, false, true, true, true }),
            "neural worker enables paired reference features only at the measured eight-logical-CPU threshold");
        var clip = new PcmAudio(Enumerable.Repeat((short)1000, 22050).ToArray(), 22050);
        NeuralOpenVoiceSession.ValidateInputs(clip, Enumerable.Repeat(clip, 149).ToArray());
        check(true, "neural reference limits accept an existing 149-file bank independently of source duration");
        Rejects(() => NeuralOpenVoiceSession.ValidateModels([], []), check, "neural graph size validation runs before any native runtime is loaded");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, []), check, "neural rejects a missing reference bank");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, Enumerable.Repeat(clip, 150).ToArray()), check, "neural caps reference count");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, [new PcmAudio(new short[22050 * 30 + 1], 22050)]), check, "neural reference duration is capped per file");
        var thirty = new PcmAudio(Enumerable.Repeat((short)1000, 22050 * 30).ToArray(), 22050);
        NeuralOpenVoiceSession.ValidateInputs(clip, Enumerable.Repeat(thirty, 6).ToArray());
        check(true, "neural accepts exactly 180 seconds of valid reference PCM");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, Enumerable.Repeat(thirty, 7).ToArray()), check, "neural caps total reference duration independently");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(new PcmAudio(new short[22050 * 45 + 1], 22050), [clip]), check, "neural caps the source utterance independently");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, [new PcmAudio(new short[22050], 22050)]), check, "neural rejects silent reference audio");
        Rejects(() => NeuralOpenVoiceSession.ValidateInputs(clip, [new PcmAudio([1], 192000)]), check, "neural rejects references too short for its encoder");
        var first = NeuralOpenVoiceSession.FingerprintReferences([clip]);
        clip.Samples[10]++;
        var changed = NeuralOpenVoiceSession.FingerprintReferences([clip]);
        check(!first.SequenceEqual(changed), "neural target cache fingerprint follows PCM content rather than file dates");
        var noise = NeuralOpenVoiceSession.CreateNoise(20000);
        check(noise.SequenceEqual(NeuralOpenVoiceSession.CreateNoise(20000)), "neural explicit latent noise is reproducible");
        check(noise.All(float.IsFinite) && Math.Abs(noise.Average(x => (double)x)) < 0.04 && Math.Abs(noise.Average(x => (double)x * x) - 1) < 0.05,
            "neural explicit noise has finite roughly standard-normal moments");
        Rejects(() => NeuralOpenVoiceSession.CreateNoise(int.MaxValue), check, "neural rejects unbounded latent noise allocations");
        CheckVectorQuantization(check);
        var quiet = NeuralOpenVoiceSession.Quantize([0.25f, -0.25f]);
        check(quiet.SampleRate == 22050 && quiet.Samples.SequenceEqual(new short[] { 8192, -8192 }), "neural PCM quantization preserves moderate output levels");
        var loud = NeuralOpenVoiceSession.Quantize([2, -2]);
        check(loud.Samples.Max(x => Math.Abs((int)x)) <= 30147, "neural PCM output preserves bounded headroom");
        var tensorStorage = new float[] { 4, 0.25f, -0.25f, 2, -2, 4 };
        var tensor = new DenseTensor<float>(tensorStorage.AsMemory(1, 4), [1, 4]);
        var tensorPcm = NeuralOpenVoiceSession.QuantizeTensor(tensor, 4);
        var copiedPcm = NeuralOpenVoiceSession.Quantize(tensor.ToArray());
        Array.Fill(tensorStorage, float.NaN);
        check(tensorPcm.Samples.SequenceEqual(copiedPcm.Samples),
            "native-backed tensor slice quantizes identically and returns independent PCM before output disposal");
        Rejects(() => NeuralOpenVoiceSession.QuantizeTensor(tensor, 5), check,
            "neural tensor output validates expected frame size before consuming samples");
        Rejects(() => NeuralOpenVoiceSession.QuantizeTensor(tensor, 4), check,
            "neural tensor output still rejects nonfinite values");
        var modelTone = new PcmAudio(Enumerable.Range(0, 22050).Select(i => (short)Math.Round(15000 * Math.Sin(2 * Math.PI * 220 * i / 22050))).ToArray(), 22050);
        var utau = NeuralOpenVoiceSession.ToUtauAudio(modelTone);
        check(utau.SampleRate == UtauSpeech.OutputSampleRate && utau.Samples.Length == 44100 && utau.Samples.Max(x => Math.Abs((int)x)) <= 30147,
            "neural model PCM reaches the unchanged 44100 Hz UTAU contract with bounded headroom");
        Rejects(() => NeuralOpenVoiceSession.ToUtauAudio(new PcmAudio([1], 48000)), check, "neural output conversion rejects an unexpected model sample rate");
        Rejects(() => NeuralOpenVoiceSession.Quantize([float.NaN]), check, "neural rejects nonfinite model output");
        Rejects(() => NeuralOpenVoiceSession.Quantize([0f]), check, "neural rejects silent model output");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var cancelled = false;
        try { NeuralOpenVoiceSession.CreateNoise(20, stopped.Token); } catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "neural noise preparation observes stop requests");
    }

    private static void CheckVectorQuantization(Action<bool, string> check)
    {
        var random = new Random(6914);
        foreach (var count in new[] { 1, 7, 8, 9, 15, 16, 17, 1024, 70656 })
        {
            var input = Enumerable.Range(0, count).Select(_ => (float)(random.NextDouble() * 7.9 - 3.95)).ToArray();
            input[0] = -0f; input[^1] = 0.2f;
            var peak = 0f;
            foreach (var value in input) peak = Math.Max(peak, Math.Abs(value));
            var scale = peak > 0.92f ? 0.92f / peak : 1;
            var expected = input.Select(value => (short)Math.Round(value * scale * 32767)).ToArray();
            check(NeuralOpenVoiceSession.Quantize(input).Samples.SequenceEqual(expected),
                $"vector peak preserves scalar quantization for {count} samples including signed zero and partial vectors");
        }
        foreach (var extraPeak in new[] { 0.5f, 0.92f, 1f, 4f })
        {
            var values = Enumerable.Range(-30000, 60001).Select(n => (n + 0.5f) / 32767).Append(extraPeak).ToArray();
            var peak = values.Max(v => Math.Abs(v));
            var scale = peak > 0.92f ? 0.92f / peak : 1;
            check(NeuralOpenVoiceSession.Quantize(values).Samples.SequenceEqual(
                values.Select(v => (short)Math.Round(v * scale * 32767))),
                $"SIMD PCM rounding preserves signed half steps and normalization at peak {extraPeak}");
        }
        foreach (var gain in new[] { 1f, 0.98f, 0.49f, 0.245f })
        {
            var values = Enumerable.Range(-32000, 64001).Select(n => (n + 0.5f) / 32767).ToArray();
            check(NeuralOpenVoiceSession.QuantizeValidatedSamples(values, gain, default).SequenceEqual(
                values.Select(value => (short)Math.Round(value * gain * 32767))),
                "shared PCM rounding preserves reference gains and signed half steps");
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            foreach (var length in new[] { 1, 16, 17, 4097 })
            {
                var rejected = false;
                try { _ = NeuralOpenVoiceSession.Quantize(Enumerable.Repeat(0.2f, length).ToArray(), cancelled.Token); }
                catch (OperationCanceledException) { rejected = true; }
                check(rejected, "SIMD PCM conversion honors cancellation including a short scalar tail");
            }
        }
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var rejected = true;
            for (var at = 0; at < 2 * System.Numerics.Vector<float>.Count + 1; at++)
            {
                var values = Enumerable.Repeat(5f, 2 * System.Numerics.Vector<float>.Count + 1).ToArray();
                values[at] = invalid;
                try { _ = NeuralOpenVoiceSession.Quantize(values); rejected = false; }
                catch (InvalidDataException error) { rejected &= error.Message.StartsWith("NEURAL_OUTPUT_INVALID:", StringComparison.Ordinal); }
            }
            check(rejected, "nonfinite rejection wins over excessive level in every vector lane and scalar tail");
        }
        Rejects(() => NeuralOpenVoiceSession.Quantize(Enumerable.Repeat(4.01f, 65).ToArray()), check,
            "vector peak still rejects finite excessive model output");
    }

    private static void CheckReferenceFeaturePairs(Action<bool, string> check)
    {
        var random = new Random(203149);
        var first = new PcmAudio(Enumerable.Range(0, 4096).Select(i => (short)(8000 * Math.Sin(i * 0.04))).ToArray(), 22050);
        var second = new PcmAudio(Enumerable.Range(0, 8193).Select(_ => (short)random.Next(-16000, 16001)).ToArray(), 44100);
        var originalFirst = (short[])first.Samples.Clone(); var originalSecond = (short[])second.Samples.Clone();
        var paired = NeuralOpenVoiceSession.CreateReferenceFeaturePair(first, second, default);
        var serialFirst = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(first.Samples, first.SampleRate));
        var serialSecond = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(second.Samples, second.SampleRate));
        check(paired.SecondError is null && paired.First.Frames == serialFirst.Frames && paired.Second?.Frames == serialSecond.Frames &&
            paired.First.Magnitudes.Select(BitConverter.SingleToInt32Bits).SequenceEqual(serialFirst.Magnitudes.Select(BitConverter.SingleToInt32Bits)) &&
            paired.Second!.Magnitudes.Select(BitConverter.SingleToInt32Bits).SequenceEqual(serialSecond.Magnitudes.Select(BitConverter.SingleToInt32Bits)),
            "two-feature bounded extraction preserves serial STFT bits and input order without a native runtime");
        check(first.Samples.SequenceEqual(originalFirst) && second.Samples.SequenceEqual(originalSecond),
            "two-feature extraction never writes caller-owned reference PCM");
        var badSecond = NeuralOpenVoiceSession.CreateReferenceFeaturePair(first, new PcmAudio([1], 22050), default);
        check(badSecond.Second is null && badSecond.SecondError?.SourceException is InvalidDataException &&
            badSecond.First.Magnitudes.SequenceEqual(serialFirst.Magnitudes),
            "second feature failure is joined and retained with its original exception for rethrow after first Embed");
        Exception? firstFailure = null;
        try { NeuralOpenVoiceSession.CreateReferenceFeaturePair(new PcmAudio([1], 0), new PcmAudio([1], 22050), default); }
        catch (Exception error) { firstFailure = error; }
        Exception? serialFailure = null;
        try { _ = NeuralSpectrogram.Create(NeuralSpectrogram.Resample([1], 0)); }
        catch (Exception error) { serialFailure = error; }
        check(firstFailure is InvalidDataException && serialFailure is not null && firstFailure.GetType() == serialFailure.GetType() &&
            firstFailure.Message == serialFailure.Message,
            "first feature failure wins while the second operation is cancelled and joined without AggregateException");
        using var stop = new CancellationTokenSource(); stop.Cancel(); Exception? cancelled = null;
        try { NeuralOpenVoiceSession.CreateReferenceFeaturePair(first, second, stop.Token); }
        catch (Exception error) { cancelled = error; }
        check(cancelled is OperationCanceledException cancellation && cancellation.CancellationToken == stop.Token,
            "paired feature extraction preserves caller cancellation before starting tasks");
    }

    private static void Rejects(Action action, Action<bool, string> check, string name)
    {
        var rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, name);
    }
}
