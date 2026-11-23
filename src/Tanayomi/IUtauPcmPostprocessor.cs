namespace Tanayomi;

public enum UtauSynthesisMode { Context, Simple, Flat, Timing, FastContext, Psola, PhaseVocoder, DirectPsola = 8, OpenVoice = 10 }
public sealed record UtauModeChoice(UtauSynthesisMode Mode, string Label)
{
    public override string ToString() => Label;
}
public interface IUtauModeProvider
{
    IReadOnlyList<UtauModeChoice> Modes { get; }
    IUtauPcmPostprocessor? Resolve(UtauSynthesisMode mode);
    IReadOnlyList<Exception> InitializationErrors => Array.Empty<Exception>();
}

/// <summary>Optional platform-provided processing of the existing unpitched UTAU
/// PCM. UtauSpeech owns/disposes this processor. Implementations must observe
/// cancellation, return the same sample rate/count, and propagate failures.
/// They must not play audio or persist input/utterances by default.</summary>
public interface IUtauPcmPostprocessor : IAsyncDisposable
{
    Task PrepareAsync(Voicebank bank, CancellationToken cancellationToken) => Task.CompletedTask;
    Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken cancellationToken);
}

/// <summary>Optional complete rendering from the bank. Unlike postprocessing,
/// this path replaces source generation. It must not play or persist utterances.
/// Failure propagates to the ordinary speech fallback; cancellation never falls back.</summary>
public interface IUtauDirectRenderer
{
    Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken cancellationToken);
    Task<PcmAudio> RenderAsync(string text, Voicebank bank, bool drawnOutMode, CancellationToken cancellationToken)
        => RenderAsync(text, bank, cancellationToken);
}

// A direct renderer can retain usable ordinary timing when a prosody model
// cannot fit the source. The normal view receives the same bounded diagnostics
// as the existing postprocessing path; cancellation is never a fallback.
public interface IUtauDirectDiagnostics
{
    event Action<Exception>? PlanningFallback;
    event Action<UtauTimingPlanException>? TimingFallback;
}

public sealed record UtauReadingUnit(string? Alias, int Extensions, char Pause);

/// <summary>Optional per-utterance planning. The renderer retains ownership of
/// source timing limits; a rejected plan falls back to ordinary synthesis.</summary>
public interface IUtauProsodyPostprocessor : IUtauPcmPostprocessor
{
    // A read-only normalized unit snapshot; long vowels belong to one unit.
    // Null declines planning without a failure diagnostic. Exceptions request
    // ordinary B + flat processing; caller cancellation never requests fallback.
    Task<IUtauUtterancePlan?> CreatePlanAsync(string text, IReadOnlyList<UtauReadingUnit> reading,
        CancellationToken cancellationToken);
}

public interface IUtauUtterancePlan
{
    // Bounded to 640 entries. Signature and renderer source limits remain
    // authoritative. No per-utterance native resource ownership is transferred.
    IReadOnlyList<UtauTimingAllocation> Timing { get; }
    // Opt in only when ProcessAsync can time-warp its contour to the actual
    // ordinary-render anchors after a source timing guard rejects Timing.
    bool CanProcessOrdinaryTiming => false;
    Task<PcmAudio> ProcessAsync(UtauReferenceTiming rendered, Voicebank bank, CancellationToken cancellationToken);
}
