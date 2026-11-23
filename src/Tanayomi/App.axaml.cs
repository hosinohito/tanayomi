using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;

namespace Tanayomi;

public partial class App : Application
{
    public static Func<ISpeechOutput>? CreateSpeech { get; set; }
    public static Func<IPcmOutput>? CreatePcm { get; set; }
    public static Func<IUtauPcmPostprocessor?>? CreateUtauPostprocessor { get; set; }
    public static Func<IPcmOutput, ISpeechOutput?>? CreateOpenJtalkSpeech { get; set; }
    public static Func<ICredentialStore>? CreateCredentialStore { get; set; }
    public static Func<bool, bool>? ConfigureMobileBackground { get; set; }
    private static MainViewModel? mobileModel;
    private static int mobileForeground = 1;
    public static bool IsMobileForeground => Volatile.Read(ref mobileForeground) != 0;
    public static bool IsMobileSpeechBusy => mobileModel?.IsMobileSpeechBusy == true;
    public static event Action? MobileSpeechIdle;

    public static void SetMobileForeground(bool active)
    {
        Volatile.Write(ref mobileForeground, active ? 1 : 0);
        WithMobile(model => model.SetMobileForeground(active));
    }
    public static void InterruptMobileOperations() => WithMobile(model => model.InterruptMobileOperations());
    public static void ResumeMobileOperations() => WithMobile(model => model.ResumeMobileOperations());
    public static void StopMobileOperations() => WithMobile(model => model.StopMobileOperations());
    public static void ReportMobileStatus(string message) => WithMobile(model => model.ReportMobileStatus(message));
    public static void ReportMobileStatus(string message, DiagnosticEvent diagnostic) =>
        WithMobile(model => model.ReportMobileStatus(message, diagnostic));
    private static void WithMobile(Action<MainViewModel> action)
    {
        void ReportFailure(Exception error)
        {
            try
            {
                mobileModel?.ReportMobileStatus("動作状態を更新できませんでした。アプリへ戻って接続状態を確認してください。",
                    DiagnosticEvent.Create(DiagnosticCode.MobileStateFailed, DiagnosticOperation.MobileLifecycle,
                        DiagnosticRecovery.ReconnectOnForeground, error));
            }
            catch (Exception) { /* The view may already have been disposed. */ }
        }
        void Apply()
        {
            try { if (mobileModel is { } model) action(model); }
            catch (Exception error) { ReportFailure(error); }
        }
        try { if (Dispatcher.UIThread.CheckAccess()) Apply(); else Dispatcher.UIThread.Post(Apply); }
        catch (Exception error) { ReportFailure(error); }
    }
    private static bool SetMobileBackground(bool wanted)
    {
        try { return ConfigureMobileBackground?.Invoke(wanted) ?? true; }
        catch (Exception error)
        {
            ReportMobileStatus("バックグラウンド動作を更新できません。アプリを前面へ戻して接続状態を確認してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileBackgroundUnavailable,
                    wanted ? DiagnosticOperation.MobileBackgroundStart : DiagnosticOperation.MobileBackgroundStop,
                    DiagnosticRecovery.ReconnectOnForeground, error));
            return false;
        }
    }
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime and not ISingleViewApplicationLifetime)
        { base.OnFrameworkInitializationCompleted(); return; }
        var services = InitializeServices();
        var isMobile = ApplicationLifetime is ISingleViewApplicationLifetime;
        var model = new MainViewModel(services.Speech, pcm: services.Pcm, credentialStore: services.Credentials,
            mobileLifecycle: isMobile, backgroundOperation: isMobile ? SetMobileBackground : null,
            utauPostprocessor: services.UtauPostprocessor, openJtalkSpeech: services.OpenJtalk, playbackVolume: services.Volume);
        foreach (var diagnostic in services.Diagnostics) model.ReportInitializationFailure(diagnostic);
        if (isMobile)
        {
            mobileModel = model;
            model.MobileSpeechIdle += () => MobileSpeechIdle?.Invoke();
            model.SetMobileForeground(IsMobileForeground);
        }
        var view = new MainView { DataContext = model };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new Window
            {
                Title = "たなよみ", Width = 1060, Height = 860, MinWidth = 420, MinHeight = 560,
                Content = view, Background = Avalonia.Media.Brush.Parse("#0D1522")
            };
            desktop.Exit += (_, _) => Shutdown(model);
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime mobile) mobile.MainView = view;
        base.OnFrameworkInitializationCompleted();
    }

    internal static void Shutdown(MainViewModel model)
    {
        try { model.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        catch (Exception error)
        {
            // The window is closing; never let diagnostics or a failing native
            // shutdown escape the OS callback. No secret-bearing exception text.
            try
            {
                var report = new DiagnosticBuffer();
                report.Record(DiagnosticEvent.Create(DiagnosticCode.ApplicationShutdownFailed,
                    DiagnosticOperation.ApplicationShutdown, DiagnosticRecovery.Stopped, error));
                System.Diagnostics.Trace.WriteLine(report.CreateReport());
            }
            catch (Exception) { /* There is no remaining UI in which to report failure. */ }
        }
    }

    // Also exercised without constructing native outputs by the offline tests.
    // Only the safe snapshots survive the factory call; no Exception is retained.
    public static (ISpeechOutput Speech, IPcmOutput? Pcm, ICredentialStore? Credentials,
        IReadOnlyList<DiagnosticEvent> Diagnostics, IUtauPcmPostprocessor? UtauPostprocessor, ISpeechOutput? OpenJtalk, PlaybackVolume Volume) InitializeServices()
    {
        var diagnostics = new List<DiagnosticEvent>(3);
        var volume = new PlaybackVolume();
        ISpeechOutput speech;
        try { speech = CreateSpeech?.Invoke() ?? new UnavailableSpeech(); }
        catch (Exception error)
        {
            speech = new UnavailableSpeech();
            diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.StandardSpeechInitializationFailed,
                DiagnosticOperation.StandardSpeechInitialization, DiagnosticRecovery.AvailableFeatures, error));
        }
        if (speech is ISpeechVolumeControl adjustable) adjustable.Volume = volume;
        IPcmOutput? pcm;
        try { pcm = CreatePcm?.Invoke(); if (pcm is not null) pcm = new VolumePcmOutput(pcm, volume, ownsOutput: true); }
        catch (Exception error)
        {
            pcm = null;
            diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.PcmInitializationFailed,
                DiagnosticOperation.PcmInitialization, DiagnosticRecovery.StandardVoice, error));
        }
        ICredentialStore? credentials;
        try { credentials = CreateCredentialStore?.Invoke(); }
        catch (Exception error)
        {
            credentials = null;
            diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.CredentialStoreInitializationFailed,
                DiagnosticOperation.CredentialStoreInitialization, DiagnosticRecovery.SessionOnly, error));
        }
        IUtauPcmPostprocessor? postprocessor = null;
        try
        {
            if (pcm is not null) postprocessor = CreateUtauPostprocessor?.Invoke();
            if (postprocessor is IUtauModeProvider modes)
                foreach (var error in modes.InitializationErrors.Take(8))
                    diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.UtauPostprocessorInitializationFailed,
                        DiagnosticOperation.VoicePreparation, DiagnosticRecovery.AvailableFeatures, error));
        }
        catch (Exception error)
        {
            diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.UtauPostprocessorInitializationFailed,
                DiagnosticOperation.VoicePreparation, DiagnosticRecovery.AvailableFeatures, error));
        }
        ISpeechOutput? openJtalk = null;
        try
        {
            if (pcm is not null) openJtalk = CreateOpenJtalkSpeech?.Invoke(pcm);
            if (openJtalk is IOpenJtalkVoiceSelection selection)
                foreach (var error in selection.InitializationErrors.Take(8))
                    diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.OpenJtalkSpeechInitializationFailed,
                        DiagnosticOperation.OpenJtalkInitialization, DiagnosticRecovery.AvailableFeatures, error));
        }
        catch (Exception error)
        {
            diagnostics.Add(DiagnosticEvent.Create(DiagnosticCode.OpenJtalkSpeechInitializationFailed,
                DiagnosticOperation.OpenJtalkInitialization, DiagnosticRecovery.AvailableFeatures, error));
        }
        return (speech, pcm, credentials, diagnostics.AsReadOnly(), postprocessor, openJtalk, volume);
    }

    private sealed class UnavailableSpeech : ISpeechOutput
    {
        public string Name => "音声機能を初期化できませんでした";
        public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
            Task.FromException(new PlatformNotSupportedException());
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

