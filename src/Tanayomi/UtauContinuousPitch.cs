namespace Tanayomi;

/// <summary>Independent pitch-frame renderer for simple CV speech. The phrase
/// owns the output pulse grid; OTO scheduling and unvoiced samples remain dry.</summary>
internal sealed class UtauContinuousPitch
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private readonly double[] pulses;

    internal UtauContinuousPitch(int length, double basePeriod, bool question, CancellationToken ct = default, int[]? accentEdges = null, UtauVoiceTuning? tuning = null)
    {
        var grid = new List<double>();
        var baseHz = Rate / basePeriod;
        var pitch = tuning?.Pitch ?? 0;
        var range = (tuning?.Range ?? 100) / 100.0;
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
            return baseHz * Math.Pow(2, (pitch + semitones * range) / 12);
        }
        static double Ease(double t) => t * t * (3 - 2 * t);
    }

    // Analyze independent locally periodic islands; never ask an unvoiced
    // section to synthesize a pitch. All offsets are in output samples.
    internal void Apply(Span<float> samples, int absoluteStart, double hint, CancellationToken ct, bool preserveEnergy = false, bool rampAtVoicedStart = false, bool trackVoicedOnset = false, bool lookBackVoicedOnset = true)
    {
        if (hint <= 0 || !double.IsFinite(hint)) return;
        if (trackVoicedOnset)
        {
            ApplyConfidentVoicing(samples, absoluteStart, hint, ct, preserveEnergy, lookBackVoicedOnset);
            return;
        }
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

    // Re-estimate the raw period where the stable-vowel hint does not fit.
    // A note's raw period is used solely to find its source grains.
    private void ApplyConfidentVoicing(Span<float> samples, int absoluteStart, double hint, CancellationToken ct, bool preserveEnergy, bool lookBackVoicedOnset)
    {
        const int hop = Rate / 200;
        var width = Math.Max(Rate * 26 / 1000, (int)Math.Ceiling(3.2 * hint));
        if (samples.Length < width) return;
        var period = hint;
        var runs = new List<(int Start, int NormalStart, int End, double Period)>();
        var normalStart = 0;
        var runStart = -1;
        var runEnd = 0;
        for (var at = 0; ; at = Math.Min(at + hop, samples.Length - width))
        {
            ct.ThrowIfCancellationRequested();
            var frame = samples.Slice(at, width);
            var found = 0.0;
            if (UtauPitch.IsVoiced(frame, period)) found = period;
            else if (UtauPitch.IsVoiced(frame, hint)) found = hint;
            else if (MayBeVoiced(frame))
            {
                var measured = UtauPitch.FindPeriod(frame, ct);
                if (UtauPitch.IsVoiced(frame, measured)) found = measured;
            }
            if (runStart >= 0 && (found <= 0 || Math.Abs(Math.Log(found / period)) > 0.07))
            {
                // Close the old run before the next one starts; overlapping
                // analysis windows must never re-render already shifted samples.
                var end = Math.Min(runEnd, at + hop);
                if (end > runStart)
                    runs.Add((runStart, normalStart, end, period));
                runStart = -1;
            }
            if (found > 0)
            {
                if (runStart < 0)
                {
                    var usualStart = normalStart = at + hop;
                    runStart = lookBackVoicedOnset
                        ? BacktrackVoicedOnset(samples, usualStart, runs.Count > 0 ? runs[^1].End : 0, found)
                        : usualStart;
                    period = found;
                }
                runEnd = at + width - width / 4;
            }
            if (at == samples.Length - width) break;
        }
        if (runStart >= 0 && runEnd > runStart)
            runs.Add((runStart, normalStart, runEnd, period));
        foreach (var run in runs)
            ApplyIsland(samples[run.Start..run.End], absoluteStart + run.Start, run.Period, ct, preserveEnergy, false,
                confidentOnset: true, onsetLead: run.NormalStart - run.Start);
    }

    // The longer, confident detection window anchors the period. Check adjacent
    // earlier blocks against their next cycle before moving the blend into them.
    internal static int BacktrackVoicedOnset(ReadOnlySpan<float> samples, int start, int earliest, double period)
    {
        var lag = (int)Math.Round(period);
        if (lag < 60 || lag > 490) return start;
        var width = Math.Max(32, lag * 3 / 4);
        var step = Math.Max(16, lag / 3);
        var lowerBound = Math.Max(earliest, start - Rate * 14 / 1000);
        while (start > lowerBound)
        {
            var candidate = Math.Max(lowerBound, start - step);
            if (candidate + width + lag + lag / 4 > samples.Length ||
                !MatchesFollowingCycle(samples, candidate, width, lag)) break;
            start = candidate;
        }
        return start;
    }

    private static bool MatchesFollowingCycle(ReadOnlySpan<float> samples, int start, int width, int lag)
    {
        double sum = 0, power = 0, difference = 0;
        for (var i = 0; i < width; i++)
        {
            var x = samples[start + i];
            sum += x; power += x * x;
            if (i > 0) { var d = x - samples[start + i - 1]; difference += d * d; }
        }
        var centered = power - sum * sum / width;
        if (centered < width * 1e-8 || difference > centered * 0.10) return false;
        // A mostly voiced block may still begin with a short noisy consonant.
        // Protect its first sub-window instead of relying on whole-block energy.
        var head = Math.Min(width, Math.Max(16, lag / 3));
        double headPower = 0, headDifference = 0;
        for (var i = 0; i < head; i++)
        {
            var x = samples[start + i];
            headPower += (double)x * x;
            if (i > 0)
            {
                var delta = x - samples[start + i - 1];
                headDifference += (double)delta * delta;
            }
        }
        if (headPower < head * 1e-9 || headDifference > headPower * 0.11) return false;
        var slop = Math.Max(1, lag / 4);
        for (var later = lag - slop; later <= lag + slop; later += Math.Max(1, slop / 3))
        {
            double dot = 0, otherSum = 0, otherPower = 0;
            for (var i = 0; i < width; i++)
            {
                var x = samples[start + i];
                var y = samples[start + i + later];
                dot += x * y; otherSum += y; otherPower += y * y;
            }
            var otherCentered = otherPower - otherSum * otherSum / width;
            if (otherCentered < centered * 0.12 || centered < otherCentered * 0.12) continue;
            var cross = dot - sum * otherSum / width;
            if (cross > 0 && cross * cross > 0.80 * 0.80 * centered * otherCentered) return true;
        }
        return false;
    }

    private static bool MayBeVoiced(ReadOnlySpan<float> frame)
    {
        double energy = 0, difference = 0;
        for (var i = 1; i < frame.Length; i++)
        {
            energy += frame[i] * frame[i];
            var delta = frame[i] - frame[i - 1];
            difference += delta * delta;
        }
        return energy > 1e-6 && difference < energy * 0.08;
    }

    private void ApplyIsland(Span<float> raw, int start, double hint, CancellationToken ct, bool preserveEnergy, bool rampAtVoicedStart, bool confidentOnset = false, int onsetLead = 0)
    {
        var radius = (int)Math.Round(hint);
        if (radius < 2 || raw.Length - onsetLead < radius * (confidentOnset ? 4 : 5)) return;
        var source = new List<int>();
        var best = onsetLead + radius;
        for (var i = best + 1; i < onsetLead + 2 * radius; i++)
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
        if (source.Count < (confidentOnset ? 2 : 4) || source[^1] < raw.Length - radius * 4) return;

        // The extension borrows from the already located first cycle. Keep
        // source marks in the main region anchored to the original analysis.
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
