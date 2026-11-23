using System.Security.Cryptography;
using Tanayomi;

internal static class UtauCvvcSlicesTests
{
    private const int Rate = 44100;
    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bankRoot = Path.Combine(temporary.DirectoryPath, "bank");
        var nested = Path.Combine(bankRoot, "nested"); Directory.CreateDirectory(nested);
        var vowelPath = Path.Combine(bankRoot, "a.wav"); var cvPath = Path.Combine(nested, "ak.wav");
        var vowelBytes = Wave(false); var cvBytes = Wave(true);
        await File.WriteAllBytesAsync(vowelPath, vowelBytes); await File.WriteAllBytesAsync(cvPath, cvBytes);
        var a = new OtoEntry(vowelPath, "あ", 0, 40, -400, 20, 8);
        var ka = new OtoEntry(cvPath, "か", 100, 140, -400, 100, 8);
        var vc = new OtoEntry(cvPath, "a k", 70, 60, -80, 30, 8);
        var bank = new Voicebank("synthetic CVVC", bankRoot, [a, ka, vc], 0);
        UtauReadingUnit[] reading = [new("あ", 0, '\0'), new("か", 0, '\0')];
        await using var planner = new UtauDirectPlanner();
        var baseline = await planner.CreateAsync(reading, bank with { Entries = [a, ka] }, sourceTargetHz: 261.625565);
        var planned = await planner.CreateAsync(reading, bank, sourceTargetHz: 261.625565);
        check(planned.Reading.SequenceEqual(baseline.Reading) && planned.Anchors.SequenceEqual(baseline.Anchors) &&
            planned.SampleCount == baseline.SampleCount && planned.Units.SequenceEqual(baseline.Units),
            "CVVC physical scheduling preserves every logical mora, HTS anchor, onset and final sample count");
        check(planned.RenderSlices is { Count: 3 } && planned.RenderUnits[1].SliceKind == UtauDirectSliceKind.Vc &&
            planned.RenderUnits[2].SliceKind == UtauDirectSliceKind.CvRelease && planned.RenderUnits[1].UnitIndex == 1 &&
            planned.RenderUnits[2].UnitIndex == 1 && planned.RenderUnits[2].VowelOnsetSample == planned.Units[1].VowelOnsetSample,
            "a shared-WAV boundary becomes one VC and one CV release without adding a reading or moving its vowel");
        if (planned.RenderSlices is not { Count: 3 }) return;
        var transition = planned.RenderUnits[1]; var release = planned.RenderUnits[2];
        check(transition.Source!.EndSample - release.Source!.StartSample == release.OverlapSamples &&
            transition.EndSample - release.StartSample == release.OverlapSamples &&
            transition.EndSample - transition.StartSample == transition.Source.EndSample - transition.Source.StartSample,
            "VC and CV share exactly the same absolute source samples during their bounded crossfade and preserve consonant duration");
        var physicalEnd = 0;
        foreach (var unit in planned.RenderUnits)
        {
            check(unit.StartSample == physicalEnd - unit.OverlapSamples && unit.EndSample > unit.StartSample,
                "CVVC physical slices form one bounded continuous schedule");
            physicalEnd = unit.EndSample;
        }
        check(physicalEnd == planned.SampleCount, "CVVC ends at the unchanged logical utterance boundary");
        var rendered = await Render(planned);
        var closureAt = transition.StartSample + Position(128) - transition.Source.StartSample;
        var releaseAt = release.StartSample + Position(170) - release.Source.StartSample;
        check(Peak(rendered.Samples, closureAt, 3) > 23000 && Peak(rendered.Samples, releaseAt, 3) > 26000 &&
            rendered.Samples.Count(value => Math.Abs((int)value) > 22000) == 2,
            "synthetic closure and release markers each survive once, without consonant duplication or omission");
        var partialPath = Path.Combine(nested, "non-hop.wav");
        var partialSamples = UtauSpeech.DecodeWave(cvBytes, default).Select(value => (short)Math.Round(value * 32768)).ToArray();
        partialSamples[Position(70) + 3300] = 25000;
        await File.WriteAllBytesAsync(partialPath, new PcmAudio(partialSamples, Rate).ToWaveBytes());
        var partialBank = bank with { Entries = [a, ka with { WavePath = partialPath },
            vc with { WavePath = partialPath, Cutoff = -3700 * 1000.0 / Rate }] };
        var partial = await planner.CreateAsync(reading, partialBank, sourceTargetHz: 261.625565);
        var partialVc = partial.RenderUnits.Single(unit => unit.SliceKind == UtauDirectSliceKind.Vc);
        var partialAudio = await Render(partial);
        var lastCenter = ((3700 + 440) / 441) * 441;
        var ghost = partialVc.StartSample + 3300 + lastCenter - (3700 - 1);
        check(partialVc.DurationSamples == 3700 && Peak(partialAudio.Samples, partialVc.StartSample + 3300, 2) > 23000 &&
            Peak(partialAudio.Samples, ghost, 2) < 4,
            "a non-hop-aligned VC keeps its final grain's source/destination delta and cannot duplicate an earlier tail marker inside the crossfade");
        var plain = await planner.CreateAsync(reading, bank, sourceTargetHz: null);
        var baselineAudio = await Render(baseline);
        var plainAudio = await Render(plain);
        check(baselineAudio.Samples.SequenceEqual(plainAudio.Samples),
            "a CV-only plan remains bit-identical to the existing non-context CV path");
        var vcv = bank with { Entries = [a, ka, vc, ka with { Alias = "a か" }] };
        check((await planner.CreateAsync(reading, vcv, sourceTargetHz: 261.625565)).RenderSlices is null,
            "an available VCV retains priority over CVVC and is never given an additional VC");
        var otherPath = Path.Combine(nested, "different.wav"); await File.WriteAllBytesAsync(otherPath, cvBytes);
        foreach (var invalid in new[] { vc with { WavePath = otherPath }, vc with { Cutoff = -5 },
            vc with { Cutoff = -150 }, vc with { Preutterance = double.NaN }, vc with { Offset = 150 } })
            check((await planner.CreateAsync(reading, bank with { Entries = [a, ka, invalid] }, sourceTargetHz: 261.625565)).RenderSlices is null,
                "a different recording, short or ambiguous OTO boundary keeps the prior CV plan instead of guessing a slice");
        UtauReadingUnit[] paused = [reading[0], new(null, 0, 'っ'), reading[1]];
        check((await planner.CreateAsync(paused, bank, sourceTargetHz: 261.625565)).RenderSlices is null,
            "CVVC never bridges a closure or pause boundary");
        var shortTiming = new[] { new UtauTimingAllocation("あ", '\0', 0, Position(80)), new UtauTimingAllocation("か", '\0', 0, Position(180)) };
        check((await planner.CreateAsync(reading, bank, shortTiming, sourceTargetHz: 261.625565)).RenderSlices is null,
            "a short preceding vowel keeps CV rather than compressing or clipping the extra consonant slice");
        var mapped = bank with { Entries = [a with { Alias = "あ_C4" }, ka with { Alias = "か_C4" }, vc with { Alias = "a k_C4" }],
            PitchMappings = [new(60, "", "_C4")] };
        check((await planner.CreateAsync(reading, mapped, sourceTargetHz: 261.625565)).RenderUnits.Any(unit =>
            unit.SliceKind == UtauDirectSliceKind.Vc && unit.Source!.Oto.Alias == "a k_C4"),
            "CVVC uses the same prefix-map pitch selection for CV and VC, including nested source paths");

        using var stop = new CancellationTokenSource(); stop.Cancel();
        check(await Capture(() => planner.CreateAsync(reading, bank, ct: stop.Token, sourceTargetHz: 261.625565)) is OperationCanceledException,
            "CVVC source planning honors precancellation without inspecting or altering originals");
        var oversized = planned with { RenderSlices = Enumerable.Repeat(transition, 1280).ToArray() };
        check(await Capture(() => Render(oversized)) is InvalidDataException,
            "the physical-slice renderer rejects oversized schedules before rendering");
        var malformed = planned.RenderUnits.ToArray(); malformed[1] = transition with { DurationSamples = transition.DurationSamples + 1 };
        check(await Capture(() => Render(planned with { RenderSlices = malformed })) is InvalidDataException,
            "VC fixed-length inconsistencies fail safely instead of stretching a consonant");
        check(SHA256.HashData(await File.ReadAllBytesAsync(vowelPath)).SequenceEqual(SHA256.HashData(vowelBytes)) &&
            SHA256.HashData(await File.ReadAllBytesAsync(cvPath)).SequenceEqual(SHA256.HashData(cvBytes)),
            "CVVC candidate selection, rendering and failure checks leave the synthetic source WAVs unchanged");
    }
    private static async Task<PcmAudio> Render(UtauDirectPlan plan)
    {
        await using var renderer = new UtauDirectPsola((_, _, _) => Task.FromResult(new UtauDirectRenderPlan(plan, Flat(plan.SampleCount))));
        return await renderer.RenderAsync("fixture", new Voicebank("fixture", "unused", [], 0), default);
    }
    private static IReadOnlyList<double> Flat(int count) => Array.AsReadOnly(Enumerable.Repeat(261.625565, (int)(1000.0 * count / Rate / 5) + 1).ToArray());
    private static int Peak(short[] samples, int at, int radius) => samples.Skip(Math.Max(0, at - radius)).Take(radius * 2 + 1).Max(value => Math.Abs((int)value));
    private static int Position(double milliseconds) => (int)Math.Round(milliseconds * Rate / 1000);
    private static byte[] Wave(bool consonants)
    {
        var samples = new short[Rate / 2];
        for (var i = 0; i < samples.Length; i++)
        {
            double ms = i * 1000.0 / Rate;
            samples[i] = consonants && ms >= 100 && ms < 200 ? (short)0 : (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / Rate));
        }
        if (consonants) { samples[Position(128)] = 27000; samples[Position(170)] = -30000; }
        return new PcmAudio(samples, Rate).ToWaveBytes();
    }
    private static async Task<Exception?> Capture(Func<Task> action) { try { await action(); return null; } catch (Exception error) { return error; } }
}
