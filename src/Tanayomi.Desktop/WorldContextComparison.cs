using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tanayomi.Desktop;

public static partial class WorldComparison
{
    // Opt-in fixed-phrase diagnostic. Uses the same Context processor and common
    // UtauSpeech fallback policy as the product; no comparison timing or gain.
    internal static int RunContextPronunciationWorker(string[] args)
    {
        try
        {
            ConfigureWorker();
            if (args.Length != 2 || args[0] is not ("--utau-context-pronunciation-worker" or "--utau-context-devoicing-worker")) throw new ArgumentException();
            var workspace = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));
            var development = Path.Combine(workspace, ".local", "build", "app");
            if (!Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory).Equals(development, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Development diagnostic only.");
            var root = Path.GetFullPath(args[1]);
            var parent = Path.Combine(workspace, ".local", "tmp");
            var name = Path.GetFileName(root);
            if (!Path.IsPathFullyQualified(args[1]) || !string.Equals(Path.GetDirectoryName(root), parent, StringComparison.OrdinalIgnoreCase) ||
                name.Length != 38 || !name.StartsWith("tests-", StringComparison.Ordinal) || !name.AsSpan(6).ToString().All(Uri.IsHexDigit)) throw new InvalidDataException();
            RejectLinks(root);
            if (args[0] == "--utau-context-devoicing-worker")
                return WriteDevoicingComparisonAsync(root).GetAwaiter().GetResult();
            var result = RenderPronunciationDiagnosticAsync(root).GetAwaiter().GetResult();
            var production = RenderPronunciationDiagnosticAsync(root, baseline: false).GetAwaiter().GetResult();
            var rules = RenderPronunciationDiagnosticAsync(root, baseline: false, unitRules: true).GetAwaiter().GetResult();
            var source = DiagnosticPcm(result.UnitAudio.SourceUsed, result.UnitAudio.SampleRate);
            var shaped = DiagnosticPcm(result.UnitAudio.ShapedBeforePitch, result.UnitAudio.SampleRate);
            var ruleUnit = DiagnosticPcm(rules.UnitAudio.ShapedBeforePitch, rules.UnitAudio.SampleRate);
            var restoredLeadingSamples = rules.UnitAudio.SourceUsed.Length - production.UnitAudio.SourceUsed.Length;
            if (restoredLeadingSamples is < 0 or > 44100 * 60 / 1000 ||
                !rules.UnitAudio.SourceUsed.AsSpan(restoredLeadingSamples).SequenceEqual(production.UnitAudio.SourceUsed) ||
                rules.Diagnostic.PreutteranceSample != production.Diagnostic.PreutteranceSample ||
                rules.Diagnostic.StableSample != production.Diagnostic.StableSample || rules.Diagnostic.EndSample != production.Diagnostic.EndSample ||
                production.Diagnostic.TrimmedStartSample - rules.Diagnostic.TrimmedStartSample != restoredLeadingSamples ||
                rules.Audio.Samples.Length < production.Audio.Samples.Length || ruleUnit.Samples.Length < shaped.Samples.Length ||
                ruleUnit.Samples.Length != rules.Diagnostic.LeadSamples + rules.Diagnostic.DurationSamples)
                throw new InvalidDataException("Unit rule source or output-duration contract failed.");
            var candidate = result.UnitAudio.AcousticTransitionProtected is { } candidateSamples ? DiagnosticPcm(candidateSamples, result.UnitAudio.SampleRate) : null;
            var metadata = JsonSerializer.SerializeToUtf8Bytes(new { text = "てすとです。", mode = "Context", result.FallbackCount,
                result.TimingFallbackCount, requestedTiming = result.Timing, unitDiagnostic = result.Diagnostic, pcmResponses = candidate is null ? 6 : 7,
                productionUnit = production.Diagnostic, productionFallbacks = production.FallbackCount, productionTimingFallbacks = production.TimingFallbackCount,
                unitAcousticRevision = 1, ruleUnit = rules.Diagnostic, ruleFallbacks = rules.FallbackCount, ruleTimingFallbacks = rules.TimingFallbackCount,
                restoredLeadingSamples, sourceSuffixUnchanged = true,
                requestedUnitSamples = shaped.Samples.Length, effectiveUnitSamples = ruleUnit.Samples.Length,
                previousPhraseSamples = production.Audio.Samples.Length, effectivePhraseSamples = rules.Audio.Samples.Length,
                transitionProtection = result.UnitAudio.TransitionProtection,
                acousticTransitionProtection = result.UnitAudio.AcousticTransitionProtection,
                unitScope = "Decoded source (including normal decoder DC removal) passed into ShapeMora versus its output before pitch/ramp/Append/WORLD; first su only",
                gain = "same PCM conversion, no gain normalization; source and shaped durations differ" });
            if (metadata.Length > 65536) throw new InvalidDataException();
            using var output = Console.OpenStandardOutput();
            Span<byte> length = stackalloc byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, metadata.Length);
            output.Write(length); output.Write(metadata);
            NeuralPcmProtocol.WriteResponse(output, 0, result.Audio);
            NeuralPcmProtocol.WriteResponse(output, 0, source);
            NeuralPcmProtocol.WriteResponse(output, 0, shaped);
            if (candidate is not null) NeuralPcmProtocol.WriteResponse(output, 0, candidate);
            NeuralPcmProtocol.WriteResponse(output, 0, production.Audio);
            NeuralPcmProtocol.WriteResponse(output, 0, ruleUnit);
            NeuralPcmProtocol.WriteResponse(output, 0, rules.Audio);
            return 0;
        }
        catch (Exception error) { ReportFailure(error); return 1; }
    }

    private static PcmAudio DiagnosticPcm(float[] source, int rate)
    {
        if (rate != 44100 || source.Length is < 1 or > 44100 * 45) throw new InvalidDataException();
        var samples = new short[source.Length];
        for (var i = 0; i < source.Length; i++)
        {
            if (!float.IsFinite(source[i]) || Math.Abs(source[i]) >= 1) throw new InvalidDataException("Diagnostic audio out of range.");
            samples[i] = (short)Math.Round(source[i] * 32767);
        }
        return new(samples, rate);
    }

    private static async Task<int> WriteDevoicingComparisonAsync(string root)
    {
        var bank = (await Voicebanks.LoadInstalledAsync(root).ConfigureAwait(false)).Single(value => value.Name == "hosinohito");
        var cases = new List<object>();
        PcmAudio? primaryBaseline = null, primaryCandidate = null;
        var primaryApplied = false;
        await using var planner = new DesktopOpenJtalkPlanner();
        foreach (var (text, target) in new[] { ("てすとです。", 1), ("すきです。", 0), ("すみです。", 0), ("す。", 0) })
        {
            var model = await planner.PlanAsync(text, CancellationToken.None).ConfigureAwait(false);
            var modelPhoneVoicing = model.Phones.Select(phone => new { phone.Symbol, phone.StartFrame, phone.EndFrame,
                voicingMask = new string(Enumerable.Range(phone.StartFrame, phone.EndFrame - phone.StartFrame)
                    .Select(frame => model.Voiced[frame] == 0 ? '-' : 'v').ToArray()) }).ToArray();
            var baseline = await RenderFrictionDiagnosticAsync(bank, text, target, enabled: false).ConfigureAwait(false);
            var candidate = await RenderFrictionDiagnosticAsync(bank, text, target, enabled: true).ConfigureAwait(false);
            foreach (var pcm in new[] { baseline.Audio, candidate.Audio, baseline.BeforeWorld, candidate.BeforeWorld })
                if (pcm.SampleRate != 44100 || pcm.Samples.Length is < 1 or > 44100 * 45 ||
                    !pcm.Samples.Any(x => x != 0) || pcm.Samples.Any(x => x is short.MinValue or short.MaxValue))
                    throw new InvalidDataException("Context comparison PCM is invalid or clipped.");
            if (baseline.Audio.SampleRate != candidate.Audio.SampleRate || baseline.Audio.Samples.Length != candidate.Audio.Samples.Length ||
                !baseline.Anchors.SequenceEqual(candidate.Anchors))
                throw new InvalidDataException("Context comparison changed placement or duration.");
            var applied = candidate.Diagnostic.ContextFriction?.Applied == true;
            if (applied && candidate.Diagnostic.ContextFriction is { } friction &&
                (friction.NextPcmUnchanged ? friction.NextPcmSha256Before != friction.NextPcmSha256After :
                    friction.RecoveredNextPrefixSamples is < 1 or > 44100 * 60 / 1000 ||
                    friction.NextOriginalStartSample - friction.NextRecoveredStartSample != friction.RecoveredNextPrefixSamples ||
                    !friction.NextAbsoluteLandmarksUnchanged || friction.NextPcmSha256Before == friction.NextPcmSha256After))
                throw new InvalidDataException("Paired stop recovery contract failed.");
            var sameBeforeWorld = baseline.BeforeWorld.Samples.AsSpan().SequenceEqual(candidate.BeforeWorld.Samples);
            var sameOutput = baseline.Audio.Samples.AsSpan().SequenceEqual(candidate.Audio.Samples);
            if ((!applied && (!sameBeforeWorld || !sameOutput)) || (applied && sameBeforeWorld))
                throw new InvalidDataException("Context candidate availability does not match PCM.");
            if ((text is "すみです。" or "す。") && applied)
                throw new InvalidDataException("Non-target control was modified.");
            cases.Add(new { text, target, applied, sameBeforeWorld, sameOutput, baseline.Audio.SampleRate,
                samples = baseline.Audio.Samples.Length, baseline.FallbackCount, baseline.TimingFallbackCount,
                baselinePeak = baseline.Audio.Samples.Max(x => Math.Abs((int)x)), candidatePeak = candidate.Audio.Samples.Max(x => Math.Abs((int)x)),
                candidateFallbacks = candidate.FallbackCount, candidateTimingFallbacks = candidate.TimingFallbackCount,
                context = candidate.Timing[target].PhoneticContext, candidate.Diagnostic.ContextFriction, modelPhoneVoicing,
                baselineAnchors = baseline.Anchors, candidateAnchors = candidate.Anchors,
                baselineSha256 = PcmDigest(baseline.Audio), candidateSha256 = PcmDigest(candidate.Audio),
                baselineBeforeWorldSha256 = PcmDigest(baseline.BeforeWorld), candidateBeforeWorldSha256 = PcmDigest(candidate.BeforeWorld) });
            if (cases.Count == 1)
            {
                primaryBaseline = baseline.Audio; primaryCandidate = candidate.Audio; primaryApplied = applied;
            }
        }
        var metadata = JsonSerializer.SerializeToUtf8Bytes(new { contextFrictionRevision = 3, pcmResponses = 2,
            text = "てすとです。", primaryApplied, cases,
            limitation = "Recorded frication surrogate for frontend s-U-t/k intent. Mixed model VUV is observed, not reproduced. Not a reconstruction of devoiced-u articulation. B uses the product default; A explicitly disables contextual friction.",
            controls = "Same original recordings, placement, sample count and WORLD processing. A confirmed quiet next-stop prefix may be recovered and redrawn as an explicit paired change. No level normalization. Voiced/end controls must be unchanged." });
        if (metadata.Length > 65536 || primaryBaseline is null || primaryCandidate is null) throw new InvalidDataException();
        using var output = Console.OpenStandardOutput();
        var length = new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(length, metadata.Length);
        output.Write(length); output.Write(metadata);
        NeuralPcmProtocol.WriteResponse(output, 0, primaryBaseline);
        NeuralPcmProtocol.WriteResponse(output, 0, primaryCandidate);
        return 0;
    }

    private static string PcmDigest(PcmAudio audio)
    {
        var bytes = new byte[audio.Samples.Length * 2];
        for (var i = 0; i < audio.Samples.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), audio.Samples[i]);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }

    private sealed record FrictionComparisonResult(PcmAudio Audio, PcmAudio BeforeWorld, int FallbackCount, int TimingFallbackCount,
        IReadOnlyList<UtauTimingAllocation> Timing, IReadOnlyList<UtauTimingAnchor> Anchors, UtauUnitDiagnostic Diagnostic);

    private static async Task<FrictionComparisonResult> RenderFrictionDiagnosticAsync(Voicebank bank, string text, int target, bool enabled)
    {
        var processor = new PronunciationContextProcessor();
        await using var speech = enabled
            ? new UtauSpeech(new ContextCheckNoPlayback(), processor) { Voicebank = bank, Mode = UtauSynthesisMode.Context }
            : new UtauSpeech(new ContextCheckNoPlayback(), processor) { Voicebank = bank, Mode = UtauSynthesisMode.Context, DisableContextFrictionForComparison = true };
        speech.EnableUnitDiagnostics(target, captureAudio: false);
        var fallbacks = 0; var timingFallbacks = 0;
        speech.ProsodyFallback += _ => fallbacks++;
        speech.ProsodyTimingFallback += _ => timingFallbacks++;
        var audio = await speech.SynthesizeAsync(text).ConfigureAwait(false);
        if (fallbacks != 0 || timingFallbacks != 0 || processor.Timing is null || processor.BeforeWorld is null || processor.Anchors is null ||
            target >= processor.Timing.Count || speech.UnitDiagnostics is null)
            throw new InvalidDataException("Context comparison fell back or lost diagnostics.");
        return new(audio, processor.BeforeWorld, fallbacks, timingFallbacks, processor.Timing, processor.Anchors, speech.UnitDiagnostics);
    }
    private static async Task<(PcmAudio Audio, PcmAudio BeforeWorld, int FallbackCount, int TimingFallbackCount, IReadOnlyList<UtauTimingAllocation>? Timing, UtauUnitDiagnostic Diagnostic, UtauUnitDiagnosticAudio UnitAudio)>
        RenderPronunciationDiagnosticAsync(string root, bool baseline = true, bool unitRules = false)
    {
        var bank = (await Voicebanks.LoadInstalledAsync(root).ConfigureAwait(false)).Single(value => value.Name == "hosinohito");
        var processor = new PronunciationContextProcessor();
        await using var speech = new UtauSpeech(new ContextCheckNoPlayback(), processor)
        { Voicebank = bank, Mode = UtauSynthesisMode.Context, DisableAcousticTransitionProtectionForComparison = baseline,
            DisableUnitAcousticRulesForComparison = !unitRules, DisableContextFrictionForComparison = true };
        speech.EnableUnitDiagnostics(1, captureAudio: true);
        var fallbacks = 0; var timingFallbacks = 0;
        speech.ProsodyFallback += _ => fallbacks++;
        speech.ProsodyTimingFallback += _ => timingFallbacks++;
        var audio = await speech.SynthesizeAsync("てすとです。").ConfigureAwait(false);
        // A failed context stage must not be mislabeled as the adopted Context
        // voice. Ordinary timing with contextual F0 remains the product fallback.
        if (fallbacks != 0 || processor.Timing is null || processor.BeforeWorld is null) throw new InvalidDataException("Context diagnostic synthesis fell back.");
        return (audio, processor.BeforeWorld, fallbacks, timingFallbacks, processor.Timing,
            speech.UnitDiagnostics ?? throw new InvalidDataException("Missing unit diagnostic."),
            speech.UnitDiagnosticAudio ?? throw new InvalidDataException("Missing unit audio."));
    }

    private sealed class PronunciationContextProcessor : IUtauProsodyPostprocessor
    {
        private readonly DesktopUtauProsody current = new();
        internal IReadOnlyList<UtauTimingAllocation>? Timing { get; private set; }
        internal PcmAudio? BeforeWorld { get; private set; }
        internal IReadOnlyList<UtauTimingAnchor>? Anchors { get; private set; }
        public Task PrepareAsync(Voicebank bank, CancellationToken ct) => current.PrepareAsync(bank, ct);
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => current.ProcessAsync(input, bank, ct);
        public async Task<IUtauUtterancePlan?> CreatePlanAsync(string text, IReadOnlyList<UtauReadingUnit> reading, CancellationToken ct)
        {
            var plan = await current.CreatePlanAsync(text, reading, ct).ConfigureAwait(false);
            Timing = plan?.Timing;
            return plan is null ? null : new CapturePlan(plan, this);
        }
        private sealed class CapturePlan(IUtauUtterancePlan inner, PronunciationContextProcessor owner) : IUtauUtterancePlan
        {
            public IReadOnlyList<UtauTimingAllocation> Timing => inner.Timing;
            public bool CanProcessOrdinaryTiming => inner.CanProcessOrdinaryTiming;
            public Task<PcmAudio> ProcessAsync(UtauReferenceTiming rendered, Voicebank bank, CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                owner.BeforeWorld = new((short[])rendered.Audio.Samples.Clone(), rendered.Audio.SampleRate);
                owner.Anchors = rendered.Anchors.ToArray();
                return inner.ProcessAsync(rendered, bank, ct);
            }
        }
        public ValueTask DisposeAsync() => current.DisposeAsync();
    }

    /// <summary>Explicit product-path check. Uses the installed adapters without
    /// playing or writing synthesized audio; the caller owns the temporary bank root.</summary>
    internal static int RunProductContextCheck(string[] args)
    {
        try
        {
            ConfigureWorker();
            if (args.Length is < 2 or > 3 || args.Length == 3 && args[2] is not ("--modes" or "--neural" or "--waveform") || args[0] != "--utau-context-check-worker" || string.IsNullOrWhiteSpace(args[1]))
                throw new ArgumentException();
            var root = Path.GetFullPath(args[1]);
            var temporary = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp")).TrimEnd(Path.DirectorySeparatorChar);
            if (!root.StartsWith(temporary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                Path.GetRelativePath(temporary, root).Contains(':') || !Directory.Exists(root))
                throw new InvalidDataException();
            RejectLinks(root);
            // This non-default root cannot trigger legacy user-bank migration.
            var bank = Voicebanks.LoadInstalledAsync(root).GetAwaiter().GetResult()
                .Single(value => value.Name == "hosinohito");
            RejectLinks(bank.Directory);
            var cases = CheckProductContextAsync(bank, args.Length == 3,
                args.Length == 3 && args[2] == "--neural", root, args.Length == 3 && args[2] == "--waveform").GetAwaiter().GetResult();
            var timing = args.Length == 3 ? null : InspectTimingAsync(bank).GetAwaiter().GetResult();
            var json = JsonSerializer.Serialize(new { Check = "utau-product-context", ContextRevision = 2, Cases = cases, Timing = timing });
            if (Encoding.UTF8.GetByteCount(json) > 16384) throw new InvalidDataException();
            Console.WriteLine(json); Console.Out.Flush();
            return 0;
        }
        catch (Exception error) { ReportFailure(error); return 1; }
    }

    private sealed record ProductContextCheckCase(int Case, int FallbackCount, int TimingFallbackCount, int Samples,
        int SampleRate, double Seconds, int Peak, double ElapsedMilliseconds, string Mode, int? ChangedSamples = null,
        int PeriodFallbackCount = 0,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        NeuralStageTimings? NeuralStages = null,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        double? PreparationMilliseconds = null);

    // Fixed public regression phrase only; never accepts or records user chat.
    private static async Task<object> InspectTimingAsync(Voicebank bank)
    {
        const string text = "てすとです";
        await using var planner = new DesktopOpenJtalkPlanner();
        var model = await planner.PlanAsync(text, CancellationToken.None).ConfigureAwait(false);
        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank).ConfigureAwait(false);
        var units = UtauProsodyPlan.Map(model, ordinary.Anchors);
        var allocation = UtauProsodyTransfer.Allocate(units);
        UtauReferenceTiming rendered;
        object? guard = null;
        try { rendered = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, allocation).ConfigureAwait(false); }
        catch (UtauTimingPlanException error)
        {
            guard = new { error.UnitIndex, error.RequestedSamples, error.MinimumSamples };
            rendered = ordinary;
        }
        var (candidate, adjusted, constraints) = await RenderAdjustedTimingAsync(text, bank, units, ordinary).ConfigureAwait(false);
        var controls = new List<object>();
        foreach (var control in new[] { "すきです", "みずです", "てすとですね", "テストです" })
        {
            var controlModel = await planner.PlanAsync(control, CancellationToken.None).ConfigureAwait(false);
            var reference = await UtauSpeech.SynthesizeReferenceWithTimingAsync(control, bank).ConfigureAwait(false);
            var controlUnits = UtauProsodyPlan.Map(controlModel, reference.Anchors);
            var comparison = await RenderAdjustedTimingAsync(control, bank, controlUnits, reference).ConfigureAwait(false);
            var consonants = await RenderAdjustedTimingAsync(control, bank, controlUnits, reference, consonantTiming: true).ConfigureAwait(false);
            controls.Add(new { Case = controls.Count, Samples = comparison.Rendered.Audio.Samples.Length,
                Constraints = comparison.Constraints.Count, ConsonantSamples = consonants.Rendered.Audio.Samples.Length,
                ConsonantConstraints = consonants.Constraints.Count,
                Phones = controlModel.Phones.Select(p => new { p.Symbol, Frames = p.EndFrame - p.StartFrame }) });
        }
        return new
        {
            Guard = guard, Constraints = constraints, Controls = controls,
            RenderedTiming = candidate.RenderedTiming,
            Adjusted = adjusted.Select((a, i) => new { a.DurationSamples,
                candidate.Anchors[i].StartSample, candidate.Anchors[i].VowelOnsetSample, candidate.Anchors[i].EndSample }),
            Phones = model.Phones.Select(p => new { p.Symbol, p.StartFrame, p.EndFrame }),
            Units = units.Select((u, i) => new
            {
                u.Alias, u.StartFrame, u.MainStartFrame, u.EndFrame, allocation[i].DurationSamples,
                rendered.Anchors[i].StartSample, rendered.Anchors[i].VowelOnsetSample, rendered.Anchors[i].EndSample
            })
        };
    }

    private static async Task<(UtauReferenceTiming Rendered, UtauTimingAllocation[] Allocation, List<object> Constraints)>
        RenderAdjustedTimingAsync(string text, Voicebank bank, IReadOnlyList<UtauPlannedUnit> units, UtauReferenceTiming ordinary,
            bool consonantTiming = false)
    {
        // Experimental only: preserve predicted vowel time while using the
        // actual next lead. Raise only constraints reported by the renderer.
        var adjusted = UtauProsodyTransfer.Allocate(units).ToArray();
        var leads = units.Select(u => consonantTiming && u.MainStartFrame is int onset && onset > u.StartFrame
            ? (int?)Math.Round((onset - u.StartFrame) * 220.5) : null).ToArray();
        for (var i = 0; i < units.Count; i++)
        {
            if (units[i].MainStartFrame is not int onset) continue;
            var nextLead = i + 1 < units.Count && ordinary.Anchors[i + 1].VowelOnsetSample is int nextOnset
                ? leads[i + 1] ?? nextOnset - ordinary.Anchors[i + 1].StartSample : 0;
            adjusted[i] = adjusted[i] with { DurationSamples = (int)Math.Round((units[i].EndFrame - onset) * 220.5) + nextLead };
        }
        UtauReferenceTiming? candidate = null;
        var constraints = new List<object>();
        for (var attempt = 0; attempt <= units.Count * 2; attempt++)
        {
            try
            {
                candidate = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, adjusted,
                    leadingSamples: consonantTiming ? leads : null).ConfigureAwait(false);
                break;
            }
            catch (UtauTimingPlanException error)
            {
                if (error.UnitIndex < 0 || error.UnitIndex >= adjusted.Length ||
                    error.MinimumSamples <= adjusted[error.UnitIndex].DurationSamples) throw;
                constraints.Add(new { error.UnitIndex, error.RequestedSamples, error.MinimumSamples });
                adjusted[error.UnitIndex] = adjusted[error.UnitIndex] with { DurationSamples = error.MinimumSamples };
            }
        }
        if (candidate is null) throw new InvalidDataException();
        return (candidate, adjusted, constraints);
    }

    private static async Task<ProductContextCheckCase[]> CheckProductContextAsync(Voicebank bank, bool allModes,
        bool neuralOnly = false, string? neuralReferenceRoot = null, bool waveformOnly = false)
    {
        var neuralAvailable = neuralOnly || NeuralOpenVoiceAssets.Configured &&
            Directory.Exists(Path.Combine(AppContext.BaseDirectory, "resources", "openvoice"));
        var modes = new DesktopUtauModes(neuralAvailable: neuralAvailable, neuralReferenceRoot: neuralReferenceRoot);
        await using var speech = new UtauSpeech(new ContextCheckNoPlayback(), modes) { Voicebank = bank };
        var fallbacks = 0;
        var timingFallbacks = 0;
        var periodFallbacks = 0;
        speech.ProsodyFallback += error =>
        {
            if (error is InvalidDataException && error.Message.StartsWith("UTAU-DIRECT-PERIOD:", StringComparison.Ordinal)) periodFallbacks++;
            else fallbacks++;
        };
        speech.ProsodyTimingFallback += _ => timingFallbacks++;
        string[] texts = ["こんにちは。", "ありがとう。", WorldReference.ReviewText, "てすとです"];
        if (allModes) texts = [.. texts, "てすとです"];
        else texts = [.. texts, "すきです", "みずです", "てすとですね"];
        var results = new List<ProductContextCheckCase>();
        var timingPcm = new Dictionary<int, short[]>();
        short[]? neuralPrevious = null;
        double? preparationMs = null;
        if (neuralOnly)
        {
            speech.Mode = UtauSynthesisMode.OpenVoice;
            var prepareStarted = System.Diagnostics.Stopwatch.GetTimestamp();
            await speech.PrepareBankAsync(bank).ConfigureAwait(false);
            preparationMs = System.Diagnostics.Stopwatch.GetElapsedTime(prepareStarted).TotalMilliseconds;
        }
        foreach (var mode in waveformOnly ? [UtauSynthesisMode.Timing, UtauSynthesisMode.Psola, UtauSynthesisMode.PhaseVocoder, UtauSynthesisMode.DirectPsola] : neuralOnly ? [UtauSynthesisMode.OpenVoice] : allModes ? speech.Modes.Select(x => x.Mode) : [UtauSynthesisMode.Context])
        {
        speech.Mode = mode;
        for (var i = 0; i < texts.Length; i++)
        {
            fallbacks = 0;
            timingFallbacks = 0;
            periodFallbacks = 0;
            var started = System.Diagnostics.Stopwatch.GetTimestamp();
            PcmAudio audio;
            try { audio = await speech.SynthesizeAsync(texts[i]).ConfigureAwait(false); }
            catch (Exception error)
            {
                Console.Error.WriteLine($"context-case:{mode}:{i}:{error.GetType().Name}");
                throw;
            }
            var elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (audio.SampleRate != Rate || audio.Samples.Length is < MinimumSamples or > Rate * 45 ||
                !double.IsFinite(elapsed) || elapsed < 0) throw new InvalidDataException();
            var peak = audio.Samples.Max(value => Math.Abs((int)value));
            if (peak is < 1 or > 30147) throw new InvalidDataException();
            if (neuralOnly && i == 3) neuralPrevious = (short[])audio.Samples.Clone();
            if (neuralOnly && i == 4 && (neuralPrevious is null ||
                !audio.Samples.AsSpan().SequenceEqual(neuralPrevious) ||
                (modes.Resolve(mode) as DesktopNeuralUtau)?.LastTimings?.DonorMilliseconds != 0))
                throw new InvalidDataException("NEURAL_REPEAT_REGRESSION");
            int? changed = null;
            if (mode == UtauSynthesisMode.Timing) timingPcm[i] = (short[])audio.Samples.Clone();
            if (mode is UtauSynthesisMode.Psola or UtauSynthesisMode.PhaseVocoder)
            {
                var baseline = timingPcm[i];
                if (baseline.Length != audio.Samples.Length) throw new InvalidDataException();
                changed = baseline.Where((value, index) => value != audio.Samples[index]).Count();
                if (changed < 100) throw new InvalidDataException("追加方式が波形を変更していません。");
            }
            results.Add(new(i, fallbacks, timingFallbacks, audio.Samples.Length, audio.SampleRate,
                audio.Samples.Length / (double)audio.SampleRate, peak, elapsed, mode.ToString(), changed, periodFallbacks,
                neuralOnly ? (modes.Resolve(mode) as DesktopNeuralUtau)?.LastTimings : null,
                neuralOnly && i == 0 ? preparationMs : null));
        }
        }
        // await using completes disposal of both real child transports before
        // the wrapper emits a successful JSON response.
        return results.ToArray();
    }

    private sealed class ContextCheckNoPlayback : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("確認用の音声を再生できません。");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static int RunContextWorker(string[] args)
    {
        var owned = new List<string>(); var success = false; var stage = 0;
        try
        {
            ConfigureWorker();
            if (args.Length is not (6 or 7) || args.Length == 7 && args[6] is not ("pitch-control" or "original-timing" or "duration-control" or "duration-consonants") || args[0] != "--world-context-worker" ||
                !double.TryParse(args[4], NumberStyles.Float, CultureInfo.InvariantCulture, out var target) ||
                target != TimingTargetF0) throw new ArgumentException();
            var consonantTiming = args.Length == 7 && args[6] == "duration-consonants";
            var durationControl = args.Length == 7 && args[6] is "duration-control" or "duration-consonants";
            var pitchControl = args.Length == 7 && !durationControl;
            var originalTiming = args.Length == 7 && args[6] == "original-timing";
            var paths = ValidateJobOutputs(args[1], 2, args[2], args[3]);
            var bank = Voicebanks.LoadInstalledAsync(Path.Combine(paths[0], "banks")).GetAwaiter().GetResult()
                .Single(value => value.Name == "hosinohito");
            stage = 1;
            var text = durationControl ? "てすとです" : WorldReference.ReviewText;
            var plan = OpenJtalkContext.Read(args[5], text);
            stage = 2;
            var original = UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank).GetAwaiter().GetResult();
            stage = 3;
            var units = UtauProsodyPlan.Map(plan, original.Anchors);
            stage = 4;
            UtauReferenceTiming changed;
            if (durationControl)
            {
                changed = RenderAdjustedTimingAsync(text, bank, units, original, consonantTiming).GetAwaiter().GetResult().Rendered;
                try { original = UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, UtauProsodyTransfer.Allocate(units)).GetAwaiter().GetResult(); }
                catch (UtauTimingPlanException) { /* Match the product's ordinary-timing fallback. */ }
            }
            else if (originalTiming) changed = original;
            else
            {
            var allocated = UtauProsodyTransfer.Allocate(units);
            // One measured recording only. This is an experimental landmark,
            // not an automatically inferred phonetic boundary for other banks.
            if (allocated.Count <= 27 || allocated[27].Alias != "て" || allocated[27].DurationSamples != 2205)
                throw new InvalidDataException();
            var entry = bank.Entries.Single(value => value.Alias == "て");
            if (entry.Offset != 7 || entry.Preutterance != 61 || entry.Consonant != 221 || entry.Cutoff != 144)
                throw new InvalidDataException();
            var recording = Path.GetFullPath(Path.Combine(bank.Directory, entry.WavePath));
            if (!recording.StartsWith(Path.GetFullPath(bank.Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
            RejectLinks(recording);
            using var recordingPin = new FileStream(recording, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (recordingPin.Length > 1024 * 1024 ||
                Convert.ToHexString(SHA256.HashData(recordingPin)) != "21A2934B5BDD5D2367C76C8E13D94778BD507BCF5DA4247816D11D029E87192D")
                throw new InvalidDataException();
            stage = 5;
            changed = UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(WorldReference.ReviewText, bank, allocated,
                landmarkOverrides: [new UtauLandmarkOverride(27, 71, 1764)]).GetAwaiter().GetResult();
            }
            using var native = new NativeApi(WorldNativePlatform.LibraryPath(AppContext.BaseDirectory));
            stage = 6;
            var baseline = durationControl ? RenderContext(original, plan, units, native, 0)
                : pitchControl ? RenderContext(changed, plan, units, native, 0, flat: true)
                : RenderTiming(bank, 180, native, 0);
            stage = 7;
            var alternative = RenderContext(changed, plan, units, native, baseline.Samples.Length);
            if (pitchControl && (baseline.Samples.Length != alternative.Samples.Length ||
                baseline.Analysis.SpectrumSha256 != alternative.Analysis.SpectrumSha256 ||
                baseline.Analysis.AperiodicitySha256 != alternative.Analysis.AperiodicitySha256 ||
                baseline.Analysis.VoicingSha256 != alternative.Analysis.VoicingSha256 ||
                !baseline.Analysis.Anchors.SequenceEqual(alternative.Analysis.Anchors))) throw new InvalidDataException();
            var renders = new[] { baseline, alternative };
            stage = 8;
            var levels = MatchLevels(renders.Select(value => value.Samples).ToArray(), allowDifferentLengths: true);
            var conditions = new TimingCondition[2];
            for (var i = 0; i < renders.Length; i++)
            {
                var gain = levels.RmsGains[i] * levels.Headroom;
                var pcm = WriteWave(paths[i + 1], renders[i].Samples, gain, owned);
                conditions[i] = new(i == 0 ? "baseline" : "context", renders[i].Analysis,
                    levels.Rms[i], levels.Peaks[i], levels.RmsGains[i], levels.Rms[i] * gain, levels.Peaks[i] * gain,
                    Math.Sqrt(pcm.Samples.Sum(value => Math.Pow(value / 32768.0, 2)) / pcm.Samples.Length),
                    pcm.Samples.Max(value => Math.Abs((double)value)) / 32768.0,
                    pcm.Samples.Count(value => value is short.MinValue or short.MaxValue));
            }
            var json = JsonSerializer.Serialize(new
            {
                Comparison = "context", PitchControl = pitchControl, OriginalTiming = originalTiming, DurationControl = durationControl,
                ConsonantTiming = consonantTiming, TargetF0 = target, Gain = levels.Headroom,
                ManagedDoubleBufferBytes = Math.Max(baseline.Analysis.ManagedDoubleBufferBytes, alternative.Analysis.ManagedDoubleBufferBytes),
                ModelFrames = plan.FrameCount, ModelPhones = plan.Phones, MappedUnits = units,
                SourceRanges = changed.SourceRanges,
                Conditions = conditions
            });
            if (Encoding.UTF8.GetByteCount(json) > 65536) throw new InvalidDataException();
            Console.WriteLine(json); Console.Out.Flush(); success = true; return 0;
        }
        catch (Exception error)
        {
            var diagnostic = error is UtauTimingPlanException timing
                ? $"ContextStage{stage}Unit{timing.UnitIndex}Requested{timing.RequestedSamples}Minimum{timing.MinimumSamples}"
                : $"ContextStage{stage}";
            Console.Error.WriteLine($"world-error:{diagnostic}:{error.HResult:X8}");
            return 1;
        }
        finally
        {
            if (!success) foreach (var path in owned)
                try { RejectLinks(path); File.Delete(path); }
                catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException) { }
        }
    }

    private static TimingRender RenderContext(UtauReferenceTiming reference, OpenJtalkPlan plan,
        IReadOnlyList<UtauPlannedUnit> units, NativeApi native, int retainedSamples, bool flat = false)
    {
        var audio = reference.Audio;
        if (audio.SampleRate != Rate || audio.Samples.Length is < MinimumSamples or > Rate * 45 ||
            reference.Anchors.Count is < 1 or > 128) throw new InvalidDataException();
        var samples = audio.Samples.Select(value => value / 32768.0).ToArray();
        Check(native.Layout(samples.Length, out var frames, out var fft));
        if (fft != 2048 || frames is < 2 or > 9001 ||
            frames != (int)(1000.0 * samples.Length / Rate / 5.0) + 1) throw new InvalidDataException();
        var cells = checked(frames * (fft / 2 + 1));
        var bytes = checked((2L * cells + 9L * frames + 2L * samples.Length + retainedSamples) * sizeof(double));
        if (bytes > ProsodyBufferLimit) throw new InvalidDataException();
        var sourceF0 = new double[frames]; var times = new double[frames];
        var spectrum = new double[cells]; var ap = new double[cells];
        Check(native.Analyze(samples, samples.Length, sourceF0, frames, times, frames, spectrum, cells, ap, cells, fft));
        var f0 = UtauProsodyTransfer.TransferF0(plan, units, reference, sourceF0, TimingTargetF0);
        if (flat) for (var i = 0; i < f0.Length; i++) if (f0[i] > 0) f0[i] = TimingTargetF0;
        var spectrumHash = Hash(spectrum); var apHash = Hash(ap);
        var voicing = sourceF0.Select(value => value > 0 ? (byte)1 : (byte)0).ToArray();
        if (!f0.Select((value, i) => (value > 0 ? 1 : 0) == voicing[i]).All(value => value)) throw new InvalidDataException();
        var output = new double[samples.Length];
        Check(native.Synthesize(f0, frames, spectrum, cells, ap, cells, fft, output, output.Length));
        if (Hash(spectrum) != spectrumHash || Hash(ap) != apHash) throw new InvalidDataException();
        var weights = times.Select((time, index) => f0[index] > 0
            ? Math.Min(0.005, Math.Max(0, samples.Length / (double)Rate - time)) : 0).ToArray();
        return new(output, new(0, samples.Length, frames, fft, voicing.Count(value => value == 1), bytes,
            spectrumHash, apHash, Convert.ToHexString(SHA256.HashData(voicing)), Statistics(f0, weights), reference.Anchors));
    }
}






