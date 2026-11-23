using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics.Drawables;
using Android.OS;

namespace Tanayomi.Android;

[Service(Exported = false, StopWithTask = false, ForegroundServiceType = ForegroundService.TypeMediaPlayback)]
public sealed class ChatPlaybackService : Service
{
    private const string ChannelId = "tanayomi.playback";
    private const string StopAction = "app.tanayomi.mobile.STOP_PLAYBACK";
    private const int NotificationId = 4101;
    private static volatile bool wanted;
    private static volatile ChatPlaybackService? current;
    private volatile bool expectedStop;
    private PowerManager.WakeLock? cpuWakeLock;

    internal static bool Configure(bool required)
    {
        var context = global::Android.App.Application.Context;
        using var intent = new Intent(context, typeof(ChatPlaybackService));
        if (!required)
        {
            wanted = false;
            if (current is { } running) running.expectedStop = true;
            context.StopService(intent);
            return true;
        }
        if (wanted) return true;
        // Android 12+ does not permit arbitrary FGS starts from background.
        // OAuth can finish in the browser; defer that initial start until return.
        if (!App.IsMobileForeground)
        {
            App.ReportMobileStatus("通知付きの読み上げを開始するには、アプリを前面へ戻してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileBackgroundUnavailable, DiagnosticOperation.MobileBackgroundStart,
                    DiagnosticRecovery.ReconnectOnForeground));
            return false;
        }
        MainActivity.RequestPlaybackNotifications();
        wanted = true;
        try
        {
            if (OperatingSystem.IsAndroidVersionAtLeast(26)) context.StartForegroundService(intent);
            else context.StartService(intent);
            return true;
        }
        catch (Exception error)
        {
            wanted = false;
            App.ReportMobileStatus("Androidがバックグラウンド読み上げの開始を拒否しました。アプリを前面へ戻して再接続してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileBackgroundUnavailable, DiagnosticOperation.MobileBackgroundStart,
                    DiagnosticRecovery.ReconnectOnForeground, error));
            return false;
        }
    }

    public override IBinder? OnBind(Intent? intent) => null;
    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        current = this;
        try
        {
            if (intent?.Action == StopAction)
            {
                App.StopMobileOperations();
                Finish();
                return StartCommandResult.NotSticky;
            }
            if (!wanted) { Finish(); return StartCommandResult.NotSticky; }
            expectedStop = false;
            if (OperatingSystem.IsAndroidVersionAtLeast(26))
            {
                using var manager = GetSystemService(NotificationService) as NotificationManager
                    ?? throw new IOException("Notification service unavailable.");
                using var channel = new NotificationChannel(ChannelId, "チャット読み上げ", NotificationImportance.Low);
                manager.CreateNotificationChannel(channel);
            }
            using var open = new Intent(this, typeof(MainActivity));
            open.SetFlags(ActivityFlags.ClearTop | ActivityFlags.SingleTop);
            using var content = PendingIntent.GetActivity(this, 0, open, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
                ?? throw new IOException("Notification intent unavailable.");
            using var stop = new Intent(this, typeof(ChatPlaybackService));
            stop.SetAction(StopAction);
            using var stopPending = PendingIntent.GetService(this, 1, stop, PendingIntentFlags.UpdateCurrent | PendingIntentFlags.Immutable)
                ?? throw new IOException("Stop intent unavailable.");
            using var icon = Icon.CreateWithResource(this, global::Android.Resource.Drawable.IcMediaPause)
                ?? throw new IOException("Notification icon unavailable.");
            using var actionBuilder = new Notification.Action.Builder(icon, "停止", stopPending);
            using var action = actionBuilder.Build() ?? throw new IOException("Notification action unavailable.");
            using var builder = OperatingSystem.IsAndroidVersionAtLeast(26) ? new Notification.Builder(this, ChannelId) : new Notification.Builder(this);
            builder.SetSmallIcon(global::Android.Resource.Drawable.IcMediaPlay);
            builder.SetContentTitle("たなよみ");
            builder.SetContentText("読み上げを継続しています。停止で接続と音声を終了します。");
            builder.SetOngoing(true);
            builder.SetContentIntent(content);
            builder.SetCategory(Notification.CategoryService);
            builder.AddAction(action);
            using var notification = builder.Build() ?? throw new IOException("Notification unavailable.");
            if (OperatingSystem.IsAndroidVersionAtLeast(29)) StartForeground(NotificationId, notification, ForegroundService.TypeMediaPlayback);
            else StartForeground(NotificationId, notification);
            AcquireCpu();
            Report("通知付きサービスでバックグラウンド受信と読み上げを継続します。省電力・OS制限では通信が休止することがあります。終了後はアプリへ戻ると再接続します。");
        }
        catch (Exception error)
        {
            Report("Androidの読み上げサービスを開始できませんでした。通知・サービスの許可を確認してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileBackgroundUnavailable, DiagnosticOperation.MobileBackgroundStart,
                    DiagnosticRecovery.ReconnectOnForeground, error));
            TryCallback(App.InterruptMobileOperations);
            Finish();
        }
        // Do not silently restart after the user or OS has stopped the process.
        return StartCommandResult.NotSticky;
    }

    public override void OnTimeout(int startId, ForegroundService fgsType)
    {
        Report("Androidがバックグラウンドサービスを中断しました。アプリへ戻ると再接続します。",
            DiagnosticEvent.Create(DiagnosticCode.MobileInterrupted, DiagnosticOperation.MobileBackgroundStop,
                DiagnosticRecovery.ReconnectOnForeground));
        TryCallback(App.InterruptMobileOperations);
        Finish();
    }
    public override void OnDestroy()
    {
        if (ReferenceEquals(current, this))
        {
            current = null;
            if (!expectedStop)
            {
                wanted = false;
                Report("Androidが読み上げサービスを終了しました。アプリへ戻ると再接続します。",
                    DiagnosticEvent.Create(DiagnosticCode.MobileInterrupted, DiagnosticOperation.MobileBackgroundStop,
                        DiagnosticRecovery.ReconnectOnForeground));
                TryCallback(App.InterruptMobileOperations);
            }
        }
        ReleaseCpu();
        TryCallback(base.OnDestroy);
    }
    private void AcquireCpu()
    {
        if (cpuWakeLock?.IsHeld == true) return;
        using var power = GetSystemService(PowerService) as PowerManager
            ?? throw new IOException("Power service unavailable.");
        cpuWakeLock ??= power.NewWakeLock(WakeLockFlags.Partial, "app.tanayomi.mobile:ChatPlayback")
            ?? throw new IOException("CPU wake lock unavailable.");
        // The user explicitly requested live reading and sees its stop notification.
        // Hold only for that session; never turn on the screen or request Doze exemption.
        cpuWakeLock.SetReferenceCounted(false);
        cpuWakeLock.Acquire();
    }
    private void ReleaseCpu()
    {
        var wake = cpuWakeLock;
        cpuWakeLock = null;
        if (wake is null) return;
        TryCallback(() => { if (wake.IsHeld) wake.Release(); });
        TryCallback(wake.Dispose);
    }
    private void Finish()
    {
        wanted = false; expectedStop = true;
        ReleaseCpu();
        TryCallback(RemoveNotification);
        TryCallback(StopSelf);
    }
    private static void TryCallback(Action action)
    {
        try { action(); }
        catch (Exception error) { Report("Androidの読み上げサービスの終了処理に失敗しました。接続状態を確認してください。",
            DiagnosticEvent.Create(DiagnosticCode.MobileStateFailed, DiagnosticOperation.MobileBackgroundStop,
                DiagnosticRecovery.ReconnectOnForeground, error)); }
    }
    private static void Report(string message, DiagnosticEvent? diagnostic = null)
    {
        try
        {
            if (diagnostic is null) App.ReportMobileStatus(message);
            else App.ReportMobileStatus(message, diagnostic);
        }
        catch (Exception) { /* The UI may already have been destroyed; never escape a native callback. */ }
    }
    private void RemoveNotification()
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(24)) StopForeground(StopForegroundFlags.Remove);
        else StopForeground(true);
    }
}
