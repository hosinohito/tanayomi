using AVFoundation;
using Avalonia.Threading;

namespace Tanayomi.iOS;

public sealed class IosSpeech : ISpeechOutput, ISpeechVolumeControl
{
    private readonly AVSpeechSynthesizer engine = new();
    private readonly Progress progress = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;
    public PlaybackVolume Volume { get; set; } = new();
    public string Name => "iOS標準音声";
    public IosSpeech()
    {
        engine.Delegate = progress;
        engine.UsesApplicationAudioSession = true;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = cancellation.Token;
        await serial.WaitAsync(token).ConfigureAwait(false);
        IDisposable? audio = null;
        var attempted = false;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                var percent = Volume.Percent;
                if (percent == 0) { completion.TrySetResult(); return; }
                attempted = true;
                var voice = AVSpeechSynthesisVoice.FromLanguage("ja-JP") ?? throw new IOException("日本語音声がありません。");
                var utterance = new AVSpeechUtterance(text) { Voice = voice, Volume = percent / 100f };
                progress.Current = (utterance, completion);
                audio = IosAudioSession.BeginPlayback();
                engine.SpeakUtterance(utterance);
            });
            await completion.Task.WaitAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { if (attempted) engine.StopSpeaking(AVSpeechBoundary.Immediate); }
                    finally
                    {
                        try { progress.Current?.Utterance.Dispose(); }
                        finally { progress.Current = null; audio?.Dispose(); }
                    }
                });
            }
            finally { serial.Release(); }
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                try { engine.StopSpeaking(AVSpeechBoundary.Immediate); }
                finally
                {
                    try { engine.Delegate = null; engine.Dispose(); }
                    finally { progress.Dispose(); }
                }
            });
        }
        finally { serial.Release(); }
    }

    private sealed class Progress : AVSpeechSynthesizerDelegate
    {
        public (AVSpeechUtterance Utterance, TaskCompletionSource Completion)? Current { get; set; }
        public override void DidFinishSpeechUtterance(AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
        { if (Current is { } c && c.Utterance.Handle == utterance.Handle) c.Completion.TrySetResult(); }
        public override void DidCancelSpeechUtterance(AVSpeechSynthesizer synthesizer, AVSpeechUtterance utterance)
        { if (Current is { } c && c.Utterance.Handle == utterance.Handle) c.Completion.TrySetCanceled(); }
    }
}
