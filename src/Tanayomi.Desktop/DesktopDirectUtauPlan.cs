namespace Tanayomi.Desktop;

internal sealed class DesktopDirectUtauPlan(bool useProsody = true, double targetHz = 261.625565)
    : IAsyncDisposable
{
    private readonly UtauDirectPlanner sources = new();
    private readonly DesktopOpenJtalkPlanner model = new();
    internal event Action<Exception>? PlanningFallback;
    internal event Action<UtauTimingPlanException>? TimingFallback;

    internal async Task<UtauDirectRenderPlan> CreateAsync(string text, Voicebank bank, CancellationToken ct)
    {
        var reading = await UtauDirectPlanner.ReadAsync(text, ct).ConfigureAwait(false);
        OpenJtalkPlan? predicted = null;
        IReadOnlyList<UtauPlannedUnit>? mapped = null;
        IReadOnlyList<UtauTimingAllocation>? allocation = null;
        if (useProsody)
        {
            try
            {
                predicted = await model.PlanAsync(text, ct).ConfigureAwait(false);
                mapped = UtauProsodyPlan.Map(predicted, reading, ct);
                allocation = UtauProsodyTransfer.Allocate(mapped, consonantTiming: true);
            }
            catch (Exception error)
            {
                ct.ThrowIfCancellationRequested();
                predicted = null; mapped = null; allocation = null;
                Report(PlanningFallback, error);
            }
        }
        UtauDirectPlan plan;
        try { plan = await sources.CreateAsync(reading, bank, allocation, ct, sourceTargetHz: targetHz).ConfigureAwait(false); }
        catch (UtauTimingPlanException error) when (allocation is not null)
        {
            ct.ThrowIfCancellationRequested();
            plan = await sources.CreateAsync(reading, bank, null, ct, sourceTargetHz: targetHz).ConfigureAwait(false);
            Report(TimingFallback, error);
        }
        ct.ThrowIfCancellationRequested();
        double[] f0;
        if (predicted is not null && mapped is not null)
        {
            try { f0 = UtauProsodyTransfer.TransferF0(predicted, mapped, plan.Anchors, plan.SampleCount, targetHz); }
            catch (Exception error)
            {
                ct.ThrowIfCancellationRequested();
                Report(PlanningFallback, error); f0 = Flat(plan.SampleCount);
            }
        }
        else f0 = Flat(plan.SampleCount);
        ct.ThrowIfCancellationRequested();
        return new(plan, Array.AsReadOnly(f0));
    }

    private double[] Flat(int length)
    {
        if (!double.IsFinite(targetHz) || targetHz is < 70 or > 700) throw new InvalidDataException();
        var result = new double[(int)(1000.0 * length / 44100 / 5.0) + 1];
        Array.Fill(result, targetHz); return result;
    }

    private static void Report<T>(Action<T>? handlers, T error)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(error); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        try { await model.DisposeAsync().ConfigureAwait(false); }
        finally { await sources.DisposeAsync().ConfigureAwait(false); }
    }
}
