using Tanayomi;
using Tanayomi.Desktop;

public static class UtauProsodyPlanTests
{
    // Symbols observed from the explicit audited native run. Frame lengths
    // below are synthetic and unequal; they are not measured speech timings.
    private const string ReviewPhones = "sil s u u cl t o pau sh i i cl t o pau k o N n i ch i w a pau ky o o w a h a i sh i N o m i n i k i t e k u r e t e a r i g a t o o sil";

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).ConfigureAwait(false))
            .Single(value => value.Name == "hosinohito");
        var reference = await UtauSpeech.SynthesizeReferenceWithTimingAsync(WorldReference.ReviewText, bank).ConfigureAwait(false);
        var symbols = ReviewPhones.Split(' ');
        var plan = MakePlan(symbols);
        var mapped = UtauProsodyPlan.Map(plan, reference.Anchors);
        check(UtauProsodyPlan.Map(plan, reference.Anchors.Select(x => new UtauReadingUnit(x.Alias, x.Extensions, x.Pause)).ToArray())
            .SequenceEqual(mapped), "pre-render reading mapping matches the complete rendered reference without synthesizing a baseline");
        check(mapped.Count == 33 && mapped.Count == reference.Anchors.Count && mapped.Zip(reference.Anchors).All(pair =>
            pair.First.UnitIndex == pair.Second.UnitIndex && pair.First.Alias == pair.Second.Alias &&
            pair.First.Pause == pair.Second.Pause && pair.First.Extensions == pair.Second.Extensions),
            "observed native fixed phone sequence maps to every unit of the actual bundled B reading");
        check(mapped.SelectMany(unit => Enumerable.Range(unit.FirstPhone, unit.PhoneCount))
                .SequenceEqual(Enumerable.Range(1, plan.Phones.Count - 1)) &&
            mapped.All(unit => unit.StartFrame == plan.Phones[unit.FirstPhone].StartFrame &&
                unit.EndFrame == plan.Phones[unit.FirstPhone + unit.PhoneCount - 1].EndFrame),
            "explicit leading sil plus mapped ranges account for every phone and use model frame positions rather than character counts");
        check(mapped.Where(unit => unit.Extensions > 0).Count() == 4 && mapped.Where(unit => unit.Extensions > 0).All(unit =>
            unit.PhoneCount == 3 && unit.MainPhone == unit.FirstPhone + 1 &&
            plan.Phones[unit.MainPhone!.Value].Symbol == plan.Phones[unit.MainPhone.Value + 1].Symbol) &&
            mapped[^2] is { Alias: "と", Extensions: 1, PhoneCount: 3 },
            "long vowels consume repeated vowel phones inside their existing CV unit without creating a new attack");
        check(mapped.Where(unit => unit.Alias == "ん").All(unit => unit.PhoneCount == 1 && unit.MainPhone == unit.FirstPhone &&
                plan.Phones[unit.FirstPhone].Symbol == "N") &&
            mapped.Where(unit => unit.Pause == 'っ').All(unit => unit.MainPhone is null && unit.MainStartFrame is null &&
                plan.Phones[unit.FirstPhone].Symbol == "cl"),
            "nasal N remains a separate sonorant and sokuon is a closure rather than an ordinary vowel or pause");
        check(mapped[13].Pause == '。' && plan.Phones[mapped[13].FirstPhone].Symbol == "pau" &&
            mapped[^1].Pause == '。' && plan.Phones[mapped[^1].FirstPhone].Symbol == "sil" &&
            mapped.Where(unit => unit.Pause == '、').All(unit => plan.Phones[unit.FirstPhone].Symbol == "pau"),
            "internal punctuation maps to pau and terminal punctuation explicitly consumes the final sil");
        var unvoiced = symbols.Select(symbol => symbol is "i" or "u" ? symbol.ToUpperInvariant() : symbol).ToArray();
        var unvoicedPlan = MakePlan(unvoiced);
        check(UtauProsodyPlan.Map(unvoicedPlan, reference.Anchors).SequenceEqual(mapped) &&
            unvoicedPlan.Phones.Any(phone => phone.Symbol == "I") && unvoicedPlan.Voiced.Count(value => value == 0) > plan.Voiced.Count(value => value == 0),
            "matching devoice-marked vowels does not rewrite the source LF0 or voiced flags");
        var phoneCopy = plan.Phones.ToArray(); var lf0Copy = plan.Lf0.ToArray(); var voicedCopy = plan.Voiced.ToArray();
        var snapshot = new OpenJtalkPlan(phoneCopy, lf0Copy, voicedCopy);
        phoneCopy[1] = new("unknown", 0, 1); lf0Copy[0] = double.NaN; voicedCopy[0] = 2;
        check(UtauProsodyPlan.Map(snapshot, reference.Anchors).SequenceEqual(mapped) &&
            snapshot.Phones is IList<OpenJtalkPhone> { IsReadOnly: true } && snapshot.Lf0 is IList<double> { IsReadOnly: true } &&
            snapshot.Voiced is IList<byte> { IsReadOnly: true } && mapped is IList<UtauPlannedUnit> { IsReadOnly: true },
            "plan and mapping are immutable snapshots unaffected by reusable native caller buffers");

        foreach (var invalid in new[]
                 {
                     symbols.Where((_, i) => i != 4).ToArray(), // Missing cl.
                     symbols.Where((_, i) => i != 3).ToArray(), // Missing long u.
                     symbols.Select((symbol, i) => i == 17 ? "n" : symbol).ToArray(), // N is not n.
                     symbols.Select((symbol, i) => i == 24 ? "sil" : symbol).ToArray(), // Internal silence mismatch.
                     symbols.Take(20).Concat(new[] { "a" }).Concat(symbols.Skip(20)).ToArray(),
                     symbols.Select((symbol, i) => i == 20 ? "unknown" : symbol).ToArray()
                 })
            Throws(() => UtauProsodyPlan.Map(MakePlan(invalid), reference.Anchors), check,
                "different missing extra or unknown phones fail explicitly instead of coercing the reference reading");
        var closureAtEnd = reference.Anchors.ToArray(); closureAtEnd[^1] = closureAtEnd[^1] with { Pause = 'っ' };
        Throws(() => UtauProsodyPlan.Map(plan, closureAtEnd), check,
            "terminal sil cannot silently replace a required sokuon closure");
        var wrongSignature = reference.Anchors.ToArray(); wrongSignature[0] = wrongSignature[0] with { UnitIndex = 7 };
        Throws(() => UtauProsodyPlan.Map(plan, wrongSignature), check,
            "invalid unit signatures are rejected before a partial plan is accepted");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        var stopped = false;
        try { UtauProsodyPlan.Map(plan, reference.Anchors, cancelled.Token); }
        catch (OperationCanceledException) { stopped = true; }
        check(stopped, "comparison mapping honors cancellation without returning a partial unit sequence");

        var brokenPhones = plan.Phones.ToArray(); brokenPhones[1] = brokenPhones[1] with { StartFrame = 0 };
        Throws(() => new OpenJtalkPlan(brokenPhones, plan.Lf0, plan.Voiced), check,
            "overlapping or incomplete phone time ranges cannot become a valid context plan");
        var badLf0 = plan.Lf0.ToArray(); badLf0[0] = double.NaN;
        Throws(() => new OpenJtalkPlan(plan.Phones, badLf0, plan.Voiced), check,
            "nonfinite log pitch cannot enter the comparison renderer");
        var badVoicing = plan.Voiced.ToArray(); badVoicing[0] = 1;
        Throws(() => new OpenJtalkPlan(plan.Phones, plan.Lf0, badVoicing), check,
            "an absent unvoiced pitch cannot be relabelled as valid voiced LF0");
        Throws(() => new OpenJtalkPlan(plan.Phones, new double[9001], new byte[9001]), check,
            "context metadata cannot exceed the 45-second native frame bound");
        Throws(() => new OpenJtalkPlan(plan.Phones, plan.Lf0, plan.Voiced, 44100, 240), check,
            "model frame coordinates cannot be silently interpreted using the UTAU sample rate");
    }

    private static OpenJtalkPlan MakePlan(IReadOnlyList<string> symbols)
    {
        var phones = new OpenJtalkPhone[symbols.Count]; var frames = 0;
        for (var i = 0; i < symbols.Count; i++)
        {
            var end = frames + 10 + i % 7;
            phones[i] = new(symbols[i], frames, end); frames = end;
        }
        var lf0 = new double[frames]; var voiced = new byte[frames];
        foreach (var phone in phones)
            if (phone.Symbol is "a" or "i" or "u" or "e" or "o" or "N")
                for (var i = phone.StartFrame; i < phone.EndFrame; i++) { voiced[i] = 1; lf0[i] = Math.Log(180 + i % 50); }
        return new(phones, lf0, voiced);
    }

    private static void Throws(Action action, Action<bool, string> check, string description)
    {
        var rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, description);
    }
}
