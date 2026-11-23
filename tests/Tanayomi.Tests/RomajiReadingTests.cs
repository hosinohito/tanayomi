using Tanayomi;

public static class RomajiReadingTests
{
    public static Task RunAsync(Action<bool, string> check)
    {
        void Reads(string input, string expected, string reason) => check(RomajiReading.Convert(input) == expected, reason);
        Reads("konnichiha arigatou", "こんにちは ありがとう", "romaji converts complete common words");
        Reads("SHINJUKU Tokyo tanaka", "SHINJUKU ときょ たなか", "romaji preserves uppercase acronyms and accepts mixed case without inferring English");
        Reads("shi chi tsu fu ji ai neko", "shi chi tsu fu ji ai ねこ", "romaji preserves tokens shorter than four ASCII letters");
        Reads("shichitsufujisa", "しちつふじさ", "romaji supports Hepburn digraphs");
        Reads("sitituhuzi didu", "しちつふじ ぢづ", "romaji supports common Kunrei-compatible syllables");
        Reads("kyakyukyogyagyugyo", "きゃきゅきょぎゃぎゅぎょ", "romaji supports voiced and unvoiced palatalized syllables");
        Reads("shashusho syasyusyo chachucho tyatyutyo", "しゃしゅしょ しゃしゅしょ ちゃちゅちょ ちゃちゅちょ", "romaji supports common alternative yoon spellings");
        Reads("jajujonyanyunyo hyahyuhyo byabyubyo pyapyupyo myamyumyo ryaryuryo", "じゃじゅじょにゃにゅにょ ひゃひゅひょ びゃびゅびょ ぴゃぴゅぴょ みゃみゅみょ りゃりゅりょ", "romaji covers remaining yoon rows");
        Reads("shinyou shin'you shinn shin", "しにょう しんよう しん しん", "romaji distinguishes n apostrophe from palatalized ny and accepts final n or nn");
        Reads("kannai kannpai konnichiha kannnya", "かんない かんぱい こんにちは かんにゃ", "romaji handles nn before vowels consonants and yoon without recursive replacement");
        Reads("gakkou zasshi kippu matcha", "がっこう ざっし きっぷ まっちゃ", "romaji supports doubled consonants and tch gemination");
        Reads("oosaka toukyou sensei", "おおさか とうきょう せんせい", "romaji retains explicitly written long-vowel spelling");
        Reads("name banana", "なめ ばなな", "romaji deliberately does not detect English words and requires opt-in by its caller");
        Reads("konnichihax hello qwerty", "konnichihax hello qwerty", "romaji leaves an entire unparseable word unchanged instead of converting a prefix");
        Reads("tanaka42 42tanaka tanaka_name _tanaka tanaka_", "tanaka42 42tanaka tanaka_name _tanaka tanaka_", "romaji preserves complete identifiers with digits or underscores");
        Reads("TANAKA KONNICHIHA Shin'You", "TANAKA KONNICHIHA しんよう", "romaji treats apostrophes consistently with uppercase preservation");
        Reads("'konnichiha' konnichiha's shin'", "'こんにちは' konnichiha's しん", "romaji preserves quoting and rejects non-n apostrophe inside a token");
        Reads("https://konnichiha.test/tanaka HTTP://name.test www.tanaka.test/name ftp://tanaka.test/name konnichiha", "https://konnichiha.test/tanaka HTTP://name.test www.tanaka.test/name ftp://tanaka.test/name こんにちは", "romaji preserves complete URL spans before scanning their words");
        Reads("😀konnichiha👩‍💻 (arigatou)、tanaka!", "😀こんにちは👩‍💻 (ありがとう)、たなか!", "romaji retains emoji sequences and punctuation boundaries");
        Reads("日本konnichiha café konnichiha\u0301 ｔａｎａｋａ tanaka４２", "日本konnichiha café konnichiha\u0301 ｔａｎａｋａ tanaka４２", "romaji preserves mixed Unicode words combining marks and non-ASCII identifiers");
        Reads("Tōkyō xya ltu", "Tōkyō xya ltu", "romaji does not guess macron vowels or IME-only small-kana input conventions");
        Reads("konnichiha\ud800arigatou", "こんにちは\ud800ありがとう", "romaji preserves an existing malformed UTF16 boundary for the caller's normalizer");
        Reads("konnichiha\n\t arigatou", "こんにちは\n\t ありがとう", "romaji does not normalize whitespace or change its caller's formatting");
        Reads(string.Concat(Enumerable.Repeat("aaaa-", 2000)), string.Concat(Enumerable.Repeat("ああああ-", 2000)), "romaji handles many scheme-like hyphenated words without suffix rescans");
        Reads("", "", "romaji accepts empty text");
        Reads(new string('a', RomajiReading.MaximumInputScalars), new string('あ', RomajiReading.MaximumInputScalars), "romaji accepts its exact scalar limit without unbounded expansion");
        var supplementary = string.Concat(Enumerable.Repeat("😀", RomajiReading.MaximumInputScalars));
        Reads(supplementary, supplementary, "romaji counts supplementary characters as one scalar");
        var oversized = false;
        try { _ = RomajiReading.Convert(supplementary + "a"); }
        catch (ArgumentException) { oversized = true; }
        check(oversized, "romaji rejects oversized input before producing a partial conversion");
        var nullRejected = false;
        try { _ = RomajiReading.Convert(null!); }
        catch (ArgumentNullException) { nullRejected = true; }
        check(nullRejected, "romaji rejects null input explicitly");
        return Task.CompletedTask;
    }
}
