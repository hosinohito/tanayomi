using Tanayomi;

// Synthetic, owned WAVs and identity processors only. No native execution,
// physical playback, persistent utterance or timing threshold is required.
internal static class UtauUnusedPitchAnalysisTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var entries = new List<OtoEntry>();
        foreach (var (alias, hz) in new[] { ("あ", 220), ("い", 330) })
        {
            var path = Path.Combine(temporary.DirectoryPath, alias + ".wav");
            var wave = new PcmAudio(Enumerable.Range(0, 22050).Select(i =>
                (short)(6000 * Math.Sin(2 * Math.PI * hz * i / 44100))).ToArray(), 44100);
            await File.WriteAllBytesAsync(path, wave.ToWaveBytes());
            entries.Add(new(path, alias, 0, 200, -400, 20, 10));
        }
        var bank = new Voicebank("unused pitch analysis fixture", temporary.DirectoryPath, entries, 0);
        const string text = "あー、いっあいあい。";
        var expected = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        await using (var reference = new UtauSpeech(new NoPlayback()) { Voicebank = bank, ApplyPitch = false })
        {
            var cold = await reference.SynthesizeAsync(text); var warm = await reference.SynthesizeAsync(text);
            check(cold.Samples.SequenceEqual(expected.Audio.Samples) && warm.Samples.SequenceEqual(cold.Samples) && reference.PitchOnlyAnalysisCount == 0,
                "cold and reused B rendering preserve exact PCM without unused voice-basis rendering or onset-pitch analysis");
        }

        var processor = new Processor(expected); var timingFallbacks = 0;
        await using (var speech = new UtauSpeech(new NoPlayback(), processor) { Voicebank = bank })
        {
            speech.ProsodyTimingFallback += _ => timingFallbacks++;
            var cold = await speech.SynthesizeAsync(text); var warm = await speech.SynthesizeAsync(text);
            check(cold.Samples.SequenceEqual(expected.Audio.Samples) && warm.Samples.SequenceEqual(cold.Samples) &&
                processor.Last!.Anchors.SequenceEqual(expected.Anchors) && processor.ContextCalls == 2 && processor.FlatCalls == 0 && timingFallbacks == 2,
                "a late planned timing guard still retries ordinary timing with identical long-vowel pause sokuon PCM and actual anchors");
            check(speech.PitchOnlyAnalysisCount == 0,
                "both the discarded planned render and ordinary-timing retry skip old-Shift-only analysis in the WORLD path");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            var cancelled = false;
            try { await speech.SynthesizeAsync(text, stop.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            check(cancelled && processor.ContextCalls == 2 && processor.FlatCalls == 0 && timingFallbacks == 2,
                "pre-cancelled B rendering performs neither context processing nor timing or flat fallback");
        }

        await using (var pitched = new UtauSpeech(new NoPlayback()) { Voicebank = bank })
        {
            var rendered = await pitched.SynthesizeAsync("あい");
            check(pitched.PitchOnlyAnalysisCount > 0 && rendered.SampleRate == 44100 && rendered.Samples.Any(sample => sample != 0),
                "the existing processor-free Shift mode retains its needed pitch-basis and onset analysis");
        }
        await using var guarded = new UtauSpeech(new NoPlayback(), new FlatOnly()) { Voicebank = bank };
        await guarded.SynthesizeAsync("あ");
        guarded.Voicebank = bank with { Entries = [entries[0] with { Consonant = 9000 }] };
        check(await InvalidAsync(() => guarded.SynthesizeAsync("あ")),
            "skipping unused pitch analysis never bypasses the actual recording's OTO range guards");
        guarded.Voicebank = bank;
        await File.WriteAllBytesAsync(entries[0].WavePath, new byte[64]);
        check(await InvalidAsync(() => guarded.SynthesizeAsync("あ")),
            "a changed corrupt WAV is revalidated in B mode rather than served from the existing decoded cache");
    }

    private static async Task<bool> InvalidAsync(Func<Task> action)
    { try { await action(); return false; } catch (InvalidDataException) { return true; } }
    private sealed class Processor(UtauReferenceTiming baseline) : IUtauProsodyPostprocessor
    {
        public int ContextCalls { get; private set; } public int FlatCalls { get; private set; }
        public UtauReferenceTiming? Last { get; private set; }
        public Task<IUtauUtterancePlan?> CreatePlanAsync(string text, IReadOnlyList<UtauReadingUnit> reading, CancellationToken ct)
        {
            var timing = baseline.Anchors.Select(unit => new UtauTimingAllocation(unit.Alias, unit.Pause, unit.Extensions,
                unit.EndSample - (unit.VowelOnsetSample ?? unit.StartSample))).ToArray();
            var lastSound = Array.FindLastIndex(timing, unit => unit.Alias is not null);
            timing[lastSound] = timing[lastSound] with { DurationSamples = 1 };
            return Task.FromResult<IUtauUtterancePlan?>(new Plan(timing, rendered => { ContextCalls++; Last = rendered; }));
        }
        public Task<PcmAudio> ProcessAsync(PcmAudio audio, Voicebank bank, CancellationToken ct)
        { FlatCalls++; return Task.FromResult(audio); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Plan(IReadOnlyList<UtauTimingAllocation> timing, Action<UtauReferenceTiming> receive) : IUtauUtterancePlan
    {
        public IReadOnlyList<UtauTimingAllocation> Timing => timing;
        public bool CanProcessOrdinaryTiming => true;
        public Task<PcmAudio> ProcessAsync(UtauReferenceTiming rendered, Voicebank bank, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); receive(rendered); return Task.FromResult(rendered.Audio); }
    }
    private sealed class FlatOnly : IUtauPcmPostprocessor
    {
        public Task<PcmAudio> ProcessAsync(PcmAudio audio, Voicebank bank, CancellationToken ct) => Task.FromResult(audio);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class NoPlayback : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct) =>
            throw new InvalidOperationException("Offline synthesis must not play audio.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
