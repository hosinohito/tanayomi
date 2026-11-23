using System.Diagnostics;
using System.Text;
using Avalonia.Threading;
using Tanayomi;

public static class ShutdownTests
{
    private const string Secret = "shutdown-private-token:private-chat-and-path";

    public static async Task RunAsync(Action<bool, string> check)
    {
        var order = new List<string>();
        var primary = new SpeechProbe("primary", order, failDispose: true);
        var fallback = new SpeechProbe("fallback", order);
        var queue = new SpeechQueue(primary, fallback);
        var failure = await CaptureAsync(() => queue.DisposeAsync().AsTask());
        check(failure is IOException && order.SequenceEqual(["primary", "fallback"]),
            "queue shutdown disposes fallback even when primary disposal fails");
        await queue.DisposeAsync();
        check(primary.DisposeCalls == 1 && fallback.DisposeCalls == 1,
            "queue shutdown failure does not dispose owned outputs twice on a later call");

        order.Clear();
        primary = new SpeechProbe("primary", order, failDispose: true);
        fallback = new SpeechProbe("fallback", order, failDispose: true);
        queue = new SpeechQueue(primary, fallback);
        failure = await CaptureAsync(() => queue.DisposeAsync().AsTask());
        check(failure is IOException && primary.DisposeCalls == 1 && fallback.DisposeCalls == 1,
            "queue attempts both output disposals even when both throw");

        primary = new SpeechProbe("shared", [], failDispose: true);
        queue = new SpeechQueue(primary, primary);
        failure = await CaptureAsync(() => queue.DisposeAsync().AsTask());
        check(failure is IOException && primary.DisposeCalls == 1,
            "queue disposes a shared primary and fallback only once even after failure");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        primary = new SpeechProbe("active", [], handler: async (_, ct) =>
        {
            var delay = Task.Delay(Timeout.Infinite, ct);
            // Cancellation callbacks run in reverse registration order. Register
            // the throwing callback last so the resumed await cannot remove it first.
            using var callback = ct.Register(() => throw new IOException(Secret));
            entered.TrySetResult();
            await delay;
        });
        fallback = new SpeechProbe("fallback", []);
        queue = new SpeechQueue(primary, fallback);
        try
        {
            check(queue.Enqueue(ChatMessage.Manual("shutdown test")), "shutdown fixture starts one controlled active request");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            failure = await CaptureAsync(() => queue.DisposeAsync().AsTask());
            check(failure is AggregateException && primary.DisposeCalls == 1 && fallback.DisposeCalls == 1,
                "throwing cancellation callbacks cannot bypass queue output cleanup");
            check(fallback.SpeakCalls == 0, "queue shutdown cancellation never starts fallback speech");
        }
        finally { await queue.DisposeAsync(); }
    }

    // Register only after the existing headless AppBuilder initialization.
    public static void RunViewModels(Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var owned = new TestDirectory();
        var voiceRoot = Path.Combine(owned.DirectoryPath, "voices");
        Task.Run(() => Voicebanks.LoadInstalledAsync(voiceRoot)).GetAwaiter().GetResult();

        foreach (var (processorFails, pcmFails, standardFails) in new[]
        {
            (true, false, false), (false, true, false), (true, true, true)
        })
        {
            var order = new List<string>();
            var standard = new SpeechProbe("standard", order, standardFails);
            var pcm = new PcmProbe(order, pcmFails);
            var processor = new ProcessorProbe(order, processorFails);
            var model = new MainViewModel(standard, voiceRoot, pcm, utauPostprocessor: processor);
            try
            {
                PumpUntil(() => model.SelectedVoicebank is not null);
                var disposal = model.DisposeAsync().AsTask();
                PumpUntil(() => disposal.IsCompleted);
                var failure = CaptureAsync(() => disposal).GetAwaiter().GetResult();
                check(failure is IOException && processor.DisposeCalls == 1 && pcm.DisposeCalls == 1 && standard.DisposeCalls == 1,
                    "view-model shutdown finishes processor PCM and standard cleanup after a custom-output disposal failure");
                check(order.SequenceEqual(["processor", "pcm", "standard"]),
                    "custom processor failure cannot skip PCM or the queue-owned standard output");
            }
            finally { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }

        CheckCallback(voiceRoot, false, check);
        CheckCallback(voiceRoot, true, check);
        CheckBankWarmup(voiceRoot, check);
        CheckFailedBankWarmup(voiceRoot, check);
    }

    private static void CheckFailedBankWarmup(string voiceRoot, Action<bool, string> check)
    {
        var order = new List<string>();
        var standard = new SpeechProbe("standard", order);
        var pcm = new PcmProbe(order, failDispose: false);
        var processor = new ProcessorProbe(order, failDispose: false)
        {
            PrepareHandle = (_, _) => Task.FromException(new IOException(Secret))
        };
        var model = new MainViewModel(standard, voiceRoot, pcm, utauPostprocessor: processor);
        try
        {
            PumpUntil(() => model.SelectedVoicebank is not null);
            model.UseUtau = true;
            PumpUntil(() => model.Activities.Any(entry => entry.Contains("音源の事前準備に失敗しました", StringComparison.Ordinal)));
            var report = "";
            var copying = model.CopyDiagnosticsAsync(text => { report = text; return Task.CompletedTask; });
            PumpUntil(() => copying.IsCompleted);
            copying.GetAwaiter().GetResult();
            check(report.Contains("VoicePreparationFailed", StringComparison.Ordinal) &&
                report.Contains("exception=IOException", StringComparison.Ordinal) &&
                !report.Contains(Secret, StringComparison.Ordinal) && model.Activities.All(entry => !entry.Contains(Secret, StringComparison.Ordinal)),
                "failed bank warmup records the existing safe preparation diagnostic without exception text");
            model.TestText = "あ";
            model.SpeakCommand.Execute(null);
            PumpUntil(() => pcm.PlayCalls == 1);
            check(processor.ProcessCalls == 1 && standard.SpeakCalls == 0 && model.UseUtau,
                "a warmup failure leaves subsequent UTAU speech available for ordinary processing and recovery");
        }
        finally
        {
            var disposal = model.DisposeAsync().AsTask();
            PumpUntil(() => disposal.IsCompleted);
            disposal.GetAwaiter().GetResult();
        }
    }

    private static void CheckBankWarmup(string voiceRoot, Action<bool, string> check)
    {
        var oldEntered = Signal(); var oldCancelled = Signal(); var releaseOld = Signal();
        var nextEntered = Signal(); var nextCancelled = Signal(); var releaseNext = Signal();
        var finalEntered = Signal(); var finalCancelled = Signal(); var releaseFinal = Signal();
        var order = new List<string>();
        var standard = new SpeechProbe("standard", order);
        var pcm = new PcmProbe(order, failDispose: false);
        var processor = new ProcessorProbe(order, failDispose: false);
        processor.PrepareHandle = async (_, ct) =>
        {
            var index = processor.PrepareCalls;
            var entered = index == 1 ? oldEntered : index == 2 ? nextEntered : finalEntered;
            var cancelled = index == 1 ? oldCancelled : index == 2 ? nextCancelled : finalCancelled;
            var release = index == 1 ? releaseOld : index == 2 ? releaseNext : releaseFinal;
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                cancelled.TrySetResult();
                await release.Task;
                throw;
            }
        };
        var model = new MainViewModel(standard, voiceRoot, pcm, utauPostprocessor: processor);
        Task? disposal = null;
        try
        {
            PumpUntil(() => model.SelectedVoicebank is not null);
            check(!model.UseUtau && processor.PrepareCalls == 0,
                "selected-bank calibration does not consume CPU while UTAU is disabled by default");
            var original = model.SelectedVoicebank!;
            model.UseUtau = true;
            PumpUntil(() => oldEntered.Task.IsCompleted);
            var replacement = original with { Name = "warmup-next-bank" };
            model.SelectedVoicebank = replacement;
            PumpUntil(() => oldCancelled.Task.IsCompleted);
            check(ReferenceEquals(model.SelectedVoicebank, replacement) && !nextEntered.Task.IsCompleted,
                "bank selection updates immediately while its cancelled preparation finishes off the UI thread");
            releaseOld.TrySetResult();
            PumpUntil(() => nextEntered.Task.IsCompleted);
            check(processor.PreparedBanks.ToArray().SequenceEqual([original, replacement]),
                "the newest bank warmup starts only after the previous bank's cancelled request has released resources");
            model.UseUtau = false;
            PumpUntil(() => nextCancelled.Task.IsCompleted);
            check(!model.UseUtau && processor.ProcessCalls == 0 && pcm.PlayCalls == 0,
                "disabling UTAU cancels calibration without processing or playing any utterance");
            model.UseUtau = true;
            releaseNext.TrySetResult();
            PumpUntil(() => finalEntered.Task.IsCompleted);
            disposal = model.DisposeAsync().AsTask();
            PumpUntil(() => finalCancelled.Task.IsCompleted);
            check(!disposal.IsCompleted && processor.DisposeCalls == 0 && pcm.DisposeCalls == 0 && standard.DisposeCalls == 0,
                "view-model exit cancels and waits for current bank warmup before disposing its audio resources");
            releaseFinal.TrySetResult();
            PumpUntil(() => disposal.IsCompleted);
            disposal.GetAwaiter().GetResult();
            check(processor.DisposeCalls == 1 && pcm.DisposeCalls == 1 && standard.DisposeCalls == 1,
                "bank warmup cleanup completes before all normal shutdown disposals");
        }
        finally
        {
            releaseOld.TrySetResult(); releaseNext.TrySetResult(); releaseFinal.TrySetResult();
            disposal ??= model.DisposeAsync().AsTask();
            PumpUntil(() => disposal.IsCompleted);
            disposal.GetAwaiter().GetResult();
        }
    }

    private static void CheckCallback(string voiceRoot, bool failingTrace, Action<bool, string> check)
    {
        var order = new List<string>();
        var standard = new SpeechProbe("standard", order);
        var pcm = new PcmProbe(order, failDispose: true);
        var processor = new ProcessorProbe(order, failDispose: true);
        var model = new MainViewModel(standard, voiceRoot, pcm, utauPostprocessor: processor);
        using var listener = new ShutdownTrace(failingTrace);
        try
        {
            PumpUntil(() => model.SelectedVoicebank is not null);
            Trace.Listeners.Add(listener);
            Exception? escaped = null;
            try { App.Shutdown(model); } catch (Exception error) { escaped = error; }
            check(escaped is null && processor.DisposeCalls == 1 && pcm.DisposeCalls == 1 && standard.DisposeCalls == 1,
                failingTrace ? "OS shutdown callback also contains diagnostic-output failures after finishing all resource cleanup" :
                "OS shutdown callback contains disposal failures after finishing all resource cleanup");
            check(listener.Calls > 0 && !listener.Text.Contains(Secret, StringComparison.Ordinal),
                "shutdown trace reports no secret-bearing exception messages");
            if (!failingTrace)
                check(listener.Text.Contains("ApplicationShutdownFailed", StringComparison.Ordinal) &&
                    listener.Text.Contains("exception=IOException", StringComparison.Ordinal),
                    "shutdown callback retains the typed failure diagnostic without exporting paths or chat content");
            App.Shutdown(model);
            check(standard.DisposeCalls == 1 && pcm.DisposeCalls == 1 && processor.DisposeCalls == 1,
                "repeated OS shutdown callbacks do not repeat disposal of already released outputs");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            model.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> operation)
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(5)); return null; }
        catch (Exception error) { return error; }
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition()) timeout.Cancel(); };
        timer.Start();
        try { Dispatcher.UIThread.MainLoop(timeout.Token); }
        finally { timer.Stop(); }
        if (!condition()) throw new TimeoutException("Shutdown fixture did not finish.");
    }

    private sealed class SpeechProbe(string name, List<string> order, bool failDispose = false,
        Func<string, CancellationToken, Task>? handler = null) : ISpeechOutput
    {
        public string Name => name;
        public int DisposeCalls { get; private set; }
        public int SpeakCalls { get; private set; }
        public Task SpeakAsync(string text, CancellationToken ct)
        { SpeakCalls++; return handler?.Invoke(text, ct) ?? Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++; order.Add(name);
            return failDispose ? ValueTask.FromException(new IOException(Secret)) : ValueTask.CompletedTask;
        }
    }

    private sealed class PcmProbe(List<string> order, bool failDispose) : IPcmOutput
    {
        private int playCalls;
        public int PlayCalls => Volatile.Read(ref playCalls);
        public int DisposeCalls { get; private set; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Interlocked.Increment(ref playCalls); return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++; order.Add("pcm");
            return failDispose ? ValueTask.FromException(new IOException(Secret)) : ValueTask.CompletedTask;
        }
    }

    private sealed class ProcessorProbe(List<string> order, bool failDispose) : IUtauPcmPostprocessor
    {
        private int prepareCalls, processCalls;
        public int PrepareCalls => Volatile.Read(ref prepareCalls);
        public int ProcessCalls => Volatile.Read(ref processCalls);
        public System.Collections.Concurrent.ConcurrentQueue<Voicebank> PreparedBanks { get; } = new();
        public Func<Voicebank, CancellationToken, Task>? PrepareHandle { get; set; }
        public int DisposeCalls { get; private set; }
        public Task PrepareAsync(Voicebank bank, CancellationToken ct)
        {
            Interlocked.Increment(ref prepareCalls); PreparedBanks.Enqueue(bank);
            return PrepareHandle?.Invoke(bank, ct) ?? Task.CompletedTask;
        }
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct)
        { Interlocked.Increment(ref processCalls); return Task.FromResult(input); }
        public ValueTask DisposeAsync()
        {
            DisposeCalls++; order.Add("processor");
            return failDispose ? ValueTask.FromException(new IOException(Secret)) : ValueTask.CompletedTask;
        }
    }

    private sealed class ShutdownTrace(bool fail) : TraceListener
    {
        private readonly StringBuilder text = new();
        public int Calls { get; private set; }
        public string Text => text.ToString();
        public override void Write(string? message)
        {
            Calls++; text.Append(message);
            if (fail) throw new IOException(Secret);
        }
        public override void WriteLine(string? message) => Write(message);
    }
}
