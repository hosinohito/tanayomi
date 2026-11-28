namespace Tanayomi;

/// <summary>Join two already scheduled voiced units, without changing either
/// onset or borrowing audio across a rest. Both waveforms must support the join.</summary>
internal static class UtauVoicedJoin
{
    internal static bool TryBlend(Span<float> previous, ReadOnlySpan<float> incoming,
        int previousDuration, int otoOverlap, double previousPeriod, double incomingPeriod, CancellationToken ct)
    {
        var length = previous.Length;
        if (incoming.Length != length || !double.IsFinite(previousPeriod) || !double.IsFinite(incomingPeriod) ||
            previousPeriod < 1 || incomingPeriod < 1 || Math.Abs(Math.Log(previousPeriod / incomingPeriod)) > 0.12)
            return false;
        // Keep the actual OTO placement. Only the mixing window borrows time
        // from the previous vowel, with a half-unit ceiling for short notes.
        var window = Math.Min(length, Math.Min(previousDuration / 2, UtauSpeech.OutputSampleRate * 30 / 1000));
        if (window < 3 * Math.Max(previousPeriod, incomingPeriod)) return false;
        if (!UtauPitch.IsVoiced(previous[^window..], previousPeriod) ||
            !UtauPitch.IsContinuouslyVoiced(incoming, incomingPeriod, ct) ||
            !HasEvenEnergy(incoming, (int)Math.Ceiling(incomingPeriod))) return false;

        var fade = Math.Clamp(otoOverlap, (int)Math.Ceiling(2 * incomingPeriod), window);
        // Use one cycle to estimate a phase offset at the center of the fade.
        // Delaying only the outgoing tail leaves the incoming phoneme intact.
        var from = length - fade;
        var period = (int)Math.Round(previousPeriod);
        var compare = Math.Min(fade, period);
        var at = from + (fade - compare) / 2;
        var maxDelay = Math.Min(at, period - 1);
        var bestDelay = 0;
        double best = -2;
        for (var lag = 0; lag <= maxDelay; lag++)
        {
            if ((lag & 31) == 0) ct.ThrowIfCancellationRequested();
            double ab = 0, aa = 0, bb = 0;
            for (var j = at; j < at + compare; j += 2)
            {
                double a = previous[j - lag], b = incoming[j];
                ab += a * b; aa += a * a; bb += b * b;
            }
            var score = aa * bb > 1e-12 ? ab / Math.Sqrt(aa * bb) : -1;
            if (score > best + 0.002) { best = score; bestDelay = lag; }
        }
        if (best < 0.55) return false;

        for (var i = length - 1; i >= from; i--)
        {
            var t = (i - from) / (double)(fade - 1);
            var weight = (float)(t * t * (3 - 2 * t));
            // Ease the phase correction in at the outgoing edge. The reverse
            // loop reads only untouched (earlier) samples.
            var stretch = Math.Min(1, t * 4);
            var position = Math.Max(0, i - bestDelay * stretch * stretch * (3 - 2 * stretch));
            var lower = (int)position;
            var sample = previous[lower];
            if (position > lower)
                sample += (previous[lower + 1] - sample) * (float)(position - lower);
            previous[i] = (1 - weight) * sample + weight * incoming[i];
        }
        return true;
    }

    // Phase-align only the outgoing, already retuned lead. Keep the
    // complementary envelope, recorded incoming attack and schedule unchanged.
    internal static bool TryAlignRetunedTail(Span<float> previous, ReadOnlySpan<float> incoming, CancellationToken ct)
    {
        if (previous.Length != incoming.Length || previous.Length < UtauSpeech.OutputSampleRate * 11 / 1000)
            return false;
        var center = previous.Length / 2;
        var radius = Math.Min(center, UtauSpeech.OutputSampleRate * 10 / 1000);
        var a = previous.Slice(center - radius, radius * 2);
        var b = incoming.Slice(center - radius, radius * 2);
        var p = UtauPitch.FindPeriod(a, ct);
        var q = UtauPitch.FindPeriod(b, ct);
        if (p <= 0 || q <= 0 || Math.Abs(Math.Log(p / q)) > 0.07 ||
            !UtauPitch.IsVoiced(a, p) || !UtauPitch.IsVoiced(b, q) ||
            !HasEvenEnergy(a, (int)Math.Ceiling(p)) || !HasEvenEnergy(b, (int)Math.Ceiling(q)))
            return false;

        var period = (int)Math.Round(p);
        var halfWindow = Math.Min(period, center / 2);
        var at = center - halfWindow;
        var count = 2 * halfWindow;
        var maxShift = Math.Min(period / 2 + 1, at);
        if (count < period || maxShift < 1) return false;
        var original = Correlation(previous, incoming, at, count, 0);
        var best = original;
        var shift = 0;
        for (var lag = -maxShift; lag <= maxShift; lag++)
        {
            if ((lag & 31) == 0) ct.ThrowIfCancellationRequested();
            var corr = Correlation(previous, incoming, at, count, lag);
            if (corr > best + 0.002) { best = corr; shift = lag; }
        }
        if (best < 0.65 || best - original < 0.15 || shift == 0) return false;
        var start = Math.Max(maxShift, center - period * 2);
        var stop = Math.Min(previous.Length - 1 - maxShift, center + period * 2);
        if (start >= center || stop <= center) return false;
        ct.ThrowIfCancellationRequested();
        // In-place interpolation reads only untouched points in its direction.
        for (var i = shift > 0 ? start : stop; shift > 0 ? i <= stop : i >= start; i += Math.Sign(shift))
        {
            var x = i < center ? (i - start) / (double)(center - start) : (stop - i) / (double)(stop - center);
            var pos = i + shift * x * x * (3 - 2 * x);
            var j = (int)pos;
            var sample = previous[j];
            if (pos > j) sample += (previous[j + 1] - sample) * (float)(pos - j);
            previous[i] = sample;
        }
        return true;
    }

    private static double Correlation(ReadOnlySpan<float> a, ReadOnlySpan<float> b, int at, int count, int shift)
    {
        double aa = 0, bb = 0, ab = 0;
        for (var i = at; i < at + count; i += 2)
        {
            double x = a[i + shift], y = b[i];
            aa += x * x; bb += y * y; ab += x * y;
        }
        // A near-silent or very unbalanced onset is not a phase join candidate.
        if (aa * bb < 1e-11 || Math.Min(aa, bb) < Math.Max(aa, bb) * 0.035) return -1;
        return ab / Math.Sqrt(aa * bb);
    }

    private static bool HasEvenEnergy(ReadOnlySpan<float> audio, int cycle)
    {
        double low = double.PositiveInfinity, high = 0;
        var width = Math.Max(2 * cycle, UtauSpeech.OutputSampleRate / 200);
        var end = Math.Max(0, audio.Length - width);
        for (var start = 0; ; start = Math.Min(end, start + width / 2))
        {
            double energy = 0;
            foreach (var s in audio.Slice(start, Math.Min(width, audio.Length - start))) energy += (double)s * s;
            low = Math.Min(low, energy);
            high = Math.Max(high, energy);
            if (start == end) return low >= high * 0.08;
        }
    }
}
