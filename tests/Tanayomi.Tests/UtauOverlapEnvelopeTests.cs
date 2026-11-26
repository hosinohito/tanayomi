using Tanayomi;

internal static class UtauOverlapEnvelopeTests
{
    internal static async Task RunAsync(Action<bool,string> check, string? directory)
    {
        List<float> dry = [.25f,.25f,.25f,.25f,.25f,.25f,.25f,.25f];
        var coupled = dry.ToList();
        float[] input = [.25f,.25f,.25f,.25f,.25f,.25f,.125f,-.125f,.125f,-.125f];
        UtauSpeech.Append(dry, input, 6, 4);
        UtauSpeech.Append(coupled, input, 6, 4, complementary: true);
        check(dry.Max() == .5f && coupled.Max() == .25f, "coherent overlap does not double matched signals");
        check(coupled.Count == dry.Count && coupled.Skip(2+4).SequenceEqual(dry.Skip(2+4)),
            "samples beyond the shared fade, including the consonant, are unchanged");

        var transient = Enumerable.Repeat(.1f, 7938).ToArray();
        Array.Fill(transient, .22f, 950, 400);
        Array.Fill(transient, .23f, 80, 100);
        var sustained = transient.ToArray();
        UtauAttackEnvelope.Attenuate(transient, 440, pureVowel: false);
        check(transient[1150] < .18f && transient.Take(440 + 308).SequenceEqual(sustained.Take(440 + 308))
            && transient.Skip(440 + 3175).SequenceEqual(sustained.Skip(440 + 3175)),
            "source attack is attenuated without touching the consonant, steady tail or length");

        using var temp = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        await using var before = Speaker(false);
        await using var after = Speaker(true);
        await using var combined = Speaker(true, true);
        var texts = new[] { "あいうえお。", "こんにちは、ゆっくりしていってね。", "きっと、またあえるよ。", "なにぬねの、まみむめも、やゆよ。", "おはよう、今日はここまで来たの？" };
        for (var i = 0; i < texts.Length; i++)
        {
            var a = await before.SynthesizeAsync(texts[i]); var b = await after.SynthesizeAsync(texts[i]);
            check(a.Samples.Length == b.Samples.Length && !a.Samples.SequenceEqual(b.Samples), "actual phrase changes while retaining length");
            var c = await combined.SynthesizeAsync(texts[i]);
            check(b.Samples.Length == c.Samples.Length && !b.Samples.SequenceEqual(c.Samples),
                "source attack treatment remains separate and preserves utterance duration");
            if (directory is null) continue;
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{i+1}-before.wav"), a.ToWaveBytes());
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{i+1}-overlap-only.wav"), b.ToWaveBytes());
            await File.WriteAllBytesAsync(Path.Combine(directory, $"{i+1}-after.wav"), c.ToWaveBytes());
        }

        UtauSpeech Speaker(bool enable, bool attack = false) => new(new NoPlayback())
        {
            Mode = UtauSynthesisMode.Simple, Voicebank = bank, SimplePitchContourEnabled = true,
            SimpleComplementaryEnvelopeEnabled = enable, SimpleAttackEnvelopeEnabled = attack,
            // Hold later corrections off: a more even vowel need not trigger attack attenuation.
            SimplePitchPreserveEnergy = false, SimpleSourceAnchoredPitchEnabled = false
        };
    }
    private sealed class NoPlayback : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
