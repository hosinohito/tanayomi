using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tanayomi;

// Small, bounded time-domain DSP. No resampler executable, model or external
// implementation is used. Uncertain/unvoiced regions keep their recorded pitch.
internal static class UtauPitch
{
    private const int Rate = UtauSpeech.OutputSampleRate;

    internal static double FindPeriod(ReadOnlySpan<float> source, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        source = source[..Math.Min(source.Length, Rate / 20)];
        var minimum = Rate / 735;
        var maximum = Math.Min(Rate / 90, source.Length / 3);
        if (maximum <= minimum) return 0;
        // Exact silence has no qualifying correlation peak. Do not apply an
        // amplitude threshold: even a single nonzero sample uses normal analysis.
        if (source.IndexOfAnyExcept(0f) < 0) return 0;
        // Every lag samples the same left prefix with stride three. Preserve
        // its original accumulation order once, using bounded stack storage.
        Span<double> leftSums = stackalloc double[(source.Length + 2) / 3];
        Span<double> leftSquares = stackalloc double[leftSums.Length];
        double sum = 0, squares = 0;
        for (int i = 0, n = 0; i < source.Length; i += 3, n++)
        {
            var sample = source[i];
            sum += sample; squares += sample * sample;
            leftSums[n] = sum; leftSquares[n] = squares;
        }
        var previous = CorrelationWithLeftPrefix(source, minimum - 1, leftSums, leftSquares);
        var value = CorrelationWithLeftPrefix(source, minimum, leftSums, leftSquares);
        Span<double> batch = stackalloc double[4];
        var batchStart = -4;
        for (var lag = minimum; lag <= maximum; lag++)
        {
            if (((lag + 1) & 31) == 0) ct.ThrowIfCancellationRequested();
            double next;
            if ((uint)(lag + 1 - batchStart) < 4)
                next = batch[lag + 1 - batchStart];
            else if (Avx.IsSupported && lag >= minimum + 8 && lag + 4 <= maximum + 1)
            {
                // Leave the earliest peaks scalar; otherwise use independent
                // lanes without changing the per-lag accumulation order.
                CorrelationsFour(source, lag + 1, leftSums, leftSquares, batch);
                batchStart = lag + 1;
                next = batch[0];
            }
            else next = CorrelationWithLeftPrefix(source, lag + 1, leftSums, leftSquares);
            // Preserve the first qualifying local peak. At most three later
            // scores may already exist in the bounded batch; none is selected.
            if (!(value < 0.88 || value < previous || value < next))
            {
                var curvature = previous - 2 * value + next;
                var adjustment = Math.Abs(curvature) < 1e-10 ? 0 : 0.5 * (previous - next) / curvature;
                return lag + Math.Clamp(adjustment, -0.5, 0.5);
            }
            previous = value;
            value = next;
        }
        return 0;
    }

    private static double Correlation(ReadOnlySpan<float> source, int lag)
    {
        double dot = 0, left = 0, right = 0, sumLeft = 0, sumRight = 0;
        var count = 0;
        for (var i = 0; i + lag < source.Length; i += 3)
        {
            var a = source[i]; var b = source[i + lag];
            dot += a * b; left += a * a; right += b * b;
            sumLeft += a; sumRight += b; count++;
        }
        if (count == 0) return 0;
        dot -= sumLeft * sumRight / count;
        left -= sumLeft * sumLeft / count; right -= sumRight * sumRight / count;
        return dot / Math.Sqrt(Math.Max(1e-20, left * right));
    }

    private static double CorrelationWithLeftPrefix(ReadOnlySpan<float> source, int lag,
        ReadOnlySpan<double> leftSums, ReadOnlySpan<double> leftSquares)
    {
        double dot = 0, right = 0, sumRight = 0;
        var count = 0;
        for (var i = 0; i + lag < source.Length; i += 3)
        {
            var a = source[i]; var b = source[i + lag];
            dot += a * b; right += b * b;
            sumRight += b; count++;
        }
        var sumLeft = leftSums[count - 1];
        var left = leftSquares[count - 1];
        dot -= sumLeft * sumRight / count;
        left -= sumLeft * sumLeft / count; right -= sumRight * sumRight / count;
        return dot / Math.Sqrt(Math.Max(1e-20, left * right));
    }

    private static void CorrelationsFour(ReadOnlySpan<float> source, int lag,
        ReadOnlySpan<double> leftSums, ReadOnlySpan<double> leftSquares, Span<double> scores)
    {
        var dots = Vector256<double>.Zero;
        var rights = Vector256<double>.Zero;
        var sums = Vector256<double>.Zero;
        var count = 0;
        var i = 0;
        ref var first = ref MemoryMarshal.GetReference(source);
        for (; i + lag + 3 < source.Length; i += 3, count++)
        {
            var a = Vector128.Create(source[i]);
            var b = Vector128.LoadUnsafe(ref first, (nuint)(i + lag));
            // The scalar code rounds each product to float before adding to
            // a double accumulator. Widen after multiplication, not before.
            dots = Avx.Add(dots, Avx.ConvertToVector256Double(Sse.Multiply(a, b)));
            rights = Avx.Add(rights, Avx.ConvertToVector256Double(Sse.Multiply(b, b)));
            sums = Avx.Add(sums, Avx.ConvertToVector256Double(b));
        }
        for (var lane = 0; lane < 4; lane++)
        {
            var dot = dots.GetElement(lane);
            var right = rights.GetElement(lane);
            var sumRight = sums.GetElement(lane);
            var laneCount = count;
            if (i + lag + lane < source.Length)
            {
                var a = source[i]; var b = source[i + lag + lane];
                dot += a * b; right += b * b; sumRight += b; laneCount++;
            }
            var sumLeft = leftSums[laneCount - 1];
            var left = leftSquares[laneCount - 1];
            dot -= sumLeft * sumRight / laneCount;
            left -= sumLeft * sumLeft / laneCount;
            right -= sumRight * sumRight / laneCount;
            scores[lane] = dot / Math.Sqrt(Math.Max(1e-20, left * right));
        }
    }

    internal static int StableStart(ReadOnlySpan<float> source, int pre, int fixedEnd, double period, CancellationToken ct)
    {
        if (period <= 0) return fixedEnd;
        var lag = (int)Math.Round(period);
        var window = Math.Max(Rate * 30 / 1000, lag * 3);
        var run = 0;
        var runStart = pre;
        for (var at = pre; at + window <= source.Length && at < fixedEnd; at += Rate / 100)
        {
            ct.ThrowIfCancellationRequested();
            var frame = source.Slice(at, window);
            // Strong low-frequency periodicity must persist across several
            // windows. A fricative/high-frequency transition is never a vowel.
            if (IsVoiced(frame, lag))
            {
                if (run++ == 0) runStart = at;
            }
            else run = 0;
        }
        // Require a stable suffix, not merely a stable early window: a later
        // consonant transition inside the fixed range must still be retained.
        return run >= 5 ? Math.Min(fixedEnd, runStart) : fixedEnd;
    }

    internal static bool IsVoiced(ReadOnlySpan<float> source, double period)
    {
        if (period <= 0 || source.Length < period * 3) return false;
        double energy = 0, difference = 0;
        for (var i = 1; i < source.Length; i++)
        {
            energy += source[i] * source[i];
            var delta = source[i] - source[i - 1]; difference += delta * delta;
        }
        return energy > 1e-6 && difference < energy * 0.08 && Correlation(source, (int)Math.Round(period)) > 0.82;
    }

    internal static bool IsContinuouslyVoiced(ReadOnlySpan<float> source, double period, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(period) || period <= 0 || source.Length < period * 3) return false;
        var window = Math.Min(source.Length, Math.Max(Rate * 30 / 1000, (int)Math.Ceiling(period * 3)));
        var lastAt = source.Length - window;
        // Overlapping windows prevent a short interior closure from being hidden
        // by a high correlation averaged over the entire onset. Check the final
        // off-grid window too, so no suffix falls outside the classification.
        for (var at = 0; ; at = Math.Min(at + Rate / 100, lastAt))
        {
            ct.ThrowIfCancellationRequested();
            if (!IsVoiced(source.Slice(at, window), period)) return false;
            if (at == lastAt) return true;
        }
    }

    internal static void Shift(Span<float> samples, double period, double ratio, CancellationToken ct)
    {
        if (samples.Length < period * 5 || Math.Abs(ratio - 1) < 0.005) return;
        var radius = (int)Math.Round(period);
        var marks = new List<int>();
        var first = 0;
        for (var i = 1; i < Math.Min(samples.Length, radius); i++) if (samples[i] > samples[first]) first = i;
        marks.Add(first);
        while (marks[^1] + period * 1.3 < samples.Length)
        {
            var expected = marks[^1] + period;
            var from = Math.Max(marks[^1] + radius / 2, (int)(expected - period * 0.2));
            var to = Math.Min(samples.Length - 1, (int)(expected + period * 0.2));
            var best = from;
            for (var i = from + 1; i <= to; i++) if (samples[i] > samples[best]) best = i;
            marks.Add(best);
        }
        var output = new float[samples.Length];
        var weights = new float[samples.Length];
        // Radius is fixed for this shift. Reuse the exact same float window
        // rather than evaluating cosine again for every destination pulse.
        var pulseWindow = new float[checked(radius * 2 + 1)];
        for (var j = -radius; j <= radius; j++)
            pulseWindow[j + radius] = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / radius));
        var index = 0;
        for (double position = first; position < samples.Length + radius; position += period / ratio)
        {
            ct.ThrowIfCancellationRequested();
            while (index + 1 < marks.Count && Math.Abs(marks[index + 1] - position) < Math.Abs(marks[index] - position)) index++;
            var center = (int)Math.Round(position);
            var sourceCenter = marks[index];
            for (var j = -radius; j <= radius; j++)
            {
                var destination = center + j; var origin = sourceCenter + j;
                if ((uint)destination >= (uint)output.Length || (uint)origin >= (uint)samples.Length) continue;
                var weight = pulseWindow[j + radius];
                output[destination] += samples[origin] * weight; weights[destination] += weight;
            }
        }
        var fade = radius * 2;
        for (var i = 0; i < samples.Length; i++)
        {
            if (weights[i] < 0.01f) continue;
            var edge = Math.Min(i, samples.Length - 1 - i);
            var mix = edge >= fade ? 1f : (float)(0.5 - 0.5 * Math.Cos(Math.PI * edge / fade));
            samples[i] = samples[i] * (1 - mix) + output[i] / weights[i] * mix;
        }
    }
}
