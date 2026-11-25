using System.Diagnostics;
using Tanayomi;

internal static class UtauSimplePrefetchTests
{
    private const string ShortText = "こんにちは";
    private const string LongText = "こんにちは。ゆっくりしていってね。こんばんは、今日は配信を見に来てくれてありがとう。";

    private sealed class Probe(Func<int, CancellationToken, Task>? hold = null) : IPcmOutput
    {
        public readonly List<short[]> Parts = [];
        public Action? First { get; init; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (rate != 44100) throw new InvalidDataException();
            Parts.Add(samples.ToArray());
            if (Parts.Count == 1) First?.Invoke();
            return hold?.Invoke(Parts.Count, ct) ?? Task.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Fallback : ISpeechOutput
    {
        public string Name => "fallback";
        public int Calls;
        public Task SpeakAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static UtauSpeech Create(Voicebank bank, IPcmOutput output, bool enabled) => new(output)
    { Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePrefetchEnabled = enabled, SimplePitchContourEnabled = false, SimpleVoicedJoinsEnabled = false };

    public static async Task RunAsync(Action<bool, string> check, bool benchmark = false)
    {
        UtauReadingUnit[] reading = [new("き",0,'\0'), new(null,0,'っ'), new("て",0,'\0'),
            new(null,0,'。'), new(null,0,'、'), new("か",0,'\0'), new(null,0,'?')];
        check(UtauSpeech.SimplePhraseRanges(reading).SequenceEqual(new Range[] { 0..5, 5..7 }),
            "prefetch splits only at punctuation and never loses or doubles pause units");
        using var fixture = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath))[0];
        var reference = new Probe(); var trial = new Probe();
        await using var baseline = Create(bank, reference, false);
        await using var candidate = Create(bank, trial, true);
        await baseline.SpeakAsync(ShortText, default);
        await candidate.SpeakAsync(ShortText, default);
        check(trial.Parts.Count == 1 && trial.Parts[0].SequenceEqual(reference.Parts[0]), "single phrase remains bit-identical");
        trial.Parts.Clear(); reference.Parts.Clear();
        await baseline.SpeakAsync(LongText, default);
        await candidate.SpeakAsync(LongText, default);
        var assembled = trial.Parts.SelectMany(part => part).ToArray();
        var whole = reference.Parts.Single();
        check(trial.Parts.Count == 4 && assembled.Length == whole.Length, "phrase scheduling preserves whole-comment length");
        check(trial.Parts.Take(3).All(part => part.TakeLast(4410).All(sample => sample == 0)),
            "phrase boundaries reuse existing punctuation silence");
        var scale = (double)assembled.Max(s => Math.Abs((int)s)) / whole.Max(s => Math.Abs((int)s));
        check(scale is >= 0.919 and <= 1.001 &&
            whole.Zip(assembled, (a, b) => Math.Abs(a * scale - b)).Max() < 4,
            "all phrases share a single gain and preserve the bundled voice PCM shape");

        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holding = new Probe((i, ct) => i == 1 ? Task.Delay(Timeout.InfiniteTimeSpan, ct) : Task.CompletedTask)
        { First = () => entered.TrySetResult() };
        await using var cancellable = Create(bank, holding, true);
        var speaking = cancellable.SpeakAsync(LongText, stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        stop.Cancel();
        var canceled = false;
        try { await speaking.WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (OperationCanceledException) { canceled = true; }
        check(canceled && holding.Parts.Count == 1, "stop during playback prevents stale prefetch and releases synthesis");
        await cancellable.SpeakAsync(ShortText, default).WaitAsync(TimeSpan.FromSeconds(10));
        check(holding.Parts.Count == 2, "following utterance recovers after stop");

        var late = new Probe((i, _) => i == 2 ? Task.FromException(new IOException()) : Task.CompletedTask);
        var fallback = new Fallback();
        var error = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SpeechQueue(Create(bank, late, true), fallback))
        {
            queue.Notice += n => { if (n.Kind == "エラー") error.TrySetResult(); if (n.Kind == "完了") finished.TrySetResult(); };
            queue.Enqueue(ChatMessage.Manual(LongText));
            await error.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(fallback.Calls == 0 && late.Parts.Count == 2, "failure after first submission does not repeat the comment in SAPI");
            queue.Enqueue(ChatMessage.Manual(ShortText));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(late.Parts.Count == 3, "queue reads the next comment after a late playback failure");
        }

        var unavailable = bank with { Entries = bank.Entries.Where(entry => entry.Alias != "て").ToArray() };
        check(!UtauSpeech.HasRequiredAliases("あ。て", unavailable), "missing-alias control really lacks a later sound");
        var noPrefix = new Probe(); var earlyFallback = new Fallback();
        var fallbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using (var queue = new SpeechQueue(Create(unavailable, noPrefix, true), earlyFallback))
        {
            queue.Notice += n => { if (n.Kind == "完了") fallbackFinished.TrySetResult(); };
            queue.Enqueue(ChatMessage.Manual("あ。て"));
            await fallbackFinished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            check(noPrefix.Parts.Count == 0 && earlyFallback.Calls == 1,
                "missing alias in a later phrase is caught before any PCM and falls back once");
        }

        var volume = new PlaybackVolume();
        var magnitudes = new Probe { First = () => volume.Percent = 25 };
        await using var adjustable = Create(bank, new VolumePcmOutput(magnitudes, volume), true);
        await adjustable.SpeakAsync(LongText, default);
        check(magnitudes.Parts.SelectMany(part => part).SequenceEqual(assembled), "UI volume change does not change later phrases");
        await adjustable.SpeakAsync(ShortText, default);
        reference.Parts.Clear();
        await baseline.SpeakAsync(ShortText, default);
        check(magnitudes.Parts[^1].SequenceEqual(volume.Apply(reference.Parts.Single()).Samples.ToArray()),
            "volume change applies to the next utterance");
        if (!benchmark) return;

        async Task<double> Measure(string text, bool enable)
        {
            var start = 0L;
            var ms = 0d;
            var sink = new Probe { First = () => ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds };
            await using var speaker = Create(bank, sink, enable);
            start = Stopwatch.GetTimestamp();
            await speaker.SpeakAsync(text, default);
            return ms;
        }
        foreach (var (label, text) in new[] { ("short", ShortText), ("long", LongText) })
        {
            var a = new List<double>(); var b = new List<double>();
            for (var i = 0; i < 9; i++)
            {
                if ((i & 1) == 0) { a.Add(await Measure(text, false)); b.Add(await Measure(text, true)); }
                else { b.Add(await Measure(text, true)); a.Add(await Measure(text, false)); }
            }
            Console.WriteLine($"{label}: first PCM submission median, baseline {a.Order().ElementAt(4):F2} ms, trial {b.Order().ElementAt(4):F2} ms (9 paired runs; no audio device)");
        }
    }
}
