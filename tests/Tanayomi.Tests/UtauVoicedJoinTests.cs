using Tanayomi;

internal static class UtauVoicedJoinTests
{
    internal static async Task RunAsync(Action<bool, string> check, string? waveDirectory = null)
    {
        const int length = 1323, period = 200;
        var outgoing = Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * i / period)).ToArray();
        var incoming = outgoing.Select(x => -x).ToArray();
        var blended = outgoing.ToArray();
        check(UtauVoicedJoin.TryBlend(blended, incoming, 7000, 1200, period, period, default),
            "periodic out-of-phase join is accepted");
        var center = length - 600;
        var energy = blended.Skip(center - 60).Take(120).Sum(x => x * x);
        check(energy > 40 && blended[0] == outgoing[0] && blended[^1] == incoming[^1],
            "phase correction prevents cancellation and keeps both edge samples");
        var closure = incoming.ToArray();
        Array.Clear(closure, 0, 550);
        foreach (var (sound, duration, otherPeriod) in new[] { (closure, 7000, 200d), (incoming, 250, 200d), (incoming, 7000, 320d) })
        {
            var dry = outgoing.ToArray();
            check(!UtauVoicedJoin.TryBlend(dry, sound, duration, 1200, period, otherPeriod, default) && dry.SequenceEqual(outgoing),
                "closure, short or incompatible joins leave the old path untouched");
        }

        var phaseTail = outgoing.ToArray();
        var phaseIncoming = incoming.ToArray();
        check(UtauVoicedJoin.TryAlignRetunedTail(phaseTail, phaseIncoming, default) &&
            phaseTail[0] == outgoing[0] && phaseTail[^1] == outgoing[^1] && phaseIncoming.SequenceEqual(incoming),
            "retuned join aligns only the middle of the outgoing overlap");
        double MixedEnergy(float[] tail) => Enumerable.Range(center - 60, 120)
            .Select(i => (tail[i] + incoming[i]) * 0.5).Sum(v => v * v);
        check(MixedEnergy(phaseTail) > 20 && MixedEnergy(outgoing) < 0.01,
            "retuned phase join prevents the synthetic overlap cancellation");
        var protectedTail = outgoing.ToArray();
        var silentLead = new float[length];
        check(!UtauVoicedJoin.TryAlignRetunedTail(protectedTail, silentLead, default) && protectedTail.SequenceEqual(outgoing),
            "retuned alignment never voices a silent incoming segment");
        using var fixture = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath))[0];
        await using var before = new UtauSpeech(new NullPcm()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = false, SimpleVoicedJoinsEnabled = false };
        await using var after = new UtauSpeech(new NullPcm()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = false, SimpleVoicedJoinsEnabled = true };
        var changed = 0;
        var statements = new[] { "あいうえお。あおいあおい。", "こんにちは、ゆっくりしていってね。", "きっと、またあえるよ。", "なにぬねの、まみむめも、やゆよ。", "こんにちは。ゆっくりしていってね。こんばんは、今日は配信を見に来てくれてありがとう。" };
        for (var i = 0; i < statements.Length; i++)
        {
            var a = await before.SynthesizeAsync(statements[i]);
            var b = await after.SynthesizeAsync(statements[i]);
            var delta = a.Samples.Zip(b.Samples, (x, y) => Math.Abs(x - y)).Count(x => x > 4);
            check(a.Samples.Length == b.Samples.Length, "real CV bank keeps statement duration");
            if (delta > 0) changed++;
            if (waveDirectory is not null)
            {
                Console.WriteLine($"Real CV {i + 1}: {delta} changed samples / {a.Samples.Length}");
                await File.WriteAllBytesAsync(Path.Combine(waveDirectory, $"{i + 1}-before.wav"), a.ToWaveBytes());
                await File.WriteAllBytesAsync(Path.Combine(waveDirectory, $"{i + 1}-after.wav"), b.ToWaveBytes());
            }
        }
        check(changed > 0, "new joins are exercised on the bundled CV bank");
        var changesInFlow = 0;
        foreach (var statement in new[] { "あいうえお。", "こんにちは。たなよみの読み上げテストです。", "なにぬねの。", "あっか。あさあ。" })
        {
            var (old, revised) = (await Retuned(statement, false), await Retuned(statement, true));
            var changedSamples = old.Zip(revised).Count(pair => pair.First != pair.Second);
            Console.WriteLine($"Retuned voiced join: {statement} changed={changedSamples}/{old.Length}");
            check(old.Length == revised.Length, "retuned voiced join preserves phrase lengths");
            if (statement == "あっか。あさあ。")
                check(changedSamples == 0 && HasZeroRun(revised, 44100 * 85 / 1000),
                    "the actual closure and fricative controls stay bit-identical");
            else if (changedSamples > 0)
            {
                changesInFlow++;
                var changedAt = Enumerable.Range(0, old.Length).Where(i => old[i] != revised[i]).ToArray();
                var centerOfEdit = changedAt[changedAt.Length / 2];
                const int frame = 220; // 5 ms
                var start = Math.Max(0, centerOfEdit - frame / 2);
                var beforeRms = Rms(old, start, frame);
                var afterRms = Rms(revised, start, frame);
                var localGain = 20 * Math.Log10(afterRms / beforeRms);
                Console.WriteLine($"Retuned center {centerOfEdit * 1000.0 / 44100:F1} ms: RMS {beforeRms:F1} -> {afterRms:F1} ({localGain:+0.0;-0.0;0.0} dB)");
                check(localGain >= 0.5 && localGain <= 10, "actual retuned crossover gains energy without an excessive increase");
            }
        }
        check(changesInFlow > 0, "retuned join is exercised in actual streaming CV audio");
        async Task<short[]> Retuned(string text, bool enabled)
        {
            var sink = new NullPcm();
            await using var speaker = new UtauSpeech(sink)
            {
                Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = true,
                SimpleSourceAnchoredPitchEnabled = true, SimpleRetunedVoicedJoinEnabled = enabled,
                SimpleComplementaryEnvelopeEnabled = true, SimplePrefetchEnabled = true
            };
            await speaker.SpeakAsync(text, default);
            return sink.Parts.SelectMany(x => x).ToArray();
        }
        await UtauSimpleRhythmTests.RunAsync(check, bank);
        var sokuon = await after.SynthesizeAsync("あっか");
        check(HasZeroRun(sokuon.Samples, 44100 * 85 / 1000), "85 ms sokuon closure survives");
    }

    private static double Rms(short[] pcm, int start, int count)
    {
        double power = 0;
        foreach (var sample in pcm.AsSpan(start, count)) power += (double)sample * sample;
        return Math.Sqrt(power / count);
    }

    private static bool HasZeroRun(short[] samples, int minimum)
    {
        var run = 0;
        foreach (var sample in samples)
        {
            run = sample == 0 ? run + 1 : 0;
            if (run >= minimum) return true;
        }
        return false;
    }

    private sealed class NullPcm : IPcmOutput
    {
        internal List<short[]> Parts { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            Parts.Add(samples.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
