namespace Tanayomi;

internal static class UtauHeadProtectedJoin
{
    internal static bool Apply(Span<float> destination, Span<float> incoming, int overlap, int priorVowel,
        double priorPeriod, double nextPeriod, int maxHeadCut, CancellationToken ct)
    {
        // The outgoing and incoming measurements are independent of the crossfade.
        // In a CV overlap, compare near the center of the existing hand-off, not
        // at the incoming vowel peak where the outgoing contribution is already gone.
        const int rate = UtauSpeech.OutputSampleRate;
        var probe = Math.Clamp((int)Math.Ceiling(Math.Max(priorPeriod, nextPeriod) * 3.3), rate * 18 / 1000, rate * 34 / 1000);
        var pivot = overlap == 0 ? 0 : overlap / 2;
        var near = Math.Min(pivot, probe / 2);
        var incomingAt = pivot - near;
        var outgoingEnd = destination.Length;
        var available = Math.Min(priorVowel, outgoingEnd);
        var tailLength = Math.Min(available, rate * 55 / 1000);
        var headEnd = Math.Min(incoming.Length, Math.Max(incomingAt + probe, overlap) + rate * 30 / 1000);
        if (headEnd - incomingAt < probe || tailLength < probe) return false;
        var left = destination.Slice(outgoingEnd - tailLength, tailLength);
        var right = incoming.Slice(incomingAt, probe);
        var a = Rms(left[^probe..]); var b = Rms(right);
        if (a < .009 || b <= a * 1.04 || b / a > 5 ||
            !Confident(left[^probe..], priorPeriod, ct) || !Confident(right, nextPeriod, ct)) return false;
        // Do not amplify a tail with a real silent or unvoiced interior. Use the
        // longest contiguous voiced suffix for the ramp.
        while (left.Length >= 2 * probe && !Confident(left[..probe], priorPeriod, ct)) left = left[(probe / 2)..];
        if (!UtauPitch.IsContinuouslyVoiced(left, UtauPitch.FindPeriod(left[^probe..], ct), ct)) return false;

        float peak = 0;
        foreach (var s in left) peak = Math.Max(peak, Math.Abs(s));
        var lift = Math.Max(1, Math.Min(3, .87 / Math.Max(.0001, peak)));
        var head = Math.Max(1 - maxHeadCut / 100.0, Math.Min(1, a * lift / b));
        var boost = Math.Min(lift, b * head / a);
        if (boost <= 1 && head >= 1) return false;

        // Reach the new level over the last part of the old vowel, carry it
        // across the existing hand-off, then release only the optional head gain.
        var hold = Math.Min(probe, left.Length / 2);
        for (int j = 0; j < left.Length; j++)
            left[j] *= (float)(1 + (boost - 1) * Ease(Math.Min(1, j / (double)Math.Max(1, left.Length - hold - 1))));
        if (head < 1)
        {
            // Attenuate only the leading continuous voicing, end before the first unsafe window.
            var end = incomingAt + probe;
            while (end + probe / 2 <= headEnd && Confident(incoming.Slice(end - probe / 2, probe), nextPeriod, ct))
                end += probe / 2;
            var begin = incomingAt;
            if (incomingAt > 0 && Confident(incoming[..(incomingAt + probe / 2)], nextPeriod, ct)) begin = 0;
            for (int j = begin; j < end; j++)
            {
                var fadeIn = begin == 0 && overlap == 0 ? 1 : Ease(Math.Min(1, (j - begin) / (double)Math.Max(1, probe / 4)));
                var fadeOut = Ease(Math.Clamp((end - 1 - j) / (double)Math.Max(1, probe / 2), 0, 1));
                incoming[j] *= (float)(1 - (1 - head) * fadeIn * fadeOut);
            }
        }
        return true;
    }

    private static bool Confident(ReadOnlySpan<float> signal, double hint, CancellationToken ct)
    {
        if (hint <= 0 || signal.Length < 3 * hint || Rms(signal) < .009) return false;
        var period = UtauPitch.FindPeriod(signal, ct);
        return period > 0 && UtauPitch.IsVoiced(signal, period);
    }

    private static double Rms(ReadOnlySpan<float> signal)
    {
        double energy = 0;
        foreach (double x in signal) energy += x * x;
        return Math.Sqrt(energy / signal.Length);
    }

    private static double Ease(double x) => x * x * (3 - 2 * x);
}
