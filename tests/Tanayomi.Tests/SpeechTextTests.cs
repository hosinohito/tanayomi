using System.Text;
using Tanayomi;

public static class SpeechTextTests
{
    public static Task RunAsync(Action<bool, string> check)
    {
        var plain = new SpeechText();
        check(plain.Prepare("Twitch", "1", "hoshinohito", "konnichiha").Text == "konnichiha", "romaji conversion remains opt-in");
        var romaji = new SpeechText(new(NameReadingMode.EveryMessage,
            [new("Twitch", "custom", "hoshinohito")], [new("term", "konnichiha")],
            romajiNames: true, romajiBody: true));
        check(romaji.Prepare("Twitch", "1", "hoshinohito", "term konnichiha").Text == "ほしのひと、konnichiha こんにちは",
            "romaji applies to unmatched body and unregistered names while dictionary output stays exact");
        check(romaji.Prepare("Twitch", "custom", "ignored", "konnichiha").Text == "hoshinohito、こんにちは",
            "custom author readings take priority over romaji conversion");
        check(new SpeechText(new(NameReadingMode.EveryMessage, romajiNames: true)).Prepare("Twitch", "1", "hoshinohito", "konnichiha").Text == "ほしのひと、konnichiha",
            "name romaji can be enabled independently of the body");
        check(new SpeechText(new(NameReadingMode.EveryMessage, romajiBody: true)).Prepare("Twitch", "1", "hoshinohito", "konnichiha").Text == "hoshinohito、こんにちは",
            "body romaji can be enabled independently of author names");
        check(plain.Prepare("Twitch", "42", "表示名", "こんにちは").Text == "こんにちは", "speech names remain off by default");
        check(plain.Prepare("Twitch", null, "name", " A\0\tB\nHTTPS://example.test/隣接  C ").Text == "A B リンク C",
            "speech normalization preserves existing URL and whitespace behavior");
        check(SpeechText.NormalizeText("か\u3099 😀👩‍💻") == "か\u3099 😀👩‍💻", "speech text preserves combining characters and emoji sequences without Unicode folding");
        check(SpeechText.NormalizeText("x\ud800y") == "x\ufffdy", "malformed incoming UTF16 is bounded and replaced without passing invalid speech text");

        var settings = new SpeechTextSettings(NameReadingMode.EveryMessage,
            [new("Twitch", "42", "よみ"), new("YouTube", "42", "べつ"), new("Twitch", "CaseID", "おおもじ")],
            [new("本文", "ほんぶん"), new("よみ", "名前に適用しない")],
            [new("Twitch", "blocked")]);
        var speech = new SpeechText(settings);
        check(speech.Prepare("tWiTcH", "42", "旧名", "本文").Text == "よみ、ほんぶん", "individual name readings use service and stable author ID");
        check(speech.Prepare("Twitch", "42", "変更後", "本文").Text == "よみ、ほんぶん", "display-name changes preserve the same author's registered reading");
        check(speech.Prepare("Twitch", "43", "旧名", "本文").Text == "旧名、ほんぶん", "same display name with a different ID is a different author");
        check(speech.Prepare("YouTube", "42", "旧名", "本文").Text == "べつ、ほんぶん", "same ID string from different services does not share a reading");
        check(speech.Prepare("Twitch", "caseid", "通常", "本文").Text == "通常、ほんぶん", "stable author IDs retain ordinal case sensitivity");
        check(speech.Prepare("Twitch", null, "42", "本文").Text == "42、ほんぶん" &&
            speech.Prepare("Twitch", " ", "42", "本文").Text == "42、ほんぶん", "missing author ID never infers an individual name reading");
        var muted = speech.Prepare("Twitch", "blocked", "別表示", "本文");
        check(muted.IsMuted && muted.Text.Length == 0, "local mute selects service and stable ID without generating speech");
        check(!speech.Prepare("YouTube", "blocked", "同名", "本文").IsMuted &&
            !speech.Prepare("Twitch", null, "blocked", "本文").IsMuted, "mute never crosses services or guesses a missing ID from display name");
        check(speech.Prepare("Twitch", "blocked", "作者", "本文", isManual: true).Text == "ほんぶん",
            "manual input applies body rules but neither author naming nor local mute");
        const string original = "本文";
        _ = speech.Prepare("Twitch", "42", "旧名", original);
        check(original == "本文", "speech preparation leaves original received text unchanged");

        var words = new SpeechText(new(replacements:
            [new("ab", "長い"), new("a", "短い"), new("ab", "同長の後"), new("A", "B"), new("B", "C"), new("😀", "えがお")]));
        check(words.Prepare("", null, "", "ab a").Text == "長い 短い", "literal replacement chooses longest match and first equal-length registration");
        check(words.Prepare("", null, "", "A B").Text == "B C", "literal replacement never recursively processes its own output");
        check(words.Prepare("", null, "", "😀AB").Text == "えがおBC", "literal replacements preserve scalar boundaries including supplementary characters");
        var exact = new SpeechText(new(replacements: [new("が", "G"), new("foo", "F")]));
        check(exact.Prepare("", null, "", "が か\u3099 Foo foo").Text == "G か\u3099 Foo F", "literal dictionaries are ordinal and do not silently fold case or Unicode");
        var urls = new SpeechText(new(replacements: [new("example", "不適用"), new("リンク", "URL")]));
        check(urls.Prepare("", null, "", "https://example.test/a").Text == "URL", "URL normalization runs before the body dictionary");
        check(!SpeechTextSettings.Default.OmitUrls, "URL omission is disabled by default");
        var omitUrls = new SpeechText(new(NameReadingMode.EveryMessage,
            replacements: [new("リンク", "URL"), new("konnichiha", "辞書")], romajiBody: true, omitUrls: true));
        check(omitUrls.Prepare("Twitch", "1", "名前", "HTTPS://example.test/a").Text.Length == 0,
            "URL-only bodies become empty before author naming and literal replacement");
        check(omitUrls.Prepare("", null, "", "前 https://example.test/隣接 後 konnichiha arigatou", isManual: true).Text == "前 後 辞書 ありがとう",
            "manual URL omission keeps existing URL boundaries and runs before dictionary and romaji processing");
        check(omitUrls.Prepare("", null, "", "www.example.test ftp://example.test http://", isManual: true).Text ==
            "www.example.test ftp://example.test http://", "URL omission does not expand the established HTTP(S) matching rule");
        check(omitUrls.Prepare("Twitch", "1", "https://example.test/name", "本文").Text == "リンク、本文",
            "body URL omission does not rewrite author-name processing");
        var deletion = new SpeechText(new(NameReadingMode.EveryMessage, replacements: [new("a", "")]));
        check(deletion.Prepare("Twitch", "1", "名", "aaaa").Text.Length == 0, "an empty replaced body does not speak only the author name");
        check(deletion.Prepare("", null, "", new string('a', 1000) + "Z").Text == "Z", "deletion scans original input beyond the spoken-prefix limit");
        var spaces = new SpeechText(new(replacements: [new("a", new string(' ', 128))]));
        check(spaces.Prepare("", null, "", new string('a', 1000) + "終").Text == "終", "replacement whitespace does not consume the final spoken-scalar budget");

        var emoji180 = string.Concat(Enumerable.Repeat("😀", 180));
        check(plain.Prepare("", null, "", emoji180).Text == emoji180, "180 supplementary scalars remain intact without omission");
        var shortened = plain.Prepare("", null, "", emoji180 + "終");
        check(shortened.IsTruncated && shortened.Text == emoji180 + SpeechText.Omission && shortened.Text.EnumerateRunes().Count() == 185,
            "speech keeps the existing 180-scalar prefix plus five-scalar omission phrase");
        var withName = speech.Prepare("Twitch", "42", "名", new string('文', 180));
        check(withName.Text.StartsWith("よみ、", StringComparison.Ordinal) && withName.Text.EnumerateRunes().Count() == 185 && withName.IsTruncated,
            "name and body share one final speech limit");
        var expansion = new SpeechText(new(replacements: [new("a", new string('b', 128))]));
        check(expansion.Prepare("", null, "", new string('a', 16384)).Text == new string('b', 180) + SpeechText.Omission,
            "large replacement expansion is bounded to the useful speech prefix");
        var lookahead = new SpeechText(new(replacements: [new("unused", "x")]));
        check(lookahead.Prepare("", null, "", new string('a', 180) + " b").Text == new string('a', 180) + SpeechText.Omission,
            "lookahead across a pending whitespace still detects truncation");
        check(plain.Prepare("", null, "", new string('a', 16384)).IsTruncated, "maximum-sized input remains bounded and readable");
        Reject(() => plain.Prepare("", null, "", new string('a', 16385)), check, "oversized text input is rejected before normalization");

        var mutable = new[] { new SpeechReplacement("x", "旧") };
        var snapshot = new SpeechTextSettings(replacements: mutable);
        mutable[0] = new("x", "新");
        check(new SpeechText(snapshot).Prepare("", null, "", "x").Text == "旧", "settings snapshot copies caller-owned collections");
        var readOnly = false;
        try { ((IList<SpeechReplacement>)snapshot.Replacements)[0] = new("x", "変更"); }
        catch (NotSupportedException) { readOnly = true; }
        check(readOnly, "settings expose rule collections without writable mutation");
        var scalar128 = string.Concat(Enumerable.Repeat("😀", 128));
        check(new SpeechTextSettings(replacements: [new(scalar128, scalar128)]).Replacements.Count == 1,
            "rule length validation counts Unicode scalars rather than UTF16 units");
        Reject(() => new SpeechTextSettings(replacements: [new(scalar128 + "a", "x")]), check, "129-scalar literal keys are rejected");
        Reject(() => new SpeechTextSettings(replacements: [new("x", scalar128 + "a")]), check, "129-scalar readings are rejected");
        Reject(() => new SpeechTextSettings(replacements: [new("", "x")]), check, "empty literal keys are rejected");
        Reject(() => new SpeechTextSettings(replacements: [new("x", "a\nb")]), check, "control characters in configured readings are rejected");
        Reject(() => new SpeechTextSettings(replacements: [new("\ud800", "x")]), check, "malformed UTF16 in configured keys is rejected");
        Reject(() => new SpeechTextSettings(nameReadings: [new("Twitch", "", "名")]), check, "empty stable IDs cannot create name-reading rules");
        Reject(() => new SpeechTextSettings(nameReadings: [new("Twitch", "1", " ")]), check, "blank individual name readings are rejected");
        Reject(() => new SpeechTextSettings(mutedAuthors: [new("Twitch", "a b")]), check, "whitespace in stable IDs is rejected");
        Reject(() => new SpeechTextSettings(nameReadings: [new("Twitch", "1", "A"), new("twitch", "1", "B")]), check, "duplicate identity name rules cannot silently override each other");
        Reject(() => new SpeechTextSettings(replacements: Enumerable.Range(0, 129).Select(i => new SpeechReplacement(i.ToString(), "x"))), check, "literal dictionary count is bounded");
        Reject(() => new SpeechTextSettings(nameReadings: Enumerable.Range(0, 129).Select(i => new SpeechNameReading("Twitch", i.ToString(), "名"))), check, "individual name rule count is bounded");
        Reject(() => new SpeechTextSettings(mutedAuthors: Enumerable.Range(0, 129).Select(i => new SpeechAuthor("Twitch", i.ToString()))), check, "local mute rule count is bounded");
        Reject(() => new SpeechTextSettings((NameReadingMode)99), check, "unknown name-reading modes are rejected");
        return Task.CompletedTask;
    }

    private static void Reject(Action action, Action<bool, string> check, string name)
    {
        try { action(); }
        catch (ArgumentException) { check(true, name); return; }
        check(false, name);
    }
}
