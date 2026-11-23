using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauTimingPlanTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath)).Single(value => value.Name == "hosinohito");
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank);
        var plan = baseline.Anchors.Select(unit => new UtauTimingAllocation(unit.Alias, unit.Pause, unit.Extensions,
            unit.EndSample - (unit.VowelOnsetSample ?? unit.StartSample))).ToArray();
        var same = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan);
        check(same.Audio.Samples.SequenceEqual(baseline.Audio.Samples) && same.Anchors.SequenceEqual(baseline.Anchors),
            "explicit timing allocation reconstructed from actual onsets preserves the full normal B waveform including long vowels and pauses");
        var longer = plan.ToArray();
        longer[0] = longer[0] with { DurationSamples = longer[0].DurationSamples + 441 };
        var extended = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, longer);
        check(extended.Audio.Samples.Length == baseline.Audio.Samples.Length + 441 &&
            extended.Anchors[0].VowelOnsetSample == baseline.Anchors[0].VowelOnsetSample &&
            extended.Anchors.Skip(1).Zip(baseline.Anchors.Skip(1)).All(pair =>
                pair.First.StartSample == pair.Second.StartSample + 441 && pair.First.EndSample == pair.Second.EndSample + 441),
            "one allocated vowel extension moves later placements by its exact sample count without rearticulation or extra long-vowel time");
        foreach (var invalid in new[]
        {
            plan[..^1],
            Replace(plan, 0, plan[0] with { Alias = "あ" }),
            Replace(plan, 0, plan[0] with { Extensions = 0 }),
            Replace(plan, 0, plan[0] with { DurationSamples = 1 }),
            Replace(plan, 1, plan[1] with { Pause = '、' }),
            Replace(plan, 1, plan[1] with { DurationSamples = 0 })
        })
        {
            var rejected = false;
            try { await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, invalid); }
            catch (InvalidDataException) { rejected = true; }
            catch (UtauTimingPlanException) { rejected = true; }
            check(rejected, "a mismatched or too-short timing plan fails explicitly instead of changing the reading or silently falling back to fixed timing");
        }
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = false;
        try { await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped, "planned comparison rendering preserves cancellation and never plays its PCM");
    }

    private static UtauTimingAllocation[] Replace(UtauTimingAllocation[] original, int index, UtauTimingAllocation value)
    {
        var result = original.ToArray(); result[index] = value; return result;
    }
}
