namespace Tanayomi;

/// <summary>Retimes the overlap envelope, not the start or length of either sound.</summary>
internal static class UtauLeadHandoff
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private static int Ms(int value) => Rate * value / 1000;

    internal static int Delay(ReadOnlySpan<float> oldTail, ReadOnlySpan<float> incoming, int fade)
    {
        // Preserve at least the final 20 ms of the handoff budget for the
        // incoming lead (stop closure, friction or transition), whatever the alias.
        var budget = Math.Min(Ms(25), incoming.Length - fade - Ms(20));
        var frame = Ms(5);
        if (budget < frame || oldTail.Length != incoming.Length || oldTail.Length < frame * 3)
            return 0;
        var reference = Energy(oldTail[..(frame * 3)]);
        if (reference < 1e-5) return 0;
        // No change when the incoming note already starts audibly. Look only
        // for a contiguous weak prefix, not arbitrary troughs inside a consonant.
        var quiet = 0;
        var firstActive = Math.Min(incoming.Length - frame, budget + fade);
        for (var at = 0; at <= firstActive; at += frame)
        {
            if (Energy(incoming.Slice(at, frame)) >= reference * 0.09) break;
            quiet += frame;
        }
        return quiet >= 2 * frame ? Math.Clamp(quiet - fade / 2, 0, budget) : 0;
    }

    private static double Energy(ReadOnlySpan<float> span)
    {
        double sum = 0;
        foreach (var sample in span) sum += (double)sample * sample;
        return sum / span.Length;
    }
}
