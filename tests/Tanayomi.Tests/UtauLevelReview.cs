using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauLevelReview
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private static int Ms(int value) => Rate * value / 1000;

    internal static async Task RunAsync(string output, Action<bool, string> check)
    {
        output = Path.GetFullPath(output);
        var root = Path.GetFullPath(Path.Combine(".local", "previews"));
        if (!string.Equals(Path.GetDirectoryName(output), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(output).StartsWith("r48-", StringComparison.Ordinal) ||
            Directory.Exists(output) || File.Exists(output))
            throw new InvalidDataException("Use a fresh R48 preview directory.");
        WorldComparison.RejectLinks(root);
        WorldComparison.ConfigureWorker();
        using var temp = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        var previews = new List<(string Name, short[] Before, short[] After)>();
        foreach (var (name, phrase) in new[] {
            ("01-full", "こんにちは。たなよみの読み上げテストです。"),
            ("02-reading", "この前、青い布で大きな帽子を作りました。やわらかな風が吹くと、ひらひら揺れます。"),
            ("03-control", "た。の。さ。が。ふ。あっか。あさあ。") })
        {
            var (audio, anchors) = await UtauSpeech.SynthesizeLevelReviewAsync(phrase, bank, default);
            var original = audio.Samples;
            var result = (short[])original.Clone();
            var edited = new bool[original.Length];
            var phraseStart = 0;
            foreach (var pause in anchors.Where(a => a.Alias is null))
            {
                Smooth(original, result, edited, phraseStart, pause.StartSample);
                phraseStart = pause.EndSample;
            }
            Smooth(original, result, edited, phraseStart, original.Length);
            check(original.Length == result.Length, "equal utterance duration");
            check(Enumerable.Range(0, original.Length).All(k => edited[k] || original[k] == result[k]),
                "outside periodic runs, PCM stays exact");
            check(anchors.Where(a => a.Alias is null).All(a =>
                original.AsSpan(a.StartSample, a.EndSample - a.StartSample).SequenceEqual(result.AsSpan(a.StartSample, a.EndSample - a.StartSample))),
                "explicit pauses and geminate silence are sample-identical");
            check(result.All(s => s is > short.MinValue and < short.MaxValue), "no clipping");
            var before = new List<double>(); var after = new List<double>();
            var changed = 0;
            foreach (var a in anchors.Where(a => a.VowelOnsetSample.HasValue))
            {
                var onset = a.VowelOnsetSample!.Value;
                var l = Math.Max(a.StartSample, onset - Ms(55));
                var r = Math.Min(a.EndSample, Math.Min(original.Length, onset + Ms(64)));
                if (l >= r) continue;
                var acuityA = AccumulateSteps(original, edited, l, r, before);
                var acuityB = AccumulateSteps(result, edited, l, r, after);
                if (acuityA - acuityB >= .5) changed++;
            }
            var a90 = Percentile(before, .90);
            var b90 = Percentile(after, .90);
            check(a90 > b90 && changed > 0, "the measured level steps near onsets decrease");
            Console.WriteLine($"SUMMARY {name} {anchors.Count(a => a.Alias is not null)} onsets: {changed} improved >=0.5 dB; 90th percentile of voiced 10 ms level steps: {a90:F2} -> {b90:F2} dB; {edited.Count(x => x)*1000.0/Rate:F0} ms eligible");
            previews.Add((name, original, result));
        }
        Directory.CreateDirectory(output);
        foreach (var (name, before, after) in previews)
        {
            await File.WriteAllBytesAsync(Path.Combine(output, name + "-A-original.wav"), new PcmAudio(before, Rate).ToWaveBytes());
            await File.WriteAllBytesAsync(Path.Combine(output, name + "-B-smooth.wav"), new PcmAudio(after, Rate).ToWaveBytes());
        }
        Console.WriteLine($"OUTPUT {output}");
    }

    private static void Smooth(short[] input, short[] output, bool[] edited, int start, int end)
    {
        var hop = Ms(4);
        var half = Ms(8);
        var n = (end - start) / hop;
        if (n < 12) return;
        var level = new double[n];
        var voiced = new bool[n];
        for (int i = 2; i < n - 2; i++)
        {
            var pos = start + i * hop;
            var window = input.AsSpan(pos - half + 2, 2 * half - 4);
            level[i] = Db(Rms(window));
            voiced[i] = level[i] >= -44 && IsPeriodic(window);
        }
        for (int l = 2; l < n - 2;)
        {
            if (!voiced[l]) { l++; continue; }
            var r = l + 1;
            while (r < n - 2 && voiced[r]) r++;
            if (r - l < 10) { l = r; continue; }
            SmoothRun(output, edited, start, hop, level, l, r);
            l = r;
        }
    }

    private static void SmoothRun(short[] output, bool[] edited, int start, int hop, double[] level, int l, int r)
    {
        var correction = new double[r - l];
        Span<double> nearby = stackalloc double[33];
        for (int i = l; i < r; i++)
        {
            var a = Math.Max(l, i - 16); var b = Math.Min(r, i + 17);
            var count = b - a;
            level.AsSpan(a, count).CopyTo(nearby);
            nearby[..count].Sort();
            var target = nearby[(int)((count - 1) * .6)];
            // Bounded upward gain only within periodic sound; leave deep dips alone.
            var delta = target - level[i];
            var boostGate = Ease((14 - delta) / 6);
            correction[i - l] = delta > 0
                ? Math.Min(3.5, delta * .85) * boostGate : Math.Max(-7, delta * .85);
        }
        var gain = new double[r - l];
        for (int i = 1; i < gain.Length - 1; i++)
        {
            double sum = 0, weight = 0;
            for (int j = -2; j <= 2; j++)
            {
                int k = i + j;
                if ((uint)k >= (uint)gain.Length) continue;
                var w = 3 - Math.Abs(j);
                sum += w * correction[k]; weight += w;
            }
            gain[i] = sum / weight * Ease(i / 3.0) * Ease((gain.Length - 1 - i) / 3.0);
        }
        // Leave extra headroom for a smoothly varying gain without sample limiting.
        var positive = 1.0;
        for (int i = 0; i < gain.Length - 1; i++)
        {
            var upperDb = Math.Max(gain[i], gain[i + 1]);
            if (upperDb <= 0) continue;
            var pos = start + (l + i) * hop;
            for (int j = 0; j < hop; j++)
            {
                var s = Math.Abs((int)output[pos + j]);
                if (s > 0) positive = Math.Min(positive, Math.Max(0, 20 * Math.Log10(32000.0 / s) / upperDb));
            }
        }
        for (int i = 0; i < gain.Length - 1; i++)
        {
            var x = gain[i] > 0 ? gain[i] * positive : gain[i];
            var y = gain[i + 1] > 0 ? gain[i + 1] * positive : gain[i + 1];
            var pos = start + (l + i) * hop;
            for (int j = 0; j < hop; j++)
            {
                var db = x + (y - x) * Ease(j / (double)hop);
                output[pos + j] = (short)Math.Round(output[pos + j] * Math.Pow(10, db / 20));
            }
            edited.AsSpan(pos, hop).Fill(true);
        }
    }

    internal static bool IsPeriodic(ReadOnlySpan<short> window)
    {
        // Downsample for correlation only. Remove the frame offset without modifying PCM.
        Span<double> x = stackalloc double[176];
        var length = window.Length / 4;
        double avg = 0;
        for (int i = 0; i < length; i++) { x[i] = (window[i * 4] + window[i * 4 + 1] + window[i * 4 + 2] + window[i * 4 + 3]) * .25; avg += x[i]; }
        avg /= length;
        for (int i = 0; i < length; i++) x[i] -= avg;
        // Require an actual correlation maximum, not only one high value at the lag limit.
        double prev = 0, rising = 0;
        for (int lag = Rate / (4 * 580); lag <= Rate / (4 * 90); lag++)
        {
            double a = 0, b = 0, ab = 0;
            for (int i = 0; i < length - lag; i++)
            {
                var p = x[i]; var q = x[i + lag];
                a += p * p; b += q * q; ab += p * q;
            }
            var current = ab / Math.Sqrt(Math.Max(1, a * b));
            if (rising > 0 && prev >= .78 && current < prev) return true;
            rising = current - prev; prev = current;
        }
        return false;
    }

    private static double AccumulateSteps(short[] samples, bool[] mask, int start, int end, List<double> all)
    {
        var peak = 0.0;
        var s = Ms(10);
        for (int i = start; i + 2 * s <= end; i += Ms(4))
        {
            if (mask.AsSpan(i, 2 * s).Contains(false)) continue;
            var difference = Math.Abs(Db(Rms(samples.AsSpan(i, s))) - Db(Rms(samples.AsSpan(i + s, s))));
            peak = Math.Max(peak, difference); all.Add(difference);
        }
        return peak;
    }

    private static double Percentile(List<double> data, double q)
    {
        if (data.Count == 0) throw new InvalidDataException("No voiced samples in the review windows.");
        data.Sort(); return data[(int)((data.Count - 1) * q)];
    }
    private static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t * t * (3 - 2 * t);
    }
    private static double Rms(ReadOnlySpan<short> samples)
    {
        double sum = 0;
        foreach (int s in samples) sum += (double)s * s;
        return Math.Sqrt(sum / samples.Length);
    }
    private static double Db(double rms) => 20 * Math.Log10(Math.Max(1, rms) / 32768);
}
