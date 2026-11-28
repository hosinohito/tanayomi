using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;

namespace Tanayomi;

public enum NameReadingMode { Off, EveryMessage, OnSpeakerChange }
public sealed record SpeechAuthor(string Platform, string AuthorId);
public sealed record SpeechNameReading(string Platform, string AuthorId, string Reading);
public sealed record SpeechReplacement(string Key, string Reading);
public sealed record SpeechTextResult(string Text, bool IsMuted, bool IsTruncated)
{
    internal string? SameSpeakerText { get; init; }
}

/// <summary>Validated immutable snapshot. Construct a replacement before publishing
/// it, so an invalid settings edit cannot modify the active rules.</summary>
public sealed class SpeechTextSettings
{
    public const int MaximumRules = 128;
    public const int MaximumRuleScalars = 128;
    public const int MaximumBodyPatterns = 16;
    public static SpeechTextSettings Default { get; } = new();
    public NameReadingMode NameReading { get; }
    public bool NameAfterBody { get; }
    public bool RomajiNames { get; }
    public bool RomajiBody { get; }
    public bool SuppressRepeatedMessages { get; }
    public bool OmitUrls { get; }
    public int? BodyLimit { get; }
    public IReadOnlyList<SpeechNameReading> NameReadings { get; }
    public IReadOnlyList<SpeechReplacement> Replacements { get; }
    public IReadOnlyList<SpeechAuthor> MutedAuthors { get; }
    public IReadOnlyList<string> MutedBodyPatterns { get; }
    private readonly Regex[] mutedBodyMatchers;
    private readonly Dictionary<(string Platform, string AuthorId), string> namesById = new();
    private readonly HashSet<(string Platform, string AuthorId)> mutedById = new();
    internal IReadOnlyList<SpeechReplacement> OrderedReplacements { get; }
    internal bool TryName((string Platform, string AuthorId) key, out string reading) => namesById.TryGetValue(key, out reading!);
    internal bool IsMuted((string Platform, string AuthorId) key) => mutedById.Contains(key);

    public SpeechTextSettings(NameReadingMode nameReading = NameReadingMode.Off,
        IEnumerable<SpeechNameReading>? nameReadings = null, IEnumerable<SpeechReplacement>? replacements = null,
        IEnumerable<SpeechAuthor>? mutedAuthors = null, bool romajiNames = false, bool romajiBody = false,
        bool suppressRepeatedMessages = false, bool omitUrls = false, int? bodyLimit = null,
        IEnumerable<string>? mutedBodyPatterns = null, bool nameAfterBody = false)
    {
        if (!Enum.IsDefined(nameReading)) throw new ArgumentException("名前読みの設定が不正です。", nameof(nameReading));
        NameReading = nameReading;
        NameAfterBody = nameAfterBody;
        if (bodyLimit is < 1 or > SpeechText.MaximumSpokenScalars)
            throw new ArgumentOutOfRangeException(nameof(bodyLimit));
        BodyLimit = bodyLimit;
        RomajiNames = romajiNames;
        RomajiBody = romajiBody;
        SuppressRepeatedMessages = suppressRepeatedMessages;
        OmitUrls = omitUrls;
        var names = Copy(nameReadings);
        var words = Copy(replacements);
        var muted = Copy(mutedAuthors);
        foreach (var rule in names)
        {
            var key = RuleIdentity(rule.Platform, rule.AuthorId);
            Validate(rule.Reading, allowEmpty: false);
            if (!namesById.TryAdd(key, rule.Reading)) throw new ArgumentException("同じ発言者の名前読みが重複しています。");
        }
        foreach (var rule in words)
        {
            Validate(rule.Key, allowEmpty: false);
            Validate(rule.Reading, allowEmpty: true);
        }
        foreach (var rule in muted)
            if (!mutedById.Add(RuleIdentity(rule.Platform, rule.AuthorId))) throw new ArgumentException("同じ発言者のミュートが重複しています。");
        NameReadings = Array.AsReadOnly(names);
        Replacements = Array.AsReadOnly(words);
        MutedAuthors = Array.AsReadOnly(muted);
        var patterns = mutedBodyPatterns?.Take(MaximumBodyPatterns + 1).ToArray() ?? [];
        if (patterns.Length > MaximumBodyPatterns)
            throw new ArgumentException("本文の除外パターンは16件までです。", nameof(mutedBodyPatterns));
        MutedBodyPatterns = Array.AsReadOnly(patterns);
        mutedBodyMatchers = new Regex[patterns.Length];
        for (var i = 0; i < patterns.Length; i++)
        {
            try
            {
                Validate(patterns[i], allowEmpty: false);
                mutedBodyMatchers[i] = new Regex(patterns[i],
                    RegexOptions.NonBacktracking | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(10));
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException)
            {
                throw new ArgumentException($"本文の除外パターン {i + 1} 件目を確認してください。128文字までで、先読み・後読みや後方参照は使えません。",
                    nameof(mutedBodyPatterns), error);
            }
        }
        // LINQ ordering is stable: equal-length duplicates retain input order.
        OrderedReplacements = Array.AsReadOnly(words.OrderByDescending(x => x.Key.EnumerateRunes().Count()).ToArray());
    }

    internal bool IsBodyMuted(string text)
    {
        foreach (var matcher in mutedBodyMatchers)
        {
            try { if (matcher.IsMatch(text)) return true; }
            catch (RegexMatchTimeoutException) { /* A slow rule must not discard the message. */ }
        }
        return false;
    }

    private static T[] Copy<T>(IEnumerable<T>? source) where T : class
    {
        var result = new List<T>();
        if (source is not null)
            foreach (var value in source)
            {
                if (value is null || result.Count >= MaximumRules) throw new ArgumentException("読み上げルールは種類ごとに128件までです。");
                result.Add(value);
            }
        return result.ToArray();
    }

    private static (string, string) RuleIdentity(string platform, string authorId)
    {
        Validate(platform, allowEmpty: false);
        Validate(authorId, allowEmpty: false);
        if (authorId.EnumerateRunes().Any(Rune.IsWhiteSpace)) throw new ArgumentException("発言者IDに空白は使えません。");
        return (platform.Trim().ToUpperInvariant(), authorId);
    }

    private static void Validate(string value, bool allowEmpty)
    {
        if (value is null || !allowEmpty && string.IsNullOrWhiteSpace(value)) throw new ArgumentException("読み上げルールの値が空です。");
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || Rune.IsControl(rune))
                throw new ArgumentException("読み上げルールに不正な文字や制御文字は使えません。");
            if (++count > MaximumRuleScalars) throw new ArgumentException("読み上げルールの値は128文字までです。");
            remaining = remaining[consumed..];
        }
    }
}

/// <summary>Pure speech-only processing. Message/display data and service state
/// remain untouched; no name-history, storage or network access is involved.</summary>
public sealed class SpeechText(SpeechTextSettings? settings = null)
{
    public const int MaximumInputScalars = 16384;
    public const int MaximumSpokenScalars = 180;
    public const string Omission = "、以下省略";
    private readonly SpeechTextSettings snapshot = settings ?? SpeechTextSettings.Default;
    internal bool SuppressRepeatedMessages => snapshot.SuppressRepeatedMessages;

    public SpeechTextResult Prepare(string platform, string? authorId, string author, string text, bool isManual = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        (string Platform, string AuthorId) identity = default;
        var identified = !isManual && TryIdentity(platform, authorId, out identity);
        if (identified && snapshot.IsMuted(identity)) return new("", true, false);
        ValidateInputLength(text);
        if (snapshot.IsBodyMuted(text)) return new("", true, false);
        var body = Replace(NormalizeValidatedText(text, snapshot.OmitUrls));
        body = NormalizeWhitespace(body);
        if (body.Length == 0) return new("", false, false);
        var clipped = false;
        if (!isManual && snapshot.BodyLimit is int limit)
            body = Prefix(body, limit, out clipped);
        var plain = body;
        if (!isManual && snapshot.NameReading != NameReadingMode.Off)
        {
            string? reading = null;
            var custom = identified && snapshot.TryName(identity, out reading);
            var name = custom ? reading : author;
            name = NormalizeText(name ?? "");
            if (!custom && snapshot.RomajiNames) name = RomajiReading.Convert(name);
            if (name.Length != 0)
            {
                if (snapshot.NameAfterBody)
                {
                    // Reserve room for the name even when the body fills the reading limit.
                    name = Prefix(name, MaximumSpokenScalars - 2, out var nameClipped);
                    var endingBody = Prefix(body, MaximumSpokenScalars - 1 - name.EnumerateRunes().Count(), out var bodyClipped);
                    return new(endingBody + (clipped || bodyClipped ? Omission : "") + "、" + name +
                        (nameClipped ? Omission : ""), false, clipped || bodyClipped || nameClipped)
                    {
                        SameSpeakerText = SameSpeaker(plain, identified, clipped)
                    };
                }
                body = name + "、" + body;
            }
        }
        var limited = Prefix(body, MaximumSpokenScalars, out var totalClipped);
        return new(limited + (clipped || totalClipped ? Omission : ""), false, clipped || totalClipped)
        {
            SameSpeakerText = SameSpeaker(plain, identified, clipped)
        };
    }

    private string? SameSpeaker(string body, bool identified, bool clipped) =>
        identified && snapshot.NameReading == NameReadingMode.OnSpeakerChange
            ? (clipped ? body + Omission : Truncate(body)) : null;

    public static string NormalizeText(string text, bool omitUrls = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        ValidateInputLength(text);
        return NormalizeValidatedText(text, omitUrls);
    }

    private static void ValidateInputLength(string text)
    {
        var count = 0;
        foreach (var rune in text.EnumerateRunes())
            if (++count > MaximumInputScalars) throw new ArgumentException("読み上げ入力が長すぎます。", nameof(text));
    }

    private static string NormalizeValidatedText(string text, bool omitUrls)
    {
        // Preserve the established URL boundary: HTTP(S) through the next
        // whitespace, including adjacent punctuation, is replaced with リンク
        // or omitted by explicit choice. This does not fetch or expand links.
        text = Regex.Replace(text, @"https?://\S+", omitUrls ? "" : "リンク", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
        return NormalizeWhitespace(text);
    }

    private static string NormalizeWhitespace(string text)
    {
        var builder = new StringBuilder(text.Length);
        var space = false;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) { space = builder.Length > 0; continue; }
            if (Rune.IsControl(rune)) continue;
            if (space) { builder.Append(' '); space = false; }
            builder.Append(rune.ToString());
        }
        return builder.ToString();
    }

    public static string Truncate(string text)
        => Prefix(text, MaximumSpokenScalars, out var clipped) + (clipped ? Omission : "");

    private static string Prefix(string text, int limit, out bool clipped)
    {
        ArgumentNullException.ThrowIfNull(text);
        var count = 0;
        var length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (count++ == limit) { clipped = true; return text[..length]; }
            length += rune.Utf16SequenceLength;
        }
        clipped = false;
        return text;
    }

    private string Replace(string text)
    {
        if (snapshot.OrderedReplacements.Count == 0) return snapshot.RomajiBody ? RomajiReading.Convert(text) : text;
        // Keep only the visible prefix and lookahead (plus a pending space). Continue
        // scanning original input: deletion rules may reveal later text, while
        // replacement output is never fed back into the matching process.
        var result = new StringBuilder(MaximumSpokenScalars * 2);
        var count = 0;
        const int prefixLimit = MaximumSpokenScalars + 1;
        var space = false;
        var unmatched = new StringBuilder();
        void Append(Rune rune)
        {
            if (Rune.IsWhiteSpace(rune)) { space = result.Length > 0; return; }
            if (space && count <= prefixLimit) { result.Append(' '); count++; space = false; }
            if (count <= prefixLimit) { result.Append(rune.ToString()); count++; }
        }
        void FlushUnmatched()
        {
            if (unmatched.Length == 0) return;
            var source = unmatched.ToString();
            unmatched.Clear();
            foreach (var rune in (snapshot.RomajiBody ? RomajiReading.Convert(source) : source).EnumerateRunes())
            {
                Append(rune);
                if (count > prefixLimit) break;
            }
        }
        for (var at = 0; at < text.Length && count <= prefixLimit;)
        {
            var matched = false;
            foreach (var rule in snapshot.OrderedReplacements)
            {
                if (!text.AsSpan(at).StartsWith(rule.Key, StringComparison.Ordinal)) continue;
                FlushUnmatched();
                foreach (var rune in rule.Reading.EnumerateRunes())
                {
                    Append(rune);
                    if (count > prefixLimit) break;
                }
                at += rule.Key.Length;
                matched = true; break;
            }
            if (matched) continue;
            Rune.DecodeFromUtf16(text.AsSpan(at), out var original, out var consumed);
            unmatched.Append(original.ToString()); at += consumed;
        }
        FlushUnmatched();
        return result.ToString();
    }

    internal static bool TryIdentity(string platform, string? authorId, out (string Platform, string AuthorId) identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(platform) || string.IsNullOrWhiteSpace(authorId)) return false;
        if (!IsBoundedIdentifier(platform, allowSpace: true) || !IsBoundedIdentifier(authorId, allowSpace: false)) return false;
        identity = (platform.Trim().ToUpperInvariant(), authorId);
        return true;
    }

    private static bool IsBoundedIdentifier(string value, bool allowSpace)
    {
        var remaining = value.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != OperationStatus.Done || Rune.IsControl(rune) ||
                !allowSpace && Rune.IsWhiteSpace(rune) || ++count > SpeechTextSettings.MaximumRuleScalars) return false;
            remaining = remaining[consumed..];
        }
        return true;
    }
}
