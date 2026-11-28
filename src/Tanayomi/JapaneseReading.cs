using System.IO.Compression;
using System.Text;

namespace Tanayomi;

/// <summary>A small managed shortest-path reader over BSD-3-Clause NAIST-JDIC data.
/// It does not embed MeCab/Open JTalk code, a native analyzer, or a voice model.</summary>
public static class JapaneseReading
{
    private readonly record struct Word(string Surface, string Reading, ushort Left, ushort Right, short Cost);
    private sealed record Node(int Cost, ushort Right, string Reading, Node? Previous, int WordIndex = -1);
    private sealed record DictionaryData(Word[] Words, short[] Transitions, int Width, int MaximumWordLength);
    private static readonly Lazy<DictionaryData> Data = new(Load);
    private static readonly Lazy<byte[]> AccentData = new(LoadAccents);
    internal readonly record struct AccentSpan(int Offset, string Reading, int Drop, int MoraCount, bool IsEmbeddedKana, bool HasFollowingParticle);

    internal static (string Reading, IReadOnlyList<AccentSpan> Accents) ToAccentReading(string text, CancellationToken ct)
    {
        var accents = new List<AccentSpan>();
        return (ToReading(text, true, ct, accents), accents);
    }
    private static readonly string[] Letters = ["えー", "びー", "しー", "でぃー", "いー", "えふ", "じー", "えいち", "あい", "じぇー", "けー", "える", "えむ", "えぬ", "おー", "ぴー", "きゅー", "あーる", "えす", "てぃー", "ゆー", "ぶい", "だぶりゅー", "えっくす", "わい", "ぜっと"];
    private static readonly string[] Digits = ["ぜろ", "いち", "に", "さん", "よん", "ご", "ろく", "なな", "はち", "きゅー"];

    /// <summary>Load immutable dictionary data in the background before the first chat.</summary>
    public static Task PrepareAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => { cancellationToken.ThrowIfCancellationRequested(); _ = Data.Value; }, cancellationToken);

    public static string ToHiragana(string text, CancellationToken cancellationToken = default) =>
        ToReading(text, preservePhraseEnd: false, cancellationToken);

    /// <summary>Retain sentence/question boundaries for the speech renderer.
    /// This does not infer lexical pitch accents from the pronunciation dictionary.</summary>
    public static string ToProsodicReading(string text, CancellationToken cancellationToken = default) =>
        ToReading(text, preservePhraseEnd: true, cancellationToken);

    private static string ToReading(string text, bool preservePhraseEnd, CancellationToken cancellationToken, List<AccentSpan>? accents = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024) throw new InvalidDataException("読み上げ文が長すぎます。");
        cancellationToken.ThrowIfCancellationRequested();
        text = text.Normalize(NormalizationForm.FormKC);
        var result = new StringBuilder();
        for (var i = 0; i < text.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var c = char.ToLowerInvariant(text[i]);
            if (c is >= 'a' and <= 'z') { result.Append(Letters[c - 'a']); i++; }
            else if (c is >= '0' and <= '9') { result.Append(Digits[c - '0']); i++; }
            else if (IsJapanese(c))
            {
                var start = i++;
                while (i < text.Length && IsJapanese(text[i])) i++;
                ReadJapanese(text[start..i], cancellationToken, result, accents);
            }
            else
            {
                var rune = Rune.GetRuneAt(text, i);
                if (Rune.IsLetter(rune)) throw new InvalidDataException("この文字の読みを変換できません。");
                // Emoji and punctuation are word boundaries. Never persist the original chat.
                var boundary = preservePhraseEnd ? rune.Value switch
                {
                    '?' => '?',
                    '。' or '!' or '.' => '。',
                    _ => '、'
                } : '、';
                if (result.Length > 0)
                {
                    if (result[^1] == '、' && boundary != '、') result[^1] = boundary;
                    else if (result[^1] is not ('、' or '。' or '?')) result.Append(boundary);
                }
                i += rune.Utf16SequenceLength;
            }
        }
        // The dictionary marks devoiced vowels with an apostrophe. A CV bank
        // supplies recorded syllables, so retain the vowel and omit that annotation.
        var reading = Hiragana(result.ToString()).Replace("’", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal).Trim('、');
        if (reading.Length == 0) throw new InvalidDataException("読み上げられる文字がありません。");
        if (reading.Length > 640) throw new InvalidDataException("変換後の読みが長すぎます。");
        return reading;
    }

    private static bool IsHiragana(char c) => c is >= 'ぁ' and <= 'ゖ' or 'ー';
    private static bool IsJapanese(char c) => c is >= '\u3041' and <= '\u30ff' or >= '\u3400' and <= '\u9fff' or '々' or '〆' or '〇';
    internal static string Hiragana(string value) => string.Concat(value.Select(c => c is >= 'ァ' and <= 'ヶ' ? (char)(c - 0x60) : c));

    private static void ReadJapanese(string text, CancellationToken ct, StringBuilder output, List<AccentSpan>? accents)
    {
        var data = Data.Value;
        var paths = new List<Node>?[text.Length + 1];
        paths[0] = [new(0, 0, "", null)];
        for (var start = 0; start < text.Length; start++)
        {
            ct.ThrowIfCancellationRequested();
            if (paths[start] is not { Count: > 0 } previous) continue;
            // Only the cheapest path per right context is necessary for future decisions.
            previous = previous.GroupBy(n => n.Right).Select(g => g.MinBy(n => n.Cost)!).ToList();
            var known = false;
            for (var length = 1; length <= Math.Min(data.MaximumWordLength, text.Length - start); length++)
            {
                var surface = text.AsSpan(start, length);
                var index = LowerBound(data.Words, surface);
                // The dictionary is ordinal-sorted. If even the first word
                // at/after this prefix does not start with it, no longer
                // candidate at this position can match either.
                if (index == data.Words.Length || !data.Words[index].Surface.AsSpan().StartsWith(surface, StringComparison.Ordinal))
                    break;
                while (index < data.Words.Length && data.Words[index].Surface.AsSpan().SequenceEqual(surface))
                {
                    known = true;
                    var word = data.Words[index++];
                    // Preserve MinBy's first-wins tie rule without allocating
                    // a closure/delegate for every matching dictionary word.
                    var best = previous[0];
                    var bestCost = best.Cost + data.Transitions[best.Right * data.Width + word.Left];
                    for (var candidate = 1; candidate < previous.Count; candidate++)
                    {
                        var node = previous[candidate];
                        var cost = node.Cost + data.Transitions[node.Right * data.Width + word.Left];
                        if (cost >= bestCost) continue;
                        best = node;
                        bestCost = cost;
                    }
                    (paths[start + length] ??= []).Add(new(best.Cost + data.Transitions[best.Right * data.Width + word.Left] + word.Cost,
                        word.Right, word.Reading, best, index - 1));
                }
            }
            var c = text[start];
            // Kana always has a literal route, even for new names or chat slang.
            // An unknown kanji is explicitly rejected instead of silently dropping it.
            if (c is >= 'ぁ' and <= 'ゖ' or >= 'ァ' and <= 'ヺ' or 'ー')
            {
                var best = previous.MinBy(n => n.Cost)!;
                (paths[start + 1] ??= []).Add(new(best.Cost + (known ? 14000 : 7000), 0, c.ToString(), best));
            }
        }
        if (paths[^1] is not { Count: > 0 } end) throw new InvalidDataException("辞書にない漢字の読みを変換できません。");
        var last = end.MinBy(n => n.Cost + data.Transitions[n.Right * data.Width])!;
        var pieces = new Stack<Node>();
        for (var node = last; node.Previous is not null; node = node.Previous) pieces.Push(node);
        var surfaceOffset = 0;
        while (pieces.TryPop(out var node))
        {
            if (accents is null) { output.Append(node.Reading); continue; }
            var surface = node.WordIndex >= 0 ? data.Words[node.WordIndex].Surface : node.Reading;
            var embeddedKana = surfaceOffset > 0 && IsHiragana(text[surfaceOffset - 1]) && surface.All(IsHiragana);
            var hasParticle = pieces.TryPeek(out var following) && following.WordIndex >= 0 &&
                data.Words[following.WordIndex].Surface is "が" or "を" or "の" or "に" or "は" or "も";
            surfaceOffset += surface.Length;
            var reading = Hiragana(node.Reading).Replace("’", "", StringComparison.Ordinal).Replace("'", "", StringComparison.Ordinal);
            if (node.WordIndex >= 0)
            {
                var accent = AccentData.Value;
                var at = node.WordIndex * 2;
                if (accent[at] != 255)
                    accents.Add(new(output.Length, reading, accent[at], accent[at+1], embeddedKana, hasParticle));
            }
            output.Append(reading);
        }
    }

    private static int LowerBound(Word[] words, ReadOnlySpan<char> surface)
    {
        var low = 0; var high = words.Length;
        while (low < high)
        {
            var middle = (low + high) / 2;
            if (words[middle].Surface.AsSpan().SequenceCompareTo(surface) < 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static byte[] LoadAccents()
    {
        using var source = typeof(JapaneseReading).Assembly.GetManifestResourceStream("Tanayomi.naist-jdic-accents.bin.gz")
            ?? throw new InvalidDataException("アクセント辞書が見つかりません。");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        var count = Data.Value.Words.Length;
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "TYAC1" || reader.ReadInt32() != count)
            throw new InvalidDataException("アクセント辞書が読み辞書と一致しません。");
        var result = reader.ReadBytes(count * 2);
        if (result.Length != count * 2 || reader.BaseStream.ReadByte() != -1) throw new InvalidDataException();
        return result;
    }

    private static DictionaryData Load()
    {
        using var source = typeof(JapaneseReading).Assembly.GetManifestResourceStream("Tanayomi.naist-jdic.bin.gz") ?? throw new InvalidDataException("日本語辞書が見つかりません。");
        using var gzip = new GZipStream(source, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (Encoding.ASCII.GetString(reader.ReadBytes(5)) != "TYJD1") throw new InvalidDataException("日本語辞書が破損しています。");
        var count = reader.ReadInt32();
        if (count is < 1 or > 600000) throw new InvalidDataException();
        var words = new Word[count]; var maximum = 1;
        string ReadString()
        {
            var length = reader.ReadUInt16();
            if (length > 2048) throw new InvalidDataException();
            var bytes = reader.ReadBytes(length);
            if (bytes.Length != length) throw new EndOfStreamException();
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        for (var i = 0; i < count; i++)
        {
            var surface = ReadString(); var reading = ReadString();
            words[i] = new(surface, reading, reader.ReadUInt16(), reader.ReadUInt16(), reader.ReadInt16());
            maximum = Math.Max(maximum, surface.Length);
        }
        // The generator uses UTF-16 ordinal order, matching the binary search.
        for (var i = 1; i < words.Length; i++)
            if (string.CompareOrdinal(words[i - 1].Surface, words[i].Surface) > 0) throw new InvalidDataException();
        var width = reader.ReadUInt16(); var height = reader.ReadUInt16();
        if (width != height || width is < 1 or > 2000) throw new InvalidDataException();
        var transitions = new short[width * height];
        for (var i = 0; i < transitions.Length; i++) transitions[i] = reader.ReadInt16();
        if (words.Any(w => w.Left >= width || w.Right >= height)) throw new InvalidDataException();
        return new(words, transitions, width, maximum);
    }
}
