using AVFoundation;
using Foundation;

namespace Tanayomi.iOS;

/// <summary>Only real utterance playback holds the audio session; no silent keepalive.</summary>
internal static class IosAudioSession
{
    // Called on the UIKit/Avalonia UI thread by both audio outputs and lifecycle callbacks.
    private static int playing;
    public static bool IsPlaying => playing > 0;
    public static event Action? PlaybackEnded;

    public static IDisposable BeginPlayback()
    {
        if (playing == 0)
        {
            var session = AVAudioSession.SharedInstance();
            var configured = session.SetCategory(AVAudioSessionCategory.Playback, AVAudioSessionMode.SpokenAudio,
                AVAudioSessionCategoryOptions.MixWithOthers, out var categoryError);
            try
            {
                if (!configured)
                {
                    ReportFailure(DiagnosticOperation.MobileAudioSessionSetup);
                    throw Failure("設定", categoryError);
                }
            }
            finally { categoryError?.Dispose(); }
            var activated = session.SetActive(true, out var activationError);
            try
            {
                if (!activated)
                {
                    ReportFailure(DiagnosticOperation.MobileAudioSessionActivation);
                    throw Failure("開始", activationError);
                }
            }
            finally { activationError?.Dispose(); }
        }
        playing++;
        return new Playback();
    }

    private static IOException Failure(string operation, NSError? error) =>
        new($"iOS音声セッションの{operation}に失敗しました（OSコード{error?.Code.ToString() ?? "不明"}）。");

    private static void ReportFailure(DiagnosticOperation operation) =>
        App.ReportMobileStatus("iOSの音声セッションを更新できませんでした。次の読み上げで再試行します。",
            DiagnosticEvent.Create(DiagnosticCode.MobileAudioSessionFailed, operation, DiagnosticRecovery.NextUtterance));

    private sealed class Playback : IDisposable
    {
        private bool disposed;
        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            playing--;
            if (playing != 0) return;
            var deactivated = AVAudioSession.SharedInstance().SetActive(false,
                AVAudioSessionSetActiveOptions.NotifyOthersOnDeactivation, out var error);
            try
            {
                if (!deactivated) ReportFailure(DiagnosticOperation.MobileAudioSessionRelease);
            }
            finally
            {
                error?.Dispose();
                PlaybackEnded?.Invoke();
            }
        }
    }
}
