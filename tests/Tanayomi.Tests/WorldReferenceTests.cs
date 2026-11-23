using Tanayomi;
using Tanayomi.Desktop;

public static class WorldReferenceTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var path = Path.Combine(temporary.DirectoryPath, "tone.wav");
        var audio = new PcmAudio(Enumerable.Range(0, 22050)
            .Select(i => (short)(10000 * Math.Sin(i * 2 * Math.PI * 220 / 44100))).ToArray(), 44100);
        await File.WriteAllBytesAsync(path, audio.ToWaveBytes());
        var entry = new OtoEntry(path, "あ", 20, 100, -350, 25, 10);
        var bank = new Voicebank("any-bank-name", temporary.DirectoryPath, [entry, entry with { Alias = "い" }], 0);
        var selected = await WorldReference.CreateAsync(bank, CancellationToken.None);
        var expected = await UtauSpeech.SynthesizeReferenceAsync("あーー、いーー", bank);
        check(selected.Samples.SequenceEqual(expected.Samples) && selected.SampleRate == 44100,
            "incomplete bank calibration uses available long vowels in fixed a-i-u-e-o order");

        var aliases = Enumerable.Range('ぁ', 'ゖ' - 'ぁ' + 1).Select(c => ((char)c).ToString())
            .Concat("きぎしじちぢにひびぴみり".SelectMany(c => "ゃゅょ".Select(small => c.ToString() + small)));
        var full = bank with { Entries = aliases.Select(alias => entry with { Alias = alias }).ToArray() };
        check(UtauSpeech.HasRequiredAliases(WorldReference.ReviewText, full), "full-bank calibration fixture covers the fixed comparison phrase");
        selected = await WorldReference.CreateAsync(full, CancellationToken.None);
        expected = await UtauSpeech.SynthesizeReferenceAsync(WorldReference.ReviewText, full);
        check(selected.Samples.SequenceEqual(expected.Samples),
            "a covered bank uses exactly the original B comparison phrase instead of changing its calibration input");

        var corruptPath = Path.Combine(temporary.DirectoryPath, "corrupt.wav");
        await File.WriteAllBytesAsync(corruptPath, new byte[44]);
        var corrupt = full with { Entries = full.Entries.Select(e => e.Alias == "す" ? e with { WavePath = corruptPath } : e).ToArray() };
        await ThrowsAsync<InvalidDataException>(() => WorldReference.CreateAsync(corrupt, CancellationToken.None), check,
            "a corrupt fixed-phrase source propagates instead of silently switching to valid vowel sources");
        var noVowels = bank with { Entries = [entry with { Alias = "か" }] };
        await ThrowsAsync<InvalidDataException>(() => WorldReference.CreateAsync(noVowels, CancellationToken.None), check,
            "a bank with no reference vowels fails explicitly for OS fallback");
        using var stopped = new CancellationTokenSource(); stopped.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => WorldReference.CreateAsync(bank, stopped.Token), check,
            "reference selection honors cancellation before generating PCM");
    }

    private static async Task ThrowsAsync<T>(Func<Task> operation, Action<bool, string> check, string description) where T : Exception
    {
        var caught = false;
        try { await operation().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { caught = true; }
        check(caught, description);
    }
}
