using System.Collections;
using Tanayomi;

// Synthetic common-engine tests only. All WAV fixtures are owned by one
// TestDirectory; no native worker, speech device or saved utterance is used.
internal static class UtauContextTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = await MakeBankAsync(temporary.DirectoryPath);
        await CheckSourceInspectionCacheAsync(check);
        await CheckModesAsync(bank, check);
        await CheckAvailableModesAsync(bank, check);
        const string text = "アー、あっい。";
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        var timing = Allocations(baseline); var adjusted = timing.ToArray();
        adjusted[0] = adjusted[0] with { DurationSamples = adjusted[0].DurationSamples + 441 };
        var expected = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, adjusted);
        var output = new PcmProbe(); var processor = new Processor();
        string? receivedText = null; IReadOnlyList<UtauReadingUnit>? receivedReading = null;
        var accepted = new Plan(new IndexedTiming(adjusted));
        processor.Create = (value, reading, _) => { receivedText = value; receivedReading = reading; return Task.FromResult<IUtauUtterancePlan?>(accepted); };
        var reports = 0;
        await using (var voice = new UtauSpeech(output, processor) { Voicebank = bank })
        {
            voice.ProsodyFallback += _ => reports++;
            var actual = await voice.SynthesizeAsync(text);
            check(actual.Samples.SequenceEqual(expected.Audio.Samples) && actual.SampleRate == 44100 &&
                accepted.Calls == 1 && processor.FlatCalls == 0 && reports == 0 && output.Calls == 0,
                "valid context timing renders once and reaches context processing with exact B PCM instead of ordinary flat processing");
            check(receivedText == text && receivedReading is IList<UtauReadingUnit> { IsReadOnly: true } &&
                receivedReading.Select(unit => (unit.Alias, unit.Extensions, unit.Pause)).SequenceEqual(
                    baseline.Anchors.Select(anchor => (anchor.Alias, anchor.Extensions, anchor.Pause))),
                "context planning receives unchanged text and immutable normalized units including long vowels sokuon and punctuation");
            check(accepted.Rendered is { } rendered && rendered.Anchors.SequenceEqual(expected.Anchors) &&
                ReferenceEquals(accepted.Bank, bank) && rendered.SourceRanges.Count == 0,
                "context processing receives the actual rendered anchors and selected-bank snapshot without comparison landmark overrides");
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(timing));
            check((await voice.SynthesizeAsync(text)).Samples.SequenceEqual(baseline.Audio.Samples) && processor.FlatCalls == 0,
                "a valid identity timing plan preserves every normal B sample without reapplying pitch shaping");
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(accepted);
            await voice.SpeakAsync(text, default);
            check(output.Calls == 1 && output.Last!.Samples.SequenceEqual(expected.Audio.Samples),
                "successful context output alone reaches playback without a second pitch or flat pass");
        }
        check(processor.Disposes == 1 && output.Disposes == 1, "context processor ownership retains existing processor-before-output cleanup");
        await CheckFallbacksAsync(bank, check);
        await CheckLeadingAsync(bank, check);
        await CheckUnitDiagnosticsAsync(check);
        await CheckSingleUnitAcousticRulesAsync(check);
        await CheckContextFrictionAsync(check);
        await CheckOrdinaryTimingAsync(bank, check);
        await CheckCancellationAsync(bank, check);

        var ordinary = await UtauSpeech.SynthesizeReferenceAsync(text, bank);
        check(ordinary.Samples.SequenceEqual(baseline.Audio.Samples) &&
            (await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, timing)).Audio.Samples.SequenceEqual(ordinary.Samples),
            "planner-free B reference and existing explicit timing comparisons retain their exact normal waveform");
    }

    private static async Task CheckAvailableModesAsync(Voicebank bank, Action<bool, string> check)
    {
        await using var ordinary = new UtauSpeech(new PcmProbe()) { Voicebank = bank };
        var expected = await ordinary.SynthesizeAsync("あい");
        var errors = new Exception[] { new FileNotFoundException("Missing optional WORLD asset") };
        var provider = new Tanayomi.Desktop.DesktopUtauModes(worldAvailable: false, prosodyAvailable: false, initializationErrors: errors);
        await using var portable = new UtauSpeech(new PcmProbe(), provider) { Voicebank = bank };
        check(provider.Modes.Select(x => x.Mode).SequenceEqual(new[] { UtauSynthesisMode.Simple, UtauSynthesisMode.Context, UtauSynthesisMode.DirectPsola }) &&
            provider.Resolve(UtauSynthesisMode.Context) is null && provider.InitializationErrors.Count == 1,
            "missing optional native assets retain legacy, simple and direct modes while keeping the initialization diagnostic");
        check((await portable.SynthesizeAsync("あい")).Samples.SequenceEqual(expected.Samples),
            "a mode provider with unavailable native assets preserves the original legacy synthesis waveform");
        await using var noWorld = new Tanayomi.Desktop.DesktopUtauModes(worldAvailable: false, prosodyAvailable: true);
        check(noWorld.Modes.Any(x => x.Mode == UtauSynthesisMode.Timing) && noWorld.Modes.Any(x => x.Mode == UtauSynthesisMode.Psola) &&
            noWorld.Modes.All(x => x.Mode is not (UtauSynthesisMode.Flat or UtauSynthesisMode.FastContext)),
            "timing and waveform modes stay available when only WORLD is missing");
        await using var noModel = new Tanayomi.Desktop.DesktopUtauModes(worldAvailable: true, prosodyAvailable: false);
        await using var neural = new Tanayomi.Desktop.DesktopUtauModes(worldAvailable: false, prosodyAvailable: true, neuralAvailable: true);
        await using var neuralWithoutDonor = new Tanayomi.Desktop.DesktopUtauModes(worldAvailable: false, prosodyAvailable: false, neuralAvailable: true);
        check(neural.Modes.Any(x => x.Mode == UtauSynthesisMode.OpenVoice) &&
            neural.Resolve(UtauSynthesisMode.OpenVoice) is IUtauDirectRenderer &&
            !neuralWithoutDonor.Modes.Any(x => x.Mode == UtauSynthesisMode.OpenVoice) &&
            !noWorld.Modes.Any(x => x.Mode == UtauSynthesisMode.OpenVoice),
            "OpenVoice is optional and needs its donor assets but does not depend on WORLD");
        check(noModel.Modes.Any(x => x.Mode == UtauSynthesisMode.Flat) && noModel.Modes.Any(x => x.Mode == UtauSynthesisMode.DirectPsola) &&
            noModel.Modes.All(x => x.Mode is not (UtauSynthesisMode.Timing or UtauSynthesisMode.FastContext or UtauSynthesisMode.Psola)),
            "flat and original-source direct synthesis stay available without the context model");
        using var temporary = new TestDirectory();
        var store = new UtauModeStore(temporary.DirectoryPath);
        foreach (var mode in new[] { UtauSynthesisMode.DirectPsola, UtauSynthesisMode.OpenVoice })
        {
            await store.SaveAsync(mode);
            check(await store.LoadAsync() == mode, "new complete renderer mode persists without renaming older modes");
        }
    }

    private static async Task CheckModesAsync(Voicebank bank, Action<bool, string> check)
    {
        // Simple now includes the accepted continuous-pitch renderer. Compare
        // with the same mode without a native processor, not the legacy B oracle.
        await using var standalone = new UtauSpeech(new PcmProbe()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple };
        var ordinary = await standalone.SynthesizeAsync("あい");
        var processor = new Processor();
        await using var voice = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, Mode = UtauSynthesisMode.Simple };
        check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(ordinary.Samples) && processor.Creates == 0 && processor.FlatCalls == 0,
            "simple selection bypasses every context and WORLD stage and preserves standalone Simple PCM");
        var entered = Signal(); var resume = Signal();
        processor.Create = async (_, _, ct) => { entered.SetResult(); await resume.Task.WaitAsync(ct); return null; };
        voice.Mode = UtauSynthesisMode.Context;
        var pending = voice.SynthesizeAsync("あい");
        await entered.Task.WaitAsync(Limit);
        voice.Mode = UtauSynthesisMode.Simple; resume.SetResult();
        await pending.WaitAsync(Limit);
        check(processor.FlatCalls == 1, "mode changes cannot replace processing midway through the active utterance");
        await voice.SynthesizeAsync("あい");
        check(processor.FlatCalls == 1, "mode changes apply to the next utterance without native postprocessing");
        check(await CaptureAsync(() => { voice.Mode = (UtauSynthesisMode)999; return Task.CompletedTask; }) is ArgumentOutOfRangeException,
            "unsupported synthesis mode is rejected instead of silently selecting another algorithm");
        using var temp = new TestDirectory();
        var store = new UtauModeStore(temp.DirectoryPath);
        check(await store.LoadAsync() == UtauSynthesisMode.Context, "absent mode settings retain the current synthesis default");
        foreach (var mode in Enum.GetValues<UtauSynthesisMode>())
        {
            await store.SaveAsync(mode);
            check(await store.LoadAsync() == mode, "synthesis mode selection round-trips independently of text rules");
        }
        var savedMode = await store.LoadAsync();
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        check(await CaptureAsync(() => store.SaveAsync(UtauSynthesisMode.Simple, canceled.Token)) is OperationCanceledException &&
            await store.LoadAsync() == savedMode, "cancelled mode save preserves the previous setting");
        var path = Path.Combine(temp.DirectoryPath, "settings", "utau-mode.txt");
        foreach (var legacy in new[] { "ExternalResampler", "ExternalChain", "7", "9" })
        {
            await File.WriteAllTextAsync(path, legacy);
            check(await store.LoadAsync() == UtauSynthesisMode.Context, $"removed UTAU mode {legacy} restores Context");
        }
        foreach (var (legacy, mode) in new[] { ("8", UtauSynthesisMode.DirectPsola), ("10", UtauSynthesisMode.OpenVoice) })
        {
            await File.WriteAllTextAsync(path, legacy);
            check(await store.LoadAsync() == mode, $"UTAU mode {mode} retains its persisted numeric ID");
        }
        await File.WriteAllTextAsync(path, "999");
        check(await CaptureAsync(async () => { await store.LoadAsync(); }) is InvalidDataException, "unknown persisted mode is rejected");
        await File.WriteAllTextAsync(path, new string('x', 33));
        check(await CaptureAsync(async () => { await store.LoadAsync(); }) is InvalidDataException &&
            Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp").Length == 0, "mode storage is bounded and leaves no temporary files");
    }

    private static async Task CheckLeadingAsync(Voicebank bank, Action<bool, string> check)
    {
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あい", bank);
        var timing = Allocations(baseline);
        timing[1] = timing[1] with { LeadingSamples = 2205 };
        var expected = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync("あい", bank, timing);
        var plan = new Plan(timing) { CanProcessOrdinaryTiming = true };
        var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan) };
        await using var voice = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank };
        var timingFallbacks = 0; var flatFallbacks = 0;
        voice.ProsodyTimingFallback += _ => timingFallbacks++;
        voice.ProsodyFallback += _ => flatFallbacks++;
        check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(expected.Audio.Samples) &&
            plan.Rendered!.Anchors.SequenceEqual(expected.Anchors) && processor.FlatCalls == 0,
            "production context uses the requested consonant lead and forwards its actual changed anchors to pitch transfer");
        timing[1] = timing[1] with { LeadingSamples = 8820 };
        check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(baseline.Audio.Samples) &&
            timingFallbacks == 1 && flatFallbacks == 0 && processor.FlatCalls == 0,
            "a modeled consonant longer than the preceding allocation restores ordinary leads and keeps context pitch processing");
        timing[1] = timing[1] with { LeadingSamples = -1 };
        check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(baseline.Audio.Samples) &&
            flatFallbacks == 1 && processor.FlatCalls == 1,
            "invalid provider consonant metadata fails safely before rendering and cannot leak to the ordinary fallback");
    }

    private static async Task CheckUnitDiagnosticsAsync(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath)).Single(value => value.Name == "hosinohito");
        const string text = "てすとです。";
        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        var timing = Allocations(ordinary);
        int[] durations = [6395, 3087, 5072, 11466, 3086, 22050];
        int?[] leads = [5071, 3969, 1764, 2206, 8159, null];
        if (timing.Length != durations.Length) throw new InvalidDataException("Unit diagnostic fixed reading mismatch.");
        for (var i = 0; i < timing.Length; i++) timing[i] = timing[i] with { DurationSamples = durations[i], LeadingSamples = leads[i] };
        var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(timing)) };
        await using var normal = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, DisableUnitAcousticRulesForComparison = true };
        var baseline = await normal.SynthesizeAsync(text);
        await using var observed = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, DisableUnitAcousticRulesForComparison = true };
        observed.EnableUnitDiagnostics(1);
        var actual = await observed.SynthesizeAsync(text);
        var diagnostic = observed.UnitDiagnostics;
        check(diagnostic is { AcousticTransitionApplied: true, AcousticTransitionProtection.Available: true, AcousticBoundary.Found: true },
            "planned Context adopts the validated cross-pre map for the first eligible compressed unit");
        await using var legacy = new UtauSpeech(new PcmProbe(), processor)
            { Voicebank = bank, DisableAcousticTransitionProtectionForComparison = true, DisableUnitAcousticRulesForComparison = true };
        legacy.EnableUnitDiagnostics(1);
        var legacyAudio = await legacy.SynthesizeAsync(text);
        check(legacy.UnitDiagnostics is { AcousticTransitionApplied: false, AcousticTransitionProtection.Available: true } &&
            legacyAudio.Samples.Length == actual.Samples.Length && !legacyAudio.Samples.SequenceEqual(actual.Samples),
            "explicit internal comparison opt-out retains prior Context PCM with identical length without changing eligibility guards");
        await using var lastUnit = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, DisableUnitAcousticRulesForComparison = true };
        lastUnit.EnableUnitDiagnostics(4);
        var lastUnitAudio = await lastUnit.SynthesizeAsync(text);
        check(lastUnit.UnitDiagnostics is { UnitIndex: 4, AcousticTransitionApplied: true } && lastUnitAudio.Samples.SequenceEqual(actual.Samples) &&
            lastUnit.UnitDiagnosticAudio is null,
            "protection applies independently to another eligible unit without collecting any diagnostic audio in ordinary numeric mode");
        await using var timingOnly = new UtauSpeech(new PcmProbe(), new TimingModeProvider(processor))
            { Voicebank = bank, Mode = UtauSynthesisMode.Timing };
        timingOnly.EnableUnitDiagnostics(1);
        var timingOnlyAudio = await timingOnly.SynthesizeAsync(text);
        check(timingOnly.UnitDiagnostics is { AcousticTransitionApplied: false } && timingOnlyAudio.Samples.SequenceEqual(legacyAudio.Samples),
            "non-Context planned modes preserve their prior timing and waveform despite eligible source material");
        var rejectedTiming = timing.ToArray(); rejectedTiming[0] = rejectedTiming[0] with { DurationSamples = 1 };
        var fallbackProcessor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(rejectedTiming) { CanProcessOrdinaryTiming = true }) };
        await using var ordinaryFallback = new UtauSpeech(new PcmProbe(), fallbackProcessor) { Voicebank = bank };
        ordinaryFallback.EnableUnitDiagnostics(1);
        var ordinaryFallbacks = 0; ordinaryFallback.ProsodyTimingFallback += _ => ordinaryFallbacks++;
        var ordinaryFallbackAudio = await ordinaryFallback.SynthesizeAsync(text);
        check(ordinaryFallbacks == 1 && ordinaryFallback.UnitDiagnostics is { AcousticTransitionApplied: false } &&
            ordinaryFallbackAudio.Samples.SequenceEqual(ordinary.Audio.Samples),
            "ordinary-timing fallback disables planned protection and preserves its previous waveform after a rejected allocation");
        check(actual.SampleRate == baseline.SampleRate && actual.Samples.SequenceEqual(baseline.Samples) && normal.UnitDiagnostics is null,
            "opt-in unit grain and Append observation preserves every planned-render PCM sample; disabled instances retain no diagnostic");
        check(normal.UnitDiagnosticAudio is null && observed.UnitDiagnosticAudio is null,
            "disabled and numeric-only unit diagnostics never retain source or rendered audio clones");
        check(diagnostic is { UnitIndex: 1, SampleRate: 44100, LeadSamples: 3969, DurationSamples: 3087, Truncated: false } &&
            diagnostic.TrimmedStartSample >= diagnostic.OtoOffsetSample && diagnostic.PreutteranceSample > diagnostic.TrimmedStartSample &&
            diagnostic.StableSample >= diagnostic.PreutteranceSample && diagnostic.EndSample > diagnostic.StableSample &&
            diagnostic.Grains.Count is > 1 and <= 256 && diagnostic.AppendWindows.Count == 9,
            "fixed su diagnostic records original-WAV absolute landmarks and bounded 90ms lead-window contributions");
        check(diagnostic is not null && diagnostic.Grains[0] is { SourceStart: 0, NominalSourceStart: 0, OutputAt: 0, OverlapSamples: 0, WindowSamples: 882 } &&
            diagnostic.Grains.All(grain => grain.SourceStart >= 0 && grain.SourceStart + grain.WindowSamples <= diagnostic.EndSample - diagnostic.TrimmedStartSample &&
                grain.OutputAt >= 0 && grain.OutputAt + grain.WindowSamples <= diagnostic.LeadSamples + diagnostic.DurationSamples &&
                grain.OverlapSamples >= 0 && grain.OverlapSamples <= grain.WindowSamples),
            "diagnostic grain coordinates are relative to trimmed source and rendered unit with complete in-range windows");
        check(diagnostic is not null && diagnostic.AppendWindows.All(window => window.Samples == 441 &&
            double.IsFinite(window.IncomingRms) && window.IncomingRms >= 0 && double.IsFinite(window.PreviousContributionRms) && window.PreviousContributionRms >= 0 &&
            double.IsFinite(window.JoinedRms) && window.JoinedRms >= 0 && double.IsFinite(window.CrossMean) &&
            Math.Abs(window.JoinedRms * window.JoinedRms - window.IncomingRms * window.IncomingRms -
                window.PreviousContributionRms * window.PreviousContributionRms - 2 * window.CrossMean) < 1e-7),
            "Append diagnostic separates incoming energy, faded previous contribution and interference without changing the actual sum");
        await using var shortened = new UtauSpeech(new PcmProbe(), processor)
            { Voicebank = bank, ShortUnvoicedReleaseForComparison = true, DisableUnitAcousticRulesForComparison = true };
        shortened.EnableUnitDiagnostics(1);
        var shortAudio = await shortened.SynthesizeAsync(text);
        var shortDiagnostic = shortened.UnitDiagnostics;
        check(diagnostic is not null && shortDiagnostic is not null && shortAudio.Samples.Length == baseline.Samples.Length &&
            shortDiagnostic.Grains.SequenceEqual(diagnostic.Grains) && shortDiagnostic.OtoOffsetSample == diagnostic.OtoOffsetSample &&
            shortDiagnostic.TrimmedStartSample == diagnostic.TrimmedStartSample && shortDiagnostic.PreutteranceSample == diagnostic.PreutteranceSample &&
            shortDiagnostic.StableSample == diagnostic.StableSample && shortDiagnostic.EndSample == diagnostic.EndSample &&
            shortDiagnostic.LeadSamples == diagnostic.LeadSamples && shortDiagnostic.DurationSamples == diagnostic.DurationSamples &&
            shortDiagnostic.AppendWindows.Select(window => window.IncomingRms).SequenceEqual(diagnostic.AppendWindows.Select(window => window.IncomingRms)),
            "short unvoiced release changes no source ranges, grain selections, timing or incoming sample contributions");
        check(diagnostic is not null && shortDiagnostic is not null &&
            diagnostic.AppendWindows.Skip(1).Any(window => window.PreviousContributionRms > 0) &&
            shortDiagnostic.AppendWindows.Skip(1).All(window => window.PreviousContributionRms == 0 && window.CrossMean == 0) &&
            !shortAudio.Samples.SequenceEqual(baseline.Samples),
            "comparison unvoiced release removes only previous-sound contribution after 10ms and produces an actual bounded contrast");
        await using var audioObserved = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, DisableUnitAcousticRulesForComparison = true };
        audioObserved.EnableUnitDiagnostics(1, captureAudio: true);
        var audioObservedResult = await audioObserved.SynthesizeAsync(text);
        var audioSnapshot = audioObserved.UnitDiagnosticAudio;
        var audioRange = audioObserved.UnitDiagnostics;
        var recording = bank.Entries.Single(entry => entry.Alias == "す").WavePath;
        var rawBefore = await File.ReadAllBytesAsync(recording);
        var decoded = UtauSpeech.DecodeWave(rawBefore, default);
        check(audioObservedResult.Samples.SequenceEqual(baseline.Samples) && audioSnapshot is not null && audioRange is not null &&
            audioSnapshot.SampleRate == 44100 && audioSnapshot.SourceUsed.Length == audioRange.EndSample - audioRange.TrimmedStartSample &&
            audioSnapshot.ShapedBeforePitch.Length == audioRange.LeadSamples + audioRange.DurationSamples &&
            audioSnapshot.SourceUsed.AsSpan().SequenceEqual(decoded.AsSpan(audioRange.TrimmedStartSample, audioSnapshot.SourceUsed.Length)) &&
            audioSnapshot.SourceUsed.All(float.IsFinite) && audioSnapshot.ShapedBeforePitch.All(float.IsFinite),
            "explicit audio observation captures the same invocation's exact decoded source range and immediate ShapeMora floats without changing PCM");
        if (audioSnapshot is null) throw new InvalidDataException("Audio diagnostic snapshot missing.");
        check(!ReferenceEquals(audioSnapshot.SourceUsed, audioSnapshot.ShapedBeforePitch),
            "source and ShapeMora audio observations have independent owned arrays");
        var realProtection = audioSnapshot.TransitionProtection;
        check(realProtection is not null && realProtection.Available == (audioSnapshot.TransitionProtected is not null) &&
            (realProtection.Available ? realProtection.UnavailableReason is null && audioSnapshot.TransitionProtected!.Length == audioSnapshot.ShapedBeforePitch.Length
                : !string.IsNullOrWhiteSpace(realProtection.UnavailableReason)),
            "real-source transition diagnostic reports one same-length candidate or an explicit unavailable reason without assuming acoustic suitability");
        var acousticProtection = audioSnapshot.AcousticTransitionProtection;
        check(acousticProtection is not null && acousticProtection.Available == (audioSnapshot.AcousticTransitionProtected is not null) &&
            (acousticProtection.Available ? acousticProtection.UnavailableReason is null &&
                audioSnapshot.AcousticTransitionProtected!.Length == audioSnapshot.ShapedBeforePitch.Length
                : !string.IsNullOrWhiteSpace(acousticProtection.UnavailableReason)),
            "detected cross-pre candidate reports same-length audio or an explicit unavailable reason while adopted PCM remains unchanged");
        check(audioSnapshot.AcousticTransitionProtected is not null &&
            audioSnapshot.ShapedBeforePitch.SequenceEqual(audioSnapshot.AcousticTransitionProtected),
            "adopted planned Context ShapeMora output matches the accepted single-map diagnostic candidate before pitch and joining");
        Array.Fill(audioSnapshot.SourceUsed, 0.75f); Array.Fill(audioSnapshot.ShapedBeforePitch, -0.75f);
        var afterMutation = await audioObserved.SynthesizeAsync(text);
        var rawAfter = await File.ReadAllBytesAsync(recording);
        check(afterMutation.Samples.SequenceEqual(baseline.Samples) && ReferenceEquals(audioObserved.UnitDiagnosticAudio, audioSnapshot) &&
            audioSnapshot.SourceUsed.All(value => value == 0.75f) && audioSnapshot.ShapedBeforePitch.All(value => value == -0.75f) &&
            rawBefore.SequenceEqual(rawAfter),
            "audio diagnostic clones cannot mutate cached source, normal playback or the recording, and are not collected again on a later invocation");
        var again = await observed.SynthesizeAsync(text);
        check(ReferenceEquals(observed.UnitDiagnostics, diagnostic) && again.Samples.SequenceEqual(baseline.Samples),
            "unit diagnostic captures only its first synthesis invocation and does not change later audio");
        check(await CaptureAsync(() => { observed.EnableUnitDiagnostics(1); return Task.CompletedTask; }) is InvalidOperationException,
            "a used unit diagnostic cannot be reinitialized");
        await using var invalid = new UtauSpeech(new PcmProbe()) { Voicebank = bank };
        check(await CaptureAsync(() => { invalid.EnableUnitDiagnostics(640); return Task.CompletedTask; }) is ArgumentOutOfRangeException,
            "unit diagnostic rejects an index outside the bounded reading");
        foreach (var index in new[] { 0, 2 })
        {
            await using var boundary = new UtauSpeech(new PcmProbe()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple };
            boundary.EnableUnitDiagnostics(index);
            await boundary.SynthesizeAsync("す、す");
            check(boundary.UnitDiagnostics is { } edge && edge.UnitIndex == index && edge.Grains.Count > 0 && edge.AppendWindows.Count == 0,
                "unit diagnostics retain source and grains at utterance start or after a pause without inventing previous-sound contributions");
        }
        await CheckTransitionProtectionAsync(check);
    }

    private static async Task CheckTransitionProtectionAsync(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var sineBank = await MakeBankAsync(fixture.DirectoryPath);
        var source = new short[22050]; var random = new Random(741852);
        for (var i = 0; i < source.Length; i++)
            source[i] = i < 3087 ? (short)random.Next(-6000, 6001) : (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
        var path = Path.Combine(fixture.DirectoryPath, "transition.wav");
        await File.WriteAllBytesAsync(path, new PcmAudio(source, 44100).ToWaveBytes());
        var entries = sineBank.Entries.Where(entry => entry.Alias != "い").ToList();
        entries.Add(new(path, "い", 0, 200, -400, 90, 10));
        var transitionBank = new Voicebank("transition diagnostic fixture", fixture.DirectoryPath, entries, 0);
        var successful = await CaptureTransitionAsync(transitionBank, 1984);
        var candidate = successful.TransitionProtection;
        check(candidate is { Available: true, UnavailableReason: null, ProtectedSamples: 1323, PostOnsetVoiced: true, GrainsTruncated: false } &&
            successful.TransitionProtected is not null && successful.TransitionProtected.Length == successful.ShapedBeforePitch.Length &&
            candidate.SourceEnd - candidate.SourceStart == 1323 && candidate.OutputEnd - candidate.OutputStart == 1323 &&
            candidate.Grains.Count is > 1 and <= 256 && candidate.Grains.All(grain => grain.SourceStart >= 0 &&
                grain.SourceStart + grain.WindowSamples <= successful.SourceUsed.Length && grain.OutputAt >= 0 &&
                grain.OutputAt + grain.WindowSamples <= successful.ShapedBeforePitch.Length),
            "fixed acoustic change and sufficient budget produce one bounded same-length pre-onset transition-map candidate with actual grain positions");
        check(successful.TransitionProtected is not null && !successful.TransitionProtected.AsSpan().SequenceEqual(successful.ShapedBeforePitch),
            "transition-map diagnostic produces a real contrast without replacing the adopted ShapeMora output");
        var boundary = candidate?.AcousticBoundary;
        check(boundary is { Found: true, RequiredConsecutiveWindows: 5, WindowSamples: 882 } && boundary.Windows.Count == 21 &&
            boundary.Windows[0].StartSample == boundary.ReferencePreSample - 2646 &&
            boundary.Windows[^1].StartSample == boundary.ReferencePreSample + 1764 &&
            boundary.Windows[^1].EndSample == boundary.ReferencePreSample + 2646 &&
            boundary.Windows.Zip(boundary.Windows.Skip(1), (first, next) => next.StartSample - first.StartSample).All(hop => hop is 220 or 221),
            "acoustic observer keeps exactly 21 twenty-ms windows inside pre +/-60ms using nonaccumulating rounded five-ms offsets");
        CheckAcousticMapContracts(successful, boundary, check);
        var acousticFailure = await CaptureTransitionAsync(sineBank, 1984);
        check(acousticFailure.TransitionProtected is null && acousticFailure.TransitionProtection is { Available: false, PostOnsetVoiced: true } &&
            !string.IsNullOrWhiteSpace(acousticFailure.TransitionProtection.UnavailableReason),
            "pure voiced input cannot be mislabeled as a confirmed high-to-low-difference transition");
        var budgetFailure = await CaptureTransitionAsync(transitionBank, 882);
        check(budgetFailure.TransitionProtected is null && budgetFailure.TransitionProtection is { Available: false } &&
            !string.IsNullOrWhiteSpace(budgetFailure.TransitionProtection.UnavailableReason),
            "a lead shorter than the fixed protected interval returns an unavailable diagnostic without changing synthesis");
        check(budgetFailure.TransitionProtection?.AcousticBoundary is { Found: true, BudgetFits: false },
            "detected voiced-run start and insufficient hypothetical preservation budget are distinct diagnostic outcomes");
        foreach (var noiseMilliseconds in new[] { 100, 160 })
        {
            var delayed = new short[22050]; var delayedRandom = new Random(741852);
            var noiseEnd = noiseMilliseconds * 44100 / 1000;
            for (var i = 0; i < delayed.Length; i++) delayed[i] = i < noiseEnd ? (short)delayedRandom.Next(-6000, 6001)
                : (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100));
            var delayedPath = Path.Combine(fixture.DirectoryPath, $"boundary-{noiseMilliseconds}.wav");
            await File.WriteAllBytesAsync(delayedPath, new PcmAudio(delayed, 44100).ToWaveBytes());
            var delayedEntries = sineBank.Entries.Where(entry => entry.Alias != "い").ToList();
            delayedEntries.Add(new(delayedPath, "い", 0, 200, -400, 90, 10));
            var delayedBank = new Voicebank("boundary observer fixture", fixture.DirectoryPath, delayedEntries, 0);
            var observed = await CaptureTransitionAsync(delayedBank, 1984);
            var detection = observed.TransitionProtection?.AcousticBoundary;
            if (noiseMilliseconds == 100)
                check(detection is { Found: true, DeltaSamples: > 0, BudgetFits: true } && detection.Windows.Count == 21 &&
                    observed.TransitionProtected is null,
                    "post-OTO voiced onset is reported with hypothetical pre/post budget but never creates a new transition candidate");
            else
                check(detection is { Found: false, CandidateSample: null, BudgetFits: null } && detection.Windows.Count == 21 &&
                    !string.IsNullOrWhiteSpace(detection.UnavailableReason) && observed.TransitionProtected is null,
                    "fixed all-unvoiced scan returns an explicit unavailable reason without threshold retries or candidate audio");
        }

        async Task<UtauUnitDiagnosticAudio> CaptureTransitionAsync(Voicebank bank, int lead)
        {
            var reference = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あい", bank);
            var allocations = Allocations(reference); allocations[1] = allocations[1] with { LeadingSamples = lead };
            var expected = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync("あい", bank, allocations);
            var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(allocations)) };
            await using var voice = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank };
            voice.EnableUnitDiagnostics(1, captureAudio: true);
            var observed = await voice.SynthesizeAsync("あい");
            check(observed.Samples.SequenceEqual(expected.Audio.Samples),
                "transition candidate collection leaves adopted synthesis bit-for-bit unchanged whether the candidate is available or not");
            return voice.UnitDiagnosticAudio ?? throw new InvalidDataException("Transition audio observation missing.");
        }
    }

    // Map/guard unit tests use explicitly controlled detector metadata. They do
    // not claim the artificial boundary is the observer's acoustic estimate.
    private static void CheckAcousticMapContracts(UtauUnitDiagnosticAudio source, UtauAcousticBoundaryDiagnostic? observedBoundary,
        Action<bool, string> check)
    {
        if (observedBoundary is null) throw new InvalidDataException("Synthetic boundary observation missing.");
        const int pre = 3969, lead = 1984;
        var offset = observedBoundary.ReferencePreSample - pre;
        var duration = source.ShapedBeforePitch.Length - lead;
        var controlled = observedBoundary with
        {
            Found = true, BudgetFits = true, CandidateSample = offset + pre + 220, DeltaSamples = 220,
            HypotheticalProtectedStart = offset + pre - 662, HypotheticalProtectedEnd = offset + pre + 1102,
            RequiredPreSamples = 662, RequiredPostSamples = 1102
        };
        var original = source.SourceUsed.ToArray();
        var accepted = UtauSpeech.RenderAcousticTransitionProtection(source.SourceUsed, pre, pre, lead, 0, duration, offset, controlled, default);
        check(accepted.Diagnostic is { Available: true, UnavailableReason: null, GrainsTruncated: false } && accepted.Audio is not null &&
            accepted.Audio.Length == lead + duration && accepted.Diagnostic.OutputStart == lead - 662 &&
            accepted.Diagnostic.OutputEnd == lead + 1102 && accepted.Diagnostic.SourceStart == offset + pre - 662 &&
            accepted.Diagnostic.SourceEnd == offset + pre + 1102 && source.SourceUsed.SequenceEqual(original),
            "controlled cross-pre acoustic map retains the pre anchor and total length while leaving every source sample unchanged");
        check(accepted.Audio is not null && accepted.Audio.All(float.IsFinite) && accepted.Diagnostic.Grains.Count is > 1 and <= 256 &&
            accepted.Diagnostic.Grains.All(grain => grain.SourceStart >= 0 && grain.SourceStart + grain.WindowSamples <= source.SourceUsed.Length &&
                grain.OutputAt >= 0 && grain.OutputAt + grain.WindowSamples <= lead + duration),
            "acoustic map records actual bounded grain contributions without claiming exact preservation from nominal slope alone");
        foreach (var rejected in new[]
        {
            controlled with { Found = false }, controlled with { BudgetFits = false },
            controlled with { HypotheticalProtectedStart = offset },
            controlled with { HypotheticalProtectedEnd = offset + source.SourceUsed.Length },
            controlled with { HypotheticalProtectedEnd = offset + pre },
            controlled with { HypotheticalProtectedStart = offset + pre }
        })
        {
            var result = UtauSpeech.RenderAcousticTransitionProtection(source.SourceUsed, pre, pre, lead, 0, duration, offset, rejected, default);
            check(result.Audio is null && !result.Diagnostic.Available && !string.IsNullOrWhiteSpace(result.Diagnostic.UnavailableReason),
                "missing detection, insufficient budget and source-edge or non-crossing intervals cannot create acoustic-map audio");
        }
        var extraTransition = UtauSpeech.RenderAcousticTransitionProtection(source.SourceUsed, pre, pre + 441, lead, 441, duration, offset, controlled, default);
        check(extraTransition.Audio is null && !extraTransition.Diagnostic.Available,
            "an existing additional source transition is rejected instead of being overlapped by a second acoustic map");
        var exhausted = UtauSpeech.RenderAcousticTransitionProtection(source.SourceUsed, pre, pre, 662, 0, duration, offset, controlled, default);
        check(exhausted.Audio is null && !exhausted.Diagnostic.Available,
            "crossing preservation that exhausts the remaining lead budget is unavailable");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        var cancelled = false;
        try { UtauSpeech.RenderAcousticTransitionProtection(source.SourceUsed, pre, pre, lead, 0, duration, offset, controlled, stopped.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        check(cancelled, "acoustic-map selection observes cancellation before any waveform generation");
    }

    private static async Task CheckSingleUnitAcousticRulesAsync(Action<bool, string> check)
    {
        const int rate = 44100, pre = 6174, lead = 4410, requested = 11025, transitionStart = 5292, transitionEnd = 7056;
        var source = new float[14112]; var random = new Random(62831); var period = rate / 220.0;
        for (var i = 0; i < source.Length; i++)
            source[i] = i < transitionStart ? (float)(0.18 * (0.5 + 0.5 * Math.Min(1, i / 882.0)) * (2 * random.NextDouble() - 1))
                : i < 6395 ? random.Next(-2800, 2801) / 32768f : (float)(0.18 * Math.Sin(2 * Math.PI * 220 * i / rate));
        var before = source.ToArray();
        var rule = UtauUnitTimeMap.Create(source, pre, lead, requested, transitionStart, transitionEnd, period, default);
        check(rule.Map is not null && rule.Diagnostic.Available && rule.Diagnostic.AttackProtected && rule.Diagnostic.Segments.Count == 4,
            "confirmed sustained friction and voiced tail select the bounded four-part unit rule: " + rule.Diagnostic.UnavailableReason);
        if (rule.Map is not { } map) throw new InvalidDataException("Expected artificial unit-map fixture was not eligible.");
        var mapped = -1.0;
        for (var i = 0; i <= rule.Diagnostic.OutputLength; i++)
        {
            var at = map.SourceAt(i);
            if (!double.IsFinite(at) || at < mapped || at < 0 || at > source.Length) throw new InvalidDataException("Unit map is not monotone and finite.");
            mapped = at;
        }
        check(map.SourceAt(0) == 0 && map.SourceAt(rule.Diagnostic.OutputLength) == source.Length &&
            Math.Abs(map.SourceAt(map.MappedPre) - pre) < 1e-8 && map.MappedPre >= lead && map.MappedPre < rule.Diagnostic.OutputLength &&
            source.SequenceEqual(before), "unit map covers source continuously with fixed placement lead, mapped OTO pre, finite monotonicity and immutable original samples");
        check(map.Segments.All(part => part.SourceEnd > part.SourceStart && part.OutputEnd > part.OutputStart &&
                (!part.Protected || part.SourceEnd - part.SourceStart == part.OutputEnd - part.OutputStart)) &&
            map.Segments.Zip(map.Segments.Skip(1), (a, b) => a.SourceEnd == b.SourceStart && a.OutputEnd == b.OutputStart).All(value => value),
            "unit map has contiguous positive intervals and exact nominal slopes in protected attack and transition regions");
        foreach (var part in map.Segments.Where(part => part.Protected))
            check(map.TryProtectedSource(part.OutputStart, 882, source.Length, out var exact) && exact == part.SourceStart,
                "fully contained protected grains use their exact source offset");
        var protectedTransition = map.Segments[2];
        check(!map.TryProtectedSource(protectedTransition.OutputStart - 441, 882, source.Length, out _),
            "a grain crossing a protection boundary remains observable and is not falsely marked fully protected");
        var extended = UtauUnitTimeMap.Create(source, pre, 2646, 4851, transitionStart, transitionEnd, period, default);
        check(extended.Map is not null && extended.Diagnostic.OutputLength > extended.Diagnostic.RequestedOutputLength &&
            extended.Diagnostic.ExtendedSamples == extended.Diagnostic.OutputLength - 4851 && extended.Diagnostic.OutputLength <= source.Length &&
            extended.Diagnostic.AttackEnd + 882 + 1764 + extended.Diagnostic.TailOutputSamples == extended.Diagnostic.OutputLength,
            "short requested units extend only to the protected-rise/middle/transition/tail processing budget, bounded by original source");
        foreach (var identityLength in new[] { source.Length, source.Length + 882 })
            check(UtauUnitTimeMap.Create(source, pre, lead, identityLength, transitionStart, transitionEnd, period, default).Map is null,
                "identity and existing lengthening are not needlessly changed or shortened by compression rules");
        var periodic = new float[source.Length]; var nonperiodic = new float[source.Length]; var noise = new Random(7541);
        for (var i = 0; i < source.Length; i++)
        { periodic[i] = (float)(0.18 * Math.Sin(2 * Math.PI * 220 * i / rate)); nonperiodic[i] = (float)(0.18 * (2 * noise.NextDouble() - 1)); }
        var burst = source.ToArray(); Array.Clear(burst, 882, transitionStart - 882);
        var invalid = source.ToArray(); invalid[20] = float.NaN;
        foreach (var ineligible in new[] { periodic, nonperiodic, burst, invalid })
        {
            var declinedRule = UtauUnitTimeMap.Create(ineligible, pre, lead, requested, transitionStart, transitionEnd, period, default);
            check(declinedRule.Map is null && !declinedRule.Diagnostic.Available && !string.IsNullOrWhiteSpace(declinedRule.Diagnostic.UnavailableReason),
                "pure vowels, nonperiodic-only noise, burst/closure-like inputs and nonfinite source decline without guessed phoneme rules");
        }
        var prefix = UtauUnitTimeMap.FindContinuousPrefix(source, 441, period, default);
        check(prefix.Start < 441 && prefix.Diagnostic.RecoveredSamples > 0 && prefix.Diagnostic.RecoveredSamples <= 2646 && source.SequenceEqual(before),
            "continuous weak nonperiodic prefix is recoverable from the full immutable recording within the lookback cap");
        var silentPrefix = source.ToArray(); Array.Clear(silentPrefix, 0, 441);
        var periodicPrefix = source.ToArray(); periodic.AsSpan(0, 441).CopyTo(periodicPrefix);
        foreach (var stoppedPrefix in new[] { silentPrefix, periodicPrefix })
            check(UtauUnitTimeMap.FindContinuousPrefix(stoppedPrefix, 441, period, default).Start == 441,
                "new five-ms prefix content must itself pass guards, so known friction cannot drag silence or periodic sound into the crop");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var stopped = false;
            try { UtauUnitTimeMap.Create(source, pre, lead, requested, transitionStart, transitionEnd, period, cancelled.Token); }
            catch (OperationCanceledException) { stopped = true; }
            check(stopped, "single-unit acoustic analysis observes cancellation before scanning or generating audio");
        }

        using var fixture = new TestDirectory();
        var sourcePath = Path.Combine(fixture.DirectoryPath, "friction.wav"); var vowelPath = Path.Combine(fixture.DirectoryPath, "vowel.wav");
        var wav = new PcmAudio(source.Select(value => (short)Math.Round(value * 32767)).ToArray(), rate).ToWaveBytes();
        await File.WriteAllBytesAsync(sourcePath, wav);
        var vowel = Enumerable.Range(0, 22050).Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / rate))).ToArray();
        await File.WriteAllBytesAsync(vowelPath, new PcmAudio(vowel, rate).ToWaveBytes());
        var bank = new Voicebank("physical unit rule fixture", fixture.DirectoryPath,
            [new(vowelPath, "あ", 0, 200, -400, 20, 10), new(sourcePath, "い", 10, 130, -310, 130, 20),
                new(vowelPath, "う", 0, 200, -400, 20, 10)], 0);
        const string text = "あいう";
        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        var allocations = Allocations(ordinary);
        if (allocations.Length != 3) throw new InvalidDataException("Physical unit rule reading mismatch.");
        allocations[1] = allocations[1] with { DurationSamples = 6615, LeadingSamples = lead };
        await CheckUnitRulePipelineAsync(bank, text, allocations, wav, sourcePath, expectExtension: false, check);
        allocations[1] = allocations[1] with { DurationSamples = 2205, LeadingSamples = 2646 };
        await CheckUnitRulePipelineAsync(bank, text, allocations, wav, sourcePath, expectExtension: true, check);
        var periodicBank = new Voicebank("ineligible periodic unit", fixture.DirectoryPath,
            [new(vowelPath, "あ", 0, 200, -400, 20, 10), new(vowelPath, "い", 10, 240, -310, 130, 20),
                new(vowelPath, "う", 0, 200, -400, 20, 10)], 0);
        var declinedProcessor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(allocations)) };
        await using var declined = new UtauSpeech(new PcmProbe(), declinedProcessor) { Voicebank = periodicBank };
        declined.EnableUnitDiagnostics(1);
        var declinedAudio = await declined.SynthesizeAsync(text);
        await using var declinedOld = new UtauSpeech(new PcmProbe(), declinedProcessor)
            { Voicebank = periodicBank, DisableUnitAcousticRulesForComparison = true };
        var declinedOldAudio = await declinedOld.SynthesizeAsync(text);
        check(declined.UnitDiagnostics is { UnitAcousticRules.Available: false } && declinedAudio.Samples.SequenceEqual(declinedOldAudio.Samples),
            "ineligible periodic units retain the exact previous D/fallback waveform in the normal product path");
    }

    private static async Task CheckUnitRulePipelineAsync(Voicebank bank, string text, UtauTimingAllocation[] timing,
        byte[] originalWave, string sourcePath, bool expectExtension, Action<bool, string> check)
    {
        var previousPlan = new Plan(timing); var acceptedPlan = new Plan(timing);
        var previousProcessor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(previousPlan) };
        var acceptedProcessor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(acceptedPlan) };
        await using var previous = new UtauSpeech(new PcmProbe(), previousProcessor) { Voicebank = bank, DisableUnitAcousticRulesForComparison = true };
        previous.EnableUnitDiagnostics(1);
        var previousAudio = await previous.SynthesizeAsync(text);
        await using var accepted = new UtauSpeech(new PcmProbe(), acceptedProcessor) { Voicebank = bank };
        accepted.EnableUnitDiagnostics(1, captureAudio: true);
        var acceptedAudio = await accepted.SynthesizeAsync(text);
        var diagnostic = accepted.UnitDiagnostics;
        check(diagnostic is { UnitAcousticRules.Available: true, AcousticTransitionApplied: false, SourceRecovery.Adopted: true } &&
            diagnostic.MappedVowelOnset >= diagnostic.LeadSamples && diagnostic.MappedVowelOnset < diagnostic.LeadSamples + diagnostic.DurationSamples,
            "normal planned Context adopts unit rules and a continuous prefix with separate mapped onset: " +
                diagnostic?.UnitAcousticRules?.UnavailableReason + "; recovery=" + diagnostic?.SourceRecovery?.Adopted + "; " + diagnostic?.SourceRecovery?.Reason);
        if (diagnostic?.UnitAcousticRules is not { } rule) throw new InvalidDataException("Pipeline rule metadata missing.");
        var old = previous.UnitDiagnostics!;
        check(diagnostic.PreutteranceSample == old.PreutteranceSample && diagnostic.StableSample == old.StableSample &&
            diagnostic.EndSample == old.EndSample && diagnostic.TrimmedStartSample < old.TrimmedStartSample && diagnostic.OtoOffsetSample == old.OtoOffsetSample,
            "prefix recovery expands source usage while original absolute pre/stable/end and OTO offset remain unchanged");
        check(rule.ExtendedSamples == acceptedAudio.Samples.Length - previousAudio.Samples.Length &&
            (expectExtension ? rule.ExtendedSamples > 0 : rule.ExtendedSamples == 0) && diagnostic.LeadSamples == timing[1].LeadingSamples &&
            acceptedPlan.Rendered!.Anchors[1].StartSample == previousPlan.Rendered!.Anchors[1].StartSample &&
            acceptedPlan.Rendered.Anchors[1].EndSample - previousPlan.Rendered.Anchors[1].EndSample == rule.ExtendedSamples &&
            acceptedPlan.Rendered.Anchors[2].StartSample - previousPlan.Rendered.Anchors[2].StartSample == rule.ExtendedSamples,
            "actual unit extension shifts subsequent placement only by actual added samples while placement lead and overlap contract remain fixed");
        check(acceptedPlan.Rendered!.Anchors[1].VowelOnsetSample == acceptedPlan.Rendered.Anchors[1].StartSample + diagnostic.MappedVowelOnset &&
            acceptedPlan.Rendered.Anchors[1].VowelOnsetSample < acceptedPlan.Rendered.Anchors[2].VowelOnsetSample,
            "F0-facing vowel anchors follow the real map and retain strict order across the next unit");
        var protectedGrains = diagnostic.Grains.Where(grain => rule.Segments.Any(part => part.Protected &&
            grain.OutputAt >= part.OutputStart && grain.OutputAt + grain.WindowSamples <= part.OutputEnd)).ToArray();
        check(protectedGrains.Length > 0 && protectedGrains.All(grain => grain.SourceStart == grain.NominalSourceStart),
            "fully contained attack/transition grains keep exact nominal source positions instead of searching a different phase");
        var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(new Plan(timing)) };
        await using var noDiagnostics = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank };
        var noDiagnosticsAudio = await noDiagnostics.SynthesizeAsync(text);
        var savedWave = await File.ReadAllBytesAsync(sourcePath);
        check(noDiagnosticsAudio.Samples.SequenceEqual(acceptedAudio.Samples) && noDiagnostics.UnitDiagnostics is null &&
            noDiagnostics.UnitDiagnosticAudio is null && originalWave.SequenceEqual(savedWave),
            "diagnostic capture cannot change normally generated unit-rule PCM or original recording and is disabled by default");
        await using var timingOnly = new UtauSpeech(new PcmProbe(), new TimingModeProvider(processor)) { Voicebank = bank, Mode = UtauSynthesisMode.Timing };
        var timingAudio = await timingOnly.SynthesizeAsync(text);
        var reference = await UtauSpeech.SynthesizeReferenceWithTimingPlanAsync(text, bank, timing);
        check(timingAudio.Samples.SequenceEqual(reference.Audio.Samples), "non-Context planned mode retains its previous waveform on eligible physical material");
    }

    private static async Task CheckContextFrictionAsync(Action<bool, string> check)
    {
        const int rate = 44100;
        using var fixture = new TestDirectory();
        var random = new Random(62831); var friction = new short[22050]; var vowel = new short[22050]; var stop = new short[22050];
        for (var i = 0; i < vowel.Length; i++)
        {
            vowel[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / rate));
            friction[i] = i < 5292 ? (short)(6000 * (0.5 + 0.5 * Math.Min(1, i / 882.0)) * (2 * random.NextDouble() - 1))
                : i < 6395 ? (short)random.Next(-2800, 2801) : vowel[i];
            stop[i] = i < 662 ? (short)(32 * Math.Sin(2 * Math.PI * 220 * i / rate))
                : i < 882 ? (short)random.Next(-6000, 6001) : vowel[i];
        }
        var frictionPath = Path.Combine(fixture.DirectoryPath, "friction.wav");
        var vowelPath = Path.Combine(fixture.DirectoryPath, "vowel.wav"); var stopPath = Path.Combine(fixture.DirectoryPath, "stop.wav");
        var originalFriction = new PcmAudio(friction, rate).ToWaveBytes(); var originalStop = new PcmAudio(stop, rate).ToWaveBytes();
        await File.WriteAllBytesAsync(frictionPath, originalFriction); await File.WriteAllBytesAsync(stopPath, originalStop);
        await File.WriteAllBytesAsync(vowelPath, new PcmAudio(vowel, rate).ToWaveBytes());
        var bank = new Voicebank("context friction fixture", fixture.DirectoryPath,
            [new(vowelPath, "あ", 0, 200, -400, 20, 10), new(vowelPath, "い", 0, 200, -400, 20, 10),
                new(frictionPath, "す", 10, 130, -310, 130, 20), new(stopPath, "と", 0, 200, -400, 40, 12),
                new(stopPath, "こ", 0, 200, -400, 40, 12), new(stopPath, "き", 0, 200, -400, 40, 12)], 0);
        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あすと", bank);
        var allocation = Allocations(ordinary);
        if (allocation.Length != 3) throw new InvalidDataException("Context friction fixture reading mismatch.");
        allocation[0] = allocation[0] with { PhoneticContext = new("sil", "a", "s", 2205, false) };
        allocation[1] = allocation[1] with { DurationSamples = 2646, LeadingSamples = 4410, PhoneticContext = new("s", "U", "t", 1323, true) };
        allocation[2] = allocation[2] with { LeadingSamples = 1764, PhoneticContext = new("t", "o", "sil", 2205, false) };
        var baseline = await RunFrictionFixture(bank, "あすと", allocation, enabled: false, captureAudio: true);
        var candidate = await RunFrictionFixture(bank, "あすと", allocation, enabled: true, captureAudio: true);
        var report = candidate.Diagnostic?.ContextFriction;
        check(report is { Eligible: true, Applied: true, BasedOnAdoptedUnitRule: true, NextPcmUnchanged: true } &&
            candidate.Audio.Samples.Length == baseline.Audio.Samples.Length && candidate.Anchors.SequenceEqual(baseline.Anchors) &&
            !candidate.Audio.Samples.SequenceEqual(baseline.Audio.Samples),
            "opt-in s/U/t uses recorded friction with unchanged duration and anchors: " + report?.Reason);
        check(report is not null && report.NextPcmSha256Before is { Length: 64 } && report.NextPcmSha256Before == report.NextPcmSha256After &&
            report.ClosureRms <= report.LaterLeadMaximumRms * 0.25 && report.LaterLeadMaximumRms >= 0.0001,
            "eligible following-stop energy contrast is observed and every intrinsic following-unit PCM byte is preserved");
        check(baseline.Diagnostic?.ContextFriction is null && candidate.Captured is not null && baseline.Captured is not null &&
            candidate.Captured.ShapedBeforePitch.SequenceEqual(baseline.Captured.ShapedBeforePitch) &&
            candidate.Diagnostic?.SourceRecovery is { Adopted: true },
            "context candidate preserves the original ShapeMora diagnostic stage and source-prefix recovery rather than relabeling its audio clone");
        var noDiagnostic = await RunFrictionFixture(bank, "あすと", allocation, true, false, diagnostics: false);
        check(noDiagnostic.Diagnostic is null && noDiagnostic.Captured is null &&
            noDiagnostic.Audio.Samples.SequenceEqual(candidate.Audio.Samples) && noDiagnostic.Anchors.SequenceEqual(candidate.Anchors),
            "ordinary friction rendering without diagnostic hash or grain collection preserves successful PCM and placement");
        var noCapture = await RunFrictionFixture(bank, "あすと", allocation, enabled: true, captureAudio: false);
        check(noCapture.Audio.Samples.SequenceEqual(candidate.Audio.Samples) && noCapture.Captured is null,
            "context candidate PCM is independent of audio diagnostic collection");

        var isolated = new[] { allocation[2] };
        var nextOnly = await RunFrictionFixture(bank, "と", isolated, enabled: false, captureAudio: false, target: 0);
        var nextStart = candidate.Anchors[2].StartSample; var skip = 221;
        check(candidate.Audio.Samples.AsSpan(nextStart + skip).SequenceEqual(nextOnly.Audio.Samples.AsSpan(skip)),
            "after the standalone utterance's first fade, following PCM in the candidate equals that unchanged isolated note without residual preceding-vowel contribution");

        var suk = allocation.ToArray(); suk[1] = suk[1] with { PhoneticContext = suk[1].PhoneticContext! with { NextPhone = "k" } };
        suk[2] = suk[2] with { Alias = "こ", PhoneticContext = new("k", "o", "sil", 2205, false) };
        var kCandidate = await RunFrictionFixture(bank, "あすこ", suk, enabled: true, captureAudio: false);
        check(kCandidate.Diagnostic?.ContextFriction is { Applied: true, NextPhone: "k" }, "s/U/k selects the same bounded rule without a word-name table");

        var voiced = allocation.ToArray(); voiced[1] = voiced[1] with { PhoneticContext = new("s", "u", "t", 1323, false) };
        await DeclinesFriction(bank, "あすと", voiced, check, "voiced lowercase u");
        var upperVoiced = allocation.ToArray(); upperVoiced[1] = upperVoiced[1] with { PhoneticContext = new("s", "U", "t", 1323, false) };
        var mixed = await RunFrictionFixture(bank, "あすと", upperVoiced, enabled: true, captureAudio: false);
        check(mixed.Diagnostic?.ContextFriction is { Applied: true, ModelVoicedFramesAbsent: false } &&
            mixed.Audio.Samples.SequenceEqual(candidate.Audio.Samples),
            "frontend-U approximation retains mixed model VUV as an observation without fitting a voiced-frame threshold");
        var other = allocation.ToArray(); other[1] = other[1] with { PhoneticContext = new("sh", "U", "t", 1323, true) };
        await DeclinesFriction(bank, "あすと", other, check, "other preceding consonant");
        var adjacent = allocation.ToArray(); adjacent[0] = adjacent[0] with { Alias = "い", PhoneticContext = new("sil", "I", "s", 2205, true) };
        await DeclinesFriction(bank, "いすと", adjacent, check, "preceding I/U");
        var nextUnvoiced = suk.ToArray(); nextUnvoiced[2] = nextUnvoiced[2] with { Alias = "き", PhoneticContext = new("k", "I", "sil", 2205, true) };
        await DeclinesFriction(bank, "あすき", nextUnvoiced, check, "following I/U");
        var mismatch = allocation.ToArray(); mismatch[2] = mismatch[2] with { PhoneticContext = new("k", "o", "sil", 2205, false) };
        await DeclinesFriction(bank, "あすと", mismatch, check, "different following model consonant");
        var final = allocation.Take(2).ToArray(); final[1] = final[1] with { PhoneticContext = new("s", "U", "sil", 1323, true) };
        await DeclinesFriction(bank, "あす", final, check, "phrase-final unit");
        var longVowel = allocation.ToArray(); longVowel[1] = longVowel[1] with { Extensions = 1 };
        await DeclinesFriction(bank, "あすーと", longVowel, check, "long vowel");
        var shortFriction = allocation.ToArray(); shortFriction[1] = shortFriction[1] with { DurationSamples = 6615 };
        await DeclinesFriction(bank, "あすと", shortFriction, check, "audible duration exceeds recorded friction");
        var uncertainBank = new Voicebank("closure uncertainty", fixture.DirectoryPath,
            bank.Entries.Select(entry => entry.Alias == "と" ? entry with { WavePath = vowelPath } : entry).ToArray(), 0);
        await DeclinesFriction(uncertainBank, "あすと", allocation, check, "unconfirmed following closure");
        var timingCandidate = await RunFrictionFixture(bank, "あすと", allocation, enabled: true, captureAudio: false, timingOnly: true);
        var timingBaseline = await RunFrictionFixture(bank, "あすと", allocation, enabled: false, captureAudio: false, timingOnly: true);
        check(timingCandidate.Diagnostic?.ContextFriction is null && timingCandidate.Audio.Samples.SequenceEqual(timingBaseline.Audio.Samples),
            "non-Context modes do not use the phonetic friction candidate");
        // Distinct from the old 15ms-low fixture: the OTO crop removes most
        // of a 6ms quiet prefix, followed immediately by a rising burst.
        var croppedStop = new short[22050];
        for (var i = 0; i < croppedStop.Length; i++)
        {
            var amplitude = i < 265 ? 32.0 : i < 441 ? 32 + 5968.0 * (i - 265) / 176 : 6000;
            croppedStop[i] = (short)(amplitude * Math.Sin(2 * Math.PI * 220 * i / rate));
        }
        var croppedPath = Path.Combine(fixture.DirectoryPath, "cropped-stop.wav");
        var originalCropped = new PcmAudio(croppedStop, rate).ToWaveBytes();
        await File.WriteAllBytesAsync(croppedPath, originalCropped);
        var croppedBank = new Voicebank("recoverable next closure", fixture.DirectoryPath,
            bank.Entries.Select(entry => entry.Alias == "と" ? entry with { WavePath = croppedPath, Offset = 5, Preutterance = 23 } : entry).ToArray(), 0);
        var croppedBaseline = await RunFrictionFixture(croppedBank, "あすと", allocation, false, false);
        var recoveredPair = await RunFrictionFixture(croppedBank, "あすと", allocation, true, false);
        var recoveredReport = recoveredPair.Diagnostic?.ContextFriction;
        check(recoveredReport is { Applied: true, NextPcmUnchanged: false, RecoveredNextPrefixSamples: > 0, NextAbsoluteLandmarksUnchanged: true } &&
            recoveredReport.NextPcmSha256Before != recoveredReport.NextPcmSha256After &&
            recoveredReport.OriginalClosureRms > recoveredReport.OriginalLaterLeadMaximumRms * 0.25 &&
            recoveredReport.ClosureRms <= recoveredReport.LaterLeadMaximumRms * 0.25 &&
            recoveredPair.Audio.Samples.Length == croppedBaseline.Audio.Samples.Length && recoveredPair.Anchors.SequenceEqual(croppedBaseline.Anchors),
            "quiet prefix redraw and friction replacement are adopted together with unchanged placement and absolute landmarks: " +
            recoveredReport?.Reason + " / " + recoveredReport?.NextPrefixRecoveryReason);
        var recoveredNoDiagnostic = await RunFrictionFixture(croppedBank, "あすと", allocation, true, false, diagnostics: false);
        check(recoveredNoDiagnostic.Diagnostic is null && recoveredNoDiagnostic.Audio.Samples.SequenceEqual(recoveredPair.Audio.Samples) &&
            recoveredNoDiagnostic.Anchors.SequenceEqual(recoveredPair.Anchors),
            "ordinary paired redraw without diagnostic hash or grains preserves successful PCM and placement");
        var recoveredNoCapture = await RunFrictionFixture(croppedBank, "あすと", allocation, true, true);
        check(recoveredNoCapture.Audio.Samples.SequenceEqual(recoveredPair.Audio.Samples), "paired next-prefix recovery is independent of diagnostic PCM capture");
        var remainingCropped = await File.ReadAllBytesAsync(croppedPath);
        check(originalCropped.SequenceEqual(remainingCropped), "paired next-prefix recovery does not alter the original stop recording");
        var strongPrefix = (short[])croppedStop.Clone();
        for (var i = 0; i < 441; i++) strongPrefix[i] = (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / rate));
        var strongPath = Path.Combine(fixture.DirectoryPath, "strong-prefix.wav");
        await File.WriteAllBytesAsync(strongPath, new PcmAudio(strongPrefix, rate).ToWaveBytes());
        var strongBank = new Voicebank("no quiet next prefix", fixture.DirectoryPath,
            croppedBank.Entries.Select(entry => entry.Alias == "と" ? entry with { WavePath = strongPath } : entry).ToArray(), 0);
        await DeclinesFriction(strongBank, "あすと", allocation, check, "no contiguous weak pre-crop source prefix");
        var remainingFriction = await File.ReadAllBytesAsync(frictionPath);
        var remainingStop = await File.ReadAllBytesAsync(stopPath);
        check(originalFriction.SequenceEqual(remainingFriction) && originalStop.SequenceEqual(remainingStop),
            "context comparisons preserve original source and following-unit recordings");
        await CheckPhoneticSnapshotValidation(bank, allocation, check);
    }

    private sealed record FrictionFixtureResult(PcmAudio Audio, IReadOnlyList<UtauTimingAnchor> Anchors,
        UtauUnitDiagnostic? Diagnostic, UtauUnitDiagnosticAudio? Captured);

    private static async Task<FrictionFixtureResult> RunFrictionFixture(Voicebank bank, string text,
        UtauTimingAllocation[] timing, bool enabled, bool captureAudio, int target = 1, bool timingOnly = false, bool diagnostics = true)
    {
        var plan = new Plan(timing); var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan) };
        IUtauPcmPostprocessor outputProcessor = timingOnly ? new TimingModeProvider(processor) : processor;
        await using var speech = enabled
            ? new UtauSpeech(new PcmProbe(), outputProcessor) { Voicebank = bank, Mode = timingOnly ? UtauSynthesisMode.Timing : UtauSynthesisMode.Context }
            : new UtauSpeech(new PcmProbe(), outputProcessor) { Voicebank = bank, DisableContextFrictionForComparison = true, Mode = timingOnly ? UtauSynthesisMode.Timing : UtauSynthesisMode.Context };
        if (diagnostics) speech.EnableUnitDiagnostics(target, captureAudio);
        var audio = await speech.SynthesizeAsync(text);
        return new(audio, plan.Rendered?.Anchors ?? Array.Empty<UtauTimingAnchor>(), speech.UnitDiagnostics, speech.UnitDiagnosticAudio);
    }

    private static async Task DeclinesFriction(Voicebank bank, string text, UtauTimingAllocation[] timing,
        Action<bool, string> check, string condition)
    {
        var before = await RunFrictionFixture(bank, text, timing, false, false);
        var after = await RunFrictionFixture(bank, text, timing, true, false);
        var ordinary = await RunFrictionFixture(bank, text, timing, true, false, diagnostics: false);
        check(ordinary.Diagnostic is null && ordinary.Audio.Samples.SequenceEqual(after.Audio.Samples) && ordinary.Anchors.SequenceEqual(after.Anchors),
            "diagnostic-free friction fallback preserves PCM and placement on " + condition);
        check(after.Diagnostic?.ContextFriction is { Applied: false } && after.Audio.Samples.SequenceEqual(before.Audio.Samples) &&
            after.Anchors.SequenceEqual(before.Anchors), "context candidate preserves baseline on " + condition + ": " + after.Diagnostic?.ContextFriction?.Reason);
    }

    private static async Task CheckPhoneticSnapshotValidation(Voicebank bank, UtauTimingAllocation[] timing, Action<bool, string> check)
    {
        var valid = timing[1].PhoneticContext!;
        foreach (var invalid in new[] { valid with { PreviousPhone = "s\n" }, valid with { VowelPhone = new string('U', 32) },
            valid with { NextPhone = "" }, valid with { NextPhone = "あ" }, valid with { VowelDurationSamples = 0 },
            valid with { VowelDurationSamples = 44100 * 45 + 1 } })
        {
            var allocations = timing.ToArray(); allocations[1] = allocations[1] with { PhoneticContext = invalid };
            check(await CaptureAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync("あすと", bank, allocations)) is InvalidDataException,
                "explicit timing snapshot rejects invalid phonetic symbols or vowel duration");
            var plan = new Plan(allocations); var processor = new Processor { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan) };
            await using var speech = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank, DisableContextFrictionForComparison = false };
            var fallback = 0; speech.ProsodyFallback += _ => fallback++;
            var audio = await speech.SynthesizeAsync("あすと");
            var reference = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あすと", bank);
            check(fallback == 1 && plan.Calls == 0 && audio.Samples.SequenceEqual(reference.Audio.Samples),
                "invalid external-provider phonetics use the existing ordinary fallback without partially applying the candidate");
        }
        var pause = timing.ToArray(); pause[1] = pause[1] with { Alias = null, Pause = '、', LeadingSamples = null };
        check(await CaptureAsync(() => UtauSpeech.SynthesizeReferenceWithTimingPlanAsync("あ、と", bank, pause)) is InvalidDataException,
            "phonetic metadata cannot be attached to a pause in the explicit timing entrance");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        await using var cancelled = new UtauSpeech(new PcmProbe()) { Voicebank = bank, DisableContextFrictionForComparison = false };
        check(await CaptureAsync(() => cancelled.SynthesizeAsync("あすと", stop.Token)) is OperationCanceledException,
            "context friction comparison preserves explicit cancellation rather than starting a fallback");
    }

    private static async Task CheckFallbacksAsync(Voicebank bank, Action<bool, string> check)
    {
        const string text = "あい";
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        var timing = Allocations(baseline);
        var wrongAlias = timing.ToArray(); wrongAlias[0] = wrongAlias[0] with { Alias = "い" };
        var shortVowel = timing.ToArray(); shortVowel[0] = shortVowel[0] with { DurationSamples = 1 };
        // First vowel 40ms cannot cover the next recording's 90ms lead.
        var shortForNextPre = timing.ToArray(); shortForNextPre[0] = shortForNextPre[0] with { DurationSamples = 1764 };
        var oversized = new OversizedTiming();
        var rejected = new[] { new Plan(wrongAlias), new Plan(shortVowel), new Plan(shortForNextPre),
            new Plan(oversized), new Plan(null!),
            new Plan(timing) { Process = (rendered, _, _) => { Array.Fill(rendered.Audio.Samples, (short)0); throw new IOException("context failed after modifying its own buffer"); } },
            new Plan(timing) { Process = (rendered, _, _) => Task.FromResult(new PcmAudio(rendered.Audio.Samples, 22050)) },
            new Plan(timing) { Process = (rendered, _, _) => Task.FromResult(new PcmAudio(rendered.Audio.Samples[..^1], 44100)) } };
        foreach (var plan in rejected)
        {
            var processor = new Processor(); var output = new PcmProbe(); var reports = new List<Exception>();
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(processor.Creates == 1 ? plan : null);
            await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
            voice.ProsodyFallback += _ => throw new IOException("view subscriber failure");
            voice.ProsodyFallback += reports.Add;
            var result = await voice.SynthesizeAsync(text);
            check(result.Samples.SequenceEqual(baseline.Audio.Samples) && processor.FlatCalls == 1 && reports.Count == 1 && output.Calls == 0,
                "invalid reading short transition next-pre bound bad metadata or failed context processing rerenders ordinary B before one flat fallback");
            check(processor.LastFlat!.Samples.SequenceEqual(baseline.Audio.Samples),
                "fallback uses newly rendered ordinary PCM rather than a partially modified context buffer");
            var next = await voice.SynthesizeAsync(text);
            check(next.Samples.SequenceEqual(baseline.Audio.Samples) && reports.Count == 1 && processor.FlatCalls == 2,
                "failed or declined context plans leave no timing or anchor fields behind for the next utterance");
        }
        check(!oversized.Accessed, "oversized context timing is refused before allocation indexing or enumeration");

        var failing = new Processor { Create = (_, _, _) => throw new IOException("plan creation failed") };
        var diagnostic = 0;
        await using (var voice = new UtauSpeech(new PcmProbe(), failing) { Voicebank = bank })
        {
            voice.ProsodyFallback += _ => diagnostic++;
            check((await voice.SynthesizeAsync(text)).Samples.SequenceEqual(baseline.Audio.Samples) && diagnostic == 1 && failing.FlatCalls == 1,
                "plan creation failure reports one diagnostic and retains ordinary UTAU instead of immediately selecting OS speech");
            failing.Flat = (_, _, _) => throw new IOException("flat engine unavailable");
            check(await CaptureAsync(() => voice.SynthesizeAsync(text)) is IOException,
                "failure of the ordinary flat fallback still propagates to the existing OS fallback boundary");
        }
        var declined = new Processor();
        await using (var voice = new UtauSpeech(new PcmProbe(), declined) { Voicebank = bank })
        {
            voice.ProsodyFallback += _ => diagnostic++;
            var previous = diagnostic;
            check((await voice.SynthesizeAsync(text)).Samples.SequenceEqual(baseline.Audio.Samples) && diagnostic == previous && declined.FlatCalls == 1,
                "an explicit null plan declines context without a failure diagnostic and preserves exact normal B PCM");
        }
    }

    private static async Task CheckCancellationAsync(Voicebank bank, Action<bool, string> check)
    {
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あ", bank); var timing = Allocations(baseline);
        foreach (var duringCreation in new[] { true, false })
        {
            using var stop = new CancellationTokenSource();
            var output = new PcmProbe(); var processor = new Processor(); var reports = 0;
            var plan = new Plan(timing) { Process = (_, _, _) => { stop.Cancel(); throw new IOException("late context failure"); } };
            processor.Create = (_, _, _) =>
            {
                if (duringCreation) { stop.Cancel(); throw new IOException("late planning failure"); }
                return Task.FromResult<IUtauUtterancePlan?>(plan);
            };
            await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
            voice.ProsodyFallback += _ => reports++;
            check(await CaptureAsync(() => voice.SpeakAsync("あ", stop.Token)) is OperationCanceledException &&
                reports == 0 && processor.FlatCalls == 0 && output.Calls == 0,
                "user cancellation wins over late planning or context errors without a fallback diagnostic flat synthesis or playback");
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(null);
            check((await voice.SynthesizeAsync("あ")).Samples.SequenceEqual(baseline.Audio.Samples),
                "cancelled context processing restores the ordinary timeline for a later utterance");
        }

        var entered = Signal(); var outputAtExit = new PcmProbe(); var processorAtExit = new Processor
        {
            Create = async (_, _, ct) => { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); return null; }
        };
        var shuttingDown = new UtauSpeech(outputAtExit, processorAtExit) { Voicebank = bank };
        var fallbackReports = 0; shuttingDown.ProsodyFallback += _ => fallbackReports++;
        var pending = shuttingDown.SpeakAsync("あ", default);
        try
        {
            await entered.Task.WaitAsync(Limit);
            await shuttingDown.DisposeAsync().AsTask().WaitAsync(Limit);
            check(await CaptureAsync(() => pending) is OperationCanceledException && fallbackReports == 0 &&
                processorAtExit.FlatCalls == 0 && outputAtExit.Calls == 0 && processorAtExit.Disposes == 1 && outputAtExit.Disposes == 1,
                "shutdown cancels planning before owned cleanup without synthesizing fallback or playing late audio");
        }
        finally { await shuttingDown.DisposeAsync(); }
    }

    private static async Task CheckOrdinaryTimingAsync(Voicebank bank, Action<bool, string> check)
    {
        foreach (var nextPreGuard in new[] { false, true })
        {
            var text = nextPreGuard ? "あい" : "アー、あっい。";
            var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
            var timing = Allocations(baseline);
            timing[0] = timing[0] with { DurationSamples = nextPreGuard ? 1764 : 1 };
            var output = new PcmProbe(); var processor = new Processor();
            var notices = new List<UtauTimingPlanException>(); var flatNotices = 0;
            var notifiedBeforeProcessing = false;
            var plan = new Plan(timing)
            {
                CanProcessOrdinaryTiming = true,
                Process = (rendered, _, _) => { notifiedBeforeProcessing = notices.Count != 0; return Task.FromResult(rendered.Audio); }
            };
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(processor.Creates == 1 ? plan : null);
            await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
            voice.ProsodyFallback += _ => flatNotices++;
            voice.ProsodyTimingFallback += _ => throw new IOException("timing view failure");
            voice.ProsodyTimingFallback += notices.Add;
            var result = await voice.SynthesizeAsync(text);
            check(result.Samples.SequenceEqual(baseline.Audio.Samples) && plan.Rendered!.Anchors.SequenceEqual(baseline.Anchors) &&
                plan.Rendered.Audio.Samples.Length == baseline.Audio.Samples.Length && plan.Rendered.SourceRanges.Count == 0 &&
                plan.Calls == 1 && processor.FlatCalls == 0 && flatNotices == 0,
                "an opted-in renderer timing rejection supplies exact ordinary B PCM and actual anchors to one context pass without flat processing");
            check(notices.Count == 1 && !notifiedBeforeProcessing && notices[0].UnitIndex == 0 &&
                notices[0].RequestedSamples == timing[0].DurationSamples && notices[0].MinimumSamples > notices[0].RequestedSamples,
                "ordinary timing success is reported only after context processing and retains the original source guard diagnostic");
            check((await voice.SynthesizeAsync(text)).Samples.SequenceEqual(baseline.Audio.Samples) && notices.Count == 1 && processor.FlatCalls == 1,
                "ordinary timing retry clears partial planned anchors and restores the next utterance's default timeline");
        }

        var ordinary = await UtauSpeech.SynthesizeReferenceWithTimingAsync("あい", bank);
        var valid = Allocations(ordinary); var wrong = valid.ToArray(); wrong[0] = wrong[0] with { Alias = "い" };
        var shortPlan = valid.ToArray(); shortPlan[0] = shortPlan[0] with { DurationSamples = 1 };
        var plans = new[]
        {
            new Plan(wrong) { CanProcessOrdinaryTiming = true },
            // This exception is thrown by the processor, not by RenderMora.
            new Plan(valid) { CanProcessOrdinaryTiming = true, Process = (_, _, _) => throw new UtauTimingPlanException(0, 1, 882) },
            new Plan(shortPlan) { CanProcessOrdinaryTiming = true, Process = (rendered, _, _) =>
                { Array.Fill(rendered.Audio.Samples, (short)0); throw new IOException("ordinary context failed"); } }
        };
        foreach (var plan in plans)
        {
            var notices = 0; var errors = 0; var processor = new Processor
            { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan) };
            await using var voice = new UtauSpeech(new PcmProbe(), processor) { Voicebank = bank };
            voice.ProsodyTimingFallback += _ => notices++;
            voice.ProsodyFallback += _ => errors++;
            check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(ordinary.Audio.Samples) &&
                notices == 0 && errors == 1 && processor.FlatCalls == 1,
                "reading mismatch or context processing failure uses fresh flat fallback and cannot report a successful ordinary-timing contour");
            check(plan.Calls == (ReferenceEquals(plan, plans[0]) ? 0 : 1),
                "only the renderer timing guard permits an ordinary retry; a processor's typed exception never retries context processing");
        }
        foreach (var lateFailure in new[] { false, true })
        {
            using var stop = new CancellationTokenSource(); var notices = 0; var errors = 0;
            var output = new PcmProbe(); var processor = new Processor();
            var plan = new Plan(shortPlan)
            {
                CanProcessOrdinaryTiming = true,
                Process = (rendered, _, _) =>
                {
                    stop.Cancel();
                    if (lateFailure) throw new IOException("late ordinary context failure");
                    return Task.FromResult(rendered.Audio);
                }
            };
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan);
            await using var voice = new UtauSpeech(output, processor) { Voicebank = bank };
            voice.ProsodyTimingFallback += _ => notices++;
            voice.ProsodyFallback += _ => errors++;
            check(await CaptureAsync(() => voice.SpeakAsync("あい", stop.Token)) is OperationCanceledException &&
                notices == 0 && errors == 0 && processor.FlatCalls == 0 && output.Calls == 0 && plan.Calls == 1,
                "cancelled ordinary-timing context never emits success or flat fallback even after a late result or failure");
            processor.Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(null);
            check((await voice.SynthesizeAsync("あい")).Samples.SequenceEqual(ordinary.Audio.Samples),
                "cancellation during ordinary-timing context leaves no partial settings for later speech");
        }
    }

    private static async Task CheckSourceInspectionCacheAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var bank = await MakeBankAsync(temporary.DirectoryPath);
        const string text = "あいあい";
        var reference = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        var timing = Allocations(reference);
        var cachedPlan = new Plan(timing); var oldPlan = new Plan(timing); var diagnosticPlan = new Plan(timing);
        Processor WithPlan(Plan plan) => new() { Create = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(plan) };
        await using var cached = new UtauSpeech(new PcmProbe(), WithPlan(cachedPlan))
            { Voicebank = bank, EnableSourceInspectionDiagnostics = true };
        await using var old = new UtauSpeech(new PcmProbe(), WithPlan(oldPlan))
            { Voicebank = bank, DisableSourceInspectionCacheForComparison = true, EnableSourceInspectionDiagnostics = true };
        await using var diagnostic = new UtauSpeech(new PcmProbe(), WithPlan(diagnosticPlan)) { Voicebank = bank };
        diagnostic.EnableUnitDiagnostics(0);
        var actual = await cached.SynthesizeAsync(text); var expected = await old.SynthesizeAsync(text);
        var observed = await diagnostic.SynthesizeAsync(text);
        check(actual.Samples.SequenceEqual(expected.Samples) && actual.Samples.SequenceEqual(observed.Samples) &&
            cachedPlan.Rendered!.Anchors.SequenceEqual(oldPlan.Rendered!.Anchors) &&
            cachedPlan.Rendered!.Anchors.SequenceEqual(diagnosticPlan.Rendered!.Anchors),
            "source inspection reuse preserves planned PCM and anchors with cache bypass and unit diagnostics");
        check(cached.SourceInspectionAnalysisCount == 2 && cached.SourceInspectionCacheHitCount == 2 &&
            old.SourceInspectionAnalysisCount == 4 && old.SourceInspectionCacheHitCount == 0 &&
            diagnostic.SourceInspectionAnalysisCount == 0 && diagnostic.SourceInspectionCacheHitCount == 0,
            "source analysis runs once per repeated OTO while counters require explicit diagnostics");
        await cached.SynthesizeAsync(text);
        check(cached.SourceInspectionAnalysisCount == 2 && cached.SourceInspectionCacheHitCount == 6,
            "the next utterance reuses validated decoded-source inspection");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            check(await CaptureAsync(() => cached.SynthesizeAsync(text, stop.Token)) is OperationCanceledException &&
                cached.SourceInspectionCacheHitCount == 6,
                "cancelled speech cannot consume a cached source inspection");
        }
        var shortTiming = timing.Select(unit => unit with { DurationSamples = 1 }).ToArray();
        var cachedFallbacks = 0; var oldFallbacks = 0;
        var cachedFallbackPlan = new Plan(shortTiming) { CanProcessOrdinaryTiming = true };
        var oldFallbackPlan = new Plan(shortTiming) { CanProcessOrdinaryTiming = true };
        await using (var fallbackCached = new UtauSpeech(new PcmProbe(), WithPlan(cachedFallbackPlan)) { Voicebank = bank })
        await using (var fallbackOld = new UtauSpeech(new PcmProbe(), WithPlan(oldFallbackPlan))
            { Voicebank = bank, DisableSourceInspectionCacheForComparison = true })
        {
            fallbackCached.ProsodyTimingFallback += _ => cachedFallbacks++;
            fallbackOld.ProsodyTimingFallback += _ => oldFallbacks++;
            var fallbackActual = await fallbackCached.SynthesizeAsync(text);
            var fallbackExpected = await fallbackOld.SynthesizeAsync(text);
            check(fallbackActual.Samples.SequenceEqual(fallbackExpected.Samples) && cachedFallbacks == 1 && oldFallbacks == 1 &&
                cachedFallbackPlan.Rendered!.Anchors.SequenceEqual(oldFallbackPlan.Rendered!.Anchors),
                "inspection reuse preserves ordinary-timing fallback PCM anchors and notification count");
        }
        // Borrow one WAV through distinct immutable OTOs. Only the most recent
        // OTO may remain attached to that decoded source.
        await using var plain = new UtauSpeech(new PcmProbe())
            { Voicebank = bank, ApplyPitch = false, EnableSourceInspectionDiagnostics = true };
        await plain.SynthesizeAsync("あ");
        var changedOto = bank.Entries[0] with { Preutterance = 30 };
        var changedBank = bank with { Entries = new[] { changedOto, bank.Entries[1] } };
        plain.Voicebank = changedBank;
        var changed = await plain.SynthesizeAsync("あ");
        await using (var baseline = new UtauSpeech(new PcmProbe())
            { Voicebank = changedBank, ApplyPitch = false, DisableSourceInspectionCacheForComparison = true })
        {
            var changedExpected = await baseline.SynthesizeAsync("あ");
            check(changed.Samples.SequenceEqual(changedExpected.Samples),
                "a changed OTO on the same WAV receives its own original analysis and waveform");
        }
        plain.Voicebank = bank; await plain.SynthesizeAsync("あ");
        check(plain.SourceInspectionAnalysisCount == 3 && plain.SourceInspectionCacheHitCount == 0,
            "one decoded WAV retains only its latest OTO inspection");
        var sourcePath = bank.Entries[0].WavePath;
        var bytes = await File.ReadAllBytesAsync(sourcePath);
        var stamp = File.GetLastWriteTimeUtc(sourcePath);
        File.SetLastWriteTimeUtc(sourcePath, stamp.AddSeconds(2));
        await plain.SynthesizeAsync("あ");
        check(plain.SourceInspectionAnalysisCount == 4, "changed WAV stamps invalidate attached source inspection");
        bytes = new PcmAudio(Enumerable.Range(0, 22051)
            .Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray(), 44100).ToWaveBytes();
        await File.WriteAllBytesAsync(sourcePath, bytes);
        File.SetLastWriteTimeUtc(sourcePath, stamp.AddSeconds(2));
        await plain.SynthesizeAsync("あ");
        check(plain.SourceInspectionAnalysisCount == 5, "changed WAV length invalidates inspection even with the same stamp");
        var reading = new[] { new UtauReadingUnit("あ", 0, '\0') };
        await plain.CreateDirectPlanAsync(reading, bank, null, CancellationToken.None);
        var afterHash = plain.SourceInspectionAnalysisCount;
        await plain.CreateDirectPlanAsync(reading, bank, null, CancellationToken.None);
        check(plain.SourceInspectionAnalysisCount == afterHash && plain.SourceInspectionCacheHitCount > 0,
            "direct source content validation precedes inspection reuse");
        var unchangedStamp = File.GetLastWriteTimeUtc(sourcePath);
        bytes[44] ^= 1;
        await File.WriteAllBytesAsync(sourcePath, bytes);
        File.SetLastWriteTimeUtc(sourcePath, unchangedStamp);
        await plain.CreateDirectPlanAsync(reading, bank, null, CancellationToken.None);
        check(plain.SourceInspectionAnalysisCount == afterHash + 1,
            "same-size same-stamp content edits invalidate direct-source inspection through its hash");
        // Cross the existing path-count bound, then revisit the original. Each
        // fixture is short and owned by this TestDirectory.
        for (var i = 0; i < 64; i++)
        {
            var path = Path.Combine(temporary.DirectoryPath, $"cache-{i}.wav");
            await File.WriteAllBytesAsync(path, bytes);
            plain.Voicebank = bank with { Entries = new[] { bank.Entries[0] with { WavePath = path } } };
            await plain.SynthesizeAsync("あ");
        }
        plain.Voicebank = bank;
        var beforeRevisit = plain.SourceInspectionAnalysisCount;
        await plain.SynthesizeAsync("あ");
        check(plain.SourceInspectionAnalysisCount == beforeRevisit + 1,
            "decoded-cache path-bound clearing also releases prior source inspection");
    }

    private static UtauTimingAllocation[] Allocations(UtauReferenceTiming source) => source.Anchors.Select(unit =>
        new UtauTimingAllocation(unit.Alias, unit.Pause, unit.Extensions, unit.EndSample - (unit.VowelOnsetSample ?? unit.StartSample))).ToArray();
    private static async Task<Voicebank> MakeBankAsync(string directory)
    {
        var entries = new List<OtoEntry>();
        foreach (var (alias, pre) in new[] { ("あ", 20), ("い", 90) })
        {
            var path = Path.Combine(directory, alias + ".wav");
            var pcm = Enumerable.Range(0, 22050).Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 220 * i / 44100))).ToArray();
            await File.WriteAllBytesAsync(path, new PcmAudio(pcm, 44100).ToWaveBytes());
            entries.Add(new(path, alias, 0, 200, -400, pre, 10));
        }
        return new("context fixture", directory, entries, 0);
    }
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action().WaitAsync(Limit); return null; } catch (Exception error) { return error; } }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class TimingModeProvider(IUtauPcmPostprocessor processor) : IUtauPcmPostprocessor, IUtauModeProvider
    {
        public IReadOnlyList<UtauModeChoice> Modes { get; } = Array.AsReadOnly(new[] { new UtauModeChoice(UtauSynthesisMode.Timing, "fake planned timing") });
        public IReadOnlyList<Exception> InitializationErrors => Array.Empty<Exception>();
        public IUtauPcmPostprocessor? Resolve(UtauSynthesisMode mode) => mode == UtauSynthesisMode.Timing
            ? processor : throw new ArgumentOutOfRangeException(nameof(mode));
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => processor.ProcessAsync(input, bank, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask; // Borrows the fake processor owned by other fixture voices.
    }

    private sealed class Processor : IUtauProsodyPostprocessor
    {
        public Func<string, IReadOnlyList<UtauReadingUnit>, CancellationToken, Task<IUtauUtterancePlan?>> Create { get; set; }
            = (_, _, _) => Task.FromResult<IUtauUtterancePlan?>(null);
        public Func<PcmAudio, Voicebank, CancellationToken, Task<PcmAudio>> Flat { get; set; } = (audio, _, _) => Task.FromResult(audio);
        public int Creates { get; private set; } public int FlatCalls { get; private set; } public int Disposes { get; private set; }
        public PcmAudio? LastFlat { get; private set; }
        public Task<IUtauUtterancePlan?> CreatePlanAsync(string text, IReadOnlyList<UtauReadingUnit> reading, CancellationToken ct)
        { Creates++; return Create(text, reading, ct); }
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct)
        { FlatCalls++; LastFlat = input; return Flat(input, bank, ct); }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
    private sealed class Plan(IReadOnlyList<UtauTimingAllocation> timing) : IUtauUtterancePlan
    {
        public IReadOnlyList<UtauTimingAllocation> Timing => timing;
        public bool CanProcessOrdinaryTiming { get; init; }
        public Func<UtauReferenceTiming, Voicebank, CancellationToken, Task<PcmAudio>> Process { get; init; }
            = (rendered, _, _) => Task.FromResult(rendered.Audio);
        public int Calls { get; private set; } public UtauReferenceTiming? Rendered { get; private set; } public Voicebank? Bank { get; private set; }
        public Task<PcmAudio> ProcessAsync(UtauReferenceTiming rendered, Voicebank bank, CancellationToken ct)
        { Calls++; Rendered = rendered; Bank = bank; return Process(rendered, bank, ct); }
    }
    private sealed class PcmProbe : IPcmOutput
    {
        public int Calls { get; private set; } public int Disposes { get; private set; } public PcmAudio? Last { get; private set; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; Last = new(samples.ToArray(), sampleRate); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
    private sealed class IndexedTiming(IReadOnlyList<UtauTimingAllocation> source) : IReadOnlyList<UtauTimingAllocation>
    {
        public int Count => source.Count; public UtauTimingAllocation this[int index] => source[index];
        public IEnumerator<UtauTimingAllocation> GetEnumerator() => throw new InvalidOperationException("Use bounded indexes.");
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    private sealed class OversizedTiming : IReadOnlyList<UtauTimingAllocation>
    {
        public bool Accessed { get; private set; } public int Count => int.MaxValue;
        public UtauTimingAllocation this[int index] { get { Accessed = true; throw new InvalidOperationException(); } }
        public IEnumerator<UtauTimingAllocation> GetEnumerator() { Accessed = true; throw new InvalidOperationException(); }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}

