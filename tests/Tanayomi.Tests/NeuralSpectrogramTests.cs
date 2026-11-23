using Tanayomi.Desktop;
using System.Numerics;

internal static class NeuralSpectrogramTests
{
    private static void CheckFixedFft(Action<bool, string> check)
    {
        var transform = typeof(NeuralSpectrogram).GetMethod("Forward1024",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Complex[]>>();
        foreach (var seed in new[] { -2, -1, 0, 19, 781 })
        {
            var random = new Random(seed);
            var actual = Enumerable.Range(0, 1024).Select(i => seed == -2 ? (i == 273 ? Complex.One : Complex.Zero) :
                seed == -1 ? new Complex(Math.Sin(i * 0.027), 0) : seed == 0 ? Complex.Zero :
                new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1)).ToArray();
            var expected = (Complex[])actual.Clone();
            // Frozen pre-table recurrence: verify the optimization preserves
            // every bit, in addition to the independent direct-DFT checks below.
            for (int i = 1, j = 0; i < expected.Length; i++)
            {
                var bit = expected.Length >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (expected[i], expected[j]) = (expected[j], expected[i]);
            }
            for (var size = 2; size <= expected.Length; size <<= 1)
            {
                var increment = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
                for (var start = 0; start < expected.Length; start += size)
                {
                    var phase = Complex.One;
                    for (var offset = 0; offset < size / 2; offset++)
                    {
                        var left = expected[start + offset];
                        var right = phase * expected[start + offset + size / 2];
                        expected[start + offset] = left + right;
                        expected[start + offset + size / 2] = left - right;
                        phase *= increment;
                    }
                }
            }
            transform(actual);
            check(actual.Zip(expected).All(pair =>
                BitConverter.DoubleToInt64Bits(pair.First.Real) == BitConverter.DoubleToInt64Bits(pair.Second.Real) &&
                BitConverter.DoubleToInt64Bits(pair.First.Imaginary) == BitConverter.DoubleToInt64Bits(pair.Second.Imaginary)),
                $"fixed FFT phase table retains every real/imaginary bit for seed {seed}");
        }
    }

    internal static void MeasureResample(Action<bool, string> check)
    {
        var pcm = Enumerable.Range(0, 44100).Select(i => (short)(12000 * Math.Sin(i * 0.031))).ToArray();
        var old = LegacyResampleTo(pcm, 44100, 22050);
        var updated = NeuralSpectrogram.ResampleTo(pcm, 44100, 22050);
        check(old.Select(BitConverter.SingleToInt32Bits).SequenceEqual(updated.Select(BitConverter.SingleToInt32Bits)),
            "resample performance input preserves every output bit");
        var oldMs = new List<double>(); var newMs = new List<double>();
        for (var trial = 0; trial < 3; trial++)
        {
            // Alternate ordering to limit the simple warmup/order confound.
            for (var run = 0; run < 2; run++)
            {
                var legacy = (trial + run) % 2 == 0;
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                for (var i = 0; i < 10; i++)
                    _ = legacy ? LegacyResampleTo(pcm, 44100, 22050) : NeuralSpectrogram.ResampleTo(pcm, 44100, 22050);
                (legacy ? oldMs : newMs).Add(System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds / 10);
            }
        }
        Console.WriteLine($"NEURAL_RESAMPLE scope=same-process-1second-pcm trials=3 repetitions=10 oldMedianMs={oldMs.Order().ElementAt(1):F3} newMedianMs={newMs.Order().ElementAt(1):F3}");
    }

    internal static void Run(Action<bool, string> check)
    {
        CheckFixedFft(check);
        CheckInterleavedFftSpectrum(check);
        CheckResampleInterior(check);
        var tone = Enumerable.Range(0, 48000).Select(i => (short)Math.Round(15000 * Math.Sin(2 * Math.PI * 220 * i / 48000))).ToArray();
        var resampled = NeuralSpectrogram.Resample(tone, 48000);
        check(resampled.Length == 22050, "neural resampling has the expected sample count");
        var crossings = Enumerable.Range(1, resampled.Length - 1).Count(i => resampled[i - 1] < 0 && resampled[i] >= 0);
        check(Math.Abs(crossings - 220) <= 1, "neural resampling preserves pitch by independent zero crossing count");

        var high = Enumerable.Range(0, 48000).Select(i => (short)Math.Round(15000 * Math.Sin(2 * Math.PI * 14000 * i / 48000))).ToArray();
        var filtered = NeuralSpectrogram.Resample(high, 48000);
        var power = filtered.Skip(512).Take(filtered.Length - 1024).Average(x => (double)x * x);
        check(Math.Sqrt(power) < 0.002, "neural downsampling attenuates a tone above the new Nyquist limit");
        var dc = NeuralSpectrogram.Resample(Enumerable.Repeat((short)8192, 4096).ToArray(), 44100);
        check(dc.All(x => Math.Abs(x - 0.25f) < 0.00001), "neural resampling preserves DC including short reflected boundaries");
        var identity = NeuralSpectrogram.Resample([0, 32767, -32768], 22050);
        check(identity.SequenceEqual(new[] { 0f, 32767 / 32768f, -1f }), "neural identity-rate conversion preserves PCM16 scaling");
        var modelTone = Enumerable.Range(0, 22050).Select(i => (short)Math.Round(15000 * Math.Sin(2 * Math.PI * 220 * i / 22050))).ToArray();
        var upsampled = NeuralSpectrogram.ResampleTo(modelTone, 22050, 44100);
        var outputCrossings = Enumerable.Range(1, upsampled.Length - 1).Count(i => upsampled[i - 1] < 0 && upsampled[i] >= 0);
        check(upsampled.Length == 44100 && Math.Abs(outputCrossings - 220) <= 1, "neural 44100 Hz conversion preserves length and independently counted pitch");
        var imageReal = 0.0; var imageImaginary = 0.0;
        for (var i = 1024; i < upsampled.Length - 1024; ++i)
        {
            imageReal += upsampled[i] * Math.Cos(2 * Math.PI * 21830 * i / 44100);
            imageImaginary -= upsampled[i] * Math.Sin(2 * Math.PI * 21830 * i / 44100);
        }
        var imageAmplitude = 2 * Math.Sqrt(imageReal * imageReal + imageImaginary * imageImaginary) / (upsampled.Length - 2048);
        check(imageAmplitude < 0.002, "neural upsampling suppresses the independently measured 21830 Hz image of a 220 Hz tone");
        Throws(() => NeuralSpectrogram.ResampleTo(modelTone, 22050, 0), check, "neural rejects invalid output sample rates");

        var samples = Enumerable.Range(0, 4096).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * 20 * i / 1024))).ToArray();
        var spectrum = NeuralSpectrogram.Create(samples);
        check(spectrum.Frames == 16 && spectrum.Shape.SequenceEqual(new[] { 1, 513, 16 }) && spectrum.Magnitudes.Length == 8208,
            "neural STFT uses frequency-major ONNX shape and the upstream frame length");
        // A=0.5 sinusoid: positive-frequency amplitude A*N/2, then Hann
        // coherent gain 0.5, hence 0.5*1024/4 = 128.
        check(Math.Abs(spectrum.Magnitudes[20 * spectrum.Frames + 3] - 128) < 0.0001,
            "neural periodic-Hann STFT has independently known sinusoid amplitude");
        foreach (var frame in new[] { 0, 3, 15 })
        foreach (var bin in new[] { 0, 20, 511, 512 })
        {
            var real = 0.0; var imaginary = 0.0;
            for (var i = 0; i < 1024; ++i)
            {
                var index = frame * 256 - 384 + i;
                if (index < 0) index = -index;
                else if (index >= samples.Length) index = 2 * samples.Length - 2 - index;
                var value = samples[index] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 1024));
                real += value * Math.Cos(2 * Math.PI * bin * i / 1024);
                imaginary -= value * Math.Sin(2 * Math.PI * bin * i / 1024);
            }
            var expected = Math.Sqrt(real * real + imaginary * imaginary + 1e-6);
            check(Math.Abs(spectrum.Magnitudes[bin * spectrum.Frames + frame] - expected) < 0.0001,
                $"neural FFT agrees with independent direct DFT at frame {frame}, bin {bin}");
        }
        var silent = NeuralSpectrogram.Create(new float[2048]);
        check(silent.Magnitudes.All(x => Math.Abs(x - 0.001f) < 1e-8), "neural silent STFT retains upstream numerical floor");
        Throws(() => NeuralSpectrogram.Resample([], 48000), check, "neural rejects empty PCM");
        Throws(() => NeuralSpectrogram.Resample([1], 0), check, "neural rejects invalid sample rates");
        Throws(() => NeuralSpectrogram.Create(new float[2047]), check, "neural rejects references shorter than its encoder boundary");
        var invalid = new float[2048]; invalid[10] = float.NaN;
        Throws(() => NeuralSpectrogram.Create(invalid), check, "neural rejects nonfinite PCM");
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var cancelled = false;
        try { NeuralSpectrogram.Resample(tone, 48000, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "neural resampling responds to an existing stop request before allocating phase tables");
        cancelled = false;
        try { NeuralSpectrogram.Create(samples, cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "neural STFT responds to an existing stop request");
    }

    private static void CheckInterleavedFftSpectrum(Action<bool, string> check)
    {
        var random = new Random(891643);
        foreach (var length in new[] { 2048, 2049, 2176, 4095, 4096, 4097, 22050 })
        {
            var pcm = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            var actual = NeuralSpectrogram.Create(pcm);
            var expected = new float[actual.Magnitudes.Length];
            var values = new Complex[1024];
            // Frozen scalar STFT: both reflected ends and interior frames use
            // the pre-AVX butterfly and its original phase recurrence.
            for (var frame = 0; frame < actual.Frames; frame++)
            {
                for (var i = 0; i < values.Length; i++)
                {
                    var index = frame * 256 - 384 + i;
                    if (index < 0) index = -index;
                    else if (index >= pcm.Length) index = 2 * pcm.Length - 2 - index;
                    values[i] = new Complex(pcm[index] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / 1024)), 0);
                }
                for (int i = 1, j = 0; i < values.Length; i++)
                {
                    var bit = values.Length >> 1;
                    for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                    j ^= bit;
                    if (i < j) (values[i], values[j]) = (values[j], values[i]);
                }
                for (var size = 2; size <= values.Length; size <<= 1)
                {
                    var increment = Complex.FromPolarCoordinates(1, -2 * Math.PI / size);
                    for (var start = 0; start < values.Length; start += size)
                    {
                        var phase = Complex.One;
                        for (var offset = 0; offset < size / 2; offset++)
                        {
                            var left = values[start + offset];
                            var right = phase * values[start + offset + size / 2];
                            values[start + offset] = left + right;
                            values[start + offset + size / 2] = left - right;
                            phase *= increment;
                        }
                    }
                }
                for (var bin = 0; bin < 513; bin++)
                {
                    var value = values[bin];
                    expected[bin * actual.Frames + frame] = (float)Math.Sqrt(
                        value.Real * value.Real + value.Imaginary * value.Imaginary + 1e-6);
                }
            }
            check(actual.Magnitudes.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits)),
                $"interleaved FFT preserves every scalar STFT magnitude bit at length {length}");
        }
    }

    private static void CheckResampleInterior(Action<bool, string> check)
    {
        var random = new Random(426031);
        foreach (var sourceRate in new[] { 8000, 22050, 44100, 48000, 96000, 192000 })
        foreach (var targetRate in new[] { 22050, 44100, Math.Min(192000, sourceRate * 2) }.Distinct())
        {
            var exact = true;
            foreach (var length in new[] { 1, 2, 7, 18, 19, 31, 37, 38, 39, 40, 41, 64, 257, 4096, 4097 })
            {
                if (Math.Round((double)length * targetRate / sourceRate, MidpointRounding.ToEven) < 1) continue;
                var pcm = Enumerable.Range(0, length).Select(_ => (short)random.Next(short.MinValue, short.MaxValue + 1)).ToArray();
                pcm[0] = short.MinValue;
                if (length > 1) pcm[^1] = short.MaxValue;
                var expected = LegacyResampleTo(pcm, sourceRate, targetRate);
                var actual = NeuralSpectrogram.ResampleTo(pcm, sourceRate, targetRate);
                exact &= actual.Select(BitConverter.SingleToInt32Bits).SequenceEqual(expected.Select(BitConverter.SingleToInt32Bits));
            }
            check(exact, $"neural resampling {sourceRate} to {targetRate} remains bit-exact to the old per-tap reflection path for random PCM, short edges and interior windows");
        }
    }

    // Keep the previous coefficient construction and per-tap edge selection as
    // an independent bit-level oracle for the interior-only fast path.
    private static float[] LegacyResampleTo(short[] pcm, int sourceRate, int targetRate)
    {
        var count = checked((int)Math.Round((double)pcm.Length * targetRate / sourceRate, MidpointRounding.ToEven));
        var result = new float[count];
        if (sourceRate == targetRate)
        {
            for (var i = 0; i < count; ++i) result[i] = pcm[i] / 32768f;
            return result;
        }
        var cutoff = Math.Min(1.0, (double)targetRate / sourceRate) * 0.94;
        var radius = (int)Math.Ceiling(16 / cutoff);
        var a = sourceRate; var b = targetRate;
        while (b != 0) (a, b) = (b, a % b);
        var divisor = a;
        var phases = targetRate / divisor;
        var kernels = new double[phases][];
        for (var phase = 0; phase < phases; ++phase)
        {
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
        static int Reflect(int index, int length)
        {
            if (length == 1) return 0;
            var period = 2 * length - 2;
            index %= period;
            if (index < 0) index += period;
            return index < length ? index : period - index;
        }
        for (var i = 0; i < count; ++i)
        {
            var position = (long)i * sourceRate;
            var center = (int)(position / targetRate);
            var kernel = kernels[(int)(position % targetRate) / divisor];
            var first = center - radius + 1;
            var sample = 0.0;
            for (var tap = 0; tap < kernel.Length; ++tap)
            {
                var index = first + tap;
                sample += pcm[index >= 0 && index < pcm.Length ? index : Reflect(index, pcm.Length)] * kernel[tap];
            }
            result[i] = (float)(sample / 32768.0);
        }
        return result;
    }

    private static void Throws(Action operation, Action<bool, string> check, string name)
    {
        var rejected = false;
        try { operation(); } catch (InvalidDataException) { rejected = true; }
        check(rejected, name);
    }
}
