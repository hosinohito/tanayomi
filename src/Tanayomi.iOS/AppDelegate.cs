using Avalonia;
using Avalonia.iOS;
using AVFoundation;
using Foundation;
using UIKit;

namespace Tanayomi.iOS;

[Register("AppDelegate")]
public class AppDelegate : AvaloniaAppDelegate<App>
{
    private readonly List<NSObject> observers = [];
    private nint backgroundTask = UIApplication.BackgroundTaskInvalid;
    private bool background;
    private bool graceExpired;
    private bool backgroundSuspended;
    private bool awaitingSpeechIdle;

    public static void Main(string[] args) => UIApplication.Main(args, null, typeof(AppDelegate));
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppPaths.ConfigureProcessStorage();
        App.CreateSpeech = () => new IosSpeech();
        App.CreatePcm = () => new IosPcmOutput();
        App.CreateCredentialStore = () => new IosCredentialStore();
        // UIApplication notifications also cover an application using UIKit scenes.
        observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.WillResignActiveNotification,
            _ => Safely(() => { App.SetMobileForeground(false); BeginGrace(); })));
        observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidEnterBackgroundNotification,
            _ => Safely(() =>
            {
                background = true;
                CheckBackgroundSuspension();
            })));
        observers.Add(NSNotificationCenter.DefaultCenter.AddObserver(UIApplication.DidBecomeActiveNotification,
            _ => Safely(() =>
            {
                background = false;
                graceExpired = false;
                backgroundSuspended = false;
                awaitingSpeechIdle = false;
                EndGrace();
                App.SetMobileForeground(true);
            })));
        observers.Add(AVAudioSession.Notifications.ObserveInterruption((_, args) => Safely(() =>
        {
            if (args.InterruptionType == AVAudioSessionInterruptionType.Began)
            {
                App.InterruptMobileOperations();
                App.ReportMobileStatus("iOSが音声を中断したため受信・読み上げを停止しました。アプリへ戻ると選択していた接続を再開します。",
                    DiagnosticEvent.Create(DiagnosticCode.MobileInterrupted, DiagnosticOperation.MobileLifecycle,
                        DiagnosticRecovery.ReconnectOnForeground));
            }
            else if (args.InterruptionType == AVAudioSessionInterruptionType.Ended &&
                     (args.Option & AVAudioSessionInterruptionOptions.ShouldResume) != 0 &&
                     UIApplication.SharedApplication.ApplicationState == UIApplicationState.Active && App.IsMobileForeground)
            {
                // An interruption can finish without an inactive -> active UI transition.
                // Resume only the shared lifecycle's interrupted, user-selected work.
                App.ResumeMobileOperations();
            }
        })));
        IosAudioSession.PlaybackEnded += PlaybackEnded;
        App.MobileSpeechIdle += SpeechIdle;
        return base.CustomizeAppBuilder(builder);
    }

    private void BeginGrace()
    {
        if (backgroundTask != UIApplication.BackgroundTaskInvalid) return;
        graceExpired = false;
        backgroundTask = UIApplication.SharedApplication.BeginBackgroundTask("Tanayomi.FinishActiveOperations", () => Safely(() =>
        {
            graceExpired = true;
            // End every finite assertion even when genuine audio has its own background mode.
            EndGrace();
            CheckBackgroundSuspension();
        }));
        if (backgroundTask == UIApplication.BackgroundTaskInvalid) graceExpired = true;
        else if (graceExpired) EndGrace(); // Also balance an immediately expired request.
    }

    private void EndGrace()
    {
        if (backgroundTask == UIApplication.BackgroundTaskInvalid) return;
        var task = backgroundTask;
        backgroundTask = UIApplication.BackgroundTaskInvalid;
        UIApplication.SharedApplication.EndBackgroundTask(task);
    }

    private void PlaybackEnded() => Safely(() =>
    {
        // Wait for the queue's completion notification if the current utterance
        // is unwinding or another utterance is queued or already synthesizing.
        if (!background || !graceExpired || backgroundSuspended || IosAudioSession.IsPlaying) return;
        if (App.IsMobileSpeechBusy) awaitingSpeechIdle = true;
        else CheckBackgroundSuspension();
    });

    private void SpeechIdle() => Safely(() =>
    {
        if (awaitingSpeechIdle && !App.IsMobileSpeechBusy) CheckBackgroundSuspension();
    });

    private void CheckBackgroundSuspension()
    {
        if (!background || !graceExpired || backgroundSuspended || IosAudioSession.IsPlaying) return;
        backgroundSuspended = true;
        awaitingSpeechIdle = false;
        SuspendForBackground();
    }

    private static void SuspendForBackground()
    {
        App.InterruptMobileOperations();
        App.ReportMobileStatus("iOSのバックグラウンド実行時間が終了しました。音声がない間の常時受信は保証されません。アプリへ戻ると接続を再開します。",
            DiagnosticEvent.Create(DiagnosticCode.MobileInterrupted, DiagnosticOperation.MobileBackgroundStop,
                DiagnosticRecovery.ReconnectOnForeground));
    }

    private static void Safely(Action operation)
    {
        try { operation(); }
        catch (Exception ex)
        {
            try { App.ReportMobileStatus($"iOSの動作状態を更新できませんでした（{ex.GetType().Name}）。アプリへ戻って接続状態を確認してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileStateFailed, DiagnosticOperation.MobileLifecycle,
                    DiagnosticRecovery.ReconnectOnForeground, ex)); }
            catch (Exception) { /* UI shutdown must not leak an exception into a native callback. */ }
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IosAudioSession.PlaybackEnded -= PlaybackEnded;
            App.MobileSpeechIdle -= SpeechIdle;
            foreach (var observer in observers) observer.Dispose();
            observers.Clear();
            Safely(EndGrace);
        }
        base.Dispose(disposing);
    }
}
