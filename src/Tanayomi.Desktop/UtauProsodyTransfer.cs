namespace Tanayomi.Desktop;

// Model timing is aligned to rendered OTO landmarks.
// Optional consonant timing moves the predicted lead into the consonant itself;
// it does not claim to reproduce acoustic coarticulation or vowel devoicing.
internal static class UtauProsodyTransfer
{
    internal static IReadOnlyList<UtauTimingAllocation> Allocate(IReadOnlyList<UtauPlannedUnit> units, bool consonantTiming = false,
        OpenJtalkPlan? phoneticPlan = null)
    {
        ArgumentNullException.ThrowIfNull(units);
        if (units.Count is < 1 or > 640) throw new InvalidDataException();
        if (phoneticPlan is not null)
        {
            // Reuse the full pronunciation/index contract before exposing any
            // original model labels to the renderer. Do not enumerate a caller's
            // unbounded collection or infer phonation from canonical aliases.
            var snapshot = new UtauPlannedUnit[units.Count];
            var reading = new UtauReadingUnit[units.Count];
            for (var i = 0; i < snapshot.Length; i++)
            {
                var unit = units[i] ?? throw new InvalidDataException("Null phonetic timing unit.");
                snapshot[i] = unit; reading[i] = new(unit.Alias, unit.Extensions, unit.Pause);
            }
            var mapped = UtauProsodyPlan.Map(phoneticPlan, reading);
            for (var i = 0; i < snapshot.Length; i++)
                if (snapshot[i] != mapped[i]) throw new InvalidDataException("Phonetic timing units differ from the supplied model plan.");
            units = snapshot;
        }
        var result = new UtauTimingAllocation[units.Count];
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i] ?? throw new InvalidDataException("Null timing unit.");
            if (unit.UnitIndex != i) throw new InvalidDataException();
            var start = unit.MainStartFrame ?? unit.StartFrame;
            var end = unit.Alias is not null && i + 1 < units.Count && units[i + 1].MainStartFrame is int next
                ? next : unit.EndFrame;
            var duration = Samples(end) - Samples(start);
            if (duration <= 0) throw new InvalidDataException();
            int? lead = consonantTiming && unit.MainStartFrame is int main && main > unit.StartFrame
                ? Samples(main) - Samples(unit.StartFrame) : null;
            if (lead is < 1 or > 44100 * 700 / 1000) throw new InvalidDataException();
            UtauPhoneticContext? phonetic = null;
            if (phoneticPlan is not null && unit.MainPhone is int mainPhone)
            {
                if (mainPhone < 1 || mainPhone >= phoneticPlan.Phones.Count - 1)
                    throw new InvalidDataException("Main phone has no bounded previous/next context.");
                var phone = phoneticPlan.Phones[mainPhone];
                var vowelDuration = Samples(phone.EndFrame) - Samples(phone.StartFrame);
                if (vowelDuration <= 0 || phone.StartFrame < unit.StartFrame || phone.EndFrame > unit.EndFrame ||
                    phone.EndFrame > phoneticPlan.Voiced.Count) throw new InvalidDataException("Invalid main-phone duration.");
                var noVoicedFrames = true;
                for (var frame = phone.StartFrame; frame < phone.EndFrame; frame++)
                    if (phoneticPlan.Voiced[frame] != 0) { noVoicedFrames = false; break; }
                phonetic = new(phoneticPlan.Phones[mainPhone - 1].Symbol, phone.Symbol,
                    phoneticPlan.Phones[mainPhone + 1].Symbol, vowelDuration, noVoicedFrames);
            }
            result[i] = new(unit.Alias, unit.Pause, unit.Extensions, duration) { LeadingSamples = lead, PhoneticContext = phonetic };
        }
        return Array.AsReadOnly(result);
    }

    internal static double[] TransferF0(OpenJtalkPlan plan, IReadOnlyList<UtauPlannedUnit> units,
        UtauReferenceTiming rendered, double[] sourceF0, double targetHz)
        => TransferF0(plan, units, rendered.Anchors, rendered.Audio.Samples.Length,
            rendered.Audio.SampleRate, sourceF0, targetHz);

    // Direct renderers need the timing map, not a dummy or discarded waveform.
    internal static double[] TransferF0(OpenJtalkPlan plan, IReadOnlyList<UtauPlannedUnit> units,
        IReadOnlyList<UtauTimingAnchor> anchors, int sampleCount, double targetHz)
    {
        if (sampleCount is < 4410 or > 44100 * 45) throw new InvalidDataException();
        var grid = new double[(int)(1000.0 * sampleCount / 44100 / 5.0) + 1];
        Array.Fill(grid, 200.0);
        return TransferF0(plan, units, anchors, sampleCount, 44100, grid, targetHz);
    }

    private static double[] TransferF0(OpenJtalkPlan plan, IReadOnlyList<UtauPlannedUnit> units,
        IReadOnlyList<UtauTimingAnchor> anchors, int sampleCount, int sampleRate, double[] sourceF0, double targetHz)
    {
        if (units.Count != anchors.Count || units.Count < 1 || sampleRate != 44100 ||
            sampleCount is < 4410 or > 44100 * 45 ||
            sourceF0.Length != (int)(1000.0 * sampleCount / 44100 / 5.0) + 1 ||
            !double.IsFinite(targetHz) || targetHz is < 40 or > 1200)
            throw new InvalidDataException();
        var knots = new List<(int Sample, double Frame)>();
        for (var i = 0; i < units.Count; i++)
        {
            var unit = units[i]; var anchor = anchors[i];
            if (unit.UnitIndex != i || anchor.UnitIndex != i || unit.Alias != anchor.Alias ||
                unit.Pause != anchor.Pause || unit.Extensions != anchor.Extensions) throw new InvalidDataException();
            if (unit.Alias is null)
            {
                Add(anchor.StartSample, unit.StartFrame); Add(anchor.EndSample, unit.EndFrame);
            }
            else
            {
                if (i == 0 || units[i - 1].Alias is null) Add(anchor.StartSample, unit.StartFrame);
                if (anchor.VowelOnsetSample is not int onset || unit.MainStartFrame is not int main)
                    throw new InvalidDataException();
                Add(onset, main);
                if (i + 1 == units.Count || units[i + 1].Alias is null) Add(anchor.EndSample, unit.EndFrame);
            }
        }
        if (knots.Count < 2 || knots[0].Sample != 0 || knots[^1].Sample != sampleCount)
            throw new InvalidDataException();
        var filled = InterpolateVoicedLogF0(plan);
        var values = new double[sourceF0.Length];
        var cursor = 0; double total = 0, sum = 0;
        for (var i = 0; i < values.Length; i++)
        {
            if (!double.IsFinite(sourceF0[i]) || sourceF0[i] != 0 && sourceF0[i] is not (>= 40 and <= 1200))
                throw new InvalidDataException();
            if (sourceF0[i] == 0) continue; // Retain UTAU/WORLD voicing, not the donor mask.
            var sample = Math.Min(i * 0.005 * 44100, sampleCount);
            while (cursor + 1 < knots.Count - 1 && sample > knots[cursor + 1].Sample) cursor++;
            var left = knots[cursor]; var right = knots[cursor + 1];
            var frame = left.Frame + (right.Frame - left.Frame) * (sample - left.Sample) / (right.Sample - left.Sample);
            frame = Math.Clamp(frame, 0, filled.Length - 1);
            var index = (int)frame; var next = Math.Min(index + 1, filled.Length - 1);
            values[i] = Math.Exp(filled[index] + (filled[next] - filled[index]) * (frame - index));
            var weight = Math.Min(0.005, Math.Max(0, sampleCount / 44100.0 - i * 0.005));
            sum += values[i] * weight; total += weight;
        }
        if (total < 0.05 || sum <= 0) throw new InvalidDataException();
        var scale = targetHz * total / sum;
        for (var i = 0; i < values.Length; i++)
        {
            if (values[i] == 0) continue;
            values[i] *= scale;
            if (!double.IsFinite(values[i]) || values[i] is < 40 or > 1200) throw new InvalidDataException();
        }
        return values;

        void Add(int sample, int frame)
        {
            if (sample < 0 || sample > sampleCount || frame < 0 || frame > plan.FrameCount)
                throw new InvalidDataException();
            if (knots.Count > 0)
            {
                var previous = knots[^1];
                if (sample == previous.Sample && frame == previous.Frame) return;
                // A vowel-only or nasal unit has no model consonant, while its
                // recorded OTO lead still occupies time: hold the same F0 there.
                if (sample <= previous.Sample || frame < previous.Frame) throw new InvalidDataException();
            }
            knots.Add((sample, frame));
        }
    }

    // Donor unvoiced stretches carry no pitch measurement. Interpolate the log
    // contour there solely for frames WORLD classified as voiced; never turn a
    // WORLD-unvoiced frame on. Endpoint gaps hold the nearest measured value.
    private static double[] InterpolateVoicedLogF0(OpenJtalkPlan plan)
    {
        var indices = Enumerable.Range(0, plan.FrameCount).Where(i => plan.Voiced[i] == 1).ToArray();
        if (indices.Length < 10) throw new InvalidDataException();
        var result = new double[plan.FrameCount]; var last = indices[0];
        Array.Fill(result, plan.Lf0[last], 0, last + 1);
        foreach (var next in indices.Skip(1))
        {
            for (var i = last + 1; i <= next; i++)
                result[i] = plan.Lf0[last] + (plan.Lf0[next] - plan.Lf0[last]) * (i - last) / (next - last);
            last = next;
        }
        Array.Fill(result, plan.Lf0[last], last, result.Length - last);
        return result;
    }

    private static int Samples(int frame)
    {
        if (frame is < 0 or > 9000) throw new InvalidDataException();
        return (int)Math.Round(frame * 220.5);
    }
}
