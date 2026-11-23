using System.Collections.Concurrent;
using Tanayomi;

public static class MobileLifecycleTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var twitchStarts = 0; var youtubeStarts = 0; var stops = 0; var pauses = 0;
        var lifecycle = new MobileConnectionLifecycle(true, value => { if (value) pauses++; },
            () => stops++, () => stops++, () => { }, _ => true);
        lifecycle.ConnectTwitch(() => twitchStarts++);
        lifecycle.ConnectYouTube(() => youtubeStarts++);
        lifecycle.SetForeground(false);
        check(twitchStarts == 1 && youtubeStarts == 1 && stops == 0 && pauses == 0,
            "mobile UI inactivity preserves active background reception and audio");
        lifecycle.SetForeground(true);
        lifecycle.SetForeground(true);
        check(twitchStarts == 2 && youtubeStarts == 2,
            "mobile return recreates requested routes once without duplicate activation");
        lifecycle.DisconnectYouTube();
        lifecycle.SetForeground(false);
        lifecycle.Interrupt();
        lifecycle.SetForeground(true);
        check(twitchStarts == 3 && youtubeStarts == 2 && pauses == 1,
            "OS interruption resumes only the route still requested by the user");
        lifecycle.Interrupt(); lifecycle.Resume(); lifecycle.Resume();
        check(twitchStarts == 4 && youtubeStarts == 2,
            "audio interruption ending in foreground resumes chosen routes only once");
        lifecycle.SetForeground(false); lifecycle.Interrupt(); lifecycle.Resume();
        check(twitchStarts == 4, "audio interruption ending in background cannot restart suspended operations");
        lifecycle.SetForeground(true);
        lifecycle.Stop();
        lifecycle.Interrupt(); lifecycle.Resume();
        lifecycle.SetForeground(false); lifecycle.SetForeground(true);
        check(twitchStarts == 5 && youtubeStarts == 2,
            "notification stop clears all automatic resume intent");
        lifecycle.Dispose();

        var active = true; var accepted = 0; var connected = 0; var suspended = false;
        var auth = new MobileConnectionLifecycle(true, value => suspended = value,
            () => { }, () => { }, () => { }, wanted => !wanted || active);
        var authVersion = auth.BeginAuthentication();
        active = false; auth.SetForeground(false);
        check(auth.CompleteAuthentication(authVersion, () => accepted++, () => connected++) && accepted == 1 && connected == 0 && suspended,
            "browser OAuth can complete while Android service start waits for foreground");
        active = true; auth.SetForeground(true);
        check(connected == 1 && !suspended, "return starts the authenticated route deferred by background restrictions");
        var staleVersion = auth.BeginAuthentication();
        auth.DisconnectTwitch();
        check(!auth.CompleteAuthentication(staleVersion, () => accepted++, () => connected++) && accepted == 1,
            "manual disconnect rejects a late OAuth completion before accepting its identity");
        var oldVersion = auth.BeginAuthentication();
        var newVersion = auth.BeginAuthentication();
        check(!auth.CompleteAuthentication(oldVersion, () => accepted++, () => connected++),
            "new authentication invalidates the previous account completion");
        check(auth.CompleteAuthentication(newVersion, () => accepted++, () => connected++), "latest authentication is accepted");
        check(!auth.CompleteAuthentication(newVersion, () => accepted++, () => connected++), "OAuth completion cannot be replayed");
        var disposingVersion = auth.BeginAuthentication(); auth.Dispose();
        check(!auth.CompleteAuthentication(disposingVersion, () => accepted++, () => connected++),
            "disposed lifecycle cannot accept credentials or restart reception");

        var desktopStarts = 0; var desktopPauses = 0;
        var desktop = new MobileConnectionLifecycle(false, _ => desktopPauses++, () => { }, () => { }, () => { });
        desktop.ConnectTwitch(() => desktopStarts++);
        desktop.SetForeground(false); desktop.Interrupt(); desktop.SetForeground(true);
        check(desktopStarts == 1 && desktopPauses == 0, "desktop connections are unaffected by mobile inactivity and interruption APIs");
        desktop.Dispose();

        await CheckQueueAsync(check);
        await CheckSpeechBackgroundAsync(check);
    }

    private static async Task CheckQueueAsync(Action<bool, string> check)
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new FakeSpeech(async (text, ct) =>
        {
            if (text != "current") return;
            started.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
        });
        await using var queue = new SpeechQueue(voice);
        queue.Enqueue(ChatMessage.Manual("current"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.Enqueue(ChatMessage.Manual("old pending"));
        queue.SetSuspended(true);
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(queue.PendingCount == 0 && !queue.Enqueue(ChatMessage.Manual("background late arrival")),
            "actual interruption cancels current speech, clears old queue and rejects late reception");
        queue.SetSuspended(false);
        queue.Enqueue(ChatMessage.Manual("fresh"));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!voice.Spoken.Contains("fresh")) await Task.Delay(5, timeout.Token);
        check(!voice.Spoken.Contains("old pending") && !voice.Spoken.Contains("background late arrival"),
            "resume speaks fresh input without replaying interrupted queue");
    }

    private static async Task CheckSpeechBackgroundAsync(Action<bool, string> check)
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var manualStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSecond = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var voice = new FakeSpeech(async (text, ct) =>
        {
            if (text == "first") { firstStarted.TrySetResult(); await releaseFirst.Task.WaitAsync(ct); }
            else if (text == "second")
            {
                secondStarted.TrySetResult();
                await releaseSecond.Task.WaitAsync(ct);
                throw new IOException("Playback did not start.");
            }
            else { manualStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
        });
        await using var queue = new SpeechQueue(voice);
        var background = false;
        using var lifecycle = new LifecycleScope(new MobileConnectionLifecycle(true, queue.SetSuspended,
            () => { }, () => { }, queue.Clear, wanted => { background = wanted; return true; }));
        var mobile = lifecycle.Value;
        var uiPosts = new ConcurrentQueue<Action>();
        var idleNotifications = 0;
        // Match MainViewModel: post a notification, then read current state on UI.
        // A queued old "active" notification must not restart after Stop/Clear.
        queue.ActivityChanged += () => uiPosts.Enqueue(() =>
        {
            mobile.SetSpeechActive(queue.IsBusy);
            if (!queue.IsBusy) idleNotifications++;
        });
        void Drain() { while (uiPosts.TryDequeue(out var action)) action(); }

        mobile.ConnectTwitch(() => { });
        queue.Enqueue(ChatMessage.Manual("first"));
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.Enqueue(ChatMessage.Manual("second"));
        Drain(); mobile.DisconnectTwitch();
        check(background && queue.IsBusy, "last connection disconnect keeps background service for current and queued speech");
        releaseFirst.TrySetResult();
        await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Drain();
        check(background && queue.IsBusy && idleNotifications == 0, "background activity has no idle gap between queued utterances");
        releaseSecond.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (queue.IsBusy || background) { Drain(); await Task.Delay(5, timeout.Token); }
        Drain();
        check(!background && idleNotifications > 0 && !voice.Spoken.Contains("second"),
            "queued speech failing before playback still notifies idle and releases background service");

        queue.Enqueue(ChatMessage.Manual("manual"));
        await manualStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Drain(); mobile.SetForeground(false);
        check(background && queue.IsBusy, "manual speech started in foreground keeps background service after leaving the UI");
        queue.Enqueue(ChatMessage.Manual("cancelled pending"));
        mobile.Stop();
        // Notifications from enqueue/current completion may still be in the UI queue.
        Drain();
        check(!background && !queue.IsBusy && queue.PendingCount == 0,
            "notification stop cancels audio and queued speech without stale activity restarting the service");
        mobile.Interrupt(); mobile.SetSpeechActive(true);
        check(!background, "late activity cannot re-enable background execution during OS interruption");
        mobile.Dispose(); mobile.SetSpeechActive(true);
        check(!background, "disposed lifecycle ignores late speech activity and keeps background execution off");
    }

    private sealed class LifecycleScope(MobileConnectionLifecycle value) : IDisposable
    {
        public MobileConnectionLifecycle Value => value;
        public void Dispose() => value.Dispose();
    }

    private sealed class FakeSpeech(Func<string, CancellationToken, Task> speak) : ISpeechOutput
    {
        public string Name => "lifecycle test";
        public ConcurrentQueue<string> Spoken { get; } = new();
        public async Task SpeakAsync(string text, CancellationToken ct) { await speak(text, ct); Spoken.Enqueue(text); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
