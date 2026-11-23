using System.Diagnostics;
using System.Buffers.Binary;
using Tanayomi;

public static class UtauTests
{
    private static void CheckDecodedFrameReuse(Action<bool, string> check)
    {
        foreach (var rate in new[] { 22050, 44100, 48000, 192000 })
        foreach (var channels in new[] { 1, 2 })
        {
            const int frames = 4097;
            var raw = Enumerable.Range(0, frames * channels).Select(i => (short)(i * 7919 - 30000)).ToArray();
            var wave = new PcmAudio(raw, rate).ToWaveBytes();
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(22), (ushort)channels);
            BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), rate * channels * 2);
            BinaryPrimitives.WriteUInt16LittleEndian(wave.AsSpan(32), (ushort)(channels * 2));
            float At(int frame)
            {
                float value = 0;
                for (var channel = 0; channel < channels; channel++)
                    value += raw[Math.Min(frame, frames - 1) * channels + channel] / 32768f;
                return value / channels;
            }
            var expected = new float[(int)((long)frames * 44100 / rate)];
            double sum = 0;
            for (var i = 0; i < expected.Length; i++)
            {
                var source = (double)i * rate / 44100;
                var index = (int)source; var fraction = (float)(source - index);
                expected[i] = At(index) * (1 - fraction) + At(index + 1) * fraction;
                sum += expected[i];
            }
            var mean = (float)(sum / expected.Length);
            for (var i = 0; i < expected.Length; i++) expected[i] -= mean;
            check(UtauSpeech.DecodeWave(wave, default).Select(BitConverter.SingleToInt32Bits).SequenceEqual(
                expected.Select(BitConverter.SingleToInt32Bits)),
                $"WAV frame reuse preserves scalar interpolation and DC bits across rate/channel/end boundaries: rate={rate}, channels={channels}");
        }
        var floating = new PcmAudio(new short[4097 * 2], 44100).ToWaveBytes();
        BinaryPrimitives.WriteUInt16LittleEndian(floating.AsSpan(20), 3);
        BinaryPrimitives.WriteUInt16LittleEndian(floating.AsSpan(32), 4);
        BinaryPrimitives.WriteUInt16LittleEndian(floating.AsSpan(34), 32);
        BinaryPrimitives.WriteInt32LittleEndian(floating.AsSpan(28), 44100 * 4);
        foreach (var invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            BinaryPrimitives.WriteInt32LittleEndian(floating.AsSpan(44 + 4096 * 4), BitConverter.SingleToInt32Bits(invalid));
            var rejected = false;
            try { _ = UtauSpeech.DecodeWave(floating, default); }
            catch (InvalidDataException error) { rejected = error.Message == "WAVに不正な数値があります。"; }
            check(rejected, "WAV frame reuse still rejects a nonfinite adjacent frame before the 4096 output boundary");
        }
        using var stop = new CancellationTokenSource(); stop.Cancel();
        var cancelled = false;
        try { _ = UtauSpeech.DecodeWave(floating, stop.Token); }
        catch (OperationCanceledException error) { cancelled = error.CancellationToken == stop.Token; }
        check(cancelled, "WAV decoding retains caller cancellation before cached frame work");
    }

    private static void CheckListPeak(Action<bool, string> check)
    {
        var peak = typeof(UtauSpeech).GetMethod("OutputPeak",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<List<float>, float>>();
        var width = System.Numerics.Vector<float>.Count;
        var special = new[] { 0f, -0f, float.Epsilon, -float.Epsilon, float.NaN, float.PositiveInfinity,
            float.NegativeInfinity, float.MaxValue, -float.MaxValue, float.BitDecrement(0.0001f), 0.0001f, float.BitIncrement(0.0001f) };
        foreach (var length in new[] { 1, width - 1, width, width + 1, width * 2 + 1 })
        {
            var input = Enumerable.Repeat(0.5f, length).ToList();
            bool Same()
            {
                var original = input.Select(BitConverter.SingleToInt32Bits).ToArray();
                var capacity = input.Capacity;
                var expected = input.Max(x => Math.Abs(x));
                return BitConverter.SingleToInt32Bits(peak(input)) == BitConverter.SingleToInt32Bits(expected) &&
                    input.Count == length && input.Capacity == capacity &&
                    input.Select(BitConverter.SingleToInt32Bits).SequenceEqual(original);
            }
            var same = Same();
            for (var position = 0; position < length; position++)
            foreach (var value in special)
            {
                var original = input[position]; input[position] = value;
                same &= Same(); input[position] = original;
            }
            foreach (var value in special)
            {
                for (var i = 0; i < length; i++) input[i] = value;
                same &= Same();
            }
            check(same, $"UTAU list peak retains LINQ bits for finite, nonfinite, zero and silence-threshold lanes/tails without mutation: length={length}");
        }
        Exception? expectedError = null, actualError = null;
        try { _ = new List<float>().Max(x => Math.Abs(x)); } catch (Exception error) { expectedError = error; }
        try { _ = peak([]); } catch (Exception error) { actualError = error; }
        check(expectedError is not null && actualError is not null && actualError.GetType() == expectedError.GetType() && actualError.Message == expectedError.Message,
            "UTAU list peak preserves the original LINQ empty-list exception");
    }

    private static void CheckPcmQuantization(Action<bool, string> check)
    {
        var quantize = typeof(UtauSpeech).GetMethod("QuantizePcm",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .CreateDelegate<Func<List<float>, short[]>>();
        var integerBoundary = 2147483648f / 32767f; var shortBoundary = 32768f / 32767f;
        var special = new[] { 0f, -0f, float.Epsilon, -float.Epsilon, float.NaN, float.PositiveInfinity,
            float.NegativeInfinity, float.MaxValue, -float.MaxValue, integerBoundary, -integerBoundary,
            float.BitIncrement(integerBoundary), float.BitDecrement(integerBoundary), shortBoundary, -shortBoundary,
            float.BitIncrement(shortBoundary), float.BitDecrement(shortBoundary), 32767.5f / 32767f, -32767.5f / 32767f,
            0.5f / 32767f, -0.5f / 32767f };
        foreach (var length in new[] { 0, 1, 15, 16, 17, 31, 32, 33 })
        {
            var input = Enumerable.Range(0, length).Select(i => ((i % 101) - 50 + 0.5f) / 32767f).ToList();
            bool Same()
            {
                var original = input.Select(BitConverter.SingleToInt32Bits).ToArray();
                var expected = input.Select(x => (short)Math.Clamp((int)Math.Round(x * 32767), short.MinValue, short.MaxValue)).ToArray();
                return quantize(input).SequenceEqual(expected) && input.Select(BitConverter.SingleToInt32Bits).SequenceEqual(original);
            }
            var same = Same();
            foreach (var position in new[] { 0, 7, 8, 15, 16, length - 1 }.Where(i => i >= 0 && i < length).Distinct())
            foreach (var value in special)
            {
                var original = input[position]; input[position] = value;
                same &= Same(); input[position] = original;
            }
            check(same, $"UTAU PCM conversion retains scalar ToEven/int-cast/clamp results at half, short/int, nonfinite, lane and tail boundaries without changing its list: length={length}");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckDecodedFrameReuse(check);
        CheckListPeak(check);
        CheckPcmQuantization(check);
        // All PCM16 values, including endpoints, with an asymmetric final sample
        // exercise DC removal and the last-frame boundary at the native rate.
        var completeRange = Enumerable.Range(short.MinValue, 65536).Select(x => (short)x).Append((short)12345).ToArray();
        var nativeWave = new PcmAudio(completeRange, 44100).ToWaveBytes();
        var decodedRange = UtauSpeech.DecodeWave(nativeWave, default);
        var expectedMean = (float)(completeRange.Sum(x => (double)x / 32768) / completeRange.Length);
        check(decodedRange.Length == completeRange.Length && decodedRange.Select(BitConverter.SingleToInt32Bits).SequenceEqual(
            completeRange.Select(x => BitConverter.SingleToInt32Bits(x / 32768f - expectedMean))),
            "native-rate PCM16 retains every possible sample and exact DC subtraction including final frame");
        using var temporary = new TestDirectory();
        var root = temporary.DirectoryPath;
        var banks = await Voicebanks.LoadInstalledAsync(root);
        var recorder = new RecordingOutput();
        await using var voice = new UtauSpeech(recorder) { Voicebank = banks[0] };
        var watch = Stopwatch.StartNew();
        var reading = JapaneseReading.ToHiragana("今日は配信を見に来てくれてありがとう");
        var coldReading = watch.Elapsed.TotalMilliseconds;
        check(reading.Contains("はいしん") && reading.Contains("ありがと"), "UTAU dictionary converts kanji chat into kana");
        check(JapaneseReading.ToHiragana("こんにちは") == "こんにちわ", "UTAU dictionary resolves greeting pronunciation");
        check(JapaneseReading.ToHiragana("こんにちは😀") == "こんにちわ", "UTAU emoji is a boundary without breaking the readable greeting");
        check(JapaneseReading.ToHiragana("パーティー 123 ABC").Contains("ぱーてぃー") && JapaneseReading.ToHiragana("123 ABC").Contains("いちにさん"), "UTAU kana, numbers and Latin letters have readings");

        const string sample = "こんにちは。今日は配信を見に来てくれてありがとう。";
        watch.Restart();
        var audio = await voice.SynthesizeAsync(sample);
        var firstSynthesis = watch.Elapsed.TotalMilliseconds;
        check(audio.SampleRate == 44100 && audio.Samples.Length > 44100 && audio.Samples.Any(x => Math.Abs((int)x) > 1000), "bundled real WAVs produce audible PCM from Japanese kanji chat");
        check(audio.Samples.Length < 44100 * 15, "UTAU sample has bounded conversational duration");
        check(audio.Samples.All(x => x is > short.MinValue and < short.MaxValue), "real-bank synthesis retains headroom without PCM saturation");
        check(audio.Samples[0] == 0 && audio.Samples[^1] == 0, "utterance boundaries return to zero without an endpoint step");
        watch.Restart();
        var repeated = await voice.SynthesizeAsync(sample);
        var warmSynthesis = watch.Elapsed.TotalMilliseconds;
        check(audio.Samples.SequenceEqual(repeated.Samples), "UTAU cached synthesis preserves the actual bank output");
        await voice.SpeakAsync("テストです", CancellationToken.None);
        check(recorder.Calls == 1 && recorder.SampleCount > 4410, "UTAU SpeakAsync dispatches PCM to selected platform output");
        var spoken = await voice.SynthesizeAsync("テストです");
        check(recorder.SampleRate == spoken.SampleRate && recorder.Samples.SequenceEqual(spoken.Samples),
            "UTAU playback receives synthesized PCM unchanged without rate or sample conversion");
        Console.WriteLine($"UTAU reading: {reading}");
        Console.WriteLine($"UTAU measured on this machine: cold dictionary+reading {coldReading:F1} ms; first sample synthesis {firstSynthesis:F1} ms; warm {warmSynthesis:F1} ms; PCM duration {(double)audio.Samples.Length / audio.SampleRate:F2} s. Output-device latency is not included.");

        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Throws<OperationCanceledException>(() => voice.SynthesizeAsync("停止", cancelled.Token), check, "UTAU cancellation stops synthesis");
        voice.Voicebank = banks[0] with { Entries = banks[0].Entries.Where(e => e.Alias != "あ").ToArray() };
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check, "missing bank phoneme rejects whole utterance for OS fallback");
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("\u9fff"), check, "unknown kanji fails explicitly rather than disappearing");
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("𠮷田さん"), check, "unsupported supplementary kanji in a name is never silently omitted");
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync(new string('あ', 1025)), check, "UTAU rejects oversized input before allocation");

        // A deterministic fixture verifies actual sample rate conversion, negative
        // cutoff semantics and malformed RIFF boundaries, separately from real bank data.
        var fixtureDirectory = Path.Combine(root, "fixture");
        Directory.CreateDirectory(fixtureDirectory);
        var path = Path.Combine(fixtureDirectory, "a.wav");
        var tone = new PcmAudio(Enumerable.Range(0, 22050).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 220 / 22050) * 10000)).ToArray(), 22050);
        await File.WriteAllBytesAsync(path, tone.ToWaveBytes());
        var entry = new OtoEntry(path, "あ", 20, 80, -250, 15, 8);
        voice.Voicebank = new("fixture", fixtureDirectory, [entry], 0);
        var fixture = await voice.SynthesizeAsync("あ");
        check(fixture.SampleRate == 44100 && fixture.Samples.Length is > 4410 and < 13230, "WAV resampling and negative oto cutoff produce finite speech segment");
        var frequency = PositiveCrossings(fixture.Samples) * (double)fixture.SampleRate / fixture.Samples.Length;
        check(frequency is > 195 and < 245, "sample-rate conversion and duration shaping retain the recorded vowel pitch");
        var extended = await voice.SynthesizeAsync("あー");
        check(extended.Samples.Length > fixture.Samples.Length * 1.4 && extended.Samples.Length < fixture.Samples.Length * 2.6 &&
            Rms(extended.Samples.AsSpan(Math.Min(fixture.Samples.Length, extended.Samples.Length))) > Rms(fixture.Samples) * 0.15,
            "long vowel extends voiced sound instead of dropping the length mark or adding silence");
        var closure = await voice.SynthesizeAsync("あっあ");
        check(MaximumQuietRun(closure.Samples) >= fixture.SampleRate * 0.05,
            "sokuon retains a real closure rather than crossfading away the consonant pause");
        var punctuated = await voice.SynthesizeAsync("あ、あ");
        check(MaximumQuietRun(punctuated.Samples) >= fixture.SampleRate * 0.075,
            "punctuation leaves an audible pause between voiced units");
        var silencePath = Path.Combine(fixtureDirectory, "silence.wav");
        await File.WriteAllBytesAsync(silencePath, new PcmAudio(new short[22050], 22050).ToWaveBytes());
        voice.Voicebank = voice.Voicebank with { Entries = [entry with { WavePath = silencePath }] };
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check, "silent source is rejected for OS fallback instead of successful silence");
        voice.Voicebank = voice.Voicebank with { Entries = [entry with { Cutoff = 2000 }] };
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check, "out-of-range oto timing is rejected");
        voice.Voicebank = voice.Voicebank with { Entries = [entry] };
        var corrupt = tone.ToWaveBytes();
        corrupt[40] = 0xff; corrupt[41] = 0xff; corrupt[42] = 0xff; corrupt[43] = 0x7f;
        await File.WriteAllBytesAsync(path, corrupt.Concat(new byte[] { 0 }).ToArray());
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check, "truncated WAV chunk is rejected and never reported as synthesized");
        var outside = Path.Combine(root, "outside.wav");
        voice.Voicebank = voice.Voicebank with { Entries = [entry with { WavePath = outside }] };
        await Throws<InvalidDataException>(() => voice.SynthesizeAsync("あ"), check, "synthesis rechecks voicebank containment");

        await CheckWaveformQualityAsync(voice, fixtureDirectory, check);
    }

    private static async Task CheckWaveformQualityAsync(UtauSpeech voice, string directory, Action<bool, string> check)
    {
        const int rate = 44100;
        // A short fricative marker lives inside the fixed region, after preutterance.
        var voicedPrefix = Enumerable.Range(0, rate * 90 / 1000)
            .Select(i => (float)(0.2 * Math.Sin(i * 2 * Math.PI * 220 / rate))).ToArray();
        check(UtauPitch.IsContinuouslyVoiced(voicedPrefix, rate / 220.0, CancellationToken.None),
            "continuous voiced onset permits pitch shaping across the prefix");
        foreach (var closureMs in new[] { 5, 10 })
        {
            var shortClosure = (float[])voicedPrefix.Clone();
            Array.Clear(shortClosure, rate * 55 / 1000, rate * closureMs / 1000);
            check(!UtauPitch.IsContinuouslyVoiced(shortClosure, rate / 220.0, CancellationToken.None),
                "brief interior closure is not hidden by whole-prefix periodicity");
        }
        // Jumping from pre+25 ms straight to consonant=110 ms deletes it entirely.
        var transition = Enumerable.Range(0, rate / 2).Select(i =>
        {
            var seconds = (double)i / rate;
            var frequency = seconds is >= 0.055 and < 0.100 ? 3000 : 220;
            return (short)(Math.Sin(seconds * 2 * Math.PI * frequency) * 6000);
        }).ToArray();
        var transitionPath = Path.Combine(directory, "transition.wav");
        await File.WriteAllBytesAsync(transitionPath, new PcmAudio(transition, rate).ToWaveBytes());
        var transitionEntry = new OtoEntry(transitionPath, "か", 0, 110, -420, 15, 8);
        voice.Voicebank = new("transition", directory, [transitionEntry], 0);
        var rendered = await voice.SynthesizeAsync("か");
        check(MaximumCrossings(rendered.Samples, rate / 50) >= 35,
            "duration shaping preserves a fricative transition inside the fixed region after preutterance");
        // This bank intentionally has no separate あ. A long vowel extends the
        // preceding sound's sustain, rather than restarting another recorded onset.
        var sustained = await voice.SynthesizeAsync("かー");
        check(sustained.Samples.Length > rendered.Samples.Length + rate * 0.05 &&
            MaximumCrossings(sustained.Samples.AsSpan(rendered.Samples.Length), rate / 50) < 20,
            "long vowel sustains the preceding recording without a vowel alias or a repeated consonant onset");

        // A DC-biased source must not become a large baseline step at a pause.
        var biased = Enumerable.Range(0, rate / 2)
            .Select(i => (short)(6000 + Math.Sin(i * 2 * Math.PI * 220 / rate) * 6000)).ToArray();
        var biasedPath = Path.Combine(directory, "dc.wav");
        await File.WriteAllBytesAsync(biasedPath, new PcmAudio(biased, rate).ToWaveBytes());
        voice.Voicebank = new("dc", directory, [transitionEntry with { WavePath = biasedPath, Alias = "あ" }], 0);
        var centered = await voice.SynthesizeAsync("あ");
        check(Math.Abs(centered.Samples.Average(x => (double)x)) < Rms(centered.Samples) * 0.1,
            "DC-biased source is centered before gain and utterance envelopes");

        await CheckTempoAndPitchAsync(voice, directory, check);
        await CheckJoinContinuityAsync(voice, directory, check);
    }

    private static async Task CheckJoinContinuityAsync(UtauSpeech voice, string directory, Action<bool, string> check)
    {
        const int rate = 44100;
        var steadyPath = Path.Combine(directory, "join-steady.wav");
        var steady = Enumerable.Range(0, rate).Select(i => (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 6000)).ToArray();
        await File.WriteAllBytesAsync(steadyPath, new PcmAudio(steady, rate).ToWaveBytes());
        var a = new OtoEntry(steadyPath, "あ", 0, 389, -900, 239, 20);
        voice.Voicebank = new("join-endpoint", directory, [a], 0);
        var compressed = await voice.SynthesizeAsync("あ");
        var maximumStep = 0;
        for (var i = 1; i < compressed.Samples.Length; i++)
            maximumStep = Math.Max(maximumStep, Math.Abs((int)compressed.Samples[i] - compressed.Samples[i - 1]));
        check(maximumStep < Rms(compressed.Samples) * 0.45,
            "compressing a continuous vowel does not introduce a large one-sample step at independently rendered region joins");

        var voicedPath = Path.Combine(directory, "join-voiced.wav");
        var voiced = Enumerable.Range(0, rate).Select(i =>
            (short)(i < rate * 90 / 1000 ? Math.Sin(i * 2 * Math.PI * 220 / rate) * 600 : steady[i])).ToArray();
        await File.WriteAllBytesAsync(voicedPath, new PcmAudio(voiced, rate).ToWaveBytes());
        a = a with { Consonant = 80, Preutterance = 20, Overlap = 15 };
        var nasal = new OtoEntry(voicedPath, "な", 0, 180, -900, 90, 10);
        voice.Voicebank = new("join-active", directory, [a, nasal], 0);
        var connected = await voice.SynthesizeAsync("あな");
        // First vowel anchor is 20 ms, the next is 200 ms. Its weak but active
        // voiced lead begins at 110 ms; do not erase the old vowel at 120 ms and
        // leave an artificial low-energy hole throughout the rest of that lead.
        // This fixture does not classify fricatives or real noisy stop closures.
        var before = Rms(connected.Samples.AsSpan(rate * 60 / 1000, rate * 30 / 1000));
        var middle = Rms(connected.Samples.AsSpan(rate * 145 / 1000, rate * 30 / 1000));
        check(middle > before * 0.18,
            "correlated voiced lead does not lose the preceding vowel prematurely at a short oto overlap mark");

        var closurePath = Path.Combine(directory, "join-closure.wav");
        var closure = (short[])steady.Clone();
        Array.Clear(closure, rate * 10 / 1000, rate * 80 / 1000);
        await File.WriteAllBytesAsync(closurePath, new PcmAudio(closure, rate).ToWaveBytes());
        voice.Voicebank = new("join-closure", directory, [a, nasal with { WavePath = closurePath, Alias = "か" }], 0);
        var closed = await voice.SynthesizeAsync("あか");
        check(MaximumQuietRun(closed.Samples) >= rate * 25 / 1000,
            "join smoothing preserves a recorded interior stop closure instead of filling every quiet consonant region");
    }

    private static async Task CheckTempoAndPitchAsync(UtauSpeech voice, string directory, Action<bool, string> check)
    {
        const int rate = 44100;
        var tonePath = Path.Combine(directory, "tempo-220.wav");
        await File.WriteAllBytesAsync(tonePath, new PcmAudio(Enumerable.Range(0, rate)
            .Select(i => (short)(Math.Sin(i * 2 * Math.PI * 220 / rate) * 6000)).ToArray(), rate).ToWaveBytes());
        var a = new OtoEntry(tonePath, "あ", 0, 80, -900, 20, 15);
        var vowelI = a with { Alias = "い" };
        voice.Voicebank = new("tempo", directory, [a, vowelI], 0);
        var compact = await voice.SynthesizeAsync("あいあいあい");
        voice.Voicebank = voice.Voicebank with { Entries = [a with { Consonant = 480 }, vowelI with { Consonant = 480 }] };
        var held = await voice.SynthesizeAsync("あいあいあい");
        check(held.Samples.Length < compact.Samples.Length * 1.4 && held.Samples.Length > compact.Samples.Length * 0.7 &&
            held.Samples.Length is > rate * 7 / 10 and < rate * 2,
            "long fixed vowel region does not multiply conversational mora timing");

        var higherPath = Path.Combine(directory, "pitch-330.wav");
        await File.WriteAllBytesAsync(higherPath, new PcmAudio(Enumerable.Range(0, rate)
            .Select(i => (short)(Math.Sin(i * 2 * Math.PI * 330 / rate) * 6000)).ToArray(), rate).ToWaveBytes());
        voice.Voicebank = new("pitch", directory, [a, vowelI with { WavePath = higherPath }], 0);
        var mixed = await voice.SynthesizeAsync("あいあいあい");
        var pitches = StablePitches(mixed);
        check(pitches.Length >= 6 && pitches.Min() > 150 && pitches.Max() < 500 && pitches.Max() / pitches.Min() < 1.42,
            "different recorded vowel pitches converge within a bounded range instead of alternating by seven semitones");

        voice.Voicebank = new("prosody", directory, [a], 0);
        var statement = StablePitches(await voice.SynthesizeAsync("あああ。"));
        var question = StablePitches(await voice.SynthesizeAsync("あああ？"));
        check(statement.Length >= 6 && statement.Take(3).Average() > statement.TakeLast(3).Average() * 1.03,
            "statement has a mild phrase decline on a constant-pitch source");
        check(statement.Length >= 6 && question.Length >= 6 &&
            question.TakeLast(3).Average() > statement.TakeLast(3).Average() * 1.04,
            "question ending rises above the same statement without claiming lexical accent correctness");
    }

    private static double[] StablePitches(PcmAudio audio)
    {
        var result = new List<double>();
        var window = audio.SampleRate / 20;
        for (var start = window; start + window < audio.Samples.Length; start += audio.SampleRate * 35 / 1000)
        {
            var frame = audio.Samples.AsSpan(start, window);
            if (Rms(frame) < 500) continue;
            var pitch = EstimatePitch(frame, audio.SampleRate);
            if (pitch > 0) result.Add(pitch);
        }
        return result.ToArray();
    }

    private static double EstimatePitch(ReadOnlySpan<short> samples, int rate)
    {
        var from = rate / 550;
        var to = Math.Min(rate / 140, samples.Length / 3);
        var scores = new double[to + 1];
        var maximum = -1.0;
        for (var lag = from; lag <= to; lag++)
        {
            double dot = 0, left = 0, right = 0;
            for (var i = 0; i + to < samples.Length; i += 2)
            {
                var x = (double)samples[i]; var y = (double)samples[i + lag];
                dot += x * y; left += x * x; right += y * y;
            }
            scores[lag] = dot / Math.Sqrt(Math.Max(1.0, left * right));
            maximum = Math.Max(maximum, scores[lag]);
        }
        // Use the first strong local peak rather than a subharmonic at two periods.
        for (var lag = from + 1; lag < to; lag++)
            if (scores[lag] >= scores[lag - 1] && scores[lag] >= scores[lag + 1] && scores[lag] >= Math.Max(0.8, maximum * 0.92))
                return (double)rate / lag;
        return 0;
    }

    // Explicit review only; the unpitched control changes no production default.
    public static async Task WriteSampleAsync(string destination, bool withoutPitch = false)
    {
        using var temporary = new TestDirectory();
        var banks = await Voicebanks.LoadInstalledAsync(temporary.DirectoryPath);
        await using var voice = new UtauSpeech(new RecordingOutput()) { Voicebank = banks[0], ApplyPitch = !withoutPitch };
        const string reviewText = "すーっと、しーっと、こんにちは。今日は配信を見に来てくれてありがとう。";
        var audio = await voice.SynthesizeAsync(reviewText);
        var path = Path.GetFullPath(destination);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await output.WriteAsync(audio.ToWaveBytes());
        Console.WriteLine($"UTAU review text: {reviewText}");
        Console.WriteLine($"UTAU pitch shaping: {!withoutPitch}");
        Console.WriteLine($"UTAU sample written: {path}");
    }

    private static int PositiveCrossings(short[] samples)
    {
        var crossings = 0;
        for (var i = 1; i < samples.Length; i++) if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
        return crossings;
    }
    private static int MaximumCrossings(ReadOnlySpan<short> samples, int window)
    {
        var maximum = 0;
        for (var start = 0; start + window <= samples.Length; start += window / 2)
        {
            var crossings = 0;
            for (var i = start + 1; i < start + window; i++) if (samples[i - 1] <= 0 && samples[i] > 0) crossings++;
            maximum = Math.Max(maximum, crossings);
        }
        return maximum;
    }
    private static double Rms(ReadOnlySpan<short> samples)
    {
        double energy = 0;
        foreach (var sample in samples) energy += (double)sample * sample;
        return samples.IsEmpty ? 0 : Math.Sqrt(energy / samples.Length);
    }
    private static int MaximumQuietRun(short[] samples)
    {
        var longest = 0; var run = 0;
        foreach (var sample in samples)
        {
            run = Math.Abs((int)sample) < 25 ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }

    private static async Task Throws<T>(Func<Task> action, Action<bool, string> check, string name) where T : Exception
    {
        try { await action(); }
        catch (T) { check(true, name); return; }
        check(false, name);
    }

    private sealed class RecordingOutput : IPcmOutput
    {
        public int Calls { get; private set; }
        public int SampleCount { get; private set; }
        public int SampleRate { get; private set; }
        public short[] Samples { get; private set; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; SampleCount = samples.Length; SampleRate = sampleRate; Samples = samples.ToArray(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
