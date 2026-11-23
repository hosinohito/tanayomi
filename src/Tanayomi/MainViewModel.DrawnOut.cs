namespace Tanayomi;

public sealed partial class MainViewModel
{
    private bool drawnOutMode;
    private readonly object drawnOutSaveGate = new();
    private Func<bool, CancellationToken, Task>? saveDrawnOutMode;
    private Task drawnOutLoading = Task.CompletedTask, drawnOutSaving = Task.CompletedTask;
    private bool? pendingDrawnOut;
    private int drawnOutGeneration;
    private bool drawnOutSaveRunning, drawnOutEdited, drawnOutSettingsReady;
    private string drawnOutSettingsStatus = "";

    public bool DrawnOutMode
    {
        get => drawnOutMode;
        set
        {
            if (disposed || value == drawnOutMode) return;
            drawnOutMode = value;
            if (selectedSpeech.Utau is { } utau) utau.DrawnOutMode = value;
            drawnOutEdited = true;
            Changed();
            lock (drawnOutSaveGate)
            {
                if (saveDrawnOutMode is not { } save) return;
                // At most one active write and one latest value, during repeated toggles.
                pendingDrawnOut = value;
                if (drawnOutSaveRunning) return;
                drawnOutSaveRunning = true;
                drawnOutSaving = Task.Run(() => SaveDrawnOutModeLoopAsync(save));
            }
        }
    }
    public bool DrawnOutSettingsReady { get => drawnOutSettingsReady; private set { drawnOutSettingsReady = value; Changed(); } }
    public string DrawnOutSettingsStatus { get => drawnOutSettingsStatus; private set { drawnOutSettingsStatus = value; Changed(); } }
    internal Task DrawnOutModePersistence
    {
        get { lock (drawnOutSaveGate) return Task.WhenAll(drawnOutLoading, drawnOutSaving); }
    }

    private void InitializeDrawnOutMode(OpenVoiceDrawnOutStore? store) =>
        InitializeDrawnOutMode(store is null ? null : ct => store.LoadAsync(ct),
            store is null ? null : (value, ct) => store.SaveAsync(value, ct));

    // Injection also makes delayed restore/write behavior testable without audio
    // or filesystem timing assumptions. Production uses the store overload.
    internal void InitializeDrawnOutMode(Func<CancellationToken, Task<bool>>? load,
        Func<bool, CancellationToken, Task>? save)
    {
        if (!drawnOutLoading.IsCompleted || !drawnOutSaving.IsCompleted)
            throw new InvalidOperationException("Drawn-out mode persistence is already active.");
        saveDrawnOutMode = save;
        drawnOutEdited = false;
        DrawnOutSettingsReady = false;
        var generation = ++drawnOutGeneration;
        Changed(nameof(DrawnOutMode));
        drawnOutLoading = LoadDrawnOutModeAsync(load, generation);
    }

    private async Task LoadDrawnOutModeAsync(Func<CancellationToken, Task<bool>>? load, int generation)
    {
        try
        {
            if (load is null) return;
            var restored = await load(lifetime.Token).ConfigureAwait(false);
            OnUi(() =>
            {
                if (generation != drawnOutGeneration || drawnOutEdited || disposed) return;
                drawnOutMode = restored;
                if (selectedSpeech.Utau is { } utau) utau.DrawnOutMode = restored;
                Changed(nameof(DrawnOutMode));
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Record(DiagnosticCode.DrawnOutModeReadFailed, DiagnosticOperation.DrawnOutModeSettings, DiagnosticRecovery.WithoutStorage, error);
            OnUi(() => { if (generation == drawnOutGeneration) DrawnOutSettingsStatus = "間延びモード設定を復元できません。今回の設定で続けます。"; }, lifetime.Token);
        }
        finally { OnUi(() => { if (generation == drawnOutGeneration) DrawnOutSettingsReady = true; }, lifetime.Token); }
    }

    private async Task SaveDrawnOutModeLoopAsync(Func<bool, CancellationToken, Task> save)
    {
        while (true)
        {
            bool value;
            lock (drawnOutSaveGate)
            {
                if (pendingDrawnOut is null) { drawnOutSaveRunning = false; return; }
                value = pendingDrawnOut.Value; pendingDrawnOut = null;
            }
            try
            {
                await save(value, lifetime.Token).ConfigureAwait(false);
                OnUi(() => DrawnOutSettingsStatus = "", lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                lock (drawnOutSaveGate) { pendingDrawnOut = null; drawnOutSaveRunning = false; }
                return;
            }
            catch (Exception error)
            {
                Record(DiagnosticCode.DrawnOutModeWriteFailed, DiagnosticOperation.DrawnOutModeSettings, DiagnosticRecovery.WithoutStorage, error);
                OnUi(() => DrawnOutSettingsStatus = "間延びモードを保存できません。今回の起動中は設定したモードを使います。", lifetime.Token);
            }
        }
    }
}

