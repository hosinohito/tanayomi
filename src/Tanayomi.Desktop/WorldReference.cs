namespace Tanayomi.Desktop;

/// <summary>Deterministic, inaudible bank calibration. The initial phrase is the
/// accepted B/WORLD comparison input; an incomplete bank uses only its available
/// vowels. File/OTO/reading failures are never treated as missing aliases.</summary>
public static class WorldReference
{
    public const string ReviewText = "すーっと、しーっと、こんにちは。今日は配信を見に来てくれてありがとう。";

    public static Task<PcmAudio> CreateAsync(Voicebank bank, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bank);
        cancellationToken.ThrowIfCancellationRequested();
        var text = ReviewText;
        if (!UtauSpeech.HasRequiredAliases(text, bank, cancellationToken))
        {
            var vowels = new[] { "あ", "い", "う", "え", "お" }
                .Where(vowel => UtauSpeech.HasRequiredAliases(vowel + "ーー", bank, cancellationToken));
            text = string.Join("、", vowels.Select(vowel => vowel + "ーー"));
            if (text.Length == 0) throw new InvalidDataException("基準音声に利用できる母音が音源にありません。");
        }
        return UtauSpeech.SynthesizeReferenceAsync(text, bank, cancellationToken);
    }
}
