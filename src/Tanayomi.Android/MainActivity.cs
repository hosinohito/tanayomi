using Android.App;
using Android.Content.PM;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Tanayomi.Android;

[Activity(Label = "たなよみ", Theme = "@android:style/Theme.Material.NoActionBar", MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
public class MainActivity : AvaloniaMainActivity
{
    private static WeakReference<MainActivity>? foregroundActivity;
    private static bool notificationRequested;
    protected override void OnResume()
    {
        base.OnResume();
        foregroundActivity = new(this);
        SetForeground(true);
    }
    protected override void OnPause()
    {
        SetForeground(false);
        foregroundActivity = null;
        base.OnPause();
    }
    private static void SetForeground(bool active)
    {
        try { App.SetMobileForeground(active); }
        catch (Exception error)
        {
            try { App.ReportMobileStatus("Androidの動作状態を更新できませんでした。接続状態を確認してください。",
                DiagnosticEvent.Create(DiagnosticCode.MobileStateFailed, DiagnosticOperation.MobileLifecycle,
                    DiagnosticRecovery.ReconnectOnForeground, error)); }
            catch (Exception) { /* The UI may already be gone; never escape the OS callback. */ }
        }
    }
    internal static void RequestPlaybackNotifications()
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(33) || notificationRequested ||
            foregroundActivity is null || !foregroundActivity.TryGetTarget(out var activity) ||
            activity.CheckSelfPermission("android.permission.POST_NOTIFICATIONS") == Permission.Granted) return;
        notificationRequested = true;
        activity.RequestPermissions(["android.permission.POST_NOTIFICATIONS"], 4101);
    }
}

[Application]
public class MainApplication(nint handle, JniHandleOwnership ownership) : AvaloniaAndroidApplication<App>(handle, ownership)
{
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        AppPaths.ConfigureProcessStorage();
        App.CreateSpeech = () => new AndroidSpeech();
        App.CreatePcm = () => new AndroidPcmOutput();
        App.CreateCredentialStore = () => new AndroidCredentialStore(global::Android.App.Application.Context);
        App.ConfigureMobileBackground = ChatPlaybackService.Configure;
        return base.CustomizeAppBuilder(builder);
    }
}
