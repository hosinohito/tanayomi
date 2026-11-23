namespace Tanayomi;

internal sealed record UtauContextFrictionDiagnostic(bool Eligible, bool Applied, string Reason)
{
    public string? PreviousPhone { get; init; }
    public string? VowelPhone { get; init; }
    public string? NextPhone { get; init; }
    public bool? ModelVoicedFramesAbsent { get; init; }
    public int? SourceStartSample { get; init; }
    public int? SourceEndSample { get; init; }
    public int? AttackSamples { get; init; }
    public int? UnitSamples { get; init; }
    public int? AudibleSamples { get; init; }
    public int FadeSamples { get; init; }
    public double? ClosureRms { get; init; }
    public double? OriginalClosureRms { get; init; }
    public double? OriginalLaterLeadMaximumRms { get; init; }
    public double? LaterLeadMaximumRms { get; init; }
    public bool BasedOnAdoptedUnitRule { get; init; }
    public bool NextPcmUnchanged { get; init; }
    public int RecoveredNextPrefixSamples { get; init; }
    public int? NextOriginalStartSample { get; init; }
    public int? NextRecoveredStartSample { get; init; }
    public bool NextAbsoluteLandmarksUnchanged { get; init; }
    public string? NextPrefixRecoveryReason { get; init; }
    public string? NextPcmSha256Before { get; init; }
    public string? NextPcmSha256After { get; init; }
    public IReadOnlyList<UtauDiagnosticGrain> Grains { get; init; } = Array.Empty<UtauDiagnosticGrain>();
    public string Limitation => "Conditional frontend-U intent approximation for s/U/t-or-k from recorded aperiodic friction, not reproduction of the model's possibly mixed VUV or vowel articulation. No new noise, source-loop extension or F0-to-zero conversion. Low-energy lead windows do not prove a stop closure. An optional paired next-stop redraw recovers a quiet pre-crop prefix; this is not proof of articulation. Existing placement/mapped anchors remain comparison controls.";
}

public sealed partial class UtauSpeech
{
    private sealed record ContextNextSource(Voicebank Bank, OtoEntry Oto, int Extensions, InspectedMoraSource Inspection, int UsedStart, int AbsolutePre, int AbsoluteStable, int UsedEnd);
    private sealed record ContextFrictionSource(float[] Samples, int Start, int Length, int AttackEnd);

    private static void ValidatePhoneticContext(UtauTimingAllocation allocation)
    {
        if (allocation.PhoneticContext is not { } context) return;
        static bool Symbol(string? value) => value is { Length: >= 1 and <= 31 } && value.All(c => c is >= '!' and <= '~');
        if (allocation.Alias is null || !Symbol(context.PreviousPhone) || !Symbol(context.VowelPhone) || !Symbol(context.NextPhone) ||
            context.VowelDurationSamples is < 1 or > MaximumSamples)
            throw new InvalidDataException("Phonetic context is invalid for this timing allocation.");
    }

    private bool CanCompareContextFriction(int index, UtauReadingUnit[] reading, out string reason)
    {
        reason = "Original baseline retained.";
        if (!acousticContextRendering || comparisonTimingPlan is not { } plan)
        { reason = "Current Context planned rendering is required."; return false; }
        var context = plan[index].PhoneticContext;
        // Frontend U marks a linguistic devoicing target; HTS MSD may retain
        // voiced states. Keep that independent observation, without tuning a
        // voiced-frame threshold or claiming to reconstruct its VUV pattern.
        if (context is not { PreviousPhone: "s", VowelPhone: "U", NextPhone: "t" or "k" })
        { reason = "Frontend context is not s/U/t-or-k."; return false; }
        if (index + 1 >= reading.Length || reading[index + 1].Alias is null)
        { reason = "A non-pause following unit within the phrase is required."; return false; }
        if (reading[index].Extensions != 0 || reading[index + 1].Extensions != 0 ||
            index > 0 && reading[index - 1].Extensions != 0)
        { reason = "Target and adjacent long-vowel units are excluded."; return false; }
        var next = plan[index + 1].PhoneticContext;
        if (next is null || next.PreviousPhone != context.NextPhone)
        { reason = "Following unit does not confirm the same model consonant."; return false; }
        if (next.VowelPhone is "I" or "U" || index > 0 && plan[index - 1].PhoneticContext?.VowelPhone is "I" or "U")
        { reason = "Adjacent I/U units are excluded."; return false; }
        if (index > 0 && reading[index - 1].Alias is not null && plan[index - 1].PhoneticContext is null)
        { reason = "Previous spoken unit has no validated phonetic context."; return false; }
        return true;
    }

    private void ApplyContextFriction(List<(Mora? Sound, char Pause)> units, UtauReadingUnit[] reading,
        IReadOnlyDictionary<int, string> declined, CancellationToken ct)
    {
        for (var i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (units[i].Sound is not { } current) continue;
            var context = comparisonTimingPlan?[i].PhoneticContext;
            var source = current.FrictionSource;
            var report = new UtauContextFrictionDiagnostic(false, false, declined.TryGetValue(i, out var reason) ? reason : "No adopted unit-rule friction range.")
            {
                PreviousPhone = context?.PreviousPhone, VowelPhone = context?.VowelPhone, NextPhone = context?.NextPhone,
                ModelVoicedFramesAbsent = context?.ModelVoicedFramesAbsent,
                UnitSamples = current.Samples.Length, NextPcmUnchanged = true, BasedOnAdoptedUnitRule = source is not null,
                SourceStartSample = source?.Start, SourceEndSample = source is null ? null : source.Start + source.Length,
                AttackSamples = source?.AttackEnd
            };
            if (!declined.ContainsKey(i) && source is not null && i + 1 < units.Count && units[i + 1].Sound is { } next)
            {
                var observe = diagnosticActive && i == diagnosticUnit && unitCapture is not null;
                report = report with { Eligible = true };
                var replacement = RenderContextFriction(current, next, source, report, ct, observe);
                report = replacement.Diagnostic;
                if (replacement.Audio is null && report.ClosureRms is not null && next.NextSource is { } nextSource)
                {
                    try
                    {
                        report = report with { OriginalClosureRms = report.ClosureRms, OriginalLaterLeadMaximumRms = report.LaterLeadMaximumRms };
                        var recovery = RecoverContextNext(next, nextSource, ct);
                        report = report with { NextOriginalStartSample = nextSource.Inspection.Start,
                            NextRecoveredStartSample = recovery.Start, NextPrefixRecoveryReason = recovery.Reason };
                        if (recovery.Sound is { } recoveredNext)
                        {
                            var paired = RenderContextFriction(current, recoveredNext, source, report, ct, observe);
                            report = paired.Diagnostic;
                            if (paired.Audio is not null)
                            {
                                var pairedReport = report with { RecoveredNextPrefixSamples = nextSource.Inspection.Start - recovery.Start,
                                    NextAbsoluteLandmarksUnchanged = true, NextPcmUnchanged = false,
                                    NextPcmSha256Before = observe ? ContextHash(next.Samples) : null, NextPcmSha256After = observe ? ContextHash(recoveredNext.Samples) : null };
                                ct.ThrowIfCancellationRequested();
                                replacement = paired; report = pairedReport;
                                units[i + 1] = (recoveredNext, '\0');
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException or OverflowException or IOException)
                    { report = report with { NextPrefixRecoveryReason = "Paired redraw declined: " + error.GetType().Name }; }
                }
                if (replacement.Audio is not null) units[i] = (current with { Samples = replacement.Audio }, '\0');
            }
            if (diagnosticActive && i == diagnosticUnit) unitCapture?.SetContextFriction(report);
        }
    }

    private static (float[]? Audio, UtauContextFrictionDiagnostic Diagnostic) RenderContextFriction(Mora current,
        Mora next, ContextFrictionSource source, UtauContextFrictionDiagnostic diagnostic, CancellationToken ct, bool observe)
    {
        ct.ThrowIfCancellationRequested();
        var audible = current.Samples.Length - next.Preutterance;
        var fade = SourcePosition(5);
        diagnostic = diagnostic with { AudibleSamples = audible, FadeSamples = fade };
        if (source.Start < 0 || source.Length < 1 || (long)source.Start + source.Length > source.Samples.Length ||
            current.Samples.Length is < 1 or > MaximumSamples || audible < source.AttackEnd + OutputSampleRate / 50 ||
            audible > source.Length || source.AttackEnd < OutputSampleRate / 50)
            return (null, diagnostic with { Reason = "Recorded friction cannot cover the audible budget with its attack and another complete grain." });
        var nextHash = observe ? ContextHash(next.Samples) : null;
        if (next.Preutterance < SourcePosition(12) || next.Preutterance > next.Samples.Length)
            return (null, diagnostic with { Reason = "Following lead has insufficient bounded windows for the closure comparison." });
        var earlyStart = SourcePosition(2); var earlyEnd = SourcePosition(7); var window = earlyEnd - earlyStart;
        var laterStart = Math.Max(earlyEnd, next.Preutterance / 2); var last = next.Preutterance - window;
        if (laterStart > last) return (null, diagnostic with { Reason = "Following lead has no independent later energy window." });
        var earlyRms = ContextRms(next.Samples.AsSpan(earlyStart, window)); var maximumRms = 0.0;
        for (var at = laterStart; ; at = Math.Min(at + window, last))
        {
            ct.ThrowIfCancellationRequested();
            maximumRms = Math.Max(maximumRms, ContextRms(next.Samples.AsSpan(at, window)));
            if (at == last) break;
        }
        diagnostic = diagnostic with { ClosureRms = earlyRms, LaterLeadMaximumRms = maximumRms };
        if (!double.IsFinite(earlyRms) || !double.IsFinite(maximumRms) || maximumRms < 0.0001 || earlyRms > maximumRms * 0.25)
            return (null, diagnostic with { Reason = "Following lead did not confirm the fixed early-low/later-energy contrast." });
        var segments = Array.AsReadOnly(new[]
        {
            new UtauUnitTimeSegment(0, source.AttackEnd, 0, source.AttackEnd, true),
            new UtauUnitTimeSegment(source.AttackEnd, source.Length, source.AttackEnd, audible, false)
        });
        var map = new UtauUnitRuleMap(segments, source.AttackEnd);
        var observation = observe ? new UnitDiagnosticCapture(-1) : null;
        var shaped = ShapeMora(source.Samples.AsSpan(source.Start, source.Length), source.AttackEnd, source.AttackEnd,
            source.AttackEnd, 0, audible - source.AttackEnd, ct, out _, observation is null ? null : new GrainStatistics(observation), unitMap: map);
        var replacement = new float[current.Samples.Length]; shaped.CopyTo(replacement, 0);
        var attack = Math.Min(SourcePosition(2), audible);
        for (var i = 0; i < attack; i++) replacement[i] *= Fade(i, attack);
        for (var i = 0; i < fade; i++) replacement[audible - fade + i] *= 1 - Fade(i, fade);
        if (replacement.Any(value => !float.IsFinite(value))) throw new InvalidDataException("Context friction produced nonfinite samples.");
        var afterHash = observe ? ContextHash(next.Samples) : null;
        if (observe && !string.Equals(nextHash, afterHash, StringComparison.Ordinal)) throw new InvalidDataException("Context friction changed following-unit PCM.");
        ct.ThrowIfCancellationRequested();
        return (replacement, diagnostic with { Applied = true, Reason = "Frontend-U intent approximation: recorded friction fills the audible interval; model VUV may be mixed. Final following-lead overlap remains zero.",
            Grains = observation?.Snapshot().Grains ?? Array.Empty<UtauDiagnosticGrain>(), NextPcmSha256Before = nextHash, NextPcmSha256After = afterHash });
    }

    // The raw quiet prefix is only a low-energy closure candidate. It is never
    // adopted independently of a successful fixed-gate friction replacement.
    private (Mora? Sound, int Start, string Reason) RecoverContextNext(Mora next, ContextNextSource context, CancellationToken ct)
    {
        var source = context.Inspection;
        if (next.MappedVowelOnset is not null || source.Start < 1 || source.Pre < SourcePosition(2))
            return (null, source.Start, "Following source has no recoverable unmapped prefix.");
        var step = SourcePosition(2);
        var absolutePre = checked(source.Start + source.Pre);
        var maximum = 0.0;
        for (var at = source.Start; at < absolutePre; at += step)
        {
            ct.ThrowIfCancellationRequested();
            maximum = Math.Max(maximum, ContextRms(source.Samples.AsSpan(at, Math.Min(step, absolutePre - at))));
        }
        if (!double.IsFinite(maximum) || maximum < 0.0001)
            return (null, source.Start, "Raw leading interval has no finite non-silent comparison window.");
        var limit = Math.Max(0, source.Start - SourcePosition(60));
        var recovered = source.Start;
        while (recovered > limit)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = Math.Max(limit, recovered - step);
            var rms = ContextRms(source.Samples.AsSpan(candidate, recovered - candidate));
            if (!double.IsFinite(rms) || rms > maximum * 0.25) break;
            recovered = candidate;
        }
        if (recovered == source.Start)
            return (null, recovered, "No contiguous quiet raw prefix passes the fixed 0.25 contrast.");
        var redraw = RenderMora(context.Bank, context.Oto, context.Extensions, ct,
            next.Samples.Length - next.Preutterance, plannedLeadSamples: next.Preutterance,
            retainNextSource: true, sourceOverride: source, startOverride: recovered);
        var geometry = redraw.NextSource;
        // Absolute landmarks are unchanged by relative-coordinate expansion;
        // retaining the old overlap denominator also preserves Append placement.
        if (geometry is null || geometry.UsedStart != recovered || geometry.AbsolutePre != context.AbsolutePre ||
            geometry.AbsoluteStable != context.AbsoluteStable || geometry.UsedEnd != context.UsedEnd ||
            redraw.Samples.Length != next.Samples.Length || redraw.Preutterance != next.Preutterance ||
            redraw.Overlap != next.Overlap || redraw.MappedVowelOnset != next.MappedVowelOnset)
            return (null, recovered, "Paired redraw did not preserve length, placement or absolute landmarks.");
        return (redraw, recovered, "Contiguous quiet raw prefix recovered; fixed rendered closure gate still required.");
    }

    private static string ContextHash(float[] samples) => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
        System.Runtime.InteropServices.MemoryMarshal.AsBytes(samples.AsSpan())));

    private static double ContextRms(ReadOnlySpan<float> samples)
    {
        double energy = 0;
        foreach (var sample in samples) { if (!float.IsFinite(sample)) return double.NaN; energy += (double)sample * sample; }
        return Math.Sqrt(energy / samples.Length);
    }
}
