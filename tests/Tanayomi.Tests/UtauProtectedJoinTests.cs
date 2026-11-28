using System.IO.Compression;
using Tanayomi;

internal static class UtauProtectedJoinTests
{
    private sealed class Sink : IPcmOutput
    {
        internal List<short> Samples { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct)
        {
            Samples.AddRange(samples.ToArray()); return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using var temp = new TestDirectory();
        static float[] Tone(float amplitude) => Enumerable.Range(0, 6000)
            .Select(i => amplitude * (float)Math.Sin(2 * Math.PI * i / 176)).ToArray();
        var prior = Tone(.18f);
        var head = Tone(.36f);
        var originalHead = (float[])head.Clone();
        check(UtauHeadProtectedJoin.Apply(prior, head, 0, prior.Length, 176, 176, 0, default),
            "a voiced join raises its tail with no head cut");
        check(head.SequenceEqual(originalHead) && Math.Abs(Peak(prior[^500..]) - Peak(head[..500])) < .005,
            "a zero head cut keeps the entire next sound identical and matches the tail to it");
        prior = Tone(.12f); head = Tone(.55f);
        check(UtauHeadProtectedJoin.Apply(prior, head, 0, prior.Length, 176, 176, 25, default) &&
            head.Zip(Tone(.55f), (s, old) => Math.Abs(s) + .000001 >= Math.Abs(old) * .75).All(x => x),
            "even when the tail cannot reach the head, the cut stays within the selected percentage");
        float[] silent = new float[6000];
        prior = Tone(.13f);
        var untouched = (float[])prior.Clone();
        check(!UtauHeadProtectedJoin.Apply(prior, silent, 0, prior.Length, 176, 176, 50, default)
            && prior.SequenceEqual(untouched) && silent.All(s => s == 0), "join leaves silence and its neighboring tail intact");
        var random = new Random(27);
        var noise = Enumerable.Range(0, 6000).Select(_ => (float)(random.NextDouble() - .5)).ToArray();
        var noiseCopy = (float[])noise.Clone();
        check(!UtauHeadProtectedJoin.Apply(prior, noise, 0, prior.Length, 176, 176, 50, default)
            && prior.SequenceEqual(untouched) && noise.SequenceEqual(noiseCopy), "join does not suppress aperiodic consonant-like audio");

        var store = new UtauVoiceTuningStore(temp.DirectoryPath);
        var tuning = new UtauVoiceTuning(0, 100, levelJoins: true, maximumHeadCut: 0);
        await store.SaveAsync(tuning, default);
        check(await store.LoadAsync(default) == tuning, "the join switch and zero-degree head cut survive reload");
        var root = Path.Combine(temp.DirectoryPath, "audio");
        byte[] MakeWave(float amplitude) => new PcmAudio(Tone(amplitude).Select(x => (short)Math.Round(x * 32767)).ToArray(), 44100).ToWaveBytes();
        var low = MakeWave(.15f);
        var high = MakeWave(.6f);
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, true))
        {
            using (var a = archive.CreateEntry("tiny/a.wav").Open()) a.Write(low);
            using (var b = archive.CreateEntry("tiny/b.wav").Open()) b.Write(high);
            using var oto = new StreamWriter(archive.CreateEntry("tiny/oto.ini").Open());
            oto.Write("a.wav=あ,0,30,0,0,0\na.wav=わ,0,30,0,0,0\nb.wav=い,0,30,0,0,0\n");
        }
        zip.Position = 0;
        var bank = await Voicebanks.ImportZipAsync(zip, root);
        check(bank.RegisteredPeakGains?.Count == 2, "import measures each unique source despite repeated aliases");
        var measured = bank.Entries.DistinctBy(x => x.WavePath).Select(x =>
            Peak(UtauSpeech.DecodeWave(File.ReadAllBytes(x.WavePath), default)) * bank.RegisteredPeakGains![x.WavePath]).ToArray();
        check(measured.Length == 2 && measured.All(x => Math.Abs(x - .375) < .01),
            "the two different source peaks converge on the arithmetic bank mean");
        check(File.ReadAllBytes(bank.Entries[0].WavePath).SequenceEqual(low) && File.ReadAllBytes(bank.Entries[2].WavePath).SequenceEqual(high),
            "registering stores coefficients without rewriting either WAV");
        var reread = UtauRegisteredPeaks.Attach(Voicebanks.Read(bank.Directory), default);
        check(reread.RegisteredPeakGains!.SequenceEqual(bank.RegisteredPeakGains!), "a reloaded bank reuses the registered factors");

        var real = (await Voicebanks.LoadInstalledAsync(Path.Combine(temp.DirectoryPath, "real")))[0];
        const string phrase = "こんにちは。たなよみの読み上げテストです。";
        async Task<short[]> Play(Voicebank b, bool on)
        {
            var sink = new Sink();
            await using var app = new UtauSpeech(sink)
            {
                Voicebank = b, Mode = UtauSynthesisMode.Simple, SimplePrefetchEnabled = true,
                SimplePitchContourEnabled = true, SimpleSourceAnchoredPitchEnabled = true, SimpleAttackEnvelopeEnabled = false, SimpleMouthContourEnabled = false, SimpleSilentTailEnabled = false,
                VoiceTuning = new(0, 100, levelJoins: on, maximumHeadCut: 0)
            };
            await app.SpeakAsync(phrase, default);
            return sink.Samples.ToArray();
        }
        var oldPcm = await Play(real, false);
        var newPcm = await Play(real, true);
        check(real.RegisteredPeakGains is { Count: > 30 } && oldPcm.Length == newPcm.Length && !oldPcm.SequenceEqual(newPcm),
            "the real bank and prefetched playback honor the feature without changing full-phrase length");
        var withoutFactors = real with { RegisteredPeakGains = null };
        var withoutWords = await Play(withoutFactors, true);
        check(!withoutWords.SequenceEqual(newPcm), "the live output consumes the source peak factors");
        check(!withoutWords.SequenceEqual(oldPcm), "the live joined waveform changes even with identical source levels");
        check((await Play(withoutFactors, false)).SequenceEqual(oldPcm), "turning the setting off bypasses the peak factors");
        check(newPcm.All(x => x > short.MinValue && x < short.MaxValue), "the live adjusted output does not clip");
    }


    internal static async Task VerifyTailAndMouthAsync(Action<bool, string> check)
    {
        const int hz = 44100;
        float[] Tone(int n, int offset = 0) => Enumerable.Range(offset, n)
            .Select(i => .24f * (float)Math.Sin(2 * Math.PI * i / 176)).ToArray();
        var random = new Random(104);
        var signal = Tone(hz * 80 / 1000);
        var trailingNoise = Enumerable.Range(0, hz * 32 / 1000).Select(_ => (float)((random.NextDouble() - .5) * .001)).ToArray();
        var quiet = signal.Concat(trailingNoise).ToArray();
        var trimmed = UtauSilentSourceTail.EffectiveLength(quiet, 0, 176, default);
        check(trimmed >= signal.Length && trimmed - signal.Length < hz * 6 / 1000,
            "a padded tail is cut at the last stable voice, not several milliseconds into it");
        var fricative = signal.Concat(Enumerable.Range(0, 900).Select(_ => (float)((random.NextDouble() - .5) * .055)))
            .Concat(trailingNoise).ToArray();
        check(UtauSilentSourceTail.EffectiveLength(fricative, 0, 176, default) == fricative.Length &&
            UtauSilentSourceTail.EffectiveLength(signal, 0, 176, default) == signal.Length,
            "a trailing release and a vowel without padding are left untouched");

        using var temp = new TestDirectory();
        var root = Path.Combine(temp.DirectoryPath, "padded");
        Directory.CreateDirectory(root);
        // Deliberately use an audible low noise floor rather than exact zero:
        // the older single-sample trim can leave it in the source.
        var raw = signal.Concat(trailingNoise.Select(x => x + .0012f)).ToArray();
        var testPcm = new PcmAudio(raw.Select(x => (short)Math.Round(x * 32767)).ToArray(), hz).ToWaveBytes();
        var path = Path.Combine(root, "a.wav");
        await File.WriteAllBytesAsync(path, testPcm);
        await File.WriteAllTextAsync(Path.Combine(root, "oto.ini"), "a.wav=あ,0,25,0,8,0\n");
        var bank = Voicebanks.Read(root);
        async Task<short[]> Render(Voicebank b, string phrase, bool tail, bool mouth, int cut, bool adjust)
        {
            var sink = new Sink();
            await using var app = new UtauSpeech(sink)
            {
                Voicebank = b, Mode = UtauSynthesisMode.Simple, SimplePrefetchEnabled = true,
                SimplePitchContourEnabled = true, SimpleSourceAnchoredPitchEnabled = true,
                SimpleAttackEnvelopeEnabled = false, SimpleSilentTailEnabled = tail, SimpleMouthContourEnabled = mouth,
                VoiceTuning = new(0, 100, levelJoins: adjust, maximumHeadCut: cut)
            };
            await app.SpeakAsync(phrase, default);
            return sink.Samples.ToArray();
        }
        static double Power(short[] s, int from, int n) => s.AsSpan(from, n).ToArray().Average(x => (double)x * x);
        var oldPadding = await Render(bank, "あ", false, false, 0, false);
        var newPadding = await Render(bank, "あ", true, false, 0, false);
        var p = oldPadding.Length - hz * 30 / 1000;
        var n = hz * 16 / 1000;
        var before = Power(oldPadding, p, n);
        var after = Power(newPadding, p, n);
        Console.WriteLine($"AUDIT padded-tail RMS improvement {10*Math.Log10(after / Math.Max(1, before)):F1} dB");
        check(oldPadding.Length == newPadding.Length && after > 10 * before &&
            File.ReadAllBytes(path).SequenceEqual(testPcm), "the live renderer stretches the surviving sound at the same duration without rewriting the WAV");

        var real = (await Voicebanks.LoadInstalledAsync(Path.Combine(temp.DirectoryPath, "real")))[0];
        foreach (var phrase in new[] { "よみ。", "よた。", "こんにちは。たなよみの読み上げテストです。" })
        {
            var r51Zero = await Render(real, phrase, false, false, 0, true);
            var r51Fifty = await Render(real, phrase, false, false, 50, true);
            var noMouth = await Render(real, phrase, true, false, 50, true);
            var current = await Render(real, phrase, true, true, 50, true);
            var knob = r51Zero.Zip(r51Fifty).Count(x => x.First != x.Second);
            var shape = noMouth.Zip(current).Count(x => x.First != x.Second);
            Console.WriteLine($"AUDIT {phrase}: old 0-vs-50 {knob}/{r51Fifty.Length} samples, mouth contour {shape}/{current.Length} samples");
            check(r51Fifty.Length == current.Length && (phrase == "よた。" ? shape == 0 : shape > 0) &&
                current.All(s => s > short.MinValue && s < short.MaxValue), "new live mouth response has correct contrast and length");
        }
    }

    private static float Peak(float[] array) => array.Max(x => Math.Abs(x));
}
