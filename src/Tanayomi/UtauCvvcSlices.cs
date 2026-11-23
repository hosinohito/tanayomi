namespace Tanayomi;

// Conservative CVVC: only two OTO aliases covering the same recording can
// establish a lossless consonant handoff without guessing an acoustic release.
internal static class UtauCvvcSlices
{
    private const int Rate = UtauSpeech.OutputSampleRate, Fade = Rate * 8 / 1000;
    internal static UtauDirectPlan Create(UtauDirectPlan plan, UtauSourceCandidates candidates, double targetHz, CancellationToken ct)
    {
        var slices = new List<UtauDirectUnit>(plan.Units.Count * 2);
        var changed = false;
        for (var i = 0; i < plan.Units.Count; i++)
        {
            ct.ThrowIfCancellationRequested(); var current = plan.Units[i];
            if (i == 0 || current.Source is not { } cv || plan.Units[i - 1].Source is null ||
                current.VowelOnsetSample is not int vowel || current.OverlapSamples != current.LeadSamples ||
                candidates.SelectVc(plan.Reading, i, targetHz, ct) is not { } vc || !TryBounds(vc, cv, ct, out var start, out var end))
            { slices.Add(current); continue; }
            var previous = slices[^1];
            var vcStart = vowel - (cv.PreutteranceSample - start);
            var vcEnd = vowel - (cv.PreutteranceSample - end);
            var remainingLead = cv.PreutteranceSample - end + Fade;
            var shortened = vcStart + Fade - previous.StartSample - previous.LeadSamples;
            if (end - start > plan.Units[i - 1].DurationSamples / 2 || vcStart < 0 ||
                shortened < previous.TransitionSamples + Rate / 50 || remainingLead < Fade * 2 ||
                vcStart < previous.StartSample + previous.LeadSamples || vcEnd <= vcStart + Fade * 2)
            { slices.Add(current); continue; }
            slices[^1] = previous with { EndSample = vcStart + Fade, DurationSamples = shortened };
            var source = new UtauDirectSource(vc, cv.WavePath, cv.Samples, start, start, start, start, end, end, 0);
            slices.Add(new(i, current.Reading, source, vcStart, null, vcEnd, 0, 0, end - start, Fade, Fade)
            { SliceKind = UtauDirectSliceKind.Vc });
            slices.Add(current with
            {
                Source = cv with { StartSample = end - Fade }, StartSample = vcEnd - Fade,
                LeadSamples = remainingLead, OverlapSamples = Fade, OtoOverlapSamples = Fade,
                SliceKind = UtauDirectSliceKind.CvRelease
            });
            changed = true;
        }
        ct.ThrowIfCancellationRequested();
        return changed ? plan with { RenderSlices = Array.AsReadOnly(slices.ToArray()) } : plan;
    }
    private static bool TryBounds(OtoEntry vc, UtauDirectSource cv, CancellationToken ct, out int start, out int end)
    {
        start = end = 0;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetFullPath(vc.WavePath), Path.GetFullPath(cv.WavePath), comparison) ||
            new[] { vc.Offset, vc.Cutoff, vc.Preutterance, vc.Consonant, vc.Overlap }.Any(x => !double.IsFinite(x) || Math.Abs(x) > 600000) ||
            vc.Offset < 0 || vc.Preutterance < 0 || vc.Consonant < 0) return false;
        long offset = (long)Math.Round(vc.Offset * Rate / 1000), cutoff = (long)Math.Round(vc.Cutoff * Rate / 1000);
        long stop = vc.Cutoff < 0 ? offset - cutoff : cv.Samples.Length - cutoff;
        var pre = offset + (long)Math.Round(vc.Preutterance * Rate / 1000);
        var fixedEnd = offset + (long)Math.Round(vc.Consonant * Rate / 1000);
        if (offset < 0 || offset >= cv.StartSample || stop > cv.Samples.Length || stop - offset < Rate / 40 ||
            pre < offset || pre > cv.StartSample || fixedEnd < offset || fixedEnd > stop ||
            stop <= cv.StartSample + Fade || stop >= cv.PreutteranceSample - Fade || stop - offset > Rate * 700 / 1000)
            return false;
        double peak = 0;
        for (var at = (int)offset; at < cv.StartSample; at++)
        {
            if ((at & 4095) == 0) ct.ThrowIfCancellationRequested();
            var sample = cv.Samples.Span[at];
            if (!float.IsFinite(sample)) return false;
            peak = Math.Max(peak, Math.Abs(sample));
        }
        if (peak < 0.0001) return false;
        start = (int)offset; end = (int)stop; return true;
    }
}
