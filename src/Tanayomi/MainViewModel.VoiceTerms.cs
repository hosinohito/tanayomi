namespace Tanayomi;

public sealed partial class MainViewModel
{
    private bool voiceTermsOpen;

    public bool VoiceTermsOpen
    {
        get => voiceTermsOpen;
        set { if (voiceTermsOpen != value) { voiceTermsOpen = value; Changed(); } }
    }

    public string VoiceTermsText => SelectedOpenJtalkVoice?.TermsText ?? "";
    public bool HasVoiceTermsText => VoiceTermsText.Length != 0;
    public bool VoiceAcceptanceRequired => SelectedOpenJtalkVoice is { } voice && !CanUseVoice(voice);
    public string VoiceAcceptanceStatus => SelectedOpenJtalkVoice is { AcceptanceId.Length: > 0 } voice
        ? !CanUseVoice(voice) ? "この音声を使うには利用条件への同意が必要です。それまでは標準音声で読み上げます。"
        : lastSaved?.SupertonicTermsAcceptance == voice.AcceptanceId ? "利用条件に同意済みです。"
        : "同意を保存できませんでした。今回の起動中はこの音声を利用できます。"
        : "";

    private bool CanUseVoice(OpenJtalkVoiceChoice voice) => voice.AcceptanceId.Length == 0 ||
        StartupSession.IsTermsAcceptanceId(voice.AcceptanceId) && voice.TermsText.Length != 0 &&
        session.SupertonicTermsAcceptance == voice.AcceptanceId;

    private void RefreshVoiceTerms()
    {
        selectedSpeech.SetRandomBundledVoices(OpenJtalkVoices.Where(CanUseVoice).ToArray());
        var voice = SelectedOpenJtalkVoice;
        selectedSpeech.SetBundledVoice(voice, voice is null || CanUseVoice(voice));
        NotifyVoiceTerms();
    }

    private void NotifyVoiceTerms()
    {
        Changed(nameof(VoiceName));
        Changed(nameof(VoiceTermsText));
        Changed(nameof(HasVoiceTermsText));
        Changed(nameof(VoiceAcceptanceRequired));
        Changed(nameof(VoiceAcceptanceStatus));
    }

    private void AcceptVoiceTerms()
    {
        if (disposed || !VoiceTermsOpen || SelectedOpenJtalkVoice is not { } voice || CanUseVoice(voice) ||
            voice.TermsText.Length == 0 || !StartupSession.IsTermsAcceptanceId(voice.AcceptanceId)) return;
        session = session with { SupertonicTermsAcceptance = voice.AcceptanceId };
        SaveSession();
        RefreshVoiceTerms();
        if (UseOpenJtalk) StartSelectedPreparation();
    }
}
