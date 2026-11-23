namespace Tanayomi;

public sealed partial class MainViewModel
{
    private UtauModeStore? utauModeStore;
    private Task modeLoading = Task.CompletedTask, modeSaving = Task.CompletedTask;
    private bool modeEdited;
    public IReadOnlyList<UtauModeChoice> UtauModes => selectedSpeech.Utau?.Modes ?? [];
    public bool ShowDrawnOutMode => SelectedUtauMode?.Mode == UtauSynthesisMode.OpenVoice;
    public UtauModeChoice? SelectedUtauMode
    {
        get => UtauModes.FirstOrDefault(x => x.Mode == selectedSpeech.Utau?.Mode);
        set
        {
            if (disposed || value is null || selectedSpeech.Utau is not { } utau || !UtauModes.Contains(value) || value.Mode == utau.Mode) return;
            utau.Mode = value.Mode; modeEdited = true;
            Changed(); Changed(nameof(ShowDrawnOutMode));
            if (UseUtau) StartSelectedPreparation();
            // Serialize small atomic saves; switching takes effect immediately.
            modeSaving = SaveUtauModeAsync(modeSaving, value.Mode);
        }
    }
    private async Task LoadUtauModeAsync()
    {
        try
        {
            if (utauModeStore is null) return;
            var mode = await utauModeStore.LoadAsync(lifetime.Token).ConfigureAwait(false);
            OnUi(() =>
            {
                if (!modeEdited && selectedSpeech.Utau is { } utau && UtauModes.Any(x => x.Mode == mode))
                {
                    utau.Mode = mode; Changed(nameof(SelectedUtauMode)); Changed(nameof(ShowDrawnOutMode));
                    if (UseUtau) StartSelectedPreparation();
                }
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Post(new("設定", "合成方式を復元できません。今回の選択は使用できます。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.TextSettingsReadFailed, DiagnosticOperation.TextSettings, DiagnosticRecovery.WithoutStorage, error))); }
    }
    private async Task SaveUtauModeAsync(Task previous, UtauSynthesisMode mode)
    {
        try { await previous.ConfigureAwait(false); if (utauModeStore is not null) await utauModeStore.SaveAsync(mode, lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) { Post(new("設定", "合成方式を保存できません。今回の起動中は選択を使用します。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.TextSettingsWriteFailed, DiagnosticOperation.TextSettings, DiagnosticRecovery.WithoutStorage, error))); }
    }
}
