namespace Tanayomi;

public sealed partial class MainViewModel
{
    private string? sessionPath;
    private StartupSession session = new();
    private StartupSession? lastSaved;
    private bool sessionDirty;
    private string? rememberedBank;

    private void LoadSession(string? root)
    {
        if (root == null) return;
        sessionPath = Path.Combine(root, "settings", "startup.json");
        try { session = StartupSession.Read(sessionPath); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) { SessionError("起動設定を読み込めません。未接続で起動します。", ex); }
        lastSaved = session;
        twitchChannel = session.Channel;
        useEventSub = session.EventSub;
        rememberedBank = session.Bank;
        YouTubeVideo = session.Video;
        if (session.ClientId.Length > 0) TwitchClientId = session.ClientId;
        SelectedOpenJtalkVoice = OpenJtalkVoices.FirstOrDefault(x => x.Id == session.NitVoice);
        RefreshVoiceTerms();
        if (session.Engine == "utau") UseUtau = true;
        else if (session.Engine == "nit") UseOpenJtalk = true;
        RandomVoiceEnabled = session.RandomVoice;
    }

    private void RememberRoute(string route, string target = "")
    {
        var normalized = StartupSession.ChannelName(target);
        if ((target.Trim().Length > 0 && normalized.Length == 0) || (route == "anonymous" && normalized.Length == 0)) route = "off";
        var id = route == "account" && currentAuthorization?.AccessToken == TwitchToken
            ? currentAuthorization?.Identity.UserId ?? "" : "";
        session = session with { Route = route, Target = normalized, AccountId = id };
        SaveSession();
    }

    private void ResumeTwitch(bool anonymous)
    {
        var target = session.Target;
        var both = !anonymous && UseEventSub;
        anonymousConnection = anonymous;
        mobile.ConnectTwitch(() => StartTwitch(target, both, anonymous));
    }

    private void SaveSession()
    {
        if (sessionPath == null) return;
        var video = YouTubeVideo?.Trim() ?? "";
        var client = TwitchClientId?.Trim() ?? "";
        session = session with {
            Channel = StartupSession.ChannelName(TwitchChannel), EventSub = UseEventSub,
            Bank = SelectedVoicebank is { } bank ? Path.GetFileName(bank.Directory) : rememberedBank ?? "",
            Video = video.Length == 11 && video.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? video : "",
            ClientId = client.Length <= 64 && client.All(char.IsAsciiLetterOrDigit) ? client : ""
        };
        if (session == lastSaved) return;
        try
        {
            var savedAcceptance = lastSaved?.SupertonicTermsAcceptance;
            session.Write(sessionPath);
            lastSaved = session;
            if (savedAcceptance != session.SupertonicTermsAcceptance) Changed(nameof(VoiceAcceptanceStatus));
        }
        catch (Exception ex) { SessionError("次回起動用の設定を保存できません。", ex); }
    }

    private void SessionError(string message, Exception error) => Post(new("設定", message, Diagnostic:
        DiagnosticEvent.Create(DiagnosticCode.StorageUnavailable, DiagnosticOperation.Storage, DiagnosticRecovery.WithoutStorage, error)));

    private async Task OnStartupUiAsync(Action action, CancellationToken ct)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (ct.IsCancellationRequested) return;
            try { action(); done.TrySetResult(); }
            catch (Exception ex) { done.TrySetException(ex); }
        });
        await done.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task PrepareStartupSpeechAsync(CancellationToken ct)
    {
        var saved = session;
        // Voicebank import and UTAU mode selection only gate a UTAU utterance.
        var ready = saved.Engine == "utau" || saved.RandomVoice
            ? Task.WhenAll(voicebanksLoading, volumeLoading, modeLoading, drawnOutLoading, textLoading)
            : Task.WhenAll(volumeLoading, textLoading);
        await ready.WaitAsync(ct).ConfigureAwait(false);
    }
}
