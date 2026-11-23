using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Tanayomi;

public interface IPcmOutput : IAsyncDisposable
{
    Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken);
}

public sealed record PcmAudio(short[] Samples, int SampleRate)
{
    public byte[] ToWaveBytes()
    {
        var bytes = new byte[checked(44 + Samples.Length * 2)];
        using var stream = new MemoryStream(bytes);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8); writer.Write(bytes.Length - 8); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((ushort)1); writer.Write((ushort)1);
        writer.Write(SampleRate); writer.Write(checked(SampleRate * 2));
        writer.Write((ushort)2); writer.Write((ushort)16); writer.Write("data"u8); writer.Write(Samples.Length * 2);
        foreach (var sample in Samples) writer.Write(sample);
        return bytes;
    }
}

/// <summary>Rendered OTO placement, in output samples, not detected acoustic
/// phoneme boundaries. Pause units have no alias/vowel onset; extended vowels
/// remain one unit. Overlapping units can have intersecting start/end ranges.</summary>
public sealed record UtauTimingAnchor(int UnitIndex, string? Alias, char Pause, int Extensions,
    int StartSample, int? VowelOnsetSample, int EndSample);
public sealed record UtauReferenceTiming(PcmAudio Audio, IReadOnlyList<UtauTimingAnchor> Anchors)
{
    public IReadOnlyList<UtauSourceRange> SourceRanges { get; init; } = Array.Empty<UtauSourceRange>();
    public IReadOnlyList<UtauRenderedTiming> RenderedTiming { get; init; } = Array.Empty<UtauRenderedTiming>();
}
// Explicit reference diagnostics, not detected acoustic boundaries.
public sealed record UtauRenderedTiming(int UnitIndex, int TransitionSamples, int StableOutputSamples,
    int LeadSamples, int FadeSamples, bool VoicedLead);
// StableMilliseconds is measured after OTO offset, BEFORE leading-margin trim.
// TailUsageSamples uses the renderer's 44100Hz grid. Explicit comparison only.
public sealed record UtauLandmarkOverride(int UnitIndex, double StableMilliseconds, int TailUsageSamples);
public sealed record UtauSourceRange(int UnitIndex, int SampleRate, int OtoOffsetSample, int StartSample,
    int PreutteranceSample, int OriginalStableSample, int StableSample, int EndSample, double ReferencePeriodSamples)
{
    public int GrainCount { get; init; }
    public int MaximumSourceGapSamples { get; init; }
    public int MaximumNominalDeviationSamples { get; init; }
}
// Comparison allocation in 44100Hz samples. A sound's duration starts at its
// OTO onset, not at the beginning of its consonant. Extensions are included.
public sealed record UtauTimingAllocation(string? Alias, char Pause, int Extensions, int DurationSamples)
{
    // null preserves the bank's lead for vowels/nasals or legacy callers.
    public int? LeadingSamples { get; init; }
    // Original model symbols are retained separately from canonical bank aliases.
    // A label is a contextual prediction, not evidence about the source recording.
    public UtauPhoneticContext? PhoneticContext { get; init; }
}
public sealed record UtauPhoneticContext(string PreviousPhone, string VowelPhone, string NextPhone,
    int VowelDurationSamples, bool ModelVoicedFramesAbsent);
public sealed class UtauTimingPlanException(int unitIndex, int requestedSamples, int minimumSamples)
    : Exception("比較用の時間計画が原音の子音・遷移を保持できません。")
{
    public int UnitIndex { get; } = unitIndex;
    public int RequestedSamples { get; } = requestedSamples;
    public int MinimumSamples { get; } = minimumSamples;
}

// Comparison only. All RMS values precede the utterance gain/PCM conversion
// and WORLD. Incoming is the actual time-shaped segment before Append, not the
// original WAV or a claim about an acoustic phoneme boundary.
internal sealed record UtauJoinObservation(int UnitIndex, string PreviousAlias, string Alias,
    int StartSample, int VowelOnsetSample, double PeriodSamples, int PreutteranceSamples,
    int OtoFadeSamples, int ActualFadeSamples, int VoicingWindowSamples, bool VoicedLead,
    bool PureVowelCarryApplied, double PreviousRms,
    int MinimumOffsetSamples, double JoinedMinimumRms, double IncomingAtJoinedMinimumRms,
    int IncomingMinimumOffsetSamples, double IncomingMinimumRms,
    double BeforeOnsetRms, double AfterOnsetRms);
internal sealed record UtauJoinReview(PcmAudio Audio, IReadOnlyList<UtauJoinObservation> Joins);
// Opt-in numeric diagnostics. Source landmarks are absolute original-WAV
// samples; grain source positions are relative to TrimmedStartSample. Output
// grain/window positions are relative to this unit's rendered segment start.
internal sealed record UtauDiagnosticGrain(int SourceStart, int NominalSourceStart, int OutputAt, int OverlapSamples, int WindowSamples);
internal sealed record UtauDiagnosticWindow(int OutputAt, int Samples, double IncomingRms, double PreviousContributionRms, double JoinedRms, double CrossMean);
internal sealed record UtauUnitDiagnostic(int UnitIndex, int SampleRate, int OtoOffsetSample, int TrimmedStartSample,
    int PreutteranceSample, int StableSample, int EndSample, int LeadSamples, int DurationSamples,
    IReadOnlyList<UtauDiagnosticGrain> Grains, IReadOnlyList<UtauDiagnosticWindow> AppendWindows, bool Truncated)
{
    public bool AcousticTransitionApplied { get; init; }
    public UtauAcousticTransitionProtectionDiagnostic? AcousticTransitionProtection { get; init; }
    public UtauAcousticBoundaryDiagnostic? AcousticBoundary { get; init; }
    public UtauUnitAcousticRuleDiagnostic? UnitAcousticRules { get; init; }
    public int? MappedVowelOnset { get; init; }
    public UtauUnitSourceRecoveryDiagnostic? SourceRecovery { get; init; }
    public UtauContextFrictionDiagnostic? ContextFriction { get; init; }
}
// Kept separate from numeric metadata. Only an explicitly requested audio
// comparison may persist these clones. Samples are the actual renderer floats,
// not an untouched original WAV, and have no additional gain/quantization.
internal sealed record UtauUnitDiagnosticAudio(int SampleRate, float[] SourceUsed, float[] ShapedBeforePitch)
{
    internal float[]? TransitionProtected { get; init; }
    internal UtauTransitionProtectionDiagnostic? TransitionProtection { get; init; }
    internal float[]? AcousticTransitionProtected { get; init; }
    internal UtauAcousticTransitionProtectionDiagnostic? AcousticTransitionProtection { get; init; }
}
internal sealed record UtauAcousticTransitionProtectionDiagnostic(bool Available, string? UnavailableReason,
    int? SourceStart, int? SourceEnd, int? OutputStart, int? OutputEnd, int ReferencePreSample, int LeadSamples,
    int DurationSamples, IReadOnlyList<UtauDiagnosticGrain> Grains, bool GrainsTruncated, string NominalMap, string Limitation);
internal sealed record UtauTransitionProtectionDiagnostic(bool Available, string? UnavailableReason, int ProtectedSamples,
    int SourceStart, int SourceEnd, int OutputStart, int OutputEnd, double? ProtectedStartDifferenceRatio,
    double? ProtectedEndDifferenceRatio, bool PostOnsetVoiced, IReadOnlyList<UtauDiagnosticGrain> Grains,
    bool GrainsTruncated, string NominalMap, string Limitation)
{
    public UtauAcousticBoundaryDiagnostic? AcousticBoundary { get; init; }
}
internal sealed record UtauAcousticBoundaryWindow(int StartSample, int EndSample, double? DifferenceRatio, bool Voiced);
internal sealed record UtauAcousticBoundaryDiagnostic(bool Found, string? UnavailableReason, int WindowSamples,
    int RequiredConsecutiveWindows, int ReferencePreSample, int? CandidateSample, int? DeltaSamples,
    IReadOnlyList<UtauAcousticBoundaryWindow> Windows, int? HypotheticalProtectedStart, int? HypotheticalProtectedEnd,
    int? RequiredPreSamples, int? RequiredPostSamples, int LeadSamples, int DurationSamples,
    bool? BudgetFits, string? BudgetUnavailableReason, string Scope);

/// <summary>Single-tone CV speech, with oto-aligned onsets and conservative
/// pitch/duration shaping of voiced regions. This is not a singing engine.</summary>
public sealed partial class UtauSpeech(IPcmOutput output, IUtauPcmPostprocessor? postprocessor = null) : ISpeechOutput
{
    public const int OutputSampleRate = 44100;
    private const int MaximumSamples = OutputSampleRate * 45;
    private readonly SemaphoreSlim synthesis = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Task? disposal;
    private readonly Dictionary<string, (DateTime Modified, long Length, float[] Samples, string? Hash,
        OtoEntry? Oto, InspectedMoraSource? Inspection)> cache = new();
    private int cacheSamples;
    private PitchBasis? pitchBasis;
    // Comparison instances retain their settings. A live utterance temporarily
    // assigns a planned timeline only while holding the synthesis gate.
    private List<UtauTimingAnchor>? timingAnchors;
    private List<UtauRenderedTiming>? renderedTiming;
    private int?[]? comparisonLeadingSamples;
    private List<UtauJoinObservation>? joinObservations;
    private int comparisonMoraDurationMilliseconds = 180;
    private bool comparisonCarryPureVowels;
    internal bool ShortUnvoicedReleaseForComparison { get; init; }
    internal bool DisableAcousticTransitionProtectionForComparison { get; init; }
    internal bool DisableUnitAcousticRulesForComparison { get; init; }
    internal bool DisableContextFrictionForComparison { get; init; }
    internal bool DisableSourceInspectionCacheForComparison { get; init; }
    internal bool EnableSourceInspectionDiagnostics { get; init; }
    internal int SourceInspectionAnalysisCount { get; private set; }
    internal int SourceInspectionCacheHitCount { get; private set; }
    private bool acousticContextRendering;
    private UtauTimingAllocation[]? comparisonTimingPlan;
    private Dictionary<int, UtauLandmarkOverride>? comparisonLandmarks;
    private List<UtauSourceRange>? comparisonSourceRanges;
    private int disposed;
    private int diagnosticUnit = -1;
    private bool diagnosticConfigured, diagnosticConsumed, diagnosticActive;
    private bool diagnosticCaptureAudio;
    private int synthesisStarted;
    private UnitDiagnosticCapture? unitCapture;
    // Null when the selected unit is absent/a pause or the invocation did not
    // reach waveform rendering. A subsequent invocation never replaces it.
    internal UtauUnitDiagnostic? UnitDiagnostics { get; private set; }
    internal UtauUnitDiagnosticAudio? UnitDiagnosticAudio { get; private set; }
    internal void EnableUnitDiagnostics(int unitIndex, bool captureAudio = false)
    {
        if (unitIndex is < 0 or >= 640) throw new ArgumentOutOfRangeException(nameof(unitIndex));
        if (diagnosticConfigured || Volatile.Read(ref synthesisStarted) != 0) throw new InvalidOperationException("Unit diagnostics must be initialized once before synthesis.");
        diagnosticUnit = unitIndex; diagnosticCaptureAudio = captureAudio; diagnosticConfigured = true;
    }
    public Voicebank? Voicebank { get; set; }
    private int drawnOutMode;
    public bool DrawnOutMode
    {
        get => Volatile.Read(ref drawnOutMode) != 0;
        set => Volatile.Write(ref drawnOutMode, value ? 1 : 0);
    }
    private int mode;
    private bool simpleRendering;
    private bool legacyRendering;
    private UtauDirectPsola? portableDirect;
    public UtauSynthesisMode Mode
    {
        get => (UtauSynthesisMode)Volatile.Read(ref mode);
        set
        {
            if (!Modes.Any(x => x.Mode == value)) throw new ArgumentOutOfRangeException(nameof(value));
            Volatile.Write(ref mode, (int)value);
        }
    }
    public IReadOnlyList<UtauModeChoice> Modes => postprocessor is IUtauModeProvider provider ? provider.Modes :
        [new(UtauSynthesisMode.Context, "従来方式"), new(UtauSynthesisMode.Simple, "シンプル（波形接続）"),
            new(UtauSynthesisMode.DirectPsola, "原音直接・周期同期（一定音程）")];
    private IUtauPcmPostprocessor? Processor(UtauSynthesisMode selected) => selected == UtauSynthesisMode.Simple ? null :
        postprocessor is IUtauModeProvider provider ? provider.Resolve(selected) :
        selected == UtauSynthesisMode.DirectPsola ? portableDirect ??= new() : postprocessor;
    internal bool ApplyPitch { get; init; } = true;
    // Counts only optional old-Shift analysis, not timing/voicing guards. Used
    // by offline regressions to ensure B/WORLD never performs unused work.
    internal int PitchOnlyAnalysisCount { get; private set; }
    public string Name => "UTAU";
    public event Action<double>? SynthesizedMilliseconds;
    public event Action<Exception>? ProsodyFallback;
    public event Action<UtauTimingPlanException>? ProsodyTimingFallback;

    public Task PrepareAsync(CancellationToken cancellationToken = default) => JapaneseReading.PrepareAsync(cancellationToken);

    /// <summary>Prepare a selected-bank snapshot without playback. Shares the
    /// synthesis lifetime/gate; cancelling a waiting utterance never cancels warmup.</summary>
    public async Task PrepareBankAsync(Voicebank bank, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = operation.Token;
        token.ThrowIfCancellationRequested();
        await synthesis.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (Processor(Mode) is { } processor) await processor.PrepareAsync(bank, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
        }
        catch { token.ThrowIfCancellationRequested(); throw; }
        finally { synthesis.Release(); }
    }

    public Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        SpeakWithVoicebankAsync(text, Voicebank, cancellationToken);

    internal async Task SpeakWithVoicebankAsync(string text, Voicebank? bank, CancellationToken cancellationToken)
    {
        var audio = await SynthesizeAsync(text, bank, cancellationToken).ConfigureAwait(false);
        await output.PlayAsync(audio.Samples, audio.SampleRate, cancellationToken).ConfigureAwait(false);
    }

    public Task<PcmAudio> SynthesizeAsync(string text, CancellationToken cancellationToken = default) =>
        SynthesizeAsync(text, Voicebank, cancellationToken);

    private async Task<PcmAudio> SynthesizeAsync(string text, Voicebank? bank, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (bank is null) throw new InvalidOperationException("UTAU音源を選択してください。");
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = operation.Token;
        await synthesis.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            var started = Stopwatch.GetTimestamp();
            Volatile.Write(ref synthesisStarted, 1);
            diagnosticActive = diagnosticConfigured && !diagnosticConsumed;
            if (diagnosticActive) diagnosticConsumed = true;
            // One immutable selection per utterance, including its fallback.
            var selectedMode = Mode;
            var drawnOutMode = DrawnOutMode;
            var processor = Processor(selectedMode);
            acousticContextRendering = selectedMode == UtauSynthesisMode.Context && processor is IUtauProsodyPostprocessor;
            simpleRendering = selectedMode == UtauSynthesisMode.Simple;
            legacyRendering = processor is null && !simpleRendering;
            if (processor is IUtauDirectRenderer direct)
            {
                var diagnostics = processor as IUtauDirectDiagnostics;
                if (diagnostics is not null)
                {
                    diagnostics.PlanningFallback += ReportProsodyFallback;
                    diagnostics.TimingFallback += ReportProsodyTimingFallback;
                }
                try
                {
                    var generated = await direct.RenderAsync(text, bank, drawnOutMode, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (generated is null || generated.Samples is null || generated.SampleRate != OutputSampleRate ||
                        generated.Samples.Length is < 1 or > MaximumSamples || !generated.Samples.Any(x => x != 0))
                        throw new InvalidDataException("UTAU合成の音声形式が不正です。");
                    ReportSynthesisTime(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return generated;
                }
                finally
                {
                    if (diagnostics is not null)
                    {
                        diagnostics.PlanningFallback -= ReportProsodyFallback;
                        diagnostics.TimingFallback -= ReportProsodyTimingFallback;
                    }
                }
            }
            PcmAudio result;
            var contextProcessed = false;
            IUtauUtterancePlan? context = null;
            UtauReadingUnit[]? reading = null;
            if (processor is IUtauProsodyPostprocessor planner)
            {
                try
                {
                    reading = await Task.Run(() => ParseReading(JapaneseReading.ToProsodicReading(text, token), token), token).ConfigureAwait(false);
                    context = await planner.CreatePlanAsync(text, Array.AsReadOnly(reading), token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch (Exception error) { token.ThrowIfCancellationRequested(); ReportProsodyFallback(error); }
            }
            if (context is not null)
            {
                try
                {
                    var timing = context.Timing;
                    var timingCount = timing?.Count ?? 0;
                    if (timingCount is < 1 or > 640)
                        throw new InvalidDataException("発話計画の形式が不正です。");
                    // Do not enumerate or materialize an unbounded provider
                    // before validating Count. Snapshot through bounded indexes.
                    var allocations = new UtauTimingAllocation[timingCount];
                    for (var i = 0; i < allocations.Length; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        var allocation = timing![i];
                        if (allocation is null || allocation.DurationSamples is < 1 or > MaximumSamples ||
                            allocation.LeadingSamples is < 1 or > OutputSampleRate * 700 / 1000 ||
                            allocation.Alias is null && allocation.LeadingSamples is not null)
                            throw new InvalidDataException("発話計画の形式が不正です。");
                        ValidatePhoneticContext(allocation);
                        allocations[i] = allocation;
                    }
                    var anchors = new List<UtauTimingAnchor>();
                    UtauTimingPlanException? ordinaryTimingReason = null;
                    var savedPlan = comparisonTimingPlan; var savedAnchors = timingAnchors;
                    comparisonTimingPlan = allocations; timingAnchors = anchors;
                    try
                    {
                        try { result = await Task.Run(() => Synthesize(text, bank, token, reading), token).ConfigureAwait(false); }
                        catch (UtauTimingPlanException error)
                        {
                            token.ThrowIfCancellationRequested();
                            if (!context.CanProcessOrdinaryTiming) throw;
                            // Only a renderer timing guard permits this route.
                            // Preserve the reading/source guards and discard any
                            // partial planned anchors before ordinary rendering.
                            ordinaryTimingReason = error;
                            comparisonTimingPlan = null; anchors.Clear();
                            result = await Task.Run(() => Synthesize(text, bank, token, reading), token).ConfigureAwait(false);
                        }
                    }
                    finally { comparisonTimingPlan = savedPlan; timingAnchors = savedAnchors; }
                    token.ThrowIfCancellationRequested();
                    var processed = await context.ProcessAsync(new(result, Array.AsReadOnly(anchors.ToArray())), bank, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (processed is null || processed.Samples is null || processed.SampleRate != result.SampleRate || processed.Samples.Length != result.Samples.Length)
                        throw new InvalidDataException("発話計画の音声形式が不正です。");
                    result = processed; contextProcessed = true;
                    token.ThrowIfCancellationRequested();
                    if (ordinaryTimingReason is not null) ReportProsodyTimingFallback(ordinaryTimingReason);
                }
                catch (Exception error)
                {
                    token.ThrowIfCancellationRequested(); ReportProsodyFallback(error);
                    result = await Task.Run(() => Synthesize(text, bank, token, reading), token).ConfigureAwait(false);
                }
            }
            else result = await Task.Run(() => Synthesize(text, bank, token, reading), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (processor is not null && !contextProcessed)
            {
                var processed = await processor.ProcessAsync(result, bank, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (processed is null || processed.Samples is null || processed.SampleRate != result.SampleRate ||
                    processed.Samples.Length != result.Samples.Length)
                    throw new InvalidDataException("UTAU後処理で音声の形式または長さが変わりました。");
                result = processed;
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            ReportSynthesisTime(elapsed);
            return result;
        }
        catch { token.ThrowIfCancellationRequested(); throw; }
        finally { diagnosticActive = false; unitCapture = null; acousticContextRendering = false; synthesis.Release(); }
    }

    private void ReportSynthesisTime(double elapsed)
    {
        if (SynthesizedMilliseconds is { } handlers)
            foreach (Action<double> handler in handlers.GetInvocationList())
                try { handler(elapsed); } catch (Exception) { /* A view must not interrupt speech. */ }
    }

    private void ReportProsodyFallback(Exception error)
    {
        if (ProsodyFallback is { } handlers)
            foreach (Action<Exception> handler in handlers.GetInvocationList())
                try { handler(error); } catch (Exception) { }
    }

    private void ReportProsodyTimingFallback(UtauTimingPlanException error)
    {
        if (ProsodyTimingFallback is { } handlers)
            foreach (Action<UtauTimingPlanException> handler in handlers.GetInvocationList())
                try { handler(error); } catch (Exception) { }
    }

    /// <summary>Generate B-mode reference PCM without playback or a processor.
    /// A separate instance avoids recursively taking a live synthesis gate when
    /// a postprocessor requests calibration from inside ProcessAsync.</summary>
    public static async Task<PcmAudio> SynthesizeReferenceAsync(string text, Voicebank bank,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bank);
        await using var reference = new UtauSpeech(new ReferenceOutput()) { Voicebank = bank, ApplyPitch = false };
        return await reference.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Comparison-only B rendering with the exact unit placement used
    /// by Append/AddSilence. Does not play audio or run a postprocessor.</summary>
    public static async Task<UtauReferenceTiming> SynthesizeReferenceWithTimingAsync(string text, Voicebank bank,
        CancellationToken cancellationToken = default, int moraDurationMilliseconds = 180, bool carryPureVowels = false)
    {
        ArgumentNullException.ThrowIfNull(bank);
        if (moraDurationMilliseconds is < 100 or > 300)
            throw new ArgumentOutOfRangeException(nameof(moraDurationMilliseconds), "比較のモーラ長は100〜300msで指定してください。");
        var anchors = new List<UtauTimingAnchor>();
        var details = new List<UtauRenderedTiming>();
        await using var reference = new UtauSpeech(new ReferenceOutput())
        {
            Voicebank = bank, ApplyPitch = false, timingAnchors = anchors, renderedTiming = details,
            comparisonMoraDurationMilliseconds = moraDurationMilliseconds,
            comparisonCarryPureVowels = carryPureVowels
        };
        var audio = await reference.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);
        return new(audio, Array.AsReadOnly(anchors.ToArray())) { RenderedTiming = Array.AsReadOnly(details.ToArray()) };
    }

    public static async Task<UtauReferenceTiming> SynthesizeReferenceWithTimingPlanAsync(
        string text, Voicebank bank, IReadOnlyList<UtauTimingAllocation> plan, CancellationToken cancellationToken = default,
        IReadOnlyList<UtauLandmarkOverride>? landmarkOverrides = null, IReadOnlyList<int?>? leadingSamples = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Count is < 1 or > 640) throw new InvalidDataException("比較用の時間計画が不正です。");
        var snapshot = new UtauTimingAllocation[plan.Count];
        for (var i = 0; i < snapshot.Length; i++) snapshot[i] = plan[i];
        if (snapshot.Any(unit => unit is null || unit.DurationSamples is < 1 or > MaximumSamples ||
            unit.LeadingSamples is < 1 or > OutputSampleRate * 700 / 1000 || unit.Alias is null && unit.LeadingSamples is not null))
            throw new InvalidDataException("比較用の時間計画が不正です。");
        foreach (var allocation in snapshot) ValidatePhoneticContext(allocation);
        if (leadingSamples is not null && (leadingSamples.Count != snapshot.Length ||
            leadingSamples.Any(value => value is < 1 or > OutputSampleRate * 700 / 1000)))
            throw new InvalidDataException("比較用の子音時間指定が不正です。");
        var leads = leadingSamples?.ToArray();
        if (leads is not null && leads.Where((value, index) => value is not null && snapshot[index].Alias is null).Any())
            throw new InvalidDataException("休止へ子音時間を指定できません。");
        Dictionary<int, UtauLandmarkOverride>? landmarks = null;
        List<UtauSourceRange>? ranges = null;
        if (landmarkOverrides is { Count: > 0 })
        {
            if (landmarkOverrides.Count > snapshot.Length) throw new InvalidDataException("比較用の原音境界指定が多すぎます。");
            landmarks = new(); ranges = new();
            for (var i = 0; i < landmarkOverrides.Count; i++)
            {
                var item = landmarkOverrides[i];
                if (item is null || item.UnitIndex < 0 || item.UnitIndex >= snapshot.Length || snapshot[item.UnitIndex].Alias is null ||
                    !double.IsFinite(item.StableMilliseconds) || item.StableMilliseconds < 0 || item.StableMilliseconds > 45000 ||
                    item.TailUsageSamples < OutputSampleRate / 50 || item.TailUsageSamples > OutputSampleRate * 160 / 1000 ||
                    !landmarks.TryAdd(item.UnitIndex, item)) throw new InvalidDataException("比較用の原音境界指定が不正です。");
            }
        }
        var anchors = new List<UtauTimingAnchor>();
        var details = new List<UtauRenderedTiming>();
        await using var reference = new UtauSpeech(new ReferenceOutput())
        {
            Voicebank = bank, ApplyPitch = false, timingAnchors = anchors, renderedTiming = details, comparisonTimingPlan = snapshot,
            comparisonLandmarks = landmarks, comparisonSourceRanges = ranges, comparisonLeadingSamples = leads
        };
        var audio = await reference.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);
        return new(audio, Array.AsReadOnly(anchors.ToArray()))
        { SourceRanges = ranges is null ? Array.Empty<UtauSourceRange>() : Array.AsReadOnly(ranges.ToArray()),
            RenderedTiming = Array.AsReadOnly(details.ToArray()) };
    }

    internal static async Task<UtauJoinReview> SynthesizeReferenceWithJoinObservationsAsync(
        string text, Voicebank bank, CancellationToken cancellationToken = default, bool carryPureVowels = false,
        bool shortUnvoicedReleaseForComparison = false)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var joins = new List<UtauJoinObservation>();
        await using var reference = new UtauSpeech(new ReferenceOutput())
        {
            Voicebank = bank, ApplyPitch = false, joinObservations = joins, comparisonCarryPureVowels = carryPureVowels,
            ShortUnvoicedReleaseForComparison = shortUnvoicedReleaseForComparison
        };
        var audio = await reference.SynthesizeAsync(text, cancellationToken).ConfigureAwait(false);
        return new(audio, Array.AsReadOnly(joins.ToArray()));
    }

    private sealed class ReferenceOutput : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("基準音声は再生しません。");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>Check pronunciation/alias coverage only, without opening WAVs.
    /// False means an alias is absent; reading/cancellation failures still throw.
    /// A true result does not assert that source files or OTO timing are valid.</summary>
    public static bool HasRequiredAliases(string text, Voicebank bank, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bank);
        var units = ParseReading(JapaneseReading.ToProsodicReading(text, cancellationToken), cancellationToken);
        var aliases = Aliases(bank);
        return units.All(unit => unit.Alias is null || aliases.ContainsKey(unit.Alias));
    }

    private static Dictionary<string, OtoEntry> Aliases(Voicebank bank) =>
        bank.Entries.GroupBy(e => JapaneseReading.Hiragana(e.Alias.Normalize()), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static UtauReadingUnit[] ParseReading(string reading, CancellationToken ct)
    {
        var units = new List<UtauReadingUnit>();
        for (var i = 0; i < reading.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            var c = reading[i];
            if (c is '、' or '。' or '?' or '・' or ' ')
            {
                units.Add(new(null, 0, c)); continue;
            }
            if (c == 'っ') { units.Add(new(null, 0, c)); continue; }
            if (c == 'ー') throw new InvalidDataException("長音の前の母音がありません。");
            var alias = c.ToString();
            if (i + 1 < reading.Length && "ゃゅょぁぃぅぇぉゎ".Contains(reading[i + 1]))
            {
                alias += reading[++i];
            }
            alias = alias switch { "ぢ" => "じ", "づ" => "ず", "ぁ" => "あ", "ぃ" => "い", "ぅ" => "う", "ぇ" => "え", "ぉ" => "お", "ゎ" => "わ", _ => alias };
            var extensions = 0;
            while (i + 1 < reading.Length && reading[i + 1] == 'ー')
            {
                if (Vowel(alias) is null) throw new InvalidDataException("長音の前の母音がありません。");
                extensions++; i++;
            }
            units.Add(new(alias, extensions, '\0'));
        }
        return units.ToArray();
    }

    private PcmAudio Synthesize(string text, Voicebank bank, CancellationToken ct, UtauReadingUnit[]? preparedReading = null)
    {
        if (diagnosticActive) { unitCapture = null; UnitDiagnostics = null; UnitDiagnosticAudio = null; }
        var reading = preparedReading ?? ParseReading(JapaneseReading.ToProsodicReading(text, ct), ct);
        if (comparisonTimingPlan is { } plan && (plan.Length != reading.Length ||
            reading.Where((unit, index) => unit.Alias != plan[index].Alias || unit.Pause != plan[index].Pause ||
                unit.Extensions != plan[index].Extensions).Any()))
            throw new InvalidDataException("時間計画と音源の読みが一致しません。");
        var aliases = Aliases(bank);
        var units = new List<(Mora? Sound, char Pause)>();
        var contextFriction = acousticContextRendering && !DisableContextFrictionForComparison;
        var contextDeclined = contextFriction ? new Dictionary<int, string>() : null;
        var retainedSources = contextFriction ? new HashSet<float[]>(ReferenceEqualityComparer.Instance) : null;
        var retainedSamples = 0L;
        var plannedSamples = 0;
        for (var unitIndex = 0; unitIndex < reading.Length; unitIndex++)
        {
            var unit = reading[unitIndex];
            ct.ThrowIfCancellationRequested();
            if (unit.Alias is null) { units.Add((null, unit.Pause)); continue; }
            if (!aliases.TryGetValue(unit.Alias, out var oto)) throw new InvalidDataException("音源に必要な単独音がありません。");
            Mora segment;
            var capture = diagnosticActive && unitIndex == diagnosticUnit ? unitCapture = new UnitDiagnosticCapture(unitIndex) : null;
            var retainFriction = false;
            if (contextDeclined is not null)
            {
                retainFriction = CanCompareContextFriction(unitIndex, reading, out var reason);
                if (!retainFriction) contextDeclined[unitIndex] = reason;
            }
            var landmark = comparisonLandmarks is not null && comparisonLandmarks.TryGetValue(unitIndex, out var item) ? item : null;
            try { segment = RenderMora(bank, oto, unit.Extensions, ct, comparisonTimingPlan?[unitIndex].DurationSamples, landmark,
                comparisonLeadingSamples?[unitIndex] ?? comparisonTimingPlan?[unitIndex].LeadingSamples, capture, retainFriction, retainNextSource: contextDeclined is not null && unitIndex > 0 && CanCompareContextFriction(unitIndex - 1, reading, out _)); }
            catch (UtauTimingPlanException error) { throw new UtauTimingPlanException(unitIndex, error.RequestedSamples, error.MinimumSamples); }
            var retainedArray = segment.FrictionSource?.Samples ?? segment.NextSource?.Inspection.Samples;
            if (retainedArray is { } retained && retainedSources is not null && !retainedSources.Contains(retained))
            {
                if (retainedSamples + retained.Length > OutputSampleRate * 90L)
                {
                    segment = segment with { FrictionSource = null, NextSource = null };
                    contextDeclined![unitIndex] = "Context source-reference budget exceeded.";
                }
                else { retainedSources.Add(retained); retainedSamples += retained.Length; }
            }
            units.Add((segment, '\0'));
            plannedSamples += segment.Samples.Length - segment.Preutterance;
            if (plannedSamples > MaximumSamples)
                throw new InvalidDataException("合成音声が長すぎます。");
        }
        if (contextDeclined is not null) ApplyContextFriction(units, reading, contextDeclined, ct);
        // This baseline is consumed only by the old Shift below. B/WORLD must
        // not render extra reference vowels merely to compute an unused value.
        var referencePeriod = legacyRendering && ApplyPitch ? ReferencePeriod(bank, aliases, ct) : 0;
        var result = new List<float>(OutputSampleRate * 4);
        var previousVowel = 0;
        var phraseStart = 0;
        var phraseEnd = 0;
        for (var i = 0; i < units.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (sound, pause) = units[i];
            if (sound is null)
            {
                var pauseStart = result.Count;
                if (comparisonTimingPlan is { } timings) AddSilenceSamples(result, timings[i].DurationSamples);
                else AddSilence(result, pause == 'っ' ? 85 : 110);
                timingAnchors?.Add(new(i, null, pause, 0, pauseStart, null, result.Count));
                previousVowel = 0; continue;
            }
            if (i >= phraseEnd)
            {
                phraseStart = i; phraseEnd = i;
                while (phraseEnd < units.Count && units[phraseEnd].Pause is not ('。' or '?')) phraseEnd++;
            }
            var progress = (double)(i - phraseStart) / Math.Max(1, phraseEnd - phraseStart - 1);
            var semitones = 0.7 - 1.7 * progress;
            var question = phraseEnd < units.Count && units[phraseEnd].Pause == '?';
            if (question && progress > 0.65) semitones += (progress - 0.65) / 0.35 * 2.3;
            if (legacyRendering && ApplyPitch && sound.Period > 0 && referencePeriod > 0)
            {
                var ratio = Math.Clamp(sound.Period / referencePeriod * Math.Pow(2, semitones / 12), Math.Pow(2, -4.0 / 12), Math.Pow(2, 4.0 / 12));
                UtauPitch.Shift(sound.Samples.AsSpan(sound.StableStart), sound.Period, ratio, ct);
            }
            // A note's preutterance starts before its vowel-onset anchor, inside
            // the preceding vowel. Consequently a long recorded fixed vowel no
            // longer delays every following note's vowel onset.
            if (comparisonTimingPlan is not null && previousVowel > 0 && previousVowel < sound.Preutterance)
                throw new UtauTimingPlanException(i - 1, previousVowel, sound.Preutterance);
            var advance = Math.Min(previousVowel, sound.Preutterance);
            var voicedWindow = Math.Min(advance, OutputSampleRate * 30 / 1000);
            var voicedLead = UtauPitch.IsVoiced(sound.Samples.AsSpan(sound.Preutterance - voicedWindow, voicedWindow), sound.Period);
            // A weak but periodic incoming onset is a voiced connection, not a
            // closure. Keep the old vowel until its onset instead of discarding
            // the whole interval after the shorter oto-overlap fade. Unvoiced
            // and uncertain onsets retain the bank's original closure handling.
            // Explicit comparison only: bridge a pure vowel's weak recorded
            // lead with the previous vowel. No consonant alias or post-pause
            // start is eligible; recorded attack and segment timing stay intact.
            var vowelCarry = comparisonCarryPureVowels && advance > 0 &&
                (reading[i].Alias is "あ" or "い" or "う" or "え" or "お" or "を");
            var fadeOverlap = voicedLead || vowelCarry ? advance : sound.Overlap;
            if (ShortUnvoicedReleaseForComparison && !voicedLead && !vowelCarry)
                fadeOverlap = Math.Min(fadeOverlap, OutputSampleRate / 100);
            renderedTiming?.Add(new(i, sound.Transition, sound.StableStart, sound.Preutterance,
                Math.Min(advance, Math.Max(1, fadeOverlap)), voicedLead));
            if (diagnosticActive && i == diagnosticUnit)
                unitCapture?.ObserveAppend(result, sound.Samples, advance, fadeOverlap, ct);
            var start = Append(result, sound.Samples, advance, fadeOverlap);
            if (diagnosticActive && i == diagnosticUnit) UnitDiagnostics = unitCapture?.Snapshot();
            if (joinObservations is not null && advance > 0)
                joinObservations.Add(ObserveJoin(result, sound, i, reading[i - 1].Alias!, reading[i].Alias!,
                    start, advance, fadeOverlap, voicedLead, vowelCarry && !voicedLead && sound.Overlap < advance, ct));
            timingAnchors?.Add(new(i, reading[i].Alias, '\0', reading[i].Extensions,
                start, start + (sound.MappedVowelOnset ?? sound.Preutterance), start + sound.Samples.Length));
            previousVowel = sound.Samples.Length - sound.Preutterance;
        }
        if (result.Count == 0 || result.All(x => Math.Abs(x) < 0.0001f)) throw new InvalidDataException("音源から有効な音声を生成できません。");
        // One gain for the utterance preserves the bank's relative consonant/vowel
        // levels. Summed transitions receive headroom without per-mora pumping.
        var peak = OutputPeak(result);
        var gain = Math.Min(1f, 0.92f / peak);
        var fade = Math.Min(OutputSampleRate / 200, result.Count / 2);
        {
            var samples = CollectionsMarshal.AsSpan(result);
            for (var i = 0; i < samples.Length; i++) samples[i] *= gain;
        }
        for (var i = 0; i < fade; i++) { result[i] *= Fade(i, fade); result[^(i + 1)] *= Fade(i, fade); }
        return new(QuantizePcm(result), OutputSampleRate);
    }

    private static float OutputPeak(List<float> result)
    {
        if (!Vector.IsHardwareAccelerated || result.Count < Vector<float>.Count)
            return result.Max(x => Math.Abs(x));
        var samples = CollectionsMarshal.AsSpan(result);
        var maxima = Vector<float>.Zero;
        var finiteLimit = new Vector<float>(float.MaxValue);
        var i = 0;
        for (; i <= samples.Length - Vector<float>.Count; i += Vector<float>.Count)
        {
            var absolute = Vector.Abs(new Vector<float>(samples[i..]));
            // Preserve LINQ's selection rules for any nonfinite input.
            if (!Vector.LessThanOrEqualAll(absolute, finiteLimit)) return result.Max(x => Math.Abs(x));
            maxima = Vector.Max(maxima, absolute);
        }
        var peak = 0f;
        for (var lane = 0; lane < Vector<float>.Count; lane++) peak = Math.Max(peak, maxima[lane]);
        for (; i < samples.Length; i++)
        {
            var value = Math.Abs(samples[i]);
            if (!float.IsFinite(value)) return result.Max(x => Math.Abs(x));
            peak = Math.Max(peak, value);
        }
        return peak;
    }

    private static short[] QuantizePcm(List<float> result)
    {
        // Synthesis owns this list and has finished every resize and gain/fade.
        var samples = CollectionsMarshal.AsSpan(result);
        var pcm = new short[samples.Length]; var i = 0;
        if (Avx2.IsSupported)
        {
            var scale = Vector256.Create(32767f); var limit = Vector256.Create(32768f); var sign = Vector256.Create(-0f);
            ref var input = ref MemoryMarshal.GetReference(samples);
            ref var output = ref MemoryMarshal.GetReference(pcm.AsSpan());
            for (; i <= samples.Length - 16; i += 16)
            {
                var first = Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)i), scale);
                var second = Avx.Multiply(Vector256.LoadUnsafe(ref input, (nuint)(i + 8)), scale);
                // Outside this ordered range, preserve the original int cast
                // before Clamp, including nonfinite and huge finite inputs.
                if (Avx.MoveMask(Avx.Compare(Avx.AndNot(sign, first), limit, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)) == 255 &&
                    Avx.MoveMask(Avx.Compare(Avx.AndNot(sign, second), limit, FloatComparisonMode.OrderedLessThanOrEqualNonSignaling)) == 255)
                {
                    var packed = Avx2.PackSignedSaturate(Avx.ConvertToVector256Int32WithTruncation(Avx.RoundToNearestInteger(first)),
                        Avx.ConvertToVector256Int32WithTruncation(Avx.RoundToNearestInteger(second)));
                    Avx2.Permute4x64(packed.AsInt64(), 0xD8).AsInt16().StoreUnsafe(ref output, (nuint)i);
                }
                else for (var lane = 0; lane < 16; lane++) pcm[i + lane] = Scalar(samples[i + lane]);
            }
        }
        for (; i < samples.Length; i++) pcm[i] = Scalar(samples[i]);
        return pcm;

        static short Scalar(float x) => (short)Math.Clamp((int)Math.Round(x * 32767), short.MinValue, short.MaxValue);
    }

    private sealed record Mora(float[] Samples, int Preutterance, int StableStart, int Overlap, double Period, int Transition)
    {
        // Append placement retains Preutterance; only the acoustic/F0 anchor
        // follows a successful internal unit-map reallocation.
        internal int? MappedVowelOnset { get; init; }
        internal ContextFrictionSource? FrictionSource { get; init; }
        internal ContextNextSource? NextSource { get; init; }
    }
    private sealed record PitchBasis(string Directory, OtoEntry[] Entries, (DateTime Modified, long Length)[] Stamps, double Period);

    private static UtauJoinObservation ObserveJoin(List<float> joined, Mora incoming, int unitIndex,
        string previousAlias, string alias, int start, int advance, int fadeOverlap, bool voicedLead,
        bool pureVowelCarryApplied, CancellationToken ct)
    {
        static double Rms(IReadOnlyList<float> samples, int from, int count)
        {
            double energy = 0;
            for (var i = from; i < from + count; i++) energy += (double)samples[i] * samples[i];
            return count == 0 ? 0 : Math.Sqrt(energy / count);
        }
        var window = Math.Min(advance, OutputSampleRate / 100);
        var last = advance - window;
        var minimum = double.PositiveInfinity; var incomingMinimum = double.PositiveInfinity;
        var minimumAt = 0; var incomingMinimumAt = 0;
        for (var at = 0; ; at = Math.Min(at + OutputSampleRate / 200, last))
        {
            ct.ThrowIfCancellationRequested();
            var level = Rms(joined, start + at, window);
            var incomingLevel = Rms(incoming.Samples, at, window);
            if (level < minimum) { minimum = level; minimumAt = at; }
            if (incomingLevel < incomingMinimum) { incomingMinimum = incomingLevel; incomingMinimumAt = at; }
            if (at == last) break;
        }
        var onset = start + incoming.Preutterance;
        var before = Math.Min(OutputSampleRate / 100, incoming.Preutterance);
        var previousWindow = Math.Min(start, OutputSampleRate / 50);
        return new(unitIndex, previousAlias, alias, start, onset, incoming.Period, incoming.Preutterance,
            incoming.Overlap, Math.Min(advance, Math.Max(1, fadeOverlap)),
            Math.Min(advance, OutputSampleRate * 30 / 1000), voicedLead, pureVowelCarryApplied,
            Rms(joined, start - previousWindow, previousWindow), minimumAt, minimum,
            Rms(incoming.Samples, minimumAt, window), incomingMinimumAt, incomingMinimum,
            Rms(joined, onset - before, before), Rms(joined, onset, OutputSampleRate / 50));
    }

    private double ReferencePeriod(Voicebank bank, Dictionary<string, OtoEntry> aliases, CancellationToken ct)
    {
        // A small fixed set makes the voice's baseline independent of chat text.
        // Do not decode an entire bank merely to estimate its habitual pitch.
        var entries = new[] { "あ", "い", "う", "え", "お" }.Where(aliases.ContainsKey).Select(x => aliases[x]).ToArray();
        if (entries.Length == 0) entries = bank.Entries.Take(3).ToArray();
        var stamps = entries.Select(entry =>
        {
            var path = Voicebanks.ContainedPath(bank.Directory, Path.GetRelativePath(bank.Directory, entry.WavePath));
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : (DateTime.MinValue, 0L);
        }).ToArray();
        if (pitchBasis is { } basis && basis.Directory == bank.Directory && basis.Entries.SequenceEqual(entries) && basis.Stamps.SequenceEqual(stamps))
            return basis.Period;
        PitchOnlyAnalysisCount++;
        var periods = new List<double>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var period = RenderMora(bank, entry, 0, ct).Period;
                if (period > 0) periods.Add(period);
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException)
            { /* An unused reference sample must not make an otherwise usable bank fail. */ }
        }
        periods.Sort();
        var median = periods.Count == 0 ? 0 : periods[periods.Count / 2];
        pitchBasis = new(bank.Directory, entries, stamps, median);
        return median;
    }

    private Mora RenderMora(Voicebank bank, OtoEntry oto, int extensions, CancellationToken ct, int? plannedDurationSamples = null,
        UtauLandmarkOverride? landmark = null, int? plannedLeadSamples = null, UnitDiagnosticCapture? diagnostic = null, bool retainFriction = false, bool retainNextSource = false, InspectedMoraSource? sourceOverride = null, int? startOverride = null)
    {
        var inspected = sourceOverride ?? InspectMoraSource(bank, oto, ct);
        var source = inspected.Samples;
        var otoOffset = inspected.OtoOffset;
        var start = inspected.Start;
        var leading = inspected.Leading;
        var pre = inspected.Pre;
        var overlapReferencePre = pre;
        var stable = inspected.Stable;
        var end = inspected.End;
        var period = inspected.Period;
        if (startOverride is int recoveredStart)
        {
            if (recoveredStart < 0 || recoveredStart >= start) throw new InvalidDataException("Invalid next-source recovery range.");
            var recovered = start - recoveredStart;
            start = recoveredStart; pre += recovered; stable += recovered;
        }
        var originalStable = stable;
        int Position(double milliseconds) => SourcePosition(milliseconds);
        if (landmark is not null)
        {
            var candidate = Position(landmark.StableMilliseconds) - leading;
            if (plannedDurationSamples is null || period <= 0 || candidate < pre || candidate >= originalStable ||
                candidate + landmark.TailUsageSamples > end - start)
                throw new InvalidDataException("比較用の原音境界が採用できません。");
            stable = candidate;
        }
        var leadLength = plannedLeadSamples ?? Math.Min(pre, Position(90));
        var transitionLength = Math.Min(stable - pre, Position(70));
        var duration = plannedDurationSamples ?? Position(comparisonMoraDurationMilliseconds + extensions * 160.0);
        if (plannedDurationSamples is not null && duration < transitionLength + OutputSampleRate / 50)
            throw new UtauTimingPlanException(-1, duration, transitionLength + OutputSampleRate / 50);
        if ((long)leadLength + duration > MaximumSamples) throw new InvalidDataException("合成音声が長すぎます。");
        var tail = landmark?.TailUsageSamples ?? Math.Min(end - start - stable, Position(160));
        if (landmark is not null)
        {
            if (tail > duration - transitionLength) throw new InvalidDataException("比較用の原音末尾が時間計画より長すぎます。");
            // A local period can differ slightly from the later reference. This
            // only validates periodicity; it does not prove spectral stationarity.
            var window = Math.Min(tail, Position(30));
            var last = tail - window;
            for (var at = 0; ; at = Math.Min(at + Position(10), last))
            {
                ct.ThrowIfCancellationRequested();
                var frame = source.AsSpan(start + stable + at, window);
                var localPeriod = UtauPitch.FindPeriod(frame, ct);
                if (localPeriod <= 0 || Math.Abs(Math.Log(localPeriod / period)) > Math.Log(1.12) || !UtauPitch.IsVoiced(frame, localPeriod))
                    throw new InvalidDataException("比較用の原音末尾に連続した周期性がありません。");
                if (at == last) break;
            }
        }
        var vowelEnd = stable + tail;
        double energy = 0;
        for (var i = start + stable; i < start + vowelEnd; i++) energy += (double)source[i] * source[i];
        if (energy / (vowelEnd - stable) < 0.00000001) throw new InvalidDataException("原音の母音部分が無音です。");
        AcousticTimeMap? adoptedMap = null;
        UtauAcousticBoundaryDiagnostic? boundaryObservation = null;
        if (acousticContextRendering && plannedDurationSamples is not null && plannedLeadSamples is not null && pre > leadLength && stable == pre)
        {
            var boundary = ObserveAcousticBoundary(source.AsSpan(start, vowelEnd), pre, leadLength, duration, period, start, ct);
            var selection = SelectAcousticTransitionMap(vowelEnd, pre, stable, leadLength, transitionLength, duration, start, boundary, ct);
            boundaryObservation = boundary;
            if (!DisableAcousticTransitionProtectionForComparison) adoptedMap = selection.Map;
            diagnostic?.SetAcousticTransition(selection.Diagnostic, boundary, adoptedMap is not null);
        }
        UtauUnitRuleMap? unitMap = null;
        if (startOverride is null && !DisableUnitAcousticRulesForComparison && acousticContextRendering && plannedDurationSamples is not null &&
            plannedLeadSamples is not null && pre > leadLength && stable == pre)
        {
            boundaryObservation ??= ObserveAcousticBoundary(source.AsSpan(start, vowelEnd), pre, leadLength, duration, period, start, ct);
            var transitionFrom = boundaryObservation.Found && boundaryObservation.HypotheticalProtectedStart is int from ? from - start : -1;
            var transitionTo = boundaryObservation.Found && boundaryObservation.HypotheticalProtectedEnd is int to ? to - start : -1;
            var rule = UtauUnitTimeMap.Create(source.AsSpan(start, vowelEnd), pre, leadLength, checked(leadLength + duration),
                transitionFrom, transitionTo, period, ct);
            if (rule.Map is not null)
            {
                var recovery = UtauUnitTimeMap.FindContinuousPrefix(source, start, period, ct);
                if (recovery.Start < start)
                {
                    var recovered = start - recovery.Start;
                    var expanded = UtauUnitTimeMap.Create(source.AsSpan(recovery.Start, vowelEnd + recovered), pre + recovered,
                        leadLength, checked(leadLength + duration), transitionFrom + recovered, transitionTo + recovered, period, ct);
                    rule = expanded;
                    if (expanded.Map is not null)
                    {
                        start = recovery.Start; pre += recovered; stable += recovered; originalStable += recovered; vowelEnd += recovered;
                        diagnostic?.SetSourceRecovery(recovery.Diagnostic with { Adopted = true });
                    }
                    else diagnostic?.SetSourceRecovery(recovery.Diagnostic with
                        { Reason = "Continuous prefix candidate was not adopted because expanded-source unit rules failed: " + expanded.Diagnostic.UnavailableReason });
                }
                else diagnostic?.SetSourceRecovery(recovery.Diagnostic);
            }
            unitMap = rule.Map;
            if (unitMap is not null)
            {
                duration = rule.Diagnostic.OutputLength - leadLength;
                if (duration < 1 || (long)leadLength + duration > MaximumSamples)
                    throw new InvalidDataException("Unit acoustic rule output exceeds the synthesis limit.");
            }
            diagnostic?.SetUnitAcousticRules(rule.Diagnostic, unitMap?.MappedPre);
        }
        diagnostic?.SetSource(otoOffset, start, pre, stable, vowelEnd, leadLength, duration);
        var grains = landmark is null && diagnostic is null ? null : new GrainStatistics(diagnostic);
        var segment = ShapeMora(source.AsSpan(start, vowelEnd), pre, stable, leadLength, transitionLength, duration, ct, out var pitchStart, grains,
            acousticMap: adoptedMap, unitMap: unitMap);
        if (diagnostic is not null && diagnosticCaptureAudio)
        {
            ct.ThrowIfCancellationRequested();
            // Normal renderer guards already bound both arrays. Reassert the
            // observation boundary before copying without touching either input.
            if (vowelEnd is < 1 or > MaximumSamples || segment.Length is < 1 or > MaximumSamples)
                throw new InvalidDataException("Unit diagnostic audio limit.");
            var candidate = RenderTransitionProtection(source.AsSpan(start, vowelEnd), pre, stable, leadLength,
                transitionLength, duration, period, start, ct);
            var acoustic = RenderAcousticTransitionProtection(source.AsSpan(start, vowelEnd), pre, stable, leadLength,
                transitionLength, duration, start, candidate.Diagnostic.AcousticBoundary, ct);
            UnitDiagnosticAudio = new(OutputSampleRate, source.AsSpan(start, vowelEnd).ToArray(), (float[])segment.Clone())
            {
                TransitionProtected = candidate.Audio, TransitionProtection = candidate.Diagnostic,
                AcousticTransitionProtected = acoustic.Audio, AcousticTransitionProtection = acoustic.Diagnostic
            };
        }
        // A wholly voiced onset (vowels/nasals, for example) need not retain an
        // unrelated recorded pitch before the vowel. Require both periodicity
        // and an independent matching period, rather than a phoneme-name rule.
        // Only Shift consumes pitchStart; the main timing/period analysis above
        // remains mandatory for B/WORLD even though this onset estimate is not.
        if (legacyRendering && ApplyPitch)
        {
            PitchOnlyAnalysisCount++;
            if (UtauPitch.IsContinuouslyVoiced(source.AsSpan(start, stable), period, ct))
            {
                var onsetPeriod = UtauPitch.FindPeriod(source.AsSpan(start, stable), ct);
                if (onsetPeriod > 0 && Math.Abs(Math.Log(onsetPeriod / period)) < Math.Log(1.12)) pitchStart = 0;
            }
        }
        // A short onset ramp avoids a step without washing out the consonant.
        var attack = Math.Min(Position(2), segment.Length);
        for (var i = 0; i < attack; i++) segment[i] *= Fade(i, attack);
        var overlap = Math.Max(Position(8), (int)Math.Round(Position(oto.Overlap) * (double)leadLength / overlapReferencePre));
        if (landmark is not null)
            comparisonSourceRanges?.Add(new(landmark.UnitIndex, OutputSampleRate, otoOffset, start, start + pre,
                start + originalStable, start + stable, start + vowelEnd, period)
            {
                GrainCount = grains!.Count, MaximumSourceGapSamples = grains.MaximumGap,
                MaximumNominalDeviationSamples = grains.MaximumDeviation
            });
        var friction = retainFriction && unitMap is not null ? new ContextFrictionSource(source, start,
            unitMap.Segments[2].SourceStart, unitMap.Segments[0].SourceEnd) : null;
        return new(segment, leadLength, pitchStart, overlap, period, transitionLength)
            { MappedVowelOnset = unitMap?.MappedPre, FrictionSource = friction,
                NextSource = retainNextSource ? new ContextNextSource(bank, oto, extensions, inspected, start, start + pre, start + stable, start + vowelEnd) : null };
    }

    private sealed record InspectedMoraSource(string Path, float[] Samples, int OtoOffset, int Start, int Leading,
        int Pre, int Stable, int End, double Period);

    private static int SourcePosition(double milliseconds)
    {
        var samples = milliseconds * OutputSampleRate / 1000;
        if (!double.IsFinite(samples) || samples < -MaximumSamples || samples > MaximumSamples)
            throw new InvalidDataException("oto.iniのタイミングが長すぎます。");
        return (int)Math.Round(samples);
    }

    private InspectedMoraSource InspectMoraSource(Voicebank bank, OtoEntry oto, CancellationToken ct, bool verifyContent = false)
    {
        // Recheck containment and links at use time: an installed bank may have changed.
        if (verifyContent && (File.GetAttributes(bank.Directory) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("リンクされた音源フォルダーは使えません。");
        var path = Voicebanks.ContainedPath(bank.Directory, Path.GetRelativePath(bank.Directory, oto.WavePath));
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクされた原音は使えません。");
        var info = new FileInfo(path);
        if (info.Length is < 44 or > 32 * 1024 * 1024) throw new InvalidDataException("原音WAVのサイズが不正です。");
        byte[]? bytes = verifyContent ? ReadDirectSourceBytes(path, ct) : null;
        var hash = bytes is null ? null : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
        ct.ThrowIfCancellationRequested();
        if (!cache.TryGetValue(path, out var cached) || cached.Modified != info.LastWriteTimeUtc || cached.Length != info.Length ||
            verifyContent && cached.Hash != hash)
        {
            var samples = DecodeWave(bytes ?? File.ReadAllBytes(path), ct);
            if (cache.Count >= 64 || cacheSamples + samples.Length > OutputSampleRate * 90)
            { cache.Clear(); cacheSamples = 0; }
            else if (cache.Remove(path, out var old)) cacheSamples -= old.Samples.Length;
            cache[path] = cached = (info.LastWriteTimeUtc, info.Length, samples, hash, null, null);
            cacheSamples += samples.Length;
        }
        var source = cached.Samples;
        // File containment, links, stamps and optional direct-source content hash
        // have already been checked. Keep only one immutable OTO analysis per
        // decoded source; replacement/clear releases it with that source.
        ct.ThrowIfCancellationRequested();
        if (!DisableSourceInspectionCacheForComparison && cached.Oto == oto && cached.Inspection is { } inspection &&
            ReferenceEquals(inspection.Samples, source))
        {
            if (EnableSourceInspectionDiagnostics) SourceInspectionCacheHitCount++;
            return inspection;
        }
        if (EnableSourceInspectionDiagnostics) SourceInspectionAnalysisCount++;
        int Position(double milliseconds) => SourcePosition(milliseconds);
        if (!double.IsFinite(oto.Offset) || !double.IsFinite(oto.Cutoff) || !double.IsFinite(oto.Preutterance) ||
            !double.IsFinite(oto.Consonant) || !double.IsFinite(oto.Overlap) || oto.Offset < 0 || oto.Consonant < 0 || oto.Preutterance < 0)
            throw new InvalidDataException("oto.iniのタイミングが不正です。");
        var start = Position(oto.Offset);
        var otoOffset = start;
        // Positive cutoff is measured from the file end; negative cutoff from offset.
        var end = oto.Cutoff < 0 ? start - Position(oto.Cutoff) : source.Length - Position(oto.Cutoff);
        if (start < 0 || end > source.Length || end - start < Position(25)) throw new InvalidDataException("oto.iniの切り出し位置が原音外です。");
        var pre = Position(oto.Preutterance);
        var consonant = Math.Max(pre, Position(oto.Consonant));
        if (pre > end - start - Position(15) || pre > Position(700)) throw new InvalidDataException("oto.iniの先行発声が原音外です。");
        if (consonant > end - start - Position(15)) throw new InvalidDataException("原音に母音部分がありません。");
        var peak = 0f;
        for (var i = start; i < end; i++) peak = Math.Max(peak, Math.Abs(source[i]));
        if (peak < 0.0001f) throw new InvalidDataException("無音の原音は使えません。");
        // Remove only near-silent margins, never an interior fixed-region sample.
        // The fixed range can contain a long consonant-to-vowel transition: it
        // must not be replaced by a jump from preutterance to consonant.
        var threshold = Math.Max(0.00008f, peak * 0.001f);
        var leading = 0;
        while (leading < Math.Min(pre, consonant) && Math.Abs(source[start + leading]) < threshold) leading++;
        leading = Math.Max(0, leading - Position(2));
        start += leading; consonant -= leading; pre -= leading;
        while (end > start + consonant + Position(20) && Math.Abs(source[end - 1]) < threshold) end--;
        pre = Math.Max(pre, Math.Min(Position(8), end - start - Position(15)));
        consonant = Math.Max(consonant, pre);
        var available = Math.Min(end - start - consonant, Position(160));
        var period = UtauPitch.FindPeriod(source.AsSpan(start + consonant, available), ct);
        var stable = UtauPitch.StableStart(source.AsSpan(start, end - start), pre, consonant, period, ct);
        var inspected = new InspectedMoraSource(path, source, otoOffset, start, leading, pre, stable, end, period);
        ct.ThrowIfCancellationRequested();
        if (!DisableSourceInspectionCacheForComparison)
            cache[path] = (cached.Modified, cached.Length, source, cached.Hash, oto, inspected);
        return inspected;
    }

    private static byte[] ReadDirectSourceBytes(string path, CancellationToken ct)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is < 44 or > 32 * 1024 * 1024) throw new InvalidDataException("原音WAVのサイズが不正です。");
        var bytes = new byte[(int)stream.Length];
        for (var at = 0; at < bytes.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var read = stream.Read(bytes, at, Math.Min(bytes.Length - at, 65536));
            if (read == 0) throw new InvalidDataException("原音WAVが途中で切れています。");
            at += read;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("読み込み中に原音WAVの長さが変わりました。");
        return bytes;
    }

    private static string? Vowel(string alias)
    {
        var c = alias[^1];
        if ("あかがさざただなはばぱまやらわぁゃゎ".Contains(c)) return "あ";
        if ("いきぎしじちぢにひびぴみりぃ".Contains(c)) return "い";
        if ("うくぐすずつづぬふぶぷむゆるゔぅゅ".Contains(c)) return "う";
        if ("えけげせぜてでねへべぺめれぇ".Contains(c)) return "え";
        if ("おこごそぞとどのほぼぽもよろをぉょ".Contains(c)) return "お";
        return null;
    }

    private static float Fade(int position, int length) =>
        (float)(0.5 - 0.5 * Math.Cos(Math.PI * position / Math.Max(1, length - 1)));

    // Comparison-only scalar observer. A gap means source samples skipped
    // between consecutive complete grains, not their overlapping output span.
    private sealed class UnitDiagnosticCapture(int unitIndex)
    {
        private int otoOffset, start, pre, stable, end, lead, duration;
        private readonly List<UtauDiagnosticGrain> grains = [];
        private readonly List<UtauDiagnosticWindow> windows = [];
        private bool truncated;
        private UtauAcousticTransitionProtectionDiagnostic? acousticTransition;
        private UtauAcousticBoundaryDiagnostic? acousticBoundary;
        private bool acousticApplied;
        private UtauUnitAcousticRuleDiagnostic? unitRules;
        private int? mappedPre;
        private UtauUnitSourceRecoveryDiagnostic? sourceRecovery;
        private UtauContextFrictionDiagnostic? contextFriction;
        internal void SetSource(int offset, int trimmed, int onset, int stableStart, int usedEnd, int leadSamples, int durationSamples)
        { otoOffset = offset; start = trimmed; pre = onset; stable = stableStart; end = usedEnd; lead = leadSamples; duration = durationSamples; }
        internal void SetAcousticTransition(UtauAcousticTransitionProtectionDiagnostic decision, UtauAcousticBoundaryDiagnostic boundary, bool applied)
        { acousticTransition = decision; acousticBoundary = boundary; acousticApplied = applied; }
        internal void SetUnitAcousticRules(UtauUnitAcousticRuleDiagnostic decision, int? mappedOnset)
        { unitRules = decision; mappedPre = mappedOnset; }
        internal void SetSourceRecovery(UtauUnitSourceRecoveryDiagnostic recovery) => sourceRecovery = recovery;
        internal void SetContextFriction(UtauContextFrictionDiagnostic diagnostic) => contextFriction = diagnostic;
        internal void Record(int source, int nominal, int outputAt, int overlap, int window)
        {
            if (grains.Count == 256) { truncated = true; return; }
            grains.Add(new(source, nominal, outputAt, overlap, window));
        }
        internal void ObserveAppend(List<float> destination, float[] incoming, int requestedOverlap, int fadeOverlap, CancellationToken ct)
        {
            var overlap = Math.Min(Math.Min(destination.Count, incoming.Length), requestedOverlap);
            var joinedStart = destination.Count - overlap;
            var fade = Math.Min(overlap, Math.Max(1, fadeOverlap));
            for (var at = 0; at < overlap; at += OutputSampleRate / 100)
            {
                ct.ThrowIfCancellationRequested();
                if (windows.Count == 128) { truncated = true; return; }
                var count = Math.Min(OutputSampleRate / 100, overlap - at);
                double currentEnergy = 0, previousEnergy = 0, joinedEnergy = 0, cross = 0;
                for (var j = at; j < at + count; j++)
                {
                    var previous = j < fade ? destination[joinedStart + j] * (1 - Fade(j, fade)) : 0;
                    var current = incoming[j]; var joined = previous + current;
                    currentEnergy += (double)current * current; previousEnergy += (double)previous * previous;
                    joinedEnergy += (double)joined * joined; cross += (double)previous * current;
                }
                windows.Add(new(at, count, Math.Sqrt(currentEnergy / count), Math.Sqrt(previousEnergy / count), Math.Sqrt(joinedEnergy / count), cross / count));
            }
        }
        internal UtauUnitDiagnostic Snapshot() => new(unitIndex, OutputSampleRate, otoOffset, start, start + pre, start + stable,
            start + end, lead, duration, Array.AsReadOnly(grains.ToArray()), Array.AsReadOnly(windows.ToArray()), truncated)
        { AcousticTransitionApplied = acousticApplied && unitRules?.Available != true, AcousticTransitionProtection = acousticTransition,
            AcousticBoundary = acousticBoundary, UnitAcousticRules = unitRules, MappedVowelOnset = mappedPre, SourceRecovery = sourceRecovery,
            ContextFriction = contextFriction };
    }

    private sealed class GrainStatistics(UnitDiagnosticCapture? diagnostic = null)
    {
        private int previous;
        public int Count { get; private set; }
        public int MaximumGap { get; private set; }
        public int MaximumDeviation { get; private set; }
        public void Record(int source, int nominal, int window, int outputAt = 0, int overlap = 0)
        {
            diagnostic?.Record(source, nominal, outputAt, overlap, window);
            if (Count != 0) MaximumGap = Math.Max(MaximumGap, source - previous - window);
            MaximumDeviation = Math.Max(MaximumDeviation, Math.Abs(source - nominal));
            previous = source; Count++;
        }
    }

    private static (float[]? Audio, UtauTransitionProtectionDiagnostic Diagnostic) RenderTransitionProtection(
        ReadOnlySpan<float> source, int pre, int stable, int leadLength, int transitionLength, int duration,
        double period, int sourceOffset, CancellationToken ct)
    {
        const int protectedSamples = OutputSampleRate * 30 / 1000;
        const int checkSamples = OutputSampleRate / 100;
        const string map = "Only [OTO pre-30ms, OTO pre] has nominal slope 1; preceding source is compressed into remaining lead; post-pre map unchanged";
        const string limitation = "OTO pre is not a verified acoustic boundary. This protects only the preceding 30ms nominally, not the whole C/V transition. Grain windows can cross boundaries and correlation search can move contributions; actual preservation is not guaranteed.";
        double? startRatio = null, endRatio = null; var postVoiced = false;
        var boundary = ObserveAcousticBoundary(source, pre, leadLength, duration, period, sourceOffset, ct);
        UtauTransitionProtectionDiagnostic Describe(bool available, string? reason, IReadOnlyList<UtauDiagnosticGrain>? grains = null, bool truncated = false)
            => new(available, reason, protectedSamples, sourceOffset + pre - protectedSamples, sourceOffset + pre,
                leadLength - protectedSamples, leadLength, startRatio, endRatio, postVoiced,
                grains ?? Array.Empty<UtauDiagnosticGrain>(), truncated, map, limitation) { AcousticBoundary = boundary };
        ct.ThrowIfCancellationRequested();
        if (pre <= protectedSamples || leadLength <= protectedSamples || pre <= leadLength)
            return (null, Describe(false, "Leading compression has insufficient budget for the fixed 30ms protected interval."));
        if (pre + protectedSamples > source.Length || !double.IsFinite(period) || period <= 0)
            return (null, Describe(false, "Insufficient post-onset source or no reference period for acoustic confirmation."));
        startRatio = DifferenceRatio(source.Slice(pre - protectedSamples, checkSamples));
        endRatio = DifferenceRatio(source.Slice(pre - checkSamples, checkSamples));
        postVoiced = UtauPitch.IsVoiced(source.Slice(pre, protectedSamples), period);
        // The 0.08 threshold is the existing IsVoiced difference/energy gate.
        // These tests bracket a change within the pre-onset tail; they neither
        // label a phoneme boundary nor justify source-specific threshold tuning.
        if (startRatio is null || endRatio is null || startRatio <= 0.08 || endRatio >= 0.08 || !postVoiced)
            return (null, Describe(false, "The fixed pre-onset interval did not confirm a high-difference to low-difference change followed by voiced audio."));
        var capture = new UnitDiagnosticCapture(-1);
        var statistics = new GrainStatistics(capture);
        var candidate = ShapeMora(source, pre, stable, leadLength, transitionLength, duration, ct, out _, statistics, protectedSamples);
        var observation = capture.Snapshot();
        return (candidate, Describe(true, null, observation.Grains, observation.Truncated));
    }

    // Detection only. One closed 21-window scan; no retries, threshold fitting,
    // F0 changes or time-map construction follow the outcome of this observer.
    private static UtauAcousticBoundaryDiagnostic ObserveAcousticBoundary(ReadOnlySpan<float> source, int pre,
        int lead, int duration, double period, int sourceOffset, CancellationToken ct)
    {
        const int window = OutputSampleRate * 20 / 1000;
        const int radius = OutputSampleRate * 60 / 1000;
        const int requiredRun = 5;
        const string scope = "Window starts span OTO pre-60ms through pre+40ms on a rounded 5ms grid; 20ms windows stay inside pre +/-60ms. First five consecutive existing IsVoiced windows define only a voiced-run start candidate, not a true phoneme boundary. Budget is a necessary length check for a hypothetical candidate +/-20ms interval, not an implemented or proven preservation map.";
        var observations = new List<UtauAcousticBoundaryWindow>(21);
        UtauAcousticBoundaryDiagnostic Missing(string reason) => new(false, reason, window, requiredRun, sourceOffset + pre,
            null, null, Array.AsReadOnly(observations.ToArray()), null, null, null, null, lead, duration, null, null, scope);
        ct.ThrowIfCancellationRequested();
        if (!double.IsFinite(period) || period <= 0 || window < period * 3)
            return Missing("Reference period unavailable or fewer than three periods fit the fixed 20ms window.");
        if (pre < radius || source.Length - pre < radius)
            return Missing("The complete fixed OTO pre +/-60ms source interval is unavailable.");
        var run = 0; var runStart = 0; int? candidate = null;
        // Round absolute grid offsets separately: 5ms = 220.5 samples. Never
        // accumulate a truncated 220-sample hop or exceed the fixed 21 windows.
        for (var index = 0; index <= 20; index++)
        {
            ct.ThrowIfCancellationRequested();
            var at = pre + SourcePosition(-60 + index * 5);
            var frame = source.Slice(at, window);
            var voiced = UtauPitch.IsVoiced(frame, period);
            observations.Add(new(sourceOffset + at, sourceOffset + at + window, DifferenceRatio(frame), voiced));
            if (voiced)
            {
                if (run++ == 0) runStart = at;
                if (run >= requiredRun && candidate is null) candidate = runStart;
            }
            else run = 0;
        }
        if (candidate is not int found) return Missing("No five consecutive voiced windows in the fixed scan.");
        var protectionStart = found - window;
        var protectionEnd = found + window;
        var protectedLength = protectionEnd - protectionStart;
        var before = Math.Clamp(pre - protectionStart, 0, protectedLength);
        var after = protectedLength - before;
        var inSource = protectionStart >= 0 && protectionEnd <= source.Length;
        var fits = inSource && lead >= before && duration >= after &&
            (protectionStart >= pre || protectionStart == 0 || lead > before) &&
            (protectionEnd <= pre || protectionEnd == source.Length || duration > after);
        return new(true, null, window, requiredRun, sourceOffset + pre, sourceOffset + found, found - pre,
            Array.AsReadOnly(observations.ToArray()), sourceOffset + protectionStart, sourceOffset + protectionEnd,
            before, after, lead, duration, fits, fits ? null : "Hypothetical protected interval is outside source or leaves insufficient output budget for remaining source.", scope);
    }

    private static double? DifferenceRatio(ReadOnlySpan<float> samples)
    {
        double energy = 0, difference = 0;
        for (var i = 1; i < samples.Length; i++)
        {
            energy += (double)samples[i] * samples[i];
            var delta = samples[i] - samples[i - 1]; difference += (double)delta * delta;
        }
        return energy > 1e-6 ? difference / energy : null;
    }

    private readonly record struct AcousticTimeMap(int SourceStart, int SourceEnd, int OutputStart, int OutputEnd);

    internal static (float[]? Audio, UtauAcousticTransitionProtectionDiagnostic Diagnostic) RenderAcousticTransitionProtection(
        ReadOnlySpan<float> source, int pre, int stable, int lead, int transition, int duration, int sourceOffset,
        UtauAcousticBoundaryDiagnostic? boundary, CancellationToken ct)
    {
        var selection = SelectAcousticTransitionMap(source.Length, pre, stable, lead, transition, duration, sourceOffset, boundary, ct);
        if (selection.Map is not { } map) return (null, selection.Diagnostic);
        var capture = new UnitDiagnosticCapture(-1); var observer = new GrainStatistics(capture);
        var audio = ShapeMora(source, pre, stable, lead, transition, duration, ct, out _, observer, acousticMap: map);
        var grains = capture.Snapshot();
        return (audio, selection.Diagnostic with { Grains = grains.Grains, GrainsTruncated = grains.Truncated });
    }

    private static (AcousticTimeMap? Map, UtauAcousticTransitionProtectionDiagnostic Diagnostic) SelectAcousticTransitionMap(
        int sourceLength, int pre, int stable, int lead, int transition, int duration, int sourceOffset,
        UtauAcousticBoundaryDiagnostic? boundary, CancellationToken ct)
    {
        const string mapDescription = "Pre-anchor fixed: compress source before the detected protected interval into lead minus required pre samples; nominal slope 1 across the interval including pre; compress remaining source into remaining duration.";
        const string limitation = "Detected voiced-run start is not a true phoneme boundary. Grain windows and correlation search can cross/move beyond the nominal protected interval. Actual waveform preservation is not guaranteed. Source landmark coordinates are absolute WAV samples; grain source starts are trimmed-source relative and output starts are rendered-unit relative.";
        int? sourceStart = boundary?.HypotheticalProtectedStart, sourceEnd = boundary?.HypotheticalProtectedEnd;
        int? outputStart = null, outputEnd = null;
        UtauAcousticTransitionProtectionDiagnostic Describe(bool available, string? reason,
            IReadOnlyList<UtauDiagnosticGrain>? grains = null, bool truncated = false)
            => new(available, reason, sourceStart, sourceEnd, outputStart, outputEnd, sourceOffset + pre, lead,
                duration, grains ?? Array.Empty<UtauDiagnosticGrain>(), truncated, mapDescription, limitation);
        ct.ThrowIfCancellationRequested();
        if (boundary is not { Found: true, BudgetFits: true } || sourceStart is null || sourceEnd is null)
            return (null, Describe(false, "No detected voiced-run interval with sufficient fixed-anchor budget."));
        var from = sourceStart.Value - sourceOffset; var to = sourceEnd.Value - sourceOffset;
        if (from <= 0 || to >= sourceLength || from >= pre || to <= pre)
            return (null, Describe(false, "Detected protected interval must lie strictly within source and cross OTO pre."));
        if (stable != pre || transition != 0)
            return (null, Describe(false, "Existing additional pre-to-stable transition prevents the single protected map."));
        outputStart = lead - (pre - from); outputEnd = lead + (to - pre);
        var length = checked(lead + duration);
        if (outputStart <= 0 || outputEnd >= length)
            return (null, Describe(false, "Protected interval leaves no positive budget for remaining source."));
        var map = new AcousticTimeMap(from, to, outputStart.Value, outputEnd.Value);
        return (map, Describe(true, null));
    }

    // One continuous waveform-similarity overlap-add for the entire mora. The
    // piecewise time map anchors grain CENTERS to preutterance/stable-vowel
    // landmarks. Separate stretches would leave unrelated phases at their seams.
    private static float[] ShapeMora(ReadOnlySpan<float> source, int pre, int stable, int leadLength,
        int transitionLength, int duration, CancellationToken ct, out int stableOutput, GrainStatistics? observer = null,
        int protectedPreSamples = 0, AcousticTimeMap? acousticMap = null, UtauUnitRuleMap? unitMap = null)
    {
        var length = leadLength + duration;
        var result = new float[length];
        var window = Math.Min(OutputSampleRate / 50, source.Length);
        var hop = window - window / 2;
        if (window < 2 || length < window) throw new InvalidDataException("原音の母音部分が短すぎます。");
        var sourceEnd = source.Length;
        double SourceAt(double center)
        {
            if (unitMap is not null) return unitMap.SourceAt(center);
            if (acousticMap is { } map)
            {
                if (center < map.OutputStart) return center * map.SourceStart / map.OutputStart;
                if (center <= map.OutputEnd) return map.SourceStart + center - map.OutputStart;
                return map.SourceEnd + (center - map.OutputEnd) * (sourceEnd - map.SourceEnd) / (length - map.OutputEnd);
            }
            if (center < leadLength)
            {
                if (protectedPreSamples > 0)
                    return center >= leadLength - protectedPreSamples ? pre - (leadLength - center)
                        : center * (pre - protectedPreSamples) / (leadLength - protectedPreSamples);
                return center * pre / leadLength;
            }
            center -= leadLength;
            if (transitionLength > 0 && center < transitionLength)
                return pre + center * (stable - pre) / transitionLength;
            return stable + (center - transitionLength) * (sourceEnd - stable) / (duration - transitionLength);
        }
        source[..window].CopyTo(result);
        observer?.Record(0, 0, window);
        stableOutput = Math.Min(window, stable);
        var lastStart = source.Length - window;
        var lastAt = length - window;
        var search = OutputSampleRate * 6 / 1000;
        var previousAt = 0;
        for (var at = Math.Min(hop, lastAt); lastAt > 0; at = Math.Min(at + hop, lastAt))
        {
            ct.ThrowIfCancellationRequested();
            var overlap = previousAt + window - at;
            var expected = (int)Math.Round(SourceAt(at + window / 2.0) - window / 2.0);
            expected = Math.Clamp(expected, 0, lastStart);
            var from = Math.Max(0, expected - search);
            var to = Math.Min(lastStart, expected + search);
            if (unitMap is not null && unitMap.TryProtectedSource(at, window, source.Length, out var protectedSource))
                from = to = protectedSource;
            var best = expected;
            var bestScore = double.NegativeInfinity;
            // A bounded search at two-sample spacing is sufficient for waveform
            // matching; all copied grains still contain the original samples.
            // The final whole grain also finds a matching phase. Forcing the
            // source endpoint here can cancel the previous waveform in the
            // overlap. There is no subsequent hard splice requiring that phase.
            // The output overlap stays fixed; preserve its float products and sum order.
            // A single candidate keeps the original accumulation and score.
            var multipleCandidates = to - from >= 2;
            double sharedLeft = 0;
            if (multipleCandidates)
                for (var j = 0; j < overlap; j += 4)
                {
                    var a = result[at + j];
                    sharedLeft += a * a;
                }
            for (var candidate = from; candidate <= to; candidate += 2)
            {
                double dot = 0, left = sharedLeft, right = 0;
                if (multipleCandidates)
                {
                    for (var j = 0; j < overlap; j += 4)
                    {
                        var a = result[at + j]; var b = source[candidate + j];
                        dot += a * b; right += b * b;
                    }
                }
                else
                {
                    for (var j = 0; j < overlap; j += 4)
                    {
                        var a = result[at + j]; var b = source[candidate + j];
                        dot += a * b; left += a * a; right += b * b;
                    }
                }
                var score = dot / Math.Sqrt(Math.Max(1e-20, left * right));
                // Prefer the nominal position when several periodic matches tie.
                score -= Math.Abs(candidate - expected) * 0.000001;
                if (score > bestScore) { bestScore = score; best = candidate; }
            }
            observer?.Record(best, expected, window, at, overlap);
            // Track actual source contributions, rather than delaying all pitch
            // shaping by an arbitrary whole window after the nominal boundary.
            if (best < stable) stableOutput = Math.Max(stableOutput, at + Math.Min(window, stable - best));
            for (var j = 0; j < overlap; j++)
            {
                var weight = Fade(j, overlap);
                result[at + j] = result[at + j] * (1 - weight) + source[best + j] * weight;
            }
            source.Slice(best + overlap, window - overlap).CopyTo(result.AsSpan(at + overlap));
            if (at == lastAt) break;
            previousAt = at;
        }
        return result;
    }

    private static int Append(List<float> destination, float[] samples, int requestedOverlap, int fadeOverlap)
    {
        var overlap = Math.Min(Math.Min(destination.Count, samples.Length), requestedOverlap);
        var start = destination.Count - overlap;
        if ((long)destination.Count + samples.Length - overlap > MaximumSamples)
            throw new InvalidDataException("合成音声が長すぎます。");
        for (var i = 0; i < overlap; i++)
        {
            // The incoming onset already has a 2ms ramp. Do not attenuate its
            // consonant across the bank's potentially long preutterance overlap.
            var fade = Math.Min(overlap, Math.Max(1, fadeOverlap));
            var previous = i < fade ? destination[destination.Count - overlap + i] * (1 - Fade(i, fade)) : 0;
            destination[destination.Count - overlap + i] = previous + samples[i];
        }
        for (var i = overlap; i < samples.Length; i++) destination.Add(samples[i]);
        return start;
    }

    private static void AddSilence(List<float> result, int milliseconds)
        => AddSilenceSamples(result, milliseconds * OutputSampleRate / 1000);

    private static void AddSilenceSamples(List<float> result, int length)
    {
        var fade = Math.Min(result.Count, OutputSampleRate / 200);
        for (var i = 0; i < fade; i++) result[^(i + 1)] *= Fade(i, fade);
        if (result.Count + length > MaximumSamples) throw new InvalidDataException("合成音声が長すぎます。");
        for (var i = 0; i < length; i++) result.Add(0);
    }

    internal static float[] DecodeWave(ReadOnlySpan<byte> bytes, CancellationToken ct)
    {
        if (bytes.Length < 44 || !bytes.Slice(0, 4).SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WAVE"u8)) throw new InvalidDataException("RIFF/WAVE形式ではありません。");
        var riffEnd = (long)BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4)) + 8;
        if (riffEnd > bytes.Length || riffEnd < 12) throw new InvalidDataException("WAVが途中で切れています。");
        int format = 0, channels = 0, rate = 0, bits = 0, alignment = 0, dataStart = -1, dataLength = 0;
        for (long position = 12; position + 8 <= riffEnd;)
        {
            ct.ThrowIfCancellationRequested();
            var p = (int)position;
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(p + 4));
            if (position + 8 + size > riffEnd) throw new InvalidDataException("WAVチャンクが原音外です。");
            if (bytes.Slice(p, 4).SequenceEqual("fmt "u8))
            {
                if (size < 16) throw new InvalidDataException();
                format = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(p + 8));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(p + 10));
                rate = BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(p + 12));
                alignment = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(p + 20));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(p + 22));
            }
            else if (bytes.Slice(p, 4).SequenceEqual("data"u8))
            {
                if (dataStart >= 0) throw new InvalidDataException("複数のdataチャンクには対応していません。");
                dataStart = p + 8; dataLength = checked((int)size);
            }
            position += 8L + size + (size & 1);
        }
        if (channels is < 1 or > 2 || rate is < 8000 or > 192000 ||
            !(format == 1 && bits is 8 or 16 or 24 or 32 || format == 3 && bits == 32) ||
            alignment != channels * (bits / 8) || dataStart < 0 || dataLength == 0 || dataLength % alignment != 0)
            throw new InvalidDataException("対応していないWAV形式です（PCM/float、1–2ch）。");
        var frames = dataLength / alignment;
        if (frames > rate * 30L) throw new InvalidDataException("原音が30秒を超えています。");
        var count = checked((int)((long)frames * OutputSampleRate / rate));
        var output = new float[count];
        double sum = 0;
        float At(ReadOnlySpan<byte> samples, int frame)
        {
            float sum = 0;
            for (var channel = 0; channel < channels; channel++)
            {
                var at = dataStart + Math.Min(frame, frames - 1) * alignment + channel * bits / 8;
                var value = format == 3 ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(at))) : bits switch
                {
                    8 => (samples[at] - 128) / 128f,
                    16 => BinaryPrimitives.ReadInt16LittleEndian(samples.Slice(at)) / 32768f,
                    24 => ((samples[at] | samples[at + 1] << 8 | samples[at + 2] << 16) << 8 >> 8) / 8388608f,
                    _ => BinaryPrimitives.ReadInt32LittleEndian(samples.Slice(at)) / 2147483648f
                };
                if (!float.IsFinite(value)) throw new InvalidDataException("WAVに不正な数値があります。");
                sum += Math.Clamp(value, -1, 1);
            }
            return sum / channels;
        }
        var cachedFrame = -1; var cachedSample = 0f;
        float AtCached(ReadOnlySpan<byte> samples, int frame)
        {
            frame = Math.Min(frame, frames - 1);
            if (cachedFrame == frame) return cachedSample;
            var decoded = At(samples, frame);
            cachedFrame = frame; cachedSample = decoded;
            return decoded;
        }
        // The bundled mono PCM16 recordings already use the output rate. Decode
        // each sample once; no interpolation or adjacent sample is needed.
        if (format == 1 && bits == 16 && channels == 1 && rate == OutputSampleRate)
        {
            for (var i = 0; i < output.Length; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                output[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(dataStart + i * 2)) / 32768f;
                sum += output[i];
            }
        }
        else if (rate >= OutputSampleRate && rate < OutputSampleRate * 2)
        {
            // Adjacent outputs often reuse the preceding right-hand frame.
            // Retain only that verified decoded value for this WAV call.
            for (var i = 0; i < output.Length; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var source = (double)i * rate / OutputSampleRate;
                var index = (int)source; var fraction = (float)(source - index);
                output[i] = AtCached(bytes, index) * (1 - fraction) + AtCached(bytes, index + 1) * fraction;
                sum += output[i];
            }
        }
        else
        {
            // Rate conversion preserves sound duration/pitch.
            for (var i = 0; i < output.Length; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                var source = (double)i * rate / OutputSampleRate;
                var index = (int)source; var fraction = (float)(source - index);
                output[i] = At(bytes, index) * (1 - fraction) + At(bytes, index + 1) * fraction;
                sum += output[i];
            }
        }
        // Remove recording DC once per cached source, rather than independently
        // shifting each mora and introducing a new offset at its boundaries.
        var mean = (float)(sum / output.Length);
        for (var i = 0; i < output.Length; i++)
        {
            if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
            output[i] -= mean;
        }
        return output;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                // Do not run arbitrary cancellation/disposal callbacks under
                // this lock or capture a UI context during application exit.
                disposal = Task.Run(DisposeCoreAsync);
            }
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { lifetime.Cancel(); }
        finally
        {
            await synthesis.WaitAsync().ConfigureAwait(false);
            try
            {
                cache.Clear(); cacheSamples = 0; pitchBasis = null;
                try { if (postprocessor is not null) await postprocessor.DisposeAsync().ConfigureAwait(false); }
                finally
                {
                    try { if (portableDirect is not null) await portableDirect.DisposeAsync().ConfigureAwait(false); }
                    finally { await output.DisposeAsync().ConfigureAwait(false); }
                }
            }
            finally { synthesis.Release(); lifetime.Dispose(); }
        }
    }
}
