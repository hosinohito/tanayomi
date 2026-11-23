using System.Collections.Concurrent;
using Tanayomi;

public static class DiagnosticsTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var secrets = new[] { "oauthSecret123", "deviceSecret987", "youtubeSecret456", "authorPrivate",
            "channelPrivate", "userIdPrivate", "chatPrivate", @"C:\privateVoicebank\sample.wav",
            "https://localhost/#access_token=oauthSecret123" };
        var secretText = string.Join(" ", secrets);
        var error = new IOException(secretText, new Exception(secretText));
        error.Data["token"] = secretText;
        error.Source = secretText;
        error.HelpLink = secretText;
        var buffer = new DiagnosticBuffer(2);
        buffer.Record(DiagnosticEvent.Create(DiagnosticCode.AuthenticationFailed,
            DiagnosticOperation.TwitchAuthentication, DiagnosticRecovery.Reauthenticate, error));
        var report = buffer.CreateReport();
        check(secrets.All(secret => !report.Contains(secret, StringComparison.Ordinal)),
            "diagnostics exclude exception messages, inner exceptions, data, links, paths and credentials");
        check(report.Contains("AUTH001") && report.Contains("operation=TwitchAuthentication") &&
            report.Contains("exception=IOException") && report.Contains("recovery=Reauthenticate"),
            "diagnostics preserve stable cause, operation, safe exception type and recovery");
        var version = typeof(DiagnosticBuffer).Assembly.GetName().Version!.ToString();
        check(report.Contains("Build: " + DiagnosticBuffer.BuildIdentity) &&
            DiagnosticBuffer.BuildIdentity.StartsWith(version + "/", StringComparison.Ordinal) &&
            DiagnosticBuffer.BuildIdentity.Contains(typeof(DiagnosticBuffer).Assembly.ManifestModule.ModuleVersionId.ToString("N")),
            "diagnostics identify the running assembly build without source or machine paths");
        check(buffer.CreateReport() == report, "diagnostic report snapshots do not mutate stored events");

        // An unknown type must not export a user-defined type name or access Message.
        buffer.Record(DiagnosticEvent.Create(DiagnosticCode.UiOperationFailed,
            DiagnosticOperation.DiagnosticCopy, DiagnosticRecovery.RetryOperation, new PrivateTokenException()));
        check(buffer.CreateReport().Contains("exception=Exception") && !buffer.CreateReport().Contains(nameof(PrivateTokenException)),
            "diagnostics classify unknown exception types without reading their messages or names");
        buffer.Record(DiagnosticEvent.Create(DiagnosticCode.VoicebankImportFailed,
            DiagnosticOperation.VoicebankImport, DiagnosticRecovery.RetryOperation));
        check(buffer.Count == 2 && !buffer.CreateReport().Contains("AUTH001") && buffer.CreateReport().Contains("VOICE002"),
            "diagnostic capacity discards the oldest event");

        var invalidRejected = false;
        try { buffer.Record(new((DiagnosticCode)999, DiagnosticOperation.Storage, DiagnosticRecovery.WithoutStorage, DiagnosticException.None)); }
        catch (ArgumentException) { invalidRejected = true; }
        check(invalidRejected && buffer.Count == 2, "diagnostics reject invalid typed fields without altering the buffer");
        foreach (var capacity in new[] { 0, DiagnosticBuffer.MaximumEntries + 1 })
        {
            var rejected = false;
            try { _ = new DiagnosticBuffer(capacity); } catch (ArgumentOutOfRangeException) { rejected = true; }
            check(rejected, "diagnostics reject unbounded or empty capacity");
        }
        var ids = Enum.GetValues<DiagnosticCode>().Select(DiagnosticBuffer.CodeId).ToArray();
        check(ids.Length == ids.Distinct(StringComparer.Ordinal).Count() && ids.All(id => id.Length is >= 5 and <= 9),
            "each diagnostic cause has a distinct stable identifier");

        var copied = "";
        check(await buffer.CopyAsync(text => { copied = text; return Task.CompletedTask; }) && copied == buffer.CreateReport(),
            "explicit copy exports only the safe report");
        check(!await buffer.CopyAsync(_ => throw new IOException(secretText)),
            "synchronous clipboard failure is contained");
        check(!await buffer.CopyAsync(_ => Task.FromException(new InvalidOperationException(secretText))),
            "asynchronous clipboard failure is contained");
        check(!await buffer.CopyAsync(_ => Task.FromCanceled(new CancellationToken(true))),
            "clipboard cancellation does not escape the UI operation");
        check(!await buffer.CopyAsync(null) && buffer.Count == 2,
            "unavailable clipboard remains bounded and returns a recoverable failure");
        check(await buffer.CopyAsync(text => { copied = text; return Task.CompletedTask; }) &&
            copied.Contains("UI001") && copied.Contains("operation=DiagnosticCopy") &&
            secrets.All(secret => !copied.Contains(secret, StringComparison.Ordinal)),
            "clipboard can be retried and its failure diagnostics do not expose secrets");

        var concurrent = new DiagnosticBuffer();
        Parallel.For(0, 1024, _ => concurrent.Record(DiagnosticEvent.Create(DiagnosticCode.ConnectionInterrupted,
            DiagnosticOperation.TwitchIrc, DiagnosticRecovery.Retrying, new IOException(secretText))));
        check(concurrent.Count == DiagnosticBuffer.MaximumEntries &&
            concurrent.CreateReport().Split("NET001", StringSplitOptions.None).Length - 1 == DiagnosticBuffer.MaximumEntries,
            "concurrent recording and report snapshots honor the memory bound");
        check(System.Text.Encoding.UTF8.GetByteCount(concurrent.CreateReport()) < 64 * 1024,
            "a full diagnostic report has a small bounded copy payload");

        var speechDiagnostics = new DiagnosticBuffer();
        var receivedNotices = new ConcurrentQueue<AppNotice>();
        var nextSpoken = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new FakeSpeech((text, _) =>
        {
            if (text == "next") { nextSpoken.TrySetResult(); return Task.CompletedTask; }
            throw new IOException(secretText);
        });
        var fallback = new FakeSpeech((_, _) => throw new InvalidDataException(secretText));
        await using (var queue = new SpeechQueue(primary, fallback))
        {
            queue.Notice += notice =>
            {
                receivedNotices.Enqueue(notice);
                if (notice.Diagnostic is { } diagnostic) speechDiagnostics.Record(diagnostic);
            };
            queue.Enqueue(ChatMessage.Manual("chatPrivate") with { Author = "authorPrivate", Channel = "channelPrivate", Id = "userIdPrivate" });
            queue.Enqueue(ChatMessage.Manual("next"));
            await nextSpoken.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        var speechReport = speechDiagnostics.CreateReport();
        check(speechReport.Contains("SPEECH001") && speechReport.Contains("SPEECH002") &&
            speechReport.Contains("operation=SpeechFallback") && speechReport.Contains("recovery=NextUtterance"),
            "speech failures report fallback and next-utterance recovery while the consumer continues");
        check(receivedNotices.Any(notice => notice.Text.Contains("authorPrivate")) &&
            secrets.All(secret => !speechReport.Contains(secret, StringComparison.Ordinal)),
            "chat-bearing activities are separate from copied speech diagnostics");

        var previousSpeech = App.CreateSpeech;
        var previousPcm = App.CreatePcm;
        var previousCredentials = App.CreateCredentialStore;
        var previousPostprocessor = App.CreateUtauPostprocessor;
        try
        {
            App.CreateSpeech = () => throw new IOException(secretText);
            App.CreatePcm = () => throw new PlatformNotSupportedException(secretText);
            App.CreateCredentialStore = () => throw new UnauthorizedAccessException(secretText);
            App.CreateUtauPostprocessor = null;
            var unavailable = App.InitializeServices();
            await using (unavailable.Speech)
            {
                check(unavailable.Pcm is null && unavailable.Credentials is null && unavailable.Diagnostics.Count == 3,
                    "startup contains independent speech, PCM and credential factory failures");
                var startup = new DiagnosticBuffer();
                foreach (var diagnostic in unavailable.Diagnostics) startup.Record(diagnostic);
                var startupReport = startup.CreateReport();
                check(startupReport.Contains("APP001") && startupReport.Contains("APP002") && startupReport.Contains("APP003") &&
                    startupReport.Contains("exception=UnauthorizedAccessException") &&
                    secrets.All(secret => !startupReport.Contains(secret, StringComparison.Ordinal)),
                    "startup reports preserve specific safe causes without retaining factory secrets");
            }

            var healthy = new FakeSpeech((_, _) => Task.CompletedTask);
            App.CreateSpeech = () => healthy;
            App.CreatePcm = () => throw new IOException(secretText);
            App.CreateCredentialStore = null;
            var partial = App.InitializeServices();
            await using (partial.Speech)
            {
                check(ReferenceEquals(partial.Speech, healthy) && partial.Diagnostics.Count == 1 &&
                    partial.Diagnostics[0].Recovery == DiagnosticRecovery.StandardVoice,
                    "PCM initialization failure preserves the working standard output");
            }
            App.CreateSpeech = () => new FakeSpeech((_, _) => Task.CompletedTask);
            App.CreatePcm = null;
            var supported = App.InitializeServices();
            await using (supported.Speech)
                check(supported.Diagnostics.Count == 0 && supported.Pcm is null && supported.Credentials is null,
                    "intentionally unavailable optional factories are not reported as initialization failures");
            App.CreatePcm = () => new FakePcm();
            App.CreateUtauPostprocessor = () => throw new IOException(secretText);
            var withoutPost = App.InitializeServices();
            await using (withoutPost.Speech)
            await using (withoutPost.Pcm!)
                check(withoutPost.UtauPostprocessor is null && withoutPost.Pcm is not null &&
                    withoutPost.Diagnostics.Count == 1 &&
                    withoutPost.Diagnostics[0].Code == DiagnosticCode.UtauPostprocessorInitializationFailed &&
                    withoutPost.Diagnostics[0].Recovery == DiagnosticRecovery.AvailableFeatures,
                    "optional UTAU processor initialization failure preserves standard and existing PCM outputs");
        }
        finally
        {
            App.CreateSpeech = previousSpeech;
            App.CreatePcm = previousPcm;
            App.CreateCredentialStore = previousCredentials;
            App.CreateUtauPostprocessor = previousPostprocessor;
        }

        using var temporary = new TestDirectory();
        await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath);
        var privateBank = Path.Combine(temporary.DirectoryPath, "privateVoicebankName");
        Directory.CreateDirectory(privateBank);
        await File.WriteAllTextAsync(Path.Combine(privateBank, "oto.ini"), "invalid privateVoicebankName chatPrivate");
        var bankDiagnostics = new DiagnosticBuffer();
        var warnings = new List<string>();
        var banks = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath, warnings.Add, bankDiagnostics.Record);
        check(banks.Any(bank => bank.Name == "hosinohito") && banks.All(bank => bank.Name != "privateVoicebankName") && warnings.Count == 1,
            "an invalid installed voicebank preserves legacy warnings and other working banks");
        var bankReport = bankDiagnostics.CreateReport();
        check(bankReport.Contains("VOICE001") && bankReport.Contains("exception=InvalidDataException") &&
            bankReport.Contains("recovery=OtherVoicebanks") && !bankReport.Contains("privateVoicebankName") &&
            !bankReport.Contains(temporary.DirectoryPath) && !bankReport.Contains("chatPrivate"),
            "individual bank diagnostics are captured before warning text and exclude bank paths and contents");
        var retained = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath, null, _ => throw new PrivateTokenException());
        check(retained.Any(bank => bank.Name == "hosinohito"),
            "a failing diagnostic observer does not interrupt independent voicebanks");
        Func<string, Action<string>?, Task<IReadOnlyList<Voicebank>>> legacyLoad = Voicebanks.LoadInstalledAsync;
        check((await legacyLoad(temporary.DirectoryPath, null)).Count == banks.Count,
            "the existing two-argument installed-bank delegate remains compatible");

        var mobile = new DiagnosticBuffer();
        mobile.Record(DiagnosticEvent.Create(DiagnosticCode.MobileBackgroundUnavailable, DiagnosticOperation.MobileBackgroundStart,
            DiagnosticRecovery.ReconnectOnForeground, new PrivateTokenException()));
        mobile.Record(DiagnosticEvent.Create(DiagnosticCode.MobileAudioSessionFailed, DiagnosticOperation.MobileAudioSessionRelease,
            DiagnosticRecovery.NextUtterance));
        check(mobile.CreateReport().Contains("MOBILE002") && mobile.CreateReport().Contains("MOBILE004") &&
            !mobile.CreateReport().Contains(nameof(PrivateTokenException)),
            "mobile native rejection and callback failure use safe fixed contexts without platform exception names");
    }

    private sealed class PrivateTokenException : Exception
    {
        public override string Message => throw new InvalidOperationException("Message must not be accessed.");
    }

    private sealed class FakeSpeech(Func<string, CancellationToken, Task> speak) : ISpeechOutput
    {
        public string Name => "test";
        public Task SpeakAsync(string text, CancellationToken cancellationToken) => speak(text, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class FakePcm : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
