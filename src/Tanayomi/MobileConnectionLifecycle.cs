namespace Tanayomi;

/// <summary>Remembers user connection intent across actual mobile OS interruption.
/// UI inactivity alone never stops reception or playback.</summary>
public sealed class MobileConnectionLifecycle(bool enabled, Action<bool> suspend,
    Action stopTwitch, Action stopYouTube, Action clearSpeech, Func<bool, bool>? background = null)
{
    private readonly object gate = new();
    private Action? twitch, youtube;
    private bool foreground = true, interrupted, disposed, speechActive;
    private long twitchVersion;
    public bool HasTwitchRequest { get { lock (gate) return twitch is not null; } }

    public long BeginAuthentication()
    {
        lock (gate)
        {
            if (disposed) return long.MinValue;
            twitch = null; ++twitchVersion;
            stopTwitch(); UpdateBackground();
            return twitchVersion;
        }
    }

    public bool CompleteAuthentication(long version, Action accept, Action connect)
    {
        lock (gate)
        {
            if (disposed || version != twitchVersion) return false;
            accept();
            ++twitchVersion; twitch = connect;
            Activate();
            if (!interrupted) connect();
            return true;
        }
    }

    public long ConnectTwitch(Action connect)
    {
        lock (gate)
        {
            if (disposed) return twitchVersion;
            ++twitchVersion; twitch = connect;
            Activate();
            if (!interrupted) connect();
            return twitchVersion;
        }
    }
    public void ConnectYouTube(Action connect)
    {
        lock (gate)
        {
            if (disposed) return;
            youtube = connect; Activate();
            if (!interrupted) connect();
        }
    }
    public void RestartTwitch()
    { lock (gate) { if (!disposed && !interrupted) twitch?.Invoke(); } }
    public void SetSpeechActive(bool active)
    {
        lock (gate)
        {
            if (!enabled || disposed || interrupted || speechActive == active) return;
            speechActive = active;
            // An old audio completion/queue notification cannot undo OS interruption.
            if (!UpdateBackground()) InterruptCore();
        }
    }

    public long DisconnectTwitch()
    {
        lock (gate)
        {
            twitch = null; ++twitchVersion;
            stopTwitch(); UpdateBackground();
            return twitchVersion;
        }
    }
    public void DisconnectYouTube()
    { lock (gate) { youtube = null; stopYouTube(); UpdateBackground(); } }
    public bool IsCurrent(long version)
    { lock (gate) return !disposed && version == twitchVersion; }

    public void SetForeground(bool active)
    {
        lock (gate)
        {
            if (!enabled || disposed || foreground == active) return;
            foreground = active;
            if (!active) return;
            // The OS may have suspended sockets without a managed callback.
            // Recreate only user-requested routes; deduplication remains in the queue.
            Activate();
            if (!interrupted) { twitch?.Invoke(); youtube?.Invoke(); }
        }
    }

    public void Interrupt()
    { lock (gate) { if (enabled && !disposed) InterruptCore(); } }
    public void Resume()
    {
        lock (gate)
        {
            if (!enabled || disposed || !interrupted || !foreground) return;
            Activate();
            if (!interrupted) { twitch?.Invoke(); youtube?.Invoke(); }
        }
    }
    private void InterruptCore()
    {
        if (interrupted) return;
        interrupted = true;
        speechActive = false;
        suspend(true); stopTwitch(); stopYouTube();
        background?.Invoke(false);
    }
    private void Activate()
    {
        if (!enabled) return;
        if (interrupted && !foreground) return;
        interrupted = false;
        if (!UpdateBackground()) { InterruptCore(); return; }
        suspend(false);
    }
    private bool UpdateBackground() => !enabled || background?.Invoke(!disposed && !interrupted && (twitch is not null || youtube is not null || speechActive)) != false;

    /// <summary>A notification stop is a user disconnect, not a resumable OS interruption.</summary>
    public void Stop()
    {
        lock (gate)
        {
            twitch = youtube = null; ++twitchVersion;
            speechActive = false;
            stopTwitch(); stopYouTube(); clearSpeech();
            background?.Invoke(false);
        }
    }
    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            Stop(); suspend(true);
        }
    }
}
