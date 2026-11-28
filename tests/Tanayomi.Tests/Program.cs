using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Tanayomi;

internal static class Program
{
    private static int passed;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length == 1 && args[0] == "--utau-mode-menu-check")
            {
                UtauModeMenuTests.RunAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU mode menu checks");
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--utau-lead-handoff-check")
            {
                UtauLeadHandoffTests.RunAsync(Check, args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU lead handoff checks");
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--utau-lexical-accent-check")
            {
                UtauLexicalAccentTests.RunAsync(Check, args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU lexical accent checks");
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--utau-overlap-envelope-check")
            {
                UtauOverlapEnvelopeTests.RunAsync(Check, args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU overlap envelope checks");
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--utau-continuous-pitch-check")
            {
                UtauContinuousPitchTests.RunAsync(Check, args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU continuous pitch checks");
                return 0;
            }
            if (args.Length is 1 or 2 && args[0] == "--utau-voiced-join-check")
            {
                UtauVoicedJoinTests.RunAsync(Check, args.Length == 2 ? args[1] : null).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU voiced join checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--utau-prefetch-trial-check")
            {
                UtauSimplePrefetchTests.RunAsync(Check, benchmark: true).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} UTAU prefetch trial checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--windows-volume-boost-check")
            {
                DesktopSpeechTests.RunWindowsVolumeBoostCheck(Check);
                if (passed == 0) throw new InvalidOperationException("Windows volume boost check produced no results.");
                Console.WriteLine($"PASS: {passed} Windows volume boost checks (no playback or audio files)");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--neural-reference-check")
            {
                NeuralOpenVoiceSessionTests.RunReferenceSnapshotAuditAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} neural reference snapshot checks");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--utau-devoicing-comparison")
            {
                UtauPronunciationComparison.RunDevoicingAsync(args[1]).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--utau-pronunciation-comparison")
            {
                UtauPronunciationComparison.RunAsync(args[1]).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--openvoice-long-phrase-diagnostic")
            {
                OpenVoiceLongPhraseDiagnostic.RunAsync(args[1]).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 2 && args[0] == "--openvoice-reference-comparison")
            {
                OpenVoiceReferenceComparison.RunAsync(args[1]).GetAwaiter().GetResult();
                return 0;
            }
            if ((args.Length == 3 || (args.Length == 5 && args[3] == "--expected-native-sha256")) && args[0] == "--skia-native-check")
                return SkiaNativeSmokeTests.RunNativeCheck(args[1], args[2], args.Length == 5 ? args[4] : null);
            if ((args.Length == 3 || (args.Length == 5 && args[3] == "--expected-native-sha256")) && args[0] == "--with-skia-native")
            {
                var result = SkiaNativeSmokeTests.RunNativeCheck(args[1], args[2], args.Length == 5 ? args[4] : null);
                if (result != 0) return result;
                args = [];
            }
            if (args.Length is >= 3 and <= 5 && (args[0] is "--neural-native-check" or "--neural-profile-check") &&
                args.Skip(3).All(value => value is "--previous-runtime" or "--runtime-benchmark" or "--runtime-lifetime" or "--runtime-cancel") &&
                args.Skip(3).Distinct(StringComparer.Ordinal).Count() == args.Length - 3)
            {
                NeuralOpenVoiceSessionTests.RunNative(args[1], args[2], Check, profile: args[0] == "--neural-profile-check",
                    previousRuntime: args.Contains("--previous-runtime", StringComparer.Ordinal),
                    runtimeBenchmark: args.Contains("--runtime-benchmark", StringComparer.Ordinal),
                    runtimeLifetime: args.Contains("--runtime-lifetime", StringComparer.Ordinal),
                    runtimeCancel: args.Contains("--runtime-cancel", StringComparer.Ordinal));
                Console.WriteLine($"PASS: {passed} neural native equivalence checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--test-wait-child")
            {
                File.WriteAllText(Path.Combine(Environment.CurrentDirectory, "started"), "ready");
                Thread.Sleep(TimeSpan.FromSeconds(30));
                return 0;
            }
            if (args.Length == 2 && args[0] == "--open-jtalk-plan-product-check")
            {
                OpenJtalkIntegrationTests.RunProductPlanCheckAsync(args[1], Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} Open JTalk plan product worker checks");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--open-jtalk-product-check")
            {
                OpenJtalkIntegrationTests.RunProductCheckAsync(args[1], Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} Open JTalk product worker checks");
                return 0;
            }
            if (args.Length is 2 or 3 && args[0] == "--bundled-speech-product-check")
            {
                BundledSpeechProductTests.RunAsync(args[1], Check, args.Length == 3 ? args[2] : "all").GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} bundled speech product checks");
                return 0;
            }
            if (args.Length > 0 && args[0] == "--open-jtalk-native-check")
                return OpenJtalkIntegrationTests.RunNativeCheck(args);
            if (args.Length == 2 && args[0] == "--open-jtalk-integration")
            {
                OpenJtalkIntegrationTests.RunAsync(args[1], Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} Open JTalk integration checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--utau-joins")
            {
                UtauJoinObservationTests.ObserveAsync().GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length > 0 && args[0] == "--world-pcm-worker")
            {
                AppPaths.ConfigureProcessStorage();
                return Tanayomi.Desktop.WorldPcmWorker.RunWorker(args);
            }
            if (args.Length == 1 && args[0] == "--world-lifter-audit")
            {
                WorldIntegrationTests.RunLifterAuditAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} isolated WORLD lifter checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--world-parallel-resource-audit")
            {
                WorldIntegrationTests.RunParallelResourceAuditAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} isolated WORLD resource checks");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--world-resource-audit-worker")
            {
                WorldIntegrationTests.RunF0AuditWorkerAsync(Check, args[1]).GetAwaiter().GetResult();
                return 0;
            }
            if (args.Length == 1 && args[0] == "--world-f0-audit-worker")
            {
                WorldIntegrationTests.RunF0AuditWorkerAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} isolated WORLD F0 checks");
                return 0;
            }
            if (args.Length == 1 && args[0] == "--world-integration")
            {
                WorldIntegrationTests.RunAsync(Check).GetAwaiter().GetResult();
                Console.WriteLine($"PASS: {passed} native integration checks");
                return 0;
            }
            if (args.Length == 2 && args[0] is "--utau-sample" or "--utau-sample-unpitched")
            {
                UtauTests.WriteSampleAsync(args[1], args[0] == "--utau-sample-unpitched").GetAwaiter().GetResult();
                return 0;
            }
            if (args.Contains("--render")) { Render(); return 0; }
            RunAsync().GetAwaiter().GetResult();
            Render(saveFrames: false);
            Console.WriteLine($"PASS: {passed} checks");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }

    private static async Task RunAsync()
    {
        using var temporary = new TestDirectory();
        await UtauDirectRendererTests.RunAsync(Check);
        await UtauDirectPlanTests.RunAsync(Check);
        await UtauSourceCandidatesTests.RunAsync(Check);
        await VoicebankMultisampleTests.RunAsync(Check);
        WorldNativePlatformTests.Run(Check);
        NeuralSpectrogramTests.Run(Check);
        NeuralOpenVoiceSessionTests.Run(Check);
        await NeuralPcmWorkerTests.RunAsync(Check);
        await UtauDirectPsolaTests.RunAsync(Check);
        await TwitchAuthTests.RunAsync(Check);
        await TwitchAccountTests.RunAsync(Check);
        await TwitchAnonymousTests.RunAsync(Check);
        YouTubeParsingTests.Run(Check);
        await LinuxCredentialStoreTests.RunAsync(Check);
        await LinuxSpeechTests.RunAsync(Check);
        await MobileLifecycleTests.RunAsync(Check);
        await DiagnosticsTests.RunAsync(Check);
        await ShutdownTests.RunAsync(Check);
        await SpeechTextTests.RunAsync(Check);
        await RomajiReadingTests.RunAsync(Check);
        await SpeechTextStoreTests.RunAsync(Check);
        await SpeechTextQueueTests.RunAsync(Check);
        await PlaybackVolumeTests.RunAsync(Check);
        await PlaybackVolumeStoreTests.RunAsync(Check);
        await DrawnOutModeTests.RunStoreAsync(Check);
        await DesktopOpenJtalkSpeechTests.RunAsync(Check);
        await SupertonicWorkerTests.RunAsync(Check);
        await VoicevoxTests.RunAsync(Check);
        await AivisPcmProtocolTests.RunAsync(Check);
        await DesktopBundledSpeechSelectionTests.RunAsync(Check);
        await DesktopOpenJtalkPlannerTests.RunAsync(Check);
        await DesktopSpeechTests.RunAsync(Check);
        await DesktopPcmOutputTests.RunAsync(Check);
        await UtauTests.RunAsync(Check);
        await UtauModeMenuTests.RunAsync(Check);
        await UtauSimplePrefetchTests.RunAsync(Check);
        await UtauVoiceTuningTests.RunAsync(Check);
        await UtauVoicedJoinTests.RunAsync(Check);
        await UtauContinuousPitchTests.RunAsync(Check);
        await UtauOverlapEnvelopeTests.RunAsync(Check, null);
        await UtauLexicalAccentTests.RunAsync(Check);
        await UtauPostprocessorTests.RunAsync(Check);
        await UtauContextTests.RunAsync(Check);
        await UtauCvvcSlicesTests.RunAsync(Check);
        await WaveformPitchTests.RunAsync(Check);
        await UtauUnusedPitchAnalysisTests.RunAsync(Check);
        await UtauJoinObservationTests.RunAsync(Check);
        await UtauTimingPlanTests.RunAsync(Check);
        await UtauLandmarkOverrideTests.RunAsync(Check);
        await UtauProsodyPlanTests.RunAsync(Check);
        UtauProsodyTransferTests.Run(Check);
        await WorldReferenceTests.RunAsync(Check);
        await WorldPostprocessorTests.RunAsync(Check);
        await WorldProsodyProtocolTests.RunAsync(Check);
        await WorldProsodyTests.RunAsync(Check);
        JapaneseReadingTests.Run(Check);
        var output = new FakeSpeech();
        await using (var queue = new SpeechQueue(output))
        {
            var message = ChatMessage.Manual("test") with { Platform = "Twitch", Channel = "123", Id = "same" };
            Check(queue.Enqueue(message), "first arrival accepted");
            Check(!queue.Enqueue(message with { Transport = "EventSub" }), "cross-transport duplicate rejected");
            await Until(() => output.Spoken.Count == 1);
        }
        var fallback = new FakeSpeech();
        await using (var queue = new SpeechQueue(new FakeSpeech((_, _) => throw new IOException()), fallback))
        {
            queue.Enqueue(ChatMessage.Manual("fallback"));
            await Until(() => fallback.Spoken.Contains("fallback"));
            Check(true, "fallback used after primary failure");
        }
        var failures = new FakeSpeech((text, _) => text == "bad" ? throw new IOException() : Task.CompletedTask);
        await using (var queue = new SpeechQueue(failures))
        {
            queue.Enqueue(ChatMessage.Manual("bad")); queue.Enqueue(ChatMessage.Manual("good"));
            await Until(() => failures.Spoken.Contains("good"));
            Check(true, "one failing message does not stop consumer");
        }
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new FakeSpeech(async (text, ct) => { if (text == "hold") { started.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); } });
        await using (var queue = new SpeechQueue(blocked, capacity: 2))
        {
            queue.Enqueue(ChatMessage.Manual("hold"));
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            queue.Enqueue(ChatMessage.Manual("old")); queue.Enqueue(ChatMessage.Manual("new1")); queue.Enqueue(ChatMessage.Manual("new2"));
            Check(queue.PendingCount == 2, "queue bounded while output is blocked");
            queue.Skip();
            await Until(() => blocked.Spoken.Contains("new2"));
            Check(!blocked.Spoken.Contains("old"), "oldest pending message discarded");
        }
        var stale = new FakeSpeech();
        var timed = new FakeSpeech(async (text, ct) => { if (text == "timeout") await Task.Delay(Timeout.Infinite, ct); });
        await using (var queue = new SpeechQueue(timed) { SpeechTimeout = TimeSpan.FromMilliseconds(50) })
        {
            queue.Enqueue(ChatMessage.Manual("timeout")); queue.Enqueue(ChatMessage.Manual("after timeout"));
            await Until(() => timed.Spoken.Contains("after timeout"));
            Check(!timed.Spoken.Contains("timeout"), "speech timeout releases subsequent message");
        }
        await using (var queue = new SpeechQueue(stale))
        {
            queue.Enqueue(ChatMessage.Manual("stale") with { ReceivedAt = Stopwatch.GetTimestamp() - 30 * Stopwatch.Frequency });
            queue.Enqueue(ChatMessage.Manual("fresh"));
            await Until(() => stale.Spoken.Contains("fresh"));
            Check(!stale.Spoken.Contains("stale"), "stale chat skipped");
        }
        Check(SpeechQueue.PrepareText("hello\nhttps://example.com abc") == "hello リンク abc", "URL and whitespace normalization");
        var emoji = SpeechQueue.PrepareText(string.Concat(Enumerable.Repeat("😀", 200)));
        Check(!emoji.Contains('\uFFFD') && emoji.EnumerateRunes().Count() == 185, "truncate without breaking surrogate pairs");
        var irc = ChatSources.ParseIrc("@id=msg-1;room-id=42;display-name=Name\\sA :login!login@login.tmi.twitch.tv PRIVMSG #channel :hello : world");
        Check(irc is { Id: "msg-1", Channel: "42", Author: "Name A", Text: "hello : world" }, "IRC tags and trailing content parsed");
        Check(ChatSources.ParseIrc("@id=msg :malformed") is null, "malformed IRC ignored");
        using var eventsub = JsonDocument.Parse("""{"metadata":{"message_type":"notification"},"payload":{"event":{"message_id":"msg-1","broadcaster_user_id":"42","chatter_user_name":"Name A","message":{"text":"hello : world"}}}}""");
        var eventMessage = TwitchEventSub.ParseNotification(eventsub.RootElement);
        Check(eventMessage?.Id == irc?.Id && eventMessage?.Channel == irc?.Channel, "EventSub and IRC share deduplication identifiers");
        using var youtube = JsonDocument.Parse("""{"items":[{"id":"one","snippet":{"displayMessage":"hi"},"authorDetails":{"displayName":"viewer"}}]}""");
        Check(ChatSources.ParseYouTube(youtube.RootElement, "chat", true).Count == 0, "initial YouTube history suppressed");
        Check(ChatSources.ParseYouTube(youtube.RootElement, "chat", false).Count == 1, "subsequent YouTube page accepted");
        var attempts = 0;
        await ChatSources.SuperviseAsync(_ => { attempts++; throw new SourceConfigurationException("auth"); }, _ => { }, CancellationToken.None);
        Check(attempts == 1, "authorization failures do not retry indefinitely");
        Check(Voicebanks.DefaultDirectory == Path.Combine(AppContext.BaseDirectory, "data", "voicebanks"), "desktop voicebanks stay beside executable");
        var testRoot = temporary.DirectoryPath;
        var banks = await Voicebanks.LoadInstalledAsync(testRoot);
        Check(banks.Count == 1 && banks[0].Name == "hosinohito" && banks[0].Entries.Count > 100 && banks[0].RejectedEntries == 0, "bundled voice uses ordinary importer and valid Shift-JIS oto.ini");
        var again = await Voicebanks.LoadInstalledAsync(testRoot);
        Check(again.Count == 1, "bundled voice is not duplicated or overwritten");
        using var zip = typeof(Voicebanks).Assembly.GetManifestResourceStream("Tanayomi.hosinohito.zip")!;
        var additional = await Voicebanks.ImportZipAsync(zip, testRoot);
        Check(additional.Directory != banks[0].Directory && additional.Entries.Count == banks[0].Entries.Count, "additional copy uses same parser without overwriting existing bank");
        using var malicious = new MemoryStream();
        using (var archive = new ZipArchive(malicious, ZipArchiveMode.Create, true))
        { using var writer = new StreamWriter(archive.CreateEntry("../escape.txt").Open()); writer.Write("x"); }
        malicious.Position = 0;
        try { await Voicebanks.ImportZipAsync(malicious, testRoot); throw new Exception("zip traversal accepted"); }
        catch (InvalidDataException) { Check(!File.Exists(Path.Combine(testRoot, "escape.txt")), "archive traversal rejected"); }
        Console.WriteLine($"Bundled voice: {banks[0].Entries.Count} entries; all referenced WAV files found.");
    }

    private static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); passed++; Console.WriteLine("PASS " + name); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    private static void Render(bool saveFrames = true)
    {
        using var temporary = new TestDirectory();
        // Finish extraction before starting the UI model's background bank scan.
        Voicebanks.LoadInstalledAsync(temporary.DirectoryPath).GetAwaiter().GetResult();
        AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).UseSkia().SetupWithoutStarting();
        OpenJtalkSelectionTests.Run(Check);
        if (saveFrames) OpenJtalkSelectionTests.RenderVoiceTermsPreview(temporary.DirectoryPath);
        SpeechTextViewModelTests.Run(Check);
        PlaybackVolumeViewModelTests.Run(Check);
        DrawnOutModeTests.RunUi(Check);
        StartupSessionTests.Run(Check);
        ShutdownTests.RunViewModels(Check);
        var standard = new FakeSpeech();
        var pcm = new RecordingPcm();
        var model = new MainViewModel(standard, temporary.DirectoryPath, pcm, utauPostprocessor: new Tanayomi.Desktop.DesktopUtauModes());
        Window? window = null;
        try
        {
            PumpUiUntil(() => model.SelectedVoicebank is not null);
            var view = new MainView { DataContext = model };
            window = new Window { Width = 1060, Height = 1100, Content = view };
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var toggle = view.FindControl<CheckBox>("EventSubToggle")!;
            var settings = view.FindControl<StackPanel>("EventSubSettings")!;
            Check(model.TwitchClientId == TwitchAuthentication.DefaultClientId && toggle.IsChecked == false, "client ID preset and IRC is the default route");
            Check(!model.UseEventSub && !settings.IsVisible, "EventSub settings hidden by default");
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Check(model.UseEventSub && settings.IsVisible, "EventSub settings reappear when checked");
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Check(!model.UseEventSub && !settings.IsVisible, "EventSub settings hide again when unchecked");
            var utau = view.FindControl<CheckBox>("UtauToggle")!;
            var modeSelector = view.FindControl<ComboBox>("UtauModeSelector")!;
            const int modeCount = 8;
            Check(model.UtauModes.Count == modeCount && model.UtauModes.Select(x => x.Mode).Distinct().Count() == modeCount,
                "desktop mode selector exposes eight distinct built-in algorithms");
            modeSelector.SelectedItem = model.UtauModes.Single(x => x.Mode == UtauSynthesisMode.Simple);
            Dispatcher.UIThread.RunJobs();
            Check(model.SelectedUtauMode?.Mode == UtauSynthesisMode.Simple, "UTAU synthesis dropdown switches the active mode through its UI binding");
            var tuningPanel = view.FindControl<Expander>("UtauVoiceTuningPanel")!;
            var pitchSlider = view.FindControl<Slider>("UtauPitchSlider")!;
            var rangeSlider = view.FindControl<Slider>("UtauRangeSlider")!;
            Check(tuningPanel.IsVisible == model.ShowUtauVoiceTuning, "voice tuning visibility follows the selected engine");
            if (tuningPanel.IsVisible)
            {
                tuningPanel.IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                pitchSlider.Value = 4;
                rangeSlider.Value = 150;
                var formantSlider = view.FindControl<Slider>("UtauFormantSlider")!;
                var breathSlider = view.FindControl<Slider>("UtauBreathSlider")!;
                formantSlider.Value = -45;
                breathSlider.Value = 60;
                Dispatcher.UIThread.RunJobs();
                Check(model.UtauVoiceFormant == -45 && model.UtauVoiceBreath == 60,
                    "g and B sliders update the view-model independently");
                Check(model.UtauVoicePitch == 4 && model.UtauVoiceRange == 150, "both voice tuning sliders update the view-model");
                view.FindControl<Button>("UtauVoiceReset")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Check(pitchSlider.Value == 0 && rangeSlider.Value == 100 && formantSlider.Value == 0 && breathSlider.Value == 0,
                    "voice tuning reset restores all four slider defaults");
                modeSelector.SelectedItem = model.UtauModes.Single(x => x.Mode == UtauSynthesisMode.PhaseVocoder);
                Dispatcher.UIThread.RunJobs();
                Check(!tuningPanel.IsVisible, "voice tuning panel is hidden for the other engine");
                modeSelector.SelectedItem = model.UtauModes.Single(x => x.Mode == UtauSynthesisMode.Simple);
                Dispatcher.UIThread.RunJobs();
            }
            utau.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Check(model.UseUtau && model.VoiceName.Contains("hosinohito"), "UTAU checkbox changes selected speech and displayed voice");
            model.TestText = "今日は配信のテストです";
            model.SpeakCommand.Execute(null);
            PumpUiUntil(() => pcm.Calls > 0 || standard.Spoken.Count > 0);
            Check(pcm.Calls == 1 && pcm.Samples > 4410 && standard.Spoken.IsEmpty, "UI speech command uses real bundled UTAU synthesis and PCM output");
            model.TestText = "\u9fff";
            model.SpeakCommand.Execute(null);
            PumpUiUntil(() => standard.Spoken.Count > 0);
            Check(standard.Spoken.Count == 1 && pcm.Calls == 1, "unsupported UTAU utterance falls back to standard speech without partial playback");
            utau.IsChecked = false;
            model.TestText = "標準音声に戻す";
            model.SpeakCommand.Execute(null);
            PumpUiUntil(() => standard.Spoken.Count == 2);
            Check(!model.UseUtau && pcm.Calls == 1 && model.VoiceName == standard.Name, "unchecking UTAU restores standard speech on next utterance");
            model.TwitchToken = "http://localhost/#access_token=FAKE%0DSECRET";
            model.ConnectTwitchCommand.Execute(null);
            PumpUiUntil(() => model.TwitchStatus != "未接続");
            Check(model.TwitchStatus.Contains("トークン") && !model.Activities.Any(a => a.Contains("FAKE") || a.Contains("SECRET")),
                "manual malformed token is rejected without exposing secret or overwriting reason");
            model.TwitchToken = "";
            model.DisconnectTwitchCommand.Execute(null);
            PumpUiUntil(() => model.TwitchStatus == "切断しました");
            if (saveFrames)
            {
                model.SelectedUtauMode = model.UtauModes.Single(x => x.Mode == UtauSynthesisMode.PhaseVocoder);
                view.FindControl<Expander>("TextSettings")!.IsExpanded = true;
                window.Height = 1800;
                Dispatcher.UIThread.RunJobs();
                using var frame = window.CaptureRenderedFrame() ?? throw new Exception("No rendered frame");
                Directory.CreateDirectory(".local/previews");
                frame.Save(".local/previews/desktop.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
                window.Width = 390; window.Height = 2400;
                Dispatcher.UIThread.RunJobs();
                using var mobile = window.CaptureRenderedFrame() ?? throw new Exception("No mobile frame");
                mobile.Save(".local/previews/mobile-layout.png", new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
            }
        }
        finally
        {
            try { window?.Close(); }
            finally { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        if (saveFrames) Console.WriteLine("Rendered desktop and narrow layout previews; these are not mobile device tests.");
    }

    private static void PumpUiUntil(Func<bool> condition)
    {
        if (condition()) return;
        using var pump = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var poll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        poll.Tick += (_, _) => { if (condition()) pump.Cancel(); };
        poll.Start();
        Dispatcher.UIThread.MainLoop(pump.Token);
        poll.Stop();
        if (!condition()) throw new TimeoutException("UI condition did not complete.");
    }

    private sealed class RecordingPcm : IPcmOutput
    {
        public int Calls;
        public int Samples;
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Samples = samples.Length; Interlocked.Increment(ref Calls); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakeSpeech(Func<string, CancellationToken, Task>? behavior = null) : ISpeechOutput
    {
        public string Name => "テスト用音声";
        public ConcurrentQueue<string> Spoken { get; } = new();
        public async Task SpeakAsync(string text, CancellationToken cancellationToken)
        { if (behavior is not null) await behavior(text, cancellationToken); Spoken.Enqueue(text); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}





