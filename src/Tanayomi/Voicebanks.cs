using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace Tanayomi;

public sealed record OtoEntry(string WavePath, string Alias, double Offset, double Consonant,
    double Cutoff, double Preutterance, double Overlap);
public sealed record Voicebank(string Name, string Directory, IReadOnlyList<OtoEntry> Entries, int RejectedEntries)
{
    public IReadOnlyList<VoicebankPitchMapping> PitchMappings { get; init; } = Array.Empty<VoicebankPitchMapping>();
    internal IReadOnlyDictionary<string, float>? RegisteredPeakGains { get; init; }
    public bool PitchMappingsRejected { get; init; }
    public DiagnosticException PitchMappingException { get; init; }
    public override string ToString() => $"{Name} · {Entries.Count}音";
}

public sealed record VoicebankPitchMapping(int MidiNote, string Prefix, string Suffix);

public static class Voicebanks
{
    public static string DefaultDirectory => Path.Combine(AppPaths.DataDirectory, "voicebanks");
    private static readonly SemaphoreSlim MigrationGate = new(1, 1);

    public static Voicebank Read(string directory, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        directory = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);
        var iniFiles = FindOtoFiles(directory, ct);
        if (iniFiles.Count == 0) throw new InvalidDataException("oto.iniがありません。");
        if (!File.Exists(Path.Combine(directory, "oto.ini")) && !HasBankMetadata(directory))
            throw new InvalidDataException("複数のoto.iniをまとめる音源のルートを確認できません。");
        var entries = new List<OtoEntry>();
        var rejected = 0;
        long iniBytes = 0;
        foreach (var ini in iniFiles)
        {
            ct.ThrowIfCancellationRequested();
            var bytes = ReadMetadataBytes(ini, 2 * 1024 * 1024, ct);
            iniBytes += bytes.Length;
            if (iniBytes > 2 * 1024 * 1024) throw new InvalidDataException("oto.iniの合計サイズが上限を超えています。");
            var otoDirectory = Path.GetDirectoryName(ini)!;
            foreach (var line in DecodeMetadata(bytes).TrimStart('\uFEFF').Split('\n'))
            {
                ct.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var equals = line.IndexOf('=');
                    if (equals < 1) throw new FormatException();
                    // WAV paths belong to this oto.ini, then must also remain
                    // inside the complete bank. No sibling/parent references.
                    var path = ContainedPath(otoDirectory, line[..equals]);
                    path = ContainedPath(directory, Path.GetRelativePath(directory, path));
                    if (!path.EndsWith(".wav", StringComparison.OrdinalIgnoreCase) || !File.Exists(path) ||
                        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                    var parts = line[(equals + 1)..].TrimEnd('\r').Split(',');
                    if (parts.Length < 6) throw new FormatException();
                    // UTAU permits empty timing fields; these mean zero.
                    var numbers = parts.Skip(1).Take(5).Select(p => string.IsNullOrWhiteSpace(p) ? 0 : double.Parse(p, CultureInfo.InvariantCulture)).ToArray();
                    if (numbers.Any(n => !double.IsFinite(n) || Math.Abs(n) > 600_000) || numbers[0] < 0) throw new FormatException();
                    var alias = parts[0].Length > 0 ? parts[0] : Path.GetFileNameWithoutExtension(path);
                    if (alias.Length is < 1 or > 128 || alias.Any(char.IsControl)) throw new FormatException();
                    entries.Add(new(path, alias, numbers[0], numbers[1], numbers[2], numbers[3], numbers[4]));
                }
                catch (Exception ex) when (ex is FormatException or IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException) { rejected++; }
            }
        }
        if (entries.Count == 0) throw new InvalidDataException("利用できるoto.iniの項目がありません。");
        var pitchMap = Path.Combine(directory, "prefix.map");
        IReadOnlyList<VoicebankPitchMapping> mappings = Array.Empty<VoicebankPitchMapping>();
        var mapRejected = false;
        var mapException = DiagnosticException.None;
        if (File.Exists(pitchMap))
        {
            try { mappings = ReadPitchMap(pitchMap, ct); }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or ArgumentException)
            {
                ct.ThrowIfCancellationRequested(); mapRejected = true;
                mapException = DiagnosticEvent.Create(DiagnosticCode.VoicebankPitchMappingRejected,
                    DiagnosticOperation.VoicebankLoad, DiagnosticRecovery.AvailableFeatures, error).Exception;
            }
        }
        return new(Path.GetFileName(directory), directory, entries.AsReadOnly(), rejected)
        { PitchMappings = mappings, PitchMappingsRejected = mapRejected, PitchMappingException = mapException };
    }

    private static bool HasBankMetadata(string directory) =>
        File.Exists(Path.Combine(directory, "character.txt")) || File.Exists(Path.Combine(directory, "prefix.map"));

    private static IReadOnlyList<string> FindOtoFiles(string root, CancellationToken ct)
    {
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクされた音源フォルダーは使えません。");
        var pending = new Stack<string>(); pending.Push(root);
        var files = new List<string>(); var total = 0;
        while (pending.TryPop(out var directory))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(directory))
            {
                ct.ThrowIfCancellationRequested();
                if (++total > 4096) throw new InvalidDataException("音源のファイル数が上限を超えています。");
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクされた音源ファイルは使えません。");
                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                if (!Path.GetFileName(path).Equals("oto.ini", StringComparison.OrdinalIgnoreCase)) continue;
                if (files.Count >= 64) throw new InvalidDataException("oto.iniの数が上限を超えています。");
                files.Add(path);
            }
        }
        return files.OrderBy(path => Path.GetRelativePath(root, path).Count(c => c is '/' or '\\'))
            .ThenBy(path => path, StringComparer.Ordinal).ToArray();
    }

    private static byte[] ReadMetadataBytes(string path, int maximum, CancellationToken ct)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("リンクされた音源設定は使えません。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > maximum) throw new InvalidDataException("音源設定のサイズが上限を超えています。");
        var bytes = new byte[(int)stream.Length];
        for (var at = 0; at < bytes.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var count = stream.Read(bytes, at, Math.Min(bytes.Length - at, 65536));
            if (count == 0) throw new InvalidDataException("音源設定が途中で切れています。");
            at += count;
        }
        if (stream.ReadByte() != -1) throw new InvalidDataException("読み込み中に音源設定の長さが変わりました。");
        return bytes;
    }

    private static string DecodeMetadata(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return Encoding.GetEncoding(932, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetString(bytes); }
    }

    private static IReadOnlyList<VoicebankPitchMapping> ReadPitchMap(string path, CancellationToken ct)
    {
        var lines = DecodeMetadata(ReadMetadataBytes(path, 65536, ct)).TrimStart('\uFEFF').Split('\n');
        var result = new List<VoicebankPitchMapping>(); var notes = new HashSet<int>();
        foreach (var line in lines)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var fields = line.TrimEnd('\r').Split('\t');
            if (fields.Length != 3 || !UtauSourceCandidates.TryNote(fields[0], out var note) || !notes.Add(note) ||
                fields[1].Length > 64 || fields[2].Length > 64 || fields[1].Any(char.IsControl) || fields[2].Any(char.IsControl))
                throw new InvalidDataException("prefix.mapの音階または接頭・接尾辞が不正です。");
            result.Add(new(note, fields[1], fields[2]));
        }
        return result.AsReadOnly();
    }

    internal static string ContainedPath(string root, string relative)
    {
        relative = relative.Replace('\\', '/');
        if (relative.StartsWith('/') || relative.Contains(':') || relative.Split('/').Any(p => p is ".." or ".")) throw new InvalidDataException("音源外のパスは使用できません。");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(Path.GetFullPath(root) + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException();
        // Imported archives cannot create links; also reject existing links in directory imports.
        for (var parent = Path.GetDirectoryName(full); parent is not null && parent.Length > root.Length; parent = Path.GetDirectoryName(parent))
            if (System.IO.Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
        return full;
    }

    public static async Task<Voicebank> ImportZipAsync(Stream source, string root, CancellationToken ct = default)
    {
        System.IO.Directory.CreateDirectory(root);
        var staging = Path.Combine(root, ".import-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(staging);
        try
        {
            // Providers on phones may return a non-seekable stream. Bound it before ZipArchive buffers it.
            using var bounded = new MemoryStream();
            var readBuffer = new byte[81920];
            int read;
            while ((read = await source.ReadAsync(readBuffer, ct)) > 0)
            {
                if (bounded.Length + read > 64 * 1024 * 1024) throw new InvalidDataException("ZIPサイズが上限を超えています。");
                bounded.Write(readBuffer, 0, read);
            }
            bounded.Position = 0;
            using var archive = new ZipArchive(bounded, ZipArchiveMode.Read, leaveOpen: true);
            if (archive.Entries.Count > 4096) throw new InvalidDataException("音源のファイル数が上限を超えています。");
            long total = 0;
            foreach (var entry in archive.Entries)
            {
                ct.ThrowIfCancellationRequested();
                total = checked(total + entry.Length);
                if (total > 256 * 1024 * 1024 || entry.Length > 32 * 1024 * 1024) throw new InvalidDataException("音源サイズが上限を超えています。");
                if (entry.FullName.EndsWith('/')) continue;
                var destination = ContainedPath(staging, entry.FullName);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                await using var file = new FileStream(destination, FileMode.CreateNew);
                await using var input = entry.Open();
                var buffer = new byte[81920];
                long copied = 0;
                int count;
                while ((count = await input.ReadAsync(buffer, ct)) > 0)
                {
                    copied += count;
                    if (copied > entry.Length) throw new InvalidDataException();
                    await file.WriteAsync(buffer.AsMemory(0, count), ct);
                }
            }
            var iniFiles = FindOtoFiles(staging, ct);
            if (iniFiles.Count == 0) throw new InvalidDataException("oto.iniがありません。");
            var bankFolder = ImportedBankRoot(staging, iniFiles);
            var bank = Read(bankFolder, ct);
            if (bank.RejectedEntries > 0) throw new InvalidDataException($"音源に読み込めない項目が{bank.RejectedEntries}件あります。");
            UtauRegisteredPeaks.Attach(bank, ct, replace: true);
            var name = bankFolder == staging ? "voicebank" : bank.Name;
            if (name.StartsWith('.')) name = "voicebank";
            var target = Path.Combine(root, name);
            if (System.IO.Directory.Exists(target)) target += "-" + Guid.NewGuid().ToString("N")[..8];
            System.IO.Directory.Move(bankFolder, target);
            return UtauRegisteredPeaks.Attach(Read(target, ct), ct);
        }
        finally
        {
            if (System.IO.Directory.Exists(staging)) System.IO.Directory.Delete(staging, recursive: true);
        }
    }

    private static string ImportedBankRoot(string staging, IReadOnlyList<string> iniFiles)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var shallowest = Path.GetDirectoryName(iniFiles[0])!;
        if (iniFiles.All(path => Path.GetDirectoryName(path)!.Equals(shallowest, comparison) ||
            path.StartsWith(shallowest + Path.DirectorySeparatorChar, comparison)))
        {
            // A one-pitch package can already place its only oto.ini below the
            // explicit bank metadata. Preserve that metadata with the samples.
            for (var parent = Path.GetDirectoryName(shallowest); parent is not null && parent.Length >= staging.Length;
                parent = Path.GetDirectoryName(parent))
                if (HasBankMetadata(parent)) return parent;
            return shallowest;
        }
        var common = shallowest;
        while (!iniFiles.All(path => path.StartsWith(common + Path.DirectorySeparatorChar, comparison)))
        {
            common = Path.GetDirectoryName(common) ?? throw new InvalidDataException("音源のルートを確認できません。");
            if (common.Length < staging.Length) throw new InvalidDataException("音源のルートが取り込み範囲外です。");
        }
        // Merely sharing a ZIP is insufficient: metadata at the common root is
        // required for pitch-only subfolders that have no root oto.ini.
        if (!HasBankMetadata(common)) throw new InvalidDataException("別々の音源を含むZIPは一つずつ追加してください。");
        return common;
    }

    public static Task<IReadOnlyList<Voicebank>> LoadInstalledAsync(string root, Action<string>? warning = null) =>
        LoadInstalledAsync(root, warning, null);

    public static async Task<IReadOnlyList<Voicebank>> LoadInstalledAsync(string root, Action<string>? warning,
        Action<DiagnosticEvent>? diagnostic)
    {
        System.IO.Directory.CreateDirectory(root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Equals(Path.GetFullPath(DefaultDirectory).TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanayomi", "voicebanks");
            await MigrationGate.WaitAsync();
            try { await MigrateLegacyAsync(root, legacy, warning, diagnostic); }
            finally { MigrationGate.Release(); }
        }
        var bundled = Path.Combine(root, "hosinohito");
        if (!System.IO.Directory.Exists(bundled))
        {
            await using var stream = typeof(Voicebanks).Assembly.GetManifestResourceStream("Tanayomi.hosinohito.zip") ?? throw new FileNotFoundException("同梱音源が見つかりません。");
            await ImportZipAsync(stream, root);
        }
        var banks = new List<Voicebank>();
        foreach (var directory in System.IO.Directory.EnumerateDirectories(root).Where(p => !Path.GetFileName(p).StartsWith('.')))
        {
            try
            {
                var bank = UtauRegisteredPeaks.Attach(Read(directory), default); banks.Add(bank);
                if (bank.PitchMappingsRejected)
                {
                    PublishDiagnostic(diagnostic, new(DiagnosticCode.VoicebankPitchMappingRejected,
                        DiagnosticOperation.VoicebankLoad, DiagnosticRecovery.AvailableFeatures, bank.PitchMappingException));
                    warning?.Invoke("音源のprefix.mapを読み込めないため音階選択を省きます。利用できる単独音・連続音は継続できます。");
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException)
            {
                PublishDiagnostic(diagnostic, DiagnosticEvent.Create(DiagnosticCode.VoicebankLoadFailed,
                    DiagnosticOperation.VoicebankLoad, DiagnosticRecovery.OtherVoicebanks, ex));
                warning?.Invoke($"音源1件を読み込めませんでした（{ex.GetType().Name}）。他の音源は利用できます。");
            }
        }
        return banks;
    }

    private static void PublishDiagnostic(Action<DiagnosticEvent>? diagnostic, DiagnosticEvent value)
    {
        try { diagnostic?.Invoke(value); }
        catch (Exception) { /* Diagnostic observers must not interrupt independent voicebanks. */ }
    }

    private static async Task MigrateLegacyAsync(string root, string legacy, Action<string>? warning, Action<DiagnosticEvent>? diagnostic)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (Path.GetFullPath(root).Equals(Path.GetFullPath(legacy), comparison) || !System.IO.Directory.Exists(legacy)) return;
        try
        {
            // Do not follow links in the legacy tree, including its parent folder.
            if ((File.GetAttributes(legacy) & FileAttributes.ReparsePoint) != 0 ||
                (File.GetAttributes(Path.GetDirectoryName(legacy)!) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
            var bankCount = 0;
            foreach (var directory in System.IO.Directory.EnumerateDirectories(legacy).Where(p => !Path.GetFileName(p).StartsWith('.')).Take(4097))
            {
                if (++bankCount > 4096) throw new InvalidDataException();
                var name = Path.GetFileName(directory);
                if (System.IO.Directory.Exists(Path.Combine(root, name))) continue;
                try
                {
                    if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                    var bank = Read(directory);
                    if (bank.RejectedEntries > 0) throw new InvalidDataException();
                    using var package = new MemoryStream();
                    using (var archive = new ZipArchive(package, ZipArchiveMode.Create, leaveOpen: true))
                    {
                        var pending = new Stack<string>(); pending.Push(directory);
                        var entries = 0; long total = 0;
                        while (pending.TryPop(out var current))
                        {
                            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                            foreach (var path in System.IO.Directory.EnumerateFileSystemEntries(current))
                            {
                                if (++entries > 4096) throw new InvalidDataException();
                                var attributes = File.GetAttributes(path);
                                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException();
                                if ((attributes & FileAttributes.Directory) != 0) { pending.Push(path); continue; }
                                var relative = Path.GetRelativePath(directory, path);
                                var contained = ContainedPath(directory, relative);
                                await using var input = new FileStream(contained, FileMode.Open, FileAccess.Read, FileShare.Read);
                                var expectedLength = input.Length;
                                if (expectedLength > 32 * 1024 * 1024 || (total = checked(total + expectedLength)) > 256 * 1024 * 1024) throw new InvalidDataException();
                                await using var output = archive.CreateEntry(name + "/" + relative.Replace('\\', '/'), CompressionLevel.Fastest).Open();
                                var buffer = new byte[81920]; long copied = 0; int count;
                                while ((count = await input.ReadAsync(buffer)) > 0)
                                {
                                    copied += count;
                                    if (copied > expectedLength) throw new InvalidDataException();
                                    await output.WriteAsync(buffer.AsMemory(0, count));
                                    if (package.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                                }
                                if (copied != expectedLength) throw new InvalidDataException();
                            }
                        }
                    }
                    if (package.Length > 64 * 1024 * 1024) throw new InvalidDataException();
                    package.Position = 0;
                    await ImportZipAsync(package, root);
                    warning?.Invoke("以前の保存先の音源を実行ファイル横のdataへコピーしました。元の音源は保持しています。");
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or DecoderFallbackException or OverflowException)
                {
                    PublishDiagnostic(diagnostic, DiagnosticEvent.Create(DiagnosticCode.VoicebankMigrationFailed,
                        DiagnosticOperation.VoicebankMigration, DiagnosticRecovery.RetryOperation, ex));
                    warning?.Invoke("以前の保存先の音源1件をコピーできませんでした。元の音源は保持しています。ZIPから追加し直せます。");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            PublishDiagnostic(diagnostic, DiagnosticEvent.Create(DiagnosticCode.VoicebankMigrationFailed,
                DiagnosticOperation.VoicebankMigration, DiagnosticRecovery.RetryOperation, ex));
            warning?.Invoke("以前の音源保存先を確認できませんでした。元の音源は保持しています。");
        }
    }
}
