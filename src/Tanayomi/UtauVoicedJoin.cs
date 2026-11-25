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
