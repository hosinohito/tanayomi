using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tanayomi;
using Tanayomi.Desktop;

// Explicit listening comparison only; ordinary tests and product defaults do not call this.
internal static class OpenVoiceReferenceComparison
{
    private static readonly string[] Texts = ["こんにちは。", "すきです。てすとですね。",
        "今日は配信を見に来てくれてありがとう。ゆっくり楽しんでいってください。"];

    internal static async Task RunAsync(string executable)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows product comparison only.");
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var expected = Path.GetFullPath(Path.Combine(workspace, ".local", "build", "app", "Tanayomi.Desktop.exe"));
        if (!Path.IsPathFullyQualified(executable) || !Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected))
            throw new ArgumentException("Specify the development .local/build/app/Tanayomi.Desktop.exe only.");
        WorldComparison.RejectLinks(expected);
        var fixture = new TestDirectory();
        var retainFixture = false;
        try
        {
        var banks = await Voicebanks.LoadInstalledAsync(fixture.DirectoryPath);
        if (banks.Count != 1 || banks[0].Name != "hosinohito") throw new InvalidDataException("Comparison reference bank mismatch.");
        var snapshot = await NeuralVoiceReferences.ReadAsync(banks[0], fixture.DirectoryPath, default);
        var clips = snapshot.Clips;
        if (clips.Count != 149 || clips.Any(x => x.SampleRate != 44100)) throw new InvalidDataException("Comparison requires the 149 bundled clips.");
        var groups = Group(clips);
        var referenceHash = Hash(clips);
        if (Hash(groups) != referenceHash || groups.Sum(x => (long)x.Samples.Length) != clips.Sum(x => (long)x.Samples.Length))
            throw new InvalidDataException("Grouping altered reference PCM.");
        var previewParent = Path.Combine(workspace, ".local", "previews");
        WorldComparison.RejectLinks(previewParent); Directory.CreateDirectory(previewParent);
        var preview = Path.Combine(previewParent, "openvoice-reference-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
        if (Directory.Exists(preview) || File.Exists(preview)) throw new IOException("Comparison output exists.");
        Directory.CreateDirectory(preview);
        var complete = false;
        var ownedOutputs = new List<string>();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(4));
            var ct = deadline.Token;
            var donors = new List<PcmAudio>();
            await using (var donor = Worker.Start(expected, "--open-jtalk-pcm-worker", fixture.DirectoryPath, () => retainFixture = true))
            {
                foreach (var text in Texts)
                {
                    await donor.Process.StandardInput.BaseStream.WriteAsync(OpenJtalkPcmProtocol.EncodeRequest(text), ct);
                    await donor.Process.StandardInput.BaseStream.FlushAsync(ct);
                    donors.Add(await OpenJtalkPcmProtocol.ReadResponseAsync(donor.Process.StandardOutput.BaseStream, ct));
                }
                await donor.FinishAsync(ct);
            }
            var outputs = new List<object>();
            var nativeParent = Path.Combine(Path.GetDirectoryName(expected)!, "data", "tmp");
            WorldComparison.RejectLinks(nativeParent); Directory.CreateDirectory(nativeParent);
            var nativeRoot = Path.Combine(nativeParent, "nv-" + Guid.NewGuid().ToString("N")[..12]);
            if (Directory.Exists(nativeRoot) || File.Exists(nativeRoot)) throw new IOException("Comparison worker root exists.");
            Directory.CreateDirectory(nativeRoot);
            Worker? conversionWorker = null;
            try
            {
                await using (var converter = conversionWorker = Worker.Start(expected, NeuralPcmWorker.Argument, nativeRoot))
                {
                    foreach (var condition in new[] { (Name: "A", References: clips), (Name: "B", References: (IReadOnlyList<PcmAudio>)groups) })
                        for (var i = 0; i < donors.Count; i++)
                        {
                            await NeuralPcmProtocol.WriteRequestAsync(converter.Process.StandardInput.BaseStream, donors[i], condition.References, ct);
                            var audio = await NeuralPcmProtocol.ReadResponseAsync(converter.Process.StandardOutput.BaseStream, ct);
                            var peak = audio.Samples.Max(x => Math.Abs((int)x));
                            if (audio.SampleRate != 44100 || peak == 0 || audio.Samples.Length == 0) throw new InvalidDataException("Invalid comparison PCM.");
                            var name = $"{condition.Name}-{i + 1}.wav";
                            var path = Path.Combine(preview, name);
                            ownedOutputs.Add(path); WriteWave(path, audio);
                            outputs.Add(new { condition = condition.Name, sentence = i + 1, file = name, audio.SampleRate,
                                samples = audio.Samples.Length, peak, saturatedSamples = audio.Samples.Count(x => x is short.MinValue or short.MaxValue),
                                donorSha256 = Hash([donors[i]]), pcmSha256 = Hash([audio]) });
                        }
                    await converter.FinishAsync(ct);
                }
            }
            finally
            {
                if (conversionWorker is null || conversionWorker.Reaped) { WorldComparison.RejectLinks(nativeRoot); Directory.Delete(nativeRoot, true); }
                else Console.Error.WriteLine("Comparison worker root retained because successful exit/reap was not confirmed: " + nativeRoot);
            }
            var manifest = Path.Combine(preview, "conditions.json");
            ownedOutputs.Add(manifest);
            using (var file = new FileStream(manifest, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, new { schema = 1, executable = expected, texts = Texts,
                    referencePcmSha256 = referenceHash, referenceSamples = clips.Sum(x => (long)x.Samples.Length),
                    clipCount = clips.Count, clipSamples = clips.Select(x => x.Samples.Length).ToArray(), groupSamples = groups.Select(x => x.Samples.Length).ToArray(),
                    donorSamples = donors.Select(x => x.Samples.Length).ToArray(), donorSampleRate = 48000,
                    productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expected))),
                    grouping = "Ordinal reference order; whole clips; append while group <= 10 seconds; no additional gain, trimming or silence",
                    referenceModelSha256 = NeuralOpenVoiceSession.ReferenceSha256, converterModelSha256 = NeuralOpenVoiceSession.ConverterSha256,
                    listeningEvaluation = "Pending user evaluation; PCM checks do not assess pronunciation or naturalness", outputs }, new JsonSerializerOptions { WriteIndented = true });
            complete = true;
            Console.WriteLine("OPENVOICE_REFERENCE_COMPARISON " + preview);
        }
        finally
        {
            if (!complete)
            {
                foreach (var path in ownedOutputs) { WorldComparison.RejectLinks(path); File.Delete(path); }
                WorldComparison.RejectLinks(preview);
                if (!Directory.EnumerateFileSystemEntries(preview).Any()) Directory.Delete(preview);
            }
        }
        }
        finally
        {
            if (!retainFixture) fixture.Dispose();
            else Console.Error.WriteLine("Donor worker exit unconfirmed; fixture retained: " + fixture.DirectoryPath);
        }
    }

    private static List<PcmAudio> Group(IReadOnlyList<PcmAudio> clips)
    {
        var result = new List<PcmAudio>(); var pending = new List<short>();
        foreach (var clip in clips)
        {
            if (pending.Count > 0 && pending.Count + (long)clip.Samples.Length > 441000) Flush();
            pending.AddRange(clip.Samples);
            if (pending.Count >= 441000) Flush();
        }
        Flush(); NeuralOpenVoiceSession.ValidateReferences(result); return result;
        void Flush() { if (pending.Count > 0) { result.Add(new PcmAudio(pending.ToArray(), 44100)); pending.Clear(); } }
    }

    private static string Hash(IEnumerable<PcmAudio> clips)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[8192];
        foreach (var clip in clips)
            for (var offset = 0; offset < clip.Samples.Length; offset += buffer.Length / 2)
            {
                var count = Math.Min(buffer.Length / 2, clip.Samples.Length - offset);
                for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2, 2), clip.Samples[offset + i]);
                hash.AppendData(buffer.AsSpan(0, count * 2));
            }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void WriteWave(string path, PcmAudio audio)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8); writer.Write(36 + audio.Samples.Length * 2); writer.Write("WAVEfmt "u8);
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(audio.SampleRate);
        writer.Write(audio.SampleRate * 2); writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8);
        writer.Write(audio.Samples.Length * 2); foreach (var sample in audio.Samples) writer.Write(sample);
    }

    private sealed class Worker : IAsyncDisposable
    {
        internal Process Process { get; }
        internal bool Reaped { get; private set; }
        private readonly Task stderr;
        private readonly Action? unconfirmed;
        private Worker(Process process, Action? unconfirmed) { Process = process; this.unconfirmed = unconfirmed; stderr = DrainAsync(process.StandardError.BaseStream); }
        internal static Worker Start(string executable, string argument, string root, Action? unconfirmed = null)
        {
            WorldComparison.RejectLinks(root);
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
                WorkingDirectory = root, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(argument);
            foreach (var variable in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[variable] = root;
            return new Worker(NativeChildProcess.Start(start), unconfirmed);
        }
        internal async Task FinishAsync(CancellationToken ct)
        {
            Process.StandardInput.Close();
            var trailing = new byte[1];
            if (await Process.StandardOutput.BaseStream.ReadAsync(trailing, ct) != 0) throw new InvalidDataException("Unexpected worker trailing bytes.");
            await Task.WhenAll(Process.WaitForExitAsync(ct), stderr).WaitAsync(ct);
            if (Process.ExitCode != 0) throw new IOException("Comparison worker failed: " + Process.ExitCode);
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!Process.HasExited)
                {
                    try { Process.Kill(true); } catch (InvalidOperationException) { }
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await Process.WaitForExitAsync(timeout.Token); }
                    catch (OperationCanceledException) { throw new IOException("Comparison worker exit unconfirmed; retain owned root."); }
                }
                Reaped = Process.HasExited;
                await stderr.WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { if (!Reaped) unconfirmed?.Invoke(); Process.Dispose(); }
        }
        private static async Task DrainAsync(Stream stream)
        {
            var bytes = new byte[4096]; var count = 0;
            while (await stream.ReadAsync(bytes) is var read && read != 0)
            { count += read; if (count > 65536) throw new IOException("Comparison worker diagnostic limit."); }
        }
    }
}
