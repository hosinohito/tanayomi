namespace Tanayomi;

/// <summary>App-local playback gain. This value never changes an OS mixer setting.</summary>
public interface ISpeechVolumeControl { PlaybackVolume Volume { get; set; } }

public sealed class PlaybackVolume
{
    private int percent = 100;

    public int Percent
    {
        get => Volatile.Read(ref percent);
        set
        {
            if (value is < 0 or > 400) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref percent, value);
        }
    }

    // One transformation takes one snapshot. The owner decides when to invoke
    // it; this does not establish the UI's live-change or persistence policy.
    public PlaybackVolumeResult Apply(ReadOnlyMemory<short> samples, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var selected = Percent;
        if (selected == 0) return new(true, ReadOnlyMemory<short>.Empty);
        if (selected == 100) return new(false, samples);
        var result = new short[samples.Length];
        for (var i = 0; i < result.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            // Exact hundredths with symmetric nearest rounding. The product
            // and half-step adjustment fit int. Saturate rather than wrapping PCM16.
            var scaled = samples.Span[i] * selected;
            result[i] = (short)Math.Clamp((scaled + (scaled >= 0 ? 50 : -50)) / 100, short.MinValue, short.MaxValue);
        }
        ct.ThrowIfCancellationRequested();
        return new(false, result);
    }
}

/// <summary>Mute is a successful suppression, not an empty/invalid synthesized PCM.</summary>
public readonly record struct PlaybackVolumeResult(bool IsMuted, ReadOnlyMemory<short> Samples);

/// <summary>
/// Borrows by default; the composition root may explicitly transfer ownership.
/// No synthesis/cache PCM is modified, and mute never invokes native playback.
/// Integration and the policy for changes during playback belong to the owner.
/// </summary>
public sealed class VolumePcmOutput : IPcmOutput
{
    private readonly IPcmOutput output;
    private readonly PlaybackVolume volume;
    private int disposed;
    private readonly bool ownsOutput;
    public PlaybackVolume Volume => volume;

    public VolumePcmOutput(IPcmOutput output, PlaybackVolume volume, bool ownsOutput = false)
    {
        this.ownsOutput = ownsOutput;
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.volume = volume ?? throw new ArgumentNullException(nameof(volume));
    }

    public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (sampleRate is < 8000 or > 192000 || samples.Length < 1 || samples.Length > sampleRate * 60)
            throw new ArgumentOutOfRangeException(nameof(samples));
        var adjusted = volume.Apply(samples, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return adjusted.IsMuted ? Task.CompletedTask : output.PlayAsync(adjusted.Samples, sampleRate, cancellationToken);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0 && ownsOutput) return output.DisposeAsync();
        return ValueTask.CompletedTask;
    }
}


