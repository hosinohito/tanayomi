using System.Numerics;

namespace Tanayomi;

// Internal equivalents of g (vowel resonance) and B (aspiration). Neither effect
// changes the pulse grid, wave length, OTO overlap, or the consonant attack.
internal sealed class UtauTimbre
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private const int Size = WaveformPitch.PhaseTables.Size, Half = Size / 2, Step = Size / 4;
    private readonly Complex[] spectrum = new Complex[Size];
    private readonly double[] magnitudes = new double[Half + 1], logarithms = new double[Half + 1], envelope = new double[Half + 1];
    private float[] shaped = [];
    private float[] weights = [];
    private float[] noise = [];

    // Inspect the source before pitch processing or the addition of aspiration.
    internal static int VoicedLength(ReadOnlySpan<float> source, double period)
    {
        if (period is < 60 or > 490 || !double.IsFinite(period)) return 0;
        var width = Math.Max(Rate / 48, (int)Math.Ceiling(3 * period));
        if (source.Length < width) return 0;
        var step = width / 2;
        for (var at = 0; ; at = Math.Min(at + step, source.Length - width))
        {
            if (!UtauPitch.IsVoiced(source.Slice(at, width), period)) return at == 0 ? 0 : at + step;
            if (at == source.Length - width) return source.Length;
        }
    }

    internal void Apply(Span<float> vowel, double sourcePeriod, UtauVoiceTuning tuning, uint seed, CancellationToken ct)
    {
        if (!tuning.HasTimbre || vowel.Length < Rate / 48) return;
        var length = vowel.Length;
        if (shaped.Length < length) { shaped = new float[length]; weights = new float[length]; }
        var processed = shaped.AsSpan(0, length);
        if (tuning.Formant == 0) vowel.CopyTo(processed);
        else ShiftEnvelope(vowel, processed, sourcePeriod, tuning.Formant, ct);
        double dryPower = 0, processedPower = 0;
        for (var i = 0; i < length; i++)
        {
            dryPower += (double)vowel[i] * vowel[i];
            processedPower += (double)processed[i] * processed[i];
        }
        if (dryPower < 1e-10 || processedPower < 1e-10) return;
        var level = Math.Clamp(Math.Sqrt(dryPower / processedPower), 0.55, 1.8);

        var breath = tuning.Breath * 0.0032;
        var noiseGain = 0.0;
        if (breath > 0)
        {
            if (noise.Length < length) noise = new float[length];
            noiseGain = PrepareBreath(processed, seed, breath * Math.Sqrt(dryPower), ct);
        }
        var dryGain = level * Math.Sqrt(1 - breath * breath);
        var fade = Math.Min(length / 3, Rate / 100);
        for (var i = 0; i < length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            var t = Math.Min(1.0, (double)Math.Min(i, length - 1 - i) / fade);
            var blend = t * t * (3 - 2 * t);
            var wet = dryGain * processed[i] + (breath > 0 ? noiseGain * noise[i] : 0);
            vowel[i] += (float)((wet - vowel[i]) * blend);
        }
    }

    private void ShiftEnvelope(ReadOnlySpan<float> input, Span<float> output, double period, int amount, CancellationToken ct)
    {
        output.Clear();
        Array.Clear(weights, 0, output.Length);
        var window = WaveformPitch.PhaseTables.Window;
        var radius = Math.Clamp((int)Math.Ceiling(Size / period), 5, 32);
        var warp = Math.Pow(2, -amount * 3.0 / 1200);
        for (var center = 0; center < input.Length + Half; center += Step)
        {
            ct.ThrowIfCancellationRequested();
            var start = center - Half;
            for (var j = 0; j < Size; j++)
                spectrum[j] = (uint)(start + j) < (uint)input.Length ? new(input[start + j] * window[j], 0) : Complex.Zero;
            WaveformPitch.Fft(spectrum, false);
            for (var k = 0; k <= Half; k++) magnitudes[k] = spectrum[k].Magnitude;
            WaveformPitch.BuildEnvelope(magnitudes, logarithms, envelope, radius);
            for (var k = 1; k < Half; k++)
            {
                // Move the smooth spectral envelope, not the harmonics or their phases.
                var hz = k * (double)Rate / Size;
                var strength = Math.Min(Math.Clamp((hz - 140) / 180, 0, 1), Math.Clamp((8500 - hz) / 2200, 0, 1));
                if (strength == 0) continue;
                var shifted = Math.Clamp(k / warp, 0, Half - 1.0);
                var low = (int)shifted;
                var target = envelope[low] + (envelope[low + 1] - envelope[low]) * (shifted - low);
                var gain = 1 + (Math.Clamp(target / envelope[k], 0.42, 2.4) - 1) * strength;
                spectrum[k] *= gain;
                spectrum[Size - k] *= gain;
            }
            WaveformPitch.Fft(spectrum, true);
            for (var j = Math.Max(0, -start); j < Math.Min(Size, input.Length - start); j++)
            {
                output[start + j] += (float)(spectrum[j].Real * window[j]);
                weights[start + j] += (float)(window[j] * window[j]);
            }
        }
        for (var i = 0; i < output.Length; i++) output[i] /= weights[i];
    }

    private double PrepareBreath(ReadOnlySpan<float> signal, uint seed, double targetNorm, CancellationToken ct)
    {
        var random = seed ^ 0x7e64d281u;
        if (random == 0) random = 1;
        double slow = 0, band = 0, energy = 0, power = 0;
        for (var i = 0; i < signal.Length; i++)
        {
            if ((i & 1023) == 0) ct.ThrowIfCancellationRequested();
            random ^= random << 13; random ^= random >> 17; random ^= random << 5;
            var white = (int)(random >> 8) / 8388608.0 - 1;
            slow += 0.17 * (white - slow);
            band += 0.64 * (white - slow - band);
            energy += 0.004 * ((double)signal[i] * signal[i] - energy);
            var sample = band * Math.Sqrt(energy);
            noise[i] = (float)sample;
            power += sample * sample;
        }
        return power > 1e-14 ? targetNorm / Math.Sqrt(power) : 0;
    }
}