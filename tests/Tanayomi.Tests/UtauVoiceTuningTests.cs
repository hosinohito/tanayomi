using Tanayomi;

internal static class UtauVoiceTuningTests
{
    private sealed class Sink(Action? first = null) : IPcmOutput
    {
        internal List<short[]> Parts { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            if (rate != 44100) throw new InvalidDataException();
            Parts.Add(samples.ToArray());
            if (Parts.Count == 1) first?.Invoke();
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var temp = new TestDirectory();
        var store = new UtauVoiceTuningStore(temp.DirectoryPath);
        check(await store.LoadAsync(default) == UtauVoiceTuning.Default, "missing UTAU voice setting preserves defaults");
        var path = Path.Combine(temp.DirectoryPath, "settings", "utau-voice-tuning.txt");
        await store.SaveAsync(new(-3, 140, 35, 25), default);
        check(await store.LoadAsync(default) == new UtauVoiceTuning(-3, 140, 35, 25), "all four UTAU voice settings round-trip");
        var curve = new UtauPitchCurve(-4, 6, -6, 3, -1);
        var curvedTuning = new UtauVoiceTuning(-3, 140, 35, 25, curve);
        await store.SaveAsync(curvedTuning, default);
        var independent = new UtauVoiceTuning(0, 0, boundaryRange: 80);
        await store.SaveAsync(independent, default);
        check(await store.LoadAsync(default) == independent, "question-ending strength is saved separately from lexical accent");
        var disabledCurve = new UtauVoiceTuning(2, 60, 0, 0, curve, boundaryRange: 90, curveEnabled: false);
        await store.SaveAsync(disabledCurve, default);
        check(await store.LoadAsync(default) == disabledCurve, "curve toggle persists without discarding the five positions or automatic settings");
        await File.WriteAllTextAsync(path, "0 60 0 0 1 1 0 -1 -2 90 v3");
        check((await store.LoadAsync(default)).CurveEnabled, "older saved tuning keeps the curve enabled");
        await File.WriteAllTextAsync(path, "0 60 0 0 1 1 0 -1 -2 v2");
        check((await store.LoadAsync(default)).BoundaryRange == 60, "v2 settings restore both width controls from the previous shared value");
        await store.SaveAsync(curvedTuning, default);
        check(await store.LoadAsync(default) == curvedTuning, "all five pitch curve points round-trip with existing voice controls");
        await File.WriteAllTextAsync(path, "-3 140 35 25");
        check(await store.LoadAsync(default) == new UtauVoiceTuning(-3, 140, 35, 25),
            "previous four-value tuning files leave the new pitch curve unmodified");
        await File.WriteAllTextAsync(path, "0 100 0 0 -1 2 0 1 2");
        check((await store.LoadAsync(default)).Curve == new UtauPitchCurve(0, 3, 0, 0, 0),
            "old trial offsets are translated into actual curve positions on load");
        await store.SaveAsync(new(0, 0, curve: new UtauPitchCurve()), default);
        check((await store.LoadAsync(default)).Curve == new UtauPitchCurve(),
            "explicit all-zero curve stays flat after reloading");
        await File.WriteAllTextAsync(path, "2 60 0 0 -1 2 0 1 2 90 0 1 18 3 v6");
        var migrated = new UtauVoiceTuning(2, 60, curve: new(-1, 2, 0, 1, 2), boundaryRange: 90,
            curveEnabled: false, levelJoins: true, maximumHeadCut: 18);
        check(await store.LoadAsync(default) == migrated, "retired trial tuning retains unrelated voice settings");
        await store.SaveAsync(migrated, default);
        check((await File.ReadAllTextAsync(path)).EndsWith(" 18 v5") && await store.LoadAsync(default) == migrated,
            "new tuning saves only supported settings and round-trips");
        await File.WriteAllTextAsync(path, "0 100 0 0 0 0 7 0 0");
        try { await store.LoadAsync(default); check(false, "out-of-range pitch point rejected"); }
        catch (InvalidDataException) { check(true, "out-of-range pitch point rejected"); }

        check(new[] { 0, 0.25, 0.5, 0.75, 1 }.Select(curve.SemitonesAt).SequenceEqual(new[] { -2, 3, -3, 1.5, -0.5 }),
            "pitch curve reaches each of its five editable points");
        check(new[] { 0, 0.25, 0.5, 0.75, 1 }.Select(UtauVoiceTuning.Default.Curve.SemitonesAt)
                .SequenceEqual(new[] { 0.5, 0.5, 0.0, -0.5, -1.0 }),
            "visible slider defaults define the entire descending base line");
        check(curve.SemitonesAt(0.125) == 0.5 && Math.Abs(curve.SemitonesAt(0.25 - 0.001) - curve.SemitonesAt(0.25 + 0.001)) < 0.001,
            "pitch curve smoothly connects neighboring points without a pitch step");
        CheckCurvePcm(check);
        await File.WriteAllTextAsync(path, "-3 140");
        check(await store.LoadAsync(default) == new UtauVoiceTuning(-3, 140), "UTAU voice tuning round-trips");
        await File.WriteAllTextAsync(Path.Combine(temp.DirectoryPath, "settings", "utau-voice-tuning.txt"), "-7 201");
        try { await store.LoadAsync(default); check(false, "invalid UTAU voice settings rejected"); }
        catch (InvalidDataException) { check(true, "invalid UTAU voice settings rejected"); }

        await File.WriteAllTextAsync(path, "0 100 0 101");
        try { await store.LoadAsync(default); check(false, "invalid breath percentage rejected"); }
        catch (InvalidDataException) { check(true, "invalid breath percentage rejected"); }

        CheckTimbre(check);
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        const string text = "こんにちは。おはよう。";
        UtauSpeech Open(Sink sink, UtauVoiceTuning setting) => new(sink)
        {
            Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePrefetchEnabled = true,
            SimplePitchContourEnabled = true, SimpleSourceAnchoredPitchEnabled = true, VoiceTuning = setting
        };
        async Task<short[][]> Play(UtauVoiceTuning tuning)
        {
            var sink = new Sink();
            await using var voice = Open(sink, tuning);
            await voice.SpeakAsync(text, default);
            return sink.Parts.ToArray();
        }
        var ordinary = await Play(UtauVoiceTuning.Default);
        var raised = await Play(new(4, 100));
        var flat = await Play(new(0, 0, curve: new UtauPitchCurve()));
        var lowered = await Play(new(-4, 100));
        check(ordinary.Length == 2 && raised.Length == 2 && flat.Length == 2, "voice tuning keeps normal two-part prefetch");
        check(new[] { raised, flat, lowered }.All(x => x.Select(p => p.Length).SequenceEqual(ordinary.Select(p => p.Length))),
            "voice tuning does not change playback duration");
        check(!raised[0].SequenceEqual(ordinary[0]) && !flat[0].SequenceEqual(ordinary[0]) &&
            !lowered[0].SequenceEqual(raised[0]), "overall pitch and an explicitly flat curve change the final playback PCM");

        var curved = await Play(curvedTuning);
        var withoutCurve = await Play(new(-3, 140, 35, 25));
        check(curved.Select(p => p.Length).SequenceEqual(withoutCurve.Select(p => p.Length)) &&
            curved.Zip(withoutCurve).All(pair => !pair.First.SequenceEqual(pair.Second)),
            "five-point pitch curve changes final PCM in both prefetched phrases without changing timing");

        var off = await Play(new(-3, 140, 35, 25, curve, curveEnabled: false));
        var zero = await Play(new(-3, 140, 35, 25, new UtauPitchCurve()));
        check(off.Length == zero.Length && off.Zip(zero).All(p => p.First.SequenceEqual(p.Second)) &&
            off.Zip(curved).Any(p => !p.First.SequenceEqual(p.Second)),
            "switching off the saved curve bypasses its shape in final PCM without changing the other controls");

        var thick = await Play(new(0, 100, 90));
        var breathy = await Play(new(0, 100, 0, 90));
        var both = await Play(new(0, 100, 90, 90));
        check(new[] { thick, breathy, both }.All(x => x.Select(p => p.Length).SequenceEqual(ordinary.Select(p => p.Length))),
            "g and B preserve the playback duration and phrase boundaries");
        check(!thick[0].SequenceEqual(ordinary[0]) && !breathy[0].SequenceEqual(ordinary[0]) &&
            !both[0].SequenceEqual(thick[0]) && !both[0].SequenceEqual(breathy[0]),
            "g and B operate independently and together in final PCM");
        check(ordinary[0].AsSpan(0, 441).SequenceEqual(both[0].AsSpan(0, 441)),
            "combined settings keep the phrase attack PCM dry");

        UtauSpeech? changing = null;
        var during = new Sink(() => changing!.VoiceTuning = curvedTuning);
        await using (var voice = Open(during, new(4, 100)))
        {
            changing = voice;
            await voice.SpeakAsync(text, default);
            check(during.Parts.Zip(raised).All(pair => pair.First.SequenceEqual(pair.Second)),
                "all prefetched phrases keep the utterance's snapshotted tuning");
            await voice.SpeakAsync(text, default);
            check(during.Parts.Skip(2).Zip(curved).All(pair => pair.First.SequenceEqual(pair.Second)),
                "next utterance uses newly selected tuning");
        }
    }

    private static void CheckCurvePcm(Action<bool, string> check)
    {
        const int rate = UtauSpeech.OutputSampleRate, period = 170;
        var pcm = new float[rate * 2];
        for (var i = 0; i < pcm.Length; i++)
            pcm[i] = 0.4f * (float)Math.Sin(2 * Math.PI * i / period);
        var flat = (float[])pcm.Clone();
        var flatTrack = new UtauContinuousPitch(pcm.Length, period, false, tuning: new(0, 100, curve: new UtauPitchCurve()));
        flatTrack.Apply(flat, 0, period, default, trackVoicedOnset: true);
        check(new[] { 0.10, 0.25, 0.5, 0.75, 0.90 }.All(t =>
            Math.Abs(UtauPitch.FindPeriod(flat.AsSpan((int)(t * flat.Length) - 800, 1600), default) - period) < 1.5),
            "all five sliders at zero produce constant measured pitch without a hidden baseline");
        var curve = new UtauPitchCurve(0, 6, -6, 4, 0);
        var track = new UtauContinuousPitch(pcm.Length, period, false, tuning: new(0, 0, curve: curve));
        track.Apply(pcm, 0, period, default, trackVoicedOnset: true);
        var measured = new[] { 0.25, 0.5, 0.75 }.Select(t =>
        {
            var middle = (int)(t * pcm.Length);
            return 12 * Math.Log2(period / UtauPitch.FindPeriod(pcm.AsSpan(middle - 800, 1600), default));
        }).ToArray();
        check(measured.Zip(new[] { 3.0, -3.0, 2.0 }).All(p => Math.Abs(p.First - p.Second) < 0.35),
            $"rendered mid-phrase pitch follows the three independently adjustable middle points: {string.Join(", ", measured.Select(x => x.ToString("F2")))} semitones");
    }

    private static void CheckTimbre(Action<bool, string> check)
    {
        const int period = 220;
        var original = new float[period * 34];
        for (var h = 1; h <= 25; h++)
        {
            var x = (h - 4) / 1.25;
            var y = (h - 9) / 1.4;
            var amplitude = 0.08 * (0.12 / h + Math.Exp(-0.5 * x * x) + 0.45 * Math.Exp(-0.5 * y * y));
            for (var i = 0; i < original.Length; i++) original[i] += (float)(amplitude * Math.Sin(2 * Math.PI * h * i / period));
        }
        var effect = new UtauTimbre();
        float[] Render(int g, int b)
        {
            var pcm = (float[])original.Clone();
            effect.Apply(pcm, period, new(0, 100, g, b), 19, default);
            return pcm;
        }
        var thick = Render(100, 0);
        var thin = Render(-100, 0);
        var dry = Render(0, 0);
        var breathy = Render(0, 100);
        check(dry.SequenceEqual(original) && thick[0] == original[0] && breathy[^1] == original[^1],
            "timbre bypass is exact and the effect boundaries remain dry");
        var thickCenter = LowResonance(thick);
        var normalCenter = LowResonance(original);
        var thinCenter = LowResonance(thin);
        check(thickCenter < normalCenter - 0.12 && thinCenter > normalCenter + 0.12,
            $"g shifts low harmonic resonance in both directions: {thickCenter:F2}/{normalCenter:F2}/{thinCenter:F2}");
        check(new[] { thick, thin, breathy }.All(pcm =>
                Math.Abs(UtauPitch.FindPeriod(pcm.AsSpan(period * 10, period * 8), default) - period) < 2),
            "g and B retain the synthetic vowel fundamental");
        check(breathy.SequenceEqual(Render(0, 100)) && Residual(breathy) > Residual(original) + 0.001,
            "B adds deterministic aperiodic vowel energy");

        static double LowResonance(float[] pcm)
        {
            double sum = 0, weighted = 0;
            for (var h = 2; h <= 6; h++)
            {
                double real = 0, imag = 0;
                for (var i = period * 8; i < period * 26; i++)
                {
                    var phase = 2 * Math.PI * h * i / period;
                    real += pcm[i] * Math.Cos(phase); imag += pcm[i] * Math.Sin(phase);
                }
                var power = real * real + imag * imag;
                sum += power; weighted += power * h;
            }
            return weighted / sum;
        }

        static double Residual(float[] pcm)
        {
            double power = 0;
            for (var i = period * 8; i < period * 24; i++)
            {
                var delta = pcm[i] - pcm[i + period];
                power += delta * delta;
            }
            return Math.Sqrt(power / (period * 16));
        }
    }}
