using System.Collections.Concurrent;
using Avalonia.Threading;
using Tanayomi;

public static class SpeechTextViewModelTests
{
    // Called on the UI thread after the runner initializes Avalonia headless.
    public static void Run(Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        using var temporary = new TestDirectory();
        var voiceRoot = Path.Combine(temporary.DirectoryPath, "voices");
        // Import is file-only work. Start it without the UI synchronization
        // context before synchronously waiting in this headless UI entry point.
        Task.Run(() => Voicebanks.LoadInstalledAsync(voiceRoot)).GetAwaiter().GetResult();
        var dataRoot = Path.Combine(temporary.DirectoryPath, "data");
        var settingsPath = Path.Combine(dataRoot, "settings", "speech-text.json");
        byte[] saved;

        var output = new Probe();
        var model = Create(output, voiceRoot, dataRoot);
        try
        {
            PumpUntil(() => model.TextSettingsReady);
            check(!model.SuppressRepeatedMessages, "repeat suppression is opt-in for new settings");
            check(!model.OmitUrls, "URL omission is opt-in for new settings");
            check(!model.ReadAuthorNames && model.NameRulesText.Length == 0 && model.ReplacementRulesText.Length == 0 &&
                model.MutedRulesText.Length == 0, "new text-settings view model starts with names off and empty rules");
            model.ReadAuthorNames = true;
            model.NamesOnChange = true;
            model.BodyLimitText = "12";
            model.RomajiNames = true;
            model.RomajiBody = true;
            model.SuppressRepeatedMessages = true;
            model.OmitUrls = true;
            model.SelectedAuthor = new("Twitch", "42", "表示名");
            model.SelectedAuthorReading = "あき";
            model.AddAuthorReading();
            model.SelectedAuthor = new("YouTube", "channel-id", "別表示名");
            model.AddMutedAuthor();
            model.ReplacementRulesText = "word=読み\nリンク=URL";
            Apply(model);
            check(model.TextSettingsStatus.Contains("保存しました", StringComparison.Ordinal) && File.Exists(settingsPath),
                "view-model apply validates and saves text settings into the injected data directory");
            check(model.NameRulesText == "Twitch:42=あき" && model.MutedRulesText == "YouTube:channel-id",
                "author helper controls serialize stable service and ID rather than display names");
            check(Speak(model, output, "word https://example.test/a") == "読み",
                "actual manual speech command uses applied body rules without attaching an author name");
            check(Speak(model, output, "konnichiha") == "こんにちは", "view-model body romaji option reaches the speech queue");
            saved = File.ReadAllBytes(settingsPath);

            model.ReplacementRulesText = "word=適用しない\n=不正なキー";
            Apply(model);
            check(model.TextSettingsReady && model.TextSettingsStatus.Contains("以前の設定", StringComparison.Ordinal) &&
                File.ReadAllBytes(settingsPath).SequenceEqual(saved),
                "invalid dictionary edit preserves the previous saved settings and re-enables editing");
            check(Speak(model, output, "word") == "読み", "invalid dictionary edit preserves the active speech transformation");

            // Obstruct only the owned target path. A directory cannot be replaced
            // by a file on any platform, so this exercises a deterministic write
            // failure without modifying permissions or any user setting file.
            var backup = Path.Combine(dataRoot, "settings", "saved-test-copy.json");
            File.Move(settingsPath, backup);
            Directory.CreateDirectory(settingsPath);
            try
            {
                model.ReplacementRulesText = "word=未保存";
                Apply(model);
                check(model.TextSettingsStatus.Contains("保存できません", StringComparison.Ordinal) &&
                    File.ReadAllBytes(backup).SequenceEqual(saved),
                    "persistence failure reports recovery and keeps the previous saved contents intact");
                check(Speak(model, output, "word") == "読み", "persistence failure never publishes the unsaved speech rules");
                check(!Directory.EnumerateFiles(Path.GetDirectoryName(settingsPath)!, "speech-text-*.tmp").Any(),
                    "failed settings save cleans its temporary file");
            }
            finally
            {
                // Nonrecursive deletion of the exact empty directory created above.
                Directory.Delete(settingsPath);
                File.Move(backup, settingsPath);
            }
        }
        finally { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var restartedOutput = new Probe();
        var restarted = Create(restartedOutput, voiceRoot, dataRoot);
        try
        {
            PumpUntil(() => restarted.TextSettingsReady);
            check(restarted.NamesOnChange && restarted.BodyLimitText == "12", "restart restores speaker-change mode and optional body limit");
            restarted.BodyLimitText = "181";
            Apply(restarted);
            check(restarted.TextSettingsStatus.Contains("1～180") && File.ReadAllBytes(settingsPath).SequenceEqual(saved),
                "invalid body limit preserves the last saved settings");
            check(restarted.SuppressRepeatedMessages, "restart restores the selected repeat suppression option");
            check(restarted.OmitUrls, "restart restores the selected URL omission option");
            check(restarted.ReadAuthorNames && restarted.NameRulesText == "Twitch:42=あき" &&
                restarted.MutedRulesText == "YouTube:channel-id" && restarted.ReplacementRulesText == "word=読み\nリンク=URL",
                "a new view model reloads the last successful name, dictionary and mute settings");
            check(Speak(restarted, restartedOutput, "word https://example.test/a") == "読み",
                "restarted view-model speech applies persisted rules before any new save");
            check(restarted.RomajiNames && restarted.RomajiBody && Speak(restarted, restartedOutput, "konnichiha") == "こんにちは",
                "restarted view model restores both romaji choices and their speech effect");
            check(File.ReadAllBytes(settingsPath).SequenceEqual(saved), "restart does not rewrite the settings merely by loading them");
        }
        finally { restarted.DisposeAsync().AsTask().GetAwaiter().GetResult(); }

        var corrupt = "{ invalid settings"u8.ToArray();
        File.WriteAllBytes(settingsPath, corrupt);
        var recoveredOutput = new Probe();
        var recovered = Create(recoveredOutput, voiceRoot, dataRoot);
        try
        {
            PumpUntil(() => recovered.TextSettingsReady);
            check(recovered.TextSettingsStatus.Contains("読み込めません", StringComparison.Ordinal) && !recovered.ReadAuthorNames,
                "corrupt stored settings leave the view model usable with default text processing");
            check(Speak(recovered, recoveredOutput, "word") == "word" && File.ReadAllBytes(settingsPath).SequenceEqual(corrupt),
                "corrupt settings neither apply stale rules nor overwrite the original corrupt file");
        }
        finally { recovered.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static MainViewModel Create(Probe output, string voiceRoot, string dataRoot) =>
        new(output, voiceRoot, textSettingsStore: new SpeechTextStore(dataRoot));

    private static void Apply(MainViewModel model)
    {
        var operation = model.ApplyTextSettingsAsync();
        PumpUntil(() => operation.IsCompleted && model.TextSettingsReady);
        operation.GetAwaiter().GetResult();
    }

    private static string Speak(MainViewModel model, Probe output, string text)
    {
        model.TestText = text;
        model.SpeakCommand.Execute(null);
        PumpUntil(() => !output.Spoken.IsEmpty);
        if (!output.Spoken.TryDequeue(out var spoken)) throw new InvalidOperationException("No test speech was produced.");
        return spoken;
    }

    private static void PumpUntil(Func<bool> condition)
    {
        if (condition()) return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) => { if (condition()) timeout.Cancel(); };
        timer.Start();
        try { Dispatcher.UIThread.MainLoop(timeout.Token); }
        finally { timer.Stop(); }
        if (!condition()) throw new TimeoutException("Text-settings UI operation did not complete.");
    }

    private sealed class Probe : ISpeechOutput
    {
        public ConcurrentQueue<string> Spoken { get; } = new();
        public string Name => "text-settings test output";
        public Task SpeakAsync(string text, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Spoken.Enqueue(text); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
