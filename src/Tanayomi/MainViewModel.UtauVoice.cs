namespace Tanayomi;

public sealed partial class MainViewModel
{
    private UtauVoiceTuning voiceTuning = UtauVoiceTuning.Default;
    private UtauVoiceTuningStore? voiceTuningStore;
    private readonly object voiceTuningGate = new();
    private Task voiceTuningLoading = Task.CompletedTask, voiceTuningSaving = Task.CompletedTask;
    private UtauVoiceTuning? pendingVoiceTuning;
    private bool voiceTuningWriting, voiceTuningEdited;

    public bool ShowUtauVoiceTuning => SelectedUtauMode?.Mode == UtauSynthesisMode.Simple &&
        selectedSpeech.Utau?.SimplePitchContourEnabled == true;
    public int UtauVoicePitch
    {
        get => voiceTuning.Pitch;
        set => ChangeVoiceTuning(new(Math.Clamp(value, UtauVoiceTuning.MinimumPitch, UtauVoiceTuning.MaximumPitch), voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath));
    }
    public int UtauVoiceRange
    {
        get => voiceTuning.Range;
        set => ChangeVoiceTuning(new(voiceTuning.Pitch, Math.Clamp(value, UtauVoiceTuning.MinimumRange, UtauVoiceTuning.MaximumRange), voiceTuning.Formant, voiceTuning.Breath));
    }
    public int UtauVoiceFormant
    {
        get => voiceTuning.Formant;
        set => ChangeVoiceTuning(new(voiceTuning.Pitch, voiceTuning.Range,
            Math.Clamp(value, UtauVoiceTuning.MinimumFormant, UtauVoiceTuning.MaximumFormant), voiceTuning.Breath));
    }
    public int UtauVoiceBreath
    {
        get => voiceTuning.Breath;
        set => ChangeVoiceTuning(new(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant,
            Math.Clamp(value, UtauVoiceTuning.MinimumBreath, UtauVoiceTuning.MaximumBreath)));
    }
    public string UtauVoiceFormantLabel => $"{UtauVoiceFormant:+0;-0;0}";
    public string UtauVoiceBreathLabel => $"{UtauVoiceBreath}%";
    public string UtauVoicePitchLabel => $"{UtauVoicePitch:+0;-0;0} 半音";
    public string UtauVoiceRangeLabel => $"{UtauVoiceRange}%";
    public void ResetUtauVoiceTuning() => ChangeVoiceTuning(UtauVoiceTuning.Default);

    private void PublishVoiceTuning(UtauVoiceTuning value)
    {
        voiceTuning = value;
        if (selectedSpeech.Utau is { } utau) utau.VoiceTuning = value;
        Changed(nameof(UtauVoicePitch)); Changed(nameof(UtauVoiceRange));
        Changed(nameof(UtauVoicePitchLabel)); Changed(nameof(UtauVoiceRangeLabel));
        Changed(nameof(UtauVoiceFormant)); Changed(nameof(UtauVoiceBreath));
        Changed(nameof(UtauVoiceFormantLabel)); Changed(nameof(UtauVoiceBreathLabel));
    }

    private void ChangeVoiceTuning(UtauVoiceTuning value)
    {
        if (disposed || voiceTuning == value) return;
        voiceTuningEdited = true;
        PublishVoiceTuning(value);
        lock (voiceTuningGate)
        {
            if (voiceTuningStore is not { } store) return;
            pendingVoiceTuning = value;
            if (voiceTuningWriting) return;
            voiceTuningWriting = true;
            voiceTuningSaving = Task.Run(() => SaveUtauVoiceTuningAsync(store));
        }
    }

    private async Task LoadUtauVoiceTuningAsync()
    {
        try
        {
            if (voiceTuningStore is not { } store) return;
            var value = await store.LoadAsync(lifetime.Token).ConfigureAwait(false);
            OnUi(() => { if (!voiceTuningEdited && !disposed) PublishVoiceTuning(value); }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { ReportTuningFailure(false, ex); }
    }

    private async Task SaveUtauVoiceTuningAsync(UtauVoiceTuningStore store)
    {
        while (true)
        {
            UtauVoiceTuning value;
            lock (voiceTuningGate)
            {
                if (pendingVoiceTuning is not { } pending) { voiceTuningWriting = false; return; }
                value = pending;
                pendingVoiceTuning = null;
            }
            // The file write is short and atomic. Let the last requested write finish on normal exit.
            try { await store.SaveAsync(value, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception ex) { ReportTuningFailure(true, ex); }
        }
    }

    private void ReportTuningFailure(bool writing, Exception ex) =>
        Post(new("設定", writing ? "声の調整を保存できません。今回の起動中は設定を使用します。" : "声の調整を読み込めません。既定値を使用します。", Diagnostic:
            DiagnosticEvent.Create(writing ? DiagnosticCode.TextSettingsWriteFailed : DiagnosticCode.TextSettingsReadFailed,
                DiagnosticOperation.TextSettings, DiagnosticRecovery.WithoutStorage, ex)));
}
