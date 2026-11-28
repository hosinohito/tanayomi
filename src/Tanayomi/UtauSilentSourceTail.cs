namespace Tanayomi;

internal static class UtauSilentSourceTail
{
    internal static int EffectiveLength(ReadOnlySpan<float> source, int stable, double period, CancellationToken ct)
    {
        const int rate = UtauSpeech.OutputSampleRate;
        if (period <= 0) return source.Length;
        var block = rate * 5 / 1000;
        var minimum = Math.Max(rate * 28 / 1000, (int)Math.Ceiling(period * 4));
        if (source.Length - stable < minimum + 3 * block) return source.Length;
        // Trailing silence is both low-RMS and low-peak. Do not treat a quiet
        // release, breath or later click as silence when it crosses either test.
        double peak = 0;
        foreach (var s in source[stable..]) peak = Math.Max(peak, Math.Abs(s));
        var floor = Math.Clamp(peak * .018, .0009, .0025);
        int cursor = source.Length;
        while (cursor - block >= stable + minimum)
        {
            ct.ThrowIfCancellationRequested();
            double e = 0, p = 0;
            foreach (double x in source[(cursor - block)..cursor]) { e += x * x; p = Math.Max(p, Math.Abs(x)); }
            if (e / block > floor * floor || p > floor * 3.5) break;
            cursor -= block;
        }
        if (source.Length - cursor < 3 * block) return source.Length;
        var prior = source[(cursor - minimum)..cursor];
        var measured = UtauPitch.FindPeriod(prior, ct);
        if (measured <= 0 || !UtauPitch.IsContinuouslyVoiced(prior, measured, ct)) return source.Length;
        // Keep the original vocal onset. The existing grain synthesis stretches
        // the surviving tail to the original target length.
        return cursor;
    }
}
