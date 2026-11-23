using System.Buffers.Binary;

namespace Tanayomi.Desktop;

/// <summary>Persistent, single-request-at-a-time native host. Only this worker
/// loads WORLD; its owner enforces deadlines and kills it on cancellation.
/// Exit codes: 0 clean EOF, 2 invalid request, 3 truncated request, 4 native
/// failure, 5 other runtime/pipe failure. No fatal error is followed by another frame.</summary>
internal static class WorldPcmWorker
{
    internal static int RunWorker(string[] args)
    {
        try
        {
            WorldComparison.ConfigureWorker();
            if (args.Length is < 1 or > 2 || args[0] != "--world-pcm-worker" || args.Length == 2 && args[1] != "--dio") return 2;
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            WorldComparison.NativeApi? native = null;
            try
            {
                while (WorldPcmProtocol.ReadRequest(input) is { } request)
                {
                    if (native is null)
                    {
                        var path = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
                        WorldComparison.RejectLinks(path);
                        native = new WorldComparison.NativeApi(path);
                    }
                    Process(request, output, native, args.Length == 2);
                }
                return 0; // EOF exactly between requests is the normal shutdown.
            }
            finally { native?.Dispose(); }
        }
        catch (EndOfStreamException) { return 3; }
        catch (InvalidDataException) { return 2; }
        catch (WorldPcmNativeException) { return 4; }
        catch (DllNotFoundException) { return 4; }
        catch (EntryPointNotFoundException) { return 4; }
        catch (BadImageFormatException) { return 4; }
        catch (Exception) { return 5; }
        // No exception text, paths, PCM or diagnostics are written to either pipe.
    }

    internal static void Process(WorldPcmProtocol.Request request, Stream output, WorldComparison.NativeApi native, bool fast)
    {
        var samples = new double[request.SampleCount];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(request.Pcm.AsSpan(i * 2, 2)) / 32768.0;
        Check(native.Layout(samples.Length, out var frames, out var fft));
        if (frames is < 2 or > 9001 || fft != 2048 ||
            frames != (int)(1000.0 * samples.Length / WorldPcmProtocol.SampleRate / 5.0) + 1)
            throw new WorldPcmNativeException();
        var cells = checked(frames * (fft / 2 + 1));
        var f0 = new double[frames]; var times = new double[frames];
        double[]? spectrum = null, aperiodicity = null;
        if (request.TargetF0 == 0 && (fast ? native.AnalyzeFastF0 : native.AnalyzeF0) is { } analyzeF0)
            Check(analyzeF0(samples, samples.Length, f0, frames, times, frames));
        else
        {
            // Missing optional export alone selects the original path. A failed
            // new export remains a native failure, never a silent retry.
            spectrum = new double[cells]; aperiodicity = new double[cells];
            // The optional implementation overlaps only the independent SP/AP
            // stages and joins both before returning. Keep smaller CPUs and
            // older audited libraries on the original serial implementation.
            var parallel = Environment.ProcessorCount >= 8
                ? fast ? native.AnalyzeFastParallel : native.AnalyzeParallel
                : null;
            Check((parallel ?? (fast ? native.AnalyzeFast : native.Analyze))(samples, samples.Length, f0, frames, times, frames,
                spectrum, cells, aperiodicity, cells, fft));
        }
        foreach (var value in f0)
            if (!double.IsFinite(value) || value != 0 && (value < 40 || value > 1200))
                throw new WorldPcmNativeException();
        var voiced = f0.Where(value => value > 0).Order().ToArray();
        if (voiced.Length == 0 && request.TargetF0 > 0)
        {
            WorldPcmProtocol.WriteResponse(output, WorldPcmProtocol.Success, 0, request.Pcm);
            return;
        }
        if (voiced.Length < 10)
        {
            WorldPcmProtocol.WriteResponse(output, WorldPcmProtocol.InsufficientVoicing, 0, []);
            return;
        }
        var median = (voiced[(voiced.Length - 1) / 2] + voiced[voiced.Length / 2]) / 2;
        if (request.TargetF0 == 0)
        {
            WorldPcmProtocol.WriteResponse(output, WorldPcmProtocol.Success, median, []);
            return;
        }
        // Preserve the detected voiced/unvoiced mask and all spectral/AP parameters.
        if (request.Contour is null)
        {
            // Keep the v1 flat path and its numerical operations unchanged.
            for (var i = 0; i < f0.Length; i++) if (f0[i] > 0) f0[i] = request.TargetF0;
        }
        else ApplyContextContour(f0, request.Contour, samples.Length, request.TargetF0);
        // Analysis is synchronous and retains no input pointer. After the
        // contour is applied only this length is needed; fixed WORLD Synthesis
        // initializes every output sample before accumulation. Reuse this
        // worker-owned buffer, never the request PCM or analysis parameters.
        var rendered = samples;
        Check(native.Synthesize(f0, frames, spectrum!, cells, aperiodicity!, cells, fft, rendered, rendered.Length));
        var peak = 0.0;
        foreach (var sample in rendered)
        {
            if (!double.IsFinite(sample)) throw new WorldPcmNativeException();
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak < 1e-10) throw new WorldPcmNativeException();
        var gain = Math.Min(1.0, 0.92 / peak);
        var pcm = new byte[checked(samples.Length * 2)];
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2, 2),
                (short)Math.Clamp((int)Math.Round(rendered[i] * gain * 32767), short.MinValue, short.MaxValue));
        WorldPcmProtocol.WriteResponse(output, WorldPcmProtocol.Success, request.TargetF0, pcm);
    }

    // A positive donor curve does not supply a voiced/unvoiced decision. Only
    // native WORLD voicing contributes to its time-weighted mean or output.
    internal static void ApplyContextContour(double[] detectedF0, double[] contour, int sampleCount, double target)
    {
        WorldPcmProtocol.ValidateContour(sampleCount, target, contour);
        if (detectedF0.Length != contour.Length) throw new InvalidDataException();
        var count = 0; double total = 0, deviation = 0, baseline = 0;
        for (var i = 0; i < detectedF0.Length; ++i)
        {
            if (!double.IsFinite(detectedF0[i]) || detectedF0[i] != 0 && detectedF0[i] is not (>= 40 and <= 1200))
                throw new InvalidDataException();
            if (detectedF0[i] == 0) continue;
            if (count++ == 0) baseline = contour[i];
            var weight = Math.Min(0.005, Math.Max(0, sampleCount / (double)WorldPcmProtocol.SampleRate - i * 0.005));
            total += weight; deviation += (contour[i] - baseline) * weight;
        }
        // Process handles complete unvoicing/passthrough and short-voiced status
        // before calling here. Do not invent a contour from too little evidence.
        if (count < 10 || total <= 0) throw new InvalidDataException();
        var mean = baseline + deviation / total;
        if (!double.IsFinite(mean) || mean <= 0) throw new InvalidDataException();
        var scale = target / mean;
        var result = new double[detectedF0.Length];
        for (var i = 0; i < result.Length; ++i)
        {
            if (detectedF0[i] == 0) continue;
            result[i] = contour[i] * scale;
            // Tolerate only arithmetic roundoff at the supported endpoints.
            if (Math.Abs(result[i] - 40) <= 1e-10) result[i] = 40;
            if (Math.Abs(result[i] - 1200) <= 1e-10) result[i] = 1200;
            if (!double.IsFinite(result[i]) || result[i] is < 40 or > 1200) throw new InvalidDataException();
        }
        result.CopyTo(detectedF0, 0);
    }

    private static void Check(int status)
    {
        if (status != 0) throw new WorldPcmNativeException();
    }
    private sealed class WorldPcmNativeException : Exception { }
}

/// <summary>Version 1, little endian. Request: TWPC/u32 version/i32 sample count/
/// f64 target Hz, then count PCM16 samples at 44100 Hz mono. Target 0 analyzes only.
/// V2 adds i32 contour count to the request header, then PCM16 and f64 contour Hz.
/// All contour frames are positive; WORLD supplies VUV. Count must exactly match
/// WORLD's 5ms layout and target 0 is forbidden. Responses remain v1.
/// Response: TWPR/u32 version/i32 status/i32 sample count/f64 Hz, then PCM16.
/// Success analysis has count 0 and median Hz; synthesis has input count and target
/// Hz (0 for unvoiced passthrough). Status 1 has count/Hz 0. Invalid framing is fatal.</summary>
internal static class WorldPcmProtocol
{
    internal const int SampleRate = 44100;
    internal const int MinimumSamples = SampleRate / 10;
    internal const int MaximumSamples = SampleRate * 45;
    internal const int RequestHeaderBytes = 20;
    internal const int ContextRequestHeaderBytes = 24;
    internal const int ResponseHeaderBytes = 24;
    internal const uint Version = 1;
    internal const uint ContextVersion = 2;
    internal const int Success = 0;
    internal const int InsufficientVoicing = 1;
    internal sealed record Request(int SampleCount, double TargetF0, byte[] Pcm, double[]? Contour = null);

    internal static byte[] EncodeRequest(PcmAudio audio, double target)
        => EncodeRequest(audio, target, null);
    internal static byte[] EncodeRequest(PcmAudio audio, double target, double[]? contour)
    {
        if (audio.SampleRate != SampleRate || audio.Samples.Length is < MinimumSamples or > MaximumSamples || !ValidF0(target))
            throw new InvalidDataException("音程処理の要求が不正です。");
        if (contour is not null) ValidateContour(audio.Samples.Length, target, contour);
        var headerBytes = contour is null ? RequestHeaderBytes : ContextRequestHeaderBytes;
        var bytes = new byte[checked(headerBytes + audio.Samples.Length * 2 + (contour?.Length ?? 0) * 8)];
        "TWPC"u8.CopyTo(bytes);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4), contour is null ? Version : ContextVersion);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(8), audio.Samples.Length);
        BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(12), target);
        if (contour is not null) BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), contour.Length);
        for (var i = 0; i < audio.Samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(headerBytes + i * 2, 2), audio.Samples[i]);
        if (contour is not null)
            for (var i = 0; i < contour.Length; ++i)
                BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(headerBytes + audio.Samples.Length * 2 + i * 8), contour[i]);
        return bytes;
    }

    internal static async Task<(PcmAudio? Audio, double F0)> ReadResponseAsync(Stream input, int expectedCount,
        double target, CancellationToken ct)
    {
        if (expectedCount is < MinimumSamples or > MaximumSamples || !ValidF0(target)) throw new InvalidDataException();
        var header = new byte[ResponseHeaderBytes];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var f0 = BinaryPrimitives.ReadDoubleLittleEndian(header.AsSpan(16));
        if (!header.AsSpan(0, 4).SequenceEqual("TWPR"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != Version || status != Success ||
            count != (target == 0 ? 0 : expectedCount) || !ValidF0(f0) ||
            (target == 0 ? f0 == 0 : f0 != 0 && f0 != target))
            throw new InvalidDataException("音程処理ワーカーの応答が不正です。");
        if (target == 0) return (null, f0);
        var body = new byte[checked(count * 2)];
        await input.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        var samples = new short[count];
        for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(body.AsSpan(i * 2, 2));
        return (new PcmAudio(samples, SampleRate), f0);
    }

    internal static Request? ReadRequest(Stream input)
    {
        Span<byte> header = stackalloc byte[RequestHeaderBytes];
        var first = input.ReadByte();
        if (first < 0) return null;
        header[0] = (byte)first;
        input.ReadExactly(header[1..]);
        var version = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        if (!header[..4].SequenceEqual("TWPC"u8) || version != Version && version != ContextVersion) throw new InvalidDataException();
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var target = BinaryPrimitives.ReadDoubleLittleEndian(header[12..]);
        if (count is < MinimumSamples or > MaximumSamples || !ValidF0(target)) throw new InvalidDataException();
        var contourCount = 0;
        if (version == ContextVersion)
        {
            Span<byte> additional = stackalloc byte[4]; input.ReadExactly(additional);
            contourCount = BinaryPrimitives.ReadInt32LittleEndian(additional);
            if (target == 0 || contourCount != ContourFrames(count) || contourCount is < 2 or > 9001)
                throw new InvalidDataException();
        }
        // Only a validated, bounded count may allocate or consume a body.
        var pcm = new byte[checked(count * 2)];
        input.ReadExactly(pcm);
        double[]? contour = null;
        if (version == ContextVersion)
        {
            contour = new double[contourCount];
            Span<byte> value = stackalloc byte[8];
            for (var i = 0; i < contour.Length; ++i)
            {
                input.ReadExactly(value); contour[i] = BinaryPrimitives.ReadDoubleLittleEndian(value);
                if (!double.IsFinite(contour[i]) || contour[i] is < 40 or > 1200) throw new InvalidDataException();
            }
        }
        return new Request(count, target, pcm, contour);
    }

    internal static int ContourFrames(int sampleCount)
    {
        if (sampleCount is < MinimumSamples or > MaximumSamples) throw new InvalidDataException();
        return (int)(1000.0 * sampleCount / SampleRate / 5.0) + 1;
    }
    internal static void ValidateContour(int sampleCount, double target, double[] contour)
    {
        if (!ValidF0(target) || target == 0 || contour is null || contour.Length != ContourFrames(sampleCount) ||
            contour.Length is < 2 or > 9001 || contour.Any(value => !double.IsFinite(value) || value is < 40 or > 1200))
            throw new InvalidDataException("文脈音程曲線が不正です。");
    }

    internal static void WriteResponse(Stream output, int status, double f0, ReadOnlySpan<byte> pcm)
    {
        var count = pcm.Length / 2;
        if ((pcm.Length & 1) != 0 || !ValidF0(f0) ||
            status != Success && status != InsufficientVoicing ||
            status == InsufficientVoicing && (pcm.Length != 0 || f0 != 0) ||
            status == Success && (count == 0 ? f0 == 0 : count is < MinimumSamples or > MaximumSamples))
            throw new InvalidDataException();
        Span<byte> header = stackalloc byte[ResponseHeaderBytes];
        "TWPR"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], count);
        BinaryPrimitives.WriteDoubleLittleEndian(header[16..], f0);
        output.Write(header);
        output.Write(pcm);
        output.Flush();
    }

    private static bool ValidF0(double f0) => double.IsFinite(f0) && (f0 == 0 || f0 is >= 40 and <= 1200);
}
