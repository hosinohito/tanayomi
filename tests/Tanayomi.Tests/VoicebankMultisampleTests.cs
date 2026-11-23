using System.IO.Compression;
using System.Text;
using Tanayomi;

internal static class VoicebankMultisampleTests
{
    private static readonly byte[] Wave = new PcmAudio(Enumerable.Range(0, 4410).Select(i =>
        (short)(6000 * Math.Sin(2 * Math.PI * 210 * i / 44100))).ToArray(), 44100).ToWaveBytes();

    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var root = temporary.DirectoryPath;
        using (var package = Package([
            ("bank/oto.ini", Text("root.wav=あ,0,50,-100,20,10\n")), ("bank/root.wav", Wave),
            ("bank/C4/oto.ini", Text("a.wav=あ_C4,0,50,-100,20,10\na.wav=a あ_C4,0,50,-100,20,10\n")), ("bank/C4/a.wav", Wave),
            ("bank/C5/oto.ini", Text("a.wav=あ_C5,0,50,-100,20,10\na.wav=あ,0,50,-100,20,10\n")), ("bank/C5/a.wav", Wave),
            ("bank/prefix.map", Text("C4\t\t_C4\nC5\t\t_C5\n"))]))
        {
            var bank = await Voicebanks.ImportZipAsync(package, root);
            check(bank.Entries.Count == 5 && bank.RejectedEntries == 0 && bank.PitchMappings.Count == 2 && !bank.PitchMappingsRejected &&
                bank.Entries[0].WavePath == Path.Combine(bank.Directory, "root.wav") &&
                bank.Entries.Single(x => x.Alias == "あ_C4").WavePath == Path.Combine(bank.Directory, "C4", "a.wav"),
                "multisample import combines nested OTO files relative to each folder and retains explicit pitch mappings");
            check(bank.Entries.Count(x => x.Alias == "あ") == 2 &&
                new UtauSourceCandidates(bank).Select([new("あ", 0, '\0')], 0, 261.625565, useContext: false, usePitchMap: false)?.Oto == bank.Entries[0],
                "multisample import retains duplicate candidates while root OTO keeps ordinary CV selection priority");
            var original = await File.ReadAllBytesAsync(bank.Entries[0].WavePath);
            package.Position = 0;
            var second = await Voicebanks.ImportZipAsync(package, root);
            var preserved = await File.ReadAllBytesAsync(bank.Entries[0].WavePath);
            check(second.Directory != bank.Directory && File.Exists(bank.Entries[0].WavePath) &&
                original.SequenceEqual(preserved),
                "reimport creates a separate multisample bank and leaves the earlier source bytes unchanged");
        }
        using (var package = Package([
            ("mapped/character.txt", Text("name=owned fixture\n")), ("mapped/prefix.map", Text("C4\t\t_C4\n")),
            ("mapped/C4/oto.ini", Text("a.wav=あ_C4,0,50,-100,20,10\n")), ("mapped/C4/a.wav", Wave)]))
        {
            var bank = await Voicebanks.ImportZipAsync(package, root);
            check(bank.Name == "mapped" && bank.PitchMappings.Count == 1 && File.Exists(Path.Combine(bank.Directory, "character.txt")) &&
                bank.Entries[0].WavePath == Path.Combine(bank.Directory, "C4", "a.wav"),
                "a bank with one nested pitch OTO preserves its explicit ancestor metadata and directory structure");
        }
        using (var package = Package([
            ("pitches/character.txt", Text("name=owned fixture\n")),
            ("pitches/C3/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ("pitches/C3/a.wav", Wave),
            ("pitches/C4/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ("pitches/C4/a.wav", Wave)]))
        {
            var bank = await Voicebanks.ImportZipAsync(package, root);
            check(bank.Name == "pitches" && bank.Entries.Count == 2 && bank.PitchMappings.Count == 0,
                "explicit bank metadata allows pitch-only folders while absent pitch maps leave duplicate CV choice deterministic");
        }
        foreach (var badMap in new[] { "C4\t\t_C4\nC4\t\t_C5\n", "H4\t\t_C4\n", "C4\tinvalid\n", new string('x', 65537) })
        {
            using var package = Package([("badmap/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")),
                ("badmap/a.wav", Wave), ("badmap/prefix.map", Text(badMap))]);
            var bank = await Voicebanks.ImportZipAsync(package, root);
            check(bank.Entries.Count == 1 && bank.RejectedEntries == 0 && bank.PitchMappingsRejected && bank.PitchMappings.Count == 0 &&
                new UtauSourceCandidates(bank).Select([new("あ", 0, '\0')], 0, 261.625565)?.Oto == bank.Entries[0],
                "malformed or oversized optional prefix maps are reported separately while valid CV recordings remain available unchanged");
        }
        using (var separate = Package([
            ("one/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ("one/a.wav", Wave),
            ("two/oto.ini", Text("a.wav=い,0,50,-100,20,10\n")), ("two/a.wav", Wave)]))
            check(await RejectedAsync(separate, root), "unrelated banks sharing a ZIP are rejected rather than silently merged");
        using (var traversal = Package([("bad/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ("bad/a.wav", Wave),
            ("bad/C4/oto.ini", Text("../a.wav=い,0,50,-100,20,10\n"))]))
            check(await RejectedAsync(traversal, root), "nested OTO references cannot escape their recording folder even to another bank-local file");
        using (var excessive = Package(Enumerable.Range(0, 65).SelectMany(i => new[]
        {
            ($"excess/{i:D2}/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ($"excess/{i:D2}/a.wav", Wave)
        }).Append(("excess/character.txt", Text("name=fixture\n")))))
            check(await RejectedAsync(excessive, root), "multisample OTO discovery has a fixed count bound before parsing source candidates");
        using (var files = Package(Enumerable.Range(0, 4097).Select(i => ($"many/f{i}.txt", Array.Empty<byte>()))))
            check(await RejectedAsync(files, root), "the existing ZIP entry bound still rejects excessive packages before extraction");
        var padding = new string('\n', 1024 * 1024);
        using (var aggregate = Package([("large/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n" + padding)), ("large/a.wav", Wave),
            ("large/C4/oto.ini", Text("a.wav=い,0,50,-100,20,10\n" + padding)), ("large/C4/a.wav", Wave)]))
            check(await RejectedAsync(aggregate, root), "aggregate OTO metadata retains the two-MiB parsing budget across nested files");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        using (var package = Package([("stop/oto.ini", Text("a.wav=あ,0,50,-100,20,10\n")), ("stop/a.wav", Wave)]))
        {
            var stopped = false;
            try { await Voicebanks.ImportZipAsync(package, root, canceled.Token); } catch (OperationCanceledException) { stopped = true; }
            check(stopped && !Directory.EnumerateDirectories(root).Any(path => Path.GetFileName(path).StartsWith(".import-")),
                "canceled and rejected multisample imports clean their owned staging directories without changing installed banks");
        }
        // A small owned placeholder prevents this metadata-only test from
        // unpacking the real bundled bank; ordinary bundled import is tested elsewhere.
        var placeholder = Path.Combine(root, "hosinohito"); Directory.CreateDirectory(placeholder);
        await File.WriteAllBytesAsync(Path.Combine(placeholder, "a.wav"), Wave);
        await File.WriteAllTextAsync(Path.Combine(placeholder, "oto.ini"), "a.wav=あ,0,50,-100,20,10\n", new UTF8Encoding(false));
        var notices = new List<string>(); var diagnostics = new List<DiagnosticEvent>();
        var installed = await Voicebanks.LoadInstalledAsync(root, notices.Add, diagnostics.Add);
        check(installed.Count(bank => bank.PitchMappingsRejected) == 4 && notices.Count == 4 &&
            diagnostics.Count == 4 && diagnostics.All(value => value.Code == DiagnosticCode.VoicebankPitchMappingRejected &&
                value.Operation == DiagnosticOperation.VoicebankLoad && value.Recovery == DiagnosticRecovery.AvailableFeatures &&
                value.Exception == DiagnosticException.InvalidDataException),
            "reloading banks reports stable optional pitch-map diagnostics while retaining every valid CV bank");
    }

    private static byte[] Text(string value) => new UTF8Encoding(false).GetBytes(value);
    private static MemoryStream Package(IEnumerable<(string Path, byte[] Bytes)> files)
    {
        var result = new MemoryStream();
        using (var archive = new ZipArchive(result, ZipArchiveMode.Create, leaveOpen: true))
            foreach (var file in files) { using var output = archive.CreateEntry(file.Path).Open(); output.Write(file.Bytes); }
        result.Position = 0; return result;
    }
    private static async Task<bool> RejectedAsync(Stream package, string root)
    { try { await Voicebanks.ImportZipAsync(package, root); return false; } catch (InvalidDataException) { return true; } }
}
