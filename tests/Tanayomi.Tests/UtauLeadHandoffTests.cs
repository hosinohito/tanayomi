using Tanayomi;

internal static class UtauLeadHandoffTests
{
    private static int Ms(int x) => x * UtauSpeech.OutputSampleRate / 1000;
    internal static async Task RunAsync(Action<bool,string> check, string? output)
    {
        var tail = Enumerable.Repeat(.2f, Ms(90)).ToArray();
        var quiet = new float[tail.Length];
        var audible = Enumerable.Repeat(.12f, tail.Length).ToArray();
        var fade = Ms(35);
        check(UtauLeadHandoff.Delay(tail, quiet, fade) == Ms(25), "bounded delay for a very quiet lead");
        check(UtauLeadHandoff.Delay(tail, audible, fade) == 0, "no delay for an audible lead");
        check(UtauLeadHandoff.Delay(tail.AsSpan(0, Ms(40)), quiet.AsSpan(0, Ms(40)), Ms(25)) == 0,
            "short lead keeps its articulation budget");
        check(UtauLeadHandoff.Delay(quiet, quiet, fade) == 0, "silent previous sound is not extended");
        using var temp = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(temp.DirectoryPath))[0];
        var texts = new (string Id, string Text)[]
        {
            ("ta","あたあ。"), ("no","あのあ。"), ("sa","あさあ。"), ("ga","あがあ。"), ("fu","あふあ。"),
            ("phrase","明日の朝は雨が降りますか？"), ("list","たのさがふ。"),
            ("natural-fu","昨日の服は、風が吹くとふわっと揺れた。"),
            ("controls","あいうえお、きっとまた会おう。")
        };
        var improved = 0;
        foreach (var (id,text) in texts)
        {
            var a = await Render(false,text); var b = await Render(true,text);
            check(a.Length == b.Length, id + " duration unchanged");
            if (id is "ta" or "no" or "sa" or "ga" or "fu")
            {
                const int onset = 8688; // bundled 'a', with 17 ms pre + 180 ms mora.
                var reference = Rms(a, onset-Ms(110), Ms(15));
                var threshold = reference * .316; // 10 dB below the preceding vowel.
                int Gap(short[] x) => Enumerable.Range(0,18).Count(i => Rms(x,onset-Ms(90)+i*Ms(5),Ms(5)) < threshold)*5;
                var oldGap=Gap(a); var newGap=Gap(b);
                Console.WriteLine($"{id}: low level {oldGap}->{newGap} ms, changed={a.Zip(b,(x,y)=>x!=y).Count(x=>x)}");
                check(newGap <= oldGap, id + " no extra low-level interval");
                check(a.AsSpan(onset-Ms(19),Ms(94)).SequenceEqual(b.AsSpan(onset-Ms(19),Ms(94))),
                    id + " keeps the protected lead and early vowel bit-identical");
                if (newGap < oldGap) improved++;
            }
            if (output is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(output,id+"-before.wav"),new PcmAudio(a,UtauSpeech.OutputSampleRate).ToWaveBytes());
                await File.WriteAllBytesAsync(Path.Combine(output,id+"-after.wav"),new PcmAudio(b,UtauSpeech.OutputSampleRate).ToWaveBytes());
            }
        }
        check(improved >= 2, "quieter span decreases on at least two different actual onsets");
        async Task<short[]> Render(bool lead,string text)
        {
            var sink=new Sink();
            await using var speaker=new UtauSpeech(sink)
            {
                Mode=UtauSynthesisMode.Simple, Voicebank=bank, SimplePitchContourEnabled=true,
                SimpleLexicalAccentEnabled=true, SimpleComplementaryEnvelopeEnabled=true, SimpleAttackEnvelopeEnabled=true,
                SimpleLeadHandoffEnabled=lead, SimplePrefetchEnabled=true
            };
            await speaker.SpeakAsync(text,default);
            return sink.Samples.ToArray();
        }
    }

    private static double Rms(short[] samples, int at, int n)
    {
        double s=0;
        foreach (var v in samples.AsSpan(at,n)) s+=(double)v*v;
        return Math.Sqrt(s/n);
    }

    private sealed class Sink : IPcmOutput
    {
        internal List<short> Samples { get; } = [];
        public Task PlayAsync(ReadOnlyMemory<short> samples,int rate,CancellationToken ct)
        {
            Samples.AddRange(samples.ToArray());
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
