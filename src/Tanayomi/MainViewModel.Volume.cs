namespace Tanayomi;

public sealed partial class MainViewModel
{
    private PlaybackVolume playbackVolume = new();
    private readonly object volumeSaveGate = new();
    private Func<int, CancellationToken, Task>? savePlaybackVolume;
    private Task volumeLoading = Task.CompletedTask, volumeSaving = Task.CompletedTask;
    private int pendingVolume = -1, volumeGeneration;
    private bool volumeSaveRunning, volumeEdited, volumeSettingsReady;
    private string volumeSettingsStatus = "";

    public int VolumePercent
    {
        get => playbackVolume.Percent;
        set
        {
            if (disposed || value == playbackVolume.Percent) return;
            playbackVolume.Percent = value;
            volumeEdited = true;
            Changed(); Changed(nameof(VolumeLabel));
            lock (volumeSaveGate)
            {
                if (savePlaybackVolume is not { } save) return;
                // At most one active write and one latest value, even while dragging.
                pendingVolume = value;
                if (volumeSaveRunning) return;
                volumeSaveRunning = true;
                volumeSaving = Task.Run(() => SavePlaybackVolumeLoopAsync(save));
            }
        }
    }
    public string VolumeLabel => VolumePercent == 0 ? "0%（消音）" : $"{VolumePercent}%";
    public bool VolumeSettingsReady { get => volumeSettingsReady; private set { volumeSettingsReady = value; Changed(); } }
    public string VolumeSettingsStatus { get => volumeSettingsStatus; private set { volumeSettingsStatus = value; Changed(); } }
    internal Task PlaybackVolumePersistence
    {
        get { lock (volumeSaveGate) return Task.WhenAll(volumeLoading, volumeSaving); }
    }

    private void InitializePlaybackVolume(PlaybackVolume volume, PlaybackVolumeStore? store) =>
        InitializePlaybackVolume(volume, store is null ? null : ct => store.LoadAsync(ct),
            store is null ? null : (value, ct) => store.SaveAsync(value, ct));

    // Injection also makes delayed restore/write behavior testable without audio
    // or filesystem timing assumptions. Production uses the store overload.
    internal void InitializePlaybackVolume(PlaybackVolume volume, Func<CancellationToken, Task<int>>? load,
        Func<int, CancellationToken, Task>? save)
    {
        ArgumentNullException.ThrowIfNull(volume);
        if (!volumeLoading.IsCompleted || !volumeSaving.IsCompleted)
            throw new InvalidOperationException("Playback volume persistence is already active.");
        playbackVolume = volume;
        savePlaybackVolume = save;
        volumeEdited = false;
        VolumeSettingsReady = false;
        var generation = ++volumeGeneration;
        Changed(nameof(VolumePercent)); Changed(nameof(VolumeLabel));
        volumeLoading = LoadPlaybackVolumeAsync(load, generation);
    }

    private async Task LoadPlaybackVolumeAsync(Func<CancellationToken, Task<int>>? load, int generation)
    {
        try
        {
            if (load is null) return;
            var restored = await load(lifetime.Token).ConfigureAwait(false);
            if (restored is < 0 or > 400) throw new InvalidDataException("読み上げ音量の設定が不正です。");
            OnUi(() =>
            {
                if (generation != volumeGeneration || volumeEdited || disposed) return;
                playbackVolume.Percent = restored;
                Changed(nameof(VolumePercent)); Changed(nameof(VolumeLabel));
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Record(DiagnosticCode.PlaybackVolumeReadFailed, DiagnosticOperation.PlaybackVolumeSettings, DiagnosticRecovery.WithoutStorage, error);
            OnUi(() => { if (generation == volumeGeneration) VolumeSettingsStatus = "音量設定を復元できません。今回の音量で続けます。"; }, lifetime.Token);
        }
        finally { OnUi(() => { if (generation == volumeGeneration) VolumeSettingsReady = true; }, lifetime.Token); }
    }

    private async Task SavePlaybackVolumeLoopAsync(Func<int, CancellationToken, Task> save)
    {
        while (true)
        {
            int value;
            lock (volumeSaveGate)
            {
                if (pendingVolume < 0) { volumeSaveRunning = false; return; }
                value = pendingVolume; pendingVolume = -1;
            }
            try
            {
                await save(value, lifetime.Token).ConfigureAwait(false);
                OnUi(() => VolumeSettingsStatus = "", lifetime.Token);
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested)
            {
                lock (volumeSaveGate) { pendingVolume = -1; volumeSaveRunning = false; }
                return;
            }
            catch (Exception error)
            {
                Record(DiagnosticCode.PlaybackVolumeWriteFailed, DiagnosticOperation.PlaybackVolumeSettings, DiagnosticRecovery.WithoutStorage, error);
                OnUi(() => VolumeSettingsStatus = "音量を保存できません。今回の起動中は設定した音量を使います。", lifetime.Token);
            }
        }
    }
}

