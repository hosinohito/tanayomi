using Tanayomi;
using System.Numerics;

internal static class WaveformPitchTests
{
    private static void CheckDoublePcm(Action<bool, string> check)
    {
        var quantize = typeof(WaveformPitch).GetMethod("QuantizePcm",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<float[], double, CancellationToken, short[]>>();
        foreach (var length in new[] { 0, 1, 7, 8, 9, 15, 16, 17, 4095, 4096, 4097 })
        foreach (var gain in new[] { 1.0, 0.92, 0.92 / 1.3 })
        {
            var input = Enumerable.Range(0, length).Select(i => (float)(0.8 * Math.Sin(i * 0.031))).ToArray();
            var half = (float)(0.5 / (gain * 32768)); var clip = (float)(30147.5 / (gain * 32768));
            var special = new[] { 0f, -0f, float.Epsilon, -float.Epsilon, float.MaxValue, -float.MaxValue,
                float.NaN, float.PositiveInfinity, float.NegativeInfinity, half, -half, float.BitDecrement(half),
                float.BitIncrement(half), clip, -clip, float.BitDecrement(clip), float.BitIncrement(clip) };
            bool Same()
            {
                var bits = input.Select(BitConverter.SingleToInt32Bits).ToArray();
                var expected = input.Select(value => (short)Math.Clamp(Math.Round(value * gain * 32768), -30147, 30147)).ToArray();
                return quantize(input, gain, default).SequenceEqual(expected) &&
                    input.Select(BitConverter.SingleToInt32Bits).SequenceEqual(bits);
            }
            var same = Same();
            foreach (var position in new[] { 0, 3, 4, 7, 8, length - 1 }.Where(i => i >= 0 && i < length).Distinct())
            foreach (var value in special)
            {
                var previous = input[position]; input[position] = value;
                same &= Same(); input[position] = previous;
            }
            check(same, $"waveform PCM preserves double product/ToEven/clip and scalar nonfinite results without changing input: length={length}, gain={gain}");
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        foreach (var length in new[] { 1, 8, 4097 })
        {
            var cancelled = false;
            try { _ = quantize(new float[length], 1, stop.Token); }
            catch (OperationCanceledException error) { cancelled = error.CancellationToken == stop.Token; }
            check(cancelled, "waveform PCM retains caller cancellation before vector/scalar output");
        }
    }

    private delegate double Correlate(ReadOnlySpan<float> source, int lag);
    private delegate void CorrelateFour(ReadOnlySpan<float> source, int lag,
        ReadOnlySpan<double> leftSums, ReadOnlySpan<double> leftSquares, Span<double> scores);
    private delegate void FillWindow(ref float[] window, int radius);

    private static void CheckOutputPeak(Action<bool, string> check)
    {
        var peak = typeof(WaveformPitch).GetMethod("OutputPeak",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<float[], float>>();
        bool Same(float[] values) => BitConverter.SingleToInt32Bits(peak(values)) ==
            BitConverter.SingleToInt32Bits(values.Max(value => Math.Abs(value)));
        var width = Vector<float>.Count;
        var random = new Random(82429);
        foreach (var length in new[] { 1, width - 1, width, width + 1, width * 2 + 1, 4410 })
        {
            var values = Enumerable.Range(0, length).Select(_ => (float)((random.NextDouble() * 2 - 1) * 1.25)).ToArray();
            var same = Same(values);
            foreach (var position in new[] { 0, Math.Min(width - 1, length - 1), Math.Min(width, length - 1), length - 1 }.Distinct())
            foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity,
                float.MaxValue, -float.MaxValue, float.Epsilon, -float.Epsilon })
            {
                var original = values[position]; values[position] = value;
                same &= Same(values); values[position] = original;
            }
            same &= Same(Enumerable.Repeat(float.NaN, length).ToArray());
            same &= Same(Enumerable.Range(0, length).Select(i => i % 2 == 0 ? 0f : -0f).ToArray());
            check(same, $"waveform peak preserves LINQ float bits for finite, NaN, infinity and signed-zero inputs across lanes and tails: length={length}");
        }
        check(Throws<InvalidOperationException>(() => peak([])), "waveform peak retains the original empty-sequence failure");
    }

    private static void CheckPulseWindow(Action<bool, string> check)
    {
        var fill = typeof(WaveformPitch).GetMethod("FillPulseWindow",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<FillWindow>();
        float[] window = [];
        var same = true;
        foreach (var radius in Enumerable.Range(60, 431).Concat(new[] { 200, 60, 490, 180 }))
        {
            fill(ref window, radius);
            for (var j = -radius; j <= radius; j++)
                same &= BitConverter.SingleToInt32Bits(window[j + radius]) ==
                    BitConverter.SingleToInt32Bits((float)(0.5 + 0.5 * Math.Cos(Math.PI * j / radius)));
        }
        check(same, "PSOLA reused window retains original float bits through period growth and shrinkage");
    }

    private static void CheckPhaseFft(Action<bool, string> check)
    {
        var normalize = typeof(WaveformPitch).GetMethod("NormalizeInverse",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Complex[]>>();
        var components = new[] { 0.0, -0.0, double.Epsilon, -double.Epsilon, 1e-310, -1e-310,
            1e-300, -1e-300, 1.25, -7.5, double.MaxValue, -double.MaxValue,
            double.NaN, double.PositiveInfinity, double.NegativeInfinity };
        var normalization = new Complex[2048];
        for (var i = 0; i < normalization.Length; i++)
            normalization[i] = new Complex(components[i % components.Length], components[i / components.Length % components.Length]);
        var scalarNormalization = normalization.Select(value => value / normalization.Length).ToArray();
        normalize(normalization);
        static bool SameComponent(double actual, double expected) => double.IsNaN(expected)
            ? double.IsNaN(actual) : BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected);
        check(normalization.Zip(scalarNormalization).All(pair => SameComponent(pair.First.Real, pair.Second.Real) &&
            SameComponent(pair.First.Imaginary, pair.Second.Imaginary)),
            "inverse FFT normalization retains scalar finite bits, signed zero, subnormals and nonfinite Complex rules in every lane");
        var fft = typeof(WaveformPitch).GetMethod("Fft",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<Complex[], bool>>();
        foreach (var inverse in new[] { false, true })
        foreach (var seed in new[] { 0, -1, -2, -3, -4, 31, 8204 })
        {
            var random = new Random(seed);
            var actual = Enumerable.Range(0, 2048).Select(i => seed switch
            {
                0 => Complex.Zero,
                -1 => new Complex(i % 2 == 0 ? 0.0 : -0.0, i % 3 == 0 ? -0.0 : 0.0),
                -2 => i == 0 ? Complex.One : Complex.Zero,
                -3 => new Complex(Math.Sin(i * 0.21), Math.Cos(i * 0.13)),
                -4 => new Complex((random.NextDouble() * 2 - 1) * 1e-300, (random.NextDouble() * 2 - 1) * 1e-300),
                _ => new Complex(random.NextDouble() * 2 - 1, random.NextDouble() * 2 - 1)
            }).ToArray();
            var expected = (Complex[])actual.Clone();
            // Frozen original recurrence: both directions must retain all bits.
            for (int i = 1, j = 0; i < expected.Length; i++)
            {
                var bit = expected.Length >> 1;
                for (; (j & bit) != 0; bit >>= 1) j ^= bit;
                j ^= bit;
                if (i < j) (expected[i], expected[j]) = (expected[j], expected[i]);
            }
            for (var length = 2; length <= expected.Length; length <<= 1)
            {
                var rotation = Complex.FromPolarCoordinates(1, (inverse ? 2 : -2) * Math.PI / length);
                for (var start = 0; start < expected.Length; start += length)
                {
                    var factor = Complex.One;
                    for (var j = 0; j < length / 2; j++)
                    {
                        var even = expected[start + j]; var odd = expected[start + j + length / 2] * factor;
                        expected[start + j] = even + odd; expected[start + j + length / 2] = even - odd;
                        factor *= rotation;
                    }
                }
            }
            if (inverse) for (var i = 0; i < expected.Length; i++) expected[i] /= expected.Length;
            fft(actual, inverse);
            check(actual.Zip(expected).All(pair =>
                BitConverter.DoubleToInt64Bits(pair.First.Real) == BitConverter.DoubleToInt64Bits(pair.Second.Real) &&
                BitConverter.DoubleToInt64Bits(pair.First.Imaginary) == BitConverter.DoubleToInt64Bits(pair.Second.Imaginary)),
                $"phase FFT fixed tables preserve all double bits: inverse={inverse}, seed={seed}");
        }
    }

    private static void CheckPeriodEarlyExit(Action<bool, string> check)
    {
        var correlate = typeof(UtauPitch).GetMethod("Correlation",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Correlate>();
        if (System.Runtime.Intrinsics.X86.Avx.IsSupported)
        {
            var batch = typeof(UtauPitch).GetMethod("CorrelationsFour",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
                .CreateDelegate<CorrelateFour>();
            foreach (var length in new[] { 184, 185, 186, 2203, 2204, 2205 })
            foreach (var seed in new[] { 0, 31, 487 })
            {
                var randomValues = new Random(seed);
                var source = Enumerable.Range(0, length).Select(i => seed == 0 ? 0f :
                    seed == 31 ? (float)(0.3 * Math.Sin(2 * Math.PI * 220 * i / 44100)) :
                    (float)(randomValues.NextDouble() * 2 - 1)).ToArray();
                var sums = new double[(length + 2) / 3]; var squares = new double[sums.Length];
                double sum = 0, square = 0;
                for (int i = 0, n = 0; i < length; i += 3, n++)
                { sum += source[i]; square += source[i] * source[i]; sums[n] = sum; squares[n] = square; }
                var scores = new double[4]; var same = true;
                for (var lag = 59; lag + 3 <= Math.Min(490, length / 3) + 1; lag++)
                {
                    batch(source, lag, sums, squares, scores);
                    for (var lane = 0; lane < 4; lane++)
                        same &= BitConverter.DoubleToInt64Bits(scores[lane]) ==
                            BitConverter.DoubleToInt64Bits(correlate(source, lag + lane));
                }
                check(same, $"four independent correlation lanes retain every scalar score bit: length={length}, seed={seed}");
            }
        }
        foreach (var length in new[] { 180, 181, 183, 1001, 1024, 2205 })
        foreach (var frequency in new[] { 0, 90, 110, 220, 311, 440, 700, 735 })
        {
            var source = Enumerable.Range(0, length)
                .Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * frequency * i / 44100))).ToArray();
            var expected = FullScan(source);
            var actual = UtauPitch.FindPeriod(source, default);
            check(BitConverter.DoubleToInt64Bits(actual) == BitConverter.DoubleToInt64Bits(expected),
                $"early period selection matches full scan for {length} samples at {frequency} Hz");
        }
        foreach (var length in new[] { 183, 1001, 2205 })
        foreach (var position in new[] { -1, 0, length / 2, length - 1 })
        {
            var values = Enumerable.Range(0, length).Select(i => i % 2 == 0 ? 0f : -0f).ToArray();
            if (position >= 0) values[position] = 0.1f;
            check(BitConverter.DoubleToInt64Bits(UtauPitch.FindPeriod(values, default)) ==
                BitConverter.DoubleToInt64Bits(FullScan(values)),
                "exact silence shortcut preserves signed zero and isolated nonzero boundary samples");
        }
        var random = new Random(4391);
        var noise = Enumerable.Range(0, 2205).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
        check(UtauPitch.FindPeriod(noise, default) == FullScan(noise),
            "early period selection retains full-scan rejection of unvoiced noise");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(Throws<OperationCanceledException>(() => UtauPitch.FindPeriod(new float[180], cancelled.Token)),
            "period selection honors cancellation before short-input or early-peak exits");

        double FullScan(float[] source)
        {
            const int minimum = 44100 / 735;
            var maximum = Math.Min(44100 / 90, source.Length / 3);
            if (maximum <= minimum) return 0;
            var scores = new double[maximum + 2];
            for (var lag = minimum - 1; lag <= maximum + 1; lag++) scores[lag] = correlate(source, lag);
            for (var lag = minimum; lag <= maximum; lag++)
            {
                var value = scores[lag];
                if (value < 0.88 || value < scores[lag - 1] || value < scores[lag + 1]) continue;
                var curvature = scores[lag - 1] - 2 * value + scores[lag + 1];
                var adjustment = Math.Abs(curvature) < 1e-10 ? 0 : 0.5 * (scores[lag - 1] - scores[lag + 1]) / curvature;
                return lag + Math.Clamp(adjustment, -0.5, 0.5);
            }
            return 0;
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckDoublePcm(check);
        CheckOutputPeak(check);
        CheckPhaseFft(check);
        CheckPulseWindow(check);
        CheckPeriodEarlyExit(check);
        var envelopeBuilder = typeof(WaveformPitch).GetMethod("BuildEnvelope",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Action<double[], double[], double[], int>>();
        var magnitude = Enumerable.Range(0, 1025).Select(i => i % 17 == 0 ? 0 : Math.Abs(Math.Sin(i * 0.19)) * 300).ToArray();
        var logs = new double[magnitude.Length]; var envelope = new double[magnitude.Length];
        foreach (var radius in new[] { 4, 12, 32 })
        {
            envelopeBuilder(magnitude, logs, envelope, radius);
            var same = true;
            for (var k = 0; k < magnitude.Length; k++)
            {
                var low = Math.Max(0, k - radius); var high = Math.Min(magnitude.Length - 1, k + radius);
                double total = 0;
                for (var j = low; j <= high; j++) total += Math.Log(Math.Max(1e-9, magnitude[j]));
                same &= BitConverter.DoubleToInt64Bits(envelope[k]) ==
                    BitConverter.DoubleToInt64Bits(Math.Exp(total / (high - low + 1)));
            }
            check(same, $"phase envelope log reuse preserves every bit at radius {radius}, edges and zero bins");
        }
        var sine = new PcmAudio(Enumerable.Range(0, 44100).Select(i => (short)Math.Round(7000 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray(), 44100);
        var original = (short[])sine.Samples.Clone();
        check(Math.Abs(WaveformPitch.Measure(sine, default) - 220) < 1, "waveform calibration detects a known synthetic source pitch");
        var random = new Random(291);
        var noise = new PcmAudio(Enumerable.Range(0, 22050).Select(_ => (short)random.Next(-5000, 5001)).ToArray(), 44100);
        foreach (var method in Enum.GetValues<WaveformPitchMethod>())
        {
            var result = WaveformPitch.Process(sine, 260, null, method, default);
            // Independent positive zero-crossing counter; does not reuse the
            // DSP autocorrelation observer to assess its own output.
            var pitch = Crossings(result.Samples, 13230, 30870);
            check(result.SampleRate == 44100 && result.Samples.Length == sine.Samples.Length && pitch is > 250 and < 270,
                $"{method} changes known sustained pitch toward 260 Hz without changing duration ({pitch:F1} Hz)");
            check(result.Samples.Max(x => Math.Abs((int)x)) <= 30147 && original.SequenceEqual(sine.Samples),
                $"{method} preserves source ownership and output headroom");
            check(WaveformPitch.Process(noise, 260, null, method, default).Samples.SequenceEqual(noise.Samples),
                $"{method} leaves uncertain unvoiced noise sample-for-sample unchanged");
            var silence = new PcmAudio(new short[4410], 44100);
            check(WaveformPitch.Process(silence, 260, null, method, default).Samples.All(x => x == 0), $"{method} never voices silence");
            var contour = Enumerable.Range(0, 201).Select(i => i < 100 ? 180.0 : 220.0).ToArray();
            var shaped = WaveformPitch.Process(sine, 220, contour, method, default);
            check(Crossings(shaped.Samples, 5000, 18000) + 15 < Crossings(shaped.Samples, 27000, 40000),
                $"{method} follows a varying contour instead of merely shifting to a constant pitch");
            var mixed = new PcmAudio([.. noise.Samples, .. sine.Samples], 44100);
            var transformed = WaveformPitch.Process(mixed, 260, null, method, default);
            check(transformed.Samples.AsSpan(0, noise.Samples.Length - 2205).SequenceEqual(noise.Samples.AsSpan(0, noise.Samples.Length - 2205)),
                $"{method} keeps a noisy consonant before a voiced segment intact");
            using var stop = new CancellationTokenSource(); stop.Cancel();
            check(Throws<OperationCanceledException>(() => WaveformPitch.Process(sine, 260, null, method, stop.Token)), $"{method} honors cancellation");
            check(Throws<InvalidDataException>(() => WaveformPitch.Process(sine, double.NaN, null, method, default)) &&
                Throws<InvalidDataException>(() => WaveformPitch.Process(sine, 260, [260], method, default)) &&
                Throws<InvalidDataException>(() => WaveformPitch.Process(new(new short[44100 * 45 + 1], 44100), 260, null, method, default)),
                $"{method} rejects invalid controls and oversized input before DSP");
        }
        foreach (var method in Enum.GetValues<WaveformPitchMethod>())
        {
            var longInput = new PcmAudio(Enumerable.Range(0, 45).SelectMany(_ => sine.Samples).ToArray(), 44100);
            using var stop = new CancellationTokenSource();
            var running = Task.Run(() => WaveformPitch.Process(longInput, 260, null, method, stop.Token));
            stop.CancelAfter(10);
            var canceled = false;
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { canceled = true; }
            check(canceled && WaveformPitch.Process(sine, 260, null, method, default).Samples.Length == sine.Samples.Length,
                $"{method} cancels ongoing bounded work and processes the next utterance");
        }
    }
    private static double Crossings(short[] samples, int start, int end)
    {
        var count = 0;
        for (var i = start + 1; i < end; i++) if (samples[i - 1] <= 0 && samples[i] > 0) count++;
        return count * 44100.0 / (end - start);
    }
    private static bool Throws<T>(Action action) where T : Exception
    { try { action(); return false; } catch (T) { return true; } }
}
