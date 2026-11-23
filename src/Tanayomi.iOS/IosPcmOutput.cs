using AVFoundation;
using Avalonia.Threading;
using Foundation;

namespace Tanayomi.iOS;

/// <summary>AVAudioPlayer owns in-memory RIFF data only for the current utterance.</summary>
public sealed class IosPcmOutput : IPcmOutput
{
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private int disposed;

    public async Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (sampleRate is < 8000 or > 192000 || samples.Length < 1 || samples.Length > sampleRate * 60)
            throw new ArgumentOutOfRangeException(nameof(samples));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds((double)samples.Length / sampleRate + 5));
        var token = timeout.Token;
        await serial.WaitAsync(token).ConfigureAwait(false);
        AVAudioPlayer? player = null;
        NSData? data = null;
        IDisposable? audio = null;
        using var progress = new Progress();
        try
        {
            var wave = new PcmAudio(samples.ToArray(), sampleRate).ToWaveBytes();
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                token.ThrowIfCancellationRequested();
                data = NSData.FromArray(wave);
                player = AVAudioPlayer.FromData(data, out var error);
                try
                {
                    if (player is null || error is not null) throw new IOException("PCM出力を初期化できません。");
                    player.Delegate = progress;
                    if (!player.PrepareToPlay()) throw new IOException("PCM音声を準備できません。");
                    audio = IosAudioSession.BeginPlayback();
                    if (!player.Play()) throw new IOException("PCM音声を開始できません。");
                }
                finally { error?.Dispose(); }
            });
            await progress.Completed.Task.WaitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("PCM音声出力が時間内に完了しませんでした。"); }
        finally
        {
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    try { player?.Stop(); }
                    finally
                    {
                        try { player?.Dispose(); }
                        finally
                        {
                            try { data?.Dispose(); }
                            finally { audio?.Dispose(); }
                        }
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
        serial.Release();
    }

    private sealed class Progress : AVAudioPlayerDelegate
    {
        public TaskCompletionSource Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override void FinishedPlaying(AVAudioPlayer player, bool flag)
        {
            if (flag) Completed.TrySetResult();
            else Completed.TrySetException(new IOException("PCM音声の再生が中断されました。"));
        }
        public override void DecoderError(AVAudioPlayer player, NSError? error)
            => Completed.TrySetException(new IOException("PCM音声をデコードできません。"));
    }
}
