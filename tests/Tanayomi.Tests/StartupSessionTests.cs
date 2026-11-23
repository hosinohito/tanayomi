using Avalonia.Threading;
using Tanayomi;

internal static class StartupSessionTests
{
    internal static void Run(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var root = fixture.DirectoryPath;
        var voices = Path.Combine(root, "voices");
        var path = Path.Combine(root, "settings", "startup.json");
        var installed = Task.Run(() => Voicebanks.LoadInstalledAsync(voices)).GetAwaiter().GetResult();
        var bank = Path.GetFileName(installed[0].Directory);
        var credential = new MemoryStore();
        var wanted = 0;
        // Deny the fake mobile background lease so the real socket never opens.
        MainViewModel Open() => new(new SilentSpeech(), voices, credentialStore: credential, mobileLifecycle: true,
            backgroundOperation: enabled => { if (enabled) wanted++; return !enabled; },
            openJtalkSpeech: new SilentSpeech(), sessionDirectory: root,
            twitchAuthentication: new TwitchAuthentication(new HttpClient(new ValidateOnly())));
        void WithModel(Action<MainViewModel> test)
        {
            var m = Open();
            try { test(m); }
            finally { m.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        }
        WithModel(m => {
            // This fixture saves a selected bank; the standard voice can now
            // restore Twitch before the bank list becomes available.
            Pump(() => m.CanAuthenticate && m.SelectedVoicebank != null);
            check(wanted == 0 && m.TwitchChannel == "", "first launch makes no Twitch connection request");
            m.TwitchChannel = "hoshinohito"; m.UseEventSub = true; m.UseOpenJtalk = true;
            m.YouTubeVideo = "123456789ab"; m.YouTubeKey = "fixture-private-key"; m.TestText = "fixture-private-text";
            m.ConnectAnonymousCommand.Execute(null);
        });
        var saved = StartupSession.Read(path);
        check(saved.Route == "anonymous" && saved.Target == "hoshinohito" && saved.Bank == bank &&
            saved.Engine == "nit" && !File.ReadAllText(path).Contains("fixture-private", StringComparison.Ordinal),
            "anonymous connection and voice survive exit without saving input text or API key");
        wanted = 0;
        WithModel(m => {
            Pump(() => m.CanAuthenticate);
            check(wanted == 1 && m.UseOpenJtalk && m.UseEventSub && m.YouTubeVideo == "123456789ab" &&
                m.TwitchChannel == "hoshinohito", "restart restores reading settings and requests anonymous reception exactly once");
            m.DisconnectTwitchCommand.Execute(null);
        });
        wanted = 0;
        WithModel(m => { Pump(() => m.CanAuthenticate); check(wanted == 0, "explicit disconnect survives restart"); });
        (saved with { Route = "account", AccountId = "123" }).Write(path);
        WithModel(m => {
            Pump(() => m.CanAuthenticate);
            check(wanted == 0 && m.AuthenticationStatus.Contains("再認証", StringComparison.Ordinal), "missing credentials leave account route awaiting user authentication");
        });
        credential.Value = System.Text.Json.JsonSerializer.Serialize(new {
            version = 1, accessToken = "fixtureaccesstoken", refreshToken = "fixturerefresh", clientId = TwitchAuthentication.DefaultClientId,
            expiresAt = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
        });
        (saved with { Route = "account", AccountId = "456" }).Write(path);
        WithModel(m => { Pump(() => m.CanAuthenticate); check(wanted == 0 && m.TwitchToken == "", "another saved account cannot resume the previous user's connection"); });
        (saved with { Route = "account", AccountId = "123" }).Write(path);
        WithModel(m => {
            Pump(() => m.CanAuthenticate);
            check(wanted == 1 && m.TwitchLogin == "hoshinohito" && m.TwitchToken == "fixtureaccesstoken",
                "validated saved account resumes its requested route");
            m.ForgetTwitchAsync().GetAwaiter().GetResult();
            check(StartupSession.Read(path).Route == "off" && credential.Value == null &&
                !File.ReadAllText(path).Contains("fixtureaccesstoken", StringComparison.Ordinal), "forget clears resume intent and protected credentials");
        });
        wanted = 0; saved.Write(path);
        WithModel(m => { m.DisconnectTwitchCommand.Execute(null); Pump(() => m.Voicebanks.Count > 0);
            check(wanted == 0 && StartupSession.Read(path).Route == "off", "manual stop cancels pending startup restore"); });
        saved.Write(path);
        var speech = new SilentSpeech();
        var desktop = new MainViewModel(new SilentSpeech(), voices, openJtalkSpeech: speech, sessionDirectory: root);
        try
        {
            var received = new System.Collections.Concurrent.ConcurrentQueue<(string Channel, string Token, bool Anonymous)>();
            desktop.ReceiveIrc = async (channel, login, token, receive, status, ct, anonymous) =>
            {
                received.Enqueue((channel, token, anonymous));
                receive(new("Twitch", channel, "fixture-id", "fixture", "起動確認", "IRC", System.Diagnostics.Stopwatch.GetTimestamp()));
                await Task.Delay(Timeout.Infinite, ct);
            };
            Pump(() => !speech.Spoken.IsEmpty);
            check(received.Single() == ("hoshinohito", "", true) && speech.Spoken.Single() == "起動確認",
                "a restart without a button press routes an incoming IRC message to the saved speech engine");
        }
        finally { desktop.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        (saved with { Route = "off", Engine = "standard" }).Write(path);
        var configurable = new SelectableNit();
        var chooser = new MainViewModel(new SilentSpeech(), voices, openJtalkSpeech: configurable, sessionDirectory: root);
        try
        {
            check(!chooser.UseOpenJtalk && chooser.SelectedOpenJtalkVoice == configurable.Voices[0],
                "startup with the default voice selected keeps standard speech active");
            chooser.SelectedOpenJtalkVoice = configurable.Voices[1];
            check(!chooser.UseOpenJtalk && chooser.VoiceName == "Offline", "choosing a voice does not enable NIT");
            chooser.UseOpenJtalk = true;
        }
        finally { chooser.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        var selected = StartupSession.Read(path);
        check(selected.Engine == "nit" && selected.NitVoice == configurable.Voices[1].Id,
            "the chosen NIT voice and enabled mode are saved on exit");
        var nit = new WarmNit();
        var prepared = new MainViewModel(new SilentSpeech(), voices, openJtalkSpeech: nit, sessionDirectory: root);
        try
        {
            Pump(() => nit.Entered.Task.IsCompleted);
            check(prepared.UseOpenJtalk && prepared.SelectedOpenJtalkVoice == nit.Voices[1] &&
                nit.Entered.Task.GetAwaiter().GetResult() == nit.Voices[1].Id,
                "startup prepares the saved NIT voice before the first utterance");
            prepared.UseOpenJtalk = true;
            prepared.UseOpenJtalk = false;
            Pump(() => nit.Stopped.Task.IsCompleted);
            check(nit.Calls == 1, "restored NIT is prepared once before speech and deselection cancels its pending preparation");
        }
        finally { prepared.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        foreach (var voice in new[] { "tohoku-f01-angry", "tohoku-f01-happy", "tohoku-f01-sad",
            "mei-normal", "mei-angry", "mei-bashful", "mei-happy", "mei-sad",
            "takumi-normal", "takumi-angry", "takumi-happy", "takumi-sad" })
        {
            (saved with { Route = "off", Engine = "nit", NitVoice = voice }).Write(path);
            var variant = new SelectableNit();
            var restored = new MainViewModel(new SilentSpeech(), voices, openJtalkSpeech: variant, sessionDirectory: root);
            try
            {
                check(restored.UseOpenJtalk && restored.SelectedOpenJtalkVoice?.Id == voice && variant.VoiceId == voice,
                    "each additional bundled HTS style restores its selected voice before speech");
            }
            finally { restored.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            check(StartupSession.Read(path).NitVoice == voice,
                "each additional bundled HTS style survives another settings save");
        }
        File.WriteAllText(path, """
            {
              "Channel": "hoshinohito",
              "Route": "anonymous",
              "Target": "hoshinohito",
              "EventSub": true,
              "Engine": "voicevox",
              "Style": 2,
              "Speaker": "11111111-1111-1111-1111-111111111111",
              "PolicyHash": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
              "Video": "123456789ab"
            }
            """);
        var legacy = StartupSession.Read(path);
        check(legacy.Engine == "standard" && legacy.NitVoice == configurable.Voices[0].Id,
            "legacy VOICEVOX startup settings resolve to standard speech and default NIT voice");
        wanted = 0;
        WithModel(m =>
        {
            Pump(() => m.CanAuthenticate);
            check(wanted == 1 && !m.UseUtau && !m.UseOpenJtalk && m.VoiceName == "Offline" &&
                m.TwitchChannel == "hoshinohito" && m.UseEventSub && m.YouTubeVideo == "123456789ab",
                "legacy VOICEVOX startup resumes the saved connection with standard speech and retains other settings");
            m.DisconnectTwitchCommand.Execute(null);
        });
        using (var persisted = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
        {
            var settings = persisted.RootElement;
            check(settings.GetProperty("Engine").GetString() == "standard" &&
                !settings.TryGetProperty("Style", out _) && !settings.TryGetProperty("Speaker", out _) &&
                !settings.TryGetProperty("PolicyHash", out _),
                "the next settings save writes standard speech and omits obsolete VOICEVOX fields");
        }
        foreach (var id in new[] { "voicevox-3", "voicevox-10008", "supertonic3-0", "supertonic3-9", "aivis-47e53151-a378-46f3-abee-ce13aa07feb1" })
        {
            (saved with { Route = "off", Engine = "nit", NitVoice = id }).Write(path);
            check(StartupSession.Read(path).NitVoice == id, "bundled voice identity survives storage without engine-specific file paths");
        }
        foreach (var value in new[] { null, "unknown-nit-voice", "voicevox--1", "voicevox-10009", "supertonic3-10", "aivis-../other" })
        {
            File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(new { Engine = "nit", NitVoice = value }));
            var fallback = StartupSession.Read(path);
            check(fallback.Engine == "nit" && fallback.NitVoice == configurable.Voices[0].Id,
                "unknown or missing saved NIT voice falls back to the default while keeping NIT enabled");
        }
        File.WriteAllText(path, "{");
        wanted = 0;
        WithModel(m => { Pump(() => m.CanAuthenticate); check(wanted == 0 && !m.UseOpenJtalk, "malformed startup file starts disconnected with standard voice"); });
        CheckSpeechIdle(check, root, voices, mobile: true);
        CheckSpeechIdle(check, root, voices, mobile: false);
    }

    private static void CheckSpeechIdle(Action<bool, string> check, string root, string voices, bool mobile)
    {
        var output = new FailingSpeech();
        var model = new MainViewModel(output, voices, mobileLifecycle: mobile,
            sessionDirectory: Path.Combine(root, mobile ? "mobile-idle" : "desktop-idle"));
        try
        {
            var idle = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            model.MobileSpeechIdle += () => idle.TrySetResult(Dispatcher.UIThread.CheckAccess() && !model.IsMobileSpeechBusy);
            model.TestText = "再生開始前の失敗";
            model.SpeakCommand.Execute(null);
            Pump(() => output.Entered.Task.IsCompleted);
            check(model.IsMobileSpeechBusy && !idle.Task.IsCompleted, "speech idle is not reported while output is busy");
            output.Playback.TrySetException(new IOException("Playback did not start."));
            Pump(() => !model.IsMobileSpeechBusy);
            string? diagnostics = null;
            model.CopyDiagnosticsAsync(report =>
            {
                diagnostics = report;
                return Task.CompletedTask;
            }).GetAwaiter().GetResult();
            check(diagnostics?.Contains("SPEECH002", StringComparison.Ordinal) == true,
                "speech queue records the failure before returning to idle");
            if (mobile)
            {
                Pump(() => idle.Task.IsCompleted);
                check(idle.Task.GetAwaiter().GetResult(), "mobile speech failure reports idle on the UI thread");
            }
            else
                check(!idle.Task.IsCompleted, "desktop speech failure does not report mobile idle");
        }
        finally { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }

    private static void Pump(Func<bool> ready)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!ready()) { Dispatcher.UIThread.RunJobs(); if (DateTime.UtcNow > end) throw new TimeoutException("startup restore"); Thread.Sleep(5); }
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class ValidateOnly : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri?.AbsolutePath != "/oauth2/validate") throw new InvalidOperationException("Unexpected auth request");
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new StringContent(
                System.Text.Json.JsonSerializer.Serialize(new { client_id = TwitchAuthentication.DefaultClientId,
                    login = "hoshinohito", user_id = "123", scopes = new[] { "chat:read" }, expires_in = 3600 })) });
        }
    }
    private sealed class MemoryStore : ICredentialStore
    {
        internal string? Value;
        public Task<string?> ReadAsync(CancellationToken ct) => Task.FromResult(Value);
        public Task WriteAsync(string value, CancellationToken ct) { Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct) { Value = null; return Task.CompletedTask; }
    }
    private class SilentSpeech : ISpeechOutput
    {
        public string Name => "Offline";
        internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Spoken = new();
        public virtual Task SpeakAsync(string text, CancellationToken ct) { Spoken.Enqueue(text); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FailingSpeech : ISpeechOutput
    {
        public string Name => "Offline failure";
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Playback = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task SpeakAsync(string text, CancellationToken ct)
        {
            Entered.TrySetResult();
            return Playback.Task.WaitAsync(ct);
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private class SelectableNit : SilentSpeech, IOpenJtalkVoiceSelection
    {
        public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } =
            [new("nit-atr503", "NIT default"), new("tohoku-f01-neutral", "NIT alternate"),
             new("tohoku-f01-angry", "NIT angry"), new("tohoku-f01-happy", "NIT happy"), new("tohoku-f01-sad", "NIT sad"),
             new("mei-normal", "Mei"), new("mei-angry", "Mei angry"), new("mei-bashful", "Mei bashful"),
             new("mei-happy", "Mei happy"), new("mei-sad", "Mei sad"), new("takumi-normal", "Takumi"),
             new("takumi-angry", "Takumi angry"), new("takumi-happy", "Takumi happy"), new("takumi-sad", "Takumi sad")];
        public string VoiceId { get; set; } = "nit-atr503";
    }
    private sealed class WarmNit : SelectableNit, ISpeechPreparation
    {
        internal int Calls;
        internal readonly TaskCompletionSource<string> Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task PrepareAsync(CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            Entered.TrySetResult(VoiceId);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { Stopped.TrySetResult(); }
        }
        public override Task SpeakAsync(string text, CancellationToken ct) => throw new InvalidOperationException("Preparation must not play audio.");
    }
}
