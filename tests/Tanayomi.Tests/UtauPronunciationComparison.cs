using System.Buffers.Binary;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Tanayomi;
using Tanayomi.Desktop;

// Fixed public text only. Explicit opt-in, no playback and no user-bank access.
internal static class UtauPronunciationComparison
{
    internal static async Task RunDevoicingAsync(string executable)
    {
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var expected = Path.Combine(workspace, ".local", "build", "app", "Tanayomi.Desktop.exe");
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected))
            throw new ArgumentException("Specify the development .local/build/app/Tanayomi.Desktop.exe.");
        WorldComparison.RejectLinks(expected);
        var fixture = new TestDirectory(); var retain = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            JsonElement conditions = default;
            PcmAudio? candidate = null;
            var baseline = await RunWorkerAsync(expected, ["--utau-context-devoicing-worker", fixture.DirectoryPath], fixture.DirectoryPath,
                async (process, token) =>
                {
                    process.StandardInput.Close();
                    var header = new byte[4]; await process.StandardOutput.BaseStream.ReadExactlyAsync(header, token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is < 2 or > 65536) throw new InvalidDataException("Context metadata limit.");
                    var bytes = new byte[length]; await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token);
                    using var metadata = JsonDocument.Parse(bytes); conditions = metadata.RootElement.Clone();
                    if (conditions.GetProperty("contextFrictionRevision").GetInt32() != 3 || conditions.GetProperty("pcmResponses").GetInt32() != 2)
                        throw new InvalidDataException("Republish the contextual diagnostic worker.");
                    var audio = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    candidate = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    return audio;
                }, () => retain = true, deadline.Token);
            if (candidate is null || baseline.SampleRate != candidate.SampleRate || baseline.Samples.Length != candidate.Samples.Length)
                throw new InvalidDataException("Context comparison length mismatch.");
            Validate(baseline); Validate(candidate);
            if (baseline.Samples.Any(x => x is short.MinValue or short.MaxValue) || candidate.Samples.Any(x => x is short.MinValue or short.MaxValue))
                throw new InvalidDataException("Context comparison clipped.");
            var cases = conditions.GetProperty("cases");
            if (cases.GetArrayLength() != 4 || conditions.GetProperty("text").GetString() != "てすとです。")
                throw new InvalidDataException("Unexpected comparison cases.");
            foreach (var item in cases.EnumerateArray())
            {
                if (item.GetProperty("FallbackCount").GetInt32() != 0 || item.GetProperty("TimingFallbackCount").GetInt32() != 0 ||
                    item.GetProperty("candidateFallbacks").GetInt32() != 0 || item.GetProperty("candidateTimingFallbacks").GetInt32() != 0 ||
                    !item.GetProperty("applied").GetBoolean() && (!item.GetProperty("sameBeforeWorld").GetBoolean() || !item.GetProperty("sameOutput").GetBoolean()))
                    throw new InvalidDataException("Context comparison guard or fallback mismatch.");
            }
            var applied = conditions.GetProperty("primaryApplied").GetBoolean();
            if (applied != cases[0].GetProperty("applied").GetBoolean() ||
                !applied && !baseline.Samples.AsSpan().SequenceEqual(candidate.Samples)) throw new InvalidDataException();
            var parent = Path.Combine(workspace, ".local", "previews");
            WorldComparison.RejectLinks(parent); Directory.CreateDirectory(parent);
            var preview = Path.Combine(parent, "utau-devoicing-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(preview) || File.Exists(preview)) throw new IOException("Preview exists.");
            Directory.CreateDirectory(preview);
            var complete = false;
            try
            {
                // A declined candidate is recorded as evidence, not presented as
                // a new sound. Nothing is played or copied into a user's data.
                if (applied)
                {
                    WriteWave(Path.Combine(preview, "A-accepted-context.wav"), baseline);
                    WriteWave(Path.Combine(preview, "B-context-friction.wav"), candidate);
                }
                using var file = new FileStream(Path.Combine(preview, "conditions.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new { schema = 1, context = conditions,
                    audio = new[] { Describe("A-accepted-context.wav", baseline), Describe("B-context-friction.wav", candidate) },
                    audioSaved = applied, productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expected))),
                    levelMatching = false, listening = "Same-length accepted per-unit baseline versus experimental contextual friction. A retained vowel articulation is not reconstructed; listening required." },
                    new JsonSerializerOptions { WriteIndented = true });
                complete = true;
                Console.WriteLine("UTAU_DEVOICING_COMPARISON " + preview + " applied=" + applied);
            }
            finally
            {
                if (!complete) { WorldComparison.RejectLinks(preview); Directory.Delete(preview, recursive: true); }
            }
        }
        finally
        {
            if (!retain) fixture.Dispose();
            else Console.Error.WriteLine("Owned diagnostic worker exit unconfirmed; fixture retained: " + fixture.DirectoryPath);
        }
    }

    internal static async Task RunAsync(string executable)
    {
        const string text = "てすとです。";
        var workspace = Path.GetFullPath(Environment.CurrentDirectory);
        var expected = Path.Combine(workspace, ".local", "build", "app", "Tanayomi.Desktop.exe");
        if (!OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(executable) ||
            !Path.GetFullPath(executable).Equals(expected, StringComparison.OrdinalIgnoreCase) || !File.Exists(expected))
            throw new ArgumentException("Specify the development .local/build/app/Tanayomi.Desktop.exe.");
        WorldComparison.RejectLinks(expected);
        var fixture = new TestDirectory(); var retain = false;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(3));
            var ct = deadline.Token;
            JsonElement contextConditions = default;
            PcmAudio? source = null, shaped = null, candidate = null, production = null, ruleUnit = null, ruleContext = null;
            var context = await RunWorkerAsync(expected, ["--utau-context-pronunciation-worker", fixture.DirectoryPath], fixture.DirectoryPath,
                async (process, token) =>
                {
                    process.StandardInput.Close();
                    var header = new byte[4]; await process.StandardOutput.BaseStream.ReadExactlyAsync(header, token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is < 2 or > 65536) throw new InvalidDataException("Context metadata limit.");
                    var bytes = new byte[length]; await process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, token);
                    using var metadata = JsonDocument.Parse(bytes); contextConditions = metadata.RootElement.Clone();
                    var responseCount = contextConditions.GetProperty("pcmResponses").GetInt32();
                    if (responseCount is not (6 or 7) || contextConditions.GetProperty("unitAcousticRevision").GetInt32() != 1)
                        throw new InvalidDataException("Republish the diagnostic worker.");
                    var after = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    source = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    shaped = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    if (responseCount == 7) candidate = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    production = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    ruleUnit = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    ruleContext = await NeuralPcmProtocol.ReadResponseAsync(process.StandardOutput.BaseStream, token);
                    return after;
                }, () => retain = true, ct);
            if (contextConditions.GetProperty("text").GetString() != text || contextConditions.GetProperty("FallbackCount").GetInt32() != 0 ||
                contextConditions.GetProperty("TimingFallbackCount").GetInt32() != 0) throw new InvalidDataException("Unexpected diagnostic fallback.");
            Validate(context);
            if (production is null || production.Samples.Length != context.Samples.Length || production.SampleRate != context.SampleRate ||
                contextConditions.GetProperty("productionFallbacks").GetInt32() != 0 || contextConditions.GetProperty("productionTimingFallbacks").GetInt32() != 0) throw new InvalidDataException("Production comparison mismatch.");
            Validate(production);
            if (ruleUnit is null || ruleContext is null || ruleContext.Samples.Length < production.Samples.Length ||
                ruleContext.SampleRate != production.SampleRate || contextConditions.GetProperty("ruleFallbacks").GetInt32() != 0 ||
                contextConditions.GetProperty("ruleTimingFallbacks").GetInt32() != 0)
                throw new InvalidDataException("Unit-rule context comparison mismatch.");
            Validate(ruleUnit); Validate(ruleContext);
            if (!contextConditions.GetProperty("productionUnit").GetProperty("AcousticTransitionApplied").GetBoolean() ||
                contextConditions.GetProperty("unitDiagnostic").GetProperty("AcousticTransitionApplied").GetBoolean())
                throw new InvalidDataException("Expected product adoption and explicit baseline opt-out.");
            if (source is null || shaped is null) throw new InvalidDataException();
            Validate(source); Validate(shaped);
            if (ruleUnit.SampleRate != shaped.SampleRate || ruleUnit.Samples.Length < shaped.Samples.Length ||
                ruleUnit.Samples.Length != contextConditions.GetProperty("effectiveUnitSamples").GetInt32() ||
                ruleContext.Samples.Length != contextConditions.GetProperty("effectivePhraseSamples").GetInt32())
                throw new InvalidDataException("Unit-rule output does not match the effective timing.");
            var unit = contextConditions.GetProperty("unitDiagnostic");
            if (source.SampleRate != 44100 || shaped.SampleRate != 44100 ||
                source.Samples.Length != unit.GetProperty("EndSample").GetInt32() - unit.GetProperty("TrimmedStartSample").GetInt32() ||
                shaped.Samples.Length != unit.GetProperty("LeadSamples").GetInt32() + unit.GetProperty("DurationSamples").GetInt32())
                throw new InvalidDataException("Unit audio does not match observed boundaries.");
            var protection = contextConditions.GetProperty("acousticTransitionProtection");
            if (protection.ValueKind != JsonValueKind.Object || protection.GetProperty("Available").GetBoolean() != (candidate is not null))
                throw new InvalidDataException("Candidate availability does not match PCM.");
            if (candidate is not null)
            {
                Validate(candidate);
                if (candidate.SampleRate != shaped.SampleRate || candidate.Samples.Length != shaped.Samples.Length)
                    throw new InvalidDataException("Candidate changed the output duration.");
            }
            var descriptions = new List<object> { Describe("A-context.wav", context), Describe("B-source-su.wav", source), Describe("C-shaped-su.wav", shaped) };
            if (candidate is not null) descriptions.Add(Describe("D-acoustic-transition-su.wav", candidate));
            descriptions.Add(Describe("E-adopted-context.wav", production));
            descriptions.Add(Describe("F-unit-rules-su.wav", ruleUnit));
            descriptions.Add(Describe("G-unit-rules-context.wav", ruleContext));
            var parent = Path.Combine(workspace, ".local", "previews");
            WorldComparison.RejectLinks(parent); Directory.CreateDirectory(parent);
            var preview = Path.Combine(parent, "utau-pronunciation-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + Guid.NewGuid().ToString("N"));
            if (Directory.Exists(preview) || File.Exists(preview)) throw new IOException("Preview exists.");
            Directory.CreateDirectory(preview);
            var complete = false;
            try
            {
                WriteWave(Path.Combine(preview, "A-context.wav"), context);
                WriteWave(Path.Combine(preview, "B-source-su.wav"), source);
                WriteWave(Path.Combine(preview, "C-shaped-su.wav"), shaped);
                if (candidate is not null) WriteWave(Path.Combine(preview, "D-acoustic-transition-su.wav"), candidate);
                WriteWave(Path.Combine(preview, "E-adopted-context.wav"), production);
                WriteWave(Path.Combine(preview, "F-unit-rules-su.wav"), ruleUnit);
                WriteWave(Path.Combine(preview, "G-unit-rules-context.wav"), ruleContext);
                using var file = new FileStream(Path.Combine(preview, "conditions.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new { schema = 2, text, context = contextConditions,
                    audio = descriptions,
                    productSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(expected))),
                    levelMatching = false, listening = "D versus F: same original recording; F may restore a bounded adjacent prefix and extend the unit to preserve acoustic regions. E versus G may differ in duration. Timing and recovery are reported separately; no intelligibility conclusion from PCM checks." },
                    new JsonSerializerOptions { WriteIndented = true });
                complete = true;
                Console.WriteLine("UTAU_PRONUNCIATION_COMPARISON " + preview);
            }
            finally
            {
                if (!complete) { WorldComparison.RejectLinks(preview); Directory.Delete(preview, recursive: true); }
            }
        }
        finally
        {
            if (!retain) fixture.Dispose();
            else Console.Error.WriteLine("Owned diagnostic worker exit unconfirmed; fixture retained: " + fixture.DirectoryPath);
        }
    }

    private static void Validate(PcmAudio audio)
    {
        if (audio.SampleRate is not (44100 or 48000) || audio.Samples.Length is < 1 ||
            audio.Samples.Length > audio.SampleRate * 45 || !audio.Samples.Any(x => x != 0)) throw new InvalidDataException("Diagnostic PCM invalid.");
    }

    private static object Describe(string file, PcmAudio audio)
    {
        var bytes = new byte[audio.Samples.Length * 2];
        for (var i = 0; i < audio.Samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), audio.Samples[i]);
        return new { file, audio.SampleRate, samples = audio.Samples.Length, seconds = audio.Samples.Length / (double)audio.SampleRate,
            peak = audio.Samples.Max(x => Math.Abs((int)x)), saturatedSamples = audio.Samples.Count(x => x is short.MinValue or short.MaxValue),
            pcmSha256 = Convert.ToHexString(SHA256.HashData(bytes)) };
    }

    private static void WriteWave(string path, PcmAudio audio)
    {
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var writer = new BinaryWriter(file);
        writer.Write("RIFF"u8); writer.Write(36 + audio.Samples.Length * 2); writer.Write("WAVEfmt "u8); writer.Write(16);
        writer.Write((short)1); writer.Write((short)1); writer.Write(audio.SampleRate); writer.Write(audio.SampleRate * 2);
        writer.Write((short)2); writer.Write((short)16); writer.Write("data"u8); writer.Write(audio.Samples.Length * 2);
        foreach (var sample in audio.Samples) writer.Write(sample);
    }

    private static async Task<T> RunWorkerAsync<T>(string executable, string[] arguments, string root,
        Func<Process, CancellationToken, Task<T>> exchange, Action unconfirmed, CancellationToken ct)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        foreach (var variable in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[variable] = root;
        using var process = NativeChildProcess.Start(start);
        var reaped = false;
        var stderr = DrainAsync(process.StandardError.BaseStream);
        using var cancellation = ct.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { } });
        try
        {
            var result = await exchange(process, ct).WaitAsync(ct);
            var trailing = new byte[1];
            if (await process.StandardOutput.BaseStream.ReadAsync(trailing, ct) != 0) throw new InvalidDataException("Unexpected trailing worker data.");
            await Task.WhenAll(process.WaitForExitAsync(ct), stderr).WaitAsync(ct);
            reaped = process.HasExited;
            if (process.ExitCode != 0) throw new IOException("Diagnostic worker failed: " + process.ExitCode);
            return result;
        }
        finally
        {
            if (!reaped)
            {
                try
                {
                    if (!process.HasExited) process.Kill(true);
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await process.WaitForExitAsync(stop.Token); reaped = process.HasExited;
                }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            if (!reaped) { unconfirmed(); throw new IOException("Diagnostic worker exit unconfirmed."); }
            await stderr.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[4096]; var total = 0;
        while (true)
        {
            var count = await stream.ReadAsync(buffer); if (count == 0) return;
            total += count; if (total > 65536) throw new IOException("Diagnostic stderr limit.");
        }
    }
}






