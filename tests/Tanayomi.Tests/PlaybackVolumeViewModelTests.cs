using System.Collections.Concurrent;
using Avalonia.Controls;
using Avalonia.Threading;
using Tanayomi;

internal static class PlaybackVolumeViewModelTests
{
    // Runs in the existing headless UI stage. No native output is constructed.
    public static void Run(Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var fixture = new TestDirectory();
        var voices = Path.Combine(fixture.DirectoryPath, "voices");
        Task.Run(() => Voicebanks.LoadInstalledAsync(voices)).GetAwaiter().GetResult();
        var model = new MainViewModel(new SilentSpeech(), voices);
        Window? window = null;
        var finishFirstSave = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var restore = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            PumpUntil(() => model.PlaybackVolumePersistence.IsCompleted);
            var volume = new PlaybackVolume();
            var writes = new ConcurrentQueue<int>();
            var firstSaveStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            model.InitializePlaybackVolume(volume, _ => restore.Task, async (value, ct) =>
            {
                writes.Enqueue(value);
                if (writes.Count == 1)
                {
                    firstSaveStarted.TrySetResult();
                    await finishFirstSave.Task.WaitAsync(ct);
                }
            });
            var view = new MainView { DataContext = model };
            window = new Window { Width = 390, Height = 900, Content = view }; window.Show();
            Dispatcher.UIThread.RunJobs();
            var slider = view.FindControl<Slider>("PlaybackVolumeSlider")!;
            var label = view.FindControl<TextBlock>("PlaybackVolumeLabel")!;
            check(slider.Minimum == 0 && slider.Maximum == 400 && slider.IsSnapToTickEnabled && slider.Value == 100 && label.Text == "100%",
                "common volume UI starts at unchanged 100 percent with bounded integer steps at mobile width");
            slider.Value = 0; Dispatcher.UIThread.RunJobs();
            check(model.VolumePercent == 0 && volume.Percent == 0 && label.Text == "0%（消音）",
                "volume slider updates shared state and labels zero as mute");
            PumpUntil(() => firstSaveStarted.Task.IsCompleted);
            restore.TrySetResult(37);
            PumpUntil(() => model.VolumeSettingsReady);
            check(model.VolumePercent == 0 && volume.Percent == 0,
                "late settings restore never overrides a volume edited before loading completed");
            for (var i = 1; i <= 100; i++) model.VolumePercent = i;
            model.VolumePercent = 74;
            check(writes.Count == 1, "slider dragging never starts parallel persistence while a write is active");
            finishFirstSave.TrySetResult();
            PumpUntil(() => model.PlaybackVolumePersistence.IsCompleted);
            check(writes.ToArray().SequenceEqual(new[] { 0, 74 }) && model.VolumePercent == 74,
                "continuous slider changes retain only one pending latest value rather than an unbounded save queue");
        }
        finally
        {
            restore.TrySetResult(100); finishFirstSave.TrySetResult();
            window?.Close(); Dispose(model);
        }

        var store = new PlaybackVolumeStore(Path.Combine(fixture.DirectoryPath, "data"));
        Task.Run(() => store.SaveAsync(400)).GetAwaiter().GetResult();
        model = new MainViewModel(new SilentSpeech(), voices);
        try
        {
            PumpUntil(() => model.PlaybackVolumePersistence.IsCompleted);
            var volume = new PlaybackVolume();
            model.InitializePlaybackVolume(volume, ct => store.LoadAsync(ct), (value, ct) => store.SaveAsync(value, ct));
            PumpUntil(() => model.VolumeSettingsReady);
            check(model.VolumePercent == 400 && volume.Percent == 400, "persisted volume restores into shared playback state");
            model.VolumePercent = 250;
            PumpUntil(() => model.PlaybackVolumePersistence.IsCompleted);
            check(Task.Run(() => store.LoadAsync()).GetAwaiter().GetResult() == 250,
                "volume UI changes persist under the injected data directory");
            model.InitializePlaybackVolume(volume, _ => Task.FromException<int>(new IOException("test restore unavailable")),
                (_, _) => Task.FromException(new IOException("test storage unavailable")));
            PumpUntil(() => model.VolumeSettingsReady);
            check(model.VolumePercent == 250 && model.VolumeSettingsStatus.Contains("復元できません", StringComparison.Ordinal),
                "restore failure preserves the active session volume and displays a recovery message");
            model.VolumePercent = 17;
            PumpUntil(() => model.PlaybackVolumePersistence.IsCompleted && model.VolumeSettingsStatus.Contains("保存できません", StringComparison.Ordinal));
            check(model.VolumePercent == 17 && volume.Percent == 17,
                "save failure leaves the changed session volume usable rather than reverting it");
            string report = "";
            var copying = model.CopyDiagnosticsAsync(text => { report = text; return Task.CompletedTask; });
            PumpUntil(() => copying.IsCompleted);
            copying.GetAwaiter().GetResult();
            check(report.Contains("VOLUME001", StringComparison.Ordinal) && report.Contains("VOLUME002", StringComparison.Ordinal) &&
                report.Contains("operation=PlaybackVolumeSettings", StringComparison.Ordinal) &&
                !report.Contains("test storage unavailable", StringComparison.Ordinal),
                "volume persistence diagnostics distinguish settings context without leaking exception text");
        }
        finally { Dispose(model); }
    }

    private static void Dispose(MainViewModel model)
    {
        var operation = model.DisposeAsync().AsTask();
        PumpUntil(() => operation.IsCompleted);
        operation.GetAwaiter().GetResult();
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow >= deadline) throw new TimeoutException("Playback volume UI check timed out.");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class SilentSpeech : ISpeechOutput
    {
        public string Name => "volume UI test";
        public Task SpeakAsync(string text, CancellationToken cancellationToken) => throw new InvalidOperationException("Volume UI must not produce speech.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

