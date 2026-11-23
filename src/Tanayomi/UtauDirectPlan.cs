namespace Tanayomi;

internal enum UtauDirectSliceKind { Mora, Vc, CvRelease }

/// <summary>A validated original WAV snapshot on the 44100Hz grid. All bounds
/// are absolute indexes into Samples. EndSample limits the selected stable tail;
/// OtoEndSample retains the complete trimmed OTO range. No speech is rendered.</summary>
// A VC physical slice has no terminal stable vowel: Preutterance/Stable equal
// StartSample and its time map is identity. Logical mora sources keep the old
// vowel-tail contract.
internal sealed record UtauDirectSource(OtoEntry Oto, string WavePath, ReadOnlyMemory<float> Samples,
    int OtoOffsetSample, int StartSample, int PreutteranceSample, int StableSample, int EndSample,
    int OtoEndSample, double ReferencePeriodSamples);

/// <summary>DurationSamples starts at the vowel onset and excludes LeadSamples.
/// For a pause it is the complete silence length. OverlapSamples is the actual
/// intersection with the previous unit; OtoOverlapSamples is the bank's scaled
/// requested fade. Bounds are placements, not detected acoustic boundaries.</summary>
internal sealed record UtauDirectUnit(int UnitIndex, UtauReadingUnit Reading, UtauDirectSource? Source,
    int StartSample, int? VowelOnsetSample, int EndSample, int LeadSamples, int TransitionSamples,
    int DurationSamples, int OverlapSamples, int OtoOverlapSamples)
{
    internal UtauDirectSliceKind SliceKind { get; init; }
}

internal sealed record UtauDirectPlan(IReadOnlyList<UtauReadingUnit> Reading,
    IReadOnlyList<UtauDirectUnit> Units, IReadOnlyList<UtauTimingAnchor> Anchors, int SampleCount)
{
    internal int SampleRate => UtauSpeech.OutputSampleRate;
    // Physical slices may repeat a logical UnitIndex; HTS/F0 use Units/Anchors.
    internal IReadOnlyList<UtauDirectUnit>? RenderSlices { get; init; }
    internal IReadOnlyList<UtauDirectUnit> RenderUnits => RenderSlices ?? Units;
}

/// <summary>Bounded source inspection and placement for complete renderers.
/// Owns only original-source caches; never calls the legacy waveform renderer.</summary>
internal sealed class UtauDirectPlanner : IAsyncDisposable
{
    private readonly UtauSpeech reader = UtauSpeech.CreateDirectPlanReader();

    internal static Task<IReadOnlyList<UtauReadingUnit>> ReadAsync(string text, CancellationToken ct = default) =>
        UtauSpeech.ReadDirectReadingAsync(text, ct);

    internal Task<UtauDirectPlan> CreateAsync(IReadOnlyList<UtauReadingUnit> reading, Voicebank bank,
        IReadOnlyList<UtauTimingAllocation>? timing = null, CancellationToken ct = default, double? sourceTargetHz = null) =>
        reader.CreateDirectPlanAsync(reading, bank, timing, ct, sourceTargetHz);

    public ValueTask DisposeAsync() => reader.DisposeAsync();
}

public sealed partial class UtauSpeech
{
    internal static UtauSpeech CreateDirectPlanReader() => new(new ReferenceOutput()) { ApplyPitch = false };

    internal static Task<IReadOnlyList<UtauReadingUnit>> ReadDirectReadingAsync(string text, CancellationToken ct) =>
        Task.Run<IReadOnlyList<UtauReadingUnit>>(() =>
            Array.AsReadOnly(ParseReading(JapaneseReading.ToProsodicReading(text, ct), ct)), ct);

    internal async Task<UtauDirectPlan> CreateDirectPlanAsync(IReadOnlyList<UtauReadingUnit> reading, Voicebank bank,
        IReadOnlyList<UtauTimingAllocation>? timing, CancellationToken cancellationToken, double? sourceTargetHz = null)
    {
        ArgumentNullException.ThrowIfNull(reading); ArgumentNullException.ThrowIfNull(bank);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var ct = operation.Token;
        // Bound providers before enumeration/allocation. Copy through indexes so
        // a lazy or mutable caller cannot change a unit halfway through planning.
        ct.ThrowIfCancellationRequested();
        var count = reading.Count;
        if (count is < 1 or > 640 || timing is not null && timing.Count != count)
            throw new InvalidDataException("原音直接合成の読みまたは時間計画が上限外です。");
        var units = new UtauReadingUnit[count];
        var allocations = timing is null ? null : new UtauTimingAllocation[count];
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = reading[i];
            if (unit is null || unit.Extensions is < 0 or > 640 ||
                (unit.Alias is null ? unit.Extensions != 0 || unit.Pause is not ('っ' or '、' or '。' or '?' or '・' or ' ') :
                    unit.Pause != '\0' || unit.Alias.Length is < 1 or > 2 ||
                    unit.Extensions > 0 && Vowel(unit.Alias) is null))
                throw new InvalidDataException("原音直接合成の読みが不正です。");
            if (unit.Alias is not null)
            {
                var parsed = ParseReading(unit.Alias, ct);
                if (parsed.Length != 1 || parsed[0] != (unit with { Extensions = 0 }))
                    throw new InvalidDataException("原音直接合成の読みが正規形ではありません。");
            }
            units[i] = unit;
            if (allocations is not null)
            {
                var allocation = timing![i];
                if (allocation is null || allocation.Alias != unit.Alias || allocation.Pause != unit.Pause ||
                    allocation.Extensions != unit.Extensions || allocation.DurationSamples is < 1 or > MaximumSamples ||
                    allocation.LeadingSamples is < 1 or > OutputSampleRate * 700 / 1000 ||
                    unit.Alias is null && allocation.LeadingSamples is not null)
                    throw new InvalidDataException("時間計画と音源の読みまたは時間が一致しません。");
                allocations[i] = allocation;
            }
        }
        if (!units.Any(x => x.Alias is not null)) throw new InvalidDataException("読み上げる音がありません。");
        await synthesis.WaitAsync(ct).ConfigureAwait(false);
        try { return await Task.Run(() => BuildDirectPlan(units, bank, allocations, ct, sourceTargetHz), ct).ConfigureAwait(false); }
        finally { synthesis.Release(); }
    }

    private UtauDirectPlan BuildDirectPlan(UtauReadingUnit[] reading, Voicebank bank,
        UtauTimingAllocation[]? timing, CancellationToken ct, double? sourceTargetHz)
    {
        var aliases = Aliases(bank);
        var candidates = sourceTargetHz is null ? null : new UtauSourceCandidates(bank);
        var originals = new Dictionary<OtoEntry, UtauDirectSource>();
        var sourcePaths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        var retainedSamples = 0L;
        var units = new UtauDirectUnit[reading.Length];
        var anchors = new UtauTimingAnchor[reading.Length];
        var end = 0;
        var previousVowel = 0;
        for (var i = 0; i < reading.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var unit = reading[i];
            if (unit.Alias is null)
            {
                var duration = timing?[i].DurationSamples ?? SourcePosition(unit.Pause == 'っ' ? 85 : 110);
                if ((long)end + duration > MaximumSamples) throw new InvalidDataException("合成音声が長すぎます。");
                units[i] = new(i, unit, null, end, null, end + duration, 0, 0, duration, 0, 0);
                anchors[i] = new(i, null, unit.Pause, 0, end, null, end + duration);
                end += duration; previousVowel = 0; continue;
            }
            var oto = candidates is not null
                ? candidates.Select(reading, i, sourceTargetHz!.Value, ct: ct)?.Oto
                : aliases.GetValueOrDefault(unit.Alias);
            if (oto is null) throw new InvalidDataException("音源に必要な原音がありません。");
            if (!originals.TryGetValue(oto, out var original))
            {
                // Content is checked on every utterance, including unchanged
                // file stamps. Unused cached originals never become a fallback.
                var source = InspectMoraSource(bank, oto, ct, verifyContent: true);
                if (sourcePaths.Add(source.Path))
                {
                    retainedSamples += source.Samples.Length;
                    if (sourcePaths.Count > 64 || retainedSamples > OutputSampleRate * 90L)
                        throw new InvalidDataException("原音直接合成の原音保持量が上限を超えました。");
                }
                var tailEnd = source.Start + source.Stable + Math.Min(source.End - source.Start - source.Stable, SourcePosition(160));
                double energy = 0;
                for (var at = source.Start + source.Stable; at < tailEnd; at++)
                {
                    if ((at & 4095) == 0) ct.ThrowIfCancellationRequested();
                    energy += (double)source.Samples[at] * source.Samples[at];
                }
                if (tailEnd <= source.Start + source.Stable ||
                    energy / (tailEnd - source.Start - source.Stable) < 0.00000001)
                    throw new InvalidDataException("原音の母音部分が無音です。");
                original = new(oto, source.Path, source.Samples, source.OtoOffset, source.Start,
                    source.Start + source.Pre, source.Start + source.Stable, tailEnd, source.End, source.Period);
                originals.Add(oto, original);
            }
            var pre = original.PreutteranceSample - original.StartSample;
            var lead = timing?[i].LeadingSamples ?? Math.Min(pre, SourcePosition(90));
            var transition = Math.Min(original.StableSample - original.PreutteranceSample, SourcePosition(70));
            var length = timing?[i].DurationSamples ?? SourcePosition(180 + unit.Extensions * 160.0);
            var minimum = transition + OutputSampleRate / 50;
            if (length < minimum) throw new UtauTimingPlanException(i, length, minimum);
            if (timing is not null && previousVowel > 0 && previousVowel < lead)
                throw new UtauTimingPlanException(i - 1, previousVowel, lead);
            var overlap = Math.Min(previousVowel, lead);
            var start = end - overlap;
            if ((long)start + lead + length > MaximumSamples) throw new InvalidDataException("合成音声が長すぎます。");
            var otoOverlap = Math.Max(SourcePosition(8), (int)Math.Round(SourcePosition(oto.Overlap) * (double)lead / pre));
            end = start + lead + length;
            units[i] = new(i, unit, original, start, start + lead, end, lead, transition, length, overlap, otoOverlap);
            anchors[i] = new(i, unit.Alias, '\0', unit.Extensions, start, start + lead, end);
            previousVowel = length;
        }
        ct.ThrowIfCancellationRequested();
        var result = new UtauDirectPlan(Array.AsReadOnly(reading), Array.AsReadOnly(units), Array.AsReadOnly(anchors), end);
        return candidates is null ? result : UtauCvvcSlices.Create(result, candidates, sourceTargetHz!.Value, ct);
    }
}
