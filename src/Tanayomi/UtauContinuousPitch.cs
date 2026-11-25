namespace Tanayomi;

/// <summary>Independent pitch-frame renderer for simple CV speech. The phrase
/// owns the output pulse grid; OTO scheduling and unvoiced samples remain dry.</summary>
internal sealed class UtauContinuousPitch
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private readonly double[] pulses;

    internal UtauContinuousPitch(int length, double basePeriod, bool question, CancellationToken ct = default, int[]? accentEdges = null)
    {
        var grid = new List<double>();
        var baseHz = Rate / basePeriod;
        if (length < 1 || basePeriod is < 60 or > 490 || !double.IsFinite(basePeriod))
            throw new ArgumentOutOfRangeException(nameof(basePeriod));
        for (double sample = 0; sample < length; sample += Rate / HertzAt(sample))
        {
            if ((grid.Count & 255) == 0) ct.ThrowIfCancellationRequested();
            grid.Add(sample);
        }
        pulses = grid.ToArray();

        double HertzAt(double at)
        {
            var t = Math.Clamp(at / Math.Max(1, length - 1), 0, 1);
            var semitones = 0.45 - 1.25 * Ease(t);
            if (question) semitones += 2.0 * Ease(Math.Clamp((t - 0.65) / 0.35, 0, 1));
            if (accentEdges is { Length: > 0 })
            {
                var middle = Array.BinarySearch(accentEdges, (int)at);
                var next = middle < 0 ? ~middle : middle;
                // Locate the closest rise or drop. A short continuous
                // ramp keeps the shared phase grid free of pitch steps.
                var best = next;
                if (next > 0 && (next == accentEdges.Length ||
                    at - accentEdges[next - 1] < accentEdges[next] - at)) best = next - 1;
                var value = (best & 1) == 0
                    ? Ease(Math.Clamp((at - accentEdges[best]) / (Rate * 0.065) + 0.5, 0, 1))
                    : 1 - Ease(Math.Clamp((at - accentEdges[best]) / (Rate * 0.065) + 0.5, 0, 1));
                semitones += 1.8 * value;
            }
            return baseHz * Math.Pow(2, semitones / 12);
        }
        static double Ease(double t) => t * t * (3 - 2 * t);
    }

    // Analyze independent locally periodic islands; never ask an unvoiced
    // section to synthesize a pitch. All offsets are in output samples.
    internal void Apply(Span<float> samples, int absoluteStart, double hint, CancellationToken ct, bool preserveEnergy = false, bool rampAtVoicedStart = false)
    {
        if (hint <= 0 || !double.IsFinite(hint)) return;
        var hop = Rate / 200;
        var window = Math.Max(5 * hop, (int)Math.Ceiling(4 * hint));
        if (samples.Length < window) return;
        var runStart = -1;
        var runEnd = 0;
        for (var at = 0; ; at = Math.Min(at + hop, samples.Length - window))
        {
            ct.ThrowIfCancellationRequested();
            if (UtauPitch.IsVoiced(samples.Slice(at, window), hint))
            {
                if (runStart < 0) runStart = at + window / 4;
                runEnd = at + window - window / 4;
            }
            else if (runStart >= 0)
            {
                ApplyIsland(samples[runStart..runEnd], absoluteStart + runStart, hint, ct, preserveEnergy, rampAtVoicedStart);
                runStart = -1;
            }
            if (at == samples.Length - window) break;
        }
        if (runStart >= 0) ApplyIsland(samples[runStart..runEnd], absoluteStart + runStart, hint, ct, preserveEnergy, rampAtVoicedStart);
    }

    private void ApplyIsland(Span<float> raw, int start, double hint, CancellationToken ct, bool preserveEnergy, bool rampAtVoicedStart)
    {
        var radius = (int)Math.Round(hint);
        if (radius < 2 || raw.Length < radius * 5) return;
        var source = new List<int>();
        var best = radius;
        for (var i = radius + 1; i < 2 * radius; i++)
            if (raw[i] > raw[best]) best = i;
        source.Add(best);
        var search = Math.Max(1, radius / 5);
        while (source[^1] + 2 * radius + search < raw.Length)
        {
            ct.ThrowIfCancellationRequested();
            var prior = source[^1];
            var expected = prior + radius;
            var score = double.NegativeInfinity;
            var winner = expected;
            for (var i = expected - search; i <= expected + search; i++)
            {
                double dot = 0, left = 0, right = 0;
                for (var j = -radius / 2; j <= radius / 2; j += 4)
                {
                    double a = raw[prior + j], b = raw[i + j];
                    dot += a * b; left += a * a; right += b * b;
                }
                var quality = dot / Math.Sqrt(Math.Max(1e-20, left * right)) - Math.Abs(i - expected) * 0.01 / radius;
                if (quality > score) { score = quality; winner = i; }
            }
            if (score < 0.70) break;
            source.Add(winner);
        }
        if (source.Count < 4 || source[^1] < raw.Length - radius * 4) return;

        var first = Array.BinarySearch(pulses, (double)start);
        if (first < 0) first = ~first;
        var output = new float[raw.Length];
        var weights = new float[raw.Length];
        var envelope = new float[2 * radius + 1];
        for (var j = -radius; j <= radius; j++)
            envelope[j + radius] = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / radius));
        List<double>? leadPulses = null;
        var targetInterval = first + 1 < pulses.Length ? pulses[first + 1] - pulses[first] : hint;
        // Keep the shared phrase phase after the transition. In the early voiced
        // region, begin at the measured source period rather than snapping from
        // dry audio to the target. Short/near-matched islands keep the old grid.
        if (rampAtVoicedStart && Math.Abs(Math.Log(targetInterval / hint)) >= Math.Log(1.12))
        {
            var duration = Math.Min(Rate * 75 / 1000, raw.Length * 2 / 3);
            var cutoff = Array.BinarySearch(pulses, (double)(start + duration));
            if (cutoff < 0) cutoff = ~cutoff;
            if (duration >= Rate * 35 / 1000 && cutoff > first && cutoff < pulses.Length &&
                pulses[cutoff] < start + raw.Length)
            {
                leadPulses = new List<double>();
                var toward = pulses[cutoff] - pulses[cutoff - 1];
                for (var at = pulses[cutoff]; ;)
                {
                    var fraction = Math.Clamp((at - start) / duration, 0, 1);
                    var eased = fraction * fraction * (3 - 2 * fraction);
                    at -= hint * Math.Pow(toward / hint, eased);
                    if (at < start) break;
                    leadPulses.Add(at);
                }
                leadPulses.Reverse();
                first = cutoff;
            }
        }
        var cursor = 0;
        var leadCount = leadPulses?.Count ?? 0;
        for (var i = first - leadCount; i < pulses.Length && (i < first || pulses[i] < start + raw.Length); i++)
        {
            ct.ThrowIfCancellationRequested();
            var at = (int)Math.Round((i < first ? leadPulses![i - first + leadCount] : pulses[i]) - start);
            while (cursor + 1 < source.Count && Math.Abs(source[cursor + 1] - at) < Math.Abs(source[cursor] - at)) cursor++;
            var center = source[cursor];
            for (var j = Math.Max(-radius, -at); j <= Math.Min(radius, raw.Length - 1 - at); j++)
            {
                var w = envelope[radius + j];
                output[at + j] += raw[center + j] * w;
                weights[at + j] += w;
            }
        }
        if (preserveEnergy)
        {
            // Summing displaced pitch grains can partially cancel a vowel.
            // Match the local energy of the same dry voiced run, not that of
            // another consonant or a whole phrase. Preserve both edge samples.
            const int hop = Rate / 100;
            var referenceRadius = Math.Max(Rate * 12 / 1000, 2 * radius);
            for (var i = 0; i < raw.Length; i++)
                output[i] = weights[i] < 0.01f ? raw[i] : output[i] / weights[i];
            var gains = new float[(raw.Length - 1) / hop + 2];
            for (var k = 0; k < gains.Length; k++)
            {
                var center = Math.Min(raw.Length - 1, k * hop);
                var a = Math.Max(0, center - referenceRadius);
                var b = Math.Min(raw.Length, center + referenceRadius);
                double dry = 0, wet = 0;
                for (var j = a; j < b; j += 2)
                {
                    dry += (double)raw[j] * raw[j];
                    wet += (double)output[j] * output[j];
                }
                gains[k] = dry > (b - a) * 1e-7 && wet > dry * 0.12
                    ? (float)Math.Clamp(Math.Sqrt(dry / wet), 0.71, 2.0) : 1;
            }
            for (var i = 0; i < raw.Length; i++)
            {
                if (weights[i] < 0.01f) continue;
                var k = i / hop;
                var t = (i % hop) / (float)hop;
                var gain = gains[k] + (gains[k + 1] - gains[k]) * t;
                var mix = Math.Min(1f, Math.Min(i, raw.Length - 1 - i) / (float)radius);
                raw[i] += mix * (output[i] * gain - raw[i]);
            }
            return;
        }
        for (var i = 0; i < raw.Length; i++)
        {
            if (weights[i] < 0.01f) continue;
            var mix = Math.Min(1f, Math.Min(i, raw.Length - 1 - i) / (float)radius);
            raw[i] += mix * (output[i] / weights[i] - raw[i]);
        }
    }
}
