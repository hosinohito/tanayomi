using Tanayomi;

internal static class UtauSimpleRhythmTests
{
    internal static async Task RunAsync(Action<bool, string> check, Voicebank bank)
    {
        var shapes = UtauSpeech.ParseSimpleAccentReading("あいうえお、かきくけこ。", default);
        var offsets = UtauSimpleRhythm.Plan(shapes, _ => true, default);
        check(offsets.Take(5).Sum() == 0 && offsets.Skip(6).Sum() == 0 &&
            offsets[4] == 1058 && offsets[10] == 1587 && offsets[5] == 0,
            "rhythm redistributes only within each phrase, with a larger sentence-final lengthening");
        var closed = UtauSpeech.ParseSimpleAccentReading("あいっかー。", default);
        check(UtauSimpleRhythm.Plan(closed, _ => true, default).All(x => x == 0) &&
            UtauSimpleRhythm.Plan(shapes, _ => false, default).All(x => x == 0),
            "rhythm leaves explicit long vowels, closure and unconfirmed tails unchanged");
        var differences = 0;
        foreach (var text in new[] { "あいうえお。", "こんにちは。たなよみの読み上げテストです。", "あっか。" })
        {
            var old = await Render(text, false, false);
            var revised = await Render(text, true, false);
            var streamed = await Render(text, true, true);
            var diff = old.Zip(revised).Count(p => p.First != p.Second);
            check(old.Length == revised.Length && revised.Length == streamed.Length,
                "actual rhythm trial preserves total and prefetched speech length");
            if (text == "あっか。")
                check(diff == 0, "the actual closure control is bit-identical");
            else if (diff > 0)
                differences++;
            Console.WriteLine($"Rhythm trial: {text} changed {diff}/{old.Length} samples");
        }
        check(differences == 2, "rhythm variations affect both actual reading examples");

        async Task<short[]> Render(string text, bool enabled, bool prefetch)
        {
            var sink = new Sink();
            await using var speaker = new UtauSpeech(sink)
            {
                Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = true,
                SimpleSourceAnchoredPitchEnabled = true, SimpleContextRhythmEnabled = enabled,
                SimpleRetunedVoicedJoinEnabled = true, SimplePrefetchEnabled = prefetch
            };
            await speaker.SpeakAsync(text, default);
            return sink.Samples.ToArray();
        }
    }

    private sealed class Sink : IPcmOutput
    {
        internal List<short> Samples { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            Samples.AddRange(samples.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
