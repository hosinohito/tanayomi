using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Tanayomi;
using Tanayomi.Desktop;

// Run on the existing headless UI thread. No native loader, real server,
// playback or persistent settings are involved.
public static class OpenJtalkSelectionTests
{
    private const string Secret = "private-open-jtalk-chat-token-path";
    public static void Run(Action<bool, string> check)
    {
        Dispatcher.UIThread.VerifyAccess();
        Factories(check);
        using var temporary = new TestDirectory();
        Task.Run(() => Voicebanks.LoadInstalledAsync(temporary.DirectoryPath)).GetAwaiter().GetResult();
        Selection(temporary.DirectoryPath, check);
        RandomSelection(temporary.DirectoryPath, check);
        VoiceTerms(temporary.DirectoryPath, check);
        Unavailable(temporary.DirectoryPath, check);
    }

    internal static void RenderVoiceTermsPreview(string voiceRoot)
    {
        Dispatcher.UIThread.VerifyAccess();
        var source = Path.GetFullPath(".local/vendor/supertonic/prepared-md-vc143/licenses/Supertonic-3-OpenRAIL-M.txt");
        var text = TermsProbe.Terms;
        if (File.Exists(source))
        {
            WorldComparison.RejectLinks(source);
            using var file = File.OpenRead(source);
            if (file.Length != 15007) throw new InvalidDataException("Unexpected Supertonic preview terms length.");
            var bytes = new byte[(int)file.Length];
            file.ReadExactly(bytes);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != SupertonicPcmWorker.TermsSha256)
                throw new InvalidDataException("Unexpected Supertonic preview terms checksum.");
            text = new UTF8Encoding(false, true).GetString(bytes);
        }
        var choice = new OpenJtalkVoiceChoice("supertonic3-0", "Supertonic 3 日本語 (1)", SupertonicSpeech.Credit,
            SupertonicSpeech.Usage, SupertonicSpeech.TermsUrl, SupertonicPcmWorker.TermsSha256, text);
        var order = new ConcurrentQueue<string>();
        var model = new MainViewModel(new SpeechProbe("OS標準音声", order), voiceRoot, openJtalkSpeech: new TermsProbe(order, choice));
        var view = new MainView { DataContext = model };
        var window = new Window { Width = 1060, Height = 1040, Content = view };
        try
        {
            var previewRoot = Path.GetFullPath(".local/previews");
            WorldComparison.RejectLinks(previewRoot);
            Directory.CreateDirectory(previewRoot);
            model.UseOpenJtalk = true;
            window.Show();
            Save("supertonic-terms-closed.png");
            view.FindControl<Expander>("VoiceTermsExpander")!.IsExpanded = true;
            Save("supertonic-terms-open.png", showTermsPanel: true);
            window.Width = 390;
            window.Height = 1100;
            Save("supertonic-terms-narrow.png", showTermsPanel: true);

            void Save(string name, bool showTermsPanel = false)
            {
                var destination = Path.Combine(previewRoot, name);
                WorldComparison.RejectLinks(destination);
                Dispatcher.UIThread.RunJobs();
                if (showTermsPanel)
                {
                    view.FindControl<StackPanel>("VoiceTermsPanel")!.BringIntoView();
                    Dispatcher.UIThread.RunJobs();
                }
                using var frame = window.CaptureRenderedFrame() ?? throw new IOException("Supertonic preview frame is unavailable.");
                frame.Save(destination, new PngBitmapEncoderOptions());
            }
        }
        finally
        {
            try { window.Close(); }
            finally
            {
                var shutdown = model.DisposeAsync().AsTask();
                PumpUntil(() => shutdown.IsCompleted);
                shutdown.GetAwaiter().GetResult();
            }
        }
    }

    private static void VoiceTerms(string voiceRoot, Action<bool, string> check)
    {
        var root = Path.Combine(voiceRoot, "voice-terms");
        var path = Path.Combine(root, "settings", "startup.json");
        var oldVersion = new string('B', 64);
        new StartupSession(Engine: "nit", NitVoice: "supertonic3-0", SupertonicTermsAcceptance: oldVersion).Write(path);
        var order = new ConcurrentQueue<string>();
        var standard = new SpeechProbe("standard", order);
        var restricted = new TermsProbe(order);
        var model = new MainViewModel(standard, voiceRoot, openJtalkSpeech: restricted, sessionDirectory: root);
        var view = new MainView { DataContext = model };
        var window = new Window { Width = 900, Height = 1000, Content = view };
        window.Show(); Dispatcher.UIThread.RunJobs();
        try
        {
            var terms = view.FindControl<Expander>("VoiceTermsExpander")!;
            var fullText = view.FindControl<SelectableTextBlock>("VoiceTermsFullText")!;
            var accept = view.FindControl<Button>("AcceptVoiceTermsButton")!;
            check(model.UseOpenJtalk && model.VoiceAcceptanceRequired && model.VoiceName == standard.Name && !terms.IsExpanded,
                "outdated acceptance restores the choice and uses standard speech until renewed");
            model.AcceptVoiceTermsCommand.Execute(null);
            model.TestText = "規約同意前"; model.SpeakCommand.Execute(null);
            PumpUntil(() => standard.Spoken.Count == 1);
            check(restricted.Preparations == 0 && restricted.Spoken.IsEmpty &&
                StartupSession.Read(path).SupertonicTermsAcceptance == oldVersion,
                "a closed terms panel cannot accept, prepare or synthesize the restricted voice");
            terms.IsExpanded = true; Dispatcher.UIThread.RunJobs();
            check(model.VoiceTermsOpen && fullText.Text == TermsProbe.Terms && accept.IsVisible &&
                ReferenceEquals(accept.Command, model.AcceptVoiceTermsCommand),
                "terms panel displays the complete terms and binds the explicit acceptance action");
            accept.Command!.Execute(null);
            PumpUntil(() => restricted.Preparations == 1);
            model.TestText = "規約同意後"; model.SpeakCommand.Execute(null);
            PumpUntil(() => restricted.Spoken.Count == 1);
            check(!model.VoiceAcceptanceRequired && !accept.IsVisible &&
                StartupSession.Read(path).SupertonicTermsAcceptance == TermsProbe.Version,
                "accepting the exact displayed terms saves the current ID and enables the selected engine");
        }
        finally { Stop(model); window.Close(); }

        restricted = new TermsProbe(order);
        model = new MainViewModel(new SpeechProbe("standard", order), voiceRoot, openJtalkSpeech: restricted, sessionDirectory: root);
        try
        {
            PumpUntil(() => restricted.Preparations == 1);
            check(model.UseOpenJtalk && !model.VoiceAcceptanceRequired && model.VoiceName == restricted.Name,
                "matching acceptance permits preparation on restart without asking again");
        }
        finally { Stop(model); }

        var blocked = Path.Combine(voiceRoot, "voice-terms-blocked");
        File.WriteAllText(blocked, Secret);
        restricted = new TermsProbe(order);
        model = new MainViewModel(new SpeechProbe("standard", order), voiceRoot, openJtalkSpeech: restricted, sessionDirectory: blocked);
        try
        {
            model.UseOpenJtalk = true;
            model.VoiceTermsOpen = true;
            model.AcceptVoiceTermsCommand.Execute(null);
            PumpUntil(() => restricted.Preparations == 1);
            check(!model.VoiceAcceptanceRequired && model.VoiceAcceptanceStatus.Contains("保存できません", StringComparison.Ordinal) &&
                File.ReadAllText(blocked) == Secret && !CaptureReport(model).Contains(Secret, StringComparison.Ordinal),
                "failed acceptance storage preserves existing data and allows the voice only for this session");
        }
        finally { Stop(model); }

        static void Stop(MainViewModel model)
        {
            var shutdown = model.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        }
    }

    private static void Factories(Action<bool, string> check)
    {
        var oldSpeech = App.CreateSpeech; var oldPcm = App.CreatePcm;
        var oldStore = App.CreateCredentialStore; var oldPost = App.CreateUtauPostprocessor;
        var oldOpen = App.CreateOpenJtalkSpeech;
        var owned = new List<IAsyncDisposable>();
        try
        {
            var order = new ConcurrentQueue<string>();
            var standard = new SpeechProbe("standard", order); var pcm = new PcmProbe(order);
            owned.Add(standard); owned.Add(pcm);
            App.CreateSpeech = () => standard; App.CreatePcm = () => pcm;
            App.CreateCredentialStore = null; App.CreateUtauPostprocessor = null;
            IPcmOutput? borrowedOutput = null;
            App.CreateOpenJtalkSpeech = borrowed => { borrowedOutput = borrowed; throw new IOException(Secret); };
            var failed = App.InitializeServices();
            check(ReferenceEquals(borrowedOutput, failed.Pcm) && failed.Pcm is VolumePcmOutput adjusted && ReferenceEquals(adjusted.Volume, failed.Volume) && ReferenceEquals(failed.Speech, standard) &&
                failed.OpenJtalk is null && failed.Diagnostics.Count == 1 &&
                failed.Diagnostics[0].Code == DiagnosticCode.OpenJtalkSpeechInitializationFailed,
                "NIT factory failure is isolated while standard speech and its borrowed PCM remain available");
            var diagnostics = new DiagnosticBuffer(); foreach (var item in failed.Diagnostics) diagnostics.Record(item);
            var report = diagnostics.CreateReport();
            check(report.Contains("VOICE006", StringComparison.Ordinal) &&
                report.Contains("operation=OpenJtalkInitialization", StringComparison.Ordinal) && !report.Contains(Secret, StringComparison.Ordinal),
                "NIT factory diagnostic has its stable ID and safe operation without factory secrets");
            var nit = new SpeechProbe("nit", order); owned.Add(nit);
            App.CreateOpenJtalkSpeech = borrowed => { borrowedOutput = borrowed; return nit; };
            var success = App.InitializeServices();
            check(ReferenceEquals(success.OpenJtalk, nit) && ReferenceEquals(borrowedOutput, success.Pcm) && success.Pcm is VolumePcmOutput successPcm && ReferenceEquals(successPcm.Volume, success.Volume) && success.Diagnostics.Count == 0,
                "NIT service factory returns its optional adapter without replacing other speech services");
            var calls = 0; App.CreatePcm = null;
            App.CreateOpenJtalkSpeech = _ => { calls++; throw new IOException(Secret); };
            var noPcm = App.InitializeServices();
            check(calls == 0 && noPcm.OpenJtalk is null && noPcm.Diagnostics.Count == 0,
                "NIT optional factory is never invoked when PCM is unavailable");
        }
        finally
        {
            App.CreateSpeech = oldSpeech; App.CreatePcm = oldPcm; App.CreateCredentialStore = oldStore;
            App.CreateUtauPostprocessor = oldPost; App.CreateOpenJtalkSpeech = oldOpen;
            for (var i = owned.Count - 1; i >= 0; --i) owned[i].DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static void Selection(string voiceRoot, Action<bool, string> check)
    {
        var order = new ConcurrentQueue<string>();
        var standard = new SpeechProbe("standard", order); var nit = new SelectableSpeechProbe(order);
        var pcm = new PcmProbe(order);
        var model = new MainViewModel(standard, voiceRoot, pcm, openJtalkSpeech: nit);
        Window? window = null; var disposed = false;
        try
        {
            var view = new MainView { DataContext = model };
            window = new Window { Width = 900, Height = 1000, Content = view }; window.Show();
            Dispatcher.UIThread.RunJobs();
            var toggle = view.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "OpenJtalkToggle");
            var selector = view.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "OpenJtalkVoiceSelector");
            check(model.OpenJtalkAvailable && !model.UseOpenJtalk && toggle.IsVisible && toggle.IsChecked == false &&
                model.VoiceName == standard.Name, "NIT selection is visible when available and starts with standard speech selected");
            check(selector.IsVisible && selector.Items.Cast<OpenJtalkVoiceChoice>().SequenceEqual(nit.Voices) &&
                Equals(selector.SelectedItem, nit.Voices[0]), "NIT voice picker shows the available voices and initial default");
            toggle.IsChecked = true; Dispatcher.UIThread.RunJobs();
            check(model.UseOpenJtalk && !model.UseUtau && model.VoiceName == nit.Name,
                "NIT checkbox activates the optional speech adapter through the existing UI binding");
            model.TestText = "NIT test"; model.SpeakCommand.Execute(null);
            PumpUntil(() => nit.Spoken.Count == 1);
            check(standard.Spoken.IsEmpty && pcm.PlayCalls == 0 && nit.VoiceIds.Single() == nit.Voices[0].Id,
                "NIT mode routes the utterance to the initial voice");
            PumpUntil(() => model.SelectedVoicebank is not null);
            var bank = model.SelectedVoicebank;
            var utauMode = model.SelectedUtauMode;
            selector.SelectedItem = nit.Voices[1]; Dispatcher.UIThread.RunJobs();
            check(model.UseOpenJtalk && model.SelectedOpenJtalkVoice == nit.Voices[1] && model.VoiceName == nit.Voices[1].Name,
                "changing the NIT voice through the picker updates the active voice label");
            check(model.SelectedVoicebank == bank && model.SelectedUtauMode == utauMode,
                "changing the NIT voice preserves the selected UTAU bank and synthesis mode");
            model.SelectedOpenJtalkVoice = new OpenJtalkVoiceChoice("unknown", "Unknown");
            model.TestText = "NIT voice change"; model.SpeakCommand.Execute(null);
            PumpUntil(() => nit.Spoken.Count == 2);
            check(nit.VoiceIds.SequenceEqual(nit.Voices.Select(x => x.Id)) && standard.Spoken.IsEmpty,
                "the next utterance uses the new NIT voice and ignores an unsupported selection");
            model.UseUtau = true;
            check(model.UseUtau && !model.UseOpenJtalk, "UTAU selection turns NIT off");
            model.UseOpenJtalk = true;
            check(!model.UseUtau && model.UseOpenJtalk, "NIT selection turns UTAU off");
            nit.FailSpeak = true; model.TestText = "fallback test"; model.SpeakCommand.Execute(null);
            PumpUntil(() => standard.Spoken.Contains("fallback test"));
            check(model.UseOpenJtalk && standard.Spoken.Count == 1, "NIT failure falls back for that utterance without changing the selected mode");
            nit.FailSpeak = false; model.TestText = "recovery test"; model.SpeakCommand.Execute(null);
            PumpUntil(() => nit.Spoken.Contains("recovery test"));
            var report = CaptureReport(model);
            check(standard.Spoken.Count == 1 && report.Contains("SPEECH001", StringComparison.Ordinal) && !report.Contains(Secret, StringComparison.Ordinal),
                "the next NIT utterance succeeds and fallback diagnostics exclude speech exception text");
            toggle.IsChecked = false; Dispatcher.UIThread.RunJobs();
            check(!model.UseOpenJtalk && !model.UseUtau && model.VoiceName == standard.Name,
                "clearing NIT returns to standard speech without re-enabling a previous custom mode");
            nit.FailDispose = true;
            var shutdown = model.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); disposed = true;
            Exception? failure = null; try { shutdown.GetAwaiter().GetResult(); } catch (Exception error) { failure = error; }
            check(failure is IOException && order.ToArray().SequenceEqual(["nit", "pcm", "standard"]) &&
                nit.DisposeCalls == 1 && pcm.DisposeCalls == 1 && standard.DisposeCalls == 1,
                "failed NIT shutdown still closes the borrowed PCM owner afterwards and standard output exactly once");
        }
        finally
        {
            if (!disposed)
            {
                var shutdown = model.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
            }
            window?.Close();
        }
    }

    private static void RandomSelection(string voiceRoot, Action<bool, string> check)
    {
        var order = new ConcurrentQueue<string>();
        var standard = new SpeechProbe("standard", order);
        var bundled = new SelectableSpeechProbe(order);
        var restricted = new TermsProbe(order);
        var choices = bundled.Voices.Select(voice => new DesktopBundledSpeechSelection.Voice(voice, bundled))
            .Append(new(restricted.Voices[0], restricted));
        var router = new DesktopBundledSpeechSelection(choices, [bundled, restricted]);
        var pcm = new PcmProbe(order);
        var processor = new RandomUtauProbe();
        var root = Path.Combine(voiceRoot, "random-voice");
        var path = Path.Combine(root, "settings", "startup.json");
        new StartupSession(Engine: "nit", NitVoice: bundled.Voices[1].Id).Write(path);
        var model = new MainViewModel(standard, voiceRoot, pcm, utauPostprocessor: processor,
            openJtalkSpeech: router, sessionDirectory: root);
        var view = new MainView { DataContext = model };
        var window = new Window { Width = 800, Height = 900, Content = view };
        var counts = new ConcurrentQueue<int>();
        try
        {
            window.Show();
            PumpUntil(() => model.SelectedVoicebank is not null);
            var first = new Voicebank("Random A", Path.Combine(root, "bank-a"), [], 0);
            var second = new Voicebank("Random B", Path.Combine(root, "bank-b"), [], 0);
            model.Voicebanks.Clear(); model.Voicebanks.Add(first); model.Voicebanks.Add(second);
            model.SelectedVoicebank = first;
            model.SelectedUtauMode = processor.Modes[1];
            var toggle = view.FindControl<ToggleButton>("RandomVoiceToggle")!;
            check(toggle.IsChecked == false && !StartupSession.Read(path).RandomVoice,
                "legacy startup settings leave random voice disabled");
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            check(model.RandomVoiceEnabled && model.UseOpenJtalk, "random toggle keeps the manual engine selection");

            var next = 0;
            model.ChooseRandomVoice = count => { counts.Enqueue(count); return next++; };
            Say("標準の発言"); Say("音源一の発言"); Say("音源二の発言"); Say("同梱一の発言"); Say("同梱二の発言");
            PumpUntil(() => bundled.Spoken.Count == 2 && processor.Banks.Count == 2 && standard.Spoken.Count == 1);
            check(counts.ToArray().SequenceEqual([5, 5, 5, 5, 5]) && processor.Banks.SequenceEqual([first, second]) &&
                bundled.VoiceIds.SequenceEqual(bundled.Voices.Select(x => x.Id)) && restricted.Spoken.IsEmpty,
                "random uses one equal-sized candidate slot per available standard, UTAU and bundled voice and excludes unaccepted terms");
            check(model.SelectedVoicebank == first && model.SelectedUtauMode == processor.Modes[1] &&
                model.SelectedOpenJtalkVoice == bundled.Voices[1] && router.VoiceId == bundled.Voices[1].Id,
                "random utterances preserve the manual bank, bundled voice and UTAU synthesis method");

            model.SelectedOpenJtalkVoice = restricted.Voices[0];
            model.VoiceTermsOpen = true;
            model.AcceptVoiceTermsCommand.Execute(null);
            model.SelectedOpenJtalkVoice = bundled.Voices[1];
            model.ChooseRandomVoice = count => { counts.Enqueue(count); return count - 1; };
            model.ReceiveIrc = (_, _, _, receive, _, ct, _) =>
            {
                receive(new("Twitch", "fixture", "r1", "reader", "チャット発言", "IRC", Stopwatch.GetTimestamp()));
                return Task.Delay(Timeout.Infinite, ct);
            };
            model.TwitchChannel = "fixture";
            model.ConnectAnonymousCommand.Execute(null);
            PumpUntil(() => restricted.Spoken.Count == 1);
            model.DisconnectTwitchCommand.Execute(null);
            check(counts.Last() == 6 && restricted.Spoken.Single().Contains("チャット発言", StringComparison.Ordinal),
                "accepted voices join the random pool for received chat as well as manual speech");

            bundled.FailSpeak = true;
            model.ChooseRandomVoice = _ => 3;
            Say("標準に退避");
            PumpUntil(() => standard.Spoken.Contains("標準に退避"));
            bundled.FailSpeak = false;

            var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bundled.HoldSpeech = held.Task;
            var draws = 0;
            model.ChooseRandomVoice = _ => { Interlocked.Increment(ref draws); return 3; };
            Say("再生中に切替");
            PumpUntil(() => bundled.Spoken.Contains("再生中に切替"));
            toggle.IsChecked = false;
            Dispatcher.UIThread.RunJobs();
            Say("手動音声へ復帰");
            check(!held.Task.IsCompleted && bundled.VoiceIds.Last() == bundled.Voices[0].Id &&
                !bundled.Spoken.Contains("手動音声へ復帰"), "switching random off leaves the current utterance on its original voice");
            bundled.HoldSpeech = null;
            held.SetResult();
            PumpUntil(() => bundled.Spoken.Contains("手動音声へ復帰"));
            check(!model.RandomVoiceEnabled && bundled.VoiceIds.Last() == bundled.Voices[1].Id &&
                standard.Spoken.Count == 2 && Volatile.Read(ref draws) == 1,
                "failed random voice uses the ordinary fallback and disabling random restores the manual bundled voice");
            toggle.IsChecked = true;
            Dispatcher.UIThread.RunJobs();

            var interrupted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            bundled.HoldSpeech = interrupted.Task;
            Say("途中で停止");
            PumpUntil(() => bundled.Spoken.Contains("途中で停止"));
            model.SkipCommand.Execute(null);
            bundled.HoldSpeech = null;
            Say("停止後の発言");
            PumpUntil(() => bundled.Spoken.Contains("停止後の発言"));
            check(!interrupted.Task.IsCompleted && standard.Spoken.Count == 2 && Volatile.Read(ref draws) == 3,
                "cancelling a random utterance skips fallback and allows the next utterance to draw a new voice");
        }
        finally
        {
            window.Close();
            var shutdown = model.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        }
        var saved = StartupSession.Read(path);
        var restored = new MainViewModel(new SpeechProbe("standard", order), voiceRoot,
            openJtalkSpeech: new SelectableSpeechProbe(order), sessionDirectory: root);
        try
        {
            check(saved.RandomVoice && saved.Engine == "nit" && saved.NitVoice == bundled.Voices[1].Id &&
                restored.RandomVoiceEnabled && restored.UseOpenJtalk && restored.SelectedOpenJtalkVoice == bundled.Voices[1],
                "random selection is restored independently from the saved manual voice");
        }
        finally
        {
            var shutdown = restored.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        }

        void Say(string text) { model.TestText = text; model.SpeakCommand.Execute(null); }
    }

    private sealed class RandomUtauProbe : IUtauPcmPostprocessor, IUtauModeProvider, IUtauDirectRenderer
    {
        public IReadOnlyList<UtauModeChoice> Modes { get; } =
            [new(UtauSynthesisMode.Context, "Context"), new(UtauSynthesisMode.DirectPsola, "Direct")];
        public ConcurrentQueue<Voicebank> Banks { get; } = new();
        public IUtauPcmPostprocessor? Resolve(UtauSynthesisMode mode) => mode == Modes[1].Mode ? this : null;
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => throw new NotSupportedException();
        public Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Banks.Enqueue(bank);
            return Task.FromResult(new PcmAudio([1, -1], 44100));
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static void Unavailable(string voiceRoot, Action<bool, string> check)
    {
        var model = new MainViewModel(new SpeechProbe("standard", new()), voiceRoot);
        Window? window = null;
        try
        {
            var view = new MainView { DataContext = model };
            window = new Window { Width = 900, Height = 1000, Content = view }; window.Show(); Dispatcher.UIThread.RunJobs();
            var toggle = view.GetLogicalDescendants().OfType<CheckBox>().Single(control => control.Name == "OpenJtalkToggle");
            var selector = view.GetLogicalDescendants().OfType<ComboBox>().Single(control => control.Name == "OpenJtalkVoiceSelector");
            model.UseOpenJtalk = true;
            check(!model.OpenJtalkAvailable && !model.UseOpenJtalk && !toggle.IsVisible && !selector.IsVisible && model.VoiceName == "standard",
                "an unavailable NIT implementation is hidden and cannot activate an empty speech mode");
        }
        finally
        {
            var shutdown = model.DisposeAsync().AsTask(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
            window?.Close();
        }
    }

    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("NIT selection test did not complete.");
            Dispatcher.UIThread.RunJobs(); Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }
    private static string CaptureReport(MainViewModel model)
    {
        var report = "";
        var copy = model.CopyDiagnosticsAsync(value => { report = value; return Task.CompletedTask; });
        PumpUntil(() => copy.IsCompleted); copy.GetAwaiter().GetResult();
        return report;
    }
    private class SpeechProbe(string name, ConcurrentQueue<string> order) : ISpeechOutput
    {
        public virtual string Name => name;
        public ConcurrentQueue<string> Spoken { get; } = new();
        public bool FailSpeak { get; set; }
        public bool FailDispose { get; set; }
        public int DisposeCalls { get; private set; }
        public virtual Task SpeakAsync(string text, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Spoken.Enqueue(text); return FailSpeak ? Task.FromException(new IOException(Secret)) : Task.CompletedTask; }
        public ValueTask DisposeAsync()
        { DisposeCalls++; order.Enqueue(name); return FailDispose ? ValueTask.FromException(new IOException(Secret)) : ValueTask.CompletedTask; }
    }
    private sealed class SelectableSpeechProbe(ConcurrentQueue<string> order) : SpeechProbe("nit", order), IOpenJtalkVoiceSelection
    {
        public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } =
            [new("nit-atr503", "NIT default"), new("tohoku-f01-neutral", "NIT alternate")];
        public string VoiceId { get; set; } = "nit-atr503";
        public override string Name => Voices.Single(x => x.Id == VoiceId).Name;
        public ConcurrentQueue<string> VoiceIds { get; } = new();
        public Task? HoldSpeech { get; set; }

        public override Task SpeakAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            VoiceIds.Enqueue(VoiceId);
            var hold = HoldSpeech;
            var result = base.SpeakAsync(text, ct);
            return hold is null ? result : WaitAsync(result, hold, ct);
        }

        private static async Task WaitAsync(Task result, Task hold, CancellationToken ct)
        { await result; await hold.WaitAsync(ct); }
    }
    private sealed class TermsProbe(ConcurrentQueue<string> order, OpenJtalkVoiceChoice? choice = null) : SpeechProbe("Supertonic fixture", order), IOpenJtalkVoiceSelection, ISpeechPreparation
    {
        internal const string Terms = "Full fixture terms\nAttachment A";
        internal static readonly string Version = new('A', 64);
        public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } =
            [choice ?? new("supertonic3-0", "Supertonic fixture", AcceptanceId: Version, TermsText: Terms)];
        public string VoiceId { get; set; } = "supertonic3-0";
        public int Preparations => Volatile.Read(ref preparations);
        private int preparations;
        public Task PrepareAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref preparations);
            return Task.CompletedTask;
        }
    }
    private sealed class PcmProbe(ConcurrentQueue<string> order) : IPcmOutput
    {
        public int PlayCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct) { PlayCalls++; return Task.CompletedTask; }
        public ValueTask DisposeAsync() { DisposeCalls++; order.Enqueue("pcm"); return ValueTask.CompletedTask; }
    }
}

