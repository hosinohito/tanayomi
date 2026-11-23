using System.Net;
using System.Text;
using System.Threading.Channels;
using Tanayomi.Desktop;

internal static class LinuxSpeechTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var success = new ServerStream();
        await Client(success).SpeakAsync("こんにちは\r\n.\nCANCEL ALL\n..続き", CancellationToken.None);
        check(success.Commands.SequenceEqual(new[] { "SET SELF CLIENT_NAME tanayomi:desktop:chat", "SET SELF LANGUAGE ja", "SET SELF SSML_MODE off", "SET SELF NOTIFICATION END on", "SET SELF NOTIFICATION CANCEL on", "SPEAK" }),
            "Linux SSIP configures its own plain-text Japanese connection and closes it after END");
        check(success.Data == "こんにちは\r\n..\r\nCANCEL ALL\r\n...続き\r\n.\r\n", "Linux SSIP normalizes line endings and dot-stuffs text without executing text as commands");
        check(success.Disposed, "Linux SSIP releases its connection after successful playback");
        var closedAfterEnd = new ServerStream { RejectWritesAfterEnd = true };
        await Client(closedAfterEnd).SpeakAsync("再生完了後の切断", default);
        check(closedAfterEnd.Disposed && !closedAfterEnd.Commands.Contains("QUIT") &&
            !closedAfterEnd.Commands.Contains("CANCEL SELF"),
            "Linux completed speech remains successful when its peer accepts no commands after END");
        using (var stop = new CancellationTokenSource())
        {
            var ended = new ServerStream { OnEndRead = stop.Cancel };
            await Failure<OperationCanceledException>(() => Client(ended).SpeakAsync("完了と停止", stop.Token), check,
                "Linux explicit cancellation at END retains cancellation priority");
            check(ended.Disposed && !ended.Commands.Contains("CANCEL SELF"),
                "Linux cancellation after confirmed END releases the connection without cancelling completed speech");
        }
        check(!success.Commands.Contains("GET VOLUME") && !success.Commands.Any(x => x.StartsWith("SET SELF VOLUME", StringComparison.Ordinal)),
            "Linux 100 percent preserves the configured speech volume without overriding its baseline");
        var quiet = new ServerStream { BaselineVolume = 40 };
        var quietSpeech = Client(quiet); quietSpeech.Volume.Percent = 50;
        await quietSpeech.SpeakAsync("音量", default);
        check(quiet.Commands.Contains("GET VOLUME") && quiet.Commands.Contains("SET SELF VOLUME -30"),
            "Linux attenuates its current connection volume toward -100 and never changes other clients");
        check(LinuxSpeech.AttenuatedVolume(-40, 50) == -70 && LinuxSpeech.AttenuatedVolume(100, 100) == 100,
            "Linux attenuation preserves nondefault baselines and unity");
        var invalidVolume = new ServerStream { BaselineVolume = 101 };
        var invalidSpeech = Client(invalidVolume); invalidSpeech.Volume.Percent = 50;
        await Failure<IOException>(() => invalidSpeech.SpeakAsync("設定不正", default), check, "Linux rejects an invalid baseline instead of amplifying or guessing");
        check(!invalidVolume.Commands.Contains("SPEAK") && invalidVolume.Disposed,
            "Linux invalid volume setup never queues speech and releases its connection");
        var muteOpened = false;
        var mute = new LinuxSpeech(_ => { muteOpened = true; throw new IOException(); }); mute.Volume.Percent = 0;
        await mute.SpeakAsync("消音", default);
        check(!muteOpened, "Linux mute never connects or enqueues speech");
        using (var cancelledMute = new CancellationTokenSource())
        {
            cancelledMute.Cancel();
            await Failure<OperationCanceledException>(() => mute.SpeakAsync("消音", cancelledMute.Token), check, "Linux mute retains cancellation priority");
        }
        var finalCode = new ServerStream { FirstReply = "201-parameter\r\n200 OK\r\n" };
        await Client(finalCode).SpeakAsync("最終行の応答コード", CancellationToken.None);
        check(finalCode.Disposed && finalCode.Commands.Last() == "SPEAK", "Linux SSIP uses the final reply line's overall status as specified by the protocol");

        using (var stop = new CancellationTokenSource())
        {
            var stream = new ServerStream { EmitEnd = false, Queued = stop.Cancel, DelayCancelNotification = true };
            var speech = Client(stream).SpeakAsync("停止する発言", stop.Token);
            await stream.CancelRequested.Task.WaitAsync(TimeSpan.FromSeconds(2));
            check(!speech.IsCompleted, "Linux SSIP does not finish cancellation before the server confirms speech has stopped");
            stream.CompleteCancellation();
            await Failure<OperationCanceledException>(() => speech, check, "Linux SSIP cancellation propagates after stopping its own speech");
            check(stream.Commands.Contains("CANCEL SELF") && !stream.Commands.Any(c => c is "CANCEL ALL" or "STOP ALL") && stream.CancelNotificationRead,
                "Linux SSIP sends only CANCEL SELF and waits for the matching cancellation event");
            check(stream.Disposed, "Linux SSIP cancellation releases the socket");
        }
        using (var stop = new CancellationTokenSource())
        {
            var stream = new ServerStream { EmitEnd = false, CancelDuringQueuedReply = stop };
            await Failure<OperationCanceledException>(() => Client(stream).SpeakAsync("返信途中の停止", stop.Token), check, "Linux SSIP preserves reply framing across an interrupted partial read");
            check(stream.Commands.Contains("CANCEL SELF") && stream.CancelNotificationSent, "Linux SSIP partial-reply cancellation still addresses only its own connection");
        }
        using (var stop = new CancellationTokenSource())
        {
            var stream = new ServerStream { EmitEnd = false, CancelAfterDataWrite = stop };
            await Failure<OperationCanceledException>(() => Client(stream).SpeakAsync("送信完了が不明の停止", stop.Token), check, "Linux SSIP recovers data framing before cancelling an interrupted send");
            check(stream.Commands.Contains("CANCEL SELF") && stream.CancelNotificationRead, "Linux SSIP interrupted data transfer confirms its own terminal notification");
        }
        using (var stop = new CancellationTokenSource())
        {
            var stream = new ServerStream { EmitEnd = false, Queued = stop.Cancel, RejectCancel = true };
            await Failure<IOException>(() => Client(stream).SpeakAsync("停止確認の失敗", stop.Token), check, "Linux SSIP reports an unconfirmed stop as an error instead of pretending playback stopped");
        }

        var foreignId = new ServerStream { EndMessageId = 99 };
        await Failure<IOException>(() => Client(foreignId).SpeakAsync("別の通知", CancellationToken.None), check, "Linux SSIP rejects an END notification for a different message");
        check(foreignId.Commands.Contains("CANCEL SELF") && foreignId.Disposed, "Linux SSIP protocol failure cancels its own queued speech and frees the connection");
        var cancelledByServer = new ServerStream { EndCode = 703 };
        await Failure<IOException>(() => Client(cancelledByServer).SpeakAsync("取り消し", CancellationToken.None), check, "Linux SSIP never reports a server-cancelled utterance as successfully spoken");

        var earlyEnd = new ServerStream { EndBeforeQueueReply = true };
        await Client(earlyEnd).SpeakAsync("短い発言", CancellationToken.None);
        check(earlyEnd.Disposed && earlyEnd.Commands.Last() == "SPEAK", "Linux SSIP keeps a bounded early END notification until the queued message ID is known");
        foreach (var malformed in new[] { "serverSecret\r\n", "200 OK\n", new string('x', 1025) + "\r\n", string.Concat(Enumerable.Repeat("200-item\r\n", 33)) + "200 OK\r\n", string.Concat(Enumerable.Repeat("200-" + new string('x', 900) + "\r\n", 20)) + "200 OK\r\n" })
        {
            var stream = new ServerStream { FirstReply = malformed };
            await Failure<IOException>(() => Client(stream).SpeakAsync("形式確認", CancellationToken.None), check, "Linux SSIP rejects malformed or oversized replies without exposing server content");
            check(stream.Disposed && !stream.Commands.Contains("SPEAK"), "Linux SSIP malformed setup reply cannot enqueue speech or leak a socket");
        }
        var denied = new ServerStream { FirstReply = "500 serverSecret\r\n" };
        await Failure<IOException>(() => Client(denied).SpeakAsync("設定拒否", CancellationToken.None), check, "Linux SSIP sanitizes rejected command replies");

        var bounded = new ServerStream { EmitEnd = false };
        await Failure<TimeoutException>(() => new LinuxSpeech(_ => Task.FromResult<Stream>(bounded), TimeSpan.FromMilliseconds(300)).SpeakAsync("期限付き", CancellationToken.None), check, "Linux SSIP bounds a speech request which never reaches END");
        check(bounded.Commands.Contains("CANCEL SELF") && bounded.Disposed, "Linux SSIP timeout cancels only its own request before releasing the stream");
        var notOpened = false;
        await Failure<ArgumentException>(() => new LinuxSpeech(_ => { notOpened = true; return Task.FromResult<Stream>(new ServerStream()); }).SpeakAsync(new string('a', 2049), CancellationToken.None), check, "Linux SSIP rejects excessive input before opening a connection");
        check(!notOpened, "Linux SSIP oversized input makes no connection attempt");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Failure<OperationCanceledException>(() => Client(new ServerStream()).SpeakAsync("停止", cancelled.Token), check, "Linux SSIP honors cancellation before connecting");
        }

        check(LinuxSpeech.ResolveEndpoint(null, "/run/user/1000", null, null).ToString() == "/run/user/1000/speech-dispatcher/speechd.sock", "Linux SSIP uses the documented XDG runtime socket");
        check(LinuxSpeech.ResolveEndpoint(null, null, "/home/test/cache", "/home/test").ToString() == "/home/test/cache/speech-dispatcher/speechd.sock", "Linux SSIP follows GLib's XDG cache fallback without creating directories");
        check(LinuxSpeech.ResolveEndpoint(null, null, null, "/home/test").ToString() == "/home/test/.cache/speech-dispatcher/speechd.sock", "Linux SSIP supports the documented per-user cache fallback");
        check(LinuxSpeech.ResolveEndpoint("unix_socket:/tmp/custom.sock", null, null, null).ToString() == "/tmp/custom.sock", "Linux SSIP respects an explicit Unix socket address");
        check(LinuxSpeech.ResolveEndpoint("inet_socket:localhost:6561", null, null, null) is IPEndPoint { Port: 6561 } peer && IPAddress.IsLoopback(peer.Address), "Linux SSIP accepts explicitly configured loopback TCP only");
        foreach (var address in new[] { "inet_socket:192.168.1.2:6560", "inet_socket:example.com:6560", "inet_socket:127.0.0.1:0", "unix_socket:relative/path", "unix_socket:/tmp/a\nCANCEL ALL", "unix_socket:/" + new string('a', 108), "dbus:session" })
            await Failure<IOException>(() => Task.FromResult(LinuxSpeech.ResolveEndpoint(address, null, null, null)), check, "Linux SSIP rejects invalid or nonlocal addresses without probing them");
    }

    private static LinuxSpeech Client(ServerStream stream) => new(_ => Task.FromResult<Stream>(stream));
    private static async Task Failure<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    {
        try { await action(); }
        catch (T error) { check(!error.ToString().Contains("serverSecret", StringComparison.Ordinal), name); return; }
        check(false, name);
    }

    // In-memory duplex transport; no subprocess, socket, device, file or daemon.
    private sealed class ServerStream : Stream
    {
        public int BaselineVolume { get; init; } = 100;
        private readonly Channel<byte[]> replies = Channel.CreateUnbounded<byte[]>();
        private byte[]? current;
        private int readOffset;
        private bool receivingData;
        private bool first = true;
        private int queuedReplyBytes;
        public List<string> Commands { get; } = [];
        public string? Data { get; private set; }
        public bool EmitEnd { get; init; } = true;
        public bool EndBeforeQueueReply { get; init; }
        public bool RejectWritesAfterEnd { get; init; }
        public Action? OnEndRead { get; init; }
        private bool endRead;
        public long EndMessageId { get; init; } = 42;
        public int EndCode { get; init; } = 702;
        public string? FirstReply { get; init; }
        public Action? Queued { get; init; }
        public CancellationTokenSource? CancelDuringQueuedReply { get; init; }
        public CancellationTokenSource? CancelAfterDataWrite { get; init; }
        public bool DelayCancelNotification { get; init; }
        public bool RejectCancel { get; init; }
        public TaskCompletionSource CancelRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancelNotificationSent { get; private set; }
        public bool CancelNotificationRead { get; private set; }
        public bool Disposed { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            if (endRead && RejectWritesAfterEnd) throw new IOException("serverSecret peer closed after completed speech");
            var text = Encoding.UTF8.GetString(buffer.Span);
            if (receivingData)
            {
                receivingData = false; Data = text;
                var notification = $"{EndCode}-{EndMessageId}\r\n{EndCode}-7\r\n{EndCode} EVENT\r\n";
                if (EndBeforeQueueReply && EmitEnd) Reply(notification);
                Reply("225-42\r\n225 OK MESSAGE QUEUED\r\n");
                if (!EndBeforeQueueReply && EmitEnd) Reply(notification);
                Queued?.Invoke();
                if (CancelAfterDataWrite is { IsCancellationRequested: false } stop)
                { stop.Cancel(); throw new OperationCanceledException(stop.Token); }
                return ValueTask.CompletedTask;
            }
            var command = text.TrimEnd('\r', '\n'); Commands.Add(command);
            if (first && FirstReply is { } custom) { first = false; Reply(custom); return ValueTask.CompletedTask; }
            first = false;
            if (command == "SPEAK") { receivingData = true; Reply("230 OK RECEIVING DATA\r\n"); }
            else if (command == "CANCEL SELF")
            {
                CancelRequested.TrySetResult();
                if (RejectCancel) { Reply("500 serverSecret\r\n"); return ValueTask.CompletedTask; }
                Reply("200 OK CANCELED\r\n");
                if (!DelayCancelNotification) CompleteCancellation();
            }
            else if (command == "QUIT") Reply("231 BYE\r\n");
            else if (command == "GET VOLUME") Reply($"251-{BaselineVolume}\r\n251 OK GET RETURNED\r\n");
            else Reply(command.StartsWith("SET SELF ", StringComparison.Ordinal) ? "200 OK\r\n" : "500 INVALID COMMAND\r\n");
            return ValueTask.CompletedTask;
        }
        public void CompleteCancellation()
        { Reply("703-42\r\n703-7\r\n703 CANCELED\r\n"); CancelNotificationSent = true; }
        private void Reply(string value) => replies.Writer.TryWrite(Encoding.UTF8.GetBytes(value));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            while (current is null || readOffset == current.Length)
            {
                if (!await replies.Reader.WaitToReadAsync(ct)) return 0;
                if (replies.Reader.TryRead(out current)) readOffset = 0;
            }
            var count = Math.Min(Math.Min(buffer.Length, 7), current.Length - readOffset);
            current.AsMemory(readOffset, count).CopyTo(buffer); readOffset += count;
            if (!endRead && readOffset == current.Length && current.AsSpan().StartsWith("702-"u8))
            { endRead = true; OnEndRead?.Invoke(); }
            if (readOffset == current.Length && current.AsSpan().StartsWith("703-"u8)) CancelNotificationRead = true;
            if (CancelDuringQueuedReply is { IsCancellationRequested: false } cancellation && Data is not null && (queuedReplyBytes += count) >= 3) cancellation.Cancel();
            return count;
        }
        protected override void Dispose(bool disposing) { Disposed = true; replies.Writer.TryComplete(); base.Dispose(disposing); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
