using Tanayomi;
using Tanayomi.Desktop;

public static class WorldProsodyTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const double target = 259.4256;
        const int samples = 44100;
        var times = Enumerable.Range(0, 201).Select(i => i * 0.005).ToArray();
        var source = times.Select((_, i) => i < 20 || i is >= 80 and <= 90 ? 0 : i % 2 == 0 ? 220d : 440d).ToArray();
        var original = source.ToArray();
        var originalTimes = times.ToArray();
        WorldComparison.PhraseCommand[] phrases = [new(-0.1, WorldComparison.ProsodyPhraseMagnitude)];
        WorldComparison.AccentCommand[] accents = [new(0.25, 0.65, WorldComparison.ProsodyAccentMagnitude)];

        check(WorldComparison.PhraseResponse(-0.1) == 0 && WorldComparison.PhraseResponse(0) == 0 &&
            Math.Abs(WorldComparison.PhraseResponse(1 / WorldComparison.ProsodyAlpha) - WorldComparison.ProsodyAlpha / Math.E) < 1e-12,
            "Fujisaki phrase response follows the causal alpha-squared t exp(-alpha t) equation and analytic peak");
        check(WorldComparison.AccentResponse(-0.1) == 0 && WorldComparison.AccentResponse(0) == 0 &&
            Math.Abs(WorldComparison.AccentResponse(0.05) - (1 - 2 / Math.E)) < 1e-12 &&
            WorldComparison.AccentResponse(0.2) == 0.9,
            "Fujisaki accent response follows the causal critically-damped step with the original 0.9 ceiling");
        check(Math.Abs(WorldComparison.ProsodyPhraseMagnitude * WorldComparison.ProsodyAlpha / Math.E - Math.Log(2) / 24) < 1e-12 &&
            Math.Abs(WorldComparison.ProsodyAccentMagnitude * 0.9 - Math.Log(2) / 12) < 1e-12,
            "experimental command magnitudes mean 0.5-semitone phrase peak and 1-semitone accent ceiling before centering");

        var tracks = WorldComparison.BuildProsodyTracks(source, times, samples, target, phrases, accents);
        check(source.SequenceEqual(original) && times.SequenceEqual(originalTimes),
            "prosody track generation leaves the shared analyzed F0 and time arrays unchanged");
        check(tracks.Values.Length == 3 && tracks.Values.All(track => track.Length == source.Length &&
            track.Select((value, i) => (value == 0) == (source[i] == 0)).All(value => value)),
            "flat, phrase and accent tracks retain exactly the same frame count and voiced/unvoiced mask");
        check(tracks.Values[0].Select((value, i) => value == (source[i] > 0 ? target : 0)).All(value => value),
            "the flat control uses only the accepted reference F0 and never reintroduces original source jumps");
        check(tracks.Statistics.All(stats => Math.Abs(stats.MeanHz - target) < 1e-9 && stats.GeometricMeanHz <= stats.MeanHz + 1e-9),
            "all controls match voiced-duration arithmetic mean F0, with geometric mean reported separately");
        check(tracks.Values[1].Where(value => value > 0).Distinct().Count() > 1 &&
            !tracks.Values[1].SequenceEqual(tracks.Values[2]) && tracks.Statistics.All(stats => stats.MaximumAdjacentSemitones < 0.15),
            "the small phrase and hand-accent controls vary continuously across adjacent voiced 5ms frames");
        var differentPitch = source.Select(value => value > 0 ? 91d : 0).ToArray();
        var independent = WorldComparison.BuildProsodyTracks(differentPitch, times, samples, target, phrases, accents);
        check(tracks.Values.Zip(independent.Values).All(pair => pair.First.SequenceEqual(pair.Second)),
            "source pitch values do not affect a contour when the voiced mask and manual commands are fixed");

        var zero = WorldComparison.BuildProsodyTracks(source, times, samples, target, [], []);
        check(zero.Values.All(track => track.SequenceEqual(tracks.Values[0])),
            "zero phrase/accent commands reduce exactly to the flat baseline");
        var strong = WorldComparison.BuildProsodyTracks(source, times, samples, target,
            Enumerable.Range(0, 16).Select(_ => new WorldComparison.PhraseCommand(-0.1, 1)).ToArray(),
            Enumerable.Range(0, 16).Select(_ => new WorldComparison.AccentCommand(0.1, 0.8, 1)).ToArray());
        check(strong.Scale is > 0 and < 1 && strong.Values.All(track => track.All(value => value == 0 ||
                double.IsFinite(value) && Math.Abs(12 * Math.Log2(value / target)) <= 2 + 1e-9)) &&
            strong.Statistics.All(stats => Math.Abs(stats.MeanHz - target) < 1e-9),
            "one shared command coefficient bounds strong contours without framewise clipping or changing mean pitch");
        var invalid = source.ToArray(); invalid[30] = double.NaN;
        Throws(() => WorldComparison.BuildProsodyTracks(invalid, times, samples, target, phrases, accents), check,
            "nonfinite source F0 is rejected before contour construction");
        invalid[30] = -1;
        Throws(() => WorldComparison.BuildProsodyTracks(invalid, times, samples, target, phrases, accents), check,
            "negative voiced-state input is rejected instead of interpreted as silence");
        var badTimes = times.ToArray(); badTimes[50] += 0.001;
        Throws(() => WorldComparison.BuildProsodyTracks(source, badTimes, samples, target, phrases, accents), check,
            "a changed WORLD frame time grid is rejected instead of moving hand-specified accents");
        Throws(() => WorldComparison.BuildProsodyTracks(source, times, samples, double.NaN, phrases, accents), check,
            "a nonfinite reference F0 is rejected");
        Throws(() => WorldComparison.BuildProsodyTracks(source, times, samples, target, phrases, [new(0.8, 0.2, 0.1)]), check,
            "reversed accent commands are rejected rather than producing unintended pitch motion");
        Throws(() => WorldComparison.BuildProsodyTracks(source, times, samples, target, [new(0, double.PositiveInfinity)], accents), check,
            "nonfinite command magnitudes are rejected");
        Throws(() => WorldComparison.BuildProsodyTracks(source, times, samples, 40, phrases, accents), check,
            "a contour crossing the native F0 range fails explicitly instead of hard clipping its shape");
        Throws(() => WorldComparison.BuildProsodyTracks(new double[times.Length], times, samples, target, phrases, accents), check,
            "insufficient voicing does not produce misleading average-F0 comparisons");

        var anchors = new List<UtauTimingAnchor>();
        var cursor = 0;
        void Sound(string alias, int extensions = 0)
        {
            var length = 9000 + extensions * 7056;
            anchors.Add(new(anchors.Count, alias, '\0', extensions, cursor, cursor + 2000, cursor + length));
            cursor += length;
        }
        void Pause(char pause)
        {
            anchors.Add(new(anchors.Count, null, pause, 0, cursor, null, cursor + 4851)); cursor += 4851;
        }
        Sound("す", 1); Pause('っ'); Sound("と"); Pause('、');
        Sound("し", 1); Pause('っ'); Sound("と"); Pause('、');
        Sound("こ"); Pause('。'); Sound("きょ");
        foreach (var alias in new[] { "あ", "り", "が" }) Sound(alias);
        Sound("と", 1);
        Pause('。');
        var manual = WorldComparison.ManualCommands(anchors, cursor);
        var first = anchors.First(anchor => anchor.Alias is not null);
        var ga = anchors.Single(anchor => anchor.Alias == "が");
        check(manual.Phrases.Length == 4 && manual.Accents.Length == 1 &&
            Math.Abs(manual.Phrases[0].Time - (first.VowelOnsetSample!.Value / 44100d - 0.1)) < 1e-12 &&
            Math.Abs(manual.Accents[0].End - (ga.VowelOnsetSample!.Value / 44100d - 0.05)) < 1e-12,
            "manual review commands follow actual onset anchors, exclude closure pauses and preserve the intended experimental fall location");
        var wrongReading = anchors.Select(anchor => anchor.Alias == "が" ? anchor with { Alias = "か" } : anchor).ToArray();
        Throws(() => WorldComparison.ManualCommands(wrongReading, cursor), check,
            "a changed fixed review reading fails instead of silently assigning a lexical accent to another unit");
        var malformed = anchors.ToArray(); malformed[0] = malformed[0] with { VowelOnsetSample = null };
        Throws(() => WorldComparison.ManualCommands(malformed, cursor), check,
            "missing actual onset metadata is rejected instead of estimated from character count");
        var wrongLength = anchors.Select(anchor => anchor.Alias == "と" && anchor.Extensions == 1 ? anchor with { Extensions = 0 } : anchor).ToArray();
        Throws(() => WorldComparison.ManualCommands(wrongLength, cursor), check,
            "a changed pronunciation extension fails explicitly rather than shifting the experimental accent profile");

        using (var temporary = new TestDirectory())
        {
            var banks = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).ConfigureAwait(false);
            var bank = banks.Single(value => value.Name == "hosinohito");
            var actual = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank).ConfigureAwait(false);
            Console.WriteLine("WORLD fixed review timing units: " + string.Join(" ", actual.Anchors.Select(anchor =>
                anchor.Alias is null ? anchor.Pause.ToString() : anchor.Alias + new string('ー', anchor.Extensions) +
                "@" + (anchor.VowelOnsetSample!.Value / 44100.0).ToString("F3", System.Globalization.CultureInfo.InvariantCulture))));
            var actualSpoken = actual.Anchors.Where(anchor => anchor.Alias is not null).ToArray();
            check(actualSpoken.TakeLast(4).Select(anchor => anchor.Alias).SequenceEqual(new[] { "あ", "り", "が", "と" }) &&
                actualSpoken[^1].Extensions == 1,
                "the bundled fixed phrase keeps the dictionary pronunciation arigatoo as four units and one extension");
            var actualCommands = WorldComparison.ManualCommands(actual.Anchors, actual.Audio.Samples.Length);
            check(actualCommands.Phrases.Length == 4 && actualCommands.Accents.Length == 1 &&
                Math.Abs(actualCommands.Accents[0].End - (actualSpoken[^2].VowelOnsetSample!.Value / 44100.0 - 0.05)) < 1e-12,
                "real bundled-bank timing drives the four phrase commands and the intended ga-onset fall without a native run");
        }

        double[][] signals = [[2, 0, 0, 0], [-1, 1, -1, 1], [3, 0, 0, 0]];
        var copies = signals.Select(signal => signal.ToArray()).ToArray();
        var levels = WorldComparison.MatchLevels(signals);
        var rms = levels.Rms.Select((value, i) => value * levels.RmsGains[i] * levels.Headroom).ToArray();
        var peaks = levels.Peaks.Select((value, i) => value * levels.RmsGains[i] * levels.Headroom).ToArray();
        check(rms.All(value => Math.Abs(value - rms[0]) < 1e-12) && peaks.Max() <= 0.92 + 1e-12 && levels.Headroom < 1,
            "whole-utterance RMS controls share one headroom gain and avoid clipping despite different crest factors");
        check(signals.Zip(copies).All(pair => pair.First.SequenceEqual(pair.Second)) &&
            signals.Select((signal, i) => signal.Select(value => value * levels.RmsGains[i] * levels.Headroom)
                .Select((value, j) => value == 0 || Math.Abs(value / signal[j] - levels.RmsGains[i] * levels.Headroom) < 1e-12).All(value => value)).All(value => value),
            "level matching applies one constant per utterance without mutating inputs or changing their envelopes");
        Throws(() => WorldComparison.MatchLevels([[0, 0], [1, 0], [1, 0]]), check,
            "a silent condition is rejected instead of normalized as a valid comparison");
        Throws(() => WorldComparison.MatchLevels([[double.NaN, 0], [1, 0], [1, 0]]), check,
            "nonfinite synthesis samples cannot become PCM comparison files");
        Throws(() => WorldComparison.MatchLevels([[1], [1, 0], [1]]), check,
            "level controls reject differing durations");
        double[][] timingSignals = [[2, 0], [1, -1, 1, -1]];
        var timingCopies = timingSignals.Select(signal => signal.ToArray()).ToArray();
        var timingLevels = WorldComparison.MatchLevels(timingSignals, allowDifferentLengths: true);
        var timingRms = timingLevels.Rms.Select((value, i) => value * timingLevels.RmsGains[i] * timingLevels.Headroom).ToArray();
        check(Math.Abs(timingRms[0] - timingRms[1]) < 1e-12 &&
            timingLevels.Peaks.Select((value, i) => value * timingLevels.RmsGains[i] * timingLevels.Headroom).Max() <= 0.92 + 1e-12 &&
            timingSignals.Zip(timingCopies).All(pair => pair.First.SequenceEqual(pair.Second)),
            "two timing conditions match whole-utterance RMS and common headroom without padding, stretching or mutating either duration");
        Throws(() => WorldComparison.MatchLevels(timingSignals), check,
            "different-length level matching requires the timing comparison's explicit opt-in");
        Throws(() => WorldComparison.MatchLevels([[1]], allowDifferentLengths: true), check,
            "level comparison cannot silently accept a missing condition");
        Throws(() => WorldComparison.MatchLevels([[1], [1], [1], [1]], allowDifferentLengths: true), check,
            "level comparison remains bounded to two or three conditions");
        Throws(() => WorldComparison.MatchLevels([[], [1]], allowDifferentLengths: true), check,
            "an empty timing signal is rejected before RMS division");
    }

    private static void Throws(Action action, Action<bool, string> check, string description)
    {
        var caught = false;
        try { action(); }
        catch (InvalidDataException) { caught = true; }
        check(caught, description);
    }
}
