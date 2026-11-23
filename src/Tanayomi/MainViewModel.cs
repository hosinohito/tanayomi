using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using System.Windows.Input;
using Avalonia.Threading;

namespace Tanayomi;

internal delegate Task IrcReceiver(string channel, string login, string token, Action<ChatMessage> receive,
    Action<string> status, CancellationToken ct, bool anonymous = false);

public sealed partial class MainViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    internal IrcReceiver ReceiveIrc { get; set; } = ChatSources.TwitchIrcAsync;
    private readonly SpeechQueue speech;
    private readonly DiagnosticBuffer diagnostics = new();
    private readonly Channel<AppNotice> updates = Channel.CreateBounded<AppNotice>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private readonly DispatcherTimer timer;
    private readonly SourceSlot twitch = new(), youtube = new();
    private readonly MobileConnectionLifecycle mobile;
    private readonly TwitchAuthentication authentication;
    private readonly TwitchAccount account;
    private readonly SelectableSpeech selectedSpeech;
    private readonly CancellationTokenSource lifetime = new();
    private readonly Task preparation;
    private readonly Task voicebanksLoading;
    private Task bankPreparation = Task.CompletedTask;
    private CancellationTokenSource? bankPreparationCancellation;
    private CancellationTokenSource? authenticationCancellation;
    private Task authenticationTask = Task.CompletedTask;
    private Task sessionMonitor = Task.CompletedTask;
    private Task restoreTask = Task.CompletedTask;
    private Task forgetTask = Task.CompletedTask;
    private CancellationTokenSource? restoreCancellation;
    private bool anonymousConnection;
    private TwitchAuthorization? currentAuthorization;
    private readonly string voiceRoot;
    private bool disposed;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<string> Activities { get; } = [];
    public ObservableCollection<Voicebank> Voicebanks { get; } = [];
    private Voicebank? selectedVoicebank;
    public Voicebank? SelectedVoicebank
    {
        get => selectedVoicebank;
        set
        {
            var changed = !ReferenceEquals(selectedVoicebank, value);
            selectedVoicebank = value;
            if (selectedSpeech.Utau is { } utau) utau.Voicebank = value;
            if (changed) { sessionDirty = true; if (UseUtau) StartSelectedPreparation(); }
            Changed(); Changed(nameof(VoiceName));
        }
    }
    public bool RandomVoiceEnabled
    {
        get => selectedSpeech.RandomVoiceEnabled;
        set
        {
            if (disposed || selectedSpeech.RandomVoiceEnabled == value) return;
            selectedSpeech.RandomVoiceEnabled = value;
            session = session with { RandomVoice = value };
            sessionDirty = true;
            RandomVoiceDetails = "";
            StartSelectedPreparation();
            Changed(); Changed(nameof(VoiceName)); Changed(nameof(RandomVoiceDetails));
        }
    }
    public string RandomVoiceDetails { get; private set; } = "";
    internal Func<int, int> ChooseRandomVoice { set => selectedSpeech.ChooseRandomVoice = value; }
    public string VoiceName => RandomVoiceEnabled ? "発言ごとにランダム" : UseUtau ? $"UTAU · {SelectedVoicebank?.Name ?? "音源を選択"}" : UseOpenJtalk && selectedSpeech.BundledVoiceAllowed
        ? selectedSpeech.OpenJtalk!.Name : selectedSpeech.StandardName;
    public string TestText { get; set; } = "こんにちは。たなよみの読み上げテストです。";
    private string twitchChannel = "", twitchLogin = "", twitchToken = "";
    public string TwitchChannel { get => twitchChannel; set { twitchChannel = value; sessionDirty = true; Changed(); } }
    public string TwitchLogin { get => twitchLogin; set { twitchLogin = value; Changed(); } }
    public string TwitchToken { get => twitchToken; set { twitchToken = value; Changed(); Changed(nameof(ConnectLabel)); } }
    public string ConnectLabel => string.IsNullOrWhiteSpace(TwitchToken) ? "Twitchで認証して接続" : "Twitchに接続";
    private string twitchClientId = TwitchAuthentication.DefaultClientId;
    public string TwitchClientId { get => twitchClientId; set { twitchClientId = value; sessionDirty = true; Changed(); } }
    private bool useEventSub;
    public bool UseEventSub { get => useEventSub; set { useEventSub = value; sessionDirty = true; Changed(); } }
    public bool UseUtau
    {
        get => selectedSpeech.UseUtau;
        set
        {
            var active = value && UtauAvailable;
            var changed = selectedSpeech.UseUtau != active;
            selectedSpeech.UseUtau = active;
            if (changed) { session = session with { Engine = active ? "utau" : "standard" }; sessionDirty = true; StartSelectedPreparation(); }
            Changed(); Changed(nameof(UseOpenJtalk)); Changed(nameof(VoiceName));
        }
    }
    public IReadOnlyList<OpenJtalkVoiceChoice> OpenJtalkVoices =>
        (selectedSpeech.OpenJtalk as IOpenJtalkVoiceSelection)?.Voices ?? [];
    public OpenJtalkVoiceChoice? SelectedOpenJtalkVoice
    {
        get => OpenJtalkVoices.FirstOrDefault(x => x.Id == selectedSpeech.BundledVoiceId);
        set
        {
            if (disposed || value is null || !OpenJtalkVoices.Contains(value) || selectedSpeech.BundledVoiceId == value.Id) return;
            selectedSpeech.SetBundledVoice(value, CanUseVoice(value));
            session = session with { NitVoice = value.Id }; sessionDirty = true;
            VoiceTermsOpen = false;
            Changed(); Changed(nameof(SelectedVoiceTerms)); Changed(nameof(HasSelectedVoiceTerms));
            NotifyVoiceTerms();
            if (UseOpenJtalk) StartSelectedPreparation();
        }
    }
    public string SelectedVoiceTerms => SelectedOpenJtalkVoice is { } voice
        ? string.Join("\n", new[] { voice.Credit, voice.Usage, voice.TermsUrl }.Where(value => !string.IsNullOrWhiteSpace(value))) : "";
    public bool HasSelectedVoiceTerms => SelectedVoiceTerms.Length != 0;
    public bool OpenJtalkAvailable => selectedSpeech.OpenJtalk is not null;
    public bool UseOpenJtalk
    {
        get => selectedSpeech.UseOpenJtalk;
        set
        {
            var wasUtau = selectedSpeech.UseUtau;
            var previousNit = selectedSpeech.UseOpenJtalk;
            selectedSpeech.UseOpenJtalk = value && OpenJtalkAvailable && !disposed;
            if (selectedSpeech.UseOpenJtalk) session = session with { Engine = "nit" };
            else if (previousNit) session = session with { Engine = "standard" };
            sessionDirty = true;
            if ((wasUtau && !UseUtau) || previousNit != UseOpenJtalk) StartSelectedPreparation();
            Changed(); Changed(nameof(UseUtau)); Changed(nameof(VoiceName));
        }
    }
    public bool UtauAvailable => selectedSpeech.Utau is not null;
    internal bool IsMobileSpeechBusy => speech.IsBusy;
    private string authenticationStatus = "未認証";
    public string AuthenticationStatus { get => authenticationStatus; private set { authenticationStatus = value; Changed(); } }
    private bool canAuthenticate = true;
    public bool CanAuthenticate { get => canAuthenticate; private set { canAuthenticate = value; Changed(); } }
    private string youtubeVideo = "";
    public string YouTubeVideo { get => youtubeVideo; set { youtubeVideo = value; sessionDirty = true; Changed(); } }
    public string YouTubeKey { get; set; } = "";
    private string twitchStatus = "未接続", youtubeStatus = "未接続", speechStatus = "待機中";
    public string TwitchStatus { get => twitchStatus; private set { twitchStatus = value; Changed(); } }
    public string YouTubeStatus { get => youtubeStatus; private set { youtubeStatus = value; Changed(); } }
    public string SpeechStatus { get => speechStatus; private set { speechStatus = value; Changed(); } }
    private string diagnosticCopyStatus = "";
    public string DiagnosticCopyStatus { get => diagnosticCopyStatus; private set { diagnosticCopyStatus = value; Changed(); } }
    public ICommand SpeakCommand { get; }
    public ICommand SkipCommand { get; }
    public ICommand ClearCommand { get; }
    public ICommand ConnectTwitchCommand { get; }
    public ICommand DisconnectTwitchCommand { get; }
    public ICommand ConnectYouTubeCommand { get; }
    public ICommand DisconnectYouTubeCommand { get; }
    public ICommand ConnectAnonymousCommand { get; }
    public ICommand AcceptVoiceTermsCommand { get; }

    public MainViewModel(ISpeechOutput output, string? voiceDirectory = null, IPcmOutput? pcm = null, ICredentialStore? credentialStore = null,
        bool mobileLifecycle = false, Func<bool, bool>? backgroundOperation = null, SpeechTextStore? textSettingsStore = null,
        IUtauPcmPostprocessor? utauPostprocessor = null, ISpeechOutput? openJtalkSpeech = null,
        PlaybackVolume? playbackVolume = null, PlaybackVolumeStore? playbackVolumeStore = null, string? sessionDirectory = null,
        TwitchAuthentication? twitchAuthentication = null)
    {
        var sharedVolume = playbackVolume ?? (pcm as VolumePcmOutput)?.Volume ?? new PlaybackVolume();
        if (output is ISpeechVolumeControl adjustable) adjustable.Volume = sharedVolume;
        if (pcm is not null && pcm is not VolumePcmOutput) pcm = new VolumePcmOutput(pcm, sharedVolume, ownsOutput: true);
        InitializePlaybackVolume(sharedVolume, playbackVolumeStore ?? (voiceDirectory is null ? new PlaybackVolumeStore(AppPaths.DataDirectory) : null));
        authentication = twitchAuthentication ?? new TwitchAuthentication();
        account = new TwitchAccount(authentication, credentialStore);
        voiceRoot = voiceDirectory ?? Tanayomi.Voicebanks.DefaultDirectory;
        selectedSpeech = new SelectableSpeech(output, pcm is null ? null : new UtauSpeech(pcm, utauPostprocessor),
            openJtalkSpeech, Post);
        Voicebanks.CollectionChanged += (_, _) => selectedSpeech.SetRandomBanks(Voicebanks.ToArray());
        AcceptVoiceTermsCommand = new ActionCommand(AcceptVoiceTerms);
        RefreshVoiceTerms();
        if (selectedSpeech.Utau is { } utau)
        {
            utau.SynthesizedMilliseconds += milliseconds => Post(new("合成", "UTAUの音声を生成しました。", milliseconds));
            utau.ProsodyFallback += error => Post(new("音声", "この発言には時間・抑揚の予測を適用できません。選択方式の通常合成へ戻します。", Diagnostic:
                DiagnosticEvent.Create(DiagnosticCode.UtauProsodyFallback, DiagnosticOperation.SpeechProcessing, DiagnosticRecovery.SelectedUtauFallback, error)));
            utau.ProsodyTimingFallback += error => Post(new("音声", "原音を保つため通常の速度で読み上げます。", Diagnostic:
                DiagnosticEvent.Create(DiagnosticCode.UtauProsodyTimingFallback, DiagnosticOperation.SpeechProcessing, DiagnosticRecovery.OrdinaryUtauTiming, error)));
        }
        speech = new SpeechQueue(selectedSpeech, output);
        speech.Notice += Post;
        textStore = textSettingsStore ?? (voiceDirectory is null ? new SpeechTextStore(AppPaths.DataDirectory) : null);
        textLoading = LoadTextSettingsAsync();
        utauModeStore = voiceDirectory is null ? new UtauModeStore(AppPaths.DataDirectory) : null;
        modeLoading = LoadUtauModeAsync();
        InitializeDrawnOutMode(voiceDirectory is null ? new OpenVoiceDrawnOutStore(AppPaths.DataDirectory) : null);
        mobile = new MobileConnectionLifecycle(mobileLifecycle, speech.SetSuspended, twitch.Stop, youtube.Stop, speech.Clear, backgroundOperation);
        if (mobileLifecycle) speech.ActivityChanged += SpeechActivityChanged;
        SpeakCommand = new ActionCommand(() =>
        {
            speech.Enqueue(ChatMessage.Manual(TestText));
            mobile.SetSpeechActive(speech.IsBusy);
        });
        SkipCommand = new ActionCommand(speech.Skip);
        ClearCommand = new ActionCommand(speech.Clear);
        ConnectTwitchCommand = new ActionCommand(() => RequestTwitch(false));
        ConnectAnonymousCommand = new ActionCommand(() => RequestTwitch(true));
        DisconnectTwitchCommand = new ActionCommand(() => { RememberRoute("off"); CancelRestore(); CancelAuthentication(); DisconnectTwitchRoute(); Post(new("Twitch", "切断しました")); });
        ConnectYouTubeCommand = new ActionCommand(() =>
        {
            var video = YouTubeVideo.Trim(); var key = YouTubeKey.Trim();
            mobile.ConnectYouTube(() => youtube.Start((ct, status) => RunConnectionAsync(DiagnosticOperation.YouTubeConnection,
                () => ChatSources.YouTubeAsync(video, key, m => { if (!ct.IsCancellationRequested) ReceiveChat(m); }, status, ct), ct),
                s => Post(new("YouTube", s)), ct => ConnectionEnded(false, ct)));
        });
        DisconnectYouTubeCommand = new ActionCommand(() => { DisconnectYouTubeRoute(); Post(new("YouTube", "切断しました")); });
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) => DrainUpdates();
        timer.Start();
        LoadSession(sessionDirectory ?? (voiceDirectory == null ? AppPaths.DataDirectory : null));
        preparation = PrepareUtauAsync();
        voicebanksLoading = LoadVoicebanksAsync();
        if (AppPaths.StorageWarning is { } warning) Post(new("保存先", warning, Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.StorageUnavailable, DiagnosticOperation.Storage, DiagnosticRecovery.WithoutStorage)));
        if (credentialStore is not null || session.Route != "off")
        {
            restoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            CanAuthenticate = false;
            restoreTask = RestoreAccountAsync(credentialStore is not null, restoreCancellation.Token);
        }
    }

    private async Task RestoreAccountAsync(bool hasStore, CancellationToken ct)
    {
        try
        {
            AuthenticationStatus = "保存した認証を確認しています…";
            await PrepareStartupSpeechAsync(ct).ConfigureAwait(false);
            var anonymous = session.Route == "anonymous";
            if (anonymous)
                await OnStartupUiAsync(() => ResumeTwitch(true), ct).ConfigureAwait(false);
            var saved = hasStore ? await account.RestoreAsync(ct).ConfigureAwait(false) : null;
            OnUi(() =>
            {
                if (session.Route == "account" && (saved == null || saved.Identity.UserId != session.AccountId))
                {
                    AuthenticationStatus = "前回の接続にはTwitchの再認証が必要です。";
                    return;
                }
                if (saved is not null) { ApplyAuthorization(saved); AuthenticationStatus = $"{saved.Identity.Login} の認証を復元しました。"; }
                else AuthenticationStatus = anonymous ? "認証なしで接続します。" : "保存された認証はありません。";
                if (session.Route == "account") ResumeTwitch(false);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (SourceConfigurationException ex)
        {
            Record(DiagnosticCode.CredentialReadFailed, DiagnosticOperation.CredentialRestore, DiagnosticRecovery.Reauthenticate, ex);
            OnUi(() => AuthenticationStatus = ex.Message, ct);
        }
        catch (Exception ex)
        {
            Record(DiagnosticCode.CredentialReadFailed, DiagnosticOperation.CredentialRestore, DiagnosticRecovery.RetryOperation, ex);
            OnUi(() => AuthenticationStatus = "保存した認証を確認できませんでした。接続環境を確認して再起動するか、認証なしで接続できます。", ct);
        }
        finally { OnUi(() => CanAuthenticate = true, ct); }
    }

    private void CancelRestore()
    {
        restoreCancellation?.Cancel();
        CanAuthenticate = true;
    }

    public Task ForgetTwitchAsync()
    {
        if (disposed) return Task.CompletedTask;
        CancelRestore(); CancelAuthentication();
        RememberRoute("off");
        var version = DisconnectTwitchRoute();
        currentAuthorization = null; TwitchToken = ""; TwitchLogin = "";
        CanAuthenticate = false;
        forgetTask = ForgetCoreAsync(version);
        return forgetTask;
    }

    private async Task ForgetCoreAsync(long version)
    {
        try
        {
            await Task.WhenAll(restoreTask, authenticationTask, sessionMonitor).ConfigureAwait(false);
            await account.ForgetAsync(lifetime.Token).ConfigureAwait(false);
            OnUi(() => { if (mobile.IsCurrent(version)) { AuthenticationStatus = "保存した認証を削除しました。"; Post(new("Twitch", "未接続")); } }, lifetime.Token);
        }
        catch (Exception ex)
        {
            Record(DiagnosticCode.CredentialDeleteFailed, DiagnosticOperation.CredentialDelete, DiagnosticRecovery.RetryOperation, ex);
            OnUi(() => AuthenticationStatus = "保存した認証を削除できませんでした。OSの認証保管庫または保存先のアクセス権を確認してください。", lifetime.Token);
        }
        finally { OnUi(() => CanAuthenticate = true, lifetime.Token); }
    }

    private async Task<string> SaveAccountAsync(TwitchAuthorization authorization, CancellationToken ct)
    {
        try { await account.SaveAsync(authorization, ct).ConfigureAwait(false); return "認証を保存しました。次回は自動で復元します。"; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Record(DiagnosticCode.CredentialWriteFailed, DiagnosticOperation.CredentialSave, DiagnosticRecovery.SessionOnly, ex);
            return "今回の認証は使えますが、保存できませんでした。次回は認証が必要です。";
        }
    }

    private async Task PrepareUtauAsync()
    {
        if (selectedSpeech.Utau is not { } utau) return;
        try { await utau.PrepareAsync(lifetime.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex) { Post(new("音源", $"UTAU辞書の準備に失敗しました（{ex.GetType().Name}）。標準音声は利用できます。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.VoicePreparationFailed, DiagnosticOperation.VoicePreparation, DiagnosticRecovery.StandardVoice, ex))); }
    }

    private void StartSelectedPreparation()
    {
        if (disposed) return;
        var previous = bankPreparation;
        var oldCancellation = bankPreparationCancellation;
        try { oldCancellation?.Cancel(); }
        catch (Exception error) { ReportBankPreparationFailure(error); }
        var utau = selectedSpeech.Utau;
        var bank = !RandomVoiceEnabled && UseUtau ? SelectedVoicebank : null;
        var bundled = !RandomVoiceEnabled && UseOpenJtalk && selectedSpeech.BundledVoiceAllowed && selectedSpeech.OpenJtalk is ISpeechPreparation;
        var cancellation = (bank is not null && utau is not null) || bundled
            ? CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token) : null;
        bankPreparationCancellation = cancellation;
        var token = cancellation?.Token ?? lifetime.Token;
        // File metadata and calibration stay off the UI thread. The latest task
        // also owns completion of older cancelled preparations and their CTSs.
        bankPreparation = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (bank is not null && utau is not null)
                    await utau.PrepareBankAsync(bank, token).ConfigureAwait(false);
                else if (bundled) await selectedSpeech.PrepareBundledVoiceAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { ReportBankPreparationFailure(error); }
            finally { oldCancellation?.Dispose(); }
        });
    }

    private void ReportBankPreparationFailure(Exception error) => Post(new("音源",
        "音源の事前準備に失敗しました。読み上げ時に再試行し、利用できない場合は標準音声へ切り替えます。",
        Diagnostic: DiagnosticEvent.Create(DiagnosticCode.VoicePreparationFailed,
            DiagnosticOperation.VoicePreparation, DiagnosticRecovery.StandardVoice, error)));

    public Task AuthenticateTwitchAsync(Func<Uri, Task<bool>> openBrowser)
    {
        if (disposed || !CanAuthenticate) return authenticationTask;
        var previous = Task.WhenAll(restoreTask, forgetTask, authenticationTask, sessionMonitor);
        CancelRestore();
        CancelAuthentication();
        mobile.SetSpeechActive(speech.IsBusy);
        var version = mobile.BeginAuthentication();
        var cancellation = authenticationCancellation = new CancellationTokenSource();
        CanAuthenticate = false;
        authenticationTask = AuthenticateCoreAsync(openBrowser, cancellation, previous, version);
        return authenticationTask;
    }

    public Task ConnectWithSavedAccountAsync(Func<Uri, Task<bool>> openBrowser)
    {
        if (string.IsNullOrWhiteSpace(TwitchToken)) return AuthenticateTwitchAsync(openBrowser);
        ConnectTwitchCommand.Execute(null);
        return Task.CompletedTask;
    }

    private async Task AuthenticateCoreAsync(Func<Uri, Task<bool>> openBrowser, CancellationTokenSource cancellation, Task previous, long version)
    {
        try
        {
            AuthenticationStatus = "Twitchの認証画面を準備しています…";
            await previous.ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            var device = await authentication.StartAsync(TwitchClientId, cancellation.Token).ConfigureAwait(false);
            OnUi(() => AuthenticationStatus = $"ブラウザで許可してください。入力を求められた場合のコード：{device.UserCode}", cancellation.Token);
            // Launcher is called on the UI thread, but shutdown must not wait for its callback to run.
            var launched = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            OnUi(async () => { try { launched.TrySetResult(await openBrowser(device.VerificationUri)); } catch { launched.TrySetResult(false); } }, cancellation.Token);
            if (!await launched.Task.WaitAsync(cancellation.Token).ConfigureAwait(false))
                OnUi(() => AuthenticationStatus = $"ブラウザで twitch.tv/activate を開き、コード {device.UserCode} を入力してください。", cancellation.Token);
            var authorization = await authentication.CompleteAsync(device, cancellation.Token).ConfigureAwait(false);
            var savedStatus = await SaveAccountAsync(authorization, cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
            OnUi(() =>
            {
                var channel = ""; var both = false;
                mobile.CompleteAuthentication(version, () =>
                {
                    ApplyAuthorization(authorization);
                    AuthenticationStatus = $"{authorization.Identity.Login} として認証済み。{savedStatus}";
                    anonymousConnection = false;
                    channel = TwitchChannel; both = UseEventSub; RememberRoute("account", channel);
                }, () => StartTwitch(channel, both, false));
            }, cancellation.Token);
            sessionMonitor = MonitorSessionAsync(authorization, cancellation.Token);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (SourceConfigurationException ex)
        {
            Record(DiagnosticCode.AuthenticationFailed, DiagnosticOperation.TwitchAuthentication, DiagnosticRecovery.Reauthenticate, ex);
            OnUi(() => AuthenticationStatus = ex.Message, cancellation.Token);
        }
        catch (Exception ex)
        {
            Record(DiagnosticCode.AuthenticationFailed, DiagnosticOperation.TwitchAuthentication, DiagnosticRecovery.RetryOperation, ex);
            OnUi(() => AuthenticationStatus = $"認証を完了できませんでした（{ex.GetType().Name}）。もう一度認証できます。", cancellation.Token);
        }
        finally { OnUi(() => { if (ReferenceEquals(authenticationCancellation, cancellation)) CanAuthenticate = true; }, cancellation.Token); }
    }

    private static void OnUi(Action action, CancellationToken ct)
    {
        void Apply() { if (!ct.IsCancellationRequested) action(); }
        if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply);
    }

    private void ApplyAuthorization(TwitchAuthorization authorization)
    {
        currentAuthorization = authorization;
        TwitchToken = authorization.AccessToken;
        TwitchLogin = authorization.Identity.Login;
        TwitchClientId = authorization.Identity.ClientId;
        Changed(nameof(TwitchClientId));
        if (string.IsNullOrWhiteSpace(TwitchChannel)) TwitchChannel = authorization.Identity.Login;
    }

    private async Task MonitorSessionAsync(TwitchAuthorization authorization, CancellationToken ct)
    {
        var retry = false;
        var refreshing = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var untilRefresh = authorization.Identity.ExpiresAt - DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
                var seconds = retry ? 30 : Math.Clamp(untilRefresh.TotalSeconds, 5, 45 * 60);
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                if (DateTimeOffset.UtcNow >= authorization.Identity.ExpiresAt - TimeSpan.FromMinutes(2))
                {
                    refreshing = true;
                    authorization = await authentication.RefreshAsync(authorization.RefreshToken, authorization.Identity.ClientId, ct).ConfigureAwait(false);
                    var savedStatus = await SaveAccountAsync(authorization, ct).ConfigureAwait(false);
                    refreshing = false;
                    Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) { ApplyAuthorization(authorization); AuthenticationStatus = savedStatus; if (!anonymousConnection) mobile.RestartTwitch(); } });
                }
                else await authentication.ValidateAsync(authorization.AccessToken, ct).ConfigureAwait(false);
                retry = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                if (refreshing) await ForgetUncertainCredentialAsync().ConfigureAwait(false);
                break;
            }
            catch (SourceConfigurationException ex)
            {
                Record(DiagnosticCode.AuthenticationFailed, refreshing ? DiagnosticOperation.SessionRefresh : DiagnosticOperation.SessionValidation,
                    DiagnosticRecovery.Reauthenticate, ex);
                await ForgetUncertainCredentialAsync().ConfigureAwait(false);
                Dispatcher.UIThread.Post(() => { if (!ct.IsCancellationRequested) { DisconnectTwitchRoute(); currentAuthorization = null; TwitchToken = ""; AuthenticationStatus = ex.Message; Post(new("Twitch", "再認証待ち")); } });
                break;
            }
            catch (Exception ex)
            {
                if (DateTimeOffset.UtcNow >= authorization.Identity.ExpiresAt)
                {
                    Record(DiagnosticCode.AuthenticationExpired, DiagnosticOperation.SessionValidation, DiagnosticRecovery.Reauthenticate, ex);
                    OnUi(() => { DisconnectTwitchRoute(); currentAuthorization = null; TwitchToken = ""; AuthenticationStatus = "認証の有効期限が切れました。もう一度認証してください。"; Post(new("Twitch", "再認証待ち")); }, ct);
                    break;
                }
                retry = true;
                Record(DiagnosticCode.AuthenticationFailed, DiagnosticOperation.SessionValidation, DiagnosticRecovery.Retrying, ex);
                Post(new("認証", "認証の定期確認に失敗しました。30秒後に再試行します。"));
            }
        }
    }

    private async Task ForgetUncertainCredentialAsync()
    {
        try { await account.ForgetAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { Post(new("認証", "無効になった保存認証を削除できませんでした。認証管理の削除操作で再試行できます。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.CredentialDeleteFailed, DiagnosticOperation.CredentialDelete, DiagnosticRecovery.RetryOperation, ex))); }
    }

    private void CancelAuthentication()
    {
        var previous = authenticationCancellation;
        authenticationCancellation = null;
        previous?.Cancel();
        if (previous is not null) _ = Task.WhenAll(authenticationTask, sessionMonitor).ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        CanAuthenticate = true;
    }

    private void RequestTwitch(bool anonymous)
    {
        if (disposed) return;
        CancelRestore();
        if (anonymous) CancelAuthentication();
        anonymousConnection = anonymous;
        var channel = TwitchChannel; var both = !anonymous && UseEventSub;
        RememberRoute(anonymous ? "anonymous" : "account", channel);
        mobile.ConnectTwitch(() => StartTwitch(channel, both, anonymous));
    }

    private void StartTwitch(string channel, bool both, bool anonymous)
    {
        if (anonymous)
        {
            twitch.Start((ct, status) => RunConnectionAsync(DiagnosticOperation.TwitchIrc,
                () => ReceiveIrc(channel, "", "", m => { if (!ct.IsCancellationRequested) ReceiveChat(m); },
                    status, ct, anonymous: true), ct), s => Post(new("Twitch", s)), ct => ConnectionEnded(true, ct));
            return;
        }
        if (currentAuthorization is { } previous && TwitchToken != previous.AccessToken)
        {
            CancelAuthentication();
            currentAuthorization = null;
        }
        if (authenticationCancellation is null && currentAuthorization is { } authorization && TwitchToken == authorization.AccessToken)
        {
            authenticationCancellation = new CancellationTokenSource();
            sessionMonitor = MonitorSessionAsync(authorization, authenticationCancellation.Token);
        }
        var token = TwitchToken;
        twitch.Start((ct, status) => RunConnectionAsync(DiagnosticOperation.TwitchConnection, async () =>
        {
            token = TwitchAuthentication.NormalizeToken(token);
            var identity = await authentication.ValidateAsync(token, ct);
            var target = string.IsNullOrWhiteSpace(channel) ? identity.Login : channel;
            var gate = new object();
            var ircStatus = "接続待ち";
            var eventSubStatus = both ? "接続待ち" : "使用しない";
            string Summary() => both ? $"IRC: {ircStatus}\nEventSub: {eventSubStatus}" : ircStatus;
            void Update(bool irc, string message)
            {
                lock (gate) { if (irc) ircStatus = message; else eventSubStatus = message; status(Summary()); }
            }
            Task Irc(CancellationToken t) => RunConnectionAsync(DiagnosticOperation.TwitchIrc, () =>
            {
                if (!identity.Scopes.Contains("chat:read")) throw new SourceConfigurationException("chat:read権限がありません。再認証してください。");
                return ReceiveIrc(target, identity.Login, token, m => { if (!t.IsCancellationRequested) ReceiveChat(m); }, s => Update(true, s), t);
            }, t);
            Task EventSub(CancellationToken t) => RunConnectionAsync(DiagnosticOperation.TwitchEventSub, () =>
            {
                if (!identity.Scopes.Contains("user:read:chat")) throw new SourceConfigurationException("user:read:chat権限がありません。再認証してください。");
                return TwitchEventSub.RunAsync(target, token, identity.ClientId, m => { if (!t.IsCancellationRequested) ReceiveChat(m); }, s => Update(false, s), t);
            }, t);
            using var connected = CancellationTokenSource.CreateLinkedTokenSource(ct);
            // Manual tokens are also validated periodically while their transports remain connected.
            var routes = Task.WhenAll(ChatSources.SuperviseAsync(Irc, s => Update(true, s), connected.Token),
                both ? ChatSources.SuperviseAsync(EventSub, s => Update(false, s), connected.Token) : Task.CompletedTask);
            var validation = ValidateWhileConnectedAsync(token, identity.ExpiresAt, connected.Token);
            try
            {
                var finished = await Task.WhenAny(routes, validation);
                await finished;
                if (!ct.IsCancellationRequested) throw new SourceConfigurationException(Summary());
            }
            finally
            {
                connected.Cancel();
                try { await Task.WhenAll(routes, validation); }
                catch (OperationCanceledException) when (connected.IsCancellationRequested) { }
            }
        }, ct), s => Post(new("Twitch", s)), ct => ConnectionEnded(true, ct));
    }

    private void ConnectionEnded(bool isTwitch, CancellationToken ct) => OnUi(() =>
    {
        if (isTwitch) { CancelAuthentication(); DisconnectTwitchRoute(); }
        else DisconnectYouTubeRoute();
    }, ct);

    private async Task RunConnectionAsync(DiagnosticOperation operation, Func<Task> connect, CancellationToken ct)
    {
        try { await connect().ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var code = error is SourceConfigurationException ? DiagnosticCode.ConnectionRejected :
                error is SourceEndedException ? DiagnosticCode.SourceEnded : DiagnosticCode.ConnectionInterrupted;
            var recovery = error is SourceConfigurationException ? DiagnosticRecovery.CheckConnectionSettings :
                error is SourceEndedException ? DiagnosticRecovery.SourceFinished : DiagnosticRecovery.Retrying;
            Record(code, operation, recovery, error);
            throw;
        }
    }

    private long DisconnectTwitchRoute()
    {
        // A queued UI activity notification may not have run yet. Refresh before
        // dropping the final connection so its remaining audio keeps the service.
        mobile.SetSpeechActive(speech.IsBusy);
        return mobile.DisconnectTwitch();
    }
    private void DisconnectYouTubeRoute()
    {
        mobile.SetSpeechActive(speech.IsBusy);
        mobile.DisconnectYouTube();
    }

    private async Task ValidateWhileConnectedAsync(string token, DateTimeOffset expiresAt, CancellationToken ct)
    {
        var delay = TimeSpan.FromMinutes(45);
        while (true)
        {
            var remaining = expiresAt - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero) throw new SourceConfigurationException("認証の有効期限が切れました。再認証してください。");
            await Task.Delay(remaining < delay ? remaining : delay, ct);
            if (DateTimeOffset.UtcNow >= expiresAt) throw new SourceConfigurationException("認証の有効期限が切れました。再認証してください。");
            try { expiresAt = (await authentication.ValidateAsync(token, ct)).ExpiresAt; delay = TimeSpan.FromMinutes(45); }
            catch (HttpRequestException ex)
            {
                delay = TimeSpan.FromSeconds(30);
                Post(new("認証", "認証の定期確認を再試行します。", Diagnostic:
                    DiagnosticEvent.Create(DiagnosticCode.AuthenticationFailed, DiagnosticOperation.SessionValidation, DiagnosticRecovery.Retrying, ex)));
            }
            catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
            {
                delay = TimeSpan.FromSeconds(30);
                Record(DiagnosticCode.AuthenticationFailed, DiagnosticOperation.SessionValidation, DiagnosticRecovery.Retrying, ex);
            }
        }
    }

    private async Task LoadVoicebanksAsync()
    {
        var ct = lifetime.Token;
        try
        {
            var banks = await Task.Run(() => Tanayomi.Voicebanks.LoadInstalledAsync(voiceRoot, s => Post(new("音源", s)), diagnostics.Record)).ConfigureAwait(false);
            OnUi(() =>
            {
                foreach (var bank in banks) Voicebanks.Add(bank);
                var preferred = Voicebanks.FirstOrDefault(b => Path.GetFileName(b.Directory) == rememberedBank);
                SelectedVoicebank = preferred ?? (string.IsNullOrEmpty(rememberedBank) ? Voicebanks.FirstOrDefault() : null);
                if (preferred == null && !string.IsNullOrEmpty(rememberedBank) && UseUtau)
                {
                    selectedSpeech.UseUtau = false;
                    Changed(nameof(UseUtau)); Changed(nameof(VoiceName));
                    Post(new("音源", "前回のUTAU音源が見つからないため標準音声を使います。"));
                }
                Post(new("音源", $"UTAU音源{banks.Count}件を読み込みました。"));
            }, ct);
        }
        catch (Exception ex) { Post(new("音源", $"音源を読み込めませんでした（{ex.GetType().Name}）。標準音声は引き続き使えます。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.VoicebankLoadFailed, DiagnosticOperation.VoicebankLoad, DiagnosticRecovery.StandardVoice, ex))); }
    }

    public async Task ImportVoicebankAsync(Stream zip)
    {
        try
        {
            var bank = await Task.Run(() => Tanayomi.Voicebanks.ImportZipAsync(zip, voiceRoot));
            Voicebanks.Add(bank);
            Post(new("音源", $"{bank.Name}を追加しました。{bank.Entries.Count}音を確認しました。"));
            if (bank.PitchMappingsRejected)
                Post(new("音源", "音階対応表を読み込めないため、接頭・接尾辞による音階選択を使わず継続します。", Diagnostic:
                    new DiagnosticEvent(DiagnosticCode.VoicebankPitchMappingRejected, DiagnosticOperation.VoicebankImport,
                        DiagnosticRecovery.AvailableFeatures, bank.PitchMappingException)));
        }
        catch (Exception ex) { Post(new("音源", $"追加に失敗しました（{ex.GetType().Name}）。ZIPの構成を確認してください。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.VoicebankImportFailed, DiagnosticOperation.VoicebankImport, DiagnosticRecovery.RetryOperation, ex))); }
    }

    private void Record(DiagnosticCode code, DiagnosticOperation operation, DiagnosticRecovery recovery, Exception? error = null) =>
        diagnostics.Record(DiagnosticEvent.Create(code, operation, recovery, error));

    private void Post(AppNotice notice)
    {
        // Capture before the UI channel can drop old notices. No notice text or
        // timing, including received/read-out chat, is copied into diagnostics.
        if (notice.Diagnostic is { } diagnostic) diagnostics.Record(diagnostic);
        updates.Writer.TryWrite(notice);
    }

    public async Task CopyDiagnosticsAsync(Func<string, Task>? writeClipboard)
    {
        var copied = await diagnostics.CopyAsync(writeClipboard).ConfigureAwait(false);
        OnUi(() => DiagnosticCopyStatus = copied ? "診断をコピーしました。" :
            "診断をコピーできませんでした。クリップボードを確認して再試行できます。", lifetime.Token);
    }

    public void ReportUiError(DiagnosticOperation operation, Exception error)
    {
        Post(new("画面", "操作を完了できませんでした。もう一度操作できます。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.UiOperationFailed, operation, DiagnosticRecovery.RetryOperation, error)));
    }
    public void SetMobileForeground(bool active) { if (!disposed) mobile.SetForeground(active); }
    internal event Action? MobileSpeechIdle;
    private void SpeechActivityChanged() => Dispatcher.UIThread.Post(() =>
    {
        if (disposed) return;
        mobile.SetSpeechActive(speech.IsBusy);
        if (!speech.IsBusy) MobileSpeechIdle?.Invoke();
    });
    public void ResumeMobileOperations() { if (!disposed) mobile.Resume(); }
    public void InterruptMobileOperations()
    {
        if (disposed) return;
        mobile.Interrupt();
        Post(new("モバイル", "OSの中断により受信と読み上げを停止しました。復帰後、接続していた経路だけ再接続します。", Diagnostic:
            DiagnosticEvent.Create(DiagnosticCode.MobileInterrupted, DiagnosticOperation.MobileLifecycle, DiagnosticRecovery.ReconnectOnForeground)));
    }
    public void StopMobileOperations()
    {
        if (disposed) return;
        RememberRoute("off"); CancelRestore(); CancelAuthentication(); mobile.Stop();
        Post(new("Twitch", "切断しました"));
        Post(new("YouTube", "切断しました"));
        Post(new("停止", "通知から接続と読み上げを停止しました。"));
    }
    public void ReportMobileStatus(string message) => Post(new("モバイル", message));
    public void ReportMobileStatus(string message, DiagnosticEvent diagnostic) => Post(new("モバイル", message, Diagnostic: diagnostic));
    public void ReportInitializationFailure(DiagnosticEvent diagnostic)
    {
        var message = diagnostic.Operation switch
        {
            DiagnosticOperation.OpenJtalkInitialization => "一部の同梱音声を初期化できませんでした。他の音声を利用できます。",
            DiagnosticOperation.StandardSpeechInitialization => "標準音声を初期化できませんでした。利用可能な音声・受信機能を続けます。",
            DiagnosticOperation.PcmInitialization => "UTAUの再生機能を初期化できませんでした。標準音声は利用できます。",
            DiagnosticOperation.VoicePreparation when diagnostic.Code == DiagnosticCode.UtauPostprocessorInitializationFailed =>
                "UTAUの音程処理を初期化できませんでした。従来の合成方式で続けます。",
            DiagnosticOperation.CredentialStoreInitialization => "認証の保存機能を初期化できませんでした。今回の接続は利用できますが、次回は再認証が必要です。",
            _ => "一部の機能を初期化できませんでした。利用可能な機能を続けます。"
        };
        Post(new("初期化", message, Diagnostic: diagnostic));
    }
    public void ReportFileError(Exception error) => Post(new("音源", $"ファイルを開けませんでした（{error.GetType().Name}）。別のファイルを選択できます。", Diagnostic:
        DiagnosticEvent.Create(DiagnosticCode.VoicebankImportFailed, DiagnosticOperation.VoicebankImport, DiagnosticRecovery.RetryOperation, error)));
    private void DrainUpdates()
    {
        if (sessionDirty) { sessionDirty = false; SaveSession(); }
        DrainAuthors();
        for (var i = 0; i < 32 && updates.Reader.TryRead(out var notice); i++)
        {
            if (notice.Kind == "抽選音声")
            {
                if (RandomVoiceEnabled)
                {
                    RandomVoiceDetails = notice.Text;
                    Changed(nameof(RandomVoiceDetails));
                }
                continue;
            }
            if (notice.Kind == "Twitch") TwitchStatus = notice.Text;
            else if (notice.Kind == "YouTube") YouTubeStatus = notice.Text;
            else if (notice.Kind is "読み上げ" or "完了" or "停止" or "省略" or "エラー" or "警告") SpeechStatus = notice.Text;
            var timing = notice.Milliseconds is { } ms ? $" · {ms:F0} ms" : "";
            var code = notice.Diagnostic is { } diagnostic ? $" [{DiagnosticBuffer.CodeId(diagnostic.Code)}]" : "";
            var entry = $"{DateTime.Now:HH:mm:ss}  {notice.Kind}{code}  {notice.Text}{timing}";
            if (Activities.Count == 60) Activities.RemoveAt(Activities.Count - 1);
            Activities.Insert(0, entry);
        }
    }

    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        SaveSession();
        disposed = true;
        Exception? failure = null;
        void Cleanup(Action action) { try { action(); } catch (Exception error) { failure ??= error; } }
        async Task CleanupAsync(Func<Task> action)
        { try { await action().ConfigureAwait(false); } catch (Exception error) { failure ??= error; } }
        Cleanup(timer.Stop);
        Cleanup(lifetime.Cancel);
        Cleanup(CancelRestore);
        Cleanup(CancelAuthentication);
        Cleanup(mobile.Dispose);
        await CleanupAsync(() => Task.WhenAll(twitch.Completion, youtube.Completion, authenticationTask,
            sessionMonitor, preparation, bankPreparation, voicebanksLoading, restoreTask, forgetTask, textLoading, textSaving, modeLoading, modeSaving, volumeLoading, volumeSaving, drawnOutLoading, drawnOutSaving)).ConfigureAwait(false);
        await CleanupAsync(() => speech.DisposeAsync().AsTask()).ConfigureAwait(false);
        Cleanup(authentication.Dispose);
        Cleanup(lifetime.Dispose);
        Cleanup(() => restoreCancellation?.Dispose());
        Cleanup(() => bankPreparationCancellation?.Dispose());
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private sealed class SelectableSpeech(ISpeechOutput standard, UtauSpeech? utau, ISpeechOutput? openJtalk,
        Action<AppNotice> post) : ISpeechOutput
    {
        private int mode;
        private bool usedCustom;
        private readonly object bundledVoiceGate = new();
        private bool bundledVoiceAllowed = true;
        private bool randomVoiceEnabled;
        private Voicebank[] randomBanks = [];
        private OpenJtalkVoiceChoice[] randomBundledVoices = [];
        public bool RandomVoiceEnabled { get => Volatile.Read(ref randomVoiceEnabled); set => Volatile.Write(ref randomVoiceEnabled, value); }
        public Func<int, int> ChooseRandomVoice { private get; set; } = Random.Shared.Next;
        public void SetRandomBanks(Voicebank[] banks) => Volatile.Write(ref randomBanks, banks);
        public void SetRandomBundledVoices(OpenJtalkVoiceChoice[] voices) => Volatile.Write(ref randomBundledVoices, voices);
        public bool UseUtau { get => Volatile.Read(ref mode) == 1; set { if (value) Interlocked.Exchange(ref mode, 1); else Interlocked.CompareExchange(ref mode, 0, 1); } }
        public bool UseOpenJtalk { get => Volatile.Read(ref mode) == 3; set { if (value) Interlocked.Exchange(ref mode, 3); else Interlocked.CompareExchange(ref mode, 0, 3); } }
        public UtauSpeech? Utau => utau;
        public ISpeechOutput? OpenJtalk => openJtalk;
        public string StandardName => standard.Name;
        public bool CanFallback => usedCustom;
        public bool BundledVoiceAllowed { get { lock (bundledVoiceGate) return bundledVoiceAllowed; } }
        public string? BundledVoiceId { get { lock (bundledVoiceGate) return (openJtalk as IOpenJtalkVoiceSelection)?.VoiceId; } }
        public string Name => Volatile.Read(ref mode) switch { 1 => "UTAU", 3 when BundledVoiceAllowed => openJtalk?.Name ?? "NIT日本語 (Open JTalk)", _ => standard.Name };

        public void SetBundledVoice(OpenJtalkVoiceChoice? voice, bool allowed)
        {
            lock (bundledVoiceGate)
            {
                if (voice is not null && openJtalk is IOpenJtalkVoiceSelection selection && selection.VoiceId != voice.Id)
                    selection.VoiceId = voice.Id;
                bundledVoiceAllowed = allowed;
            }
        }

        public Task PrepareBundledVoiceAsync(CancellationToken ct)
        {
            lock (bundledVoiceGate)
                return !RandomVoiceEnabled && UseOpenJtalk && bundledVoiceAllowed && openJtalk is ISpeechPreparation prepare
                    ? prepare.PrepareAsync(ct) : Task.CompletedTask;
        }

        public Task SpeakAsync(string text, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (RandomVoiceEnabled) return SpeakRandomAsync(text, ct);
            var current = Volatile.Read(ref mode);
            if (current == 3)
            {
                lock (bundledVoiceGate)
                {
                    usedCustom = bundledVoiceAllowed;
                    if (usedCustom) return (openJtalk ?? throw new PlatformNotSupportedException()).SpeakAsync(text, ct);
                }
            }
            usedCustom = current == 1;
            return usedCustom ? (utau ?? throw new PlatformNotSupportedException()).SpeakAsync(text, ct) : standard.SpeakAsync(text, ct);
        }

        private Task SpeakRandomAsync(string text, CancellationToken ct)
        {
            var banks = utau is null ? Array.Empty<Voicebank>() : Volatile.Read(ref randomBanks);
            var bundled = Volatile.Read(ref randomBundledVoices);
            var index = ChooseRandomVoice(1 + banks.Length + bundled.Length);
            usedCustom = index != 0;
            if (index == 0)
            {
                post(new("抽選音声", standard.Name));
                return standard.SpeakAsync(text, ct);
            }
            if (--index < banks.Length)
            {
                post(new("抽選音声", $"UTAU · {banks[index].Name}"));
                return utau!.SpeakWithVoicebankAsync(text, banks[index], ct);
            }

            var voice = bundled[index - banks.Length];
            post(new("抽選音声", string.Join("\n", new[] { voice.Name, voice.Credit, voice.Usage, voice.TermsUrl }
                .Where(value => !string.IsNullOrWhiteSpace(value)))));

            lock (bundledVoiceGate)
            {
                var selector = (IOpenJtalkVoiceSelection)openJtalk!;
                var previous = selector.VoiceId;
                selector.VoiceId = voice.Id;
                // The bundled outputs snapshot the selected voice at dispatch, before awaiting native work.
                try { return openJtalk!.SpeakAsync(text, ct); }
                finally { selector.VoiceId = previous; }
            }
        }
        // The queue owns standard as its fallback; dispose it only once there.
        public async ValueTask DisposeAsync()
        {
            try { if (openJtalk is not null) await openJtalk.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                if (utau is not null) await utau.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private sealed class ActionCommand(Action action) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => action();
    }

    private sealed class SourceSlot
    {
        private CancellationTokenSource? active;
        public Task Completion { get; private set; } = Task.CompletedTask;
        public void Stop()
        {
            if (active is not { } old) return;
            active = null;
            old.Cancel();
            _ = Completion.ContinueWith(_ => old.Dispose(), TaskScheduler.Default);
        }
        public void Start(Func<CancellationToken, Action<string>, Task> connect, Action<string> status, Action<CancellationToken>? ended = null)
        {
            Stop();
            var previous = Completion;
            var cancellation = active = new CancellationTokenSource();
            Completion = Task.Run(async () =>
            {
                await previous.ConfigureAwait(false);
                await ChatSources.SuperviseAsync(ct => connect(ct, s => { if (!cancellation.IsCancellationRequested) status(s); }),
                    s => { if (!cancellation.IsCancellationRequested) status(s); }, cancellation.Token).ConfigureAwait(false);
                if (!cancellation.IsCancellationRequested) ended?.Invoke(cancellation.Token);
            });
        }
    }
}

