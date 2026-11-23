namespace Tanayomi.Desktop;

/// <summary>Uses model timing only when the complete pronunciation agrees.
/// Source timing guards remain in UtauSpeech; no bank-specific overrides.</summary>
internal sealed class DesktopUtauProsody(bool timingOnly = false, bool fastAnalysis = false, WaveformPitchMethod? waveform = null) : IUtauProsodyPostprocessor
{
    private readonly DesktopOpenJtalkPlanner planner = new();
    private readonly DesktopWorldPostprocessor world = new(WorldReference.CreateAsync, fastAnalysis);
    private readonly DesktopWaveformPostprocessor? wave = waveform is { } method ? new(method) : null;

    public Task PrepareAsync(Voicebank bank, CancellationToken ct) => timingOnly ? Task.CompletedTask : wave is not null ? wave.PrepareAsync(bank, ct) : world.PrepareAsync(bank, ct);
    public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => timingOnly ? Task.FromResult(input) : wave is not null ? wave.ProcessAsync(input, bank, ct) : world.ProcessAsync(input, bank, ct);

    public async Task<IUtauUtterancePlan?> CreatePlanAsync(string text, IReadOnlyList<UtauReadingUnit> reading, CancellationToken ct)
    {
        var model = await planner.PlanAsync(text, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var units = UtauProsodyPlan.Map(model, reading, ct);
        return new Plan(model, units, UtauProsodyTransfer.Allocate(units, consonantTiming: true, phoneticPlan: model), world, timingOnly, wave);
    }

    private sealed class Plan(OpenJtalkPlan model, IReadOnlyList<UtauPlannedUnit> units,
        IReadOnlyList<UtauTimingAllocation> timing, DesktopWorldPostprocessor world, bool timingOnly, DesktopWaveformPostprocessor? wave) : IUtauUtterancePlan
    {
        public IReadOnlyList<UtauTimingAllocation> Timing => timing;
        public bool CanProcessOrdinaryTiming => true;
        public Task<PcmAudio> ProcessAsync(UtauReferenceTiming rendered, Voicebank bank, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (timingOnly) return Task.FromResult(rendered.Audio);
            // Fill the whole grid here. Only the isolated WORLD analysis decides
            // voicing and normalizes the voiced mean to this bank's calibration.
            var frames = (int)(1000.0 * rendered.Audio.Samples.Length / 44100 / 5.0) + 1;
            var grid = new double[frames]; Array.Fill(grid, 200.0);
            var contour = UtauProsodyTransfer.TransferF0(model, units, rendered, grid, 200);
            ct.ThrowIfCancellationRequested();
            return wave is not null ? wave.ProcessContourAsync(rendered.Audio, bank, contour, ct) : world.ProcessContourAsync(rendered.Audio, bank, contour, ct);
        }
    }

    public async ValueTask DisposeAsync()
    {
        try { await planner.DisposeAsync().ConfigureAwait(false); }
        finally { try { await world.DisposeAsync().ConfigureAwait(false); } finally { if (wave is not null) await wave.DisposeAsync(); } }
    }
}
