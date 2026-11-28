using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauMouthReview
{
    private const int Rate = UtauSpeech.OutputSampleRate;
    private static int Ms(int x) => (int)(Rate * (long)x / 1000);
    private enum Gesture { Vowel, Glide, LipNasal, LipStop, LipFriction, Tongue, Nasal, Other }
    private readonly record struct Mouth(Gesture Kind, double Opens, double Ends);
    private readonly record struct Move(int Start, int Center, int End, double Before, double After);
    private static readonly Dictionary<char, Mouth> Map = MouthMap();

    private static Dictionary<char, Mouth> MouthMap()
    {
        var map = new Dictionary<char, Mouth>();
        double[] vowels = [1, .30, .24, .75, .58]; // a, i, u, e, o: model values, not millimeters.
        void Row(string kana, Gesture kind, double opening, int[]? columns = null)
        {
            for (int i = 0; i < kana.Length; i++)
            {
                var end = vowels[columns is null ? i : columns[i]];
                map.Add(kana[i], new(kind, kind == Gesture.Vowel ? end : opening, end));
            }
        }
        Row("あいうえお", Gesture.Vowel, 0);
        Row("かきくけこ", Gesture.Tongue, .55);
        Row("さしすせそ", Gesture.Tongue, .43);
        Row("たちつてと", Gesture.Tongue, .55);
        Row("なにぬねの", Gesture.Nasal, .53);
        Row("はひふへほ", Gesture.Other, .73);
        map['ふ'] = new(Gesture.LipFriction, .18, .24);
        Row("まみむめも", Gesture.LipNasal, 0);
        Row("やゆよ", Gesture.Glide, .30, [0, 2, 4]);
        Row("らりるれろ", Gesture.Tongue, .57);
        Row("わ", Gesture.Glide, .26, [0]);
        Row("ゐゑを", Gesture.Vowel, 0, [1, 3, 4]);
        map['ん'] = new(Gesture.Other, .50, .50); // Context-dependent. It is not made a fixed lip closure.
        Row("がぎぐげご", Gesture.Tongue, .55);
        Row("ざじずぜぞ", Gesture.Tongue, .43);
        Row("だぢづでど", Gesture.Tongue, .55);
        Row("ばびぶべぼ", Gesture.LipStop, 0);
        Row("ぱぴぷぺぽ", Gesture.LipStop, 0);
        return map;
    }

    private static Mouth? Describe(string? alias)
    {
        if (string.IsNullOrEmpty(alias) || !Map.TryGetValue(alias[0], out var mouth)) return null;
        return alias.Length == 2 && alias[1] is 'ゃ' or 'ゅ' or 'ょ'
            ? mouth with { Ends = Map[(char)(alias[1] + 1)].Ends } : mouth;
    }

    private static Move? Plan(UtauTimingAnchor previous, UtauTimingAnchor next)
    {
        var prior = Describe(previous.Alias);
        var incoming = Describe(next.Alias);
        if (prior is null || incoming is not Mouth current ||
            previous.VowelOnsetSample is not int priorVowel || next.VowelOnsetSample is not int vowel)
            return null;
        var close = Math.Max(0, prior.Value.Ends - current.Opens);
        var shape = current.Kind;
        if (shape is Gesture.Tongue or Gesture.Nasal or Gesture.Other || close < .18)
            return null;
        var pivot = shape is Gesture.Vowel or Gesture.Glide
            ? Math.Max(next.StartSample, vowel - Ms(8))
            : shape == Gesture.LipNasal ? Math.Max(next.StartSample, vowel - Ms(42)) : next.StartSample;
        var begin = Math.Max(priorVowel + Ms(18), pivot - Ms(62));
        var end = shape switch
        {
            Gesture.LipStop or Gesture.LipFriction => pivot, // Dry recorded burst/frication and release.
            Gesture.LipNasal => vowel + Ms(53),
            _ => vowel + Ms(42)
        };
        if (pivot - begin < Ms(17)) return null;
        var before = shape is Gesture.LipNasal or Gesture.LipStop ? .90 : Math.Min(.86, 1.20 * close);
        var after = shape == Gesture.LipNasal ? .98 : shape is Gesture.LipStop or Gesture.LipFriction ? 0 : before * .76;
        return new(begin, pivot, Math.Min(end, next.EndSample), before, after);
    }

    private static short[] Render(short[] input, IReadOnlyList<UtauTimingAnchor> anchors,
        out Dictionary<(string Previous, string Next), Move> examples)
    {
        examples = new();
        var result = (short[])input.Clone();
        var envelope = new double[input.Length];
        for (int i = 1; i < anchors.Count; i++)
        {
            var previous = anchors[i - 1]; var next = anchors[i];
            if (Plan(previous, next) is not Move move) continue;
            examples[(previous.Alias!, next.Alias!)] = move;
            var from = Math.Max(0, move.Start);
            var to = Math.Min(input.Length, move.End);
            for (int k = from; k < to; k++)
            {
                double depth;
                if (k < move.Center)
                {
                    var t = (k - move.Start) / (double)(move.Center - move.Start);
                    // Non-nasal consonants release into the original articulation, without a discontinuity.
                    depth = move.Before * (move.After == 0 ? Math.Pow(Math.Sin(Math.PI * t), 2) : Ease(t));
                }
                else
                {
                    var t = (k - move.Center) / (double)(move.End - move.Center);
                    depth = (move.Before + (move.After - move.Before) * Ease(t * 4)) * (1 - Ease(t));
                }
                envelope[k] = Math.Max(envelope[k], depth);
            }
        }
        double lp1 = 0, lp2 = 0;
        for (int i = 0; i < result.Length; i++)
        {
            var d = envelope[i];
            var z = input[i];
            var a = 1 - Math.Exp(-2 * Math.PI * (2500 - 1970 * d) / Rate);
            lp1 += a * (z - lp1);
            lp2 += a * (lp1 - lp2);
            if (d == 0 || z == 0 && Math.Abs(lp2) < 2) continue;
            var wet = (1 - d) * z + d * lp2;
            result[i] = (short)Math.Clamp(Math.Round(wet * Math.Pow(10, -11 * d / 20)), short.MinValue, short.MaxValue);
        }
        return result;
    }


    private static short[] Equalize(short[] original, short[] shaped, IReadOnlyList<UtauTimingAnchor> anchors,
        out List<(string Pair, int At, double Before, double After)> joins, out bool[] editable)
    {
        joins = new();
        editable = new bool[original.Length];
        var output = (short[])shaped.Clone();
        var hop = Ms(4);
        var half = Ms(8) - 2;
        for (int i = 1; i < anchors.Count; i++)
        {
            var left = anchors[i - 1]; var right = anchors[i];
            if (left.VowelOnsetSample is not int prior || right.VowelOnsetSample is not int onset || right.Alias is null) continue;
            // Leave the pre-release voicing and bursts of stops and fricatives alone.
            // The previous vowel's decay is still present in A and B.
            if ("かきくけこさしすせそたちつてとはひふへほがぎぐげござじずぜぞだぢづでどばびぶべぼぱぴぷぺぽ".Contains(right.Alias[0])) continue;
            var at = Plan(left, right)?.Center ?? Math.Max(right.StartSample, onset - Ms(30));
            var min = Math.Max(prior + Ms(15), 0);
            var max = Math.Min(shaped.Length, right.EndSample);
            if (i + 1 < anchors.Count) max = Math.Min(max, anchors[i + 1].StartSample);
            const int cap = 15;
            var levels = new double[2 * cap + 1];
            bool Safe(int f)
            {
                var p = at + f * hop;
                if (p - half < min || p + half > max) return false;
                var raw = original.AsSpan(p - half, 2 * half);
                var wet = shaped.AsSpan(p - half, 2 * half);
                levels[f + cap] = Rms(wet);
                return levels[f + cap] >= 125 && Rms(raw) >= 200 && UtauLevelReview.IsPeriodic(raw) && UtauLevelReview.IsPeriodic(wet);
            }
            if (!Safe(0)) continue;
            int first = 0, last = 0;
            while (first > -cap && Safe(first - 1)) first--;
            while (last < cap && Safe(last + 1)) last++;
            if (first > -4 || last < 4) continue;
            var start = at + first * hop;
            var end = at + last * hop;
            if (editable.AsSpan(start, end - start).Contains(true)) continue;
            var n = Ms(12);
            var before = Gap(shaped, at, n);
            var target = Math.Sqrt(Rms(shaped.AsSpan(at - n, n)) * Rms(shaped.AsSpan(at, n)));
            var gain = new double[last - first + 1];
            for (int f = first + 1; f < last; f++)
            {
                var desire = Math.Clamp(20 * Math.Log10(target / levels[f + cap]), -13, 13);
                var feather = Ease(Math.Clamp(Math.Min(f - first, last - f) / 3.0, 0, 1));
                gain[f - first] = desire * feather;
            }
            // Global bound for positive gain in this one join, so no sample clips and the gain curve stays continuous.
            var scale = 1.0;
            for (int k = start; k < end; k++)
            {
                var j = (k - start) / hop;
                var upper = Math.Max(gain[j], gain[j + 1]);
                if (upper <= 0 || shaped[k] == 0) continue;
                var headroom = 20 * Math.Log10(32000.0 / Math.Abs((int)shaped[k]));
                scale = Math.Min(scale, Math.Max(0, headroom / upper));
            }
            for (int j = 0; j < gain.Length; j++) if (gain[j] > 0) gain[j] *= scale;
            for (int k = start; k < end; k++)
            {
                var j = (k - start) / hop;
                var mix = Ease(((k - start) % hop) / (double)hop);
                var db = gain[j] + mix * (gain[j + 1] - gain[j]);
                output[k] = (short)Math.Round(shaped[k] * Math.Pow(10, db / 20));
            }

            // Correct residual mismatch from the analysis window straddling the boundary.
            // The correction is zero at the center and tapers back to zero at both ends.
            for (int pass = 0; pass < 3; pass++)
            {
                var error = Gap(output, at, n);
                if (Math.Abs(error) <= .25) break;
                var radius = Math.Min(Ms(30), Math.Min(at - start, end - at));
                var extent = Math.Min(Ms(7), radius / 2);
                var amount = Math.Clamp(error * .76, -3, 3);
                var trial = output.AsSpan(at - radius, 2 * radius).ToArray();
                bool safe = true;
                for (int j = 0; j < trial.Length; j++)
                {
                    var d = j - radius;
                    var abs = Math.Abs(d);
                    var weight = Ease(Math.Clamp(abs / (double)extent, 0, 1)) *
                        Ease(Math.Clamp((radius - abs) / (double)extent, 0, 1));
                    var v = trial[j] * Math.Pow(10, -Math.Sign(d) * amount * weight / 20);
                    if (Math.Abs(v) >= 32760) { safe = false; break; }
                    trial[j] = (short)Math.Round(v);
                }
                if (!safe || Math.Abs(Gap(trial, radius, n)) >= Math.Abs(error)) break;
                trial.AsSpan().CopyTo(output.AsSpan(at - radius));
            }
            var after = Gap(output, at, n);
            if (Math.Abs(after) > Math.Abs(before) && Math.Abs(after) > .85)
            {
                shaped.AsSpan(start, end - start).CopyTo(output.AsSpan(start));
                continue;
            }
            editable.AsSpan(start, end - start).Fill(true);
            joins.Add(($"{left.Alias}/{right.Alias}", at, before, after));
        }
        return output;
    }

    private static double Rms(ReadOnlySpan<short> data)
    {
        double e = 0;
        foreach (int s in data) e += (double)s * s;
        return Math.Sqrt(e / data.Length);
    }

    private static double Gap(short[] pcm, int at, int count)
        => 20 * Math.Log10(Math.Max(1, Rms(pcm.AsSpan(at, count))) / Math.Max(1, Rms(pcm.AsSpan(at - count, count))));

    internal static async Task RunAsync(string output, Action<bool, string> check, bool equalize = false)
    {
        output = Path.GetFullPath(output);
        var root = Path.GetFullPath(Path.Combine(".local", "previews"));
        if (!string.Equals(Path.GetDirectoryName(output), root, StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(output).StartsWith(equalize ? "r50-" : "r49-", StringComparison.Ordinal) ||
            Directory.Exists(output) || File.Exists(output))
            throw new InvalidDataException("Use a fresh preview directory for this test.");
        WorldComparison.RejectLinks(root);
        WorldComparison.ConfigureWorker();
        using var temp = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        const string core = "あいうえおかきくけこさしすせそたちつてとなにぬねのはひふへほまみむめもやゆよらりるれろわをん";
        check(core.Distinct().Count() == 46 && core.All(Map.ContainsKey), "all 46 current basic kana have an onset entry");
        var compare = new List<(string Name, PcmAudio Audio, short[] B, short[]? C)>();
        foreach (var (name, phrase) in new[] {
            ("01-yomi", "よみ。よみ。"), ("02-yota", "よた。よた。"), ("03-ai", "あい。あい。"),
            ("04-full", "こんにちは。たなよみの読み上げテストです。"),
            ("05-control", "よ。み。あっか。よさ。") })
        {
            var (audio, anchors) = await UtauSpeech.SynthesizeLevelReviewAsync(phrase, bank, default);
            var changed = Render(audio.Samples, anchors, out var examples);
            check(changed.Length == audio.Samples.Length && anchors.Where(a => a.Alias is null).All(a =>
                changed.AsSpan(a.StartSample, a.EndSample - a.StartSample).SequenceEqual(
                    audio.Samples.AsSpan(a.StartSample, a.EndSample - a.StartSample))), "duration, pauses, and geminate closure stay exact");
            if (name is "02-yota" or "05-control")
                check(audio.Samples.SequenceEqual(changed), "tongue stop / frication, or a pause, must not induce lip closure");
            else
            {
                var (p, n) = name == "03-ai" ? ("あ", "い") : ("よ", "み");
                var m = examples[(p, n)];
                var tail = Drop(audio.Samples, changed, m.Center - Ms(23), m.Center - Ms(6));
                var head = Drop(audio.Samples, changed, m.Center, m.Center + Ms(17));
                check(tail >= 3 && head >= 4, "preceding tail and incoming head both change audibly sized amounts");
                var highs = HighDrop(audio.Samples, changed, m.Center - Ms(23), m.Center - Ms(6));
                check(highs > tail + 1, "tail upper frequencies attenuate beyond broadband gain");
                Console.WriteLine($"{name} {p}/{n}: tail -{tail:F1} dB, tail sample-difference energy -{highs:F1} dB, head -{head:F1} dB");
            }
            short[]? leveled = null;
            if (equalize)
            {
                leveled = Equalize(audio.Samples, changed, anchors, out var joins, out var mask);
                check(Enumerable.Range(0, changed.Length).All(k => mask[k] || leveled[k] == changed[k]),
                    "level matching never changes samples outside verified voiced runs");
                check(anchors.Where(a => a.Alias is null).All(a =>
                    leveled.AsSpan(a.StartSample, a.EndSample - a.StartSample).SequenceEqual(
                    audio.Samples.AsSpan(a.StartSample, a.EndSample - a.StartSample))), "pauses retain original samples");
                if (name is "02-yota" or "05-control")
                    check(joins.Count == 0 && leveled.SequenceEqual(changed), "unvoiced joins do not trigger level matching");
                foreach (var j in joins)
                    Console.WriteLine($"{name} {j.Pair} at {j.At * 1000.0 / Rate:F1} ms: signed gap {j.Before:F2} -> {j.After:F2} dB");
                if (name is "01-yomi" or "03-ai" or "04-full")
                {
                    var pair = joins.Where(j => j.Pair == (name == "03-ai" ? "あ/い" : "よ/み")).ToArray();
                    check(pair.Length > 0 && pair.All(j => Math.Abs(j.After) <= Math.Abs(j.Before) && Math.Abs(j.After) < .65),
                        "the target voiced boundaries reach a 12-ms RMS difference under 0.65 dB");
                }
                Console.WriteLine($"{name}: {joins.Count} matched joins");
            }
            compare.Add((name, audio, changed, leveled));
        }
        Directory.CreateDirectory(output);
        foreach (var (name, a, b, c) in compare)
        {
            await File.WriteAllBytesAsync(Path.Combine(output, name + "-A-original.wav"), a.ToWaveBytes());
            await File.WriteAllBytesAsync(Path.Combine(output, name + "-B-mouth.wav"), new PcmAudio(b, Rate).ToWaveBytes());
            if (c is not null) await File.WriteAllBytesAsync(Path.Combine(output, name + "-C-matched.wav"), new PcmAudio(c, Rate).ToWaveBytes());
        }
        // Write the whole table beside the previews so each approximation can be inspected.
        await File.WriteAllLinesAsync(Path.Combine(output, "mouth-opening.csv"),
            new[] {"kana,onset_opening,vowel_opening,gesture"}.Concat(Map.Select(pair =>
                FormattableString.Invariant($"{pair.Key},{pair.Value.Opens:F2},{pair.Value.Ends:F2},{pair.Value.Kind}"))),
            new System.Text.UTF8Encoding(true));
        Console.WriteLine($"OUTPUT {output}");
    }

    private static double Ease(double t) => t * t * (3 - 2 * t);
    private static double Drop(short[] a, short[] b, int l, int r)
        => 10 * Math.Log10(Energy(a, l, r, false) / Energy(b, l, r, false));
    private static double HighDrop(short[] a, short[] b, int l, int r)
        => 10 * Math.Log10(Energy(a, l, r, true) / Energy(b, l, r, true));
    private static double Energy(short[] x, int l, int r, bool diff)
    {
        double sum = 1;
        for (int i = l; i < r; i++) { double v = diff ? x[i] - x[i-1] : x[i]; sum += v*v; }
        return sum;
    }
}
