namespace Tanayomi.Desktop;

// Native phone boundaries are model predictions;
// the UTAU OTO anchors they map to are not detected acoustic boundaries.
internal sealed record OpenJtalkPhone(string Symbol, int StartFrame, int EndFrame);

internal sealed class OpenJtalkPlan
{
    internal IReadOnlyList<OpenJtalkPhone> Phones { get; }
    internal IReadOnlyList<double> Lf0 { get; }
    internal IReadOnlyList<byte> Voiced { get; }
    internal int SampleRate { get; }
    internal int FramePeriod { get; }
    internal int FrameCount => Lf0.Count;

    internal OpenJtalkPlan(IReadOnlyList<OpenJtalkPhone> phones, IReadOnlyList<double> lf0,
        IReadOnlyList<byte> voiced, int sampleRate = 48000, int framePeriod = 240)
    {
        ArgumentNullException.ThrowIfNull(phones); ArgumentNullException.ThrowIfNull(lf0); ArgumentNullException.ThrowIfNull(voiced);
        if (phones.Count is < 1 or > 8192 || lf0.Count is < 1 or > 9000 || voiced.Count != lf0.Count ||
            sampleRate != 48000 || framePeriod != 240) throw new InvalidDataException("Unsupported comparison plan layout.");
        var phoneCopy = new OpenJtalkPhone[phones.Count]; var f0Copy = new double[lf0.Count]; var voicedCopy = new byte[voiced.Count];
        for (var i = 0; i < phoneCopy.Length; i++) phoneCopy[i] = phones[i];
        for (var i = 0; i < f0Copy.Length; i++) { f0Copy[i] = lf0[i]; voicedCopy[i] = voiced[i]; }
        var end = 0;
        foreach (var phone in phoneCopy)
        {
            if (phone is null || phone.Symbol is null || phone.Symbol.Length is < 1 or > 31 ||
                phone.Symbol.Any(c => c is < '!' or > '~') || phone.StartFrame != end ||
                phone.EndFrame <= phone.StartFrame || phone.EndFrame > f0Copy.Length)
                throw new InvalidDataException("Invalid comparison phone range.");
            end = phone.EndFrame;
        }
        if (end != f0Copy.Length) throw new InvalidDataException("Comparison phones do not cover the complete frame grid.");
        for (var i = 0; i < f0Copy.Length; i++)
            if (!double.IsFinite(f0Copy[i]) || !(voicedCopy[i] == 0 && f0Copy[i] == 0 ||
                voicedCopy[i] == 1 && f0Copy[i] >= Math.Log(40) && f0Copy[i] <= Math.Log(1200)))
                throw new InvalidDataException("Invalid comparison LF0 or voicing flag.");
        Phones = Array.AsReadOnly(phoneCopy); Lf0 = Array.AsReadOnly(f0Copy); Voiced = Array.AsReadOnly(voicedCopy);
        SampleRate = sampleRate; FramePeriod = framePeriod;
    }
}

// MainPhone is the first vowel, or N for a nasal unit. It is absent for a
// closure/pause; long vowels consume several vowel phones without new attacks.
internal sealed record UtauPlannedUnit(int UnitIndex, string? Alias, int Extensions, char Pause,
    int FirstPhone, int PhoneCount, int? MainPhone, int StartFrame, int? MainStartFrame, int EndFrame);

internal static class UtauProsodyPlan
{
    private static readonly IReadOnlyDictionary<string, string[]> PhonesByAlias = BuildPhoneTable();

    internal static IReadOnlyList<UtauPlannedUnit> Map(OpenJtalkPlan plan,
        IReadOnlyList<UtauTimingAnchor> expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        if (expected.Count is < 1 or > 1024) throw new InvalidDataException();
        foreach (var unit in expected)
            if (unit is null || unit.StartSample < 0 || unit.EndSample <= unit.StartSample ||
                unit.EndSample > UtauSpeech.OutputSampleRate * 45 ||
                (unit.Alias is null ? unit.VowelOnsetSample is not null :
                    unit.VowelOnsetSample is not int onset || onset < unit.StartSample || onset >= unit.EndSample))
                throw new InvalidDataException("Invalid reference unit signature.");
        if (expected.Where((unit, index) => unit.UnitIndex != index).Any()) throw new InvalidDataException();
        return Map(plan, expected.Select(x => new UtauReadingUnit(x.Alias, x.Extensions, x.Pause)).ToArray(), ct);
    }

    internal static IReadOnlyList<UtauPlannedUnit> Map(OpenJtalkPlan plan,
        IReadOnlyList<UtauReadingUnit> expected, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan); ArgumentNullException.ThrowIfNull(expected);
        ct.ThrowIfCancellationRequested();
        if (expected.Count is < 1 or > 1024 || expected.Any(unit => unit is null) || !expected.Any(unit => unit.Alias is not null) ||
            plan.Phones.Count < 3 || plan.Phones[0].Symbol != "sil" || plan.Phones[^1].Symbol != "sil")
            throw new InvalidDataException("Comparison needs explicit boundary silence and a spoken reference.");
        // Boundary silence is accounted for explicitly, never mistaken for an
        // unrecognized CV. A final punctuation unit may consume the final sil.
        var at = 1;
        var result = new UtauPlannedUnit[expected.Count];
        for (var i = 0; i < expected.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = expected[i];
            if (unit is null || unit.Extensions is < 0 or > 1024)
                throw new InvalidDataException("Invalid reference unit signature.");
            var first = at; int? main = null;
            if (unit.Alias is null)
            {
                if (unit.Extensions != 0 || unit.Pause is not ('っ' or '、' or '。' or '?' or '・' or ' '))
                    throw new InvalidDataException("Invalid reference pause signature.");
                if (unit.Pause == 'っ') Consume("cl");
                else if (i == expected.Count - 1 && (unit.Pause is '、' or '。' or '?') &&
                    at == plan.Phones.Count - 1 && plan.Phones[at].Symbol == "sil")
                    Consume("sil"); // Explicit terminal punctuation-to-sil equivalence.
                else Consume("pau");
            }
            else
            {
                if (unit.Pause != '\0' ||
                    !PhonesByAlias.TryGetValue(unit.Alias, out var phones))
                    throw new InvalidDataException("Unsupported reference alias or onset.");
                foreach (var phone in phones) Consume(phone);
                main = at - 1;
                var vowel = phones[^1];
                if (unit.Extensions > 0 && vowel is not ("a" or "i" or "u" or "e" or "o"))
                    throw new InvalidDataException("A non-vowel cannot own a long-vowel extension.");
                for (var extra = 0; extra < unit.Extensions; extra++) { ct.ThrowIfCancellationRequested(); Consume(vowel); }
            }
            result[i] = new(i, unit.Alias, unit.Extensions, unit.Pause, first, at - first, main,
                plan.Phones[first].StartFrame, main is int index ? plan.Phones[index].StartFrame : null,
                plan.Phones[at - 1].EndFrame);
        }
        if (at == plan.Phones.Count - 1) Consume("sil");
        if (at != plan.Phones.Count) throw new InvalidDataException("Comparison reading left unconsumed phones.");
        return Array.AsReadOnly(result);

        void Consume(string symbol)
        {
            if (at >= plan.Phones.Count || Canonical(plan.Phones[at].Symbol) != symbol)
                throw new InvalidDataException("Comparison phone sequence differs from the reference reading.");
            at++;
        }
    }

    private static string Canonical(string phone) => phone switch
    {
        "A" => "a", "I" => "i", "U" => "u", "E" => "e", "O" => "o", _ => phone
    };

    private static IReadOnlyDictionary<string, string[]> BuildPhoneTable()
    {
        var table = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var vowels = new[] { "a", "i", "u", "e", "o" };
        void Row(string kana, string consonant)
        {
            for (var i = 0; i < kana.Length; i++) table.Add(kana[i].ToString(), consonant.Length == 0 ? [vowels[i]] : [consonant, vowels[i]]);
        }
        Row("あいうえお", ""); Row("かきくけこ", "k"); Row("がぎぐげご", "g");
        Row("さしすせそ", "s"); Row("ざじずぜぞ", "z"); Row("たちつてと", "t"); Row("だぢづでど", "d");
        Row("なにぬねの", "n"); Row("はひふへほ", "h"); Row("ばびぶべぼ", "b"); Row("ぱぴぷぺぽ", "p");
        Row("まみむめも", "m"); Row("らりるれろ", "r");
        table["し"] = ["sh", "i"]; table["じ"] = table["ぢ"] = ["j", "i"];
        table["ち"] = ["ch", "i"]; table["つ"] = ["ts", "u"]; table["づ"] = ["z", "u"]; table["ふ"] = ["f", "u"];
        table.Add("や", ["y", "a"]); table.Add("ゆ", ["y", "u"]); table.Add("よ", ["y", "o"]);
        table.Add("わ", ["w", "a"]); table.Add("を", ["o"]); table.Add("ん", ["N"]); table.Add("ゔ", ["v", "u"]);
        foreach (var (kana, consonant) in new[] { ("き", "ky"), ("ぎ", "gy"), ("し", "sh"), ("じ", "j"),
                     ("ち", "ch"), ("ぢ", "j"), ("に", "ny"), ("ひ", "hy"), ("び", "by"), ("ぴ", "py"), ("み", "my"), ("り", "ry") })
            for (var i = 0; i < 3; i++) table.Add(kana + "ゃゅょ"[i], [consonant, new[] { "a", "u", "o" }[i]]);
        foreach (var (alias, consonant, vowel) in new[] { ("ふぁ", "f", "a"), ("ふぃ", "f", "i"), ("ふぇ", "f", "e"), ("ふぉ", "f", "o"),
                     ("てぃ", "t", "i"), ("でぃ", "d", "i"), ("とぅ", "t", "u"), ("どぅ", "d", "u"),
                     ("しぇ", "sh", "e"), ("じぇ", "j", "e"), ("ちぇ", "ch", "e"),
                     ("いぇ", "y", "e"), ("うぃ", "w", "i"), ("うぇ", "w", "e"), ("うぉ", "w", "o") })
            table.Add(alias, [consonant, vowel]);
        return table;
    }
}
