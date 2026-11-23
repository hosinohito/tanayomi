using Tanayomi;

internal static class UtauDirectPlanTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var entries = new List<OtoEntry>();
        foreach (var (alias, hz) in new[] { ("あ", 220), ("い", 330) })
        {
            var path = Path.Combine(temporary.DirectoryPath, alias + ".wav");
            var wave = new PcmAudio(Enumerable.Range(0, 22050).Select(i =>
                (short)(6000 * Math.Sin(2 * Math.PI * hz * i / 44100))).ToArray(), 44100);
            await File.WriteAllBytesAsync(path, wave.ToWaveBytes());
            entries.Add(new(path, alias, 0, 200, -400, 20, 10));
        }
        var bank = new Voicebank("direct plan fixture", temporary.DirectoryPath, entries, 0);
        const string text = "あー、いっあいあい。";
        var reading = await UtauDirectPlanner.ReadAsync(text);
        await using var planner = new UtauDirectPlanner();
        var actual = await planner.CreateAsync(reading, bank);
        var baseline = await UtauSpeech.SynthesizeReferenceWithTimingAsync(text, bank);
        check(actual.SampleRate == 44100 && actual.SampleCount == baseline.Audio.Samples.Length &&
            actual.Anchors.SequenceEqual(baseline.Anchors) && actual.Reading.SequenceEqual(reading),
            "direct source planning obtains exact ordinary placements including long vowels pauses and closure without requiring rendered PCM");
        check(actual.Units.All(unit => unit.Source is { } source
                ? source.Samples.Length == 22050 && source.StartSample <= source.PreutteranceSample &&
                    source.PreutteranceSample <= source.StableSample && source.StableSample < source.EndSample &&
                    source.EndSample <= source.OtoEndSample && source.OtoEndSample <= source.Samples.Length &&
                    source.ReferencePeriodSamples > 0 && unit.EndSample - unit.StartSample == unit.LeadSamples + unit.DurationSamples
                : unit.VowelOnsetSample is null && unit.LeadSamples == 0 && unit.OverlapSamples == 0),
            "direct plans expose read-only original snapshots OTO bounds period and separate lead transition duration and actual overlap");
        var timing = actual.Units.Select(unit => new UtauTimingAllocation(unit.Reading.Alias, unit.Reading.Pause,
            unit.Reading.Extensions, unit.DurationSamples)).ToArray();
        var same = await planner.CreateAsync(reading, bank, timing);
        check(same.Anchors.SequenceEqual(actual.Anchors),
            "an explicit direct timing plan preserves ordinary placements without regenerating original-source output");
        var longer = timing.ToArray(); longer[0] = longer[0] with { DurationSamples = longer[0].DurationSamples + 441 };
        var extended = await planner.CreateAsync(reading, bank, longer);
        check(extended.SampleCount == actual.SampleCount + 441 && extended.Units[0].LeadSamples == actual.Units[0].LeadSamples &&
            extended.Units.Skip(1).Zip(actual.Units.Skip(1)).All(pair => pair.First.StartSample == pair.Second.StartSample + 441),
            "direct duration allocation shifts later placements exactly without rearticulating a long vowel");
        var shortTiming = timing.ToArray(); shortTiming[0] = shortTiming[0] with { DurationSamples = 1 };
        var timingError = await CaptureAsync(() => planner.CreateAsync(reading, bank, shortTiming));
        check(timingError is UtauTimingPlanException { UnitIndex: 0 } limitation && limitation.MinimumSamples > 1,
            "direct timing below the original consonant transition limit reports the unit and required minimum for timing fallback");
        var mismatched = timing.ToArray(); mismatched[0] = mismatched[0] with { Alias = "い" };
        check(await CaptureAsync(() => planner.CreateAsync(reading, bank, mismatched)) is InvalidDataException &&
            await CaptureAsync(() => planner.CreateAsync(reading, bank, timing[..^1])) is InvalidDataException,
            "direct timing rejects reading mismatch and changed unit counts before source generation");
        check(await CaptureAsync(() => planner.CreateAsync([new("あ", 1, '、')], bank)) is InvalidDataException &&
            await CaptureAsync(() => planner.CreateAsync([new(null, 0, '。')], bank)) is InvalidDataException &&
            await CaptureAsync(() => planner.CreateAsync(new OversizedReading(), bank)) is InvalidDataException,
            "direct planning rejects malformed silent-only and unbounded reading providers without enumerating them");
        check(await CaptureAsync(() => planner.CreateAsync([new("あ", 0, '\0')], bank with
            { Entries = [entries[0] with { Preutterance = double.NaN }] })) is InvalidDataException &&
            await CaptureAsync(() => planner.CreateAsync([new("う", 0, '\0')], bank)) is InvalidDataException,
            "direct planning retains finite OTO and required-alias guards instead of accepting an invalid source");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        check(await CaptureAsync(() => planner.CreateAsync(reading, bank, ct: canceled.Token)) is OperationCanceledException &&
            await CaptureAsync(() => UtauDirectPlanner.ReadAsync(text, canceled.Token)) is OperationCanceledException,
            "direct reading and source planning both honor precancellation");
        await CheckContentInvalidationAsync(planner, bank, entries[0], check);
        check((await planner.CreateAsync(reading, bank)).Anchors.SequenceEqual(actual.Anchors),
            "a failed or canceled direct source plan leaves the planner usable for a following utterance");
        await CheckSourceSelectionAsync(planner, bank, entries, check);
        await CheckDiagnosticsAsync(check);
    }

    private static async Task CheckSourceSelectionAsync(UtauDirectPlanner planner, Voicebank bank,
        IReadOnlyList<OtoEntry> entries, Action<bool, string> check)
    {
        var reading = await UtauDirectPlanner.ReadAsync("あいっあ");
        var contextual = bank with
        {
            Entries = [.. entries, entries[0] with { Alias = "- あ_C4" }, entries[1] with { Alias = "a い_C4" }],
            PitchMappings = [new(60, "", "_C4")]
        };
        var selected = await planner.CreateAsync(reading, contextual, sourceTargetHz: 261.625565);
        check(selected.Reading.SequenceEqual(reading) && selected.Anchors.Select(x => x.Alias).SequenceEqual(reading.Select(x => x.Alias)) &&
            selected.Units[0].Source?.Oto.Alias == "- あ_C4" && selected.Units[1].Source?.Oto.Alias == "a い_C4" &&
            selected.Units[2].Source is null && selected.Units[3].Source?.Oto.Alias == "- あ_C4" && selected.Units[3].OverlapSamples == 0,
            "optional direct source selection changes only recordings and clears contextual VCV selection across closure without changing logical reading");
        var ordinary = await planner.CreateAsync(reading, bank);
        var fallback = await planner.CreateAsync(reading, bank, sourceTargetHz: 261.625565);
        check(ordinary.Anchors.SequenceEqual(fallback.Anchors) && ordinary.SampleCount == fallback.SampleCount &&
            ordinary.Units.Select(x => x.Source?.Oto).SequenceEqual(fallback.Units.Select(x => x.Source?.Oto)),
            "a CV-only bank preserves exact direct placements and source choice when optional multisample selection is enabled");
        var unchanged = await planner.CreateAsync(reading, contextual);
        check(unchanged.Units[0].Source?.Oto == entries[0] && unchanged.Units[1].Source?.Oto == entries[1],
            "legacy direct-plan calls without optional target pitch retain first-CV source selection even when VCV recordings are available");
    }

    private static async Task CheckContentInvalidationAsync(UtauDirectPlanner planner, Voicebank bank, OtoEntry entry,
        Action<bool, string> check)
    {
        var bytes = await File.ReadAllBytesAsync(entry.WavePath);
        var stamp = File.GetLastWriteTimeUtc(entry.WavePath);
        try
        {
            var corrupted = (byte[])bytes.Clone(); corrupted[0] = 0;
            await File.WriteAllBytesAsync(entry.WavePath, corrupted);
            File.SetLastWriteTimeUtc(entry.WavePath, stamp);
            check(await CaptureAsync(() => planner.CreateAsync([new("あ", 0, '\0')], bank)) is InvalidDataException,
                "direct source caches reject same-size same-timestamp content replacement rather than reuse previously validated PCM");
        }
        finally
        {
            await File.WriteAllBytesAsync(entry.WavePath, bytes);
            File.SetLastWriteTimeUtc(entry.WavePath, stamp);
        }
    }

    private static async Task CheckDiagnosticsAsync(Action<bool, string> check)
    {
        var renderer = new DiagnosticsRenderer();
        await using var speech = new UtauSpeech(new NoPlayback(), renderer)
        { Voicebank = new("direct diagnostic fixture", "unused", [], 0) };
        var planning = 0; var timing = 0;
        speech.ProsodyFallback += _ => throw new IOException("subscriber failure");
        speech.ProsodyFallback += _ => planning++;
        speech.ProsodyTimingFallback += _ => timing++;
        await speech.SynthesizeAsync("あ"); await speech.SynthesizeAsync("い");
        renderer.Raise();
        check(planning == 2 && timing == 2,
            "direct fallback diagnostics reach existing handlers once per utterance and subscriptions are removed after completion");
        renderer.Fail = true;
        check(await CaptureAsync(() => speech.SynthesizeAsync("う")) is IOException,
            "direct rendering failures still propagate after forwarding plan diagnostics");
        renderer.Raise();
        check(planning == 3 && timing == 3,
            "failed direct utterances also release diagnostic subscriptions");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }

    private sealed class OversizedReading : IReadOnlyList<UtauReadingUnit>
    {
        public int Count => 641;
        public UtauReadingUnit this[int index] => throw new InvalidOperationException("must bound count before indexing");
        public IEnumerator<UtauReadingUnit> GetEnumerator() => throw new InvalidOperationException("must not enumerate");
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class DiagnosticsRenderer : IUtauPcmPostprocessor, IUtauDirectRenderer, IUtauDirectDiagnostics
    {
        public bool Fail { get; set; }
        public event Action<Exception>? PlanningFallback;
        public event Action<UtauTimingPlanException>? TimingFallback;
        public void Raise()
        { PlanningFallback?.Invoke(new InvalidDataException("plan")); TimingFallback?.Invoke(new(0, 1, 882)); }
        public Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken ct)
        { Raise(); if (Fail) throw new IOException("direct render"); return Task.FromResult(new PcmAudio([1, -1], 44100)); }
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class NoPlayback : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct) => throw new InvalidOperationException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
