using System.Buffers.Binary;
using System.Text;
using Tanayomi;
using Tanayomi.Desktop;

internal static class AivisPcmProtocolTests
{
    internal static async Task RunProductAsync(string executable, Action<bool, string> check)
    {
        var baseDirectory = Path.GetDirectoryName(Path.GetFullPath(executable))!;
        var tempRoot = Path.GetFullPath(Path.GetTempPath());
        var temporary = Path.Combine(tempRoot, $"Aivis音声 検証-{Guid.NewGuid():N}");
        try
        {
            await using var worker = AivisSpeechWorker.TryCreate(baseDirectory, temporary)
                ?? throw new IOException("Aivis package missing from the application output.");
            var output = new PcmProbe();
            check(worker.Voices.Count is 4 or 8, "Aivis exposes every packaged voice");
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            await using var first = worker.CreateVoice(worker.Voices[0].Id, output);
            var cancellation = false;
            try { await first.PrepareAsync(cancel.Token); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancellation = true; }
            check(cancellation && output.Calls == 0, "Aivis cold preparation cancels without starting playback");

            foreach (var voice in worker.Voices)
            {
                await using var speech = worker.CreateVoice(voice.Id, output);
                await speech.PrepareAsync(default);
                var before = output.Calls;
                await speech.SpeakAsync("今日は日本語の読み上げを確認します。", default);
                check(output.Calls == before + 1 && output.Rate == 44100 && output.HasAudio,
                    $"Aivis worker plays valid 44.1kHz PCM after cancellation and restart: {voice.Id}");
            }
            check(!output.Disposed, "Aivis voices borrow the common PCM output");
        }
        finally
        {
            if (Directory.Exists(temporary))
            {
                if (Path.GetFullPath(temporary) != temporary ||
                    !temporary.StartsWith(Path.TrimEndingDirectorySeparator(tempRoot) + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Unexpected Aivis test temporary directory.");
                WorldComparison.RejectLinks(temporary);
                Directory.Delete(temporary, recursive: true);
            }
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        var text = "読み上げ😀";
        var request = AivisPcmProtocol.Encode(2, text);
        check(request.AsSpan(0, 4).SequenceEqual("AVQ1"u8) &&
              BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(4)) == 1 &&
              BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(8)) == 2 &&
              BinaryPrimitives.ReadInt32LittleEndian(request.AsSpan(12)) == request.Length - 16 &&
              Encoding.UTF8.GetString(request.AsSpan(16)) == text,
            "Aivis request sends the selected manifest index and exact UTF-8 text");
        check(AivisPcmProtocol.Encode(0, null).Length == 16 && AivisPcmProtocol.Encode(0, null)[4] == 0,
            "Aivis preparation sends no chat text");

        foreach (var invalid in new[] { "", "\0", "\ud800", new string('a', 501) })
        {
            var rejected = false;
            try { _ = AivisPcmProtocol.Encode(0, invalid); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "Aivis rejects invalid input before sending a request");
        }

        using var pcm = Reply(0, 44100, [0x34, 0x12, 0, 0x80]);
        var audio = await AivisPcmProtocol.ReadResponseAsync(pcm, false, default);
        check(audio?.SampleRate == 44100 && audio.Samples.SequenceEqual(new short[] { 0x1234, short.MinValue }),
            "Aivis preserves signed PCM16 sample values");
        using var prep = Reply(0, 0, []);
        check(await AivisPcmProtocol.ReadResponseAsync(prep, true, default) is null, "Aivis accepts the exact preparation acknowledgment");

        foreach (var (status, rate, payload, length) in new (int, int, byte[], int?)[]
        {
            (0, 48000, [1, 2], null), (0, 44100, [1], null),
            (0, 44100, [], 2 * AivisPcmProtocol.MaxSamples + 2), (0, 44100, [1], 2),
            (1, 0, "hello world"u8.ToArray(), null), (2, 44100, [1, 2], null)
        })
        {
            using var malformed = Reply(status, rate, payload, length);
            var rejected = false;
            try { _ = await AivisPcmProtocol.ReadResponseAsync(malformed, false, default); }
            catch (IOException) { rejected = true; }
            check(rejected, "Aivis rejects invalid, truncated or oversized worker responses");
        }

        using var failure = Reply(1, 0, "ValueError"u8.ToArray());
        var reason = "";
        try { _ = await AivisPcmProtocol.ReadResponseAsync(failure, false, default); }
        catch (IOException error) { reason = error.Message; }
        check(reason.Contains("AIVIS-WORKER", StringComparison.Ordinal) && reason.Contains("ValueError", StringComparison.Ordinal),
            "Aivis failure contains only its diagnostic code and exception category");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var response = Reply(0, 44100, [1, 2]);
        var observedCancellation = false;
        try { _ = await AivisPcmProtocol.ReadResponseAsync(response, false, cancelled.Token); }
        catch (OperationCanceledException) { observedCancellation = true; }
        check(observedCancellation, "Aivis observes caller cancellation while reading a response");

        foreach (var count in new[] { 16383, 16384, 16385, AivisPcmProtocol.MaxSamples })
        {
            var expected = CreateSamples(count);
            var frame = PcmReply(expected, appendAcknowledgment: true);
            var frameLength = frame.Length - 16;
            using var fragmented = new FragmentedResponse(frame);
            var decoded = await AivisPcmProtocol.ReadResponseAsync(fragmented, false, default);
            check(decoded?.SampleRate == 44100 && decoded.Samples.AsSpan().SequenceEqual(expected),
                $"Aivis preserves all {count} signed samples across seven-byte fragments");
            check(fragmented.Position == frameLength &&
                  await AivisPcmProtocol.ReadResponseAsync(fragmented, true, default) is null &&
                  fragmented.Position == fragmented.Length,
                $"Aivis leaves the next frame intact after {count} samples");
        }

        using var truncated = new FragmentedResponse(PcmReply(CreateSamples(16385))[..^1]);
        var observedEof = false;
        try { _ = await AivisPcmProtocol.ReadResponseAsync(truncated, false, default); }
        catch (EndOfStreamException) { observedEof = true; }
        check(observedEof, "Aivis rejects EOF after a complete PCM chunk and part of the final sample");

        using var interrupted = new CancellationTokenSource();
        using var partial = new FragmentedResponse(PcmReply(CreateSamples(16385)), interrupted);
        observedCancellation = false;
        try { _ = await AivisPcmProtocol.ReadResponseAsync(partial, false, interrupted.Token); }
        catch (OperationCanceledException error) when (error.CancellationToken == interrupted.Token)
        { observedCancellation = true; }
        check(observedCancellation && partial.Position > 16 && partial.Position < partial.Length,
            "Aivis reports caller cancellation after partial PCM without returning audio");

        await CheckPcmAllocationsAsync(check);
    }

    private static short[] CreateSamples(int count)
    {
        var samples = new short[count];
        for (var i = 0; i < samples.Length; i++)
            samples[i] = unchecked((short)(short.MinValue + i * 257));
        return samples;
    }

    private static byte[] PcmReply(short[] samples, bool appendAcknowledgment = false)
    {
        var length = samples.Length * sizeof(short);
        var frame = new byte[16 + length + (appendAcknowledgment ? 16 : 0)];
        WriteReplyHeader(frame, 0, 44100, length);
        for (var i = 0; i < samples.Length; i++)
            BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(16 + i * sizeof(short)), samples[i]);
        if (appendAcknowledgment) WriteReplyHeader(frame.AsSpan(16 + length), 0, 0, 0);
        return frame;
    }

    private static async Task CheckPcmAllocationsAsync(Action<bool, string> check)
    {
        using var stream = new MemoryStream(PcmReply(CreateSamples(AivisPcmProtocol.MaxSamples)), writable: false);
        _ = await ReadPreviousPcmAsync(stream);
        stream.Position = 0;
        _ = await AivisPcmProtocol.ReadResponseAsync(stream, false, default);

        stream.Position = 0;
        var thread = Environment.CurrentManagedThreadId;
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = await ReadPreviousPcmAsync(stream);
        if (Environment.CurrentManagedThreadId != thread) return;
        var previous = GC.GetAllocatedBytesForCurrentThread() - before;

        stream.Position = 0;
        before = GC.GetAllocatedBytesForCurrentThread();
        _ = await AivisPcmProtocol.ReadResponseAsync(stream, false, default);
        if (Environment.CurrentManagedThreadId != thread) return;
        var saved = previous - (GC.GetAllocatedBytesForCurrentThread() - before);
        var expected = AivisPcmProtocol.MaxSamples * sizeof(short) - 32 * 1024;
        check(saved >= expected - 1024, $"Aivis maximum-frame PCM decode saves {saved:N0} allocated bytes");
    }

    private static async Task<PcmAudio> ReadPreviousPcmAsync(Stream stream)
    {
        // The former success path held both the full byte payload and the final sample array.
        var header = new byte[16];
        await stream.ReadExactlyAsync(header);
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes);
        var samples = new short[length / sizeof(short)];
        Buffer.BlockCopy(bytes, 0, samples, 0, bytes.Length);
        return new(samples, rate);
    }

    private static MemoryStream Reply(int status, int rate, byte[] data, int? length = null)
    {
        var frame = new byte[16 + data.Length];
        WriteReplyHeader(frame, status, rate, length ?? data.Length);
        data.CopyTo(frame.AsSpan(16));
        return new(frame);
    }

    private static void WriteReplyHeader(Span<byte> frame, int status, int rate, int length)
    {
        "AVR1"u8.CopyTo(frame);
        BinaryPrimitives.WriteInt32LittleEndian(frame[4..], status);
        BinaryPrimitives.WriteInt32LittleEndian(frame[8..], rate);
        BinaryPrimitives.WriteInt32LittleEndian(frame[12..], length);
    }

    private sealed class FragmentedResponse(byte[] bytes, CancellationTokenSource? interrupt = null)
        : MemoryStream(bytes, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = Read(buffer.Span[..Math.Min(buffer.Length, 7)]);
            if (Position > 16) interrupt?.Cancel();
            return ValueTask.FromResult(count);
        }
    }

    private sealed class PcmProbe : IPcmOutput
    {
        internal int Calls { get; private set; }
        internal int Rate { get; private set; }
        internal bool HasAudio { get; private set; }
        internal bool Disposed { get; private set; }

        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            Rate = sampleRate;
            HasAudio = samples.Length is > 0 and <= AivisPcmProtocol.MaxSamples && samples.Span.ContainsAnyExcept((short)0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
