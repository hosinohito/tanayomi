using Tanayomi;

internal static class UtauLexicalAccentTests
{
    internal static async Task RunAsync(Action<bool,string> check, string? path = null)
    {
        var fixtures = new[] { "雨が。", "飴が。", "箸を。", "橋を。", "端を。" };
        foreach (var text in fixtures.Append("こんにちは、きょうはいい天気ですね。").Append("きっと雨がふる？"))
        {
            var read = JapaneseReading.ToAccentReading(text, default);
            check(read.Reading == JapaneseReading.ToProsodicReading(text), "accent metadata does not change the reading");
        }
        bool[] Profile(string input) => UtauSpeech.ParseSimpleAccentReading(input, default).Where(x => x.Alias is not null)
            .Select(x => x.LexicalHigh).ToArray();
        check(Profile(fixtures[0]).SequenceEqual(new[] { true,false,false }) && Profile(fixtures[1]).SequenceEqual(new[] { false,true,true }),
            "dictionary distinguishes initial drop from a flat noun and attached particle");
        check(Profile(fixtures[2]).SequenceEqual(new[] { true,false,false }) &&
            Profile(fixtures[3]).SequenceEqual(new[] { false,true,false }) &&
            Profile(fixtures[4]).SequenceEqual(new[] { false,true,true }), "three patterns distinguish the same reading hashi");
        check(!Profile("こんにちは。たなよみの読み上げテストです。").Any(x => x),
            "a dictionary fragment inside an unknown kana name does not create an accent peak");
        check(Profile("飴が落ちる").Take(3).SequenceEqual(new[] { false, true, true }) &&
            Profile("雨が降る").Take(3).SequenceEqual(new[] { true, false, false }),
            "a recognized particle follows the lexical pattern in the middle of a sentence");
        using var dir = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(dir.DirectoryPath))[0];
        await using var old = Create(false);
        await using var updated = Create(true);
        var oldRain = await old.SynthesizeAsync(fixtures[0]); var oldCandy = await old.SynthesizeAsync(fixtures[1]);
        check(oldRain.Samples.SequenceEqual(oldCandy.Samples), "original identical reading had identical waveform");
        var newRain = await updated.SynthesizeAsync(fixtures[0]); var newCandy = await updated.SynthesizeAsync(fixtures[1]);
        check(newRain.Samples.Length == oldRain.Samples.Length && newCandy.Samples.Length == oldCandy.Samples.Length,
            "accent leaves duration untouched");
        static double Hz(PcmAudio audio, int at)
        {
            var chunk = audio.Samples.AsSpan(at,1470).ToArray().Select(x => x / 32768f).ToArray();
            return 44100 / UtauPitch.FindPeriod(chunk, default);
        }
        var rain1=Hz(newRain,4200); var rain2=Hz(newRain,12138);
        var candy1=Hz(newCandy,4200); var candy2=Hz(newCandy,12138);
        Console.WriteLine($"Rain first/second {rain1:F1}/{rain2:F1} Hz, candy {candy1:F1}/{candy2:F1} Hz");
        check(double.IsFinite(rain1+rain2+candy1+candy2) && rain1 > rain2 && candy1 < candy2,
            "final PCM reflects opposite pitch motion for rain and candy");
        const string combined = "雨が。橋を。飴が。";
        var sink = new NoPlayback();
        await using (var streaming = Create(true, sink))
            await streaming.SpeakAsync(combined, default);
        var stitched = sink.Parts.SelectMany(p => p).ToArray();
        var whole = (await updated.SynthesizeAsync(combined)).Samples;
        var scale = (double)stitched.Max(x => Math.Abs((int)x)) / whole.Max(x => Math.Abs((int)x));
        check(sink.Parts.Count == 3 && stitched.Length == whole.Length &&
            stitched.Zip(whole, (a,b) => Math.Abs(a-b*scale)).Max() < 4, "same accents and placements with phrase prefetch");
        const string sample = "たなよみの読み上げテストです。";
        await using var reported = Create(true);
        reported.VoiceTuning = new(0, 100, curve: new UtauPitchCurve(), boundaryRange: 0);
        var measured = await reported.SynthesizeAsync(sample);
        var firstYo = Hz(measured, 2 * 7938 + 3000);
        var secondYo = Hz(measured, 5 * 7938 + 3000);
        check(Math.Abs(firstYo - secondYo) < 4, $"the two yo sounds in the reported text are no longer separated by a false lexical peak ({firstYo:F0}/{secondYo:F0} Hz)");
        await using var neutralBoundary = Create(true);
        await using var raisedBoundary = Create(true);
        neutralBoundary.VoiceTuning = new(0, 0, curve: new UtauPitchCurve(), boundaryRange: 0);
        raisedBoundary.VoiceTuning = new(0, 0, curve: new UtauPitchCurve(), boundaryRange: 100);
        var plainQuestion = await neutralBoundary.SynthesizeAsync("あいうえお？");
        var raisedQuestion = await raisedBoundary.SynthesizeAsync("あいうえお？");
        check(plainQuestion.Samples.Length == raisedQuestion.Samples.Length &&
            Math.Abs(Hz(plainQuestion, 4100) - Hz(raisedQuestion, 4100)) < 3 &&
            Hz(raisedQuestion, 4 * 7938 + 6000) > Hz(plainQuestion, 4 * 7938 + 6000) + 14,
            "question-ending strength affects only the late pitch even with lexical accent disabled");
        if (path is null) return;
        foreach (var (name, text) in new[] { ("ame-rain", fixtures[0]), ("ame-candy", fixtures[1]), ("hashi-chopsticks", fixtures[2]),
            ("hashi-bridge", fixtures[3]), ("hashi-edge", fixtures[4]), ("phrase", "明日の朝は雨が降りますか？") })
        {
            var a = await old.SynthesizeAsync(text); var b = await updated.SynthesizeAsync(text);
            await File.WriteAllBytesAsync(Path.Combine(path, name + "-before.wav"), a.ToWaveBytes());
            await File.WriteAllBytesAsync(Path.Combine(path, name + "-after.wav"), b.ToWaveBytes());
        }

        UtauSpeech Create(bool accent, NoPlayback? output = null) => new(output ?? new NoPlayback())
        {
            Mode = UtauSynthesisMode.Simple, Voicebank = bank,
            SimplePitchContourEnabled = true, SimpleLexicalAccentEnabled = accent,
            SimpleComplementaryEnvelopeEnabled = true, SimpleAttackEnvelopeEnabled = true, SimplePrefetchEnabled = output is not null
        };
    }

    private sealed class NoPlayback : IPcmOutput
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
