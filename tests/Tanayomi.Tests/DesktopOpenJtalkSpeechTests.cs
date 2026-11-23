using System.Buffers.Binary;
using System.Text;
using Tanayomi;
using Tanayomi.Desktop;

// Offline adapter/protocol regression only: injected synthesis never launches a
// worker, and the borrowed PCM probe neither plays nor saves generated audio.
public static class DesktopOpenJtalkSpeechTests
{
    private const string Secret = "private-native-message-token-and-path";
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    private static void CheckAudioRange(Action<bool, string> check)
    {
        foreach (var length in new[] { 240, 480 })
        {
            var same = true;
            var positions = Enumerable.Range(0, System.Numerics.Vector<short>.Count * 2 + 1).Append(length - 1);
            foreach (var position in positions)
            foreach (var precedingSound in new[] { false, true })
            foreach (var value in new short[] { 0, 1, -1, 30147, -30147, 30148, -30148, short.MinValue, short.MaxValue })
            {
                var samples = new short[length];
                if (precedingSound) samples[0] = 1;
                samples[position] = value;
                var original = (short[])samples.Clone();
                var peak = 0;
                foreach (var sample in samples) peak = Math.Max(peak, Math.Abs((int)sample));
                var expected = peak is >= 1 and <= 30147;
                var accepted = true;
                try { OpenJtalkPcmProtocol.ValidateAudio(new(samples, 48000)); }
                catch (InvalidDataException error)
                {
                    accepted = false;
                    same &= error.Message == "NIT音声ワーカーの形式または上限が不正です。";
                }
                same &= accepted == expected && samples.SequenceEqual(original);
            }
            check(same, $"NIT audio range retains scalar peak acceptance, errors and input at zero, signed limits, lanes and final sample: length={length}");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckAudioRange(check);
        CheckRequestEncoding(check);
        await CheckVoiceSelectionAsync(check);
        var audio = Audio();
        var pcm = new PcmProbe(); var calls = 0; string? received = null;
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (text, _) =>
        { received = text; calls++; return Task.FromResult(audio); }))
        {
            await speech.SpeakAsync("日本語の試験", default);
            check(calls == 1 && received == "日本語の試験" && pcm.Calls == 1 && pcm.Rate == 48000 && pcm.Samples.SequenceEqual(audio.Samples),
                "NIT adapter sends unchanged text to synthesis and exact 48kHz PCM to the borrowed output");
            check(speech.Name == "NIT日本語 (Open JTalk)", "NIT adapter exposes its distinct engine identity");
            foreach (var invalid in new[] { "", "x\0y", "\ud800", new string('a', 1025) })
                await ThrowsAsync<InvalidDataException>(() => speech.SpeakAsync(invalid, default), check,
                    "invalid text is rejected before synthesis or borrowed playback");
            check(calls == 1 && pcm.Calls == 1, "rejected input cannot start a transport or play partial audio");
        }
        check(pcm.DisposeCalls == 0, "disposing the NIT adapter does not dispose its borrowed PCM output");
        var synthesisOnlyOutput = new PcmProbe();
        await using (var speech = new DesktopOpenJtalkSpeech(synthesisOnlyOutput, (_, _) => Task.FromResult(audio)))
        {
            var generated = await speech.SynthesizeAsync("声質変換の入力", default);
            check(generated.SampleRate == 48000 && generated.Samples.SequenceEqual(audio.Samples) && synthesisOnlyOutput.Calls == 0,
                "NIT synthesis-only boundary returns validated PCM without playing donor speech");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => speech.SynthesizeAsync("停止", stop.Token), check,
                "NIT synthesis-only boundary preserves cancellation without donor playback");
        }
        await CheckPreparationAsync(audio, check);
        await CheckPreparationProtocolAsync(audio, check);
        await CheckFailuresAsync(audio, check);
        await CheckSerialAsync(audio, check);
        await CheckCancellationAsync(audio, check);
        await CheckDisposalAsync(audio, check);
        await CheckProtocolAsync(audio, check);
    }

    private static async Task CheckPreparationAsync(PcmAudio audio, Action<bool, string> check)
    {
        var pcm = new PcmProbe(); var initialized = false; var preparations = 0; var syntheses = 0;
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) =>
            { if (!initialized) throw new InvalidDataException(); syntheses++; return Task.FromResult(audio); },
            ct => { ct.ThrowIfCancellationRequested(); initialized = true; preparations++; return Task.CompletedTask; }))
        {
            await speech.PrepareAsync(default);
            check(preparations == 1 && syntheses == 0 && pcm.Calls == 0,
                "NIT initialization preparation invokes no synthesis, dummy text or playback");
            check((await speech.SynthesizeAsync("準備後", default)).Samples.SequenceEqual(audio.Samples) && syntheses == 1 && pcm.Calls == 0,
                "prepared NIT synthesis reuses initialized adapter state without donor playback");
        }
        var entered = Signal(); var release = Signal(); syntheses = 0;
        var gated = new DesktopOpenJtalkSpeech(pcm, (_, _) => { syntheses++; return Task.FromResult(audio); },
            async ct => { entered.TrySetResult(); await release.Task.WaitAsync(ct); });
        try
        {
            var preparing = gated.PrepareAsync(default); await entered.Task.WaitAsync(Limit);
            var queued = gated.SynthesizeAsync("後続", default);
            using var stop = new CancellationTokenSource(); var waiter = gated.PrepareAsync(stop.Token); stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => waiter, check, "cancelled preparation waiter cannot stop the active preparation");
            check(syntheses == 0 && !queued.IsCompleted && !preparing.IsCompleted,
                "NIT preparation and synthesis share serialization without overlapping transport requests");
            release.TrySetResult(); await preparing.WaitAsync(Limit); await queued.WaitAsync(Limit);
            check(syntheses == 1, "queued speech enters once only after preparation acknowledgement");
        }
        finally { release.TrySetResult(); await gated.DisposeAsync(); }
        var attempts = 0;
        await using (var failure = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(audio), _ =>
            ++attempts == 1 ? Task.FromException(new IOException(Secret)) : Task.CompletedTask))
        {
            var error = await CaptureAsync(() => failure.PrepareAsync(default));
            check(error is IOException && !error.ToString().Contains(Secret, StringComparison.Ordinal),
                "NIT preparation failure remains failure and sanitizes private native details");
            check((await failure.SynthesizeAsync("失敗後", default)).Samples.SequenceEqual(audio.Samples),
                "ordinary speech can retry after a failed preparation without playback or fallback");
            await failure.PrepareAsync(default);
            check(attempts == 2, "failed preparation is not committed as ready and can be retried");
        }
        entered = Signal(); var calls = 0;
        await using (var cancellable = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(audio), async ct =>
            { if (++calls == 1) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } }))
        {
            using var stop = new CancellationTokenSource(); var preparing = cancellable.PrepareAsync(stop.Token);
            await entered.Task.WaitAsync(Limit); stop.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => preparing, check, "active NIT preparation preserves cancellation and emits no donor audio");
            await cancellable.PrepareAsync(default);
            check((await cancellable.SynthesizeAsync("取消後", default)).Samples.SequenceEqual(audio.Samples) && calls == 2,
                "cancelled NIT preparation releases serialization for a successful retry and speech");
        }
        entered = Signal(); var late = Signal();
        var disposed = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(audio), _ => { entered.TrySetResult(); return late.Task; });
        try
        {
            var pending = disposed.PrepareAsync(default); await entered.Task.WaitAsync(Limit);
            await disposed.DisposeAsync().AsTask().WaitAsync(Limit);
            await ThrowsAsync<OperationCanceledException>(() => pending, check, "disposal cancels pending NIT preparation without fallback");
            await ThrowsAsync<ObjectDisposedException>(() => disposed.PrepareAsync(default), check, "disposed NIT cannot recreate a preparation worker");
            check(pcm.Calls == 0 && pcm.DisposeCalls == 0, "preparation lifetime never plays or owns the borrowed output");
        }
        finally { late.TrySetResult(); await disposed.DisposeAsync(); }
    }

    private static async Task CheckPreparationProtocolAsync(PcmAudio audio, Action<bool, string> check)
    {
        var request = OpenJtalkPcmProtocol.EncodePreparation();
        using var fragmented = new ChunkStream(request);
        var decoded = OpenJtalkPcmProtocol.ReadCommand(fragmented);
        check(request.Length == 12 && decoded is { Preparation: true, Text.Length: 0 } && OpenJtalkPcmProtocol.ReadCommand(fragmented) is null,
            "distinct preparation command is exactly one empty control header with clean EOF");
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(request)), check,
            "preparation cannot be reinterpreted as a synthesis text request");
        foreach (var (offset, value) in new[] { (0, 123), (4, 2), (8, -1), (8, 1), (8, int.MaxValue) })
        {
            var bad = (byte[])request.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), value);
            Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadCommand(new MemoryStream(bad)), check,
                "preparation command rejects wrong magic/version or any hidden text payload before allocation");
        }
        Throws<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadCommand(new ChunkStream(request[..^1])), check,
            "truncated preparation control header cannot initialize native state");
        var emptyText = (byte[])request.Clone(); "OJPC"u8.CopyTo(emptyText);
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadCommand(new MemoryStream(emptyText)), check,
            "empty synthesis text cannot be used as an ambiguous preparation command");
        var literal = OpenJtalkPcmProtocol.ReadCommand(new ChunkStream(OpenJtalkPcmProtocol.EncodeRequest("OJPP")));
        check(literal is { Preparation: false } && Encoding.UTF8.GetString(literal.Text) == "OJPP",
            "control-like body text remains text because only the explicit request magic selects preparation");
        var malformedText = OpenJtalkPcmProtocol.EncodeRequest("ab"); malformedText[12] = 0xc0; malformedText[13] = 0xaf;
        var drawnOut = OpenJtalkPcmProtocol.ReadCommand(new ChunkStream(OpenJtalkPcmProtocol.EncodeRequest("日本語", true)));
        check(drawnOut is { Preparation: false, DrawnOutMode: true } &&
            OpenJtalkPcmProtocol.ReadCommand(new ChunkStream(OpenJtalkPcmProtocol.EncodeRequest("日本語"))) is { DrawnOutMode: false },
            "drawn-out request flag is explicit and ordinary requests retain the existing protocol");
        var invalidDrawnOut = OpenJtalkPcmProtocol.EncodeRequest("ab", true); invalidDrawnOut[12] = 0xc0; invalidDrawnOut[13] = 0xaf;
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadCommand(new MemoryStream(invalidDrawnOut)), check,
            "drawn-out request retains strict UTF8 validation");
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadCommand(new MemoryStream(malformedText)), check,
            "combined preparation/text parser retains strict UTF8 text validation");
        using var ack = new MemoryStream(); OpenJtalkPcmProtocol.WritePreparationResponse(ack, 0);
        var ackBytes = ack.ToArray(); await OpenJtalkPcmProtocol.ReadPreparationResponseAsync(new ChunkStream(ackBytes), default);
        check(ackBytes.Length == 12, "preparation success returns only an initialization acknowledgement, never PCM");
        await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(ackBytes.Concat(new byte[8]).ToArray()), default), check,
            "preparation acknowledgement cannot be accepted as audio");
        using var normalAudio = new MemoryStream(); OpenJtalkPcmProtocol.WriteResponse(normalAudio, 0, audio.Samples);
        await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadPreparationResponseAsync(new MemoryStream(normalAudio.ToArray()), default), check,
            "audio response cannot be accepted as preparation acknowledgement");
        foreach (var status in Enumerable.Range(1, 7))
        {
            using var error = new MemoryStream(); OpenJtalkPcmProtocol.WritePreparationResponse(error, status);
            check(await CaptureAsync(() => OpenJtalkPcmProtocol.ReadPreparationResponseAsync(new ChunkStream(error.ToArray()), default)) is OpenJtalkWorkerException failure && failure.Status == status,
                "preparation error retains typed native failure without PCM");
        }
        foreach (var (offset, value) in new[] { (0, 123), (4, 2), (8, -1), (8, 8) })
        {
            var bad = (byte[])ackBytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), value);
            await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadPreparationResponseAsync(new ChunkStream(bad), default), check,
                "preparation acknowledgement validates magic/version/status strictly");
        }
        await ThrowsAsync<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadPreparationResponseAsync(new MemoryStream(ackBytes[..^1]), default), check,
            "truncated initialization acknowledgement is not readiness");
        using var stopRead = new CancellationTokenSource(); using var blocked = new BlockedStream();
        var pendingAck = OpenJtalkPcmProtocol.ReadPreparationResponseAsync(blocked, stopRead.Token);
        await blocked.Entered.Task.WaitAsync(Limit); stopRead.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => pendingAck, check, "preparation response waiting observes cancellation without accepting readiness");
        var starts = 0; var syntheses = 0; var ready = false;
        using var commands = new MemoryStream(); commands.Write(request); commands.Write(request); commands.Write(OpenJtalkPcmProtocol.EncodeRequest("同じ子")); commands.Position = 0;
        using var responses = new MemoryStream();
        var statusCode = OpenJtalkPcmWorker.RunRequests(commands, responses,
            () => { if (!ready) { ready = true; starts++; } }, _ => { syntheses++; return audio; });
        responses.Position = 0; await OpenJtalkPcmProtocol.ReadPreparationResponseAsync(responses, default); await OpenJtalkPcmProtocol.ReadPreparationResponseAsync(responses, default);
        var spoken = await OpenJtalkPcmProtocol.ReadResponseAsync(responses, default);
        check(statusCode == 0 && starts == 1 && syntheses == 1 && spoken.Samples.SequenceEqual(audio.Samples) && responses.Position == responses.Length,
            "one persistent worker loop serves repeated no-synthesis preparation and later PCM with one native initialization");
        using var failedCommands = new MemoryStream(); failedCommands.Write(request); failedCommands.Write(OpenJtalkPcmProtocol.EncodeRequest("未処理")); failedCommands.Position = 0;
        using var failedResponse = new MemoryStream(); syntheses = 0;
        check(OpenJtalkPcmWorker.RunRequests(failedCommands, failedResponse, () => throw new OpenJtalkWorkerException(3), _ => { syntheses++; return audio; }) == 4 && syntheses == 0,
            "failed native initialization terminates the worker before any following synthesis request");
        failedResponse.Position = 0;
        check(await CaptureAsync(() => OpenJtalkPcmProtocol.ReadPreparationResponseAsync(failedResponse, default)) is OpenJtalkWorkerException { Status: 3 },
            "worker initialization failure cannot become a ready acknowledgement");
    }

    private static async Task CheckFailuresAsync(PcmAudio audio, Action<bool, string> check)
    {
        var pcm = new PcmProbe(); var calls = 0;
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => ++calls == 1
            ? Task.FromException<PcmAudio>(new IOException(Secret)) : Task.FromResult(audio)))
        {
            var failure = await CaptureAsync(() => speech.SpeakAsync("試験", default));
            check(failure is IOException && !failure.ToString().Contains(Secret, StringComparison.Ordinal) && pcm.Calls == 0,
                "transport failure is sanitized and never reaches playback as successful speech");
            await speech.SpeakAsync("次の試験", default);
            check(calls == 2 && pcm.Calls == 1, "a failed NIT request does not prevent the next request from succeeding");
        }
        pcm = new PcmProbe(); calls = 0;
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => ++calls == 1
            ? Task.FromException<PcmAudio>(new OpenJtalkWorkerException(5)) : Task.FromResult(audio)))
        {
            var failure = await CaptureAsync(() => speech.SpeakAsync("試験", default));
            check(failure is OpenJtalkWorkerException { Status: 5 } && pcm.Calls == 0,
                "native terminal status stays typed for the caller without pretending synthesis succeeded");
            await speech.SpeakAsync("次の試験", default);
            check(pcm.Calls == 1, "the adapter remains usable after a native error request");
        }
        pcm = new PcmProbe((call, _) => call == 1 ? Task.FromException(new IOException(Secret)) : Task.CompletedTask);
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(audio)))
        {
            var failure = await CaptureAsync(() => speech.SpeakAsync("試験", default));
            check(failure is IOException && !failure.ToString().Contains(Secret, StringComparison.Ordinal),
                "borrowed playback errors are sanitized at the NIT boundary");
            await speech.SpeakAsync("次の試験", default);
            check(pcm.Calls == 2 && pcm.DisposeCalls == 0, "playback failure neither disposes the owner output nor blocks future speech");
        }
        var invalidAudio = new PcmAudio[] { null!, new(null!, 48000), new(audio.Samples, 44100), new([], 48000),
            new(new short[239], 48000), new(new short[241], 48000), new(new short[240], 48000),
            new(Enumerable.Repeat(short.MinValue, 240).ToArray(), 48000) };
        pcm = new PcmProbe(); calls = 0;
        await using (var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(calls < invalidAudio.Length ? invalidAudio[calls++] : audio)))
        {
            foreach (var _ in invalidAudio)
                await ThrowsAsync<IOException>(() => speech.SpeakAsync("試験", default), check,
                    "invalid rate length silence or saturation is rejected before borrowed playback");
            check(pcm.Calls == 0, "no malformed synthesized PCM is handed to an output device");
            await speech.SpeakAsync("回復", default);
            check(pcm.Calls == 1, "invalid PCM does not poison later valid synthesis");
        }
    }

    private static async Task CheckSerialAsync(PcmAudio audio, Action<bool, string> check)
    {
        var playing = Signal(); var release = Signal(); var calls = 0;
        var pcm = new PcmProbe(async (call, _) => { if (call == 1) { playing.TrySetResult(); await release.Task; } });
        var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => { calls++; return Task.FromResult(audio); });
        try
        {
            var first = speech.SpeakAsync("最初", default);
            await playing.Task.WaitAsync(Limit);
            var second = speech.SpeakAsync("次", default);
            using var cancel = new CancellationTokenSource();
            var waiting = speech.SpeakAsync("取消", cancel.Token); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => waiting, check, "waiting NIT requests can cancel without entering synthesis");
            check(calls == 1 && pcm.Calls == 1 && !second.IsCompleted,
                "NIT serialization covers playback and a cancelled waiter cannot interrupt the active request");
            release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(Limit);
            check(calls == 2 && pcm.Calls == 2, "the next queued request starts only after preceding playback completes");
        }
        finally { release.TrySetResult(); await speech.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    private static async Task CheckCancellationAsync(PcmAudio audio, Action<bool, string> check)
    {
        var entered = Signal(); var late = new TaskCompletionSource<PcmAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcm = new PcmProbe(); var calls = 0;
        var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) =>
        {
            if (++calls == 1) { entered.TrySetResult(); return late.Task; }
            return Task.FromResult(audio);
        });
        try
        {
            using var cancel = new CancellationTokenSource();
            var active = speech.SpeakAsync("取消", cancel.Token);
            await entered.Task.WaitAsync(Limit); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => active, check,
                "caller cancellation stops even a synthesis delegate that completes late");
            late.TrySetException(new IOException(Secret));
            await speech.SpeakAsync("回復", default);
            check(pcm.Calls == 1 && calls == 2, "late cancelled synthesis cannot play stale audio or block the next request");
        }
        finally { late.TrySetResult(audio); await speech.DisposeAsync().AsTask().WaitAsync(Limit); }

        entered = Signal(); var release = Signal();
        pcm = new PcmProbe(async (call, _) => { if (call == 1) { entered.TrySetResult(); await release.Task; } });
        speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => Task.FromResult(audio));
        try
        {
            using var cancel = new CancellationTokenSource();
            var active = speech.SpeakAsync("再生取消", cancel.Token);
            await entered.Task.WaitAsync(Limit); cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => active, check,
                "playback cancellation stays cancellation instead of an error that triggers fallback speech");
            release.TrySetResult();
            await speech.SpeakAsync("回復", default);
            check(pcm.Calls == 2 && pcm.DisposeCalls == 0, "cancelled playback leaves the borrowed output available to its owner");
        }
        finally { release.TrySetResult(); await speech.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    private static async Task CheckDisposalAsync(PcmAudio audio, Action<bool, string> check)
    {
        var entered = Signal(); var late = new TaskCompletionSource<PcmAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pcm = new PcmProbe(); var speech = new DesktopOpenJtalkSpeech(pcm, (_, _) => { entered.TrySetResult(); return late.Task; });
        try
        {
            var active = speech.SpeakAsync("終了", default);
            await entered.Task.WaitAsync(Limit);
            var one = speech.DisposeAsync().AsTask(); var two = speech.DisposeAsync().AsTask();
            await Task.WhenAll(one, two).WaitAsync(Limit);
            check(ReferenceEquals(one, two) && pcm.DisposeCalls == 0, "concurrent NIT disposal shares completion without disposing borrowed playback");
            await ThrowsAsync<OperationCanceledException>(() => active, check, "disposal cancels active synthesis without starting output or fallback");
            late.TrySetResult(audio);
            await ThrowsAsync<ObjectDisposedException>(() => speech.SpeakAsync("終了後", default), check,
                "disposed NIT adapters cannot accept new speech or recreate a transport");
            check(pcm.Calls == 0, "a synthesis result arriving after disposal is never played");
        }
        finally { late.TrySetResult(audio); await speech.DisposeAsync().AsTask().WaitAsync(Limit); }
    }

    private static void CheckRequestEncoding(Action<bool, string> check)
    {
        // Independent former wire encoder, including strict UTF-8 roundtrip.
        static byte[] Previous(string text, byte[] magic)
        {
            static InvalidDataException Invalid() => new("NIT音声ワーカーの形式または上限が不正です。");
            if (text is null || text.Length is < 1 or > 2048 || text.Contains('\0')) throw Invalid();
            var utf8 = new UTF8Encoding(false, true);
            byte[] body;
            try { body = utf8.GetBytes(text); }
            catch (EncoderFallbackException) { throw Invalid(); }
            if (body.Length is < 1 or > 4096 || body.Contains((byte)0) ||
                utf8.GetString(body).EnumerateRunes().Count() > 1024) throw Invalid();
            var frame = new byte[12 + body.Length]; magic.CopyTo(frame, 0);
            BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), body.Length);
            body.CopyTo(frame, 12); return frame;
        }
        static bool Same(string text, byte[] magic)
        {
            byte[]? expected = null; Exception? failure = null;
            try { expected = Previous(text, magic); } catch (InvalidDataException e) { failure = e; }
            try
            {
                var bytes = OpenJtalkPcmProtocol.ValidateRequestText(text);
                var actual = OpenJtalkPcmProtocol.EncodeTextRequest(text, magic);
                return failure is null && expected is not null && expected.SequenceEqual(actual) && bytes == expected.Length - 12;
            }
            catch (InvalidDataException e)
            {
                if (failure?.Message != e.Message) return false;
                try { OpenJtalkPcmProtocol.EncodeTextRequest(text, magic); return false; }
                catch (InvalidDataException encoded) { return encoded.Message == e.Message; }
            }
        }
        var pcm = "OJPC"u8.ToArray(); var plan = "OJLC"u8.ToArray();
        var all = true;
        for (var c = 0; c <= char.MaxValue; c++) all &= Same(new string((char)c, 1), pcm);
        check(all, "request encoding preserves acceptance and wire bytes for every individual UTF16 code unit");
        var boundary = new[] { null!, "", "x\0y", "\ud800x", "x\udfff", "\ud800\ud800", "\udfff\ud800",
            "\ud800\udc00", "\udbff\udfff", "\ufeff\ufffe\uffff", "日本語𠮷\r\n" };
        foreach (var text in boundary) check(Same(text, pcm) && Same(text, plan), "request boundary acceptance and both protocol frames match former encoder");
        foreach (var unit in new[] { "a", "あ", "😀", "a😀" })
        foreach (var count in new[] { 511, 512, 513, 1023, 1024, 1025, 2048, 2049 })
        {
            var text = string.Concat(Enumerable.Repeat(unit, count));
            check(Same(text, pcm) && Same(text, plan), "scalar, UTF16 and byte limits retain former wire encoding");
        }
        // One warmed allocation comparison, not a latency benchmark.
        var sample = string.Concat(Enumerable.Repeat("日本語😀", 128));
        _ = Previous(sample, pcm); _ = OpenJtalkPcmProtocol.EncodeRequest(sample);
        _ = OpenJtalkPcmProtocol.ValidateRequestText(sample);
        var before = GC.GetAllocatedBytesForCurrentThread();
        _ = Previous(sample, pcm); var previous = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        _ = OpenJtalkPcmProtocol.EncodeRequest(sample); var current = GC.GetAllocatedBytesForCurrentThread() - before;
        before = GC.GetAllocatedBytesForCurrentThread();
        _ = OpenJtalkPcmProtocol.ValidateRequestText(sample); var validation = GC.GetAllocatedBytesForCurrentThread() - before;
        check(current < previous && validation == 0, "request prevalidation allocates nothing and frame encoding removes temporary body/roundtrip allocations");
    }

    private static async Task CheckProtocolAsync(PcmAudio audio, Action<bool, string> check)
    {
        const string text = "日本語𠮷";
        var request = OpenJtalkPcmProtocol.EncodeRequest(text);
        using (var input = new ChunkStream(request))
            check(Encoding.UTF8.GetString(OpenJtalkPcmProtocol.ReadRequest(input)!) == text && OpenJtalkPcmProtocol.ReadRequest(input) is null,
                "request protocol handles fragmented UTF8 frames and distinguishes clean EOF");
        var boundary = string.Concat(Enumerable.Repeat("😀", 1024));
        check(OpenJtalkPcmProtocol.EncodeRequest(boundary).Length == 12 + 4096,
            "request limits count Unicode scalars and accept exactly 1024 supplementary characters");
        foreach (var value in new[] { -1, 0, 4097, int.MaxValue })
        {
            var header = request[..12]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), value);
            Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(header)), check,
                "invalid request lengths fail on the header before body allocation or reading");
        }
        var invalidRequest = request.ToArray(); invalidRequest[0] ^= 1;
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(invalidRequest)), check,
            "request framing rejects wrong magic rather than treating it as text");
        Throws<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(request[..11])), check,
            "partial request headers are different from clean EOF");
        Throws<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(request[..^1])), check,
            "partial request bodies cannot become a successful utterance");
        var badUtf8 = request[..14]; BinaryPrimitives.WriteInt32LittleEndian(badUtf8.AsSpan(8), 2); badUtf8[12] = 0xc0; badUtf8[13] = 0xaf;
        Throws<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadRequest(new MemoryStream(badUtf8)), check,
            "malformed UTF8 is rejected at the worker protocol boundary");

        using var output = new MemoryStream();
        OpenJtalkPcmProtocol.WriteResponse(output, 0, audio.Samples);
        var response = output.ToArray();
        var decoded = await OpenJtalkPcmProtocol.ReadResponseAsync(new ChunkStream(response), default);
        check(decoded.SampleRate == 48000 && decoded.Samples.SequenceEqual(audio.Samples) && response.Length == 20 + audio.Samples.Length * 2,
            "fragmented response protocol preserves complete signed little-endian PCM and its exact sample rate");
        using (var consecutive = new ChunkStream(response.Concat(response).Concat(new byte[] { 0x5a, 0xc3 }).ToArray()))
        {
            var first = await OpenJtalkPcmProtocol.ReadResponseAsync(consecutive, default);
            var firstBoundary = consecutive.Position == response.Length;
            var second = await OpenJtalkPcmProtocol.ReadResponseAsync(consecutive, default);
            check(firstBoundary && consecutive.Position == response.Length * 2L &&
                first.Samples.SequenceEqual(audio.Samples) && second.Samples.SequenceEqual(audio.Samples) &&
                consecutive.ReadByte() == 0x5a && consecutive.ReadByte() == 0xc3,
                "fragmented PCM decoding consumes exactly one response and preserves the next header and trailing bytes");
        }
        foreach (var status in Enumerable.Range(1, 7))
        {
            using var error = new MemoryStream(); OpenJtalkPcmProtocol.WriteResponse(error, status, []);
            var failure = await CaptureAsync(() => OpenJtalkPcmProtocol.ReadResponseAsync(new ChunkStream(error.ToArray()), default));
            check(error.Length == 20 && failure is OpenJtalkWorkerException native && native.Status == status,
                "native error responses carry only typed status and no partial PCM body");
        }
        foreach (var (offset, value) in new[] { (4, 2), (8, -1), (8, 8), (12, 44100), (16, 239), (16, 241),
                     (16, OpenJtalkPcmProtocol.MaximumSamples + 240), (16, int.MaxValue) })
        {
            var header = response[..20]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(offset), value);
            await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(header), default), check,
                "invalid response version status rate or length fails before allocating or consuming a PCM body");
        }
        var errorWithAudio = response[..20]; BinaryPrimitives.WriteInt32LittleEndian(errorWithAudio.AsSpan(8), 5);
        await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(errorWithAudio), default), check,
            "an error frame cannot advertise success metadata or an audio body");
        await ThrowsAsync<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(response[..19]), default), check,
            "partial response headers cannot be accepted as native success");
        await ThrowsAsync<EndOfStreamException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(response[..^1]), default), check,
            "partial response PCM cannot reach playback");
        using var silence = new MemoryStream(); OpenJtalkPcmProtocol.WriteResponse(silence, 0, new short[240]);
        await ThrowsAsync<InvalidDataException>(() => OpenJtalkPcmProtocol.ReadResponseAsync(new MemoryStream(silence.ToArray()), default), check,
            "silent PCM is not accepted as successful native speech");
        using var cancel = new CancellationTokenSource(); var blocked = new BlockedStream();
        var pending = OpenJtalkPcmProtocol.ReadResponseAsync(blocked, cancel.Token);
        await blocked.Entered.Task.WaitAsync(Limit); cancel.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => pending, check, "cancellation interrupts a pending protocol header read without waiting for a body");
    }

    private static async Task CheckVoiceSelectionAsync(Action<bool, string> check)
    {
        var pcm = new PcmProbe();
        var started = Signal();
        var finish = Signal();
        var audio = Audio();
        int nitSpoken = 0, tohokuSpoken = 0, tohokuPrepared = 0;
        var nit = new DesktopOpenJtalkSpeech(pcm, async (_, ct) =>
        {
            nitSpoken++;
            started.TrySetResult();
            await finish.Task.WaitAsync(ct);
            return audio;
        });
        var tohoku = new DesktopOpenJtalkSpeech(pcm, (_, _) => { tohokuSpoken++; return Task.FromResult(audio); },
            _ => { tohokuPrepared++; return Task.CompletedTask; }, OpenJtalkPcmWorker.TohokuVoiceId);
        var additionalIds = new[] { "tohoku-f01-angry", "tohoku-f01-happy", "tohoku-f01-sad",
            "mei-normal", "mei-angry", "mei-bashful", "mei-happy", "mei-sad",
            "takumi-normal", "takumi-angry", "takumi-happy", "takumi-sad" };
        var dispatched = new List<string>();
        var additional = additionalIds.Select(id => new DesktopOpenJtalkSpeech(pcm, (_, _) =>
        {
            dispatched.Add(id);
            return Task.FromResult(audio);
        }, voiceId: id)).ToArray();
        await using var selection = new DesktopOpenJtalkVoiceSelection(nit, [tohoku, .. additional]);
        check(selection.Voices.Select(voice => voice.Id).SequenceEqual(["nit-atr503", "tohoku-f01-neutral", .. additionalIds]) &&
            selection.Voices.Skip(2).Take(3).Select(voice => voice.Name).SequenceEqual(["東北大 f01（怒り）", "東北大 f01（喜び）", "東北大 f01（悲しみ）"]) &&
            selection.VoiceId == "nit-atr503" && selection.Name == nit.Name, "Open JTalk lists fixed voices and defaults to NIT");
        var speaking = selection.SpeakAsync("日本語", default);
        await started.Task.WaitAsync(Limit);
        selection.VoiceId = "tohoku-f01-neutral";
        finish.TrySetResult();
        await speaking.WaitAsync(Limit);
        await selection.PrepareAsync(default).WaitAsync(Limit);
        await selection.SpeakAsync("日本語", default).WaitAsync(Limit);
        check(nitSpoken == 1 && tohokuSpoken == 1 && tohokuPrepared == 1 && selection.Name == tohoku.Name && pcm.Calls == 2,
            "Open JTalk speech keeps its starting selection and later speech and preparation use the new voice");
        foreach (var id in additionalIds)
        {
            selection.VoiceId = id;
            await selection.SpeakAsync("日本語", default).WaitAsync(Limit);
        }
        check(dispatched.SequenceEqual(additionalIds) && pcm.Calls == additionalIds.Length + 2, "each additional Open JTalk voice dispatches its own synthesis");
        check(selection.Voices.Skip(5).All(voice => voice.Credit.Contains("Nagoya Institute of Technology") &&
            voice.Usage.Contains("配信概要") && voice.TermsUrl == "https://creativecommons.org/licenses/by/3.0/"),
            "all nine MMDAgent voices expose the required public-audio attribution and terms");
        selection.VoiceId = "../other.htsvoice";
        check(selection.VoiceId == "nit-atr503", "unknown Open JTalk voice IDs fall back to NIT");
        await using var limited = new DesktopOpenJtalkVoiceSelection(new DesktopOpenJtalkSpeech(pcm));
        limited.VoiceId = additionalIds[0];
        check(limited.VoiceId == "nit-atr503" && limited.Voices.Count == 1, "an unavailable fixed Open JTalk voice falls back to NIT");
        await selection.DisposeAsync();
        check(await CaptureAsync(() => nit.SpeakAsync("日本語", default)) is ObjectDisposedException &&
            await CaptureAsync(() => tohoku.SpeakAsync("日本語", default)) is ObjectDisposedException && pcm.DisposeCalls == 0,
            "Open JTalk selection disposes the original adapters and leaves the shared PCM owner available");
        foreach (var voice in additional)
            check(await CaptureAsync(() => voice.SpeakAsync("日本語", default)) is ObjectDisposedException,
                $"Open JTalk selection disposes {voice.VoiceId}");
        check(OpenJtalkPcmWorker.RunWorker(["--open-jtalk-pcm-worker", "../other.htsvoice"]) == 2 &&
            OpenJtalkPcmWorker.RunWorker(["--open-jtalk-pcm-worker", "tohoku-f01-neutral", "extra"]) == 2,
            "Open JTalk worker rejects arbitrary paths and extra voice arguments before opening native resources");
    }

    private static PcmAudio Audio() => new(Enumerable.Range(0, 4800).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 220 / 48000) * 6000)).ToArray(), 48000);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try { await operation().WaitAsync(Limit); return null; } catch (Exception error) { return error; }
    }
    private static async Task ThrowsAsync<T>(Func<Task> operation, Action<bool, string> check, string description) where T : Exception
        => check(await CaptureAsync(operation) is T, description);
    private static void Throws<T>(Action operation, Action<bool, string> check, string description) where T : Exception
    {
        var caught = false; try { operation(); } catch (T) { caught = true; } check(caught, description);
    }
    private sealed class PcmProbe(Func<int, CancellationToken, Task>? handler = null) : IPcmOutput
    {
        internal int Calls, DisposeCalls, Rate;
        internal short[] Samples = [];
        public async Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); var call = Interlocked.Increment(ref Calls);
            Samples = samples.ToArray(); Rate = sampleRate;
            if (handler is not null) await handler(call, ct);
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref DisposeCalls); return ValueTask.CompletedTask; }
    }
    private sealed class ChunkStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 3));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 3)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(Read(buffer.Span)); }
    }
    private sealed class BlockedStream : MemoryStream
    {
        internal TaskCompletionSource Entered { get; } = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
    }
}
