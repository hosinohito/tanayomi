namespace Tanayomi;

/// <summary>Trial rhythm only: transfer time between stable vowels within
/// the same punctuation-delimited phrase. No new pauses or accent targets.</summary>
internal static class UtauSimpleRhythm
{
    private static int Ms(int milliseconds) => UtauSpeech.OutputSampleRate * milliseconds / 1000;
    internal static int[] Plan(UtauReadingUnit[] reading, Func<UtauReadingUnit, bool> hasStableTail, CancellationToken ct)
    {
        var offsets = new int[reading.Length];
        // This fixed bound also limits acoustic inspections in long chat lines.
        Span<int> donors = stackalloc int[5];
        var start = 0;
        for (var end = 0; end <= reading.Length; end++)
        {
            ct.ThrowIfCancellationRequested();
            if (end < reading.Length && reading[end].Pause is not ('、' or '。' or '?')) continue;
            var last = end - 1;
            if (last >= start + 2 && Eligible(reading[last]) && hasStableTail(reading[last]))
            {
                var count = 0;
                for (var j = last - 1; j >= start && j >= last - 6 && count < donors.Length; j--)
                {
                    // Do not move time across a closure or extend an explicit long vowel.
                    if (reading[j].Pause != '\0') break;
                    if (Eligible(reading[j]) && hasStableTail(reading[j])) donors[count++] = j;
                }
                if (count >= 2)
                {
                    var transfer = Math.Min(count * Ms(18), Ms(end < reading.Length && reading[end].Pause == '、' ? 24 : 36));
                    for (var n = 0; n < count; n++) offsets[donors[n]] = -(transfer / count + (n < transfer % count ? 1 : 0));
                    offsets[last] = transfer;
                }
            }
            start = end + 1;
        }
        return offsets;
    }

    private static bool Eligible(UtauReadingUnit unit) =>
        unit is { Alias: not null and not "ん", Extensions: 0, Pause: '\0' };
}
