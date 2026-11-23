using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tanayomi.Desktop;

/// <summary>Managed, bounded input boundary for the optional OpenVoice ONNX
/// graphs. No native runtime, model, file, playback or input-dependent global cache is created.</summary>
internal static class NeuralSpectrogram
{
    internal const int SampleRate = 22050;
    internal const int FilterLength = 1024;
    internal const int HopLength = 256;
    internal const int Bins = FilterLength / 2 + 1;
    internal const int MaximumSeconds = 45;
    private const int Pad = (FilterLength - HopLength) / 2;
    // Fixed phases, window and swap positions only, independent of all voice
    // data and utterance lengths. No mutable FFT workspace is shared.
    private static readonly Complex[][] Phases = CreatePhases();
    private static readonly double[] Window = CreateWindow();
    private static readonly (int Left, int Right)[] Swaps = CreateSwaps();
    private static readonly bool UseAvxFft = Avx.IsSupported && HasInterleavedComplexLayout();

    private static bool HasInterleavedComplexLayout()
    {
        Span<Complex> values = stackalloc Complex[2];
        values[0] = new Complex(13.25, -7.5);
        values[1] = new Complex(2.75, 9.125);
        var components = MemoryMarshal.Cast<Complex, double>(values);
        return components.Length == 4 && components[0] == 13.25 && components[1] == -7.5 &&
            components[2] == 2.75 && components[3] == 9.125;
    }

    private static (int, int)[] CreateSwaps()
    {
        var swaps = new List<(int, int)>();
        for (int i = 1, j = 0; i < FilterLength; ++i)
        {
            var bit = FilterLength >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) swaps.Add((i, j));
        }
        return swaps.ToArray();
    }

    private static double[] CreateWindow()
    {
        var values = new double[FilterLength];
        for (var i = 0; i < values.Length; i++)
            values[i] = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / FilterLength);
        return values;
    }

    private static Complex[][] CreatePhases()
    {
        var stages = new Complex[10][];
        for (int stage = 0, size = 2; size <= FilterLength; stage++, size <<= 1)
        {
            var values = new Complex[size / 2];
            var increment = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
            var phase = Complex.One;
            for (var offset = 0; offset < values.Length; offset++)
            {
                values[offset] = phase;
                phase *= increment;
            }
            stages[stage] = values;
        }
        return stages;
    }

    internal sealed record Spectrum(float[] Magnitudes, int Frames)
    {
        // ONNX input layout is [batch=1, frequency=513, time=Frames].
        internal int[] Shape => [1, Bins, Frames];
    }

    internal static float[] Resample(ReadOnlySpan<short> pcm, int sourceRate, CancellationToken ct = default)
        => ResampleTo(pcm, sourceRate, SampleRate, ct);

    internal static float[] ResampleTo(ReadOnlySpan<short> pcm, int sourceRate, int targetRate, CancellationToken ct = default)
    {
        if (sourceRate is < 8000 or > 192000 || targetRate is < 8000 or > 192000 || pcm.Length < 1 || pcm.Length > (long)sourceRate * MaximumSeconds)
            throw new InvalidDataException("ニューラル音声の入力形式または長さが不正です。");
        ct.ThrowIfCancellationRequested();
        var count = checked((int)Math.Round((double)pcm.Length * targetRate / sourceRate, MidpointRounding.ToEven));
        if (count < 1 || count > targetRate * MaximumSeconds)
            throw new InvalidDataException("ニューラル音声の変換長が不正です。");
        var result = new float[count];
        if (sourceRate == targetRate)
        {
            for (var i = 0; i < count; ++i)
            {
                if ((i & 2047) == 0) ct.ThrowIfCancellationRequested();
                result[i] = pcm[i] / 32768f;
            }
            return result;
        }

        // Windowed-sinc low-pass filtering precedes decimation. A phase table
        // preserves exact rational sample positions without per-sample trig.
        // Tables live only for this bounded conversion; unusual rates cannot
        // accumulate an unbounded global cache. Reflected edges avoid a DC dip.
        var cutoff = Math.Min(1.0, (double)targetRate / sourceRate) * 0.94;
        var radius = (int)Math.Ceiling(16 / cutoff);
        var divisor = Gcd(sourceRate, targetRate);
        var phases = targetRate / divisor;
        var kernels = new double[phases][];
        for (var phase = 0; phase < phases; ++phase)
        {
            if ((phase & 63) == 0) ct.ThrowIfCancellationRequested();
            var kernel = new double[2 * radius];
            var fractional = (double)phase / phases;
            var sum = 0.0;
            for (var tap = 0; tap < kernel.Length; ++tap)
            {
                var distance = tap - radius + 1 - fractional;
                var x = Math.PI * cutoff * distance;
                var sinc = Math.Abs(x) < 1e-12 ? 1 : Math.Sin(x) / x;
                var window = 0.42 + 0.5 * Math.Cos(Math.PI * distance / radius) + 0.08 * Math.Cos(2 * Math.PI * distance / radius);
                kernel[tap] = cutoff * sinc * window;
                sum += kernel[tap];
            }
            for (var tap = 0; tap < kernel.Length; ++tap) kernel[tap] /= sum;
            kernels[phase] = kernel;
        }
        for (var i = 0; i < count; ++i)
        {
            if ((i & 2047) == 0) ct.ThrowIfCancellationRequested();
            var position = (long)i * sourceRate;
            var center = (int)(position / targetRate);
            var kernel = kernels[(int)(position % targetRate) / divisor];
            var first = center - radius + 1;
            var sample = 0.0;
            if (first >= 0 && first + kernel.Length <= pcm.Length)
            {
                var window = pcm.Slice(first, kernel.Length);
                for (var tap = 0; tap < kernel.Length; ++tap) sample += window[tap] * kernel[tap];
            }
            else
            {
                for (var tap = 0; tap < kernel.Length; ++tap)
                {
                    var index = first + tap;
                    sample += pcm[index >= 0 && index < pcm.Length ? index : Reflect(index, pcm.Length)] * kernel[tap];
                }
            }
            result[i] = (float)(sample / 32768.0);
        }
        return result;
    }

    internal static Spectrum Create(ReadOnlySpan<float> pcm, CancellationToken ct = default)
    {
        if (pcm.Length is < 2048 or > SampleRate * MaximumSeconds)
            throw new InvalidDataException("ニューラル音声の特徴量入力長が不正です。");
        ct.ThrowIfCancellationRequested();
        foreach (var sample in pcm)
            if (!float.IsFinite(sample) || Math.Abs(sample) > 2)
                throw new InvalidDataException("ニューラル音声の入力に不正な数値があります。");
        var frames = (pcm.Length + 2 * Pad - FilterLength) / HopLength + 1;
        var magnitude = new float[checked(Bins * frames)];
        var spectrum = new Complex[FilterLength];
        for (var frame = 0; frame < frames; ++frame)
        {
            if ((frame & 31) == 0) ct.ThrowIfCancellationRequested();
            var start = frame * HopLength - Pad;
            for (var i = 0; i < spectrum.Length; ++i)
            {
                var index = start + i;
                var sample = pcm[index >= 0 && index < pcm.Length ? index : Reflect(index, pcm.Length)];
                spectrum[i] = new Complex(sample * Window[i], 0);
            }
            Forward1024(spectrum);
            for (var bin = 0; bin < Bins; ++bin)
            {
                var value = spectrum[bin];
                magnitude[bin * frames + frame] = (float)Math.Sqrt(value.Real * value.Real + value.Imaginary * value.Imaginary + 1e-6);
            }
        }
        return new Spectrum(magnitude, frames);
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }

    private static int Reflect(int index, int length)
    {
        if (length == 1) return 0;
        var period = 2 * length - 2;
        index %= period;
        if (index < 0) index += period;
        return index < length ? index : period - index;
    }

    // This fixed-size transform stays private to this input boundary. The
    // existing waveform-pitch FFT is private to its independent algorithm.
    private static void Forward1024(Complex[] values)
    {
        foreach (var (left, right) in Swaps)
            (values[left], values[right]) = (values[right], values[left]);
        var data = UseAvxFft ? MemoryMarshal.Cast<Complex, double>(values.AsSpan()) : default;
        ref var first = ref MemoryMarshal.GetReference(data);
        for (int stage = 0, size = 2; size <= values.Length; stage++, size <<= 1)
        {
            var phases = Phases[stage];
            if (UseAvxFft && size >= 4)
            {
                var coefficients = MemoryMarshal.Cast<Complex, double>(phases.AsSpan());
                ref var coefficient = ref MemoryMarshal.GetReference(coefficients);
                for (var start = 0; start < values.Length; start += size)
                for (var offset = 0; offset < size / 2; offset += 2)
                {
                    var leftIndex = (nuint)(2 * (start + offset));
                    var rightIndex = (nuint)(2 * (start + offset + size / 2));
                    var phase = Vector256.LoadUnsafe(ref coefficient, (nuint)(2 * offset));
                    var input = Vector256.LoadUnsafe(ref first, rightIndex);
                    // Preserve ac-bd and bc+ad as separate products and sums; no FMA.
                    var right = Avx.AddSubtract(
                        Avx.Multiply(Avx.Permute(phase, 0), input),
                        Avx.Multiply(Avx.Permute(phase, 15), Avx.Permute(input, 5)));
                    var left = Vector256.LoadUnsafe(ref first, leftIndex);
                    Avx.Add(left, right).StoreUnsafe(ref first, leftIndex);
                    Avx.Subtract(left, right).StoreUnsafe(ref first, rightIndex);
                }
            }
            else
            {
                for (var start = 0; start < values.Length; start += size)
                for (var offset = 0; offset < size / 2; ++offset)
                {
                    var left = values[start + offset];
                    var right = phases[offset] * values[start + offset + size / 2];
                    values[start + offset] = left + right;
                    values[start + offset + size / 2] = left - right;
                }
            }
        }
    }
}
