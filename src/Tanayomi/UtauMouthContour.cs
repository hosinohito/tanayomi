namespace Tanayomi;

internal static class UtauMouthContour
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

    internal static int Apply(Span<float> samples, IReadOnlyList<UtauTimingAnchor> anchors, CancellationToken ct)
    {
        int applied = 0, last = 0;
        for (int i = 1; i < anchors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (Plan(anchors[i - 1], anchors[i]) is not Move m) continue;
            if (m.Start < last || m.Start < 0 || m.End > samples.Length || m.End <= m.Start) continue;
            if (i + 1 < anchors.Count && (anchors[i + 1].Alias is null && m.End > anchors[i + 1].StartSample)) continue;
            var dry = samples[m.Start..m.End];
            var wet = new float[dry.Length];
            double lp1 = dry[0], lp2 = dry[0];
            for (int k = 0; k < dry.Length; k++)
            {
                var t = m.Start + k;
                double depth;
                if (t < m.Center)
                {
                    var x = (t - m.Start) / (double)(m.Center - m.Start);
                    depth = m.Before * (m.After == 0 ? Math.Pow(Math.Sin(Math.PI * x), 2) : Ease(x));
                }
                else
                {
                    var x = (t - m.Center) / (double)(m.End - m.Center);
                    depth = (m.Before + (m.After - m.Before) * Ease(Math.Min(1, x * 4))) * (1 - Ease(x));
                }
                var a = 1 - Math.Exp(-2 * Math.PI * (2500 - 1970 * depth) / Rate);
                lp1 += a * (dry[k] - lp1); lp2 += a * (lp1 - lp2);
                // The demo used a separate -11 dB envelope here. Leave that out:
                // articulation changes the balance of frequencies, not the intended volume.
                var mix = depth * .82;
                wet[k] = dry[k] == 0 ? 0 : (float)(dry[k] * (1 - mix) + lp2 * mix);
            }
            var hop = Ms(5);
            var level = new double[(wet.Length + hop - 1) / hop + 1];
            level[0] = level[^1] = 1;
            for (int j = 1; j < level.Length - 1; j++)
            {
                int center = j * hop;
                int l = Math.Max(0, center - Ms(7)), r = Math.Min(wet.Length, center + Ms(7));
                double inputEnergy = 0, outputEnergy = 0;
                for (int k = l; k < r; k++) { inputEnergy += (double)dry[k] * dry[k]; outputEnergy += (double)wet[k] * wet[k]; }
                level[j] = inputEnergy <= 1e-7 ? 1 : Math.Clamp(Math.Sqrt(inputEnergy / Math.Max(1e-7, outputEnergy)), .8, 2.5);
            }
            for (int k = 0; k < dry.Length; k++)
            {
                int j = k / hop;
                var f = Ease((k % hop) / (double)hop);
                var taper = Ease(Math.Clamp((dry.Length - 1 - k) / (double)hop, 0, 1));
                var v = wet[k] * (level[j] + f * (level[j + 1] - level[j]));
                dry[k] = (float)(dry[k] + taper * (v - dry[k]));
            }
            last = m.End; applied++;
        }
        return applied;
    }
    private static double Ease(double x) => x * x * (3 - 2 * x);
}
