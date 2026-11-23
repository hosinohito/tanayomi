using System.Text;
using Tanayomi;

internal static class PlaybackVolumeStoreTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var fixture = new TestDirectory();
        var store = new PlaybackVolumeStore(fixture.DirectoryPath);
        var directory = Path.Combine(fixture.DirectoryPath, "settings");
        var path = Path.Combine(directory, "playback-volume.txt");
        check(await store.LoadAsync() == 100 && !Directory.Exists(directory),
            "missing playback volume defaults to 100 without creating settings files");
        foreach (var value in new[] { 0, 1, 50, 99, 100, 200, 400 })
        {
            await store.SaveAsync(value);
            check(await new PlaybackVolumeStore(fixture.DirectoryPath).LoadAsync() == value &&
                (await File.ReadAllBytesAsync(path)).SequenceEqual(Encoding.UTF8.GetBytes(value.ToString(System.Globalization.CultureInfo.InvariantCulture))),
                "playback volume persists and restores a bounded integer in UTF8 without BOM");
        }
        foreach (var malformed in new[] { Array.Empty<byte>(), Encoding.UTF8.GetBytes("401"), Encoding.UTF8.GetBytes("-1"),
            Encoding.UTF8.GetBytes("1.0"), Encoding.UTF8.GetBytes(" 1"), Encoding.UTF8.GetBytes("1\n"),
            Encoding.UTF8.GetBytes("+1"), Encoding.UTF8.GetBytes("NaN"), new byte[] { 0xff },
            new byte[] { 0xef, 0xbb, 0xbf, (byte)'1' }, new byte[1024] })
        {
            await File.WriteAllBytesAsync(path, malformed);
            Exception? error = null;
            try { await store.LoadAsync(); } catch (Exception caught) { error = caught; }
            check(error is InvalidDataException && (await File.ReadAllBytesAsync(path)).SequenceEqual(malformed),
                "corrupt oversized noninteger or out-of-range volume is rejected without rewriting evidence");
        }
        await store.SaveAsync(37);
        foreach (var invalid in new[] { -1, 401 })
        {
            Exception? error = null;
            try { await store.SaveAsync(invalid); } catch (Exception caught) { error = caught; }
            check(error is ArgumentOutOfRangeException && await store.LoadAsync() == 37,
                "invalid save preserves the previous playback setting");
        }
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? cancelled = null;
        try { await store.SaveAsync(0, cancellation.Token); } catch (Exception error) { cancelled = error; }
        check(cancelled is OperationCanceledException && await store.LoadAsync() == 37 &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "cancelled playback volume save preserves the previous value and leaves no temporary file");

        // Force commit failure after the temporary write, without ACL changes.
        File.Delete(path);
        Directory.CreateDirectory(path);
        Exception? failed = null;
        try { await store.SaveAsync(58); } catch (Exception error) { failed = error; }
        check(failed is IOException or UnauthorizedAccessException && Directory.Exists(path) &&
            !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "failed volume commit remains observable and cleans its own temporary file");
        Directory.Delete(path);
        await store.SaveAsync(58);
        check(await store.LoadAsync() == 58 && !Directory.EnumerateFiles(directory, "*.tmp").Any(),
            "playback volume storage remains usable after cancellation and a failed commit");
    }
}
