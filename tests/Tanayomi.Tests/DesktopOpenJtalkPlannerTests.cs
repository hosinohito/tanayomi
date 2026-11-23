using System.Buffers.Binary;
using System.Text;
using Tanayomi.Desktop;

// Memory-only transport probes. This suite never loads native code, starts a
// worker, creates files, plays speech or substitutes an unverified plan.
internal static class DesktopOpenJtalkPlannerTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);
    private const string Secret = "private-plan-input-and-native-diagnostic";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var plan = Plan(); var calls = 0; string? received = null;
        await using (var planner = new DesktopOpenJtalkPlanner((text, _) =>
        { received = text; calls++; return Task.FromResult(plan); }))
        {
            var result = await planner.PlanAsync("公開文の試験", default);
            check(calls == 1 && received == "公開文の試験" && !ReferenceEquals(result, plan) && Same(result, plan),
                "planner forwards unchanged text and returns a validated immutable snapshot of all phones LF0 and VUV");
            foreach (var invalid in new[] { "", "x\0y", "\ud800", new string('a', 1025) })
                check(await CaptureAsync(() => planner.PlanAsync(invalid, default)) is InvalidDataException,
                    "planner rejects malformed or overlimit text before exchange");
            check(calls == 1, "invalid planner input never enters the native transport");
        }
        calls = 0;
        await using (var planner = new DesktopOpenJtalkPlanner((_, _) => ++calls == 1
            ? Task.FromException<OpenJtalkPlan>(new IOException(Secret)) : Task.FromResult(plan)))
        {
            var error = await CaptureAsync(() => planner.PlanAsync(Secret, default));
            check(error is IOException && !error.ToString().Contains(Secret, StringComparison.Ordinal),
                "planner failures expose fixed diagnostics without retaining input or native error text");
            check(Same(await planner.PlanAsync("再試行", default), plan),
                "a failed planner exchange permits the next valid request instead of returning a stale plan");
        }
        calls = 0;
        await using (var planner = new DesktopOpenJtalkPlanner((_, _) => ++calls switch
        {
            1 => Task.FromException<OpenJtalkPlan>(new OpenJtalkWorkerException(5)),
            2 => Task.FromResult<OpenJtalkPlan>(null!),
            3 => Task.FromResult(Plan("#")),
            _ => Task.FromResult(plan)
        }))
        {
            check(await CaptureAsync(() => planner.PlanAsync("終端", default)) is OpenJtalkWorkerException { Status: 5 },
                "terminal native status remains typed so the caller can choose explicit UTAU fallback");
            check(await CaptureAsync(() => planner.PlanAsync("欠損", default)) is IOException &&
                await CaptureAsync(() => planner.PlanAsync("不正", default)) is IOException,
                "null and invalid phone plans are failures rather than fabricated successful planning");
            check(Same(await planner.PlanAsync("回復", default), plan), "invalid plans do not poison later valid client requests");
        }
        await CheckCancellationAsync(plan, check);
        await CheckProtocolAsync(plan, check);
    }

    private static async Task CheckCancellationAsync(OpenJtalkPlan plan, Action<bool, string> check)
    {
        var entered = Signal(); var release = new TaskCompletionSource<OpenJtalkPlan>(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var planner = new DesktopOpenJtalkPlanner((_, _) => { calls++; entered.TrySetResult(); return release.Task; });
        try
        {
            var active = planner.PlanAsync("先行", default); await entered.Task.WaitAsync(Limit);
            using var cancelled = new CancellationTokenSource();
            var waiting = planner.PlanAsync("待機", cancelled.Token); cancelled.Cancel();
            check(await CaptureAsync(() => waiting) is OperationCanceledException && calls == 1 && !active.IsCompleted,
                "a cancelled planner waiter cannot start exchange or interrupt the active request");
            release.TrySetResult(plan); check(Same(await active.WaitAsync(Limit), plan), "the preceding planner request still completes after a waiter cancels");
        }
        finally { release.TrySetResult(plan); await planner.DisposeAsync(); }

        entered = Signal(); release = new(TaskCreationOptions.RunContinuationsAsynchronously); calls = 0;
        planner = new DesktopOpenJtalkPlanner((_, _) => ++calls == 1
            ? Enter(entered, release.Task) : Task.FromResult(plan));
        try
        {
            using var cancelled = new CancellationTokenSource();
            var active = planner.PlanAsync("取消", cancelled.Token); await entered.Task.WaitAsync(Limit); cancelled.Cancel();
            check(await CaptureAsync(() => active) is OperationCanceledException,
                "planner cancellation takes precedence even when the exchange ignores its token");
            release.TrySetException(new IOException(Secret));
            check(Same(await planner.PlanAsync("次", default), plan), "a late abandoned exchange failure cannot replace a later successful plan");
        }
        finally { release.TrySetResult(plan); await planner.DisposeAsync(); }

        entered = Signal(); var observedCancellation = Signal();
        planner = new DesktopOpenJtalkPlanner(async (_, ct) =>
        { entered.TrySetResult(); try { await Task.Delay(Timeout.Infinite, ct); } finally { observedCancellation.TrySetResult(); } return plan; });
        var pending = planner.PlanAsync("終了", default); await entered.Task.WaitAsync(Limit);
        var first = planner.DisposeAsync().AsTask(); var second = planner.DisposeAsync().AsTask();
        await observedCancellation.Task.WaitAsync(Limit);
        check(await CaptureAsync(() => pending) is OperationCanceledException && ReferenceEquals(first, second),
            "planner shutdown cancels active work and shares one disposal completion instead of returning early");
        await Task.WhenAll(first, second).WaitAsync(Limit);
        check(await CaptureAsync(() => planner.PlanAsync("終了後", default)) is ObjectDisposedException,
            "disposed planners cannot start another request or trigger fallback work");
    }

    private static async Task CheckProtocolAsync(OpenJtalkPlan plan, Action<bool, string> check)
    {
        var request = OpenJtalkPlanProtocol.EncodeRequest("日本語");
        using (var stream = new FragmentStream(request))
            check(Encoding.UTF8.GetString(OpenJtalkPlanProtocol.ReadRequest(stream)!) == "日本語" &&
                OpenJtalkPlanProtocol.ReadRequest(stream) is null && request.AsSpan(0, 4).SequenceEqual("OJLC"u8),
                "planner request protocol handles fragmented UTF8 input and clean EOF with distinct magic");
        var maximumText = string.Concat(Enumerable.Repeat("😀", 1024));
        check(OpenJtalkPlanProtocol.EncodeRequest(maximumText).Length == 4108,
            "planner accepts exactly 1024 Unicode scalars and 4096 UTF8 bytes without splitting a surrogate pair");
        var invalidRequests = new[] { Mutate(request, 0, value => value[0] = (byte)'X'),
            SetInt(request, 4, 2), SetInt(request, 8, 0), SetInt(request, 8, 4097),
            request[..^1], request[..5], OpenJtalkPcmProtocol.EncodeRequest("試験") };
        foreach (var bytes in invalidRequests)
            check(Rejected(() => OpenJtalkPlanProtocol.ReadRequest(new MemoryStream(bytes))),
                "wrong-version cross-protocol oversized or truncated planner requests are rejected before native use");

        var wire = Wire(plan);
        using (var stream = new FragmentStream(wire))
            check(Same(await OpenJtalkPlanProtocol.ReadResponseAsync(stream, default), plan),
                "planner response roundtrip preserves every phone boundary log-Hz value and explicit voicing flag under fragmented reads");
        foreach (var status in Enumerable.Range(1, 7))
        {
            using var stream = new MemoryStream(); OpenJtalkPlanProtocol.WriteResponse(stream, status, null); stream.Position = 0;
            check(await CaptureAsync(() => OpenJtalkPlanProtocol.ReadResponseAsync(stream, default)) is OpenJtalkWorkerException error &&
                error.Status == status && stream.Position == 28 && stream.Length == 28,
                "native planner status replies contain no partial plan and remain typed");
        }
        var invalidHeaders = new[] { Mutate(wire, 0, value => value[0] = (byte)'X'), SetInt(wire, 4, 2), SetInt(wire, 8, 8),
            SetInt(wire, 12, 44100), SetInt(wire, 16, 0), SetInt(wire, 20, 0), SetInt(wire, 20, 8193),
            SetInt(wire, 24, 0), SetInt(wire, 24, int.MaxValue), SetInt(wire, 8, 5) };
        foreach (var bytes in invalidHeaders)
        {
            using var stream = new MemoryStream(bytes);
            check(await CaptureAsync(() => OpenJtalkPlanProtocol.ReadResponseAsync(stream, default)) is InvalidDataException && stream.Position == 28,
                "invalid planner metadata is rejected from its fixed header before reading or allocating a body");
        }
        var frameStart = 28 + plan.Phones.Count * 40;
        var invalidBodies = new[]
        {
            Mutate(wire, 28, value => value.AsSpan(28, 32).Fill((byte)'x')),
            Mutate(wire, 29, value => value[29] = (byte)'#'),
            Mutate(wire, 32, value => value[32] = (byte)'x'),
            SetInt(wire, 60, 1), SetInt(wire, 28 + 40 + 32, 3),
            SetInt(wire, 28 + (plan.Phones.Count - 1) * 40 + 36, plan.FrameCount - 1),
            Mutate(wire, frameStart, value => BinaryPrimitives.WriteDoubleLittleEndian(value.AsSpan(frameStart), double.NaN)),
            Mutate(wire, frameStart, value => BinaryPrimitives.WriteDoubleLittleEndian(value.AsSpan(frameStart), Math.Log(220))),
            Mutate(wire, frameStart + 8, value => value[frameStart + 8] = 2),
            Mutate(wire, frameStart + 5 * 9, value => BinaryPrimitives.WriteDoubleLittleEndian(value.AsSpan(frameStart + 5 * 9), Math.Log(39))),
            wire[..^1], wire[..27]
        };
        foreach (var bytes in invalidBodies)
            check(await CaptureAsync(() => OpenJtalkPlanProtocol.ReadResponseAsync(new MemoryStream(bytes), default)) is InvalidDataException or EndOfStreamException,
                "planner rejects bad phone slots padding continuity incomplete coverage LF0 VUV and truncated payloads without producing a partial plan");

        var slots = new byte[(plan.Phones.Count + 1) * 32];
        var starts = new int[plan.Phones.Count + 1]; var ends = new int[starts.Length];
        for (var i = 0; i < plan.Phones.Count; i++)
        {
            Encoding.ASCII.GetBytes(plan.Phones[i].Symbol, slots.AsSpan(i * 32, 32));
            starts[i] = plan.Phones[i].StartFrame; ends[i] = plan.Phones[i].EndFrame;
        }
        var lf0 = plan.Lf0.Append(double.NaN).ToArray(); var voiced = plan.Voiced.Append((byte)255).ToArray();
        var native = OpenJtalkPlanProtocol.FromNative(slots, starts, ends, lf0, voiced, plan.Phones.Count, plan.FrameCount, 48000, 240);
        slots[0] = (byte)'x'; starts[0] = 123; lf0[5] = double.NaN; voiced[5] = 255;
        check(Same(native, plan), "native used-range conversion snapshots buffers and ignores unused capacity sentinels");
        check(Rejected(() => OpenJtalkPlanProtocol.FromNative([], starts, ends, lf0, voiced, plan.Phones.Count, plan.FrameCount, 48000, 240)),
            "undersized native plan buffers are rejected before decoding");
        using (var untouched = new MemoryStream())
        {
            check(Rejected(() => OpenJtalkPlanProtocol.WriteResponse(untouched, 0, Plan("#"))) && untouched.Length == 0,
                "plan serialization validates the entire response before committing a successful header");
        }

        var maximumPhones = Enumerable.Range(0, 8192).Select(i => new OpenJtalkPhone("a", i, i == 8191 ? 9000 : i + 1)).ToArray();
        var maximum = new OpenJtalkPlan(maximumPhones, Enumerable.Repeat(Math.Log(220), 9000).ToArray(), Enumerable.Repeat((byte)1, 9000).ToArray());
        var maximumWire = Wire(maximum);
        check(maximumWire.Length == 28 + OpenJtalkPlanProtocol.MaximumBodyBytes &&
            Same(await OpenJtalkPlanProtocol.ReadResponseAsync(new MemoryStream(maximumWire), default), maximum),
            "the exact bounded 8192-phone 9000-frame response is supported without imposing a smaller undocumented limit");
        using var blocked = new BlockedStream(); using var cancellation = new CancellationTokenSource();
        var read = OpenJtalkPlanProtocol.ReadResponseAsync(blocked, cancellation.Token);
        await blocked.Entered.Task.WaitAsync(Limit); cancellation.Cancel();
        check(await CaptureAsync(() => read) is OperationCanceledException,
            "planner response cancellation interrupts a blocked pipe without waiting for a native reply");
    }

    private static OpenJtalkPlan Plan(string first = "sil")
    {
        OpenJtalkPhone[] phones = [new(first, 0, 2), new("k", 2, 5), new("o", 5, 10), new("N", 10, 12),
            new("cl", 12, 14), new("t", 14, 17), new("o", 17, 22), new("pau", 22, 25), new("sil", 25, 30)];
        var lf0 = new double[30]; var voiced = new byte[30];
        foreach (var i in Enumerable.Range(5, 5).Concat(Enumerable.Range(17, 5))) { lf0[i] = Math.Log(220 + i); voiced[i] = 1; }
        return new(phones, lf0, voiced);
    }
    private static bool Same(OpenJtalkPlan a, OpenJtalkPlan b) => a.SampleRate == b.SampleRate && a.FramePeriod == b.FramePeriod &&
        a.Phones.SequenceEqual(b.Phones) && a.Lf0.SequenceEqual(b.Lf0) && a.Voiced.SequenceEqual(b.Voiced);
    private static byte[] Wire(OpenJtalkPlan plan)
    { using var stream = new MemoryStream(); OpenJtalkPlanProtocol.WriteResponse(stream, 0, plan); return stream.ToArray(); }
    private static byte[] Mutate(byte[] source, int _, Action<byte[]> change)
    { var result = source.ToArray(); change(result); return result; }
    private static byte[] SetInt(byte[] source, int offset, int value) => Mutate(source, offset,
        result => BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(offset), value));
    private static bool Rejected(Action action)
    { try { action(); return false; } catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { return true; } }
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action().WaitAsync(Limit); return null; } catch (Exception error) { return error; } }
    private static Task<OpenJtalkPlan> Enter(TaskCompletionSource entered, Task<OpenJtalkPlan> pending) { entered.TrySetResult(); return pending; }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class FragmentStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(3, count));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(3, buffer.Length)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
    }
    private sealed class BlockedStream : Stream
    {
        public TaskCompletionSource Entered { get; } = Signal();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return 0; }
        public override bool CanRead => true; public override bool CanWrite => false; public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
