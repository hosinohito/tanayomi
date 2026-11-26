namespace Tanayomi;

/// <summary>Attenuates short attacks above the local sustained level, rather
/// than normalizing every mora or changing its timing.</summary>
internal static class UtauAttackEnvelope
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    internal static void Attenuate(Span<float> mixed, int onset, bool pureVowel)
    {
        const int frame = Rate * 8 / 1000;
        const int hop = Rate * 2 / 1000;
        // A stable vowel, well beyond the attack, is the loudness reference.
        var referenceStart = onset + Rate * 75 / 1000;
        var referenceEnd = Math.Min(mixed.Length, onset + Rate * 145 / 1000);
        if (referenceEnd - referenceStart < Rate / 40) return;
        var steady = Energy(mixed[referenceStart..referenceEnd]);
        if (steady < 1e-6) return;

        // For CV attacks, don't suppress the recorded consonant proper. Only a
        // pure vowel can be shaped before its anchor.
        var start = pureVowel ? Math.Max(0, onset - Rate * 24 / 1000) : onset + Rate * 7 / 1000;
        var end = Math.Min(mixed.Length, onset + Rate * 72 / 1000);
        if (end - start < 2 * frame) return;
        Span<float> attenuation = stackalloc float[(end - start + hop - 1) / hop];
        for (var i = 0; i < attenuation.Length; i++)
        {
            var center = start + i * hop;
            var from = Math.Max(start, center - frame / 2);
            var to = Math.Min(end, center + frame / 2);
            var peak = Energy(mixed[from..to]);
            // 2 dB of headroom above the sustained vowel. Limit reduction to
            // 5.2 dB. RMS measurement does not track individual pitch cycles.
            var threshold = steady * 1.585;
            attenuation[i] = peak > threshold ? (float)(1 - Math.Max(0.55, Math.Sqrt(threshold / peak))) : 0;
        }
        var edge = Rate * 6 / 1000;
        // Enlarge the attenuation by one frame on each side and interpolate.
        // The envelope is unity at both edges and affects neither samples
        // outside the attack nor duration, F0 targets, or gain of the tails.
        Span<float> held = stackalloc float[attenuation.Length];
        for (var i = 0; i < held.Length; i++)
        {
            var best = 0f;
            for (var j = Math.Max(0, i - 2); j <= Math.Min(held.Length - 1, i + 2); j++)
                best = Math.Max(best, attenuation[j] * (1 - Math.Abs(i - j) * 0.20f));
            held[i] = best;
        }
        for (var i = start; i < end; i++)
        {
            var x = (i - start) / (float)hop;
            var a = (int)x;
            var b = Math.Min(held.Length - 1, a + 1);
            var reduction = held[a] + (held[b] - held[a]) * (x - a);
            var taper = Math.Min(1f, Math.Min(i - start, end - 1 - i) / (float)edge);
            mixed[i] *= 1 - taper * reduction;
        }
    }

    private static double Energy(ReadOnlySpan<float> samples)
    {
        double sum = 0;
        foreach (var x in samples) sum += (double)x * x;
        return sum / Math.Max(1, samples.Length);
    }
}
