using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauProsodyTransferTests
{
    internal static void Run(Action<bool, string> check)
    {
        CheckPhoneticMetadata(check);
        var lf0 = Enumerable.Range(0, 60).Select(i => Math.Log(160 + i * 2.0)).ToArray();
        var voiced = Enumerable.Repeat((byte)1, 60).ToArray();
        foreach (var i in Enumerable.Range(20, 5).Concat(new[] { 0, 1, 58, 59 })) { voiced[i] = 0; lf0[i] = 0; }
        var plan = new OpenJtalkPlan([new("sil", 0, 2), new("a", 2, 58), new("sil", 58, 60)], lf0, voiced);
        UtauPlannedUnit[] units = [new(0, "あ", 0, '\0', 1, 1, 1, 2, 2, 58)];
        var allocation = UtauProsodyTransfer.Allocate(units);
        check(allocation[0].DurationSamples == 12348,
            "context allocation excludes donor initial silence and does not invent a consonant for a pure vowel");
        UtauPlannedUnit[] consonants = [new(0, "で", 0, '\0', 1, 2, 2, 10, 20, 35),
            new(1, "す", 0, '\0', 3, 2, 4, 35, 72, 86)];
        var timed = UtauProsodyTransfer.Allocate(consonants, consonantTiming: true);
        check(timed[0].DurationSamples == 11466 && timed[1].LeadingSamples == 8158 &&
            UtauProsodyTransfer.Allocate(units, consonantTiming: true)[0].LeadingSamples is null,
            "model consonant timing allocates the long next consonant to its own lead while retaining OTO leads for pure vowels");
        var audio = new UtauReferenceTiming(new(new short[12789], 44100), [new(0, "あ", '\0', 0, 0, 441, 12789)]);
        // Native WORLD reports 59 frames here; a differently ordered floating
        // point expression used to incorrectly require 58.
        var source = Enumerable.Repeat(200.0, 59).ToArray(); source[3] = 0; source[4] = 0;
        var result = UtauProsodyTransfer.TransferF0(plan, units, audio, source, 259.4256);
        var withoutWaveform = UtauProsodyTransfer.TransferF0(plan, units, audio.Anchors, audio.Audio.Samples.Length, 259.4256);
        var allVoiced = Enumerable.Repeat(200.0, source.Length).ToArray();
        check(withoutWaveform.SequenceEqual(UtauProsodyTransfer.TransferF0(plan, units, audio, allVoiced, 259.4256)),
            "direct prosody transfer uses timing alone and preserves the existing contour without synthesizing PCM");
        var weights = Enumerable.Range(0, source.Length).Select(i => source[i] == 0 ? 0 :
            Math.Min(0.005, Math.Max(0, audio.Audio.Samples.Length / 44100.0 - i * 0.005))).ToArray();
        var mean = result.Select((value, i) => value * weights[i]).Sum() / weights.Sum();
        check(result.Length == 59 && result.Select((value, i) => (value == 0) == (source[i] == 0)).All(value => value),
            "context transfer accepts native WORLD endpoint rounding and preserves every source unvoiced frame");
        check(Math.Abs(mean - 259.4256) < 1e-9 && result.Where(value => value > 0).Max() - result.Where(value => value > 0).Min() > 20,
            "context transfer preserves a varying donor contour while centering its voiced-time weighted mean on the selected voice");
        check(result.Where(value => value > 0).All(value => double.IsFinite(value) && value is >= 40 and <= 1200),
            "donor unvoiced gaps are interpolated without replacing source voicing or producing nonfinite pitch");
        var moved = audio with { Anchors = [audio.Anchors[0] with { VowelOnsetSample = 1323 }] };
        var movedPitch = UtauProsodyTransfer.TransferF0(plan, units, moved, source, 259.4256);
        check(movedPitch.Length == result.Length && !movedPitch.SequenceEqual(result) &&
            movedPitch.Select((value, i) => (value == 0) == (source[i] == 0)).All(value => value) &&
            movedPitch.Where(value => value > 0).All(value => double.IsFinite(value) && value is >= 40 and <= 1200) &&
            Math.Abs(movedPitch.Select((value, i) => value * weights[i]).Sum() / weights.Sum() - 259.4256) < 1e-9,
            "an internally delayed vowel landmark changes contour alignment without changing duration, source voicing or weighted pitch target");
        var rejected = false;
        try { UtauProsodyTransfer.TransferF0(plan, units, audio with { Anchors = [audio.Anchors[0] with { Alias = "い" }] }, source, 259.4256); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "context transfer rejects an unrelated rendered pronunciation rather than assigning its contour by index alone");
    }

    private static void CheckPhoneticMetadata(Action<bool, string> check)
    {
        string[] symbols = ["sil", "s", "U", "t", "o", "pau", "sh", "I", "k", "i", "pau", "N", "sil"];
        var plan = PhoneticPlan(symbols);
        UtauReadingUnit[] reading = [new("す", 0, '\0'), new("と", 0, '\0'), new(null, 0, '、'),
            new("し", 0, '\0'), new("き", 0, '\0'), new(null, 0, '、'), new("ん", 0, '\0')];
        var units = UtauProsodyPlan.Map(plan, reading);
        var plain = UtauProsodyTransfer.Allocate(units, consonantTiming: true);
        var annotated = UtauProsodyTransfer.Allocate(units, consonantTiming: true, phoneticPlan: plan);
        check(plain.All(value => value.PhoneticContext is null) &&
            plain.Select(value => (value.Alias, value.Pause, value.Extensions, value.DurationSamples, value.LeadingSamples)).SequenceEqual(
                annotated.Select(value => (value.Alias, value.Pause, value.Extensions, value.DurationSamples, value.LeadingSamples))),
            "optional phonetic metadata preserves every old allocation and stays null when no model is supplied");
        var first = annotated[0].PhoneticContext;
        check(first is { PreviousPhone: "s", VowelPhone: "U", NextPhone: "t", ModelVoicedFramesAbsent: true },
            "devoiced model U retains original case and direct s/U/t context independently of canonical su alias");
        var firstPhone = plan.Phones[units[0].MainPhone!.Value];
        check(first is not null && first.VowelDurationSamples ==
            (int)Math.Round(firstPhone.EndFrame * 220.5) - (int)Math.Round(firstPhone.StartFrame * 220.5),
            "main-phone duration uses endpoint rounding on the existing 44100Hz model grid rather than the whole CV or following consonant");
        check(annotated[3].PhoneticContext is { PreviousPhone: "sh", VowelPhone: "I", NextPhone: "k", ModelVoicedFramesAbsent: false },
            "uppercase I with voiced model frames is retained but cannot be mistaken for an all-unvoiced interval");
        check(annotated[4].PhoneticContext is { VowelPhone: "i", ModelVoicedFramesAbsent: false } &&
            annotated[6].PhoneticContext is { PreviousPhone: "pau", VowelPhone: "N", NextPhone: "sil" } &&
            annotated[2].PhoneticContext is null && annotated[5].PhoneticContext is null,
            "lowercase vowels and nasal N retain their symbols while MainPhone-free pauses receive no phonetic metadata");
        var lowercase = symbols.Select(value => value is "I" or "U" ? value.ToLowerInvariant() : value).ToArray();
        var lowerPlan = PhoneticPlan(lowercase);
        var lowerUnits = UtauProsodyPlan.Map(lowerPlan, reading);
        var lowerMetadata = UtauProsodyTransfer.Allocate(lowerUnits, consonantTiming: true, phoneticPlan: lowerPlan);
        check(units.SequenceEqual(lowerUnits) && lowerMetadata[0].Alias == annotated[0].Alias &&
            lowerMetadata[0].PhoneticContext is { VowelPhone: "u", ModelVoicedFramesAbsent: true } &&
            lowerMetadata[3].PhoneticContext is { VowelPhone: "i", ModelVoicedFramesAbsent: false },
            "canonical mapping accepts I/U and i/u equivalently but metadata keeps case and independently reports frame voicing");
        var adjacentPlan = PhoneticPlan(["sil", "U", "A", "sil"]);
        var adjacentUnits = UtauProsodyPlan.Map(adjacentPlan, new UtauReadingUnit[] { new("う", 0, '\0'), new("あ", 0, '\0') });
        var adjacent = UtauProsodyTransfer.Allocate(adjacentUnits, phoneticPlan: adjacentPlan);
        check(adjacent[0].PhoneticContext is { PreviousPhone: "sil", NextPhone: "A" } &&
            adjacent[1].PhoneticContext is { PreviousPhone: "U", VowelPhone: "A", NextPhone: "sil" },
            "neighbor symbols are copied exactly from original model phones without lowercasing or skipping adjacent vowels");
        var extendedPlan = PhoneticPlan(["sil", "s", "U", "u", "sil"]);
        var extendedUnits = UtauProsodyPlan.Map(extendedPlan, new UtauReadingUnit[] { new("す", 1, '\0') });
        var extended = UtauProsodyTransfer.Allocate(extendedUnits, phoneticPlan: extendedPlan);
        check(extended[0].Extensions == 1 && extended[0].PhoneticContext is { VowelPhone: "U", NextPhone: "u" } &&
            extended[0].DurationSamples == UtauProsodyTransfer.Allocate(extendedUnits)[0].DurationSamples &&
            extended[0].PhoneticContext!.VowelDurationSamples < extended[0].DurationSamples,
            "long-vowel metadata describes only MainPhone and preserves the extension instead of inventing a next consonant");
        foreach (var invalid in new[]
        {
            units[0] with { UnitIndex = 1 }, units[0] with { FirstPhone = 0 }, units[0] with { PhoneCount = 1 },
            units[0] with { MainPhone = 1 }, units[0] with { MainPhone = plan.Phones.Count },
            units[0] with { StartFrame = units[0].StartFrame + 1 },
            units[0] with { MainStartFrame = units[0].MainStartFrame + 1 },
            units[0] with { EndFrame = units[0].EndFrame - 1 }, units[0] with { Alias = "し" }
        })
        {
            var invalidUnits = units.ToArray(); invalidUnits[0] = invalid;
            RejectPhonetics(() => UtauProsodyTransfer.Allocate(invalidUnits, phoneticPlan: plan), check,
                "phonetic allocation rejects mismatched indices, pronunciation and main-phone ranges before exposing metadata");
        }
        var invalidPause = units.ToArray(); invalidPause[2] = invalidPause[2] with { MainPhone = 2, MainStartFrame = 1 };
        RejectPhonetics(() => UtauProsodyTransfer.Allocate(invalidPause, phoneticPlan: plan), check,
            "phonetic allocation rejects a pause falsely assigned another unit's main phone");
        RejectPhonetics(() => UtauProsodyTransfer.Allocate(units, phoneticPlan: adjacentPlan), check,
            "phonetic allocation rejects a different model plan rather than matching by unit index alone");
    }

    private static OpenJtalkPlan PhoneticPlan(string[] symbols)
    {
        var phones = new List<OpenJtalkPhone>(); var end = 0;
        for (var i = 0; i < symbols.Length; i++) { var next = end + 3 + i % 3; phones.Add(new(symbols[i], end, next)); end = next; }
        var voiced = Enumerable.Repeat((byte)1, end).ToArray(); var f0 = Enumerable.Repeat(Math.Log(200.0), end).ToArray();
        foreach (var phone in phones.Where(phone => phone.Symbol is "U" or "u"))
            for (var frame = phone.StartFrame; frame < phone.EndFrame; frame++) { voiced[frame] = 0; f0[frame] = 0; }
        return new(phones, f0, voiced);
    }

    private static void RejectPhonetics(Action action, Action<bool, string> check, string message)
    {
        var rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, message);
    }
}
