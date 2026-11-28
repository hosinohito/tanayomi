using System.Globalization;
using System.Text;

namespace Tanayomi;

/// <summary>Optional transliteration, not language detection. Call only for an opted-in field.</summary>
public static class RomajiReading
{
    public const int MaximumInputScalars = 16_384;
    private static readonly Dictionary<string, string> Syllables = CreateSyllables();

    public static string Convert(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var scalars = 0;
        foreach (var _ in text.EnumerateRunes())
            if (++scalars > MaximumInputScalars)
                throw new ArgumentException("ローマ字変換の入力が上限を超えています。", nameof(text));

        var result = new StringBuilder(text.Length);
        var schemeScannedThrough = 0;
        for (var position = 0; position < text.Length;)
        {
            var start = position;
            if (position >= schemeScannedThrough && StartsUrl(text, position, out schemeScannedThrough))
            {
                while (position < text.Length && !char.IsWhiteSpace(text, position)) position++;
                result.Append(text, start, position - start);
            }
            else if (IsIdentifier(text, position, out var width))
            {
                position += width;
                while (position < text.Length)
                {
                    if (IsIdentifier(text, position, out width)) position += width;
                    else if (text[position] == '\'' &&
                        (text[position - 1] is 'n' or 'N' ||
                         position + 1 < text.Length && IsIdentifier(text, position + 1, out _))) position++;
                    else break;
                }
                var token = text.AsSpan(start, position - start);
                result.Append(TryRead(token, out var reading) ? reading : token.ToString());
            }
            else
            {
                // Preserve original Unicode, including emoji joiners and combining sequences.
                width = Rune.TryGetRuneAt(text, position, out var rune) ? rune.Utf16SequenceLength : 1;
                result.Append(text, position, width);
                position += width;
            }
        }
        return result.ToString();
    }

    private static bool TryRead(ReadOnlySpan<char> token, out string reading)
    {
        reading = "";
        var letters = 0;
        var allUpper = true;
        foreach (var c in token)
        {
            if (c == '\'') continue;
            if (!char.IsAsciiLetter(c)) return false;
            letters++;
            if (c is >= 'a' and <= 'z') allUpper = false;
        }
        if (letters < 2 || allUpper) return false;
        // Short lowercase romaji (e.g. ra, shi) is valid; keep short mixed-case abbreviations.
        if (letters < 4)
            foreach (var c in token)
                if (c is >= 'A' and <= 'Z') return false;
        var lower = token.ToString().ToLowerInvariant();
        var result = new StringBuilder(lower.Length);
        for (var i = 0; i < lower.Length;)
        {
            var c = lower[i];
            if (c == 'n')
            {
                if (i + 1 == lower.Length) { result.Append('ん'); i++; continue; }
                var next = lower[i + 1];
                if (next == '\'') { result.Append('ん'); i += 2; continue; }
                if (next == 'n')
                {
                    result.Append('ん');
                    i += i + 2 < lower.Length && IsVowelOrY(lower[i + 2]) ? 1 : 2;
                    continue;
                }
                if (!IsVowelOrY(next)) { result.Append('ん'); i++; continue; }
            }
            if (i + 1 < lower.Length && c != 'n' && !IsVowelOrY(c) &&
                (c == lower[i + 1] || c == 't' && lower.AsSpan(i + 1).StartsWith("ch", StringComparison.Ordinal)))
            {
                result.Append('っ'); i++; continue;
            }
            var matched = false;
            for (var length = Math.Min(3, lower.Length - i); length > 0; length--)
                if (Syllables.TryGetValue(lower.Substring(i, length), out var kana))
                {
                    result.Append(kana); i += length; matched = true; break;
                }
            if (!matched) return false;
        }
        reading = result.ToString();
        return true;
    }

    private static bool IsVowelOrY(char c) => c is 'a' or 'i' or 'u' or 'e' or 'o' or 'y';

    private static bool IsIdentifier(string text, int index, out int width)
    {
        width = 1;
        if (text[index] == '_') return true;
        if (!Rune.TryGetRuneAt(text, index, out var rune)) return false;
        width = rune.Utf16SequenceLength;
        return Rune.IsLetterOrDigit(rune) || Rune.GetUnicodeCategory(rune) is
            UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
    }

    private static bool StartsUrl(string text, int index, out int scannedThrough)
    {
        var rest = text.AsSpan(index);
        scannedThrough = index + 1;
        if (rest.StartsWith("www.", StringComparison.OrdinalIgnoreCase)) return true;
        if (!char.IsAsciiLetter(rest[0])) return false;
        var length = 1;
        while (length < rest.Length && (char.IsAsciiLetterOrDigit(rest[length]) || rest[length] is '+' or '-' or '.')) length++;
        // A failed scheme scan also rules out starts inside that same run. This
        // keeps many hyphen-separated words linear rather than rescanning their suffixes.
        scannedThrough = index + length;
        return rest[length..].StartsWith("://", StringComparison.Ordinal);
    }

    private static Dictionary<string, string> CreateSyllables()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Row(string consonant, params string[] kana)
        {
            const string vowels = "aiueo";
            for (var i = 0; i < vowels.Length; i++)
                if (kana[i].Length != 0) result.Add(consonant + vowels[i], kana[i]);
        }
        void Yoon(string consonant, string stem)
        {
            result.Add(consonant + "a", stem + "ゃ");
            result.Add(consonant + "u", stem + "ゅ");
            result.Add(consonant + "o", stem + "ょ");
        }
        Row("", "あ", "い", "う", "え", "お");
        Row("k", "か", "き", "く", "け", "こ"); Row("g", "が", "ぎ", "ぐ", "げ", "ご");
        Row("s", "さ", "し", "す", "せ", "そ"); Row("z", "ざ", "じ", "ず", "ぜ", "ぞ");
        Row("t", "た", "ち", "つ", "て", "と"); Row("d", "だ", "ぢ", "づ", "で", "ど");
        Row("n", "な", "に", "ぬ", "ね", "の"); Row("h", "は", "ひ", "ふ", "へ", "ほ");
        Row("b", "ば", "び", "ぶ", "べ", "ぼ"); Row("p", "ぱ", "ぴ", "ぷ", "ぺ", "ぽ");
        Row("m", "ま", "み", "む", "め", "も"); Row("r", "ら", "り", "る", "れ", "ろ");
        Row("y", "や", "", "ゆ", "", "よ"); Row("w", "わ", "うぃ", "", "うぇ", "を");
        Row("f", "ふぁ", "ふぃ", "ふ", "ふぇ", "ふぉ"); Row("v", "ゔぁ", "ゔぃ", "ゔ", "ゔぇ", "ゔぉ");
        Row("j", "じゃ", "じ", "じゅ", "じぇ", "じょ");
        Row("sh", "しゃ", "し", "しゅ", "しぇ", "しょ");
        Row("ch", "ちゃ", "ち", "ちゅ", "ちぇ", "ちょ");
        Row("ts", "つぁ", "つぃ", "つ", "つぇ", "つぉ");
        foreach (var (prefix, stem) in new (string, string)[]
        {
            ("ky", "き"), ("gy", "ぎ"), ("sy", "し"), ("zy", "じ"), ("jy", "じ"),
            ("ty", "ち"), ("cy", "ち"), ("dy", "ぢ"), ("ny", "に"), ("hy", "ひ"),
            ("by", "び"), ("py", "ぴ"), ("my", "み"), ("ry", "り")
        }) Yoon(prefix, stem);
        return result;
    }
}
