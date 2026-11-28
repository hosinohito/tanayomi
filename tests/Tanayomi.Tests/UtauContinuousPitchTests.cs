using Tanayomi;

internal static class UtauContinuousPitchTests
{
    private const int Rate = 44100;
    private static double Hertz(float[] audio, int at)
    {
        var period = UtauPitch.FindPeriod(audio.AsSpan(at, Rate / 30), default);
        return period > 0 ? Rate / period : 0;
    }
    private static double Hertz(PcmAudio audio, int at) =>
        Hertz(audio.Samples.AsSpan(at, Rate / 30).ToArray().Select(x => x / 32768f).ToArray(), 0);

    internal static async Task RunAsync(Action<bool, string> check, string? directory = null)
    {
        const int length = 8820, firstPeriod = 220, secondPeriod = 286, target = 250;
        var a = Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * i / firstPeriod)).ToArray();
        var b = Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * i / secondPeriod)).ToArray();
        var track = new UtauContinuousPitch(length, target, false);
        track.Apply(a, 0, firstPeriod, default, preserveEnergy: true);
        track.Apply(b, 0, secondPeriod, default, preserveEnergy: true);
        var h1 = Hertz(a, 4200); var h2 = Hertz(b, 4200);
        check(h1 is > 165 and < 185 && h2 is > 165 and < 185 && Math.Abs(h1 - h2) < 3,
            $"different source pitches follow the same frame curve ({h1:F1}, {h2:F1} Hz)");
        track = new UtauContinuousPitch(length, target, true);
        var question = Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * i / target)).ToArray();
        track.Apply(question, 0, target, default, preserveEnergy: true);
        check(Hertz(question, 6800) > Hertz(question, 4550) + 3,
            "question rises within a single vowel, not by stepping per mora");
        var silence = new float[length];
        track.Apply(silence, 0, firstPeriod, default, preserveEnergy: true);
        check(silence.All(x => x == 0), "pitch renderer does not voice silence");

        using var fixture = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath))[0];
        await using var original = Speaker(bank, false);
        await using var candidate = Speaker(bank, true);
        var texts = new[] { "あいうえお。", "こんにちは、ゆっくりしていってね。", "きっと、またあえるよ。", "なにぬねの、まみむめも、やゆよ。", "おはよう、今日はここまで来たの？" };
        var changed = 0;
        for (var i = 0; i < texts.Length; i++)
        {
            var before = await original.SynthesizeAsync(texts[i]);
            var after = await candidate.SynthesizeAsync(texts[i]);
            var count = before.Samples.Zip(after.Samples, (x,y) => Math.Abs(x-y)).Count(x => x > 20);
            if (count > 0) changed++;
            check(before.Samples.Length == after.Samples.Length, "pitch rendering preserves phrase duration");
            if (i == 0)
            {
                // Interior positions of the five nominal 180 ms vowel units
                // in this fixed reading. Independent F0 estimation on final PCM.
                var points = Enumerable.Range(0, 5).Select(n => 4100 + 7938*n).ToArray();
                var f0a = points.Select(at => Hertz(before, at)).ToArray();
                var f0b = points.Select(at => Hertz(after, at)).ToArray();
                Console.WriteLine($"Real CV F0 (Hz) before: {string.Join(", ", f0a.Select(x => x.ToString("F1")))}; after: {string.Join(", ", f0b.Select(x => x.ToString("F1")))}");
                check(f0a.All(x => x > 0) && f0b.All(x => x > 0) &&
                    f0b.Max()/f0b.Min() < f0a.Max()/f0a.Min(),
                    "final bundled-vowel PCM has smaller inter-vowel pitch steps");
            }
            if (directory is not null)
            {
                Console.WriteLine($"Real CV {i+1}: {count} changed / {after.Samples.Length} samples");
                await File.WriteAllBytesAsync(Path.Combine(directory, $"{i+1}-before.wav"), before.ToWaveBytes());
                await File.WriteAllBytesAsync(Path.Combine(directory, $"{i+1}-after.wav"), after.ToWaveBytes());
            }
        }
        check(changed == texts.Length, "pitch mode affects all five actual CV test sentences");
        // Compare the loudness step at a fixed OTO anchor, with all other
        // simple-mode processing and utterance timing held constant.
        var onsetCases = new[] { ("あたあ。", 0.6), ("あのあ。", -0.5), ("あさあ。", 3.0), ("あがあ。", 0.3), ("あふあ。", 0.2) };
        foreach (var (text, minimumImprovement) in onsetCases)
        {
            var baseline = await OnsetPcm(false);
            var revised = await OnsetPcm(true);
            check(baseline.Length == revised.Length, $"{text}: no timing change");
            var improvement = Step(baseline) - Step(revised);
            check(improvement >= minimumImprovement, $"{text}: onset-to-tail step improves or stays level ({improvement:F2} dB)");
            async Task<short[]> OnsetPcm(bool preserve)
            {
                var sink = new NullPcm();
                await using var speaker = new UtauSpeech(sink)
                {
                    Voicebank = bank, Mode = UtauSynthesisMode.Simple,
                    SimplePitchContourEnabled = true, SimpleLexicalAccentEnabled = true,
                    SimpleComplementaryEnvelopeEnabled = true, SimpleAttackEnvelopeEnabled = true,
                    SimpleLeadHandoffEnabled = true, SimplePrefetchEnabled = true,
                    SimplePitchPreserveEnergy = preserve, SimpleSourceAnchoredPitchEnabled = false
                };
                await speaker.SpeakAsync(text, default);
                return sink.Parts.SelectMany(part => part).ToArray();
            }
        }
        static double Step(short[] samples)
        {
            const int anchor = 8688; // Fixed bundled 'a': 17 ms pre + 180 ms.
            const int stride = Rate / 200;
            double Power(int at)
            {
                double energy = 0;
                foreach (var sample in samples.AsSpan(anchor + at * stride, stride))
                    energy += (double)sample * sample;
                return energy;
            }
            return 10 * Math.Log10(Enumerable.Range(0, 9).Max(Power) /
                Enumerable.Range(15, 9).Select(Power).Order().ElementAt(4));
        }
        const string probeText = "たのさがふ。";
        var oldSource = await SourceAnchorProbe(false);
        var priorSource = await SourceAnchorProbe(true, legacyLead: true);
        var previousOnset = await SourceAnchorProbe(true, lookback: false);
        var newSource = await SourceAnchorProbe(true);
        check(previousOnset.Samples.Length == newSource.Samples.Length &&
            previousOnset.Samples.AsSpan(0, Rate * 45 / 1000).SequenceEqual(newSource.Samples.AsSpan(0, Rate * 45 / 1000)),
            "lookback keeps the unvoiced start and total duration in たのさがふ");
        double EarlyHz(PcmAudio pcm, int atMs)
        {
            var span = pcm.Samples.AsSpan(atMs * Rate / 1000, Rate * 19 / 1000);
            var data = Array.ConvertAll(span.ToArray(), x => x / 32768f);
            var periodAt = UtauPitch.FindPeriod(data, default);
            return periodAt > 0 ? Rate / periodAt : 0;
        }
        double HeadPower(PcmAudio pcm)
        {
            double power = 0;
            foreach (var sample in pcm.Samples.AsSpan(0, Rate * 19 / 1000)) power += (double)sample * sample;
            return power;
        }
        check(oldSource.Samples.Length == newSource.Samples.Length, "source-anchored pitch leaves PCM duration unchanged");
        var oldEarly = Hertz(oldSource, Rate * 55 / 1000);
        var oldLate = Hertz(oldSource, Rate * 155 / 1000);
        var newEarly = Hertz(newSource, Rate * 55 / 1000);
        var newLate = Hertz(newSource, Rate * 155 / 1000);
        check(oldEarly > 0 && oldLate > 0 && newEarly > 0 && newLate > 0 &&
            Math.Abs(newLate - newEarly) < Math.Abs(oldLate - oldEarly) * 0.55,
            $"first-vowel pitch discontinuity is reduced ({oldEarly:F0}→{oldLate:F0} vs {newEarly:F0}→{newLate:F0} Hz)");
        double StepFromSustain(PcmAudio pcm, int earlyMs, int lateMs)
        {
            var early = Hertz(pcm, Rate * earlyMs / 1000);
            var late = Hertz(pcm, Rate * lateMs / 1000);
            return early > 0 && late > 0 ? Math.Abs(late - early) : double.PositiveInfinity;
        }
        var priorTa = StepFromSustain(priorSource, 55, 155);
        var revisedTa = StepFromSustain(newSource, 55, 155);
        check(double.IsFinite(priorTa) && priorTa >= 25 && revisedTa <= 10,
            $"ta voiced onset now follows the phrase target (onset-to-sustain difference {priorTa:F0} → {revisedTa:F0} Hz)");
        var priorGa = StepFromSustain(priorSource, 3 * 180 + 55, 3 * 180 + 155);
        var revisedGa = StepFromSustain(newSource, 3 * 180 + 55, 3 * 180 + 155);
        check(double.IsFinite(priorGa) && revisedGa <= 5 && revisedGa < priorGa,
            $"ga voiced onset no longer holds a separate pitch (onset-to-sustain difference {priorGa:F0} → {revisedGa:F0} Hz)");
        check(priorSource.Samples.AsSpan(0, Rate * 12 / 1000).SequenceEqual(newSource.Samples.AsSpan(0, Rate * 12 / 1000)),
            "the leading unvoiced attack is unchanged");
        async Task<PcmAudio> SourceAnchorProbe(bool enabled, bool legacyLead = false, bool lookback = true, string text = probeText)
        {
            var sink = new NullPcm();
            await using var speaker = new UtauSpeech(sink)
            {
                Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = true,
                SimplePitchPreserveEnergy = true, SimpleSourceAnchoredPitchEnabled = enabled,
                SimpleLegacyPitchLeadForComparison = legacyLead, SimpleVoicedOnsetLookback = lookback, SimplePrefetchEnabled = true,
                SimpleLexicalAccentEnabled = true, SimpleComplementaryEnvelopeEnabled = true,
                SimpleAttackEnvelopeEnabled = true, SimpleLeadHandoffEnabled = true
            };
            await speaker.SpeakAsync(text, default);
            return new PcmAudio(sink.Parts.Single(), Rate);
        }
        foreach (var text in new[] { "あいうえお。", "なにぬねの。" })
        {
            var previous = await SourceAnchorProbe(true, lookback: false, text: text);
            var revised = await SourceAnchorProbe(true, text: text);
            var oldHead = EarlyHz(previous, 0);
            var head = EarlyHz(revised, 0);
            var later = EarlyHz(revised, 55);
            var gain = 10 * Math.Log10(HeadPower(revised) / HeadPower(previous));
            check(oldHead == 0 && head > 0 && Math.Abs(head - later) < 10 &&
                previous.Samples.Length == revised.Samples.Length,
                $"lookback earlier confirmed pitch {text}: {head:F0} Hz in first 19 ms, {later:F0} Hz later");
            check(Math.Abs(gain) < 2.5, $"lookback onset energy stays bounded {text}: {gain:F2} dB");
        }
        var noise = new Random(42);
        var onsetInput = new float[220 * 12];
        for (var i = 0; i < onsetInput.Length; i++)
            onsetInput[i] = i < 880 ? (float)(noise.NextDouble() - 0.5) * 0.06f : (float)(Math.Sin(2 * Math.PI * i / 220) * 0.14);
        var refined = UtauContinuousPitch.BacktrackVoicedOnset(onsetInput, 1100, 0, 220);
        check(refined is >= 855 and <= 954, "lookback locates the first periodic cycle without extending into noise");
        check(UtauContinuousPitch.BacktrackVoicedOnset(onsetInput, 1100, 1060, 220) >= 1060,
            "lookback cannot overwrite a preceding detected run");
        const string longText = "あいうえお。きっと、またあえるよ。おはよう、今日はここまで来たの？";
        var outA = new NullPcm(); var outB = new NullPcm();
        await using var whole = Speaker(bank, true, outA, false);
        await using var prefetched = Speaker(bank, true, outB, true);
        await whole.SpeakAsync(longText, default);
        await prefetched.SpeakAsync(longText, default);
        var reference = outA.Parts.Single();
        var assembled = outB.Parts.SelectMany(part => part).ToArray();
        var scale = (double)assembled.Max(x => Math.Abs((int)x)) / reference.Max(x => Math.Abs((int)x));
        check(outB.Parts.Count == 5 && assembled.Length == reference.Length &&
            reference.Zip(assembled, (x,y) => Math.Abs(x*scale-y)).Max() < 4,
            "phrase prefetch keeps the same shared pitch curves as whole-utterance playback");
        var sokuon = await candidate.SynthesizeAsync("あっか");
        var run = 0;
        var maximum = 0;
        foreach (var x in sokuon.Samples) { run = x == 0 ? run + 1 : 0; maximum = Math.Max(maximum, run); }
        check(maximum >= Rate * 85 / 1000, "pitch mode preserves the sokuon closure");
    }

    private static UtauSpeech Speaker(Voicebank bank, bool pitch, NullPcm? output = null, bool prefetch = false) => new(output ?? new NullPcm())
    {
        Mode = UtauSynthesisMode.Simple, Voicebank = bank,
        SimplePitchContourEnabled = pitch, SimpleVoicedJoinsEnabled = false, SimplePrefetchEnabled = prefetch, SimpleSourceAnchoredPitchEnabled = true
    };
    private sealed class NullPcm : IPcmOutput
    {
        public List<short[]> Parts { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            Parts.Add(samples.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
