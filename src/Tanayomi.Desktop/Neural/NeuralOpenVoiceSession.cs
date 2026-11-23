using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Tanayomi.Desktop;

/// <summary>CPU-only OpenVoice graph session, for an isolated worker process.
/// The host must verify/load the audited native runtime before construction,
/// and owns the finite wall deadline, termination and process reaping. ORT's
/// cooperative Terminate flag alone is not a process-stop guarantee.</summary>
internal sealed class NeuralOpenVoiceSession : IDisposable
{
    internal const string ReferenceSha256 = "0A0B7E0BEC15CCD2C0466F67FAA0387F3E81480C12B8B46DC295B3E8AD3F986D";
    internal const string ConverterSha256 = "A63E14E74DE2B963A8FC544BF778E28A65520525C76DAB94DD75A6DB466789CA";
    internal const int ReferenceBytes = 3258423, ConverterBytes = 128049101;
    internal const int MaximumReferences = 149, MaximumReferenceSeconds = 30, MaximumTotalReferenceSeconds = 180;
    private readonly SemaphoreSlim serial = new(1, 1);
    private InferenceSession? reference, converter;
    private byte[]? referenceKey;
    private float[]? targetEmbedding;
    private int disposed;
    private readonly bool collectTimings;
    internal bool ParallelReferenceFeatures { get; init; }
    internal bool LastReferenceCacheHit { get; private set; }
    internal bool CancellationSignalFailed { get; private set; }
    internal NeuralSessionInitializationTimings? InitializationTimings { get; }
    internal NeuralReferencePreparationTimings? LastPreparationTimings { get; private set; }
    internal NeuralConversionTimings? LastConversionTimings { get; private set; }

    // Bound inference parallelism so receiving chat and playback keep CPU
    // headroom. Four threads were measured only on an eight-logical-CPU host.
    internal static int WorkerThreads(int processors) => processors >= 8 ? 4 : processors >= 2 ? 2 : 1;
    internal static bool WorkerParallelReferenceFeatures(int processors) => processors >= 8;

    internal NeuralOpenVoiceSession(byte[] referenceOnnx, byte[] converterOnnx, int threads = 2, bool collectTimings = false)
        : this(() => ValidateModels(referenceOnnx, converterOnnx),
            options => new InferenceSession(referenceOnnx, options), options => new InferenceSession(converterOnnx, options), threads, collectTimings) { }

    internal NeuralOpenVoiceSession(NeuralVerifiedModel referenceOnnx, NeuralVerifiedModel converterOnnx, int threads = 2, bool collectTimings = false)
        : this(() => ValidateVerifiedModels(referenceOnnx, converterOnnx),
            options => referenceOnnx.CreateNativeSession(options), options => converterOnnx.CreateNativeSession(options), threads, collectTimings) { }

    internal static void ValidateVerifiedModels(NeuralVerifiedModel? referenceOnnx, NeuralVerifiedModel? converterOnnx)
    {
        ArgumentNullException.ThrowIfNull(referenceOnnx); ArgumentNullException.ThrowIfNull(converterOnnx);
        referenceOnnx.ValidateKind(NeuralModelKind.Reference); converterOnnx.ValidateKind(NeuralModelKind.Converter);
    }

    private NeuralOpenVoiceSession(Action validate, Func<SessionOptions, InferenceSession> createReference,
        Func<SessionOptions, InferenceSession> createConverter, int threads, bool collectTimings)
    {
        this.collectTimings = collectTimings;
        var started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        validate();
        var validationMs = collectTimings ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
        if (threads is < 1 or > 4) throw Invalid("NEURAL_THREAD_LIMIT");
        if (collectTimings) started = Stopwatch.GetTimestamp();
        using var options = new SessionOptions
        {
            IntraOpNumThreads = threads,
            InterOpNumThreads = 1,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            EnableCpuMemArena = false,
            EnableMemoryPattern = false,
            LogSeverityLevel = OrtLoggingLevel.ORT_LOGGING_LEVEL_ERROR
        };
        options.AppendExecutionProvider_CPU(0);
        var optionsMs = collectTimings ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
        try
        {
            if (collectTimings) started = Stopwatch.GetTimestamp();
            reference = createReference(options);
            var referenceMs = collectTimings ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
            if (collectTimings) started = Stopwatch.GetTimestamp();
            converter = createConverter(options);
            if (collectTimings) InitializationTimings = new(validationMs, optionsMs, referenceMs,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
        catch
        {
            reference?.Dispose(); reference = null;
            converter?.Dispose(); converter = null;
            throw;
        }
    }

    internal static void ValidateModels(byte[]? referenceOnnx, byte[]? converterOnnx)
    {
        if (referenceOnnx is null || converterOnnx is null || referenceOnnx.Length != ReferenceBytes || converterOnnx.Length != ConverterBytes)
            throw Invalid("NEURAL_MODEL_SIZE");
        if (!Convert.ToHexString(SHA256.HashData(referenceOnnx)).Equals(ReferenceSha256, StringComparison.Ordinal) ||
            !Convert.ToHexString(SHA256.HashData(converterOnnx)).Equals(ConverterSha256, StringComparison.Ordinal))
            throw Invalid("NEURAL_MODEL_HASH");
    }

    internal static void ValidateInputs(PcmAudio? source, IReadOnlyList<PcmAudio>? references)
    {
        ValidateSource(source);
        ValidateReferences(references);
    }

    internal static void ValidateSource(PcmAudio? source) =>
        ValidateClip(source, NeuralSpectrogram.MaximumSeconds, "NEURAL_SOURCE_LIMIT");

    internal static void ValidateReferences(IReadOnlyList<PcmAudio>? references)
    {
        if (references is null || references.Count is < 1 or > MaximumReferences)
            throw Invalid("NEURAL_REFERENCE_COUNT");
        var total = 0.0;
        foreach (var item in references)
        {
            ValidateClip(item, MaximumReferenceSeconds, "NEURAL_REFERENCE_LIMIT");
            total += (double)item.Samples.Length / item.SampleRate;
            if (total > MaximumTotalReferenceSeconds + 1e-6) throw Invalid("NEURAL_REFERENCE_TOTAL");
        }
    }

    private static void ValidateClip(PcmAudio? clip, int seconds, string errorId)
    {
        if (clip is null || clip.Samples is null || clip.SampleRate is < 8000 or > 192000 ||
            clip.Samples.Length < 1 || clip.Samples.Length > (long)clip.SampleRate * seconds ||
            Math.Round((double)clip.Samples.Length * NeuralSpectrogram.SampleRate / clip.SampleRate) < 2048)
            throw Invalid(errorId);
        if (!clip.Samples.Any(sample => sample != 0)) throw Invalid("NEURAL_INPUT_SILENT");
    }

    internal static byte[] FingerprintReferences(IReadOnlyList<PcmAudio> references, CancellationToken ct = default)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> dimensions = stackalloc byte[8];
        foreach (var item in references)
        {
            ct.ThrowIfCancellationRequested();
            BinaryPrimitives.WriteInt32LittleEndian(dimensions, item.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(dimensions[4..], item.Samples.Length);
            hash.AppendData(dimensions);
            // Supported desktop targets are little-endian; encode explicitly
            // on a different host rather than changing the cache's meaning.
            if (BitConverter.IsLittleEndian) hash.AppendData(MemoryMarshal.AsBytes(item.Samples.AsSpan()));
            else
                foreach (var sample in item.Samples)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(dimensions, sample);
                    hash.AppendData(dimensions[..2]);
                }
        }
        return hash.GetHashAndReset();
    }

    // Only the protocol reader can create these snapshots, by reading fresh
    // PCM and checking its complete fingerprint. Neither backing array escapes.
    internal sealed class OwnedReferences
    {
        private readonly IReadOnlyList<PcmAudio> clips;
        private readonly byte[] fingerprint;
        internal int Count => clips.Count;
        internal int TotalSamples { get; }

        private OwnedReferences(IReadOnlyList<PcmAudio> clips, byte[] fingerprint, int totalSamples)
        { this.clips = clips; this.fingerprint = fingerprint; TotalSamples = totalSamples; }

        internal static OwnedReferences ReadVerified(Stream input, int count, int total, ReadOnlySpan<byte> expectedKey,
            PcmAudio source, bool preparation, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (count is < 1 or > NeuralPcmProtocol.MaximumReferenceCount ||
                total is < 1 or > NeuralPcmProtocol.MaximumTotalReferenceSamples || total < count || expectedKey.Length != 32)
                throw InvalidProtocol();
            var clips = new PcmAudio[count]; var remaining = total;
            Span<byte> countBytes = stackalloc byte[4];
            for (var i = 0; i < count; i++)
            {
                ct.ThrowIfCancellationRequested();
                input.ReadExactly(countBytes); var length = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
                if (length is < 1 or > NeuralPcmProtocol.MaximumReferenceSamples || length > remaining || remaining - length < count - i - 1)
                    throw InvalidProtocol();
                clips[i] = new(NeuralPcmProtocol.ReadSamples(input, length), NeuralPcmProtocol.ReferenceRate);
                remaining -= length;
            }
            if (remaining != 0) throw InvalidProtocol();
            if (preparation) NeuralPcmProtocol.ValidateReferenceRequest(clips); else NeuralPcmProtocol.ValidateRequest(source, clips);
            var fingerprint = FingerprintReferences(clips, ct);
            if (!fingerprint.AsSpan().SequenceEqual(expectedKey)) throw InvalidProtocol();
            ct.ThrowIfCancellationRequested();
            return new(Array.AsReadOnly(clips), fingerprint, total);
        }

        internal bool Matches(ReadOnlySpan<byte> key, int count, int total)
            => Count == count && TotalSamples == total && fingerprint.AsSpan().SequenceEqual(key);

        internal void Prepare(NeuralOpenVoiceSession session, CancellationToken ct = default)
            => session.PrepareReferencesEntry(clips, fingerprint, ct);

        internal PcmAudio Convert(NeuralOpenVoiceSession session, PcmAudio source, CancellationToken ct = default)
            => session.ConvertAudioCore(source, clips, fingerprint, ct);

        // Explicit diagnostic/test copy; normal worker/session paths never call it.
        internal IReadOnlyList<PcmAudio> CopyForInspection()
            => Array.AsReadOnly(clips.Select(clip => new PcmAudio((short[])clip.Samples.Clone(), clip.SampleRate)).ToArray());

        private static InvalidDataException InvalidProtocol()
            => new("NEURAL_PROTOCOL: 声質変換ワーカーの形式または上限が不正です。");
    }

    internal PcmAudio ConvertAudio(PcmAudio source, IReadOnlyList<PcmAudio> references, CancellationToken ct = default)
        => ConvertAudioCore(source, references, null, ct);

    private PcmAudio ConvertAudioCore(PcmAudio source, IReadOnlyList<PcmAudio> references, byte[]? verifiedFingerprint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateInputs(source, references);
        serial.Wait(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            if (collectTimings) LastConversionTimings = null;
            ct.ThrowIfCancellationRequested();
            var stage = collectTimings ? Stopwatch.GetTimestamp() : 0;
            double Mark()
            {
                if (!collectTimings) return 0;
                var now = Stopwatch.GetTimestamp();
                var elapsed = Stopwatch.GetElapsedTime(stage, now).TotalMilliseconds;
                stage = now; return elapsed;
            }
            PrepareReferencesCore(references, ct, verifiedFingerprint);
            var preparationMs = Mark();
            var spectrum = NeuralSpectrogram.Create(NeuralSpectrogram.Resample(source.Samples, source.SampleRate, ct), ct);
            var featuresMs = Mark();
            var sourceEmbedding = Embed(spectrum, ct);
            var encoderMs = Mark();
            var noise = CreateNoise(checked(192 * spectrum.Frames), ct);
            var inputs = new[]
            {
                Value("spectrum", spectrum.Magnitudes, [1, 513, spectrum.Frames]),
                Value("source_embedding", sourceEmbedding, [1, 256, 1]),
                Value("target_embedding", targetEmbedding!, [1, 256, 1]),
                Value("noise", noise, [1, 192, spectrum.Frames])
            };
            var inputsMs = Mark();
            var expected = checked(spectrum.Frames * NeuralSpectrogram.HopLength);
            double converterMs = 0;
            var result = Run(converter!, inputs, "pcm", ct, converted =>
            {
                converterMs = Mark();
                return QuantizeTensor(converted, expected, ct);
            });
            if (collectTimings) LastConversionTimings = new(preparationMs, featuresMs, encoderMs,
                inputsMs, converterMs, Mark());
            return result;
        }
        finally { serial.Release(); }
    }

    internal void PrepareReferences(IReadOnlyList<PcmAudio> references, CancellationToken ct = default)
        => PrepareReferencesEntry(references, null, ct);

    private void PrepareReferencesEntry(IReadOnlyList<PcmAudio> references, byte[]? verifiedFingerprint, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        ValidateReferences(references); serial.Wait(ct);
        try
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
            PrepareReferencesCore(references, ct, verifiedFingerprint);
        }
        finally { serial.Release(); }
    }

    private void PrepareReferencesCore(IReadOnlyList<PcmAudio> references, CancellationToken ct, byte[]? verifiedFingerprint)
    {
        if (collectTimings) LastPreparationTimings = null;
        var started = collectTimings ? Stopwatch.GetTimestamp() : 0;
        ct.ThrowIfCancellationRequested();
        var key = verifiedFingerprint ?? FingerprintReferences(references, ct);
        var fingerprintMs = collectTimings ? Stopwatch.GetElapsedTime(started).TotalMilliseconds : 0;
        LastReferenceCacheHit = referenceKey is not null && key.AsSpan().SequenceEqual(referenceKey);
        if (LastReferenceCacheHit)
        {
            if (collectTimings) LastPreparationTimings = new(references.Count, references.Sum(item => (long)item.Samples.Length),
                fingerprintMs, 0, 0, true);
            return;
        }
        var accumulated = new double[256];
        var featureMs = 0.0; var encoderMs = 0.0;
        for (var index = 0; index < references.Count; index++)
        {
            ct.ThrowIfCancellationRequested();
            if (collectTimings) started = Stopwatch.GetTimestamp();
            if (ParallelReferenceFeatures && index + 1 < references.Count)
            {
                // Input clips are caller-owned and must remain unchanged during
                // this call, as in the serial path. Worker/test fixtures own them.
                var pair = CreateReferenceFeaturePair(references[index], references[index + 1], ct);
                if (collectTimings) featureMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Accumulate(pair.First);
                // Preserve original exception priority: first Embed precedes a
                // failure preparing the second clip. Both tasks are joined now.
                pair.SecondError?.Throw();
                Accumulate(pair.Second!);
                index++;
            }
            else
            {
                var spectrum = CreateReferenceFeature(references[index], ct);
                if (collectTimings) featureMs += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                Accumulate(spectrum);
            }
        }
        void Accumulate(NeuralSpectrogram.Spectrum spectrum)
        {
            var encoderStarted = collectTimings ? Stopwatch.GetTimestamp() : 0;
            var values = Embed(spectrum, ct);
            if (collectTimings) encoderMs += Stopwatch.GetElapsedTime(encoderStarted).TotalMilliseconds;
            for (var i = 0; i < values.Length; ++i) accumulated[i] += values[i];
        }
        var embedding = new float[256];
        for (var i = 0; i < embedding.Length; ++i) embedding[i] = (float)(accumulated[i] / references.Count);
        targetEmbedding = embedding; referenceKey = key;
        if (collectTimings) LastPreparationTimings = new(references.Count, references.Sum(item => (long)item.Samples.Length),
            fingerprintMs, featureMs, encoderMs, false);
    }

    internal sealed record ReferenceFeaturePair(NeuralSpectrogram.Spectrum First, NeuralSpectrogram.Spectrum? Second,
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? SecondError);

    private static NeuralSpectrogram.Spectrum CreateReferenceFeature(PcmAudio clip, CancellationToken ct)
        => NeuralSpectrogram.Create(NeuralSpectrogram.Resample(clip.Samples, clip.SampleRate, ct), ct);

    // One background feature operation, one on this thread; never more than
    // two clips resident. No native work overlaps a feature task. Exceptions
    // retain original types, and every exit joins the task before returning.
    internal static ReferenceFeaturePair CreateReferenceFeaturePair(PcmAudio first, PcmAudio second, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var pairCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = Task.Run(() => CreateReferenceFeature(second, pairCancellation.Token));
        NeuralSpectrogram.Spectrum firstSpectrum;
        try { firstSpectrum = CreateReferenceFeature(first, pairCancellation.Token); }
        catch
        {
            pairCancellation.Cancel();
            try { _ = pending.GetAwaiter().GetResult(); } catch (Exception) { /* Observe and preserve first failure. */ }
            ct.ThrowIfCancellationRequested();
            throw;
        }
        NeuralSpectrogram.Spectrum? secondSpectrum = null;
        System.Runtime.ExceptionServices.ExceptionDispatchInfo? secondError = null;
        try { secondSpectrum = pending.GetAwaiter().GetResult(); }
        catch (Exception error) { secondError = System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error); }
        ct.ThrowIfCancellationRequested();
        return new(firstSpectrum, secondSpectrum, secondError);
    }

    private float[] Embed(NeuralSpectrogram.Spectrum spectrum, CancellationToken ct)
    {
        var embedding = Run(reference!, [Value("spectrum", spectrum.Magnitudes, spectrum.Shape)], "embedding", ct);
        if (embedding.Length != 256 || embedding.Any(value => !float.IsFinite(value))) throw Invalid("NEURAL_EMBEDDING_INVALID");
        return embedding;
    }

    private static NamedOnnxValue Value(string name, float[] values, int[] shape)
        => NamedOnnxValue.CreateFromTensor(name, new DenseTensor<float>(values, shape));

    private float[] Run(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, string outputName, CancellationToken ct)
        => Run(session, inputs, outputName, ct, tensor => tensor.ToArray());

    private T Run<T>(InferenceSession session, IReadOnlyCollection<NamedOnnxValue> inputs, string outputName,
        CancellationToken ct, Func<Tensor<float>, T> consume)
    {
        ct.ThrowIfCancellationRequested();
        using var options = new RunOptions();
        using var registration = ct.Register(() =>
        {
            try { options.Terminate = true; }
            catch (Exception) { CancellationSignalFailed = true; } // Never throw across cancellation callbacks.
        });
        try
        {
            using var output = session.Run(inputs, [outputName], options);
            ct.ThrowIfCancellationRequested();
            // Consume native-backed memory while the owning output is alive.
            var result = consume(output.Single().AsTensor<float>());
            ct.ThrowIfCancellationRequested();
            return result;
        }
        catch (OnnxRuntimeException) when (ct.IsCancellationRequested)
        { throw new OperationCanceledException(ct); }
    }

    internal static float[] CreateNoise(int count, CancellationToken ct = default)
    {
        if (count is < 1 or > 192 * (NeuralSpectrogram.SampleRate * NeuralSpectrogram.MaximumSeconds / NeuralSpectrogram.HopLength))
            throw Invalid("NEURAL_NOISE_LIMIT");
        var result = new float[count];
        uint state = 0x12345678;
        double Uniform()
        {
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            return (state + 0.5) / 4294967296.0;
        }
        for (var i = 0; i < count; i += 2)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            var radius = Math.Sqrt(-2 * Math.Log(Uniform()));
            var angle = 2 * Math.PI * Uniform();
            result[i] = (float)(radius * Math.Cos(angle));
            if (i + 1 < count) result[i + 1] = (float)(radius * Math.Sin(angle));
        }
        return result;
    }

    internal static PcmAudio Quantize(float[] converted, CancellationToken ct = default)
        => QuantizeAtRate(converted, NeuralSpectrogram.SampleRate, ct);

    internal static PcmAudio QuantizeTensor(Tensor<float> converted, int expected, CancellationToken ct = default)
    {
        if (converted.Length != expected || expected < 1 ||
            expected > NeuralSpectrogram.SampleRate * NeuralSpectrogram.MaximumSeconds)
            throw Invalid("NEURAL_OUTPUT_SIZE");
        return converted is DenseTensor<float> dense && !dense.IsReversedStride
            ? QuantizeAtRate(dense.Buffer.Span, NeuralSpectrogram.SampleRate, ct)
            : Quantize(converted.ToArray(), ct);
    }

    internal static PcmAudio ToUtauAudio(PcmAudio audio, CancellationToken ct = default)
    {
        if (audio is null || audio.Samples is null || audio.SampleRate != NeuralSpectrogram.SampleRate)
            throw Invalid("NEURAL_OUTPUT_RATE");
        return QuantizeAtRate(NeuralSpectrogram.ResampleTo(audio.Samples, audio.SampleRate, UtauSpeech.OutputSampleRate, ct),
            UtauSpeech.OutputSampleRate, ct);
    }

    private static PcmAudio QuantizeAtRate(ReadOnlySpan<float> converted, int rate, CancellationToken ct)
    {
        if (converted.Length < 1 || converted.Length > rate * NeuralSpectrogram.MaximumSeconds)
            throw Invalid("NEURAL_OUTPUT_SIZE");
        var peak = 0f;
        var index = 0;
        if (Vector.IsHardwareAccelerated && converted.Length >= Vector<float>.Count)
        {
            var maxima = Vector<float>.Zero;
            var finiteLimit = new Vector<float>(float.MaxValue);
            for (; index <= converted.Length - Vector<float>.Count; index += Vector<float>.Count)
            {
                var absolute = Vector.Abs(new Vector<float>(converted.Slice(index)));
                // Ordered comparison also rejects NaN, so it cannot disappear
                // behind a SIMD maximum. Infinity is rejected before level checks.
                if (!Vector.LessThanOrEqualAll(absolute, finiteLimit)) throw Invalid("NEURAL_OUTPUT_INVALID");
                maxima = Vector.Max(maxima, absolute);
            }
            for (var lane = 0; lane < Vector<float>.Count; lane++) peak = Math.Max(peak, maxima[lane]);
        }
        for (; index < converted.Length; index++)
        {
            var sample = converted[index];
            if (!float.IsFinite(sample)) throw Invalid("NEURAL_OUTPUT_INVALID");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak is <= 0.00001f or > 4) throw Invalid("NEURAL_OUTPUT_LEVEL");
        var scale = peak > 0.92f ? 0.92f / peak : 1;
        return new PcmAudio(QuantizeValidatedSamples(converted, scale, ct), rate);
    }

    // Callers validate finite samples and choose a gain that keeps PCM in range.
    // Reference and generated audio retain their distinct level policies.
    internal static short[] QuantizeValidatedSamples(ReadOnlySpan<float> converted, float scale, CancellationToken ct)
    {
        var result = new short[converted.Length];
        var i = 0;
        if (Avx2.IsSupported)
        {
            var scaleVector = Vector256.Create(scale);
            var pcmScale = Vector256.Create(32767f);
            ref var input = ref MemoryMarshal.GetReference(converted);
            ref var output = ref MemoryMarshal.GetReference(result.AsSpan());
            for (; i <= result.Length - 16; i += 16)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                // Two float multiplications, then fixed ties-to-even rounding,
                // independent of the thread's floating-point rounding mode.
                var first = Avx.RoundToNearestInteger(Avx.Multiply(
                    Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)i), scaleVector), pcmScale));
                var second = Avx.RoundToNearestInteger(Avx.Multiply(
                    Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)(i + 8)), scaleVector), pcmScale));
                var packed = Avx2.PackSignedSaturate(Avx.ConvertToVector256Int32WithTruncation(first),
                    Avx.ConvertToVector256Int32WithTruncation(second));
                // Pack works in 128-bit lanes. Restore sequential sample order.
                Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsInt16().StoreUnsafe(ref output, (nuint)i);
            }
        }
        for (; i < result.Length; ++i)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            result[i] = (short)Math.Round(converted[i] * scale * 32767);
        }
        return result;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        serial.Wait();
        try
        {
            reference?.Dispose(); reference = null;
            converter?.Dispose(); converter = null;
            targetEmbedding = null; referenceKey = null;
        }
        finally { serial.Release(); }
    }

    private static InvalidDataException Invalid(string errorId) => new(errorId + ": ニューラル音声の形式または上限が不正です。");
}

// Collected only by an explicitly opted-in native audit. They do not create
// files, product logs or user-facing controls, and do not change inference.
internal sealed record NeuralSessionInitializationTimings(double ModelValidationMilliseconds,
    double OptionsMilliseconds, double ReferenceSessionMilliseconds, double ConverterSessionMilliseconds);
internal sealed record NeuralConversionTimings(double ReferenceMilliseconds, double SourceFeaturesMilliseconds,
    double SourceEncoderMilliseconds, double NoiseAndInputsMilliseconds, double ConverterMilliseconds,
    double QuantizeMilliseconds);
internal sealed record NeuralReferencePreparationTimings(int ReferenceCount, long ReferenceSamples,
    double FingerprintMilliseconds, double FeaturesMilliseconds, double EncoderMilliseconds, bool CacheHit);
