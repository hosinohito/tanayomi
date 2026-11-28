using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tanayomi;

public enum WaveformPitchMethod { Psola, PhaseVocoder }

// Independent experimental implementations. No third-party DSP code. A short
// autocorrelation observer is deliberately conservative: noise, closures and
// uncertain periodicity keep their source waveform instead of being voiced.
public static class WaveformPitch
{
    private const int Rate = 44100, Hop = 882, Window = 2205;
    public static double Measure(PcmAudio input, CancellationToken ct)
    {
        var samples = Samples(input, ct);
        var pitches = Analyze(samples, ct).Where(x => x > 0).Select(x => Rate / x).Order().ToArray();
        if (pitches.Length < 5) throw new InvalidDataException("基準音程を検出できません。");
        return pitches[pitches.Length / 2];
    }

    public static PcmAudio Process(PcmAudio input, double target, double[]? contour, WaveformPitchMethod method, CancellationToken ct)
    {
        if (!Enum.IsDefined(method) || !double.IsFinite(target) || target is < 90 or > 735)
            throw new InvalidDataException("波形変換の指定が不正です。");
        var samples = Samples(input, ct);
        if (contour is not null && (contour.Length != (int)(1000.0 * samples.Length / Rate / 5) + 1 ||
            contour.Any(x => !double.IsFinite(x) || x is < 40 or > 1200)))
            throw new InvalidDataException("抑揚の形式が不正です。");
        var periods = Analyze(samples, ct);
        var desired = new double[periods.Length];
        double sum = 0; var count = 0;
        for (var i = 0; i < periods.Length; i++)
            if (periods[i] > 0) { sum += Curve(i * Hop); count++; }
        if (count == 0) return new((short[])input.Samples.Clone(), Rate);
        var mean = sum / count;
        for (var i = 0; i < desired.Length; i++)
            desired[i] = Math.Clamp(target * Curve(i * Hop) / mean, target / 1.26, target * 1.26);
        // Both algorithms retain duration; ratios are limited to four semitones
        // around each confidently observed local source pitch.
        var changed = method == WaveformPitchMethod.Psola
            ? Psola(samples, periods, desired, ct) : Phase(samples, periods, desired, ct);
        ct.ThrowIfCancellationRequested();
        var peak = OutputPeak(changed);
        if (!double.IsFinite(peak)) throw new InvalidDataException("波形変換の出力が不正です。");
        var gain = peak > 0.92 ? 0.92 / peak : 1;
        var pcm = QuantizePcm(changed, gain, ct);
        return new(pcm, Rate);

        double Curve(int sample)
        {
            if (contour is null) return target;
            var at = sample / (Rate * 0.005); var index = Math.Min((int)at, contour.Length - 1);
            return contour[index] + (contour[Math.Min(index + 1, contour.Length - 1)] - contour[index]) * (at - index);
        }
    }

    private static short[] QuantizePcm(float[] changed, double gain, CancellationToken ct)
    {
        var pcm = new short[changed.Length];
        var i = 0;
        if (Avx2.IsSupported)
        {
            var gainVector = Vector256.Create(gain); var scale = Vector256.Create(32768.0);
            var finiteLimit = Vector256.Create(double.MaxValue); var sign = Vector256.Create(-0.0);
            var low = Vector256.Create(-30147.0); var high = Vector256.Create(30147.0);
            ref var input = ref MemoryMarshal.GetReference(changed.AsSpan());
            ref var output = ref MemoryMarshal.GetReference(pcm.AsSpan());
            for (; i <= pcm.Length - 8; i += 8)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                // Preserve float-to-double widening and both double products.
                var first = Avx.Multiply(Avx.Multiply(Avx.ConvertToVector256Double(
                    Vector128.LoadUnsafe(ref input, (nuint)i)), gainVector), scale);
                var second = Avx.Multiply(Avx.Multiply(Avx.ConvertToVector256Double(
                    Vector128.LoadUnsafe(ref input, (nuint)(i + 4))), gainVector), scale);
                if (Avx.MoveMask(Avx.CompareLessThanOrEqual(Avx.AndNot(sign, first), finiteLimit)) == 15 &&
                    Avx.MoveMask(Avx.CompareLessThanOrEqual(Avx.AndNot(sign, second), finiteLimit)) == 15)
                {
                    first = Avx.Max(low, Avx.Min(high, Avx.RoundToNearestInteger(first)));
                    second = Avx.Max(low, Avx.Min(high, Avx.RoundToNearestInteger(second)));
                    Sse2.PackSignedSaturate(Avx.ConvertToVector128Int32WithTruncation(first),
                        Avx.ConvertToVector128Int32WithTruncation(second)).StoreUnsafe(ref output, (nuint)i);
                }
                else
                    for (var lane = 0; lane < 8; lane++)
                        pcm[i + lane] = (short)Math.Clamp(Math.Round(changed[i + lane] * gain * 32768), -30147, 30147);
            }
        }
        for (; i < pcm.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            pcm[i] = (short)Math.Clamp(Math.Round(changed[i] * gain * 32768), -30147, 30147);
        }
        return pcm;
    }

    private static float[] Samples(PcmAudio input, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (input is null || input.Samples is null || input.SampleRate != Rate || input.Samples.Length is < 4410 or > Rate * 45)
            throw new InvalidDataException("波形変換の入力が不正です。");
        return input.Samples.Select(x => x / 32768f).ToArray();
    }

    private static float OutputPeak(float[] changed)
    {
        if (!Vector.IsHardwareAccelerated || changed.Length < Vector<float>.Count)
            return changed.Max(x => Math.Abs(x));
        var maxima = Vector<float>.Zero;
        var limit = new Vector<float>(float.MaxValue);
        var i = 0;
        for (; i <= changed.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            var values = Vector.Abs(new Vector<float>(changed, i));
            // LINQ's NaN selection differs from rejecting every nonfinite value.
            // Preserve it exactly by using the original reduction on this path.
            if (!Vector.LessThanOrEqualAll(values, limit)) return changed.Max(x => Math.Abs(x));
            maxima = Vector.Max(maxima, values);
        }
        var peak = 0f;
        for (var lane = 0; lane < Vector<float>.Count; lane++) peak = Math.Max(peak, maxima[lane]);
        for (; i < changed.Length; i++)
        {
            var value = Math.Abs(changed[i]);
            if (!float.IsFinite(value)) return changed.Max(x => Math.Abs(x));
            peak = Math.Max(peak, value);
        }
        return peak;
    }

    private static double[] Analyze(float[] samples, CancellationToken ct)
    {
        var result = new double[(samples.Length + Hop - 1) / Hop];
        for (var i = 0; i < result.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var from = i * Hop - Window / 2;
            if (from < 0 || from + Window > samples.Length) continue;
            var span = samples.AsSpan(from, Window);
            var period = UtauPitch.FindPeriod(span, ct);
            if (period > 0 && UtauPitch.IsVoiced(span, period)) result[i] = period;
        }
        return result;
    }

    private static float[] Psola(float[] input, double[] periods, double[] desired, CancellationToken ct)
    {
        var output = new float[input.Length]; var weights = new float[input.Length];
        float[] pulseWindow = [];
        var windowRadius = -1;
        for (var frame = 0; frame < periods.Length;)
        {
            ct.ThrowIfCancellationRequested();
            if (periods[frame] <= 0) { frame++; continue; }
            var first = frame++;
            while (frame < periods.Length && periods[frame] > 0 && Math.Abs(Math.Log(periods[frame] / periods[frame - 1])) < 0.18) frame++;
            var start = first * Hop; var end = Math.Min(input.Length, (frame - 1) * Hop);
            if (end - start < Rate / 12) continue;
            var marks = new List<int>();
            var at = Peak(start, Math.Min(end, start + (int)periods[first]));
            marks.Add(at);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var period = periods[Math.Min(at / Hop, frame - 1)];
                var next = at + period;
                if (next + period * 0.2 >= end) break;
                at = Peak((int)(next - period * 0.2), (int)(next + period * 0.2));
                marks.Add(at);
            }
            var cursor = 0;
            for (double position = marks[0]; position < end;)
            {
                ct.ThrowIfCancellationRequested();
                while (cursor + 1 < marks.Count && Math.Abs(marks[cursor + 1] - position) < Math.Abs(marks[cursor] - position)) cursor++;
                var source = marks[cursor]; var center = (int)Math.Round(position);
                var index = Math.Clamp(center / Hop, first, frame - 1);
                var radius = (int)Math.Round(periods[source / Hop]);
                if (radius != windowRadius)
                {
                    FillPulseWindow(ref pulseWindow, radius);
                    windowRadius = radius;
                }
                var firstOffset = Math.Max(-radius, Math.Max(start - source, start - center));
                var lastOffset = Math.Min(radius, Math.Min(end - 1 - source, end - 1 - center));
                for (var j = firstOffset; j <= lastOffset; j++)
                {
                    var origin = source + j; var destination = center + j;
                    var weight = pulseWindow[j + radius];
                    output[destination] += input[origin] * weight; weights[destination] += weight;
                }
                var ratio = Math.Clamp(desired[index] * periods[index] / Rate, 1 / 1.26, 1.26);
                position += periods[index] / ratio;
            }
            // Process owns this array. This run has finished reading its samples,
            // and later runs start beyond this range.
            for (var i = start; i < end; i++)
            {
                var edge = Math.Min(i - start, end - 1 - i);
                var blend = edge >= Hop ? 1.0 : 0.5 - 0.5 * Math.Cos(Math.PI * Math.Clamp(edge / (double)Hop, 0, 1));
                if (weights[i] > 0.1) input[i] = (float)(input[i] * (1 - blend) + output[i] / weights[i] * blend);
            }
        }
        return input;

        int Peak(int from, int to)
        {
            var best = from;
            for (var i = from + 1; i < to; i++) if (input[i] > input[best]) best = i;
            return best;
        }
    }

    private static void FillPulseWindow(ref float[] window, int radius)
    {
        var length = checked(radius * 2 + 1);
        if (window.Length < length) window = new float[length];
        for (var j = -radius; j <= radius; j++)
            window[j + radius] = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / radius));
    }

    private static float[] Phase(float[] input, double[] periods, double[] desired, CancellationToken ct)
    {
        const int size = 2048, step = 256, half = size / 2;
        var spectrum = new Complex[size];
        var previous = new double[half + 1]; var phase = new double[half + 1];
        var magnitudes = new double[half + 1]; var envelope = new double[half + 1];
        var logMagnitudes = new double[half + 1];
        var amplitudes = new double[half + 1]; var frequencies = new double[half + 1];
        var output = new double[input.Length]; var weights = new double[input.Length];
        var window = PhaseTables.Window;
        var previousVoiced = false;
        for (var center = 0; center < input.Length + half; center += step)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = 0; j < size; j++)
            {
                var i = center - half + j;
                spectrum[j] = (uint)i < (uint)input.Length ? new(input[i] * window[j], 0) : Complex.Zero;
            }
            Fft(spectrum, false);
            var frame = Math.Min(center / Hop, periods.Length - 1);
            var voiced = frame > 0 && frame + 1 < periods.Length && periods[frame - 1] > 0 && periods[frame] > 0 && periods[frame + 1] > 0;
            if (!voiced)
            {
                // Unvoiced frames keep the original spectrum for inverse FFT.
                // Retain phase history for the next voiced frame, while skipping
                // envelope and destination accumulators that are never consumed.
                for (var k = 0; k <= half; k++)
                {
                    var angle = spectrum[k].Phase;
                    previous[k] = angle;
                    if (!previousVoiced) phase[k] = angle;
                }
            }
            else
            {
                var ratio = Math.Clamp(desired[frame] * periods[frame] / Rate, 1 / 1.26, 1.26);
                for (var k = 0; k <= half; k++) magnitudes[k] = spectrum[k].Magnitude;
                // A bounded log-magnitude smoothing approximation, not the iterative
                // true-envelope algorithm or a claim of exact formant preservation.
                var radius = Math.Clamp((int)(size / periods[frame]), 4, 32);
                BuildEnvelope(magnitudes, logMagnitudes, envelope, radius);
                Array.Clear(amplitudes); Array.Clear(frequencies);
                for (var k = 0; k <= half; k++)
                {
                    var angle = spectrum[k].Phase;
                    var omega = 2 * Math.PI * k / size;
                    var delta = Math.IEEERemainder(angle - previous[k] - omega * step, 2 * Math.PI);
                    previous[k] = angle;
                    var destination = (int)Math.Round(k * ratio);
                    if (destination > half) continue;
                    var amplitude = magnitudes[k] * Math.Clamp(envelope[destination] / Math.Max(1e-9, envelope[k]), 0.25, 4);
                    amplitudes[destination] += amplitude;
                    frequencies[destination] += amplitude * (omega + delta / step) * ratio;
                    if (!previousVoiced) phase[destination] = angle;
                }
            }
            if (voiced)
            {
                // The lower half and its conjugate cover every bin before the inverse FFT.
                for (var k = 0; k <= half; k++)
                {
                    if (amplitudes[k] > 1e-9 && previousVoiced) phase[k] += step * frequencies[k] / amplitudes[k];
                    phase[k] = Math.IEEERemainder(phase[k], 2 * Math.PI);
                    spectrum[k] = Complex.FromPolarCoordinates(amplitudes[k], phase[k]);
                    if (k > 0 && k < half) spectrum[size - k] = Complex.Conjugate(spectrum[k]);
                }
                spectrum[0] = new(spectrum[0].Real, 0); spectrum[half] = new(spectrum[half].Real, 0);
            }
            previousVoiced = voiced;
            Fft(spectrum, true);
            for (var j = 0; j < size; j++)
            {
                var i = center - half + j;
                if ((uint)i >= (uint)input.Length) continue;
                output[i] += spectrum[j].Real * window[j]; weights[i] += window[j] * window[j];
            }
        }
        // Process passes its private sample array. All frame reads are complete,
        // so each sample can now be replaced after reading its original value.
        for (var i = 0; i < input.Length; i++)
        {
            var frame = i / Hop;
            // Two neighboring confidence decisions protect consonant edges.
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (frame < 2 || frame + 2 >= periods.Length || weights[i] < 0.1 ||
                periods[frame - 2] <= 0 || periods[frame - 1] <= 0 || periods[frame] <= 0 || periods[frame + 1] <= 0 || periods[frame + 2] <= 0) continue;
            // Interior voiced samples use the reconstructed signal; only runs'
            // first/last cells fade. Do not fade at every frame boundary.
            var blend = periods[frame - 3 < 0 ? 0 : frame - 3] <= 0 ? (i % Hop) / (double)Hop :
                frame + 3 >= periods.Length || periods[frame + 3] <= 0 ? 1 - (i % Hop) / (double)Hop : 1;
            input[i] = (float)(input[i] * (1 - blend) + output[i] / weights[i] * blend);
        }
        return input;
    }

    internal static void BuildEnvelope(double[] magnitudes, double[] logarithms, double[] envelope, int radius)
    {
        // Neighboring windows reuse the same logarithms. Keep each window's
        // original summation order; a rolling sum would change rounding.
        for (var k = 0; k < magnitudes.Length; k++)
            logarithms[k] = Math.Log(Math.Max(1e-9, magnitudes[k]));
        for (var k = 0; k < magnitudes.Length; k++)
        {
            double total = 0;
            var low = Math.Max(0, k - radius);
            var high = Math.Min(magnitudes.Length - 1, k + radius);
            for (var j = low; j <= high; j++) total += logarithms[j];
            envelope[k] = Math.Exp(total / (high - low + 1));
        }
    }

    // Initialized only when a spectral algorithm is used, never from input voice data.
    internal static class PhaseTables
    {
        internal const int Size = 2048;
        internal static readonly bool UseAvxFft = Avx.IsSupported && HasInterleavedComplexLayout();
        internal static readonly double[] Window = Enumerable.Range(0, Size)
            .Select(i => 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / Size)).ToArray();
        internal static readonly Complex[][] Forward = Rotations(false);
        internal static readonly Complex[][] Inverse = Rotations(true);
        internal static readonly (int Left, int Right)[] Swaps = CreateSwaps();

        private static bool HasInterleavedComplexLayout()
        {
            Span<Complex> values = stackalloc Complex[2];
            values[0] = new Complex(13.25, -7.5); values[1] = new Complex(2.75, 9.125);
            var parts = MemoryMarshal.Cast<Complex, double>(values);
            return parts.Length == 4 && parts[0] == 13.25 && parts[1] == -7.5 && parts[2] == 2.75 && parts[3] == 9.125;
        }

        private static Complex[][] Rotations(bool inverse)
        {
            var result = new Complex[11][];
            for (int stage = 0, length = 2; length <= Size; stage++, length <<= 1)
            {
                var rotation = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
                var factor = Complex.One;
                result[stage] = new Complex[length / 2];
                for (var j = 0; j < length / 2; j++)
                {
                    result[stage][j] = factor;
                    factor *= rotation;
                }
            }
            return result;
        }

        private static (int, int)[] CreateSwaps()
        {
            var result = new List<(int, int)>();
            for (int i = 1, j = 0; i < Size; i++)
            {
                var bit = Size >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) result.Add((i, j));
            }
            return result.ToArray();
        }
    }

    internal static void Fft(Complex[] data, bool inverse)
    {
        if (data.Length != PhaseTables.Size) throw new ArgumentException("Invalid phase FFT size.", nameof(data));
        foreach (var (left, right) in PhaseTables.Swaps)
            (data[left], data[right]) = (data[right], data[left]);
        var rotations = inverse ? PhaseTables.Inverse : PhaseTables.Forward;
        var parts = PhaseTables.UseAvxFft ? MemoryMarshal.Cast<Complex, double>(data.AsSpan()) : default;
        ref var first = ref MemoryMarshal.GetReference(parts);
        for (int stage = 0, length = 2; length <= data.Length; stage++, length <<= 1)
        {
            var factors = rotations[stage];
            if (PhaseTables.UseAvxFft && length >= 4)
            {
                var coefficients = MemoryMarshal.Cast<Complex, double>(factors.AsSpan());
                ref var coefficient = ref MemoryMarshal.GetReference(coefficients);
                for (var start = 0; start < data.Length; start += length)
                for (var j = 0; j < length / 2; j += 2)
                {
                    var leftIndex = (nuint)(2 * (start + j));
                    var rightIndex = (nuint)(2 * (start + j + length / 2));
                    var phase = Vector256.LoadUnsafe(ref coefficient, (nuint)(2 * j));
                    var input = Vector256.LoadUnsafe(ref first, rightIndex);
                    // Keep the original component products and sums separate; no FMA.
                    var odd = Avx.AddSubtract(
                        Avx.Multiply(input, Avx.Permute(phase, 0)),
                        Avx.Multiply(Avx.Permute(input, 5), Avx.Permute(phase, 15)));
                    var even = Vector256.LoadUnsafe(ref first, leftIndex);
                    Avx.Add(even, odd).StoreUnsafe(ref first, leftIndex);
                    Avx.Subtract(even, odd).StoreUnsafe(ref first, rightIndex);
                }
            }
            else
            {
                for (var start = 0; start < data.Length; start += length)
                for (var j = 0; j < length / 2; j++)
                {
                    var even = data[start + j]; var odd = data[start + j + length / 2] * factors[j];
                    data[start + j] = even + odd; data[start + j + length / 2] = even - odd;
                }
            }
        }
        if (inverse) NormalizeInverse(data);
    }

    // Called only after Fft's fixed-size check. Keep Complex's special handling
    // of nonfinite components rather than treating them as independent doubles.
    private static void NormalizeInverse(Complex[] data)
    {
        if (PhaseTables.UseAvxFft)
        {
            var parts = MemoryMarshal.Cast<Complex, double>(data.AsSpan());
            ref var first = ref MemoryMarshal.GetReference(parts);
            var divisor = Vector256.Create((double)data.Length);
            var finiteLimit = Vector256.Create(double.MaxValue);
            var signMask = Vector256.Create(-0.0);
            for (var i = 0; i < parts.Length; i += 4)
            {
                var value = Vector256.LoadUnsafe(ref first, (nuint)i);
                if (Avx.MoveMask(Avx.CompareLessThanOrEqual(Avx.AndNot(signMask, value), finiteLimit)) == 15)
                    Avx.Divide(value, divisor).StoreUnsafe(ref first, (nuint)i);
                else
                {
                    data[i / 2] /= data.Length;
                    data[i / 2 + 1] /= data.Length;
                }
            }
        }
        else for (var i = 0; i < data.Length; i++) data[i] /= data.Length;
    }
}
