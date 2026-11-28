using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauSpectralJoinAudit
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private static int Ms(int value) => Rate * value / 1000;

    private sealed record Analysis(double[] F0, double[] Times, double[] Spectrum, double[] Noise, int Fft)
    {
        internal int Bins => Fft / 2 + 1;
    }

    internal static async Task RunAsync(string library, string output, Action<bool, string> check)
    {
        library = Path.GetFullPath(library);
        output = Path.GetFullPath(output);
        var previewRoot = Path.GetFullPath(Path.Combine(".local", "previews"));
        if (!string.Equals(Path.GetDirectoryName(output), previewRoot, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(output).StartsWith("r47-", StringComparison.Ordinal) ||
            Directory.Exists(output) || File.Exists(output)) throw new InvalidDataException("Use a fresh R47 preview directory.");
        WorldComparison.RejectLinks(previewRoot);
        WorldComparison.RejectLinks(library);
        WorldComparison.ConfigureWorker();
        using var temp = new TestDirectory();
        using var native = new WorldComparison.NativeApi(library);
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        var previews = new List<(string Name, short[] A, short[] B, short[] C)>();
        foreach (var item in new[] { ("01-tanayomi", "たなよみ。"), ("02-full", "こんにちは。たなよみの読み上げテストです。"), ("03-reading", "この前、青い布で大きな帽子を作りました。やわらかな風が吹くと、ひらひら揺れます。"), ("04-vowels", "あいうえお。"), ("control", "あっか。あさあ。") })
        {
            var (audio, joins) = await UtauSpeech.SynthesizeSpectralJoinReviewAsync(item.Item2, bank, default);
            var a = audio.Samples;
            var b = (short[])a.Clone(); var c = (short[])a.Clone();
            var masks = new bool[a.Length]; var count = 0;
            Console.WriteLine($"{item.Item1}: {joins.Count} candidates, {a.Length / (double)Rate:F2}s");
            foreach (var j in joins)
            {
                // End ahead of the original vowel onset; preserve the donor glide and following vowel.
                var length = Math.Min(Ms(20), j.Overlap - Ms(7));
                var start = j.Start - Ms(65);
                var stop = j.Start + length + Ms(65);
                if (length < Ms(10) || start < 0 || stop > a.Length || masks.AsSpan(j.Start, length).Contains(true)) continue;
                var left = Analyze(native, Array.ConvertAll(j.Prior, x => (double)x));
                var right = Analyze(native, Array.ConvertAll(j.Incoming, x => (double)x));
                var local = Analyze(native, Array.ConvertAll(a[start..stop], x => x / 32768.0));
                var li = Nearest(left, 0.100);
                var ri = Nearest(right, Math.Min(0.017, (j.Overlap - Ms(7)) / (double)Rate));
                var first = Ms(65) / (double)Rate;
                var last = (Ms(65) + length) / (double)Rate;
                var edited = Enumerable.Range(0, local.F0.Length).Where(k => local.Times[k] > first && local.Times[k] < last).ToArray();
                if (left.F0[li] <= 0 || right.F0[ri] <= 0 || edited.Length < 2 || edited.Any(k => local.F0[k] <= 0))
                {
                    Console.WriteLine($"Skip {item.Item1} {j.Alias} @{j.Start * 1000.0 / Rate:F1} ms: F0={left.F0[li]:F0}/{right.F0[ri]:F0}, local {edited.Count(k => local.F0[k] > 0)}/{edited.Length}");
                    continue;
                }
                var spectra = (double[])local.Spectrum.Clone();
                foreach (var k in edited)
                {
                    var t = (local.Times[k] - first) / (last - first);
                    for (var bin = 0; bin < local.Bins; bin++)
                    {
                        var x = Math.Log(Math.Max(1e-12, left.Spectrum[li * local.Bins + bin]));
                        var y = Math.Log(Math.Max(1e-12, right.Spectrum[ri * local.Bins + bin]));
                        spectra[k * local.Bins + bin] = Math.Exp(x + t * (y - x));
                    }
                }
                var plain = Render(native, local, local.Spectrum, stop - start);
                var bridge = Render(native, local, spectra, stop - start);
                var k0 = j.Start - start;
                var gain = Rms(Array.ConvertAll(a[j.Start..(j.Start + length)], v => v / 32768.0)) /
                    Math.Max(1e-6, Rms(plain.AsSpan(k0, length)));
                if (!double.IsFinite(gain) || gain is < 0.4 or > 2.5) continue;
                var candidateB = new short[length]; var candidateC = new short[length];
                var safe = true;
                for (var i = 0; i < length; i++)
                {
                    var w = Math.Min(1.0, Math.Min(i, length - 1 - i) / (double)Ms(4));
                    w *= w * (3 - 2 * w);
                    var input = a[j.Start + i] / 32768.0;
                    var x = input * (1 - w) + plain[k0 + i] * gain * w;
                    var y = input * (1 - w) + bridge[k0 + i] * gain * w;
                    if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) > 0.98 || Math.Abs(y) > 0.98) { safe = false; break; }
                    candidateB[i] = (short)Math.Round(x * 32768);
                    candidateC[i] = (short)Math.Round(y * 32768);
                }
                if (!safe || candidateB.SequenceEqual(candidateC)) continue;
                candidateB.CopyTo(b, j.Start); candidateC.CopyTo(c, j.Start);
                masks.AsSpan(j.Start, length).Fill(true);
                count++;
                Console.WriteLine($"AUDIT {item.Item1} {j.Alias}: {j.Start * 1000.0 / Rate:F1} ms; bridge={length * 1000.0 / Rate:F1} ms; {edited.Length} voiced frames");
            }
            check(Enumerable.Range(0, a.Length).All(k => masks[k] || a[k] == b[k] && a[k] == c[k]),
                "both comparison signals are exact outside editable voiced windows");
            check(a.Length == b.Length && a.Length == c.Length, "all variants keep identical speech and pause length");
            if (item.Item1 == "control") check(count == 0 && a.SequenceEqual(c), "fricative and geminate controls never enter the interpolation");
            else if (count > 0) previews.Add((item.Item1, a, b, c));
        }
        check(previews.Any(p => p.Name is "01-tanayomi" or "02-full"), "the reported utterance exercises a real interpolation");
        Directory.CreateDirectory(output);
        foreach (var (name, a, b, c) in previews)
        {
            foreach (var (label, data) in new[] { ("A-original", a), ("B-world-only", b), ("C-linear", c) })
                await File.WriteAllBytesAsync(Path.Combine(output, name + "-" + label + ".wav"), new PcmAudio(data, Rate).ToWaveBytes());
        }
        Console.WriteLine($"OUTPUT {output}");
    }

    private static int Nearest(Analysis a, double time) => Enumerable.Range(0, a.Times.Length).MinBy(i => Math.Abs(a.Times[i] - time));

    private static double Rms(ReadOnlySpan<double> data)
    {
        double sum = 0;
        foreach (var v in data) sum += v * v;
        return Math.Sqrt(sum / data.Length);
    }

    private static Analysis Analyze(WorldComparison.NativeApi native, double[] data)
    {
        if (native.Layout(data.Length, out var frames, out var fft) != 0 || frames < 2 || fft != 2048)
            throw new InvalidDataException("WORLD layout failed");
        var n = frames * (fft / 2 + 1);
        var a = new Analysis(new double[frames], new double[frames], new double[n], new double[n], fft);
        if (native.AnalyzeFast(data, data.Length, a.F0, frames, a.Times, frames, a.Spectrum, n, a.Noise, n, fft) != 0)
            throw new InvalidDataException("WORLD analysis failed");
        return a;
    }

    private static double[] Render(WorldComparison.NativeApi native, Analysis a, double[] s, int count)
    {
        var wav = new double[count];
        if (native.Synthesize(a.F0, a.F0.Length, s, s.Length, a.Noise, a.Noise.Length, a.Fft, wav, count) != 0)
            throw new InvalidDataException("WORLD synthesis failed");
        return wav;
    }
}
