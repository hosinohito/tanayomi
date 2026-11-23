using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tanayomi;

// Original-source TD-PSOLA: analysis and synthesis centers are on the original
// WAV and final utterance grids. There is no intermediate B/WSOLA utterance and
// no subsequent pitch conversion. Noise and transitions use nonperiodic grains.
internal sealed class UtauDirectPsola(
    Func<string, Voicebank, CancellationToken, Task<UtauDirectRenderPlan>>? planFactory = null,
    IAsyncDisposable? planOwner = null) : IUtauPcmPostprocessor, IUtauDirectRenderer, IUtauDirectDiagnostics
{
    private const int Rate = UtauSpeech.OutputSampleRate, Hop = 441, Window = 2205;
    private readonly UtauDirectPlanner sources = new();
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private readonly Dictionary<AnalysisKey, SourceAnalysis> cache = new();
    private int cacheSamples, disposed;
    private Task? disposal;
    private sealed record AnalysisKey(string Hash, int Start, int Stable, int End);
    private sealed record VoicedRun(int Start, int End, double Period, int[] Epochs);
    private sealed record SourceAnalysis(VoicedRun[] Runs);
    public event Action<Exception>? PlanningFallback;
    public event Action<UtauTimingPlanException>? TimingFallback;

    internal void NotifyPlanningFallback(Exception error) => Report(PlanningFallback, error);
    internal void NotifyTimingFallback(UtauTimingPlanException error) => Report(TimingFallback, error);

    public Task PrepareAsync(Voicebank bank, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return JapaneseReading.PrepareAsync(ct);
    }

    public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) =>
        throw new InvalidOperationException("原音周期同期方式は原音から直接合成します。");

    public async Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = operation.Token;
        var acquired = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); acquired = true;
            ct.ThrowIfCancellationRequested();
            UtauDirectRenderPlan requested;
            if (planFactory is not null) requested = await planFactory(text, bank, ct).ConfigureAwait(false);
            else
            {
                var reading = await UtauDirectPlanner.ReadAsync(text, ct).ConfigureAwait(false);
                var plan = await sources.CreateAsync(reading, bank, ct: ct, sourceTargetHz: 261.625565).ConfigureAwait(false);
                var contour = new double[(int)(1000.0 * plan.SampleCount / Rate / 5.0) + 1];
                Array.Fill(contour, 261.625565); requested = new(plan, Array.AsReadOnly(contour));
            }
            ct.ThrowIfCancellationRequested();
            return await Task.Run(() => Render(requested, ct), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("原音周期同期合成が60秒以内に完了しませんでした。"); }
        finally { if (acquired) serial.Release(); }
    }

    private PcmAudio Render(UtauDirectRenderPlan requested, CancellationToken ct)
    {
        if (requested?.Plan is not { } plan || plan.SampleCount is < 1 or > Rate * 45 ||
            plan.Units is null || requested.F0 is null)
            throw new InvalidDataException("原音周期同期の発話計画が不正です。");
        var renderUnits = plan.RenderUnits;
        var unitCount = renderUnits.Count;
        var frameCount = requested.F0.Count;
        if (unitCount < 1 || unitCount > (plan.RenderSlices is null ? 640 : 1279) ||
            plan.Units.Count is < 1 or > 640 || unitCount > plan.Units.Count * 2 - 1 ||
            frameCount != (int)(1000.0 * plan.SampleCount / Rate / 5.0) + 1)
            throw new InvalidDataException("原音周期同期の発話計画が不正です。");
        var units = new UtauDirectUnit[unitCount];
        for (var i = 0; i < units.Length; i++) { ct.ThrowIfCancellationRequested(); units[i] = renderUnits[i]; }
        var f0 = new double[frameCount];
        for (var i = 0; i < f0.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); f0[i] = requested.F0[i];
            if (!double.IsFinite(f0[i]) || f0[i] is < 40 or > 1200) throw new InvalidDataException("原音周期同期の音程計画が不正です。");
        }
        var result = new float[plan.SampleCount];
        var local = new Dictionary<UtauDirectSource, (SourceAnalysis Analysis, bool? VoicedLead)>();
        var originalMemory = new HashSet<ReadOnlyMemory<float>>();
        var retainedSamples = 0L;
        var previousEnd = 0; var warned = false;
        for (var i = 0; i < units.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = units[i];
            if (unit is null || unit.Reading is null || unit.UnitIndex < 0 || unit.UnitIndex >= plan.Units.Count ||
                (plan.RenderSlices is null ? unit.UnitIndex != i : unit.Reading != plan.Units[unit.UnitIndex].Reading ||
                    i > 0 && unit.UnitIndex < units[i - 1].UnitIndex) ||
                !Enum.IsDefined(unit.SliceKind) || unit.StartSample < 0 || unit.EndSample <= unit.StartSample ||
                unit.EndSample > result.Length || unit.DurationSamples < 1 || unit.LeadSamples < 0 ||
                (long)unit.StartSample + unit.LeadSamples + unit.DurationSamples != unit.EndSample ||
                unit.OverlapSamples < 0 || unit.OverlapSamples > (unit.SliceKind == UtauDirectSliceKind.Vc ? unit.DurationSamples : unit.LeadSamples) ||
                unit.StartSample != previousEnd - unit.OverlapSamples)
                throw new InvalidDataException("原音周期同期の単音配置が不正です。");
            if (unit.Source is null)
            {
                if (unit.Reading.Alias is not null || unit.LeadSamples != 0 || unit.OverlapSamples != 0)
                    throw new InvalidDataException("原音周期同期の休止配置が不正です。");
                var fade = Math.Min(unit.StartSample, Rate / 200);
                for (var j = 0; j < fade; j++) result[unit.StartSample - 1 - j] *= Ramp(j, fade);
                previousEnd = unit.EndSample; continue;
            }
            var source = unit.Source;
            if (unit.Reading.Alias is null ||
                (unit.SliceKind == UtauDirectSliceKind.Vc ? unit.VowelOnsetSample is not null || unit.LeadSamples != 0 ||
                    unit.TransitionSamples != 0 || unit.DurationSamples != source.EndSample - source.StartSample :
                    unit.VowelOnsetSample != unit.StartSample + unit.LeadSamples || unit.LeadSamples < 1) ||
                unit.TransitionSamples < 0 || unit.TransitionSamples >= unit.DurationSamples ||
                source.Samples.Length is < 1 or > Rate * 30 || source.StartSample < 0 ||
                (unit.SliceKind == UtauDirectSliceKind.Vc ? source.PreutteranceSample != source.StartSample || source.StableSample != source.StartSample :
                    source.PreutteranceSample <= source.StartSample || source.StableSample < source.PreutteranceSample) ||
                source.EndSample <= source.StableSample || source.OtoEndSample < source.EndSample ||
                source.OtoEndSample > source.Samples.Length || !double.IsFinite(source.ReferencePeriodSamples) ||
                source.ReferencePeriodSamples is < 0 or > Rate / 40.0)
                throw new InvalidDataException("原音周期同期の原音範囲が不正です。");
            if (originalMemory.Add(source.Samples))
            {
                retainedSamples += source.Samples.Length;
                if (originalMemory.Count > 64 || retainedSamples > Rate * 90L)
                    throw new InvalidDataException("原音周期同期の原音保持量が上限を超えました。");
            }
            if (!local.TryGetValue(source, out var cached))
            {
                cached = (AnalyzeCached(source, ct), null); local.Add(source, cached);
                if (cached.Analysis.Runs.Length == 0 && source.ReferencePeriodSamples > 0 && !warned)
                {
                    warned = true;
                    NotifyPlanningFallback(new InvalidDataException("UTAU-DIRECT-PERIOD: 原音の連続周期を確認できない区間は原音の非周期配置で保持しました。"));
                }
            }
            var sound = RenderSource(unit, f0, cached.Analysis, ct);
            var attack = Math.Min(unit.SliceKind == UtauDirectSliceKind.Mora ? Rate / 500 : unit.OverlapSamples, sound.Length);
            for (var j = 0; j < attack; j++) sound[j] *= Ramp(j, attack);
            var overlap = unit.OverlapSamples;
            var voicedLead = false;
            if (unit.SliceKind == UtauDirectSliceKind.Mora && overlap > 0)
            {
                if (cached.VoicedLead is null)
                {
                    cached.VoicedLead = UtauPitch.IsContinuouslyVoiced(source.Samples.Span.Slice(source.StartSample,
                        source.PreutteranceSample - source.StartSample), source.ReferencePeriodSamples, ct);
                    local[source] = cached;
                }
                voicedLead = cached.VoicedLead.Value;
            }
            var fadeOverlap = unit.SliceKind == UtauDirectSliceKind.Mora
                ? Math.Min(overlap, Math.Max(1, voicedLead ? overlap : unit.OtoOverlapSamples)) : overlap;
            for (var j = 0; j < overlap; j++)
                result[unit.StartSample + j] = (j < fadeOverlap ? result[unit.StartSample + j] * (1 - Ramp(j, fadeOverlap)) : 0) + sound[j];
            sound.AsSpan(overlap).CopyTo(result.AsSpan(unit.StartSample + overlap));
            previousEnd = unit.EndSample;
        }
        if (previousEnd != result.Length) throw new InvalidDataException("原音周期同期の発話終端が一致しません。");
        var peak = FindPeak(result, ct);
        if (peak < 0.0001f) throw new InvalidDataException("原音周期同期の結果が無音です。");
        return new(Quantize(result, peak, ct), Rate);
    }

    private static short[] Quantize(float[] result, float peak, CancellationToken ct)
    {
        var gain = Math.Min(1, 0.92f / peak); var edge = Math.Min(Rate / 200, result.Length / 2);
        var pcm = new short[result.Length];
        var gainVector = Vector256.Create(gain);
        var one = Vector256.Create(1f);
        var pcmScale = Vector256.Create(32767f);
        ref var input = ref MemoryMarshal.GetReference(result.AsSpan());
        ref var output = ref MemoryMarshal.GetReference(pcm.AsSpan());
        for (var i = 0; i < pcm.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            // Keep both ramps scalar. Align the interior so no 4096-sample
            // cancellation boundary can be crossed by a sixteen-sample block.
            if (Avx2.IsSupported && (i & 15) == 0 && i >= edge && i <= pcm.Length - edge - 16)
            {
                // Preserve all three float products, including envelope=1,
                // before the fixed ties-to-even rounding; never use FMA.
                var first = Avx.RoundToNearestInteger(Avx.Multiply(Avx.Multiply(
                    Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)i), gainVector), one), pcmScale));
                var second = Avx.RoundToNearestInteger(Avx.Multiply(Avx.Multiply(
                    Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)(i + 8)), gainVector), one), pcmScale));
                var packed = Avx2.PackSignedSaturate(Avx.ConvertToVector256Int32WithTruncation(first),
                    Avx.ConvertToVector256Int32WithTruncation(second));
                Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsInt16().StoreUnsafe(ref output, (nuint)i);
                i += 15; continue;
            }
            var envelope = i < edge ? Ramp(i, edge) : i >= pcm.Length - edge ? Ramp(pcm.Length - 1 - i, edge) : 1;
            pcm[i] = (short)Math.Clamp(Math.Round(result[i] * gain * envelope * 32767), short.MinValue, short.MaxValue);
        }
        return pcm;
    }

    private static float FindPeak(ReadOnlySpan<float> result, CancellationToken ct)
    {
        var peak = 0f;
        var i = 0;
        if (Vector.IsHardwareAccelerated)
        {
            var maxima = Vector<float>.Zero;
            var finiteLimit = new Vector<float>(float.MaxValue);
            for (; i <= result.Length - Vector<float>.Count; i += Vector<float>.Count)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var absolute = Vector.Abs(new Vector<float>(result[i..]));
                // Ordered comparison rejects NaN before maximum reduction.
                if (!Vector.LessThanOrEqualAll(absolute, finiteLimit))
                    throw new InvalidDataException("原音周期同期が不正な波形を生成しました。");
                maxima = Vector.Max(maxima, absolute);
            }
            for (var lane = 0; lane < Vector<float>.Count; lane++) peak = Math.Max(peak, maxima[lane]);
        }
        for (; i < result.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (!float.IsFinite(result[i])) throw new InvalidDataException("原音周期同期が不正な波形を生成しました。");
            peak = Math.Max(peak, Math.Abs(result[i]));
        }
        return peak;
    }

    private SourceAnalysis AnalyzeCached(UtauDirectSource source, CancellationToken ct)
    {
        var hash = Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(source.Samples.Span)));
        var key = new AnalysisKey(hash, source.StartSample, source.StableSample, source.EndSample);
        if (cache.TryGetValue(key, out var found)) return found;
        var analysis = Analyze(source, ct);
        if (cache.Count >= 64 || cacheSamples + source.Samples.Length > Rate * 90) { cache.Clear(); cacheSamples = 0; }
        cache.Add(key, analysis); cacheSamples += source.Samples.Length;
        return analysis;
    }

    private static SourceAnalysis Analyze(UtauDirectSource source, CancellationToken ct)
    {
        var samples = source.Samples.Span;
        for (var i = source.StartSample; i < source.EndSample; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (!float.IsFinite(samples[i])) throw new InvalidDataException("原音周期同期の原音に不正な数値があります。");
        }
        var frames = new List<(int Center, double Period)>();
        for (var at = source.StableSample; at + Window <= source.EndSample; at += Hop)
        {
            ct.ThrowIfCancellationRequested(); var frame = samples.Slice(at, Window);
            var period = UtauPitch.FindPeriod(frame, ct);
            frames.Add((at + Window / 2, period > 0 && UtauPitch.IsVoiced(frame, period) ? period : 0));
        }
        var runs = new List<VoicedRun>();
        for (var at = 0; at < frames.Count;)
        {
            if (frames[at].Period <= 0) { at++; continue; }
            var first = at++;
            while (at < frames.Count && frames[at].Period > 0 &&
                Math.Abs(Math.Log(frames[at].Period / frames[at - 1].Period)) < 0.12) at++;
            if (at - first < 3) continue;
            var period = frames.Skip(first).Take(at - first).Select(x => x.Period).Order().ElementAt((at - first) / 2);
            var start = frames[first].Center - Window / 4;
            var end = frames[at - 1].Center + Window / 4;
            var epoch = start + (int)Math.Round(period);
            // Start from a positive zero crossing, then match entire neighboring
            // cycles. A single large sample cannot pull every following epoch.
            for (var i = start + 1; i < Math.Min(end, start + period * 2); i++)
                if (samples[i - 1] <= 0 && samples[i] > 0) { epoch = i; break; }
            var epochs = new List<int> { epoch };
            while (epoch + period * 1.25 < end)
            {
                ct.ThrowIfCancellationRequested(); var expected = epoch + period;
                var half = (int)Math.Round(period / 2);
                var from = Math.Max(source.StartSample + half, (int)Math.Round(expected - period * 0.18));
                var to = Math.Min(source.EndSample - half - 1, Math.Min(end - 1, (int)Math.Round(expected + period * 0.18)));
                if (from > to || epoch - half < source.StartSample || epoch + half >= source.EndSample) break;
                var best = (int)Math.Round(expected); var score = double.NegativeInfinity;
                for (var candidate = from; candidate <= to; candidate++)
                {
                    double dot = 0, left = 0, right = 0;
                    for (var j = -half; j <= half; j += 3)
                    {
                        var a = samples[epoch + j]; var b = samples[candidate + j];
                        dot += a * b; left += a * a; right += b * b;
                    }
                    var correlation = dot / Math.Sqrt(Math.Max(1e-20, left * right)) - Math.Abs(candidate - expected) / period * 0.02;
                    if (correlation > score) { score = correlation; best = candidate; }
                }
                if (score < 0.75 || best <= epoch) break;
                epochs.Add(best); epoch = best;
            }
            if (epochs.Count >= 4) runs.Add(new(start, Math.Min(end, epochs[^1] + (int)Math.Round(period / 2)), period, epochs.ToArray()));
        }
        return new(runs.ToArray());
    }

    private static float[] RenderSource(UtauDirectUnit unit, double[] f0, SourceAnalysis analysis, CancellationToken ct)
    {
        var source = unit.Source!; var raw = source.Samples.Span;
        var length = unit.EndSample - unit.StartSample;
        var result = new float[length]; var weights = new float[length];
        var pre = source.PreutteranceSample - source.StartSample;
        var stable = source.StableSample - source.StartSample;
        var sourceLength = source.EndSample - source.StartSample;
        double SourceAt(double position) => unit.SliceKind == UtauDirectSliceKind.Vc ? source.StartSample + position : position < unit.LeadSamples
            ? source.StartSample + position * pre / unit.LeadSamples
            : position < unit.LeadSamples + unit.TransitionSamples && unit.TransitionSamples > 0
                ? source.PreutteranceSample + (position - unit.LeadSamples) * (stable - pre) / unit.TransitionSamples
                : source.StableSample + (position - unit.LeadSamples - unit.TransitionSamples) *
                    (sourceLength - stable) / (unit.DurationSamples - unit.TransitionSamples);
        double DestinationAt(double position) => unit.SliceKind == UtauDirectSliceKind.Vc ? position - source.StartSample : position < source.PreutteranceSample
            ? (position - source.StartSample) * unit.LeadSamples / pre
            : position < source.StableSample && stable > pre
                ? unit.LeadSamples + (position - source.PreutteranceSample) * unit.TransitionSamples / (stable - pre)
                : unit.LeadSamples + unit.TransitionSamples + (position - source.StableSample) *
                    (unit.DurationSamples - unit.TransitionSamples) / (sourceLength - stable);
        // Nonperiodic grains retain the original local waveform of noise,
        // closures and transitions; only their centers follow the time map.
        for (var center = 0; center < length + Hop; center += Hop)
        {
            ct.ThrowIfCancellationRequested();
            // VC has an identity time map. Extrapolate its last grain center;
            // AddGrain clips source bounds. Clamping only the source center
            // would move an earlier consonant into the final partial hop.
            var origin = SourceAt(unit.SliceKind == UtauDirectSliceKind.Vc ? center : Math.Min(center, length - 1));
            var covered = false;
            foreach (var run in analysis.Runs)
            {
                if (origin > run.Start + Hop / 2 && origin < run.End - Hop / 2)
                {
                    covered = true;
                    break;
                }
            }
            if (covered) continue;
            AddGrain(result, weights, raw, source, (int)Math.Round(origin), center, Hop, ct);
        }
        foreach (var run in analysis.Runs)
        {
            var cursor = 0;
            for (var position = DestinationAt(run.Epochs[0]); position < DestinationAt(run.End);)
            {
                ct.ThrowIfCancellationRequested();
                var origin = SourceAt(position);
                while (cursor + 1 < run.Epochs.Length && Math.Abs(run.Epochs[cursor + 1] - origin) < Math.Abs(run.Epochs[cursor] - origin)) cursor++;
                var absolute = unit.StartSample + position;
                var frame = absolute / (Rate * 0.005); var index = Math.Clamp((int)frame, 0, f0.Length - 1);
                var hz = f0[index] + (f0[Math.Min(index + 1, f0.Length - 1)] - f0[index]) * Math.Clamp(frame - index, 0, 1);
                var period = Rate / hz;
                var radius = (int)Math.Ceiling(Math.Max(run.Period, period));
                AddGrain(result, weights, raw, source, run.Epochs[cursor], (int)Math.Round(position), radius, ct,
                    source.StableSample, (int)Math.Ceiling(DestinationAt(run.Start)), (int)Math.Ceiling(DestinationAt(run.End)));
                position += period;
            }
        }
        for (var i = 0; i < length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            if (weights[i] > 0.0001f) result[i] /= weights[i];
            else
            {
                var origin = Math.Clamp(SourceAt(i), source.StartSample, source.EndSample - 1);
                var at = (int)origin; var fraction = (float)(origin - at);
                result[i] = raw[at] * (1 - fraction) + raw[Math.Min(at + 1, source.EndSample - 1)] * fraction;
            }
        }
        return result;
    }

    private static void AddGrain(float[] result, float[] weights, ReadOnlySpan<float> raw, UtauDirectSource source,
        int origin, int destination, int radius, CancellationToken ct, int sourceLower = 0,
        int destinationLower = 0, int destinationUpper = int.MaxValue)
    {
        ct.ThrowIfCancellationRequested();
        var firstSource = Math.Max(sourceLower, source.StartSample);
        var firstDestination = Math.Max(0, destinationLower);
        var endDestination = Math.Min(result.Length, destinationUpper);
        if (firstSource >= source.EndSample || firstDestination >= endDestination) return;
        var firstOffset = Math.Max(-radius, Math.Max(firstSource - origin, firstDestination - destination));
        var lastOffset = Math.Min(radius, Math.Min(source.EndSample - 1 - origin, endDestination - 1 - destination));
        for (var j = firstOffset; j <= lastOffset; j++)
        {
            var from = origin + j; var to = destination + j;
            var weight = (float)(0.5 + 0.5 * Math.Cos(Math.PI * j / radius));
            result[to] += raw[from] * weight; weights[to] += weight;
        }
    }

    private static float Ramp(int at, int length) => (float)(0.5 - 0.5 * Math.Cos(Math.PI * at / Math.Max(1, length - 1)));
    private static void Report<T>(Action<T>? handlers, T error)
    {
        if (handlers is null) return;
        foreach (Action<T> handler in handlers.GetInvocationList()) try { handler(error); } catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null) { Volatile.Write(ref disposed, 1); disposal = Task.Run(DisposeCoreAsync); }
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        try { lifetime.Cancel(); }
        finally
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try
            {
                cache.Clear(); cacheSamples = 0;
                try { if (planOwner is not null) await planOwner.DisposeAsync().ConfigureAwait(false); }
                finally { await sources.DisposeAsync().ConfigureAwait(false); }
            }
            finally { serial.Release(); lifetime.Dispose(); }
        }
    }
}
