using System.Text.Json;
using Tanayomi;
using Tanayomi.Desktop;

public static class UtauJoinObservationTests
{
    // The ordinary suite checks the observer, not subjective sound quality.
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).ConfigureAwait(false))
            .Single(value => value.Name == "hosinohito");
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank).ConfigureAwait(false);
        var observed = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync(WorldReference.ReviewText, bank).ConfigureAwait(false);
        check(observed.Audio.SampleRate == baseline.Audio.SampleRate && observed.Audio.Samples.SequenceEqual(baseline.Audio.Samples),
            "comparison join observation leaves the unchanged 180ms B reference sample-for-sample identical");
        check(observed.Joins is IList<UtauJoinObservation> { IsReadOnly: true } && observed.Joins.Count > 0 &&
            observed.Joins.All(join =>
            {
                var anchor = baseline.Anchors[join.UnitIndex];
                return join.UnitIndex > 0 && baseline.Anchors[join.UnitIndex - 1].Alias == join.PreviousAlias &&
                    anchor.Alias == join.Alias && anchor.StartSample == join.StartSample && anchor.VowelOnsetSample == join.VowelOnsetSample &&
                    join.PreutteranceSamples > 0 && join.ActualFadeSamples > 0 && join.ActualFadeSamples <= join.PreutteranceSamples &&
                    join.VoicingWindowSamples > 0 && join.VoicingWindowSamples <= join.PreutteranceSamples &&
                    join.MinimumOffsetSamples >= 0 && join.MinimumOffsetSamples < join.PreutteranceSamples &&
                    join.IncomingMinimumOffsetSamples >= 0 && join.IncomingMinimumOffsetSamples < join.PreutteranceSamples &&
                    new[] { join.PeriodSamples, join.PreviousRms, join.JoinedMinimumRms, join.IncomingAtJoinedMinimumRms,
                        join.IncomingMinimumRms, join.BeforeOnsetRms, join.AfterOnsetRms }.All(value => double.IsFinite(value) && value >= 0);
            }),
            "join observations are immutable bounded scalars on actual adjacent sound placements and never cross a pause");
        check(new[] { "に", "わ", "れ", "み", "り", "を", "ん", "く", "て" }
            .All(alias => observed.Joins.Any(join => join.Alias == alias)),
            "fixed public review covers voiced handoffs plus nasal and stop-closure controls without arbitrary input");
        var carried = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync(WorldReference.ReviewText, bank,
            carryPureVowels: true).ConfigureAwait(false);
        var carriedTiming = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank,
            carryPureVowels: true).ConfigureAwait(false);
        check(carriedTiming.Anchors.SequenceEqual(baseline.Anchors) && carriedTiming.Audio.Samples.Length == baseline.Audio.Samples.Length &&
            carriedTiming.Audio.Samples.SequenceEqual(carried.Audio.Samples),
            "comparison-only vowel carry preserves every real 180ms placement and observation adds no PCM change");
        var changed = observed.Joins.Zip(carried.Joins)
            .Where(pair => pair.First.ActualFadeSamples != pair.Second.ActualFadeSamples).ToArray();
        check(changed.Length == 1 && changed[0].First is { PreviousAlias: "ん", Alias: "を", VoicedLead: false, PureVowelCarryApplied: false } &&
            changed[0].Second is { VoicedLead: false, PureVowelCarryApplied: true } &&
            changed[0].Second.ActualFadeSamples == changed[0].Second.PreutteranceSamples &&
            !carried.Audio.Samples.SequenceEqual(observed.Audio.Samples),
            "bundled review vowel-carry changes only the observed n-to-o handoff without relabelling its original voicing decision");
        check(observed.Joins.Zip(carried.Joins).Where(pair => pair.First.Alias != "を").All(pair => pair.First == pair.Second) &&
            observed.Joins.Zip(carried.Joins).All(pair => pair.First.IncomingMinimumRms == pair.Second.IncomingMinimumRms &&
                pair.First.IncomingMinimumOffsetSamples == pair.Second.IncomingMinimumOffsetSamples &&
                pair.First.PeriodSamples == pair.Second.PeriodSamples),
            "vowel carry leaves consonant and closure joins and every incoming recorded segment unchanged before global gain");
        const string pauseControl = "を、を";
        var pauseBaseline = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync(pauseControl, bank).ConfigureAwait(false);
        var pauseCarry = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync(pauseControl, bank, carryPureVowels: true).ConfigureAwait(false);
        check(pauseBaseline.Joins.Count == 0 && pauseCarry.Joins.Count == 0 && pauseBaseline.Audio.Samples.SequenceEqual(pauseCarry.Audio.Samples),
            "vowel carry neither invents a preceding voice at utterance start nor carries a vowel across a pause");
        CheckPairValidation(baseline, check);
        await CheckShortReleaseControlsAsync(check);
    }

    private static async Task CheckShortReleaseControlsAsync(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var sine = new short[22050];
        for (var i = 0; i < sine.Length; i++) sine[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
        var noiseLead = sine.ToArray(); var random = new Random(13579);
        for (var i = 0; i < 3969; i++) noiseLead[i] = (short)random.Next(-6000, 6001);
        var vowelPath = Path.Combine(fixture.DirectoryPath, "vowel.wav");
        var uncertainPath = Path.Combine(fixture.DirectoryPath, "uncertain.wav");
        await File.WriteAllBytesAsync(vowelPath, new PcmAudio(sine, 44100).ToWaveBytes());
        await File.WriteAllBytesAsync(uncertainPath, new PcmAudio(noiseLead, 44100).ToWaveBytes());
        var bank = new Voicebank("release controls", fixture.DirectoryPath,
            [new(vowelPath, "あ", 0, 200, -400, 90, 0), new(vowelPath, "う", 0, 200, -400, 90, 0), new(uncertainPath, "い", 0, 200, -400, 90, 0)], 0);
        // The dictionary reads "ああ" as "あー", producing one extended unit
        // and no join. "あう" was checked against the actual managed renderer:
        // two units, one voiced join, pre/fade both 3969 samples.
        var voiced = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あう", bank);
        var voicedShort = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あう", bank, shortUnvoicedReleaseForComparison: true);
        check(voiced.Joins.Count == 1 && voiced.Joins[0].VoicedLead && voiced.Audio.Samples.SequenceEqual(voicedShort.Audio.Samples) &&
            voiced.Joins.SequenceEqual(voicedShort.Joins), "short-release comparison leaves strongly voiced connections sample-for-sample unchanged");
        var narrow = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あい", bank);
        var narrowShort = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あい", bank, shortUnvoicedReleaseForComparison: true);
        check(narrow.Joins.Count == 1 && !narrow.Joins[0].VoicedLead && narrow.Joins[0].ActualFadeSamples == 353 &&
            narrow.Audio.Samples.SequenceEqual(narrowShort.Audio.Samples) && narrow.Joins.SequenceEqual(narrowShort.Joins),
            "short-release comparison never increases an existing 8ms unvoiced fade or changes its PCM");
        var carry = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あい", bank, carryPureVowels: true);
        var carryShort = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("あい", bank, carryPureVowels: true, shortUnvoicedReleaseForComparison: true);
        check(carry.Joins.Count == 1 && carry.Joins[0].PureVowelCarryApplied && carry.Audio.Samples.SequenceEqual(carryShort.Audio.Samples) &&
            carry.Joins.SequenceEqual(carryShort.Joins), "short-release comparison preserves explicit pure-vowel carry including uncertain voiced classification");
        var pause = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("い、い", bank);
        var pauseShort = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync("い、い", bank, shortUnvoicedReleaseForComparison: true);
        check(pause.Joins.Count == 0 && pauseShort.Joins.Count == 0 && pause.Audio.Samples.SequenceEqual(pauseShort.Audio.Samples),
            "short-release comparison never bridges pauses or changes post-pause attacks");
    }

    private static void CheckPairValidation(UtauReferenceTiming reference, Action<bool, string> check)
    {
        var track = new WorldComparison.TrackStatistics(259.4256, 259.4256, 259.4256, 259.4256, 259.4256, 0);
        var baseline = new WorldComparison.TimingAnalysis(180, reference.Audio.Samples.Length, 2, 2048, 10, 1,
            "sp", "ap", "vuv", track, reference.Anchors);
        var alternative = baseline with { CarryPureVowels = true };
        WorldComparison.ValidateJoinPair(baseline, alternative);
        check(true, "join pair admits identical duration and actual anchors with only the explicit comparison flag enabled");
        var malformed = reference.Anchors.ToArray();
        malformed[0] = malformed[0] with { VowelOnsetSample = malformed[0].VowelOnsetSample + 1 };
        foreach (var invalid in new[] { alternative with { MoraDurationMilliseconds = 140 },
                     alternative with { SampleCount = baseline.SampleCount - 1 },
                     alternative with { Anchors = malformed }, alternative with { CarryPureVowels = false } })
        {
            var rejected = false;
            try { WorldComparison.ValidateJoinPair(baseline, invalid); }
            catch (InvalidDataException) { rejected = true; }
            check(rejected, "join comparison rejects changed tempo, duration, actual placement or a missing carry condition");
        }
    }

    // Explicit runner mode only: no user input, files, playback or WORLD load.
    // The imported bank belongs to this invocation and is always cleaned up.
    public static async Task ObserveAsync()
    {
        using var temporary = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).ConfigureAwait(false))
            .Single(value => value.Name == "hosinohito");
        var observed = await UtauSpeech.SynthesizeReferenceWithJoinObservationsAsync(WorldReference.ReviewText, bank).ConfigureAwait(false);
        var targets = new HashSet<string>(["あ", "い", "う", "え", "お", "を", "に", "わ", "れ", "み", "り", "ん", "く", "て"], StringComparer.Ordinal);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            Reference = "fixed-public-WorldReference.ReviewText",
            Stage = "B time-shaped segments and Append output, BEFORE utterance gain/PCM/WORLD",
            SampleRate = observed.Audio.SampleRate,
            SampleCount = observed.Audio.Samples.Length,
            BaseMoraMilliseconds = 180,
            MinimumWindowSamples = UtauSpeech.OutputSampleRate / 100,
            MinimumHopSamples = UtauSpeech.OutputSampleRate / 200,
            PreviousWindowMilliseconds = 20,
            BeforeOnsetWindowMilliseconds = 10,
            AfterOnsetWindowMilliseconds = 20,
            IncomingAtJoinedMinimum = "same window position as JoinedMinimum; IncomingMinimum is searched independently",
            Controls = "ん: equal OTO/pre fade; く and て: preserve natural stop closure, not voiced positives",
            Joins = observed.Joins.Where(join => targets.Contains(join.Alias)).ToArray()
        }));
    }
}
