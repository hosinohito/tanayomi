using System.Security.Cryptography;
using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauLandmarkOverrideTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath)).Single(value => value.Name == "hosinohito");
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank);
        var plan = baseline.Anchors.Select(unit => new UtauTimingAllocation(unit.Alias, unit.Pause, unit.Extensions,
            unit.EndSample - (unit.VowelOnsetSample ?? unit.StartSample))).ToArray();
        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan);
        var empty = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan,
            landmarkOverrides: Array.Empty<UtauLandmarkOverride>());
        check(ordinary.Audio.Samples.SequenceEqual(baseline.Audio.Samples) && empty.Audio.Samples.SequenceEqual(baseline.Audio.Samples) &&
            ordinary.Anchors.SequenceEqual(baseline.Anchors) && empty.Anchors.SequenceEqual(baseline.Anchors) &&
            ordinary.SourceRanges.Count == 0 && empty.SourceRanges.Count == 0,
            "null and empty comparison landmarks preserve the exact normal B PCM, long vowels, pauses and placements");
        check(baseline.RenderedTiming is IList<UtauRenderedTiming> { IsReadOnly: true } &&
            baseline.RenderedTiming.SequenceEqual(ordinary.RenderedTiming) &&
            baseline.RenderedTiming.Count == baseline.Anchors.Count(a => a.Alias is not null) &&
            baseline.RenderedTiming.All(d => d.TransitionSamples >= 0 && d.StableOutputSamples >= 0 &&
                d.LeadSamples == baseline.Anchors[d.UnitIndex].VowelOnsetSample - baseline.Anchors[d.UnitIndex].StartSample &&
                d.FadeSamples >= 0 && d.FadeSamples <= d.LeadSamples),
            "reference timing diagnostics are immutable actual-render scalars and preserve the same PCM across equivalent allocations");
        var leads = baseline.Anchors.Select(a => a.VowelOnsetSample is int onset ? (int?)(onset - a.StartSample) : null).ToArray();
        var sameLeads = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, leadingSamples: leads);
        check(sameLeads.Audio.Samples.SequenceEqual(baseline.Audio.Samples) && sameLeads.Anchors.SequenceEqual(baseline.Anchors),
            "explicit original consonant leads preserve every sample and anchor without altering normal reference synthesis");
        var longerLead = (int?[])leads.Clone(); longerLead[0] += 441;
        var longer = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, leadingSamples: longerLead);
        check(longer.Audio.Samples.Length == baseline.Audio.Samples.Length + 441 &&
            longer.Anchors[0].VowelOnsetSample == baseline.Anchors[0].VowelOnsetSample + 441 &&
            longer.Anchors.Skip(1).Select((a, i) => a.StartSample == baseline.Anchors[i + 1].StartSample + 441 &&
                a.EndSample == baseline.Anchors[i + 1].EndSample + 441).All(value => value),
            "a first consonant extension changes its lead and later positions by the exact requested samples without extending vowels");
        check(await RejectedAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, leadingSamples: [441])),
            "comparison consonant timing rejects a partial unit mapping rather than applying leads to unrelated units");
        foreach (var invalidLead in new[] { 0, -1, 30871 })
        {
            var invalid = (int?[])leads.Clone(); invalid[0] = invalidLead;
            check(await RejectedAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, leadingSamples: invalid)),
                "comparison consonant lengths reject nonpositive and overlimit durations before rendering");
        }
        var pauseLead = (int?[])leads.Clone(); pauseLead[Array.FindIndex(plan, a => a.Alias is null)] = 441;
        check(await RejectedAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, plan, leadingSamples: pauseLead)),
            "comparison consonant timing never assigns a consonant to a pause or closure");

        var source = bank.Entries.Single(value => value.Alias == "て");
        using (var file = File.OpenRead(source.WavePath))
            check(Convert.ToHexString(SHA256.HashData(file)).Equals("21a2934b5bdd5d2367c76c8e13d94778bd507bcf5da4247816d11d029e87192d", StringComparison.OrdinalIgnoreCase),
                "the explicit te landmark regression uses the measured original recording without modifying it");
        check(plan.Length > 27 && plan[27].Alias == "て", "the measured comparison landmark addresses the exact fixed-text unit");
        var shortened = plan.ToArray(); shortened[27] = shortened[27] with { DurationSamples = 2205 };
        var refused = false;
        try { await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, shortened); }
        catch (UtauTimingPlanException error) { refused = error.UnitIndex == 27 && error.RequestedSamples == 2205 && error.MinimumSamples == 3969; }
        check(refused, "a short te allocation still fails the original transition guard without an explicit landmark");
        var actual = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, shortened,
            landmarkOverrides: [new(27, 71, 1764)]);
        var range = actual.SourceRanges.Single();
        check(range.UnitIndex == 27 && range.SampleRate == 44100 && range.StartSample == range.OtoOffsetSample &&
            range.PreutteranceSample - range.OtoOffsetSample == 2690 && range.OriginalStableSample - range.OtoOffsetSample == 5777 &&
            range.StableSample - range.OtoOffsetSample == 3131 && range.EndSample - range.OtoOffsetSample == 4895 &&
            range.ReferencePeriodSamples is > 199 and < 202,
            "the measured te override reports original and selected landmarks on the actual 44100Hz source grid");
        check(range.GrainCount > 1 && range.MaximumSourceGapSamples == 0 && range.MaximumNominalDeviationSamples <= 264 &&
            actual.Anchors[27].EndSample - actual.Anchors[27].VowelOnsetSample == 2205 &&
            actual.Audio.Samples.Length == baseline.Audio.Samples.Length - (plan[27].DurationSamples - 2205) &&
            actual.Anchors[28].VowelOnsetSample == baseline.Anchors[28].VowelOnsetSample - (plan[27].DurationSamples - 2205),
            "the explicit short allocation retains the next-pre guard, actual grain coverage and exact planned duration without global slowdown");

        foreach (var invalid in new UtauLandmarkOverride[][]
        {
            [new(-1, 71, 1764)], [new(27, double.NaN, 1764)], [new(27, 71, 881)],
            [new(27, 50, 1764)], [new(27, 131, 1764)], [new(27, 71, 1765)],
            [new(27, 71, 1764), new(27, 71, 1764)]
        })
            check(await RejectedAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, shortened,
                landmarkOverrides: invalid)), "invalid index, nonfinite, short-tail, before-pre, non-earlier, over-budget or duplicate landmarks are rejected");

        // Synthetic periodic suffix behind a silent local candidate: the later
        // fixed-region period must not authorize an unvoiced replacement tail.
        var samples = new short[22050];
        for (var i = 0; i < samples.Length; i++)
            if (i >= 4410) samples[i] = (short)Math.Round(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
        var path = Path.Combine(temporary.DirectoryPath, "local-silence.wav");
        await File.WriteAllBytesAsync(path, new PcmAudio(samples, 44100).ToWaveBytes());
        var synthetic = new Voicebank("landmark fixture", temporary.DirectoryPath,
            [new(path, "て", 0, 200, -400, 40, 15)], 0);
        check(await RejectedAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync("て", synthetic,
            [new("て", '\0', 0, 2646)], landmarkOverrides: [new(0, 60, 882)])),
            "a voiced later reference does not allow a locally silent comparison tail");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, shortened, cancellation.Token, [new(27, 71, 1764)]); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "landmark comparison rendering honours cancellation and never plays or saves its output");
    }

    private static async Task<bool> RejectedAsync(Func<Task<UtauReferenceTiming>> render)
    {
        try { await render(); return false; }
        catch (InvalidDataException) { return true; }
    }
}
