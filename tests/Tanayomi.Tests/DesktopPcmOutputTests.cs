using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using Tanayomi;
using Tanayomi.Desktop;

// Offline process-lifetime probes only. No child process, audio API, temporary
// file or audio device is used by these tests.
internal static class DesktopPcmOutputTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan ReapLimit = TimeSpan.FromMilliseconds(30);
    private static readonly short[] Samples = [0, 1000, -1000, 500];

    public static async Task RunAsync(Action<bool, string> check)
    {
        var pcm = Convert.FromHexString("0000E80318FCF401");
        using var wire = new MemoryStream([.. pcm, 0x5A]);
        var wave = DesktopPcmOutput.ReadWindowsWave(wire, Samples.Length, 44100);
        check(Convert.ToHexString(wave) == "524946462C00000057415645666D7420100000000100010044AC0000885801000200100064617461080000000000E80318FCF401" &&
            wire.ReadByte() == 0x5A, "Windows playback preserves the WAV header and every signed sample without consuming the next frame");
        using var shortWire = new MemoryStream(pcm, 0, pcm.Length - 1);
        var truncated = false;
        try { DesktopPcmOutput.ReadWindowsWave(shortWire, Samples.Length, 44100); }
        catch (EndOfStreamException) { truncated = true; }
        check(truncated, "a truncated Windows PCM frame never reaches playback");
        foreach (var rate in new[] { 8000, 22050, 44100, 48000, 96000, 192000 })
        foreach (var count in new[] { 1, 17, 257 })
        {
            var bytes = Enumerable.Range(0, count * 2).Select(i => (byte)(i * 37)).ToArray();
            using var expected = new MemoryStream();
            using (var writer = new BinaryWriter(expected, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write("RIFF"u8); writer.Write(36 + bytes.Length); writer.Write("WAVEfmt "u8);
                writer.Write(16); writer.Write((short)1); writer.Write((short)1);
                writer.Write(rate); writer.Write(rate * 2); writer.Write((short)2); writer.Write((short)16);
                writer.Write("data"u8); writer.Write(bytes.Length); writer.Write(bytes);
            }
            using var input = new MemoryStream([.. bytes, 0x5a]);
            check(DesktopPcmOutput.ReadWindowsWave(input, count, rate).SequenceEqual(expected.ToArray()) && input.ReadByte() == 0x5a,
                "Windows direct WAV header preserves all fields, payload and next-frame boundary across supported rates");
        }
        var child = new Child("ok\nok\r\n"); var calls = 0; ProcessStartInfo? launch = null;
        var stderr = new ByteFlood(1024 * 1024); child.Stderr = stderr;
        var output = new DesktopPcmOutput(start => { calls++; launch = start; return child; });
        try
        {
            short[] padded = [short.MinValue, .. Samples, short.MaxValue];
            await output.PlayAsync(padded.AsMemory(1, Samples.Length), 44100, default);
            await output.PlayAsync(Samples, 44100, default);
            var bytes = child.Stdin.ToArray(); var frame = 8 + Samples.Length * 2;
            check(calls == 1 && bytes.Length == frame * 2 && BinaryPrimitives.ReadInt32LittleEndian(bytes) == 44100 &&
                BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) == Samples.Length &&
                bytes.AsSpan(8, pcm.Length).SequenceEqual(pcm) && bytes.AsSpan(frame + 8).SequenceEqual(pcm),
                "PCM sends exact binary mono frames and reuses one successfully responding owned worker");
            var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
            check(launch is { UseShellExecute: false, CreateNoWindow: true, RedirectStandardInput: true,
                RedirectStandardOutput: true, RedirectStandardError: true } && launch.ArgumentList.Contains("--pcm-worker") &&
                launch.Environment["TEMP"] == temporary && launch.Environment["TMP"] == temporary && launch.Environment["TMPDIR"] == temporary,
                "PCM launches an isolated worker with explicit app-local temporary directory inheritance");
            await DesktopPcmOutput.DrainAsync(stderr);
            check(stderr.Remaining == 0 && stderr.MaximumRead <= 512,
                "PCM discards a large stderr stream without newline buffering or retained native diagnostics");
        }
        finally { await output.DisposeAsync(); }
        check(child.Kills == 1 && child.Waits == 1 && child.Disposes == 1,
            "PCM disposal confirms owned worker exit before releasing its handles");

        foreach (var reply in new[] { "error:private-native-message:DEADBEEF\n", new string('x', 10000), "", "\u0080\n" })
        {
            var bad = new Child(reply); var good = new Child("ok\n"); calls = 0;
            output = new DesktopPcmOutput(_ => ++calls == 1 ? bad : good);
            try
            {
                var error = await CaptureAsync(() => output.PlayAsync(Samples, 44100, default));
                check(error is IOException && !error.ToString().Contains("private-native-message", StringComparison.Ordinal) &&
                    bad.Reply.Position <= 256 && bad.Disposes == 1 && bad.HasExited,
                    "invalid or oversized PCM replies fail with a fixed message and a confirmed reap before retry");
                await output.PlayAsync(Samples, 44100, default);
                check(calls == 2, "a confirmed failed PCM worker can be replaced for the next playback");
            }
            finally { await output.DisposeAsync(); }
        }
        await CheckCancellationAndRetentionAsync(check);
        await CheckWaitingAndDisposeAsync(check);
    }

    private static async Task CheckCancellationAndRetentionAsync(Action<bool, string> check)
    {
        var blocked = new GateReply();
        var retained = new Child(blocked) { ExitOnKill = false, KillFailure = new IOException("private-kill-error") };
        var next = new Child("ok\n"); var starts = 0;
        var output = new DesktopPcmOutput(_ => ++starts == 1 ? retained : next, ReapLimit);
        try
        {
            using var stop = new CancellationTokenSource();
            var playing = output.PlayAsync(Samples, 44100, stop.Token);
            await blocked.Entered.Task.WaitAsync(Limit);
            stop.Cancel();
            var cancellation = await CaptureAsync(() => playing);
            check(cancellation is OperationCanceledException && retained.Disposes == 0 && !retained.HasExited,
                "user PCM cancellation takes precedence over kill failure and bounded reap failure");
            var failure = await CaptureAsync(() => output.PlayAsync(Samples, 44100, default));
            check(failure is IOException && !failure.ToString().Contains("private-kill-error", StringComparison.Ordinal) &&
                starts == 1 && retained.Disposes == 0,
                "an unconfirmed PCM worker stays owned and prevents a replacement from multiplying children");
            retained.Exit();
            await output.PlayAsync(Samples, 44100, default);
            check(starts == 2 && retained.Disposes == 1,
                "PCM can recover after the retained worker exit is actually confirmed");
        }
        finally { retained.Exit(); await output.DisposeAsync(); }

        retained = new Child("bad\n") { ExitOnKill = false }; starts = 0;
        output = new DesktopPcmOutput(_ => { starts++; return retained; }, ReapLimit);
        await CaptureAsync(() => output.PlayAsync(Samples, 44100, default));
        try
        {
            var first = output.DisposeAsync().AsTask(); var second = output.DisposeAsync().AsTask();
            var one = await CaptureAsync(() => first); var two = await CaptureAsync(() => second);
            check(ReferenceEquals(first, second) && one is IOException && ReferenceEquals(one, two) &&
                retained.Disposes == 0 && starts == 1,
                "repeated PCM disposal shares its failed completion and does not silently release an unreaped child");
        }
        finally { retained.Exit(); retained.Dispose(); }
    }

    private static async Task CheckWaitingAndDisposeAsync(Action<bool, string> check)
    {
        var reply = new GateReply(); var child = new Child(reply); var starts = 0;
        var output = new DesktopPcmOutput(_ => { starts++; return child; });
        try
        {
            var first = output.PlayAsync(Samples, 44100, default);
            await reply.Entered.Task.WaitAsync(Limit);
            using var cancelled = new CancellationTokenSource();
            var waiting = output.PlayAsync(Samples, 44100, cancelled.Token); cancelled.Cancel();
            var error = await CaptureAsync(() => waiting);
            check(error is OperationCanceledException && child.Kills == 0 && starts == 1,
                "a cancelled PCM waiter never kills or replaces another active playback worker");
            reply.Release.TrySetResult();
            await first.WaitAsync(Limit);
        }
        finally { reply.Release.TrySetResult(); await output.DisposeAsync(); }

        reply = new GateReply(); child = new Child(reply) { ExitOnKill = false };
        output = new DesktopPcmOutput(_ => child);
        var active = output.PlayAsync(Samples, 44100, default);
        await reply.Entered.Task.WaitAsync(Limit);
        try
        {
            var first = output.DisposeAsync().AsTask(); var second = output.DisposeAsync().AsTask();
            await child.KillRequested.Task.WaitAsync(Limit);
            check(ReferenceEquals(first, second) && !first.IsCompleted && child.Disposes == 0,
                "concurrent PCM disposal waits for the same active playback cleanup and confirmed child exit");
            child.Exit();
            check(await CaptureAsync(() => active) is OperationCanceledException,
                "PCM shutdown cancels active playback without converting the stop into a fallback error");
            await Task.WhenAll(first, second).WaitAsync(Limit);
            check(child.Disposes == 1, "PCM shutdown disposes a confirmed worker exactly once");
            check(await CaptureAsync(() => output.PlayAsync(Samples, 44100, default)) is ObjectDisposedException,
                "disposed PCM outputs refuse to start another playback");
        }
        finally { child.Exit(); reply.Release.TrySetResult(); await output.DisposeAsync(); }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Limit); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Child : DesktopPcmOutput.IWorker
    {
        private readonly TaskCompletionSource exited = Signal();
        public MemoryStream Stdin { get; } = new();
        public MemoryStream Reply { get; } = new();
        private readonly Stream stdout;
        public Stream Stderr { get; set; } = new MemoryStream();
        public Stream Input => Stdin;
        public Stream Output => stdout;
        public Stream Error => Stderr;
        public bool HasExited { get; private set; }
        public bool ExitOnKill { get; init; } = true;
        public Exception? KillFailure { get; init; }
        public TaskCompletionSource KillRequested { get; } = Signal();
        public int Kills { get; private set; }
        public int Waits { get; private set; }
        public int Disposes { get; private set; }
        public Child(string reply) { Reply.Write(Encoding.UTF8.GetBytes(reply)); Reply.Position = 0; stdout = Reply; }
        public Child(Stream stdout) { this.stdout = stdout; }
        public void Exit() { HasExited = true; exited.TrySetResult(); }
        public void Kill()
        {
            Kills++; KillRequested.TrySetResult();
            if (KillFailure is not null) throw KillFailure;
            if (ExitOnKill) Exit();
        }
        public Task WaitForExitAsync(CancellationToken ct) { Waits++; return exited.Task.WaitAsync(ct); }
        // Keep probe streams readable for post-cleanup assertions. The fake has
        // no unmanaged handles and no actual child process to release.
        public void Dispose() { Disposes++; }
    }

    private sealed class GateReply : Stream
    {
        private readonly MemoryStream response = new("ok\n"u8.ToArray());
        public TaskCompletionSource Entered { get; } = Signal();
        public TaskCompletionSource Release { get; } = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(ct); return await response.ReadAsync(buffer, ct); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ByteFlood(int remaining) : Stream
    {
        public int Remaining { get; private set; } = remaining;
        public int MaximumRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); MaximumRead = Math.Max(MaximumRead, buffer.Length);
            var count = Math.Min(Remaining, buffer.Length); buffer.Span[..count].Fill((byte)'x'); Remaining -= count;
            return ValueTask.FromResult(count);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
