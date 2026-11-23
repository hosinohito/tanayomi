using System.Globalization;

namespace Tanayomi.Desktop;

internal sealed record VoicevoxVoice(uint StyleId, string Speaker, string Style, string ModelFile,
    string Credit, string Usage, string TermsUrl)
{
    internal string Id => "voicevox-" + StyleId.ToString(CultureInfo.InvariantCulture);
    internal string Name => $"VOICEVOX {Speaker}（{Style}）";
}

// Fixed VVM 0.16.4 speech-only allowlist. A model file can contain other styles.
internal static class VoicevoxCatalog
{
    private sealed record VoiceTerms(string Url, string Usage, string? Credit = null);
    private static readonly VoiceTerms Sss = new("https://zunko.jp/con_ongen_kiyaku.html",
        "政治・宗教活動、特定対象の非難・応援、情報商材、意図的な虚偽の拡散、指定業態等への利用は禁止。");
    private static readonly VoiceTerms Virvox = new("https://www.virvoxproject.com/voicevoxの利用規約",
        "犯罪助長、政治団体・宗教に関する禁止用途、機械学習、無許可の公衆送信用素材販売等に制限。");
    private static readonly VoiceTerms Nemo = new("https://voicevox.hiroshiba.jp/nemo/term/",
        "著しい信用毀損、権利侵害、犯罪助長、機械学習等は禁止。", "VOICEVOX Nemo");
    private static readonly VoiceTerms Hau = new("https://amehau.com/rules/amehare-hau-rule",
        "成人向け音声利用を控え、出力を用いたボイスチェンジャー等は禁止。");
    private static readonly VoiceTerms Himari = new("https://www.meimeihimari.com/terms-of-use",
        "公序良俗違反、第三者への損害、特定の思想・団体に関する用途等に制限。");
    private static readonly VoiceTerms Chibi = new("https://docs.google.com/presentation/d/1AcD8zXkfzKFf2ertHwWRwJuQXjNnijMxhz7AJzEkaI4/htmlpresent",
        "成人向け・キャラクター虐待、差別、犯罪、特定の政治・宗教等の主張に制限。");
    private static readonly VoiceTerms Sayo = new("https://316soramegu.wixsite.com/sayo-official/guideline",
        "誹謗中傷、権利侵害、著しいイメージ毀損、犯罪助長等は禁止。音声素材の配布は帰属条件を継承。");
    private static readonly VoiceTerms Nurse = new("https://www.krnr.top/rules",
        "機械学習、無許可の他者利用向け音声素材販売、AI声変換や生成画像・動画への声当て等は禁止。");
    private static readonly VoiceTerms Nana = new("https://nanahira.jp/haruka_nana/guideline.html",
        "誹謗中傷、権利侵害、特定思想・団体等に制限。歌唱の商用利用には事前連絡が必要。");
    private static readonly VoiceTerms Iltan = new("https://infiniteloop.co.jp/special/aieru-tan/terms/",
        "成人向け、政治・宗教・特定思想、権利侵害、公式誤認等は禁止。");
    private static readonly VoiceTerms Hanamaru = new("https://100hanamaru.wixsite.com/manbetsu-hanamaru/rule",
        "反社会的利用、極端な政治的賛否、他者への損害、公式の詐称等は禁止。");
    private static readonly VoiceTerms RaceNoae = new("https://yogataritobari.studio.site/#rules",
        "VOICEVOXの生成音声を対象。キャラクター、公式素材、関連二次創作には別条件が適用されます。");
    private static readonly VoiceTerms Tsumugi = new("https://tsumugi-official.studio.site/rule",
        "誹謗中傷、権利侵害、特定の思想・団体に関する用途、生成音声の二次配布や自作発言等は禁止。公式素材は別条件。");
    private static readonly VoiceTerms Ritsu = new("https://www.canon-voice.com/terms/",
        "VOICEVOXの利用条件とクレジット表記が適用されます。公式素材は別条件。");
    private static readonly VoiceTerms Kenzaki = new("https://frontier.creatia.cc/fanclubs/413/posts/4507",
        "犯罪、詐欺・脅迫、特定の思想や政治運動の勧誘、誤った医療情報の意図的な流布等は禁止。公式イラストの配布・販売は別条件。");
    private static readonly VoiceTerms Whitecul = new("https://github.com/VOICEVOX/voicevox_vvm/blob/0.16.4/README.md#whitecul",
        "クレジット省略や他者の公衆送信用の音声素材販売は要許可。犯罪助長、権利侵害、著しい信用毀損、学習用利用等は禁止。");
    private static readonly VoiceTerms Miko = new("https://voicevox35miko.studio.site/rule",
        "合成音声を含む公開作品には帰属表示。誹謗中傷、実在する特定の政治・宗教の支持・批判、犯罪助長、情報商材等は禁止。");
    private static readonly VoiceTerms Benizakura = new("https://commons.nicovideo.jp/works/nc296132",
        "第三者に損害を与える利用、公式と誤認させる表示、特定の思想・団体への勧誘等は禁止。", "VOICEVOX †聖騎士 紅桜†");
    private static readonly VoiceTerms Maron = new("https://aivoice.jp/character/maron/",
        "クレジット付きの通常トーク音声を対象。一部の法人スポンサー案件やキャラクター利用には個別相談の条件があります。");
    private static readonly VoiceTerms Lit = new("https://litmus9.com/#/voicebank#rules",
        "政治・宗教目的、機械学習、過激な成人向け作品の一般公開等は禁止。法人のキャラクター利用は別途許諾。", "VOICEVOX 離途");

    internal static IReadOnlyList<VoicevoxVoice> Voices { get; } = Array.AsReadOnly<VoicevoxVoice>([
        Voice(0, "四国めたん", "あまあま", "0.vvm", Sss),
        Voice(1, "ずんだもん", "あまあま", "0.vvm", Sss),
        Voice(2, "四国めたん", "ノーマル", "0.vvm", Sss),
        Voice(3, "ずんだもん", "ノーマル", "0.vvm", Sss),
        Voice(4, "四国めたん", "セクシー", "0.vvm", Sss),
        Voice(5, "ずんだもん", "セクシー", "0.vvm", Sss),
        Voice(6, "四国めたん", "ツンツン", "0.vvm", Sss),
        Voice(7, "ずんだもん", "ツンツン", "0.vvm", Sss),
        Voice(8, "春日部つむぎ", "ノーマル", "0.vvm", Tsumugi),
        Voice(9, "波音リツ", "ノーマル", "3.vvm", Ritsu),
        Voice(10, "雨晴はう", "ノーマル", "0.vvm", Hau),
        Voice(11, "玄野武宏", "ノーマル", "4.vvm", Virvox),
        Voice(12, "白上虎太郎", "ふつう", "9.vvm", Virvox),
        Voice(14, "冥鳴ひまり", "ノーマル", "1.vvm", Himari),
        Voice(15, "九州そら", "あまあま", "2.vvm", Sss),
        Voice(16, "九州そら", "ノーマル", "2.vvm", Sss),
        Voice(17, "九州そら", "セクシー", "2.vvm", Sss),
        Voice(18, "九州そら", "ツンツン", "2.vvm", Sss),
        Voice(19, "九州そら", "ささやき", "5.vvm", Sss),
        Voice(21, "剣崎雌雄", "ノーマル", "4.vvm", Kenzaki),
        Voice(22, "ずんだもん", "ささやき", "5.vvm", Sss),
        Voice(23, "WhiteCUL", "ノーマル", "8.vvm", Whitecul),
        Voice(24, "WhiteCUL", "たのしい", "8.vvm", Whitecul),
        Voice(25, "WhiteCUL", "かなしい", "8.vvm", Whitecul),
        Voice(26, "WhiteCUL", "びえーん", "8.vvm", Whitecul),
        Voice(32, "白上虎太郎", "わーい", "9.vvm", Virvox),
        Voice(33, "白上虎太郎", "びくびく", "9.vvm", Virvox),
        Voice(34, "白上虎太郎", "おこ", "9.vvm", Virvox),
        Voice(35, "白上虎太郎", "びえーん", "9.vvm", Virvox),
        Voice(36, "四国めたん", "ささやき", "5.vvm", Sss),
        Voice(37, "四国めたん", "ヒソヒソ", "5.vvm", Sss),
        Voice(38, "ずんだもん", "ヒソヒソ", "5.vvm", Sss),
        Voice(39, "玄野武宏", "喜び", "10.vvm", Virvox),
        Voice(40, "玄野武宏", "ツンギレ", "10.vvm", Virvox),
        Voice(41, "玄野武宏", "悲しみ", "10.vvm", Virvox),
        Voice(42, "ちび式じい", "ノーマル", "10.vvm", Chibi),
        Voice(43, "櫻歌ミコ", "ノーマル", "11.vvm", Miko),
        Voice(44, "櫻歌ミコ", "第二形態", "11.vvm", Miko),
        Voice(45, "櫻歌ミコ", "ロリ", "11.vvm", Miko),
        Voice(46, "小夜/SAYO", "ノーマル", "15.vvm", Sayo),
        Voice(47, "ナースロボ＿タイプＴ", "ノーマル", "11.vvm", Nurse),
        Voice(48, "ナースロボ＿タイプＴ", "楽々", "11.vvm", Nurse),
        Voice(49, "ナースロボ＿タイプＴ", "恐怖", "11.vvm", Nurse),
        Voice(50, "ナースロボ＿タイプＴ", "内緒話", "11.vvm", Nurse),
        Voice(51, "†聖騎士 紅桜†", "ノーマル", "12.vvm", Benizakura),
        Voice(52, "雀松朱司", "ノーマル", "12.vvm", Virvox),
        Voice(53, "麒ヶ島宗麟", "ノーマル", "12.vvm", Virvox),
        Voice(54, "春歌ナナ", "ノーマル", "13.vvm", Nana),
        Voice(61, "中国うさぎ", "ノーマル", "3.vvm", Sss),
        Voice(62, "中国うさぎ", "おどろき", "3.vvm", Sss),
        Voice(63, "中国うさぎ", "こわがり", "3.vvm", Sss),
        Voice(64, "中国うさぎ", "へろへろ", "3.vvm", Sss),
        Voice(65, "波音リツ", "クイーン", "3.vvm", Ritsu),
        Voice(67, "栗田まろん", "ノーマル", "14.vvm", Maron),
        Voice(68, "あいえるたん", "ノーマル", "14.vvm", Iltan),
        Voice(69, "満別花丸", "ノーマル", "14.vvm", Hanamaru),
        Voice(70, "満別花丸", "元気", "14.vvm", Hanamaru),
        Voice(71, "満別花丸", "ささやき", "14.vvm", Hanamaru),
        Voice(72, "満別花丸", "ぶりっ子", "14.vvm", Hanamaru),
        Voice(73, "満別花丸", "ボーイ", "14.vvm", Hanamaru),
        Voice(75, "ずんだもん", "ヘロヘロ", "15.vvm", Sss),
        Voice(76, "ずんだもん", "なみだめ", "15.vvm", Sss),
        Voice(94, "中部つるぎ", "ノーマル", "18.vvm", Sss),
        Voice(95, "中部つるぎ", "怒り", "18.vvm", Sss),
        Voice(96, "中部つるぎ", "ヒソヒソ", "18.vvm", Sss),
        Voice(97, "中部つるぎ", "おどおど", "18.vvm", Sss),
        Voice(98, "中部つるぎ", "絶望と敗北", "18.vvm", Sss),
        Voice(99, "離途", "ノーマル", "19.vvm", Lit),
        Voice(100, "黒沢冴白", "ノーマル", "19.vvm", Virvox),
        Voice(101, "離途", "シリアス", "19.vvm", Lit),
        Voice(107, "東北ずん子", "ノーマル", "21.vvm", Sss),
        Voice(108, "東北きりたん", "ノーマル", "21.vvm", Sss),
        Voice(109, "東北イタコ", "ノーマル", "21.vvm", Sss),
        Voice(113, "あんこもん", "ノーマル", "22.vvm", Sss),
        Voice(114, "あんこもん", "つよつよ", "22.vvm", Sss),
        Voice(115, "あんこもん", "よわよわ", "22.vvm", Sss),
        Voice(116, "あんこもん", "けだるげ", "22.vvm", Sss),
        Voice(117, "あんこもん", "ささやき", "23.vvm", Sss),
        Voice(118, "夜語トバリ", "ノーマル", "24.vvm", RaceNoae),
        Voice(119, "夜語トバリ", "明るい", "24.vvm", RaceNoae),
        Voice(120, "夜語トバリ", "哀しみ", "24.vvm", RaceNoae),
        Voice(121, "夜語トバリ", "呆れ", "24.vvm", RaceNoae),
        Voice(122, "暁記ミタマ", "ノーマル", "24.vvm", RaceNoae),
        Voice(123, "暁記ミタマ", "怒り", "24.vvm", RaceNoae),
        Voice(124, "暁記ミタマ", "哀しみ", "24.vvm", RaceNoae),
        Voice(125, "暁記ミタマ", "ささやき", "24.vvm", RaceNoae),
        Voice(10000, "男声2", "ノーマル", "n0.vvm", Nemo),
        Voice(10001, "男声1", "ノーマル", "n0.vvm", Nemo),
        Voice(10002, "男声3", "ノーマル", "n0.vvm", Nemo),
        Voice(10003, "女声4", "ノーマル", "n0.vvm", Nemo),
        Voice(10004, "女声3", "ノーマル", "n0.vvm", Nemo),
        Voice(10005, "女声1", "ノーマル", "n0.vvm", Nemo),
        Voice(10006, "女声6", "ノーマル", "n0.vvm", Nemo),
        Voice(10007, "女声2", "ノーマル", "n0.vvm", Nemo),
        Voice(10008, "女声5", "ノーマル", "n0.vvm", Nemo),
    ]);

    private static readonly Dictionary<uint, VoicevoxVoice> ByStyle = Voices.ToDictionary(voice => voice.StyleId);
    private static readonly Dictionary<string, VoicevoxVoice> ById = Voices.ToDictionary(voice => voice.Id, StringComparer.Ordinal);
    private static readonly Dictionary<string, string> ModelHashes = new(StringComparer.Ordinal)
    {
        ["0.vvm"] = "917E592CAE92A02FB7668BD9E168DF4E39C8AB2F5302C2BE71906C70EC4E2DF2",
        ["1.vvm"] = "8DF20815BE9A84A4B4723B9E778D2EF6A4DF277E4872FE9EFE29676A60E02774",
        ["2.vvm"] = "4C97FF029895A819CEE3AD431A77284D6AF7E097AC42FAA5AF1C87D2BC5A9B5C",
        ["3.vvm"] = "BE05BA9A196BC5ACC298DAA2C85DE1188F0795B46422581437E2B07071963E03",
        ["4.vvm"] = "DA5AAAD87D5B7EA5B346DEF2E610CC63E64A90088BB0FC2D27FB63A622113953",
        ["5.vvm"] = "487856083C347E298B0B9BDABFC278DC14240C9365A7A0E4870AEA965CE7B4D1",
        ["8.vvm"] = "BDE9CB507F62277A176CC2C7182DB686294028D2ADFF4D3A49C39DD1498F2FED",
        ["9.vvm"] = "5145551D31ED4FAB14D7921AAFDD2DB5C9E90DA40D5B59F9418EE435C400C2FA",
        ["10.vvm"] = "B5F8A97C0B6BE2B8EAA6F2C1AF88B2E06F9072BB82DDDD4E1B3A05E9DE0C04D4",
        ["11.vvm"] = "46C209914812C13B1717331701D76491708448F90C7F8E87B5B286E5CA91341E",
        ["12.vvm"] = "5961B7F7EECEC1BE9EB38C0868898483D9582E490E9D9631DCA9EA5018BD0E70",
        ["13.vvm"] = "F083DD1C3B67091FBBF1839C97D0CF817DBFAE40DDC4FA8E88236CE6E2ABE55D",
        ["14.vvm"] = "BAAB280D80769F1D8FD5AEAD07137C5ED1B5E61ACC5F0E26DFF53439E907D085",
        ["15.vvm"] = "1CAA2A42417F6B9BBB1A3C8EC1370724D7E153862B4D12F8C317C4E37CEA786F",
        ["18.vvm"] = "FBD69EBF1775E5E44E39DB8580584601822686D0ABD0383DEE01DD3A0A5FFC9D",
        ["19.vvm"] = "D6C8096606D462A26C7E7B093E0EAB4452E52EFD42AF29E9CDC989E022EA9F0E",
        ["21.vvm"] = "DA1172DA295F89E9733CDE36468365F573133E1903A4FE9CD47B2BA4BDADBEAC",
        ["22.vvm"] = "4B7F2AB58A14F163C95B4896CCF5116AA8BBF4ADAF2B18632ACBB279CCA357C5",
        ["23.vvm"] = "A5862259A0A3173A15FE83B56817F6E66386B5059864889D69B0A1A0B43F281F",
        ["24.vvm"] = "7A80EAC3F27D33E45AC1C23534F9E5C5967A29F4D5EC28B898FC453871F1C341",
        ["n0.vvm"] = "E91E2E6ED5CFA6940FF55B61234BA89C631FCE3D0146C23685552E7F5D2FE437",
    };

    internal static VoicevoxVoice? Find(uint styleId) => ByStyle.GetValueOrDefault(styleId);
    internal static VoicevoxVoice? Find(string? id) => id is null ? null : ById.GetValueOrDefault(id);
    internal static string ModelSha256(string fileName) => ModelHashes[fileName];

    private static VoicevoxVoice Voice(uint id, string speaker, string style, string file, VoiceTerms terms) =>
        new(id, terms == Nemo ? "Nemo " + speaker : speaker, style, file,
            terms.Credit ?? "VOICEVOX:" + speaker, terms.Usage, terms.Url);
}
