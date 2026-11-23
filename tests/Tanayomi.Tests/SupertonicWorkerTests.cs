using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Tanayomi;
using Tanayomi.Desktop;

internal static class SupertonicWorkerTests
{
    internal static async Task RunProductAsync(string executable, Action<bool, string> check)
    {
        executable = Path.GetFullPath(executable);
        var tempRoot = Path.Combine(Path.GetDirectoryName(executable)!, "data", "tmp");
        var existing = WorkerDirectories().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var output = new PcmProbe();
        string[] CreatedDirectories() => WorkerDirectories().Where(path => !existing.Contains(path)).ToArray();
        string[] WorkerDirectories() => Directory.Exists(tempRoot)
            ? Directory.GetDirectories(tempRoot, "st-*", SearchOption.TopDirectoryOnly) : [];

        await using (var speech = new SupertonicSpeech(output, executable))
        {
            check(speech.Voices.Count == 10, "Supertonic product exposes ten bundled speakers");
            using var cancel = new CancellationTokenSource();
            var interrupted = speech.SpeakAsync("日本語読み上げを途中で中止します。", cancel.Token);
            var cancelledDirectories = CreatedDirectories();
            cancel.Cancel();
            var observedCancellation = false;
            try { await interrupted; }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { observedCancellation = true; }
            check(observedCancellation && output.Calls == 0 && cancelledDirectories.Length == 1 &&
                cancelledDirectories.All(path => !Directory.Exists(path)),
                "Supertonic cancels cold synthesis, reaps the worker and removes its temporary directory");

            await speech.PrepareAsync(default);
            var restartedDirectories = CreatedDirectories();
            check(restartedDirectories.Length == 1 && !cancelledDirectories.Contains(restartedDirectories[0]),
                "Supertonic restarts in a fresh temporary directory after cancellation");
            foreach (var voice in speech.Voices)
            {
                speech.VoiceId = voice.Id;
                var before = output.Calls;
                await speech.SpeakAsync("こんにちは。", default);
                check(output.Calls == before + 1 && output.SampleRate == 44100 && output.HasAudio,
                    $"Supertonic product returns non-silent 44.1kHz Japanese PCM: {voice.Id}");
            }
        }
        check(!output.Disposed && CreatedDirectories().Length == 0,
            "Supertonic stops its worker, removes its temporary directory and keeps borrowed PCM alive");
    }

    private sealed class PcmProbe : IPcmOutput
    {
        internal int Calls { get; private set; }
        internal int SampleRate { get; private set; }
        internal bool HasAudio { get; private set; }
        internal bool Disposed { get; private set; }

        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            SampleRate = sampleRate;
            HasAudio = samples.Length <= SupertonicProtocol.MaximumSamples && samples.Span.ContainsAnyExcept((short)0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        CheckNativePcm(check);
        const string text = "こんにちは、配信の読み上げです。";
        using var requests = new MemoryStream();
        requests.Write(SupertonicProtocol.Encode(0, null));
        for (var speaker = 0; speaker < 10; speaker++) requests.Write(SupertonicProtocol.Encode(speaker, text));
        requests.Position = 0;
        using var responses = new MemoryStream();
        var calls = new List<int>();
        var exit = SupertonicPcmWorker.RunRequests(requests, responses, () => { }, (speaker, message) =>
        {
            if (message != text) throw new InvalidDataException();
            calls.Add(speaker);
            return [short.MinValue, (short)speaker, short.MaxValue];
        });
        responses.Position = 0;
        var preparation = await SupertonicProtocol.ReadResponseAsync(responses, preparation: true, default);
        check(exit == 0 && preparation.Samples.Length == 0 &&
            calls.SequenceEqual(Enumerable.Range(0, 10)), "Supertonic dispatch accepts all ten speakers and preparation");
        for (var speaker = 0; speaker < 10; speaker++)
        {
            var audio = await SupertonicProtocol.ReadResponseAsync(responses, preparation: false, default);
            check(audio.SampleRate == 44100 && audio.Samples.SequenceEqual(
                new[] { short.MinValue, (short)speaker, short.MaxValue }), "Supertonic transport preserves 44.1kHz PCM");
        }
        check(responses.Position == responses.Length, "Supertonic transport consumes consecutive responses exactly");

        check(Rejects(() => SupertonicProtocol.Encode(10, text), typeof(InvalidDataException)) &&
            Rejects(() => SupertonicProtocol.Encode(0, new string('あ', 700)), typeof(InvalidDataException)) &&
            Rejects(() => SupertonicProtocol.Encode(0, "abc\0def"), typeof(InvalidDataException)),
            "Supertonic request rejects invalid speaker, embedded NUL and oversized UTF-8");
        var truncated = SupertonicProtocol.Encode(0, text)[..^1];
        var malformed = SupertonicProtocol.Encode(0, "x");
        malformed[^1] = 0xff;
        using var malformedStream = new MemoryStream(malformed);
        using var truncatedStream = new MemoryStream(truncated);
        check(Rejects(() => SupertonicProtocol.ReadRequest(truncatedStream), typeof(EndOfStreamException)) &&
            Rejects(() => SupertonicProtocol.ReadRequest(malformedStream), typeof(DecoderFallbackException)),
            "Supertonic request rejects truncated or invalid UTF-8");

        requests.Position = 0;
        responses.SetLength(0);
        check(SupertonicPcmWorker.RunRequests(requests, responses,
                () => throw new IOException("private input"), (_, _) => []) == 4,
            "Supertonic worker reports initialization failure through a fixed status");
        responses.Position = 0;
        try
        {
            _ = await SupertonicProtocol.ReadResponseAsync(responses, preparation: true, default);
            check(false, "Supertonic rejects a failed preparation");
        }
        catch (SupertonicWorkerException error)
        {
            check(!error.Message.Contains("private input"), "Supertonic error does not expose underlying exception text");
        }
        var badHeader = new byte[SupertonicProtocol.HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(badHeader.AsSpan(4), SupertonicProtocol.MaximumSamples + 1);
        using var invalidResponse = new MemoryStream(badHeader);
        try
        {
            _ = await SupertonicProtocol.ReadResponseAsync(invalidResponse, preparation: false, default);
            check(false, "Supertonic rejects a hostile PCM length");
        }
        catch (InvalidDataException)
        {
            check(invalidResponse.Position == badHeader.Length, "Supertonic rejects hostile PCM lengths before reading a body");
        }

        await BundledPcmResponseTests.CheckAsync("Supertonic", SupertonicProtocol.SampleRate, SupertonicProtocol.MaximumSamples,
            (stream, samples) => SupertonicProtocol.WriteResponse(stream, 0, samples ?? []),
            async (stream, preparation, ct) => await SupertonicProtocol.ReadResponseAsync(stream, preparation, ct), check);
    }

    private static void CheckNativePcm(Action<bool, string> check)
    {
        const int chunk = 8192;
        const int rate = SupertonicProtocol.SampleRate;
        const int maximum = SupertonicProtocol.MaximumSamples;
        float[] values = [-2f, -1f, -32767.5f / 32768f, -2.5f / 32768f, -1.5f / 32768f,
            -0.5f / 32768f, BitConverter.Int32BitsToSingle(int.MinValue), 0f, 0.5f / 32768f,
            1.5f / 32768f, 2.5f / 32768f, 32766.5f / 32768f, 32767.5f / 32768f, 1f, 2f];
        var source = Marshal.AllocHGlobal(maximum * sizeof(float));
        try
        {
            foreach (var length in new[] { 1, 431, chunk - 1, chunk, chunk + 1, maximum })
            {
                var samples = new float[length];
                for (var i = 0; i < samples.Length; i++)
                    samples[i] = values[i % values.Length];
                Marshal.Copy(samples, 0, source, samples.Length);
                var expected = new short[length];
                for (var i = 0; i < expected.Length; i++)
                    expected[i] = (short)Math.Clamp(MathF.Round(Math.Clamp(samples[i], -1, 1) * 32768f), short.MinValue, short.MaxValue);
                check(SupertonicPcmWorker.CopyNativePcm(source, length, rate).AsSpan().SequenceEqual(expected),
                    $"Supertonic native PCM matches previous quantization at {length} samples");
            }

            foreach (var index in new[] { 0, chunk - 1, chunk, 2 * chunk - 1, 2 * chunk })
            {
                var address = source + index * sizeof(float);
                var original = Marshal.ReadInt32(address);
                foreach (var invalid in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity })
                {
                    Marshal.WriteInt32(address, BitConverter.SingleToInt32Bits(invalid));
                    check(Rejects(() => SupertonicPcmWorker.CopyNativePcm(source, 2 * chunk + 1, rate),
                        typeof(InvalidDataException)), $"Supertonic native PCM rejects {invalid} at {index}");
                }
                Marshal.WriteInt32(address, original);
            }
            Marshal.WriteInt32(source + (maximum - 1) * sizeof(float), BitConverter.SingleToInt32Bits(float.NaN));
            check(Rejects(() => SupertonicPcmWorker.CopyNativePcm(source, maximum, rate), typeof(InvalidDataException)),
                "Supertonic native PCM rejects NaN in the final partial chunk at maximum length");
        }
        finally { Marshal.FreeHGlobal(source); }

        check(Rejects(() => SupertonicPcmWorker.CopyNativePcm(0, 1, rate), typeof(InvalidDataException)) &&
            Rejects(() => SupertonicPcmWorker.CopyNativePcm(1, 0, rate), typeof(InvalidDataException)) &&
            Rejects(() => SupertonicPcmWorker.CopyNativePcm(1, maximum + 1, rate), typeof(InvalidDataException)) &&
            Rejects(() => SupertonicPcmWorker.CopyNativePcm(1, 1, rate - 1), typeof(InvalidDataException)),
            "Supertonic rejects invalid native PCM bounds before accessing the pointer");
    }

    private static bool Rejects(Action action, Type expected)
    {
        try { action(); return false; }
        catch (Exception error) { return expected.IsInstanceOfType(error); }
    }
}
