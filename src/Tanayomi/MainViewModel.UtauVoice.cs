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
        set => ChangeVoiceTuning(new UtauVoiceTuning(Math.Clamp(value, UtauVoiceTuning.MinimumPitch, UtauVoiceTuning.MaximumPitch), voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath, voiceTuning.Curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public int UtauVoiceRange
    {
        get => voiceTuning.Range;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, Math.Clamp(value, UtauVoiceTuning.MinimumRange, UtauVoiceTuning.MaximumRange), voiceTuning.Formant, voiceTuning.Breath, voiceTuning.Curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public int UtauVoiceFormant
    {
        get => voiceTuning.Formant;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range,
            Math.Clamp(value, UtauVoiceTuning.MinimumFormant, UtauVoiceTuning.MaximumFormant), voiceTuning.Breath, voiceTuning.Curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public int UtauVoiceBreath
    {
        get => voiceTuning.Breath;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant,
            Math.Clamp(value, UtauVoiceTuning.MinimumBreath, UtauVoiceTuning.MaximumBreath), voiceTuning.Curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public double UtauCurveStart
    {
        get => voiceTuning.Curve.Start * 0.5;
        set { if (double.IsFinite(value)) ChangePitchCurve(voiceTuning.Curve with { Start = CurveStep(value) }); }
    }
    public string UtauCurveStartLabel => CurveLabel(UtauCurveStart);
    public double UtauCurveEarly
    {
        get => voiceTuning.Curve.Early * 0.5;
        set { if (double.IsFinite(value)) ChangePitchCurve(voiceTuning.Curve with { Early = CurveStep(value) }); }
    }
    public string UtauCurveEarlyLabel => CurveLabel(UtauCurveEarly);
    public double UtauCurveMiddle
    {
        get => voiceTuning.Curve.Middle * 0.5;
        set { if (double.IsFinite(value)) ChangePitchCurve(voiceTuning.Curve with { Middle = CurveStep(value) }); }
    }
    public string UtauCurveMiddleLabel => CurveLabel(UtauCurveMiddle);
    public double UtauCurveLate
    {
        get => voiceTuning.Curve.Late * 0.5;
        set { if (double.IsFinite(value)) ChangePitchCurve(voiceTuning.Curve with { Late = CurveStep(value) }); }
    }
    public string UtauCurveLateLabel => CurveLabel(UtauCurveLate);
    public double UtauCurveEnd
    {
        get => voiceTuning.Curve.End * 0.5;
        set { if (double.IsFinite(value)) ChangePitchCurve(voiceTuning.Curve with { End = CurveStep(value) }); }
    }
    public string UtauCurveEndLabel => CurveLabel(UtauCurveEnd);
    private static int CurveStep(double value) => (int)Math.Round(Math.Clamp(value, -3, 3) * 2);
    private static string CurveLabel(double value) => $"{value:+0.#;-0.#;0} 半音";
    private void ChangePitchCurve(UtauPitchCurve curve) =>
        ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath, curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    public string UtauVoiceFormantLabel => $"{UtauVoiceFormant:+0;-0;0}";
    public string UtauVoiceBreathLabel => $"{UtauVoiceBreath}%";
    public string UtauVoicePitchLabel => $"{UtauVoicePitch:+0;-0;0} 半音";
    public string UtauVoiceRangeLabel => $"{UtauVoiceRange}%";
    public int UtauVoiceBoundaryRange
    {
        get => voiceTuning.BoundaryRange;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath,
            voiceTuning.Curve, Math.Clamp(value, UtauVoiceTuning.MinimumRange, UtauVoiceTuning.MaximumRange), voiceTuning.CurveEnabled, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public string UtauVoiceBoundaryRangeLabel => $"{UtauVoiceBoundaryRange}%";
    public bool UtauVoiceCurveEnabled
    {
        get => voiceTuning.CurveEnabled;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath,
            voiceTuning.Curve, voiceTuning.BoundaryRange, value, voiceTuning.LevelJoins, voiceTuning.MaximumHeadCut));
    }
    public bool UtauLevelJoins
    {
        get => voiceTuning.LevelJoins;
        set => ChangeVoiceTuning(voiceTuning with { LevelJoins = value });
    }

    public int UtauMaximumHeadCut
    {
        get => voiceTuning.MaximumHeadCut;
        set => ChangeVoiceTuning(new UtauVoiceTuning(voiceTuning.Pitch, voiceTuning.Range, voiceTuning.Formant, voiceTuning.Breath,
            voiceTuning.Curve, voiceTuning.BoundaryRange, voiceTuning.CurveEnabled, voiceTuning.LevelJoins, Math.Clamp(value, 0, 50)));
    }
    public string UtauMaximumHeadCutLabel => $"{UtauMaximumHeadCut}%";

    public void ResetUtauVoiceTuning() => ChangeVoiceTuning(UtauVoiceTuning.Default);

    private void PublishVoiceTuning(UtauVoiceTuning value)
    {
        voiceTuning = value;
        if (selectedSpeech.Utau is { } utau) utau.VoiceTuning = value;
        Changed(nameof(UtauVoicePitch)); Changed(nameof(UtauVoiceRange));
        Changed(nameof(UtauVoicePitchLabel)); Changed(nameof(UtauVoiceRangeLabel));
        Changed(nameof(UtauVoiceBoundaryRange)); Changed(nameof(UtauVoiceBoundaryRangeLabel));
        Changed(nameof(UtauVoiceFormant)); Changed(nameof(UtauVoiceBreath));
        Changed(nameof(UtauVoiceFormantLabel)); Changed(nameof(UtauVoiceBreathLabel));
        Changed(nameof(UtauVoiceCurveEnabled));
        Changed(nameof(UtauLevelJoins)); Changed(nameof(UtauMaximumHeadCut)); Changed(nameof(UtauMaximumHeadCutLabel));
        Changed(nameof(UtauCurveStart)); Changed(nameof(UtauCurveStartLabel));
        Changed(nameof(UtauCurveEarly)); Changed(nameof(UtauCurveEarlyLabel));
        Changed(nameof(UtauCurveMiddle)); Changed(nameof(UtauCurveMiddleLabel));
        Changed(nameof(UtauCurveLate)); Changed(nameof(UtauCurveLateLabel));
        Changed(nameof(UtauCurveEnd)); Changed(nameof(UtauCurveEndLabel));
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
