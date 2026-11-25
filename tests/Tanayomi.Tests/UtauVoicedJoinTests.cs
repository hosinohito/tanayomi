using Tanayomi;

internal static class UtauVoicedJoinTests
{
    internal static async Task RunAsync(Action<bool, string> check, string? waveDirectory = null)
    {
        const int length = 1323, period = 200;
        var outgoing = Enumerable.Range(0, length).Select(i => (float)Math.Sin(2 * Math.PI * i / period)).ToArray();
        var incoming = outgoing.Select(x => -x).ToArray();
        var blended = outgoing.ToArray();
        check(UtauVoicedJoin.TryBlend(blended, incoming, 7000, 1200, period, period, default),
            "periodic out-of-phase join is accepted");
        var center = length - 600;
        var energy = blended.Skip(center - 60).Take(120).Sum(x => x * x);
        check(energy > 40 && blended[0] == outgoing[0] && blended[^1] == incoming[^1],
            "phase correction prevents cancellation and keeps both edge samples");
        var closure = incoming.ToArray();
        Array.Clear(closure, 0, 550);
        foreach (var (sound, duration, otherPeriod) in new[] { (closure, 7000, 200d), (incoming, 250, 200d), (incoming, 7000, 320d) })
        {
            var dry = outgoing.ToArray();
            check(!UtauVoicedJoin.TryBlend(dry, sound, duration, 1200, period, otherPeriod, default) && dry.SequenceEqual(outgoing),
                "closure, short or incompatible joins leave the old path untouched");
        }

        using var fixture = new TestDirectory();
        var bank = (await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath))[0];
        await using var before = new UtauSpeech(new NullPcm()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = false, SimpleVoicedJoinsEnabled = false };
        await using var after = new UtauSpeech(new NullPcm()) { Voicebank = bank, Mode = UtauSynthesisMode.Simple, SimplePitchContourEnabled = false, SimpleVoicedJoinsEnabled = true };
        var changed = 0;
        var statements = new[] { "あいうえお。あおいあおい。", "こんにちは、ゆっくりしていってね。", "きっと、またあえるよ。", "なにぬねの、まみむめも、やゆよ。", "こんにちは。ゆっくりしていってね。こんばんは、今日は配信を見に来てくれてありがとう。" };
        for (var i = 0; i < statements.Length; i++)
        {
            var a = await before.SynthesizeAsync(statements[i]);
            var b = await after.SynthesizeAsync(statements[i]);
            var delta = a.Samples.Zip(b.Samples, (x, y) => Math.Abs(x - y)).Count(x => x > 4);
            check(a.Samples.Length == b.Samples.Length, "real CV bank keeps statement duration");
            if (delta > 0) changed++;
            if (waveDirectory is not null)
            {
                Console.WriteLine($"Real CV {i + 1}: {delta} changed samples / {a.Samples.Length}");
                await File.WriteAllBytesAsync(Path.Combine(waveDirectory, $"{i + 1}-before.wav"), a.ToWaveBytes());
                await File.WriteAllBytesAsync(Path.Combine(waveDirectory, $"{i + 1}-after.wav"), b.ToWaveBytes());
            }
        }
        check(changed > 0, "new joins are exercised on the bundled CV bank");
        var sokuon = await after.SynthesizeAsync("あっか");
        check(HasZeroRun(sokuon.Samples, 44100 * 85 / 1000), "85 ms sokuon closure survives");
    }

    private static bool HasZeroRun(short[] samples, int minimum)
    {
        var run = 0;
        foreach (var sample in samples)
        {
            run = sample == 0 ? run + 1 : 0;
            if (run >= minimum) return true;
        }
        return false;
    }

    private sealed class NullPcm : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
