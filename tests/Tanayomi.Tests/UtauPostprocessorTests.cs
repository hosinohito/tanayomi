using System.Collections.Concurrent;
using Tanayomi;

public static class UtauPostprocessorTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = await MakeBankAsync(temporary.DirectoryPath);
        check(UtauSpeech.HasRequiredAliases("アー、あっい。", bank),
            "reference alias preflight shares kana normalization, long vowels and pause parsing with synthesis");
        check(!UtauSpeech.HasRequiredAliases("あきゃ", bank), "reference preflight reports a missing combined mora alias");
        check(UtauSpeech.HasRequiredAliases("あ", bank with { Entries = [bank.Entries[0] with { WavePath = Path.Combine(temporary.DirectoryPath, "absent.wav") }] }),
            "alias preflight does not misclassify a missing WAV as an absent alias");
        await ThrowsAsync<InvalidDataException>(() => Task.FromResult(UtauSpeech.HasRequiredAliases("ーあ", bank)), check,
            "invalid reading remains an error rather than a false alias-coverage result");
        const string text = "あい、あー";
        await using var unpitched = new UtauSpeech(new RecordingPcm()) { Voicebank = bank, ApplyPitch = false };
        var expected = await unpitched.SynthesizeAsync(text);
        var reference = await UtauSpeech.SynthesizeReferenceAsync(text, bank);
        check(reference.SampleRate == expected.SampleRate && reference.Samples.SequenceEqual(expected.Samples),
            "public reference synthesis uses exactly the B renderer without playback or postprocessing");
        await CheckReferenceTimingAsync(bank, check);

        var output = new RecordingPcm();
        var referenceInsideProcessor = false;
        var processor = new Processor(async (input, selected, ct) =>
        {
            // This must not recursively wait on the live UtauSpeech gate.
            var nested = await UtauSpeech.SynthesizeReferenceAsync(text, selected, ct).WaitAsync(TimeSpan.FromSeconds(3));
            referenceInsideProcessor = nested.Samples.SequenceEqual(expected.Samples);
            return new(input.Samples.Select(sample => (short)(sample / 2)).ToArray(), input.SampleRate);
        });
        await using (var voice = new UtauSpeech(output, processor) { Voicebank = bank, ApplyPitch = true })
        {
            var synthesized = await voice.SynthesizeAsync(text);
            check(processor.Calls == 1 && processor.Input!.Samples.SequenceEqual(expected.Samples),
                "postprocessor receives B PCM exactly once even when legacy pitch shaping is explicitly enabled");
            check(ReferenceEquals(processor.Bank, bank) && referenceInsideProcessor,
                "postprocessor receives the selected bank and can generate a separate calibration reference without deadlock");
            check(synthesized.Samples.SequenceEqual(expected.Samples.Select(sample => (short)(sample / 2))) && output.Calls == 0,
                "synthesis returns processed PCM while reference and synthesis APIs remain inaudible");
            await voice.SpeakAsync(text, CancellationToken.None);
            check(processor.Calls == 2 && output.Calls == 1 && output.Last!.Samples.SequenceEqual(synthesized.Samples),
                "SpeakAsync plays the one processed result without a second postprocessing pass");
        }
        check(processor.DisposeCalls == 1 && output.DisposeCalls == 1,
            "UtauSpeech owns and disposes its optional processor and PCM output");

        await CheckRejectedResultAsync(bank, check);
        await CheckBankSnapshotAsync(bank, check);
        await CheckCancellationAsync(bank, check);
        await CheckQueueRecoveryAsync(bank, check);
        await CheckPreparationAsync(bank, check);
        await CheckDisposalAsync(bank, check);
    }

    private static async Task CheckReferenceTimingAsync(Voicebank bank, Action<bool, string> check)
    {
        var variedPre = bank with
        {
            Entries = [bank.Entries[0], bank.Entries[1] with { Preutterance = 65, Consonant = 130 }]
        };
        const string text = "あーいっあ、い。";
        var baseline = await UtauSpeech.SynthesizeReferenceAsync(text, variedPre);
        var rendered = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, variedPre);
        check(rendered.Audio.SampleRate == baseline.SampleRate && rendered.Audio.Samples.SequenceEqual(baseline.Samples),
            "comparison timing anchors leave every B PCM sample and duration unchanged");
        check(rendered.Anchors.Count == 7 && rendered.Anchors.Select(anchor => anchor.UnitIndex).SequenceEqual(Enumerable.Range(0, 7)) &&
            rendered.Anchors.Where(anchor => anchor.Alias is not null).Select(anchor => anchor.Alias).SequenceEqual(["あ", "い", "あ", "い"]),
            "comparison anchors follow parsed mora and pause units rather than characters or duplicate pronunciation parsing");
        var first = rendered.Anchors[0]; var next = rendered.Anchors[1];
        var firstOnset = first.VowelOnsetSample ?? -1; var nextOnset = next.VowelOnsetSample ?? -1;
        check(first.Extensions == 1 && first.StartSample == 0 && firstOnset > 0 &&
            first.EndSample - firstOnset == 44100 * 340 / 1000,
            "a length mark remains one extended mora with its actual rendered onset and tail");
        check(next.StartSample < first.EndSample && nextOnset == first.EndSample &&
            nextOnset - next.StartSample != firstOnset - first.StartSample,
            "different OTO preutterances advance segment starts while preserving the true next vowel-onset anchor");
        var pauses = rendered.Anchors.Where(anchor => anchor.Alias is null).ToArray();
        check(pauses.Select(anchor => anchor.Pause).SequenceEqual(['っ', '、', '。']) &&
            pauses.All(anchor => anchor.VowelOnsetSample is null && anchor.Extensions == 0 &&
                anchor.EndSample - anchor.StartSample == 44100 * (anchor.Pause == 'っ' ? 85 : 110) / 1000 &&
                rendered.Audio.Samples[anchor.StartSample..anchor.EndSample].All(sample => sample == 0)),
            "sokuon and punctuation anchors identify the exact existing silent sample ranges");
        check(rendered.Anchors[3].StartSample == pauses[0].EndSample && rendered.Anchors[5].StartSample == pauses[1].EndSample &&
            rendered.Anchors[^1].EndSample == rendered.Audio.Samples.Length &&
            rendered.Anchors.All(anchor => anchor.StartSample >= 0 && anchor.EndSample <= rendered.Audio.Samples.Length && anchor.EndSample > anchor.StartSample),
            "post-pause starts and final endpoint use the real append positions within the final PCM");
        check(rendered.Anchors is IList<UtauTimingAnchor> { IsReadOnly: true },
            "comparison anchor collections are immutable snapshots");
        var explicitDefault = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, variedPre, moraDurationMilliseconds: 180);
        check(explicitDefault.Audio.Samples.SequenceEqual(baseline.Samples) && explicitDefault.Anchors.SequenceEqual(rendered.Anchors),
            "explicit comparison tempo 180 remains sample-for-sample identical to the unchanged production B reference");
        var faster = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, variedPre, moraDurationMilliseconds: 140);
        var delta = 44100 * 40 / 1000;
        var precedingSounds = 0; var exactPositions = faster.Anchors.Count == rendered.Anchors.Count;
        foreach (var (oldAnchor, newAnchor) in rendered.Anchors.Zip(faster.Anchors))
        {
            exactPositions &= oldAnchor.Alias == newAnchor.Alias && oldAnchor.Pause == newAnchor.Pause && oldAnchor.Extensions == newAnchor.Extensions &&
                newAnchor.StartSample == oldAnchor.StartSample - precedingSounds * delta &&
                (oldAnchor.VowelOnsetSample is null ? newAnchor.VowelOnsetSample is null :
                    newAnchor.VowelOnsetSample == oldAnchor.VowelOnsetSample - precedingSounds * delta);
            if (oldAnchor.Alias is not null) precedingSounds++;
            exactPositions &= newAnchor.EndSample == oldAnchor.EndSample - precedingSounds * delta;
        }
        check(exactPositions && faster.Audio.Samples.Length == baseline.Samples.Length - precedingSounds * delta,
            "tempo-only comparison moves actual append anchors by exactly 40ms per sound while preserving lead preutterance and unit order");
        check(faster.Anchors[0].EndSample - faster.Anchors[0].VowelOnsetSample == 44100 * 300 / 1000 &&
            faster.Anchors[1].VowelOnsetSample - faster.Anchors[0].VowelOnsetSample == 44100 * 300 / 1000 &&
            faster.Anchors[1].EndSample - faster.Anchors[1].VowelOnsetSample == 44100 * 140 / 1000 &&
            faster.Anchors.Where(anchor => anchor.Alias is null).All(anchor =>
                anchor.EndSample - anchor.StartSample == 44100 * (anchor.Pause == 'っ' ? 85 : 110) / 1000 &&
                faster.Audio.Samples[anchor.StartSample..anchor.EndSample].All(sample => sample == 0)),
            "140ms base tempo keeps the same additional 160ms long vowel and exact sokuon and punctuation pauses");
        foreach (var boundary in new[] { 100, 300 })
        {
            var allowed = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あ", variedPre, moraDurationMilliseconds: boundary);
            check(allowed.Anchors.Single().EndSample - allowed.Anchors.Single().VowelOnsetSample == 44100 * boundary / 1000,
                "comparison tempo accepts both bounded endpoints with a finite stable vowel segment");
        }
        foreach (var invalid in new[] { 99, 301 })
            await ThrowsAsync<ArgumentOutOfRangeException>(() => UtauSpeech.SynthesizeReferenceWithTimingAsync(text, variedPre, moraDurationMilliseconds: invalid), check,
                "comparison tempo rejects values outside its strictly bounded experimental range");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => UtauSpeech.SynthesizeReferenceWithTimingAsync(text, variedPre, cancelled.Token), check,
            "comparison timing extraction preserves synthesis cancellation instead of returning a partial plan");
    }

    private static async Task CheckPreparationAsync(Voicebank bank, Action<bool, string> check)
    {
        var entered = Signal(); var release = Signal();
        var output = new RecordingPcm();
        var processor = new Processor((input, _, _) => Task.FromResult(input))
        {
            PrepareHandle = async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
        };
        await using (var voice = new UtauSpeech(output, processor) { Voicebank = bank })
        {
            var preparing = voice.PrepareBankAsync(bank);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                var replacement = bank with { Name = "next-bank" };
                voice.Voicebank = replacement;
                using var cancel = new CancellationTokenSource();
                var waiting = voice.SynthesizeAsync("あ", cancel.Token);
                cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => waiting, check,
                    "UTAU cancels a waiting utterance without cancelling selected-bank warmup");
                check(!preparing.IsCompleted && ReferenceEquals(processor.PreparedBank, bank) && processor.Calls == 0 && output.Calls == 0,
                    "UTAU warmup holds its explicit bank snapshot and performs no speech processing or playback");
                release.TrySetResult();
                await preparing.WaitAsync(TimeSpan.FromSeconds(3));
                await voice.SynthesizeAsync("あ");
                check(ReferenceEquals(processor.Bank, replacement) && processor.PrepareCalls == 1,
                    "UTAU speech after warmup uses the new selected bank without replaying preparation");
            }
            finally { release.TrySetResult(); }
        }
        entered = Signal();
        output = new RecordingPcm();
        processor = new Processor((input, _, _) => Task.FromResult(input))
        {
            PrepareHandle = async (_, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        };
        var shuttingDown = new UtauSpeech(output, processor) { Voicebank = bank };
        var activePreparation = shuttingDown.PrepareBankAsync(bank);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await shuttingDown.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            await ThrowsAsync<OperationCanceledException>(() => activePreparation, check,
                "UTAU disposal cancels an in-flight bank preparation before releasing its processor");
            check(output.DisposeCalls == 1 && processor.DisposeCalls == 1,
                "UTAU warmup is included in owned processor and PCM lifetime cleanup");
        }
        finally { await shuttingDown.DisposeAsync(); }
    }

    private static async Task CheckBankSnapshotAsync(Voicebank bank, Action<bool, string> check)
    {
        var entered = Signal(); var release = Signal();
        var processor = new Processor((input, _, _) => Task.FromResult(input));
        processor.Handle = async (input, _, ct) =>
        {
            if (processor.Calls == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); }
            return input;
        };
        await using var voice = new UtauSpeech(new RecordingPcm(), processor) { Voicebank = bank };
        var active = voice.SynthesizeAsync("あ");
        var replacement = bank with { Name = "next-bank", Entries = bank.Entries.Reverse().ToArray() };
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            voice.Voicebank = replacement;
        }
        finally { release.TrySetResult(); }
        await active.WaitAsync(TimeSpan.FromSeconds(3));
        check(ReferenceEquals(processor.Bank, bank), "bank selection changes cannot replace the bank used to postprocess an in-flight utterance");
        await voice.SynthesizeAsync("あ");
        check(ReferenceEquals(processor.Bank, replacement), "the next utterance uses the newly selected bank for postprocessing");
    }

    private static async Task CheckRejectedResultAsync(Voicebank bank, Action<bool, string> check)
    {
        var output = new RecordingPcm();
        var processor = new Processor((input, _, _) => Task.FromResult(new PcmAudio(input.Samples, 22050)));
        await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
        await ThrowsAsync<InvalidDataException>(() => voice.SpeakAsync("あ", CancellationToken.None), check,
            "postprocessing cannot silently change the B sample rate or trigger playback of malformed output");
        processor.Handle = (input, _, _) => Task.FromResult(new PcmAudio(input.Samples[..^1], input.SampleRate));
        await ThrowsAsync<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check,
            "postprocessing cannot shorten the established mora timeline");
        processor.Handle = (input, _, _) => Task.FromResult(input);
        await voice.SpeakAsync("あ", CancellationToken.None);
        check(output.Calls == 1 && processor.Calls == 3,
            "a malformed postprocessing result affects one utterance and leaves the synthesis gate usable");
    }

    private static async Task CheckCancellationAsync(Voicebank bank, Action<bool, string> check)
    {
        foreach (var lateFailure in new[] { false, true })
        {
            var entered = Signal(); var release = Signal();
            var output = new RecordingPcm();
            var processor = new Processor(async (input, _, _) =>
            {
                entered.TrySetResult();
                await release.Task;
                if (lateFailure) throw new IOException("late worker failure");
                return input;
            });
            await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
            using var cancellation = new CancellationTokenSource();
            var speaking = voice.SpeakAsync("あ", cancellation.Token);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                cancellation.Cancel();
            }
            finally { release.TrySetResult(); }
            await ThrowsAsync<OperationCanceledException>(() => speaking, check, lateFailure
                ? "stop wins over a late postprocessor failure so it cannot trigger fallback"
                : "stop suppresses playback even when a processor returns successfully after cancellation");
            check(output.Calls == 0, "cancelled postprocessing never reaches PCM playback");
        }

        var unused = new Processor((input, _, _) => Task.FromResult(input));
        await using var preCancelled = new UtauSpeech(new RecordingPcm(), unused) { Voicebank = bank };
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => preCancelled.SynthesizeAsync("あ", stopped.Token), check,
            "pre-cancelled synthesis never starts postprocessing");
        check(unused.Calls == 0, "a cancelled request does not start a native processor");
        await ThrowsAsync<OperationCanceledException>(() => UtauSpeech.SynthesizeReferenceAsync("あ", bank, stopped.Token), check,
            "calibration reference generation observes cancellation without playback");
    }

    private static async Task CheckQueueRecoveryAsync(Voicebank bank, Action<bool, string> check)
    {
        var output = new RecordingPcm();
        var processor = new Processor((input, _, _) => Task.FromResult(input));
        processor.Handle = (input, _, _) => processor.Calls == 1
            ? Task.FromException<PcmAudio>(new IOException("worker failed")) : Task.FromResult(input);
        var fallback = new Fallback();
        var voice = new UtauSpeech(output, processor) { Voicebank = bank };
        await using (var queue = new SpeechQueue(voice, fallback))
        {
            queue.Enqueue(ChatMessage.Manual("あ"));
            await fallback.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
            queue.Enqueue(ChatMessage.Manual("い"));
            await output.Played.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(fallback.Calls == 1 && output.Calls == 1 && processor.Calls == 2,
                "postprocessor failure uses existing OS fallback and the next utterance can still use UTAU");
        }

        var blocked = Signal();
        output = new RecordingPcm();
        processor = new Processor((input, _, _) => Task.FromResult(input));
        processor.Handle = async (input, _, ct) =>
        {
            if (processor.Calls == 1) { blocked.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
            return input;
        };
        fallback = new Fallback();
        voice = new UtauSpeech(output, processor) { Voicebank = bank };
        await using (var queue = new SpeechQueue(voice, fallback))
        {
            queue.Enqueue(ChatMessage.Manual("あ"));
            await blocked.Task.WaitAsync(TimeSpan.FromSeconds(3));
            queue.Skip();
            queue.Enqueue(ChatMessage.Manual("い"));
            await output.Played.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(fallback.Calls == 0 && output.Calls == 1 && processor.Calls == 2,
                "queue Skip cancels postprocessing without fallback and releases the next utterance");
        }
    }

    private static async Task CheckDisposalAsync(Voicebank bank, Action<bool, string> check)
    {
        var order = new ConcurrentQueue<string>();
        var entered = Signal();
        var output = new RecordingPcm(() => order.Enqueue("output"));
        var processor = new Processor(async (input, _, ct) =>
        { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return input; }, () => { order.Enqueue("processor"); return ValueTask.CompletedTask; });
        var voice = new UtauSpeech(output, processor) { Voicebank = bank };
        try
        {
            var active = voice.SynthesizeAsync("あ");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var waiting = voice.SynthesizeAsync("い");
            var disposing = voice.DisposeAsync().AsTask();
            var concurrent = voice.DisposeAsync().AsTask();
            await Task.WhenAll(disposing, concurrent).WaitAsync(TimeSpan.FromSeconds(3));
            await ThrowsAsync<OperationCanceledException>(() => active, check, "Dispose cancels active postprocessing before awaiting the synthesis gate");
            await ThrowsAsync<OperationCanceledException>(() => waiting, check, "Dispose cancels synthesis already waiting behind the current processor");
            check(order.SequenceEqual(new[] { "processor", "output" }) && processor.DisposeCalls == 1 && output.DisposeCalls == 1,
                "concurrent disposal completes once in processor-before-output order");
            await ThrowsAsync<ObjectDisposedException>(() => voice.SynthesizeAsync("あ"), check,
                "disposed UTAU cannot restart postprocessing");
        }
        finally { await voice.DisposeAsync(); }

        order.Clear();
        output = new RecordingPcm(() => order.Enqueue("output"));
        processor = new Processor((input, _, _) => Task.FromResult(input), () =>
        { order.Enqueue("processor"); throw new IOException("processor cleanup failed"); });
        voice = new UtauSpeech(output, processor) { Voicebank = bank };
        await ThrowsAsync<IOException>(() => voice.DisposeAsync().AsTask(), check,
            "postprocessor cleanup failure is propagated instead of hidden");
        check(order.SequenceEqual(new[] { "processor", "output" }) && output.DisposeCalls == 1,
            "PCM output is still disposed when processor cleanup fails");
    }

    private static async Task<Voicebank> MakeBankAsync(string directory)
    {
        var entries = new List<OtoEntry>();
        foreach (var (alias, frequency) in new[] { ("あ", 220), ("い", 330) })
        {
            var path = Path.Combine(directory, alias + ".wav");
            var pcm = new PcmAudio(Enumerable.Range(0, 22050)
                .Select(i => (short)(10000 * Math.Sin(2 * Math.PI * frequency * i / 44100))).ToArray(), 44100);
            await File.WriteAllBytesAsync(path, pcm.ToWaveBytes());
            entries.Add(new(path, alias, 20, 100, -350, 25, 10));
        }
        return new("postprocessor-fixture", directory, entries, 0);
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task ThrowsAsync<T>(Func<Task> operation, Action<bool, string> check, string description) where T : Exception
    {
        var caught = false;
        try { await operation().WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (T) { caught = true; }
        check(caught, description);
    }
    private sealed class Processor(Func<PcmAudio, Voicebank, CancellationToken, Task<PcmAudio>> handle,
        Func<ValueTask>? cleanup = null) : IUtauPcmPostprocessor
    {
        private int calls, disposeCalls, prepareCalls;
        public int Calls => Volatile.Read(ref calls);
        public int DisposeCalls => Volatile.Read(ref disposeCalls);
        public int PrepareCalls => Volatile.Read(ref prepareCalls);
        public Func<Voicebank, CancellationToken, Task>? PrepareHandle { get; set; }
        public Voicebank? PreparedBank { get; private set; }
        public Func<PcmAudio, Voicebank, CancellationToken, Task<PcmAudio>> Handle { get; set; } = handle;
        public PcmAudio? Input { get; private set; }
        public Voicebank? Bank { get; private set; }
        public Task PrepareAsync(Voicebank bank, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref prepareCalls); PreparedBank = bank;
            return PrepareHandle?.Invoke(bank, cancellationToken) ?? Task.CompletedTask;
        }
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls);
            Input = new((short[])input.Samples.Clone(), input.SampleRate); Bank = bank;
            return Handle(input, bank, cancellationToken);
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref disposeCalls); return cleanup?.Invoke() ?? ValueTask.CompletedTask; }
    }
    private sealed class RecordingPcm(Action? cleanup = null) : IPcmOutput
    {
        private readonly ConcurrentQueue<PcmAudio> played = new();
        private int disposeCalls;
        public int Calls => played.Count;
        public int DisposeCalls => Volatile.Read(ref disposeCalls);
        public PcmAudio? Last => played.LastOrDefault();
        public TaskCompletionSource Played { get; } = Signal();
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            played.Enqueue(new(samples.ToArray(), sampleRate)); Played.TrySetResult();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Interlocked.Increment(ref disposeCalls); cleanup?.Invoke(); return ValueTask.CompletedTask; }
    }
    private sealed class Fallback : ISpeechOutput
    {
        private int calls;
        public int Calls => Volatile.Read(ref calls);
        public string Name => "test standard voice";
        public TaskCompletionSource Started { get; } = Signal();
        public Task SpeakAsync(string text, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Interlocked.Increment(ref calls); Started.TrySetResult(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
