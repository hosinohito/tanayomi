using Android.Media;

namespace Tanayomi.Android;

/// <summary>In-memory PCM playback; no voicebank audio is written to device storage.</summary>
public sealed class AndroidPcmOutput : IPcmOutput
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
        try
        {
            token.ThrowIfCancellationRequested();
            using var attributesBuilder = new AudioAttributes.Builder();
            attributesBuilder.SetUsage(AudioUsageKind.Media);
            attributesBuilder.SetContentType(AudioContentType.Speech);
            using var attributes = attributesBuilder.Build() ?? throw new IOException("音声属性を作成できません。");
            using var formatBuilder = new AudioFormat.Builder();
            formatBuilder.SetEncoding(Encoding.Pcm16bit);
            formatBuilder.SetSampleRate(sampleRate);
            formatBuilder.SetChannelMask(ChannelOut.Mono);
            using var format = formatBuilder.Build() ?? throw new IOException("PCM形式を作成できません。");
            using var builder = new AudioTrack.Builder();
            builder.SetAudioAttributes(attributes);
            builder.SetAudioFormat(format);
            builder.SetTransferMode(AudioTrackMode.Static);
            builder.SetBufferSizeInBytes(checked(samples.Length * 2));
            using var track = builder.Build() ?? throw new IOException("PCM出力を作成できません。");
            if (track.State != AudioTrackState.Initialized && track.State != AudioTrackState.NoStaticData)
                throw new IOException("PCM出力を初期化できません。");
            try
            {
                var pcm = samples.ToArray();
                // Static mode copies to native memory without waiting for the output device.
                if (track.Write(pcm, 0, pcm.Length, WriteMode.NonBlocking) != pcm.Length)
                    throw new IOException("PCM音声を出力バッファーへ転送できません。");
                token.ThrowIfCancellationRequested();
                track.Play();
                while ((uint)track.PlaybackHeadPosition < (uint)pcm.Length)
                    await Task.Delay(10, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
            }
            finally
            {
                // Release remains guaranteed even if the device rejects Stop after disconnecting.
                try { track.Stop(); }
                finally { track.Release(); }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("PCM音声出力が時間内に完了しませんでした。"); }
        finally { serial.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        serial.Release();
    }
}
