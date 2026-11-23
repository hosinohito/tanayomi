using Tanayomi;

internal static class JapaneseReadingTests
{
    public static void Run(Action<bool, string> check)
    {
        var city = JapaneseReading.ToHiragana("東京");
        check(JapaneseReading.ToHiragana("東京ゖ東京") == city + "ゖ" + city,
            "missing dictionary prefix retains the preceding match, literal kana and following match");
        var rejected = false;
        try { _ = JapaneseReading.ToHiragana("東京\u9fff"); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "prefix-search termination never discards an unreadable trailing kanji");
        check(JapaneseReading.ToHiragana("こんにちは。テストです？") == "こんにちわ、てすとです",
            "plain reading remains compatible when renderer sentence markers are introduced");
        check(JapaneseReading.ToProsodicReading("こんにちは。テストです？") == "こんにちわ。てすとです?",
            "speech reading preserves sentence and question boundaries without inventing word accents");
        check(JapaneseReading.ToProsodicReading("テストです？！") == "てすとです?",
            "consecutive punctuation keeps a single question boundary");
        check(JapaneseReading.ToProsodicReading("こんにちは！テストです。") == "こんにちわ。てすとです。",
            "exclamation and sentence punctuation terminate a speech phrase");
        check(JapaneseReading.ToProsodicReading("こんにちは😀") == "こんにちわ",
            "trailing emoji does not manufacture a question or sentence contour");
    }
}
