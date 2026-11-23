namespace Tanayomi;

internal sealed record UtauUnitTimeSegment(int SourceStart, int SourceEnd, int OutputStart, int OutputEnd, bool Protected);
internal sealed record UtauUnitSourceRecoveryDiagnostic(int OriginalStartSample, int CandidateStartSample, int RecoveredSamples,
    int MaximumLookbackSamples, double ReferenceRms, double? MinimumPrefixRms, bool ContinuousCandidate, bool Adopted, string? Reason);
internal sealed record UtauUnitAcousticRuleDiagnostic(bool Available, string? UnavailableReason, int SourceLength,
    int RequestedOutputLength, int OutputLength, int ExtendedSamples, int PlacementLead, int? MappedVowelOnset, int? AttackEnd, int TransitionStart, int TransitionEnd,
    int? OutputTransitionStart, int? OutputTransitionEnd, int? TailOutputSamples, double? TailRms,
    double? FrictionMinimumRms, double? FrictionMinimumDifferenceRatio, IReadOnlyList<UtauUnitTimeSegment> Segments,
    bool AttackProtected, string BudgetStrategy, string Limitation);

internal sealed class UtauUnitRuleMap(IReadOnlyList<UtauUnitTimeSegment> segments, int mappedPre)
{
    internal IReadOnlyList<UtauUnitTimeSegment> Segments => segments;
    internal int MappedPre => mappedPre;
    internal double SourceAt(double output)
    {
        foreach (var segment in segments)
            if (output <= segment.OutputEnd)
                return segment.SourceStart + (output - segment.OutputStart) *
                    (segment.SourceEnd - segment.SourceStart) / (segment.OutputEnd - segment.OutputStart);
        return segments[^1].SourceEnd;
    }

    // Fully contained protected grains use the exact source/output offset.
    // Crossing grains retain the original search and must be observed rather
    // than described as completely protected.
    internal bool TryProtectedSource(int outputAt, int window, int sourceLength, out int sourceAt)
    {
        sourceAt = 0; var found = false;
        foreach (var segment in segments)
        {
            if (!segment.Protected || outputAt < segment.OutputStart || outputAt + window > segment.OutputEnd) continue;
            var candidate = segment.SourceStart + outputAt - segment.OutputStart;
            if (candidate < 0 || candidate + window > sourceLength || found && candidate != sourceAt)
                throw new InvalidDataException("Unit acoustic map protected-grain contract.");
            sourceAt = candidate; found = true;
        }
        return found;
    }
}

// Single-unit acoustic rules, independent of kana, neighboring phones and model
// vowel labels. Numeric gates are conservative engineering guards, not validated
// perceptual boundaries or a claim that one grain is a natural minimum vowel.
internal static class UtauUnitTimeMap
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private const int Window = Rate / 50;
    private const int MaximumSamples = Rate * 45;

    internal static (int Start, UtauUnitSourceRecoveryDiagnostic Diagnostic) FindContinuousPrefix(ReadOnlySpan<float> source,
        int originalStart, double period, CancellationToken ct)
    {
        var maximum = Math.Min(originalStart, Rate * 60 / 1000);
        UtauUnitSourceRecoveryDiagnostic Missing(string reason) => new(originalStart, originalStart, 0, maximum, 0, null, false, false, reason);
        ct.ThrowIfCancellationRequested();
        if (originalStart < 0 || originalStart + Window > source.Length || maximum <= 0 || !double.IsFinite(period) || period <= 0)
            return (originalStart, Missing("No bounded prefix with an adjacent reference window."));
        var reference = source.Slice(originalStart, Window);
        var referenceRms = Rms(reference);
        if (referenceRms < 0.0001 || Difference(reference) < 0.5 || UtauPitch.IsVoiced(reference, period))
            return (originalStart, Missing("Crop-adjacent source is not confirmed nonperiodic high-difference audio."));
        var selected = originalStart; var previousRms = referenceRms; var minimum = double.PositiveInfinity;
        // At most twelve overlapping windows. The final partial 5ms step may
        // stop at the file beginning; each complete 20ms window is still used.
        for (var index = 1; index <= 12; index++)
        {
            ct.ThrowIfCancellationRequested();
            var back = Math.Min(maximum, (int)Math.Round(index * Rate * 0.005));
            var at = originalStart - back; var frame = source.Slice(at, Window); var rms = Rms(frame);
            var added = source.Slice(at, selected - at); var addedRms = Rms(added);
            if (!double.IsFinite(rms) || rms < Math.Max(0.00001, referenceRms * 0.1) || rms < previousRms * 0.25 ||
                !double.IsFinite(addedRms) || addedRms < Math.Max(0.00001, referenceRms * 0.1) || addedRms < previousRms * 0.25 ||
                Difference(added) < 0.5 || Difference(frame) < 0.5 || UtauPitch.IsVoiced(frame, period)) break;
            selected = at; previousRms = rms; minimum = Math.Min(minimum, rms);
            if (back == maximum) break;
        }
        return (selected, new(originalStart, selected, originalStart - selected, maximum, referenceRms,
            selected == originalStart ? null : minimum, selected != originalStart, false,
            selected == originalStart ? "No continuous nonperiodic prefix met fixed energy/difference guards." :
                "Adjacent acoustic continuity is a bounded heuristic, not proof of phoneme or speaker identity; expanded-source rule budget must still succeed."));
    }

    internal static (UtauUnitRuleMap? Map, UtauUnitAcousticRuleDiagnostic Diagnostic) Create(ReadOnlySpan<float> source,
        int pre, int placementLead, int outputLength, int transitionStart, int transitionEnd, double period, CancellationToken ct)
    {
        const string limitation = "All segment coordinates are trimmed-source/unit relative. Strong friction uses sustained high difference energy and relative RMS, not phoneme identity. Attack is the first sustained high-RMS plateau, not a verified perceptual rise endpoint. Only fully contained protected grains have fixed offsets; crossing-grain contributions are not guaranteed preserved. Classification remains heuristic. The tail floor is a computational grain/period bound, not a natural minimum vowel. Placement lead is separate from mapped OTO pre.";
        int? attack = null, outputStart = null, outputEnd = null, tailOutput = null, mappedPre = null;
        var attackProtected = false; var strategy = "unavailable";
        var requestedOutputLength = outputLength;
        double? tailRms = null, minimumRms = null, minimumDifference = null;
        var sourceLength = source.Length;
        UtauUnitAcousticRuleDiagnostic Describe(bool available, string? reason, IReadOnlyList<UtauUnitTimeSegment>? parts = null)
            => new(available, reason, sourceLength, requestedOutputLength, outputLength, Math.Max(0, outputLength - requestedOutputLength), placementLead, mappedPre, attack, transitionStart, transitionEnd,
                outputStart, outputEnd, tailOutput, tailRms, minimumRms, minimumDifference,
                parts ?? Array.Empty<UtauUnitTimeSegment>(), attackProtected, strategy, limitation);
        ct.ThrowIfCancellationRequested();
        if (sourceLength is < Window or > MaximumSamples || outputLength is < Window or > MaximumSamples ||
            pre <= 0 || pre >= sourceLength || placementLead < 1 || placementLead >= outputLength ||
            transitionStart <= Window || transitionStart >= pre || transitionEnd <= pre || transitionEnd >= sourceLength ||
            transitionEnd - transitionStart != 2 * Window || !double.IsFinite(period) || period <= 0 || period * 3 > Window)
            return (null, Describe(false, "Source, transition, period or output layout does not meet the bounded rule contract."));
        if (outputLength >= sourceLength)
            return (null, Describe(false, "Identity and lengthening do not need this compression rule."));
        foreach (var sample in source)
            if (!float.IsFinite(sample)) return (null, Describe(false, "Source contains a nonfinite sample."));
        var tail = source[transitionEnd..];
        if (tail.Length < Window)
            return (null, Describe(false, "No complete voiced-tail analysis window."));
        for (var at = 0; ; at = Math.Min(at + Rate / 100, tail.Length - Window))
        {
            ct.ThrowIfCancellationRequested();
            if (!UtauPitch.IsVoiced(tail.Slice(at, Window), period))
                return (null, Describe(false, "The complete tail is not continuously voiced at the source reference period."));
            if (at == tail.Length - Window) break;
        }
        tailRms = Rms(tail);
        var levels = new List<(int At, double Rms, double Difference)>();
        for (var index = 0; ; index++)
        {
            ct.ThrowIfCancellationRequested();
            var at = (int)Math.Round(index * Rate * 0.005);
            if (at + Window > transitionStart) break;
            var frame = source.Slice(at, Window);
            levels.Add((at, Rms(frame), Difference(frame)));
        }
        if (levels.Count < 5 || tailRms <= 0.0001)
            return (null, Describe(false, "Insufficient friction analysis or voiced-tail energy."));
        var peakRms = levels.Max(value => value.Rms);
        foreach (var frame in levels)
        {
            ct.ThrowIfCancellationRequested();
            if (frame.Rms < Math.Max(0.00001, peakRms * 0.03) || frame.Difference < 0.5 ||
                UtauPitch.IsVoiced(source.Slice(frame.At, Window), period))
                return (null, Describe(false, "Leading interval has a closure, discontinuous weak energy, periodic sound or insufficient high-difference continuity."));
        }
        var run = 0;
        foreach (var frame in levels)
        {
            if (frame.Rms >= peakRms * 0.9)
            {
                if (++run == 3) { attack = frame.At + Window; break; }
            }
            else run = 0;
        }
        if (attack is null || attack < Window || transitionStart - attack < Window)
            return (null, Describe(false, "No bounded sustained RMS plateau followed by a full friction-middle window."));
        minimumRms = double.PositiveInfinity; minimumDifference = double.PositiveInfinity;
        for (var at = attack.Value; ; at = Math.Min(at + Rate / 100, transitionStart - Window))
        {
            ct.ThrowIfCancellationRequested();
            var frame = source.Slice(at, Window);
            var rms = Rms(frame); var difference = Difference(frame);
            minimumRms = Math.Min(minimumRms.Value, rms); minimumDifference = Math.Min(minimumDifference.Value, difference);
            // Requiring persistent energy excludes closures and isolated bursts;
            // high difference energy plus a failed voiced test excludes vowels.
            if (rms < Math.Max(0.0001, tailRms.Value * 0.1) || difference < 0.5 || UtauPitch.IsVoiced(frame, period))
                return (null, Describe(false, "Friction middle is weak, periodic, closure-like or not persistently high-difference."));
            if (at == transitionStart - Window) break;
        }
        var tailFloor = Math.Max(Window, (int)Math.Ceiling(period * 3));
        var transitionLength = transitionEnd - transitionStart;
        var sourceMiddle = transitionStart - attack.Value;
        // A predicted duration is a target, not permission to crush the recorded
        // rise/transition. Extend only to the minimum processing budget; never
        // beyond the selected original source or over an existing long note.
        var requiredLength = checked(attack.Value + Window + transitionLength + tailFloor);
        outputLength = Math.Max(requestedOutputLength, requiredLength);
        if (outputLength > sourceLength || outputLength > MaximumSamples)
            return (null, Describe(false, "Protected rise, middle and tail require more than the bounded original source."));
        var availableMiddle = outputLength - attack.Value - transitionLength - tailFloor;
        var outputMiddle = Math.Min(sourceMiddle, availableMiddle);
        tailOutput = outputLength - attack.Value - outputMiddle - transitionLength;
        outputStart = attack.Value + outputMiddle; outputEnd = outputStart + transitionLength;
        mappedPre = outputStart + (pre - transitionStart);
        var priorTransitionStart = placementLead - (pre - transitionStart);
        var priorTail = requestedOutputLength - (placementLead + transitionEnd - pre);
        IReadOnlyList<UtauUnitTimeSegment> segments;
        var fourFits = outputMiddle >= Window && tailOutput >= tailFloor && tailOutput < tail.Length &&
            (outputLength > requestedOutputLength || tailOutput < priorTail) &&
            outputStart > priorTransitionStart && mappedPre >= placementLead && mappedPre < outputLength;
        if (fourFits)
        {
            attackProtected = true; strategy = "four-segment attack and transition protection";
            segments = Array.AsReadOnly(new[]
            {
                new UtauUnitTimeSegment(0, attack.Value, 0, attack.Value, true),
                new UtauUnitTimeSegment(attack.Value, transitionStart, attack.Value, outputStart.Value, false),
                new UtauUnitTimeSegment(transitionStart, transitionEnd, outputStart.Value, outputEnd.Value, true),
                new UtauUnitTimeSegment(transitionEnd, sourceLength, outputEnd.Value, outputLength, false)
            });
        }
        else return (null, Describe(false, "Protected-rise map cannot provide a genuine bounded tail-to-friction transfer."));
        return (new UtauUnitRuleMap(segments, mappedPre.Value), Describe(true, null, segments));
    }

    private static double Rms(ReadOnlySpan<float> samples)
    { double sum = 0; foreach (var sample in samples) sum += (double)sample * sample; return Math.Sqrt(sum / samples.Length); }
    private static double Difference(ReadOnlySpan<float> samples)
    {
        double energy = 0, difference = 0;
        for (var i = 1; i < samples.Length; i++)
        { energy += (double)samples[i] * samples[i]; var delta = samples[i] - samples[i - 1]; difference += (double)delta * delta; }
        return energy > 1e-12 ? difference / energy : 0;
    }
}
