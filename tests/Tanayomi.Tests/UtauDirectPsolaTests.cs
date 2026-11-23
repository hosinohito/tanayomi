using Tanayomi;

internal static class UtauDirectPsolaTests
{
    private delegate float Peak(ReadOnlySpan<float> samples, CancellationToken ct);

    private static void CheckFinitePeak(Action<bool, string> check)
    {
        var peak = typeof(UtauDirectPsola).GetMethod("FindPeak",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.CreateDelegate<Peak>();
        var width = System.Numerics.Vector<float>.Count;
        foreach (var length in new[] { 1, width - 1, width, width + 1, width * 2 + 1, 4095, 4096, 4097 })
        {
            foreach (var level in new[] { -0f, 0f, float.Epsilon, float.BitDecrement(0.0001f), 0.0001f,
                float.BitIncrement(0.0001f), -0.92f, 1.25f, float.MaxValue })
            {
                var samples = Enumerable.Repeat(level, length).ToArray();
                check(BitConverter.SingleToInt32Bits(peak(samples, default)) ==
                    BitConverter.SingleToInt32Bits(samples.Max(value => Math.Abs(value))),
                    "direct PSOLA peak preserves zero, gain and silence-threshold bits across vector and cancellation boundaries");
            }
        }
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        for (var position = 0; position <= width * 2; position++)
        {
            var samples = Enumerable.Repeat(0.5f, width * 2 + 1).ToArray(); samples[position] = invalid;
            var rejected = false;
            try { _ = peak(samples, default); }
            catch (InvalidDataException error) { rejected = error.Message == "原音周期同期が不正な波形を生成しました。"; }
            check(rejected, "direct PSOLA rejects nonfinite samples in every vector lane and scalar tail with the existing error");
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        foreach (var length in new[] { 1, width + 1, 4097 })
        {
            var cancelled = false;
            try { _ = peak(new float[length], stop.Token); }
            catch (OperationCanceledException error) { cancelled = error.CancellationToken == stop.Token; }
            check(cancelled, "direct PSOLA peak honors an existing stop before vector or scalar work");
        }
    }

    private static void CheckQuantization(Action<bool, string> check)
    {
        var quantize = typeof(UtauDirectPsola).GetMethod("Quantize",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<float[], float, CancellationToken, short[]>>();
        foreach (var length in new[] { 1, 439, 440, 441, 455, 456, 479, 480, 481, 4095, 4096, 4097, 8209 })
        foreach (var peak in new[] { 0.0001f, 0.92f, 1f, 10f, float.MaxValue })
        {
            var gain = Math.Min(1, 0.92f / peak);
            var source = Enumerable.Range(0, length).Select(i =>
            {
                var value = (float)(((i % 101) - 50 + 0.5) / 32767.0 / gain);
                value = i % 3 == 0 ? float.BitIncrement(value) : i % 3 == 1 ? float.BitDecrement(value) : value;
                if (i % 17 == 0) value = -0f;
                if (i % 19 == 0) value = float.Epsilon;
                return Math.Clamp(value, -peak, peak);
            }).ToArray();
            source[length / 2] = peak;
            var original = (float[])source.Clone(); var expected = new short[length];
            var edge = Math.Min(44100 / 200, length / 2);
            for (var i = 0; i < length; i++)
            {
                float Ramp(int at) => (float)(0.5 - 0.5 * Math.Cos(Math.PI * at / Math.Max(1, edge - 1)));
                var envelope = i < edge ? Ramp(i) : i >= length - edge ? Ramp(length - 1 - i) : 1;
                expected[i] = (short)Math.Clamp(Math.Round(source[i] * gain * envelope * 32767), short.MinValue, short.MaxValue);
            }
            var actual = quantize(source, peak, default);
            check(actual.SequenceEqual(expected) && source.Select(BitConverter.SingleToInt32Bits).SequenceEqual(original.Select(BitConverter.SingleToInt32Bits)),
                "direct PSOLA quantization retains scalar float products and ToEven half-neighbors through edge, lane, tail and 4096 boundaries without modifying its input");
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        foreach (var length in new[] { 1, 480, 4097 })
        {
            var canceled = false;
            try { _ = quantize(new float[length], 1, stop.Token); }
            catch (OperationCanceledException error) { canceled = error.CancellationToken == stop.Token; }
            check(canceled, "direct PSOLA quantization honors cancellation before scalar edges or vector interior work");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckFinitePeak(check);
        CheckQuantization(check);
        const double originalHz = 210;
        var raw = new float[26460];
        var random = new Random(1931);
        for (var i = 882; i < 3528; i++) raw[i] = (float)((random.NextDouble() * 2 - 1) * 0.08);
        for (var i = 3528; i < raw.Length; i++)
        {
            var phase = 2 * Math.PI * originalHz * (i - 3528) / 44100;
            raw[i] = (float)(0.18 * (Math.Sin(phase) + 0.25 * Math.Sin(2 * phase)));
        }
        var bank = new Voicebank("raw direct PSOLA fixture", "unused", [], 0);
        var requested = Plan(raw, originalHz);
        var calls = 0;
        await using var renderer = new UtauDirectPsola((_, _, ct) =>
        { ct.ThrowIfCancellationRequested(); calls++; return Task.FromResult(requested); });
        var warnings = 0; renderer.PlanningFallback += _ => warnings++;
        var identity = await renderer.RenderAsync("fixture", bank, default);
        var error = Enumerable.Range(7000, 11000).Max(i => Math.Abs(identity.Samples[i] - Math.Round(raw[i] * 32767)));
        check(identity.Samples.Length == raw.Length && error <= 3 && warnings == 0,
            "direct original-source PSOLA identity reconstruction retains central samples at matching duration and F0 without a B waveform");
        check(identity.Samples.Take(800).All(x => x == 0) && Rms(identity.Samples, 1400, 1500) > 300,
            "direct source synthesis preserves the original closure and nonperiodic fricative before the stable vowel");
        requested = Plan(raw, 315);
        var raised = await renderer.RenderAsync("fixture", bank, default);
        var raisedHz = Crossings(raised.Samples, 10000, 10000);
        check(raised.Samples.Length == raw.Length && Math.Abs(raisedHz - 315) < 3 &&
            Math.Abs(Crossings(identity.Samples, 10000, 10000) - originalHz) < 3,
            "independent positive zero-crossing observation finds the requested direct F0 change rather than an octave or duration change");
        requested = Plan(raw, originalHz, extraStableSamples: 13230);
        var stretched = await renderer.RenderAsync("fixture", bank, default);
        check(stretched.Samples.Length == raw.Length + 13230 &&
            Math.Abs(Crossings(stretched.Samples, 11000, 22000) - originalHz) < 3 &&
            stretched.Samples.Take(800).All(x => x == 0),
            "direct duration-only stretching preserves measured pitch and closure while extending only the planned stable output");
        requested = Plan(raw, 210, extraStableSamples: 8820);
        var contour = requested.F0.Select((_, i) => i < requested.F0.Count / 2 ? 210.0 : 315.0).ToArray();
        requested = requested with { F0 = contour };
        var changing = await renderer.RenderAsync("fixture", bank, default);
        check(Math.Abs(Crossings(changing.Samples, 7500, 7000) - 210) < 7 &&
            Math.Abs(Crossings(changing.Samples, 23500, 7000) - 315) < 7,
            "direct epochs follow the finite utterance F0 contour independently of source-time stretching");
        check(raised.Samples.Max(x => Math.Abs((int)x)) < 31000 && calls == 4 && warnings == 0,
            "direct source reconstruction remains finite bounded and reuses its numerical analysis without false uncertainty diagnostics");
        foreach (var edgeHz in new[] { 40.0, 1200.0 })
        {
            requested = Plan(raw, edgeHz);
            var edge = await renderer.RenderAsync("fixture", bank, default);
            check(edge.Samples.Length == raw.Length && edge.Samples.Take(800).All(x => x == 0) &&
                edge.Samples.Any(x => x != 0) && edge.Samples.All(x => Math.Abs((int)x) <= 31000),
                "inclusive low and high F0 bounds keep periodic grains within original and output boundaries while preserving closure");
        }
        await CheckJoinAndPauseAsync(check);
        await CheckRepeatedSourceLeadAsync(check);
        await CheckInvalidAndCancellationAsync(raw, bank, check);
    }

    private static async Task CheckRepeatedSourceLeadAsync(Action<bool, string> check)
    {
        var raw = Enumerable.Range(0, 13230).Select(i => (float)(0.18 * Math.Sin(2 * Math.PI * i / 210))).ToArray();
        var template = Plan(raw, 210).Plan.Units[0];
        var bank = new Voicebank("repeated direct source fixture", "unused", [], 0);
        UtauDirectRenderPlan Create(bool separateSources, bool zeroPeriod)
        {
            var units = new UtauDirectUnit[6]; var anchors = new UtauTimingAnchor[6];
            var end = 0;
            for (var i = 0; i < units.Length; i++)
            {
                var overlap = i == 0 ? 0 : 441;
                var start = end - overlap; end = start + raw.Length;
                var source = template.Source! with
                {
                    WavePath = separateSources ? "distinct-" + i : "shared",
                    ReferencePeriodSamples = zeroPeriod || i % 2 != 0 ? 0 : 210
                };
                units[i] = template with { UnitIndex = i, Source = source, StartSample = start,
                    VowelOnsetSample = start + template.LeadSamples, EndSample = end,
                    OverlapSamples = overlap, OtoOverlapSamples = 110 };
                anchors[i] = new(i, "あ", '\0', 0, start, units[i].VowelOnsetSample, end);
            }
            var plan = new UtauDirectPlan(units.Select(x => x.Reading).ToArray(), units, anchors, end);
            return new(plan, Enumerable.Repeat(210.0, (int)(1000.0 * end / 44100 / 5) + 1).ToArray());
        }
        async Task<PcmAudio> Render(UtauDirectRenderPlan request)
        {
            await using var renderer = new UtauDirectPsola((_, _, _) => Task.FromResult(request));
            return await renderer.RenderAsync("fixture", bank, default);
        }
        var repeated = await Render(Create(false, false));
        var distinct = await Render(Create(true, false));
        var unvoiced = await Render(Create(false, true));
        check(repeated.Samples.SequenceEqual(distinct.Samples) && !repeated.Samples.SequenceEqual(unvoiced.Samples),
            "repeated source lead classification preserves every PCM sample against distinct source identities and keeps differing reference periods separate");
    }

    private static async Task CheckJoinAndPauseAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var path = Path.Combine(temporary.DirectoryPath, "a.wav");
        var wave = new PcmAudio(Enumerable.Range(0, 22050).Select(i =>
            (short)(6000 * Math.Sin(2 * Math.PI * 210 * i / 44100))).ToArray(), 44100);
        await File.WriteAllBytesAsync(path, wave.ToWaveBytes());
        var bank = new Voicebank("direct join fixture", temporary.DirectoryPath, [new(path, "あ", 0, 100, -400, 20, 10)], 0);
        await using var planner = new UtauDirectPlanner();
        var reading = await UtauDirectPlanner.ReadAsync("あーっあ。あ");
        var source = await planner.CreateAsync(reading, bank);
        var f0 = Enumerable.Repeat(210.0, (int)(1000.0 * source.SampleCount / 44100 / 5) + 1).ToArray();
        await using var renderer = new UtauDirectPsola((_, _, _) => Task.FromResult(new UtauDirectRenderPlan(source, f0)));
        var audio = await renderer.RenderAsync("fixture", bank, default);
        check(audio.Samples.Length == source.SampleCount && source.Units.Where(x => x.Source is null).All(pause =>
            audio.Samples.AsSpan(pause.StartSample, pause.EndSample - pause.StartSample).IndexOfAnyExcept((short)0) < 0) &&
            source.Units.Where(x => x.Source is not null).All(unit => unit.OverlapSamples == 0 || unit.StartSample > 0),
            "direct concatenation preserves complete sokuon and punctuation pauses and prevents overlap across them");
    }

    private static async Task CheckInvalidAndCancellationAsync(float[] raw, Voicebank bank, Action<bool, string> check)
    {
        var requested = Plan(raw, 210);
        var owner = new Owner();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = false;
        var renderer = new UtauDirectPsola(async (_, _, ct) =>
        {
            if (hold) { entered.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
            ct.ThrowIfCancellationRequested(); return requested;
        }, owner);
        try
        {
            var normal = requested;
            foreach (var invalid in new[]
            {
                normal with { F0 = [] },
                normal with { F0 = normal.F0.Select((x, i) => i == 0 ? double.NaN : x).ToArray() },
                normal with { Plan = normal.Plan with { SampleCount = 44100 * 45 + 1 } },
                normal with { Plan = normal.Plan with { Units = [normal.Plan.Units[0] with { EndSample = 1 }] } },
                normal with { Plan = normal.Plan with { Units = [normal.Plan.Units[0] with
                    { Source = normal.Plan.Units[0].Source! with { EndSample = int.MaxValue } }] } }
            })
            {
                requested = invalid;
                check(await CaptureAsync(() => renderer.RenderAsync("fixture", bank, default)) is InvalidDataException,
                    "direct PSOLA rejects malformed contour bounds or source placements without attempting playback");
            }
            requested = normal;
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            check(await CaptureAsync(() => renderer.RenderAsync("fixture", bank, canceled.Token)) is OperationCanceledException,
                "precancelled direct PSOLA never starts source synthesis");
            using var stop = new CancellationTokenSource(); hold = true;
            var pending = renderer.RenderAsync("fixture", bank, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            check(await CaptureAsync(() => pending) is OperationCanceledException,
                "direct PSOLA observes cancellation while awaiting a context plan without starting fallback");
            hold = false;
            check((await renderer.RenderAsync("fixture", bank, default)).Samples.Any(x => x != 0),
                "direct PSOLA releases its gate after validation failures and cancellation for a later utterance");
            var uncertain = raw.Select((_, i) => (float)(Math.Sin(i * 2.2) * 0.08)).ToArray();
            requested = Plan(uncertain, 210);
            var warnings = 0; renderer.PlanningFallback += _ => warnings++;
            renderer.PlanningFallback += _ => throw new IOException("subscriber failure");
            var preserved = await renderer.RenderAsync("fixture", bank, default);
            check(warnings == 1 && preserved.Samples.Any(x => x != 0),
                "unconfirmed source periodicity preserves nonperiodic original grains and reports one bounded diagnostic despite subscriber failure");
        }
        finally { await renderer.DisposeAsync(); await renderer.DisposeAsync(); }
        check(owner.Disposals == 1 && await CaptureAsync(() => renderer.RenderAsync("fixture", bank, default)) is ObjectDisposedException,
            "direct renderer disposes its owned plan provider once and rejects subsequent rendering");
    }

    private static UtauDirectRenderPlan Plan(float[] raw, double hz, int extraStableSamples = 0)
    {
        var reading = new UtauReadingUnit("あ", 0, '\0');
        var oto = new OtoEntry("unused.wav", "あ", 0, 100, 0, 50, 10);
        var source = new UtauDirectSource(oto, "unused.wav", raw, 0, 0, 2205, 4410, raw.Length, raw.Length, 210);
        var length = raw.Length + extraStableSamples;
        var unit = new UtauDirectUnit(0, reading, source, 0, 2205, length, 2205, 2205, length - 2205, 0, 441);
        var plan = new UtauDirectPlan([reading], [unit], [new(0, "あ", '\0', 0, 0, 2205, length)], length);
        return new(plan, Enumerable.Repeat(hz, (int)(1000.0 * length / 44100 / 5) + 1).ToArray());
    }

    private static double Crossings(short[] samples, int from, int count)
    {
        var crossings = 0;
        for (var i = from + 1; i < from + count; i++) if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
        return crossings * 44100.0 / count;
    }
    private static double Rms(short[] samples, int from, int count) =>
        Math.Sqrt(samples.Skip(from).Take(count).Select(x => (double)x * x).Average());
    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    { try { await action(); return null; } catch (Exception error) { return error; } }
    private sealed class Owner : IAsyncDisposable
    { internal int Disposals; public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; } }
}
