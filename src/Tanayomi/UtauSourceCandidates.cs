using System.Globalization;

namespace Tanayomi;

internal enum UtauSourceKind { Cv, Vcv }
internal sealed record UtauSourceCandidate(OtoEntry Oto, UtauSourceKind Kind, bool UsesPitchMap);

// Optional selection for direct renderers only. The pronunciation stays CV;
// a contextual alias names the recording, not an additional spoken vowel.
// Logical selection stays CV/VCV; VC-only aliases are queried separately for
// conservative, validated physical CVVC slices.
internal sealed class UtauSourceCandidates
{
    private readonly Dictionary<string, OtoEntry[]> aliases;
    private readonly Dictionary<int, VoicebankPitchMapping> pitches;

    internal UtauSourceCandidates(Voicebank bank)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (bank.Entries is null || bank.Entries.Count is < 1 or > 262144 ||
            bank.PitchMappings is null || bank.PitchMappings.Count > 128)
            throw new InvalidDataException("原音候補の件数が上限外です。");
        var grouped = new Dictionary<string, List<OtoEntry>>(StringComparer.Ordinal);
        for (var i = 0; i < bank.Entries.Count; i++)
        {
            var entry = bank.Entries[i];
            if (entry is null || entry.Alias is null || entry.Alias.Length is < 1 or > 128 || entry.Alias.Any(char.IsControl))
                throw new InvalidDataException("原音候補のエイリアスが不正です。");
            var key = Normalize(entry.Alias);
            if (!grouped.TryGetValue(key, out var values)) grouped.Add(key, values = []);
            values.Add(entry);
        }
        aliases = grouped.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray(), StringComparer.Ordinal);
        pitches = new();
        for (var i = 0; i < bank.PitchMappings.Count; i++)
        {
            var map = bank.PitchMappings[i];
            if (map is null || map.MidiNote is < 0 or > 127 || map.Prefix is null || map.Suffix is null ||
                map.Prefix.Length > 64 || map.Suffix.Length > 64 || map.Prefix.Any(char.IsControl) || map.Suffix.Any(char.IsControl) ||
                !pitches.TryAdd(map.MidiNote, map)) throw new InvalidDataException("原音候補の音階対応が不正です。");
        }
    }

    internal UtauSourceCandidate? Select(IReadOnlyList<UtauReadingUnit> reading, int unitIndex, double targetHz,
        bool useContext = true, bool usePitchMap = true, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(reading); ct.ThrowIfCancellationRequested();
        if (reading.Count is < 1 or > 640 || unitIndex < 0 || unitIndex >= reading.Count ||
            !double.IsFinite(targetHz) || targetHz is < 40 or > 1200)
            throw new InvalidDataException("原音候補の読みまたは目標音程が不正です。");
        var current = reading[unitIndex] ?? throw new InvalidDataException("原音候補の読みが不正です。");
        if (current.Alias is null) return null;
        if (current.Alias.Length is < 1 or > 2 || current.Pause != '\0' || current.Extensions is < 0 or > 640)
            throw new InvalidDataException("原音候補の読みが不正です。");
        VoicebankPitchMapping? mapping = null;
        if (usePitchMap)
        {
            var note = (int)Math.Round(69 + 12 * Math.Log2(targetHz / 440));
            pitches.TryGetValue(note, out mapping);
        }
        string? context = null;
        if (useContext)
        {
            if (unitIndex == 0) context = "-";
            else
            {
                var previous = reading[unitIndex - 1] ?? throw new InvalidDataException("原音候補の前音が不正です。");
                context = previous.Alias is null ? "-" : Vowel(previous.Alias);
            }
        }
        var alias = Normalize(current.Alias);
        if (context is not null)
        {
            var contextual = context + " " + alias;
            if (mapping is not null && Find(mapping.Prefix + contextual + mapping.Suffix) is { } mappedVcv)
                return new(mappedVcv, UtauSourceKind.Vcv, true);
            if (Find(contextual) is { } vcv) return new(vcv, UtauSourceKind.Vcv, false);
        }
        if (mapping is not null && Find(mapping.Prefix + alias + mapping.Suffix) is { } mappedCv)
            return new(mappedCv, UtauSourceKind.Cv, true);
        return Find(alias) is { } cv ? new(cv, UtauSourceKind.Cv, false) : null;
    }

    private OtoEntry? Find(string key) => aliases.TryGetValue(Normalize(key), out var entries) ? entries[0] : null;
    internal OtoEntry? SelectVc(IReadOnlyList<UtauReadingUnit> reading, int unitIndex, double targetHz, CancellationToken ct)
    {
        var current = Select(reading, unitIndex, targetHz, ct: ct);
        if (unitIndex < 1 || current is not { Kind: UtauSourceKind.Cv } ||
            reading[unitIndex - 1].Alias is not { } previous || reading[unitIndex].Alias is not { } next) return null;
        var vowel = Vowel(previous); var consonant = Consonant(Normalize(next));
        if (vowel is null || consonant is null) return null;
        var key = vowel + " " + consonant;
        var note = (int)Math.Round(69 + 12 * Math.Log2(targetHz / 440));
        if (pitches.TryGetValue(note, out var map) && Find(map.Prefix + key + map.Suffix) is { } mapped) return mapped;
        return Find(key);
    }
    private static string? Consonant(string alias)
    {
        if (alias.Length is < 1 or > 2 || alias.Length == 2 && alias[1] is not ('ゃ' or 'ゅ' or 'ょ')) return null;
        return alias[0] switch
        {
            'か' or 'く' or 'け' or 'こ' => "k", 'き' => "ky",
            'が' or 'ぐ' or 'げ' or 'ご' => "g", 'ぎ' => "gy",
            'さ' or 'す' or 'せ' or 'そ' => "s", 'し' => "sh",
            'ざ' or 'ず' or 'ぜ' or 'ぞ' => "z", 'じ' or 'ぢ' => "j",
            'た' or 'て' or 'と' => "t", 'ち' => "ch", 'つ' => "ts", 'だ' or 'で' or 'ど' => "d",
            'な' or 'ぬ' or 'ね' or 'の' => "n", 'に' => "ny",
            'は' or 'へ' or 'ほ' => "h", 'ひ' => "hy", 'ふ' => "f",
            'ば' or 'ぶ' or 'べ' or 'ぼ' => "b", 'び' => "by",
            'ぱ' or 'ぷ' or 'ぺ' or 'ぽ' => "p", 'ぴ' => "py",
            'ま' or 'む' or 'め' or 'も' => "m", 'み' => "my",
            'ら' or 'る' or 'れ' or 'ろ' => "r", 'り' => "ry",
            'や' or 'ゆ' or 'よ' => "y", 'わ' => "w", _ => null
        };
    }
    private static string Normalize(string value) => JapaneseReading.Hiragana(value.Normalize());
    private static string? Vowel(string alias)
    {
        if (alias.Length is < 1 or > 2) return null;
        var last = Normalize(alias)[^1];
        if ("あかがさざただなはばぱまやらわぁゃゎ".Contains(last)) return "a";
        if ("いきぎしじちぢにひびぴみりぃ".Contains(last)) return "i";
        if ("うくぐすずつづぬふぶぷむゆるゔぅゅ".Contains(last)) return "u";
        if ("えけげせぜてでねへべぺめれぇ".Contains(last)) return "e";
        if ("おこごそぞとどのほぼぽもよろをぉょ".Contains(last)) return "o";
        return last == 'ん' ? "n" : null;
    }

    internal static bool TryNote(string text, out int midi)
    {
        midi = 0;
        if (text is null) return false;
        var note = text.Trim();
        if (note.Length is < 2 or > 4) return false;
        var semitone = note[0] switch { 'C' => 0, 'D' => 2, 'E' => 4, 'F' => 5, 'G' => 7, 'A' => 9, 'B' => 11, _ => -1 };
        if (semitone < 0) return false;
        var start = 1;
        if (note[start] is '#' or 'b') { semitone += note[start] == '#' ? 1 : -1; start++; }
        if (!int.TryParse(note.AsSpan(start), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var octave)) return false;
        var value = ((long)octave + 1) * 12 + semitone;
        if (value is < 0 or > 127) return false;
        midi = (int)value; return true;
    }
}
