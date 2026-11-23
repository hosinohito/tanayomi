using Tanayomi;

internal static class UtauSourceCandidatesTests
{
    public static Task RunAsync(Action<bool, string> check)
    {
        var plain = Oto("plain", "あ"); var duplicate = Oto("duplicate", "あ");
        var start = Oto("start", "- あ"); var connected = Oto("connected", "a い");
        var mappedStart = Oto("start-c4", "- あ_C4"); var mappedConnected = Oto("connected-c4", "a い_C4");
        var mappedCv = Oto("cv-c5", "い_C5"); var cvI = Oto("plain-i", "い");
        var bank = new Voicebank("candidate fixture", "unused", [plain, duplicate, cvI, start, connected,
            mappedStart, mappedConnected, mappedCv, Oto("unused-vc", "a k")], 0)
        { PitchMappings = [new(60, "", "_C4"), new(72, "", "_C5")] };
        var candidates = new UtauSourceCandidates(bank);
        UtauReadingUnit[] reading = [new("あ", 1, '\0'), new("い", 0, '\0')];
        check(candidates.Select(reading, 0, 261.625565) is { Kind: UtauSourceKind.Vcv, UsesPitchMap: true } first && first.Oto == mappedStart &&
            candidates.Select(reading, 1, 261.625565) is { Kind: UtauSourceKind.Vcv, UsesPitchMap: true } second && second.Oto == mappedConnected,
            "optional source selection uses explicit prefix-map aliases and the preceding vowel without adding a spoken unit");
        check(candidates.Select(reading, 0, 261.625565, useContext: false, usePitchMap: false)?.Oto == plain &&
            candidates.Select(reading, 1, 261.625565, usePitchMap: false)?.Oto == connected,
            "disabled extensions preserve the first ordinary CV duplicate while contextual selection can be enabled independently of pitch mapping");
        check(candidates.Select(reading, 1, 523.25113, useContext: false)?.Oto == mappedCv &&
            candidates.Select(reading, 1, 277.18263, useContext: false)?.Oto == cvI,
            "exact note mappings select the declared pitch alias and a missing map falls back to CV without guessing from suffix or folder names");
        foreach (var pause in new[] { 'っ', '、', '。', '?', '・', ' ' })
        {
            UtauReadingUnit[] paused = [new("あ", 0, '\0'), new(null, 0, pause), new("あ", 0, '\0')];
            check(candidates.Select(paused, 1, 261.625565) is null &&
                candidates.Select(paused, 2, 261.625565)?.Oto == mappedStart,
                "every pause clears vowel context and selects only a phrase-start recording or CV");
        }
        var limited = new UtauSourceCandidates(bank with { Entries = [plain, cvI, Oto("vc", "a k")] });
        check(limited.Select(reading, 1, 261.625565)?.Oto == cvI &&
            limited.Select([new("か", 0, '\0')], 0, 261.625565) is null,
            "missing VCV recordings fall back to CV and a VC-only alias never substitutes for a complete syllable");
        var kana = Oto("katakana", "a イ");
        var normalized = new UtauSourceCandidates(bank with { Entries = [plain, cvI, kana] });
        check(normalized.Select(reading, 1, 261.625565, usePitchMap: false)?.Oto == kana,
            "candidate aliases share the existing Unicode and hiragana normalization");
        check(Invalid(() => new UtauSourceCandidates(bank with { PitchMappings = [new(60, "", ""), new(60, "x", "")] })) &&
            Invalid(() => new UtauSourceCandidates(bank with { Entries = new OversizedEntries() })) &&
            Invalid(() => candidates.Select(new OversizedReading(), 0, 261.625565)) &&
            Invalid(() => candidates.Select(reading, 0, double.NaN)),
            "source candidate indexes reject ambiguous maps unbounded providers and nonfinite target pitch before enumeration");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var canceled = false;
        try { candidates.Select(reading, 0, 261.625565, ct: stop.Token); } catch (OperationCanceledException) { canceled = true; }
        check(canceled, "optional source selection observes cancellation before selecting a recording");
        check(UtauSourceCandidates.TryNote("C4", out var c4) && c4 == 60 &&
            UtauSourceCandidates.TryNote("Bb3", out var bb3) && bb3 == 58 &&
            UtauSourceCandidates.TryNote("C-1", out var low) && low == 0 &&
            !UtauSourceCandidates.TryNote("G#9", out _) && !UtauSourceCandidates.TryNote("H4", out _),
            "prefix-map note parsing uses bounded MIDI notes including flats and the lowest supported octave");
        return Task.CompletedTask;
    }

    private static OtoEntry Oto(string path, string alias) => new(path, alias, 0, 50, 0, 20, 10);
    private static bool Invalid(Action action) { try { action(); return false; } catch (InvalidDataException) { return true; } }
    private sealed class OversizedEntries : IReadOnlyList<OtoEntry>
    {
        public int Count => 262145;
        public OtoEntry this[int index] => throw new InvalidOperationException("must bound Count before indexing");
        public IEnumerator<OtoEntry> GetEnumerator() => throw new InvalidOperationException("must not enumerate");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class OversizedReading : IReadOnlyList<UtauReadingUnit>
    {
        public int Count => 641;
        public UtauReadingUnit this[int index] => throw new InvalidOperationException("must bound Count before indexing");
        public IEnumerator<UtauReadingUnit> GetEnumerator() => throw new InvalidOperationException("must not enumerate");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
