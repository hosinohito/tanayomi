using Avalonia.Controls;
using Avalonia.Threading;
using Tanayomi;

internal static class DrawnOutModeTests
{
    public static async Task RunStoreAsync(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var store = new OpenVoiceDrawnOutStore(fixture.DirectoryPath);
        var path = Path.Combine(fixture.DirectoryPath, "settings", "openvoice-drawn-out.txt");
        check(!await store.LoadAsync() && !File.Exists(path), "drawn-out mode defaults off without creating files");
        foreach (var value in new[] { true, false, true })
        {
            await store.SaveAsync(value);
            check(await new OpenVoiceDrawnOutStore(fixture.DirectoryPath).LoadAsync() == value,
                "drawn-out preference survives a new settings instance");
        }
        foreach (var invalid in new[] { "", "true", "2", " 1", "1\n", "01" })
        {
            await File.WriteAllTextAsync(path, invalid);
            Exception? failure = null;
            try { await store.LoadAsync(); } catch (Exception error) { failure = error; }
            check(failure is InvalidDataException && await File.ReadAllTextAsync(path) == invalid,
                "malformed drawn-out setting is rejected without rewriting evidence");
        }
        await store.SaveAsync(true);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try { await store.SaveAsync(false, cancelled.Token); } catch (OperationCanceledException) { }
        check(await store.LoadAsync() && !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(),
            "cancelled drawn-out save preserves the previous preference and leaves no temporary file");
    }

    public static void RunUi(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var model = new MainViewModel(new SilentSpeech(), fixture.DirectoryPath, new SilentPcm(),
            utauPostprocessor: new ModeProbe());
        var restore = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            PumpUntil(() => model.DrawnOutModePersistence.IsCompleted);
            var writes = new List<bool>();
            model.InitializeDrawnOutMode(_ => restore.Task, (value, _) => { lock (writes) writes.Add(value); return Task.CompletedTask; });
            var view = new MainView { DataContext = model };
            var window = new Window { Content = view, Width = 390, Height = 900 }; window.Show();
            try
            {
                var toggle = view.FindControl<CheckBox>("DrawnOutModeToggle")!;
                Dispatcher.UIThread.RunJobs();
                check(!model.DrawnOutMode && toggle.IsChecked == false && !model.ShowDrawnOutMode,
                    "drawn-out checkbox defaults off and stays hidden for other synthesis modes");
                var openVoice = model.UtauModes.FirstOrDefault(x => x.Mode == UtauSynthesisMode.OpenVoice);
                check(openVoice is not null, "Windows mode provider exposes OpenVoice for its checkbox");
                model.SelectedUtauMode = openVoice;
                Dispatcher.UIThread.RunJobs();
                check(model.ShowDrawnOutMode && toggle.IsEffectivelyVisible, "drawn-out checkbox appears for OpenVoice");
                toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
                restore.TrySetResult(false);
                PumpUntil(() => model.DrawnOutSettingsReady && model.DrawnOutModePersistence.IsCompleted);
                check(model.DrawnOutMode && writes.Last() == true, "checkbox edit persists and wins over delayed restore");
                model.InitializeDrawnOutMode(_ => Task.FromResult(false), (_, _) => Task.FromException(new IOException("private test")));
                PumpUntil(() => model.DrawnOutSettingsReady);
                check(!model.DrawnOutMode, "saved off preference restores into the checkbox");
                model.DrawnOutMode = true;
                PumpUntil(() => model.DrawnOutModePersistence.IsCompleted && model.DrawnOutSettingsStatus.Contains("保存できません"));
                check(model.DrawnOutMode, "save failure preserves this session's drawn-out preference");
                model.InitializeDrawnOutMode(_ => Task.FromException<bool>(new IOException("private restore")), null);
                PumpUntil(() => model.DrawnOutSettingsReady);
                check(model.DrawnOutMode && model.DrawnOutSettingsStatus.Contains("復元できません"),
                    "restore failure preserves the session preference and reports recovery");

            }
            finally { window.Close(); }
        }
        finally
        {
            restore.TrySetResult(false);
            var dispose = model.DisposeAsync().AsTask(); PumpUntil(() => dispose.IsCompleted); dispose.GetAwaiter().GetResult();
        }
    }
    private static void PumpUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Drawn-out UI check timed out.");
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private sealed class ModeProbe : IUtauPcmPostprocessor, IUtauModeProvider
    {
        public IReadOnlyList<UtauModeChoice> Modes { get; } = [new(UtauSynthesisMode.Context, "Context"), new(UtauSynthesisMode.OpenVoice, "OpenVoice")];
        public IUtauPcmPostprocessor? Resolve(UtauSynthesisMode mode) => null;
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SilentSpeech : ISpeechOutput
    {
        public string Name => "drawn-out UI test";
        public Task SpeakAsync(string text, CancellationToken ct) => throw new InvalidOperationException("No UI speech expected.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class SilentPcm : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct) => throw new InvalidOperationException("No UI audio expected.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
