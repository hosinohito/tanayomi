using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
using Tanayomi;
using Tanayomi.Desktop;

internal static class DesktopSpeechTests
{
    internal static void RunWindowsVolumeBoostCheck(Action<bool, string> check)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows SAPI check requires Windows.");
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            if (!OperatingSystem.IsWindows()) return;
            object? voice = null, stream = null, format = null, voices = null, token = null;
            try
            {
                voice = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new PlatformNotSupportedException())
                    ?? throw new IOException("SAPI voice unavailable.");
                stream = Activator.CreateInstance(Type.GetTypeFromProgID("SAPI.SpMemoryStream") ?? throw new PlatformNotSupportedException())
                    ?? throw new IOException("SAPI memory stream unavailable.");
                dynamic sapi = voice;
                voices = sapi.GetVoices("Language=411", "");
                if (((dynamic)voices).Count > 0) { token = ((dynamic)voices).Item(0); sapi.Voice = token; }
                format = ((dynamic)stream).Format;
                var pcm = DesktopSpeech.SynthesizeWindowsPcm(voice, stream, "音量の確認です。<volume level=\"0\">これは文字です。</volume>");
                var original = (byte[])pcm.Clone();
                check(pcm.Length > 0 && pcm.Any(value => value != 0), "real Windows SAPI memory synthesis returns non-silent PCM");
                var wave = DesktopSpeech.CreateBoostedWindowsWave(pcm, 400);
                check(wave.AsSpan(0, 4).SequenceEqual("RIFF"u8) && wave.AsSpan(8, 8).SequenceEqual("WAVEfmt "u8) &&
                    wave.AsSpan(36, 4).SequenceEqual("data"u8) && wave.Length == 44 + pcm.Length &&
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(4)) == 36 + pcm.Length &&
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(16)) == 16 &&
                    BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(20)) == 1 &&
                    BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(22)) == 1 &&
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)) == 22050 &&
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(28)) == 44100 &&
                    BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(32)) == 2 &&
                    BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(34)) == 16 &&
                    BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(40)) == pcm.Length,
                    "real Windows speech boost constructs a bounded PCM16 mono WAV header");
                var matches = true;
                for (var i = 0; i < pcm.Length; i += 2)
                {
                    var input = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i));
                    var expected = (short)Math.Clamp(input * 4, short.MinValue, short.MaxValue);
                    if (BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44 + i)) != expected) { matches = false; break; }
                }
                check(matches, "every real SAPI sample receives exact 400 percent gain with PCM16 saturation");
                check(pcm.AsSpan().SequenceEqual(original), "real SAPI synthesis PCM remains unchanged after amplification");
            }
            catch (Exception error) { failure = ExceptionDispatchInfo.Capture(error); }
            finally
            {
                // Attempt every release even if disconnecting a failed COM engine throws.
                try { if (voice is not null) ((dynamic)voice).AudioOutputStream = null; }
                catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
                foreach (var item in new[] { token, voices, format, stream, voice })
                    try { if (item is not null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                    catch (Exception error) { failure ??= ExceptionDispatchInfo.Capture(error); }
            }
        }) { IsBackground = true, Name = "Windows volume boost verification" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(55))) throw new TimeoutException("Windows SAPI memory synthesis check timed out.");
        failure?.Throw();
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        WindowsPlainText(check);
        await WindowsBoost(check);
        await VolumeRequests(check);
        await ReapBeforeReplacement(check);
        await UnconfirmedStop(check);
        await SharedDisposal(check);
        await LinuxStop(check, 0);
        await LinuxStop(check, 1);
        using var bytes = new CountingStream(2 * 1024 * 1024);
        await DesktopSpeech.DrainAsync(bytes);
        check(bytes.Remaining == 0 && bytes.MaximumRead <= 512,
            "standard speech drains newline-free stderr using fixed-size storage");
        using var reply = new MemoryStream("ok\r\n"u8.ToArray());
        check(await DesktopSpeech.ReadReplyAsync(reply, CancellationToken.None) == "ok",
            "standard speech accepts its short CRLF reply without a line-buffer reader");
        using var invalid = new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 257)));
        check(await Failure(() => DesktopSpeech.ReadReplyAsync(invalid, CancellationToken.None)) is IOException,
            "standard speech rejects an overlong newline-free reply at its fixed ASCII bound");
    }

    private static void WindowsPlainText(Action<bool, string> check)
    {
        var voice = new FakeWindowsVoice();
        string[] texts = ["<volume level=\"0\">こんにちは</volume>", "<まだ閉じていないタグ", "こんにちは。"];
        foreach (var text in texts) DesktopSpeech.SpeakWindowsText(voice, SpeechQueue.PrepareText(text));
        check(voice.Calls.Select(call => call.Text).SequenceEqual(texts) && voice.Calls.All(call => call.Flags == 16),
            "Windows standard speech sends markup-like and ordinary chat unchanged with synchronous SVSFIsNotXML, never XML/file/async flags");
    }

    // Public members let the production dynamic COM call bind to this managed
    // fake. It records only fixed regression text and never invokes OS speech.
    public sealed class FakeWindowsVoice
    {
        public int Volume { get; set; }
        public object? AudioOutputStream { get; set; }
        public List<(string Text, int Flags)> Calls { get; } = [];
        public int Speak(string text, int flags) { Calls.Add((text, flags)); return Calls.Count; }
    }

    public sealed class FakeWindowsMemoryStream
    {
        public FakeWindowsAudioFormat Format { get; } = new();
        public byte[] Data { get; set; } = [];
        public byte[] GetData() => Data;
    }
    public sealed class FakeWindowsAudioFormat { public int Type { get; set; } }

    private static async Task WindowsBoost(Action<bool, string> check)
    {
        var voice = new FakeWindowsVoice();
        var pcm = new byte[12];
        short[] values = [1000, -1000, 10000, -10000, short.MaxValue, short.MinValue];
        for (var i = 0; i < values.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), values[i]);
        var stream = new FakeWindowsMemoryStream { Data = pcm };
        var synthesized = DesktopSpeech.SynthesizeWindowsPcm(voice, stream, "<volume level=\"0\">fixed</volume>");
        check(ReferenceEquals(synthesized, pcm) && ReferenceEquals(voice.AudioOutputStream, stream) && stream.Format.Type == 22 &&
            voice.Volume == 100 && voice.Calls.Single() == ("<volume level=\"0\">fixed</volume>", 16),
            "boosted SAPI synthesizes synchronous plain text into an explicit PCM16 mono memory stream at native volume 100");
        var wave = DesktopSpeech.CreateBoostedWindowsWave(pcm, 400);
        short[] expected = [4000, -4000, short.MaxValue, short.MinValue, short.MaxValue, short.MinValue];
        check(wave.Length == 44 + pcm.Length && BinaryPrimitives.ReadInt32LittleEndian(wave.AsSpan(24)) == 22050 &&
            expected.Select((value, i) => value == BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44 + i * 2))).All(value => value) &&
            values.Select((value, i) => value == BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2))).All(value => value),
            "Windows standard speech 400 percent amplifies with saturation into an in-memory WAV while protecting synthesis PCM");
        foreach (var data in new[] { Array.Empty<byte>(), new byte[3], new byte[22050 * 60 * 2 + 2] })
        {
            stream.Data = data;
            check(await Failure(() => Task.Run(() => DesktopSpeech.SynthesizeWindowsPcm(voice, stream, "fixed"))) is InvalidDataException,
                "SAPI boosted synthesis rejects empty, odd and over-duration PCM before playback");
        }
        check(DesktopSpeech.ParseRequest("{\"text\":\"boost\",\"volume\":400}") == ("boost", 400),
            "speech IPC accepts maximum amplification snapshot");
    }

    private static async Task VolumeRequests(Action<bool, string> check)
    {
        var voice = new FakeWindowsVoice();
        DesktopSpeech.SpeakWindowsText(voice, "fixed", 35);
        check(voice.Volume == 35 && voice.Calls.Count == 1, "SAPI volume is set on its own voice before plain-text Speak");
        DesktopSpeech.SpeakWindowsText(voice, "muted", 0);
        check(voice.Calls.Count == 1, "muted SAPI requests never invoke Speak");
        check(DesktopSpeech.ParseRequest("\"legacy\"") == ("legacy", 100) &&
            DesktopSpeech.ParseRequest("{\"text\":\"fixed\",\"volume\":35}") == ("fixed", 35),
            "standard speech accepts bounded volume requests and legacy JSON string requests at default 100");
        foreach (var malformed in new[] { "{\"text\":\"x\",\"volume\":401}", "{\"text\":\"x\",\"volume\":-1}", "{\"text\":null,\"volume\":10}", new string('x', 16385) })
            check(await Failure(() => Task.Run(() => DesktopSpeech.ParseRequest(malformed))) is not null, "standard speech rejects invalid request bounds");
        check(await Failure(() => DesktopSpeech.ReadRequestLineAsync(new StringReader(new string('x', 16385)))) is InvalidDataException,
            "standard speech bounds newline-free requests before JSON parsing");
        check(await Failure(() => DesktopSpeech.ReadRequestLineAsync(new StringReader("\"partial\""))) is EndOfStreamException,
            "standard speech rejects incomplete request framing");
        using var worker = new Worker { ExitOnKill = true };
        await using var speech = new DesktopSpeech(_ => worker);
        speech.Volume.Percent = 0;
        await speech.SpeakAsync("mute", default);
        check(worker.InputPipe.Length == 0, "standard speech mute completes without starting or sending native speech");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(await Failure(() => speech.SpeakAsync("mute", cancelled.Token)) is OperationCanceledException, "standard speech mute preserves cancellation priority");
        speech.Volume.Percent = 75;
        var first = speech.SpeakAsync("first", default); await Ready(worker.OutputPipe.Entered.Task);
        var second = speech.SpeakAsync("second", default);
        speech.Volume.Percent = 25;
        worker.OutputPipe.Reply("ok\nok\n");
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        var requests = Encoding.UTF8.GetString(worker.InputPipe.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(DesktopSpeech.ParseRequest).ToArray();
        check(requests.Length == 2 && requests[0].Volume == 75 && requests[1].Volume == 25,
            "standard speech snapshots volume after acquiring its serial gate, preserving the active utterance and updating the next one");
    }

    private static async Task ReapBeforeReplacement(Action<bool, string> check)
    {
        using var first = new Worker(); using var second = new Worker { ExitOnKill = true };
        second.OutputPipe.Reply("ok\n"); var created = 0; ProcessStartInfo? configuration = null;
        await using var speech = new DesktopSpeech(start => { configuration = start; return ++created == 1 ? first : second; });
        using var stop = new CancellationTokenSource();
        var active = speech.SpeakAsync("first", stop.Token); await Ready(first.OutputPipe.Entered.Task);
        stop.Cancel(); await Ready(first.WaitEntered.Task);
        var next = speech.SpeakAsync("second", CancellationToken.None);
        check(!active.IsCompleted && !next.IsCompleted && created == 1 && first.DisposeCalls == 0,
            "standard speech keeps the stopping worker and serial gate until its exit is confirmed");
        first.Exit(0);
        check(await Failure(() => active) is OperationCanceledException,
            "standard speech preserves explicit cancellation after successful reap");
        await next.WaitAsync(TimeSpan.FromSeconds(2));
        check(created == 2 && first.DisposeCalls == 1,
            "standard speech starts the next worker only after reaping and disposing the previous one");
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        check(configuration is { CreateNoWindow: true, UseShellExecute: false } &&
            configuration.ArgumentList.Contains("--speech-worker") && configuration.Environment["TEMP"] == temporary &&
            configuration.Environment["TMP"] == temporary && configuration.Environment["TMPDIR"] == temporary,
            "standard speech uses its hidden existing executable and executable-local child temporary directory");
    }

    private static async Task UnconfirmedStop(Action<bool, string> check)
    {
        using var process = new Worker(); var created = 0;
        var speech = new DesktopSpeech(_ => { created++; return process; }, stopTimeout: TimeSpan.FromMilliseconds(30));
        try
        {
            using var stop = new CancellationTokenSource();
            var active = speech.SpeakAsync("unconfirmed", stop.Token); await Ready(process.OutputPipe.Entered.Task);
            stop.Cancel();
            check(await Failure(() => active) is OperationCanceledException,
                "an unconfirmed standard speech exit never turns a user's cancellation into fallback failure");
            check(await Failure(() => speech.SpeakAsync("next", CancellationToken.None)) is IOException &&
                created == 1 && process.DisposeCalls == 0,
                "an unreaped standard speech worker remains owned and prevents replacement or protocol reuse");
            var first = speech.DisposeAsync().AsTask(); var second = speech.DisposeAsync().AsTask();
            check(ReferenceEquals(first, second) && await Failure(() => first) is IOException && await Failure(() => second) is IOException,
                "standard speech shares the same failed shutdown completion across concurrent disposals");
        }
        finally { process.Exit(0); } // In-memory seam only; no OS child exists.
    }

    private static async Task SharedDisposal(Action<bool, string> check)
    {
        using var process = new Worker();
        var speech = new DesktopSpeech(_ => process);
        var active = speech.SpeakAsync("dispose active", CancellationToken.None); await Ready(process.OutputPipe.Entered.Task);
        var first = speech.DisposeAsync().AsTask(); var second = speech.DisposeAsync().AsTask();
        await Ready(process.WaitEntered.Task);
        check(ReferenceEquals(first, second) && !first.IsCompleted && !second.IsCompleted,
            "concurrent standard speech disposal waits for the same active-worker reap");
        process.Exit(0);
        check(await Failure(() => active) is OperationCanceledException, "standard speech lifetime cancellation stops the current utterance");
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
        check(process.DisposeCalls == 1 && await Failure(() => speech.SpeakAsync("closed", CancellationToken.None)) is ObjectDisposedException,
            "standard speech shutdown disposes once and rejects later utterances");
    }

    private static async Task LinuxStop(Action<bool, string> check, int exitCode)
    {
        using var process = new Worker(); var created = 0;
        var speech = new DesktopSpeech(_ => { created++; return process; }, gracefulStop: true);
        using var stop = new CancellationTokenSource();
        var active = speech.SpeakAsync("linux own utterance", stop.Token); await Ready(process.OutputPipe.Entered.Task);
        stop.Cancel(); await Ready(process.WaitEntered.Task);
        check(process.InputPipe.Closed && process.Kills == 0 && !active.IsCompleted,
            "Linux standard speech closes only its own stdin and awaits the CANCEL SELF worker exit");
        process.Exit(exitCode);
        check(await Failure(() => active) is OperationCanceledException,
            "Linux standard speech cancellation stays cancellation even when its stop cannot be confirmed");
        if (exitCode == 0)
        {
            await speech.DisposeAsync();
            check(process.DisposeCalls == 1, "Linux standard speech releases the worker after its clean self-stop acknowledgement");
        }
        else
        {
            check(await Failure(() => speech.SpeakAsync("refuse", CancellationToken.None)) is IOException && created == 1 && process.DisposeCalls == 0,
                "Linux standard speech refuses a new worker when daemon-side self-stop was not confirmed");
            check(await Failure(() => speech.DisposeAsync().AsTask()) is IOException,
                "Linux standard speech exposes an unconfirmed stop during shutdown instead of pretending success");
        }
    }

    private static Task Ready(Task signal) => signal.WaitAsync(TimeSpan.FromSeconds(2));
    private static async Task<Exception?> Failure(Func<Task> action)
    { try { await action().WaitAsync(TimeSpan.FromSeconds(2)); return null; } catch (Exception error) { return error; } }

    private sealed class Worker : IDesktopSpeechWorker
    {
        private readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int disposed;
        public TrackingInput InputPipe { get; } = new();
        public ReplyPipe OutputPipe { get; } = new();
        public TaskCompletionSource WaitEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool ExitOnKill { get; init; }
        public int Kills { get; private set; }
        public int DisposeCalls { get; private set; }
        public Stream Input => InputPipe;
        public Stream Output => OutputPipe;
        public Stream Error => Stream.Null;
        public bool HasExited => exited.Task.IsCompleted;
        public int ExitCode { get; private set; }
        public void Kill() { Kills++; if (ExitOnKill) Exit(0); }
        public void Exit(int code) { ExitCode = code; exited.TrySetResult(); }
        public Task WaitForExitAsync(CancellationToken ct) { WaitEntered.TrySetResult(); return exited.Task.WaitAsync(ct); }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            DisposeCalls++; InputPipe.Dispose(); OutputPipe.Dispose();
        }
    }
    private sealed class TrackingInput : MemoryStream
    {
        public bool Closed { get; private set; }
        protected override void Dispose(bool disposing) { Closed = true; base.Dispose(disposing); }
    }
    private sealed class ReplyPipe : Stream
    {
        private readonly Channel<byte> bytes = Channel.CreateUnbounded<byte>();
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Reply(string text) { foreach (var b in Encoding.ASCII.GetBytes(text)) bytes.Writer.TryWrite(b); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            Entered.TrySetResult();
            if (!await bytes.Reader.WaitToReadAsync(ct)) return 0;
            var value = await bytes.Reader.ReadAsync(ct); buffer.Span[0] = value; return 1;
        }
        protected override void Dispose(bool disposing) { bytes.Writer.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingStream(long remaining) : Stream
    {
        public long Remaining { get; private set; } = remaining;
        public int MaximumRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { MaximumRead = Math.Max(MaximumRead, buffer.Length); var count = (int)Math.Min(Remaining, buffer.Length); buffer.Span[..count].Fill((byte)'x'); Remaining -= count; return ValueTask.FromResult(count); }
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { } public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
