using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tanayomi;
using Tanayomi.Desktop;

// Explicit opt-in only. The existing console runner is its own hidden native
// child. The parent never loads Open JTalk, plays or persists generated speech.
public static class OpenJtalkIntegrationTests
{
    private const int Rate = 48000;
    private const int MaximumSamples = Rate * 45;
    private const int PipeLimit = 16 * 1024;
    private const string LibraryName = "tanayomi-open-jtalk.dll";
    private const string ModelName = "nitech_jp_atr503_m001.htsvoice";
    private static readonly string[] DictionaryFiles = ["char.bin", "matrix.bin", "sys.dic", "unk.dic"];
    private sealed record NativeCheck(string Name, bool Passed);
    private sealed record NativeReport(NativeCheck[] Checks, int SampleCount, int SampleRate, int Peak, int CorruptStatus,
        string[] Phones, int PlanFrames);

    // Explicit packaging/production-dispatch check. This does not use the
    // injected adapter or load a DLL in the tests process, and creates no files.
    public static Task RunProductCheckAsync(string desktopExecutable, Action<bool, string> check, CancellationToken ct = default)
        => RunProductWorkerCheckAsync(desktopExecutable, check, planning: false, ct);

    public static Task RunProductPlanCheckAsync(string desktopExecutable, Action<bool, string> check, CancellationToken ct = default)
        => RunProductWorkerCheckAsync(desktopExecutable, check, planning: true, ct);

    private static async Task RunProductWorkerCheckAsync(string desktopExecutable, Action<bool, string> check, bool planning, CancellationToken ct)
    {
        RequirePlatform();
        if (!Path.IsPathFullyQualified(desktopExecutable)) throw new ArgumentException("Specify an absolute product executable path.");
        var executable = Contained(desktopExecutable, Path.Combine(Environment.CurrentDirectory, ".local"));
        if (!Path.GetFileName(executable).Equals("Tanayomi.Desktop.exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(executable))
            throw new ArgumentException("Specify the built Tanayomi product executable.");
        var coldPcm = new List<short[]>();
        var voicePcm = new List<short[]>();
        string[] scenarios = planning ? ["cold", "malformed"] : ["cold", "prepared", "malformed", "unknown-voice"];
        if (!planning)
        {
            var resourceRoot = Path.Combine(Path.GetDirectoryName(executable)!, "resources", "open-jtalk");
            scenarios = [.. scenarios, .. OpenJtalkPcmWorker.AdditionalModels
                .Where(model => File.Exists(Path.Combine(resourceRoot, model.FileName))).Select(model => model.Id)];
        }
        foreach (var scenario in scenarios)
        {
            var tohoku = OpenJtalkPcmWorker.FindModel(scenario) is not null;
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.ArgumentList.Add(planning ? "--open-jtalk-plan-worker" : "--open-jtalk-pcm-worker");
            if (tohoku) start.ArgumentList.Add(scenario);
            if (scenario == "unknown-voice") start.ArgumentList.Add("unknown-voice");
            using var child = new Process { StartInfo = start };
            var started = false;
            try
            {
                ct.ThrowIfCancellationRequested();
                var launched = Stopwatch.GetTimestamp();
                if (!child.Start()) throw new IOException("Product native worker did not start.");
                started = true;
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(120));
                var token = deadline.Token;
                var stderr = ReadBoundedAsync(child.StandardError.BaseStream, token);
                try
                {
                    if (scenario == "unknown-voice")
                    {
                        child.StandardInput.Close();
                        var stdout = ReadBoundedAsync(child.StandardOutput.BaseStream, token);
                        await Task.WhenAll(child.WaitForExitAsync(token), stdout, stderr).WaitAsync(token).ConfigureAwait(false);
                        check(child.ExitCode == 2 && (await stdout.ConfigureAwait(false)).Length == 0 &&
                            (await stderr.ConfigureAwait(false)).Length == 0,
                            "the packaged PCM worker rejects an unknown voice ID before native initialization");
                    }
                    else if (scenario == "malformed")
                    {
                        var stdout = ReadBoundedAsync(child.StandardOutput.BaseStream, token);
                        var header = new byte[12]; "BAD!"u8.CopyTo(header);
                        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
                        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 1);
                        await child.StandardInput.BaseStream.WriteAsync(header, token).ConfigureAwait(false);
                        await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                        child.StandardInput.Close();
                        await Task.WhenAll(child.WaitForExitAsync(token), stdout, stderr).WaitAsync(token).ConfigureAwait(false);
                        check(child.ExitCode == 2 && (await stdout.ConfigureAwait(false)).Length == 0,
                            "a separate product worker rejects a malformed header with fixed exit two and no response payload");
                    }
                    else
                    {
                        var lengths = new List<int>();
                        short[]? previousTohoku = null;
                        if (scenario == "prepared" || tohoku)
                        {
                            await child.StandardInput.BaseStream.WriteAsync(OpenJtalkPcmProtocol.EncodePreparation(), token);
                            await child.StandardInput.BaseStream.FlushAsync(token);
                            await OpenJtalkPcmProtocol.ReadPreparationResponseAsync(child.StandardOutput.BaseStream, token);
                            Console.WriteLine($"{(tohoku ? "TOHOKU" : "NIT")}_PREPARE scenario={scenario} readyMs={Stopwatch.GetElapsedTime(launched).TotalMilliseconds:F1}");
                        }
                        foreach (var text in new[] { "こんにちは。", tohoku ? "こんにちは。" : planning ? WorldReference.ReviewText : "今日は配信を見に来てくれてありがとう。" })
                        {
                            var requested = Stopwatch.GetTimestamp();
                            var request = planning ? OpenJtalkPlanProtocol.EncodeRequest(text) : OpenJtalkPcmProtocol.EncodeRequest(text);
                            await child.StandardInput.BaseStream.WriteAsync(request, token).ConfigureAwait(false);
                            await child.StandardInput.BaseStream.FlushAsync(token).ConfigureAwait(false);
                            if (planning)
                            {
                                var reply = await OpenJtalkPlanProtocol.ReadResponseAsync(child.StandardOutput.BaseStream, token).ConfigureAwait(false);
                                check(reply.SampleRate == Rate && reply.FramePeriod == 240 && reply.Phones.Count is >= 3 and <= 8192 &&
                                    reply.FrameCount is > 0 and <= 9000 && reply.Phones[0].Symbol == "sil" && reply.Phones[^1].Symbol == "sil" &&
                                    reply.Phones[^1].EndFrame == reply.FrameCount && reply.Voiced.Any(value => value == 1),
                                    "the packaged plan worker returns bounded continuous phones finite LF0 and VUV on its 48kHz 240-sample grid");
                                if (lengths.Count == 1)
                                    check(new[] { "N", "cl", "pau" }.All(symbol => reply.Phones.Any(phone => phone.Symbol == symbol)),
                                        "the product fixed-review plan retains nasal closure and internal pause phones");
                                lengths.Add(reply.FrameCount);
                            }
                            else
                            {
                                var reply = await OpenJtalkPcmProtocol.ReadResponseAsync(child.StandardOutput.BaseStream, token).ConfigureAwait(false);
                                Console.WriteLine($"{(tohoku ? "TOHOKU" : "NIT")}_PCM scenario={scenario} index={lengths.Count} requestMs={Stopwatch.GetElapsedTime(requested).TotalMilliseconds:F1}");
                                if (scenario == "cold") coldPcm.Add(reply.Samples);
                                else if (tohoku)
                                {
                                    if (previousTohoku is null)
                                    {
                                        check(!coldPcm[0].AsSpan().SequenceEqual(reply.Samples) &&
                                            voicePcm.All(previous => !previous.AsSpan().SequenceEqual(reply.Samples)),
                                            $"the packaged {scenario} worker produces distinct PCM from NIT and other packaged voices for the same text");
                                        voicePcm.Add(reply.Samples);
                                    }
                                    else
                                        check(previousTohoku.AsSpan().SequenceEqual(reply.Samples),
                                            "the prepared Tohoku worker gives identical PCM on consecutive identical requests");
                                    previousTohoku = reply.Samples;
                                }
                                else check(coldPcm[lengths.Count].AsSpan().SequenceEqual(reply.Samples),
                                    "preparing a separate packaged NIT worker before the request preserves the entire cold-worker PCM");
                                var peak = reply.Samples.Max(value => Math.Abs((int)value));
                                check(reply.SampleRate == Rate && reply.Samples.Length is >= 240 and <= MaximumSamples &&
                                    reply.Samples.Length % 240 == 0 && peak is > 0 and <= 30147,
                                    "the packaged product worker returns bounded non-silent 48kHz PCM for a fixed public request");
                                lengths.Add(reply.Samples.Length);
                            }
                        }
                        if (!tohoku)
                            check(lengths[1] > lengths[0],
                                "one persistent product worker handles distinct requests without replaying stale output");
                        if (!planning && scenario == "cold")
                        {
                            async Task<PcmAudio> DrawnOutRequest(string text, bool enabled)
                            {
                                await child.StandardInput.BaseStream.WriteAsync(OpenJtalkPcmProtocol.EncodeRequest(text, enabled), token);
                                await child.StandardInput.BaseStream.FlushAsync(token);
                                return await OpenJtalkPcmProtocol.ReadResponseAsync(child.StandardOutput.BaseStream, token);
                            }
                            var shortOld = await DrawnOutRequest("こんにちは。", true);
                            check(shortOld.Samples.AsSpan().SequenceEqual(coldPcm[0]),
                                "drawn-out mode preserves short NIT PCM exactly");
                            const string longText = "最近のついったーあるごりずむだとセルフリツイートだとまだ表示されてない人のところにしか表示されないしなんぼしてもいいかも";
                            var oldFirst = await DrawnOutRequest(longText, true);
                            var fixedAudio = await DrawnOutRequest(longText, false);
                            var oldAgain = await DrawnOutRequest(longText, true);
                            check(oldFirst.Samples.Length == 1910160 && fixedAudio.Samples.Length == 468960 &&
                                oldFirst.Samples.AsSpan().SequenceEqual(oldAgain.Samples),
                                "ON OFF ON reproduces original 39.795-second and fixed 9.770-second NIT without stale mode state");
                        }
                        child.StandardInput.Close();
                        var trailing = new byte[1];
                        if (await child.StandardOutput.BaseStream.ReadAsync(trailing, token).ConfigureAwait(false) != 0)
                            throw new InvalidDataException("Product worker wrote unexpected trailing protocol bytes.");
                        await Task.WhenAll(child.WaitForExitAsync(token), stderr).WaitAsync(token).ConfigureAwait(false);
                        check(child.ExitCode == 0, "clean request EOF makes the packaged native worker release resources and exit normally");
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                { throw new TimeoutException("Product native worker check exceeded 120 seconds."); }
            }
            finally
            {
                if (started && !child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await child.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    { throw new IOException("The owned product worker exit is unconfirmed; no further worker is started."); }
                }
            }
        }
    }

    public static async Task RunAsync(string auditDirectory, Action<bool, string> check, CancellationToken ct = default)
    {
        RequirePlatform();
        var auditRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local", "build", "open-jtalk"));
        var audit = Contained(auditDirectory, auditRoot);
        if (!Directory.Exists(audit)) throw new DirectoryNotFoundException("Open JTalk audit directory is missing.");
        var temporary = new TestDirectory();
        Process? child = null;
        var started = false;
        var canClean = true;
        try
        {
            var fixture = Path.Combine(temporary.DirectoryPath, "日本語の検証");
            var dictionary = Path.Combine(fixture, "辞書");
            Directory.CreateDirectory(dictionary);
            foreach (var name in DictionaryFiles)
                await CopyBoundedAsync(Path.Combine(audit, "dictionary", name), Path.Combine(dictionary, name), 128L * 1024 * 1024, ct).ConfigureAwait(false);
            await CopyBoundedAsync(Path.Combine(audit, ModelName), Path.Combine(fixture, ModelName), 2 * 1024 * 1024, ct).ConfigureAwait(false);
            var library = Path.Combine(fixture, LibraryName);
            await CopyBoundedAsync(Path.Combine(audit, LibraryName), library, 16 * 1024 * 1024, ct).ConfigureAwait(false);
            // Verify the copied DLL against this audit's bounded manifest, not
            // a build output that another invocation may replace afterwards.
            var manifest = Path.Combine(audit, "open-jtalk-build.json");
            var voicedComparison = false;
            WorldComparison.RejectLinks(manifest);
            using (var input = new FileStream(manifest, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length is < 1 or > 256 * 1024) throw new InvalidDataException("Invalid audit manifest size.");
                using var document = JsonDocument.Parse(input);
                var expected = document.RootElement.GetProperty("dllSha256").GetString();
                voicedComparison = document.RootElement.TryGetProperty("flags", out var flags) &&
                    flags.ValueKind == JsonValueKind.Array && flags.EnumerateArray().Any(flag =>
                        flag.ValueKind == JsonValueKind.String && flag.GetString() == "-DTANAYOMI_AUDIT_VOICED_PLAN");
                using var copied = File.OpenRead(library);
                if (expected is null || expected.Length != 64 ||
                    !Convert.ToHexString(SHA256.HashData(copied)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Copied DLL differs from the requested audit.");
            }
            MakeCorruptModel(Path.Combine(fixture, ModelName), Path.Combine(fixture, "破損モデル.htsvoice"));
            var executable = Environment.ProcessPath ?? throw new IOException("The current test runner has no executable path.");
            WorldComparison.RejectLinks(executable);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Environment.CurrentDirectory
            };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(OpenJtalkIntegrationTests).Assembly.Location);
            else if (!Path.GetFileName(executable).Equals("Tanayomi.Tests.exe", StringComparison.OrdinalIgnoreCase))
                throw new IOException("Open JTalk integration must use the existing console test runner.");
            start.ArgumentList.Add("--open-jtalk-native-check");
            start.ArgumentList.Add(fixture);
            if (voicedComparison) start.ArgumentList.Add("--voiced-plan");
            child = new Process { StartInfo = start };
            if (!child.Start()) throw new IOException("Open JTalk native test child did not start.");
            started = true;
            child.StandardInput.Close();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(120));
            var stdout = ReadBoundedAsync(child.StandardOutput.BaseStream, timeout.Token);
            var stderr = ReadBoundedAsync(child.StandardError.BaseStream, timeout.Token);
            try
            {
                await Task.WhenAll(child.WaitForExitAsync(timeout.Token), stdout, stderr).WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                var stagePath = Path.Combine(fixture, "stage.txt");
                var lastStage = File.Exists(stagePath) && new FileInfo(stagePath).Length <= 16 &&
                    int.TryParse(File.ReadAllText(stagePath), out var value) && value is >= 0 and <= 64 ? value : -1;
                throw new TimeoutException($"Open JTalk native integration exceeded 120 seconds after check {lastStage}.");
            }
            if (child.ExitCode != 0)
            {
                var fields = (await stderr.ConfigureAwait(false)).Trim().Split(':');
                var numbers = new int[5];
                var valid = fields.Length == 6 && fields[0] == "open-jtalk-native-error";
                if (valid)
                    for (var i = 0; i < numbers.Length; i++)
                        valid &= int.TryParse(fields[i + 1], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out numbers[i]);
                valid &= numbers[0] is >= 0 and <= 64 && numbers[1] is >= -1 and <= 6 && numbers[4] is >= 0 and <= 64768;
                var safe = valid ? $"stage={numbers[0]}, status={numbers[1]}, count={numbers[2]}, rate={numbers[3]}, peak={numbers[4]}"
                    : "invalid numeric diagnostic";
                throw new IOException("Open JTalk native test child failed: " + safe + "; native diagnostic text is not forwarded.");
            }
            check(true, "Open JTalk hidden native test child exits normally with native dialog suppression configured");
            var report = JsonSerializer.Deserialize<NativeReport>(await stdout.ConfigureAwait(false))
                ?? throw new InvalidDataException("Missing native test report.");
            if (report.Checks is null || report.Checks.Length is < 12 or > 64 ||
                report.Checks.Any(item => item is null || !item.Passed || item.Name is null || item.Name.Length is < 1 or > 120))
                throw new InvalidDataException("Invalid native test report.");
            foreach (var item in report.Checks) check(item.Passed, "explicit Open JTalk: " + item.Name);
            if (report.Phones is null || report.Phones.Length is < 1 or > 8192 || report.PlanFrames is < 1 or > 9000 ||
                report.Phones.Any(phone => phone is null || phone.Length is < 1 or > 31 || phone.Any(c => c is < '!' or > '~')))
                throw new InvalidDataException("Invalid fixed-text phone report.");
            check(report.SampleRate == Rate && report.SampleCount is > Rate / 10 and <= MaximumSamples && report.Peak is > 0 and <= 30147,
                "explicit Open JTalk report contains bounded non-silent 48kHz PCM with headroom");
            Console.WriteLine($"Open JTalk explicit integration: {report.SampleCount / (double)Rate:F3}s, {report.SampleRate}Hz, peak {report.Peak}; corrupt model status {report.CorruptStatus}. No playback or audio file.");
            Console.WriteLine("Open JTalk fixed public review phones: " + string.Join(" ", report.Phones) + $"; frames {report.PlanFrames}.");
        }
        finally
        {
            try
            {
                if (started && child is not null && !child.HasExited)
                {
                    try { child.Kill(entireProcessTree: true); }
                    catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
                    using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await child.WaitForExitAsync(stop.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException)
                    {
                        canClean = false;
                        throw new IOException("Owned native child exit is unconfirmed; its unique test directory is retained until it exits.");
                    }
                }
            }
            finally
            {
                child?.Dispose();
                if (canClean) temporary.Dispose();
            }
        }
    }

    // Dispatch this before the ordinary suite, under Program's top-level catch.
    // All native faults/asserts stay in this bounded disposable child process.
    public static int RunNativeCheck(string[] args)
    {
        var stage = 0;
        var errorStatus = -1; var errorCount = 0; var errorRate = 0; var errorPeak = 0;
        try
        {
            RequirePlatform();
            if (args.Length is not (2 or 3) || args[0] != "--open-jtalk-native-check" ||
                args.Length == 3 && args[2] != "--voiced-plan") return 2;
            var voicedComparison = args.Length == 3;
            var temporaryRoot = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local", "tmp"));
            var fixture = Contained(args[1], temporaryRoot);
            var owned = Path.GetDirectoryName(fixture)!;
            if (Path.GetDirectoryName(owned) != temporaryRoot ||
                !Path.GetFileName(owned).StartsWith("tests-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(Path.GetFileName(owned)[6..], "N", out _) ||
                Path.GetFileName(fixture) != "日本語の検証") return 2;
            // Both are required BEFORE NativeLibrary.Load, including DLL init.
            SetErrorMode(0x0001 | 0x0002);
            if (SetCrtErrorMode(1) == -1) throw new IOException("Cannot suppress native CRT dialogs.");
            var checks = new List<NativeCheck>();
            void Verify(bool condition, string name, int nativeStatus = -1, int nativeCount = 0, int nativeRate = 0, int nativePeak = 0)
            {
                stage++;
                errorStatus = nativeStatus; errorCount = nativeCount; errorRate = nativeRate; errorPeak = nativePeak;
                if (!condition) throw new InvalidDataException("Native integration check failed.");
                checks.Add(new(name, true));
                // Bounded scalar progress in the owned fixture permits a hung
                // native call to be located; finally removes it with the fixture.
                File.WriteAllText(Path.Combine(fixture, "stage.txt"), stage.ToString(CultureInfo.InvariantCulture));
            }
            using var api = new NativeApi(Contained(Path.Combine(fixture, LibraryName), fixture));
            Verify(api.Version() == 1, "bounded C ABI version is one");
            var dictionary = Encoding.UTF8.GetBytes(Contained(Path.Combine(fixture, "辞書"), fixture));
            var model = Encoding.UTF8.GetBytes(Contained(Path.Combine(fixture, ModelName), fixture));
            var text = Encoding.UTF8.GetBytes("今日は配信を見に来てくれてありがとう。");
            var samples = new short[MaximumSamples];
            var status = api.Synthesize(text, text.Length, samples, samples.Length, out var count, out var rate);
            Verify(status == 2 && count == 0 && rate == 0, "unloaded synthesis returns status two and zero output metadata", status, count, rate);
            Verify((status = api.Load([0xc0, 0xaf], 2, model, model.Length)) == 1, "invalid UTF8 dictionary path is rejected without poisoning the engine", status);
            Verify((status = api.Load(dictionary, dictionary.Length, model, model.Length)) == 0, "dictionary and model load from Japanese UTF8 file paths", status);
            status = api.Synthesize(text, text.Length, samples, samples.Length, out count, out rate);
            var firstCount = count; var firstRate = rate;
            var peak = count is > 0 and <= MaximumSamples ? samples.Take(count).Max(value => Math.Abs((int)value)) : 0;
            Verify(status == 0 && rate == Rate && count is > Rate / 10 and <= MaximumSamples && count % 240 == 0 && peak is > 0 and <= 30147,
                "Japanese text generates bounded non-silent 48kHz PCM without clipping", status, count, rate, peak);
            byte[][] invalidTexts = [[0xc0, 0xaf], [0xe3, 0x81], [0xed, 0xa0, 0x80], [0xf4, 0x90, 0x80, 0x80], [65, 0, 66], new byte[4097]];
            var invalidRejected = true;
            foreach (var invalid in invalidTexts)
            {
                short[] sentinel = [1234];
                status = api.Synthesize(invalid, invalid.Length, sentinel, sentinel.Length, out count, out rate);
                invalidRejected &= status == 1 && count == 0 && rate == 0 && sentinel[0] == 1234;
            }
            var tooManyCharacters = Encoding.UTF8.GetBytes(new string('あ', 1025));
            short[] guard = [1234];
            status = api.Synthesize(tooManyCharacters, tooManyCharacters.Length, guard, guard.Length, out count, out rate);
            Verify(invalidRejected && status == 1 && count == 0 && rate == 0 && guard[0] == 1234,
                "malformed UTF8, NUL and byte/scalar overlimits leave output and state untouched", status, count, rate);
            status = api.Synthesize(text, text.Length, samples, samples.Length, out count, out rate);
            Verify(status == 0 && count > 0 && rate == Rate, "valid synthesis recovers after rejected text input", status, count, rate);
            status = api.Synthesize(text, text.Length, guard, guard.Length, out count, out rate);
            Verify(status == 4 && count == 0 && rate == 0 && guard[0] == 1234, "insufficient capacity is bounded before waveform allocation or output writes", status, count, rate);
            status = api.Synthesize(text, text.Length, samples, samples.Length, out count, out rate);
            Verify(status == 0 && count > 0 && rate == Rate, "valid synthesis recovers after a capacity failure", status, count, rate);
            Verify((status = api.Clear()) == 0 && (status = api.Clear()) == 0, "clear releases normal state and is repeatable", status);
            status = api.Synthesize(text, text.Length, guard, guard.Length, out count, out rate);
            Verify(status == 2 && count == 0 && rate == 0, "clear returns synthesis to the unloaded state", status, count, rate);
            Verify((status = api.Load(dictionary, dictionary.Length, model, model.Length)) == 0, "normal clear permits the same model to load again", status);
            var planText = Encoding.UTF8.GetBytes(WorldReference.ReviewText);
            var plan = new PlanBuffers();
            status = plan.Call(api, Encoding.UTF8.GetBytes("てすとです"));
            var expectedVowel = voicedComparison ? "u" : "U";
            Verify(status == 0 && plan.ValidMetadata() && plan.TryGetPhones(out var testPhones) &&
                testPhones.SequenceEqual(new[] { "sil", "t", "e", "s", expectedVowel, "t", "o", "d", "e", "s", expectedVowel, "sil" }),
                "fixed phrase vowel voicing matches the explicitly selected audit build while retaining all other phones", status, plan.FrameCount, plan.SampleRate);
            status = plan.Call(api, planText);
            Verify(status == 0 && plan.ValidMetadata(), "plan returns bounded 48kHz and 240-sample frame metadata", status, plan.FrameCount, plan.SampleRate);
            var decoded = plan.TryGetPhones(out var phones);
            Verify(decoded && phones[0] == "sil" && phones[^1] == "sil", "plan phone slots are terminated ASCII and time ranges consume every frame", status, plan.FrameCount, plan.SampleRate);
            Verify(phones.Contains("N") && phones.Contains("cl") && phones.Contains("pau"),
                "fixed public review plan includes nasal closure and pause phones without discarding them", status, plan.FrameCount, plan.SampleRate);
            var planFrames = plan.FrameCount;
            var validLf0 = true; var voicedFrames = 0;
            for (var i = 0; i < plan.FrameCount; i++)
            {
                var voiced = plan.Voiced[i]; var lf0 = plan.Lf0[i];
                validLf0 &= double.IsFinite(lf0) && (voiced == 0 && lf0 == 0 || voiced == 1 && lf0 >= Math.Log(40) && lf0 <= Math.Log(1200));
                if (voiced == 1) voicedFrames++;
            }
            Verify(validLf0 && voicedFrames > 0 && voicedFrames < plan.FrameCount,
                "plan preserves explicit voiced flags and finite natural-log F0 with zero unvoiced frames", status, plan.FrameCount, plan.SampleRate);
            status = api.Synthesize(planText, planText.Length, samples, samples.Length, out count, out rate);
            Verify(status == 0 && rate == Rate && count == planFrames * 240,
                "plan state durations equal the complete generated waveform duration for the same fixed text", status, count, rate);
            plan.FillSentinels();
            status = plan.Call(api, [0xed, 0xa0, 0x80]);
            Verify(status == 1 && plan.ZeroMetadata() && plan.SentinelsUnchanged(),
                "plan rejects invalid UTF8 without partial metadata or writes to any output array", status, plan.FrameCount, plan.SampleRate);
            status = plan.Call(api, planText);
            Verify(status == 0 && plan.ValidMetadata() && plan.TryGetPhones(out var recoveredPhones) && phones.SequenceEqual(recoveredPhones),
                "plan generation recovers after invalid text and preserves the complete fixed phone sequence", status, plan.FrameCount, plan.SampleRate);
            plan.FillSentinels();
            status = plan.Call(api, planText, phoneCapacity: 1);
            var phoneCapacityRejected = status == 4 && plan.ZeroMetadata() && plan.SentinelsUnchanged();
            plan.FillSentinels();
            status = plan.Call(api, planText, frameCapacity: 1);
            Verify(phoneCapacityRejected && status == 4 && plan.ZeroMetadata() && plan.SentinelsUnchanged(),
                "plan capacity failures leave all phone time LF0 and voiced arrays untouched", status, plan.FrameCount, plan.SampleRate);
            status = plan.Call(api, planText);
            Verify(status == 0 && plan.ValidMetadata() && plan.FrameCount == planFrames,
                "plan refresh permits recovery after either capacity failure", status, plan.FrameCount, plan.SampleRate);
            Verify((status = api.Clear()) == 0, "clear releases the model after planning without waveform synthesis", status);
            plan.FillSentinels();
            status = plan.Call(api, planText);
            Verify(status == 2 && plan.ZeroMetadata() && plan.SentinelsUnchanged(),
                "plan after clear reports unloaded state and leaves output arrays intact", status, plan.FrameCount, plan.SampleRate);
            Verify((status = api.Load(dictionary, dictionary.Length, model, model.Length)) == 0, "model reload remains possible after plan clear", status);
            // The same pinned model under an audit-only name bypasses the
            // NIT-specific breath limit, providing an otherwise identical control.
            var controlPath = Contained(Path.Combine(fixture, "control.htsvoice"), fixture);
            File.Copy(Encoding.UTF8.GetString(model), controlPath, overwrite: false);
            var controlModel = Encoding.UTF8.GetBytes(controlPath);
            var longText = Encoding.UTF8.GetBytes("最近のついったーあるごりずむだとセルフリツイートだとまだ表示されてない人のところにしか表示されないしなんぼしてもいいかも");
            Verify(api.Load(dictionary, dictionary.Length, controlModel, controlModel.Length) == 0,
                "audit control loads the identical model with NIT-specific handling disabled");
            var control = new PlanBuffers();
            Verify(control.Call(api, longText) == 0 && control.TryGetPhones(out _), "long text control plan is valid");
            control.TryGetPhones(out var controlPhones);
            var controlFrames = control.FrameCount;
            Verify(api.Load(dictionary, dictionary.Length, model, model.Length) == 0 && plan.Call(api, longText) == 0 && plan.TryGetPhones(out _),
                "long NIT text generates a bounded plan with automatic breath boundaries");
            plan.TryGetPhones(out var boundedPhones);
            Verify(boundedPhones.Where(p => p is not "sil" and not "pau").SequenceEqual(controlPhones.Where(p => p is not "sil" and not "pau")) &&
                boundedPhones.Count(p => p == "pau") > controlPhones.Count(p => p == "pau") && plan.FrameCount < controlFrames / 2,
                "long NIT speech retains all non-pause phones and avoids the pathological duration branch");

            foreach (var unchanged in new[] { "てすとです。", "今日は配信を見に来てくれてありがとう。", "こんにちは、、今日は良い天気ですね。", "こんにちは？元気ですか？" })
            {
                var input = Encoding.UTF8.GetBytes(unchanged);
                Verify(api.Load(dictionary, dictionary.Length, controlModel, controlModel.Length) == 0 && control.Call(api, input) == 0 &&
                    api.Load(dictionary, dictionary.Length, model, model.Length) == 0 && plan.Call(api, input) == 0 &&
                    control.FrameCount == plan.FrameCount && control.PhoneCount == plan.PhoneCount &&
                    control.Slots.AsSpan(0, control.PhoneCount * 32).SequenceEqual(plan.Slots.AsSpan(0, plan.PhoneCount * 32)) &&
                    control.Starts.AsSpan(0, control.PhoneCount).SequenceEqual(plan.Starts.AsSpan(0, plan.PhoneCount)) &&
                    control.Ends.AsSpan(0, control.PhoneCount).SequenceEqual(plan.Ends.AsSpan(0, plan.PhoneCount)) &&
                    control.Lf0.AsSpan(0, control.FrameCount).SequenceEqual(plan.Lf0.AsSpan(0, plan.FrameCount)),
                    "short and explicitly punctuated NIT plans retain exact phones, timing and pitch");
            }
            var corrupt = Encoding.UTF8.GetBytes(Contained(Path.Combine(fixture, "破損モデル.htsvoice"), fixture));
            var corruptStatus = api.Load(dictionary, dictionary.Length, corrupt, corrupt.Length);
            api.Poisoned = corruptStatus == 5;
            Verify(corruptStatus == 5, "zero duration-PDF count reaches the guarded fatal HTS path and poisons the process", corruptStatus);
            Verify((status = api.Clear()) == 5, "poisoned state refuses native clear instead of traversing partial state", status);
            Verify((status = api.Load(dictionary, dictionary.Length, model, model.Length)) == 5, "poisoned process refuses even a valid model reload", status);
            status = api.Synthesize(text, text.Length, guard, guard.Length, out count, out rate);
            Verify(status == 5 && count == 0 && rate == 0 && guard[0] == 1234, "poisoned process refuses reuse and returns no partial PCM", status, count, rate);
            plan.FillSentinels();
            status = plan.Call(api, planText);
            Verify(status == 5 && plan.ZeroMetadata() && plan.SentinelsUnchanged(),
                "poisoned process also refuses plan reuse without partial metadata or array writes", status, plan.FrameCount, plan.SampleRate);
            Console.WriteLine(JsonSerializer.Serialize(new NativeReport(checks.ToArray(), firstCount, firstRate, peak, corruptStatus, phones, planFrames)));
            return 0;
        }
        catch (Exception)
        {
            try { Console.Error.WriteLine(FormattableString.Invariant($"open-jtalk-native-error:{stage}:{errorStatus}:{errorCount}:{errorRate}:{errorPeak}")); }
            catch (IOException) { }
            return 1;
        }
    }

    private static void RequirePlatform()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Explicit Open JTalk native integration requires Windows x64.");
    }

    private static string Contained(string path, string root)
    {
        var full = Path.GetFullPath(path); var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetRelativePath(boundary, full).Contains(':')) throw new InvalidDataException("Native fixture path escaped its boundary.");
        WorldComparison.RejectLinks(full);
        return full;
    }

    private static async Task CopyBoundedAsync(string source, string destination, long maximum, CancellationToken ct)
    {
        WorldComparison.RejectLinks(source); WorldComparison.RejectLinks(destination);
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (input.Length < 1 || input.Length > maximum) throw new InvalidDataException("Native fixture file exceeds its bound.");
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, ct).ConfigureAwait(false);
    }

    private static void MakeCorruptModel(string original, string destination)
    {
        var bytes = File.ReadAllBytes(original);
        var lf = bytes.AsSpan().IndexOf("[DATA]\n"u8);
        var crlf = bytes.AsSpan().IndexOf("[DATA]\r\n"u8);
        var data = lf >= 0 ? lf + 7 : crlf >= 0 ? crlf + 8 : -1;
        if (data is < 1 or > 4096 || data + 4 > bytes.Length ||
            BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(data, 4)) == 0 ||
            !Encoding.ASCII.GetString(bytes, 0, data).Contains("\nDURATION_PDF:0-", StringComparison.Ordinal))
            throw new InvalidDataException("The fixed model's duration PDF layout changed.");
        // HTS 1.10 HTS_Model_load_pdf reads this little-endian npdf first;
        // zero reaches HTS_error(1). Keep every offset/tree and file size intact.
        bytes.AsSpan(data, 4).Clear();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        output.Write(bytes);
    }

    private static async Task<string> ReadBoundedAsync(Stream stream, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[1024]; int count;
        while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (bytes.Length + count > PipeLimit) throw new InvalidDataException("Native test pipe exceeded its bound.");
            bytes.Write(buffer, 0, count);
        }
        return new UTF8Encoding(false, true).GetString(bytes.ToArray());
    }

    private sealed class PlanBuffers
    {
        private const int MaximumPhones = 8192;
        private const int MaximumFrames = 9000;
        internal readonly byte[] Slots = new byte[MaximumPhones * 32];
        internal readonly int[] Starts = new int[MaximumPhones];
        internal readonly int[] Ends = new int[MaximumPhones];
        internal readonly double[] Lf0 = new double[MaximumFrames];
        internal readonly byte[] Voiced = new byte[MaximumFrames];
        internal int PhoneCount, FrameCount, SampleRate, FramePeriod;
        internal int Call(NativeApi api, byte[] text, int phoneCapacity = MaximumPhones, int frameCapacity = MaximumFrames)
            => api.Plan(text, text.Length, Slots, phoneCapacity, Starts, Ends, Lf0, Voiced, frameCapacity,
                out PhoneCount, out FrameCount, out SampleRate, out FramePeriod);
        internal bool ValidMetadata() => PhoneCount is >= 1 and <= MaximumPhones && FrameCount is >= 1 and <= MaximumFrames &&
            SampleRate == Rate && FramePeriod == 240;
        internal bool ZeroMetadata() => PhoneCount == 0 && FrameCount == 0 && SampleRate == 0 && FramePeriod == 0;
        internal void FillSentinels()
        {
            Array.Fill(Slots, (byte)0xa5); Array.Fill(Starts, -1234567); Array.Fill(Ends, -1234567);
            Array.Fill(Lf0, -1234567.0); Array.Fill(Voiced, (byte)0xa5);
        }
        internal bool SentinelsUnchanged() => Slots.All(value => value == 0xa5) && Starts.All(value => value == -1234567) &&
            Ends.All(value => value == -1234567) && Lf0.All(value => value == -1234567.0) && Voiced.All(value => value == 0xa5);
        internal bool TryGetPhones(out string[] phones)
        {
            phones = [];
            if (!ValidMetadata()) return false;
            var decoded = new string[PhoneCount]; var previousEnd = 0;
            for (var i = 0; i < PhoneCount; i++)
            {
                var slot = Slots.AsSpan(i * 32, 32); var end = slot.IndexOf((byte)0);
                if (end is < 1 or > 31 || Starts[i] != previousEnd || Ends[i] <= Starts[i] || Ends[i] > FrameCount)
                    return false;
                for (var j = 0; j < slot.Length; j++)
                    if (j < end ? slot[j] is < 33 or > 126 : slot[j] != 0) return false;
                decoded[i] = Encoding.ASCII.GetString(slot[..end]); previousEnd = Ends[i];
            }
            if (previousEnd != FrameCount) return false;
            phones = decoded;
            return true;
        }
    }

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint SetErrorMode(uint mode);
    [DllImport("msvcrt.dll", EntryPoint = "_set_error_mode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetCrtErrorMode(int mode);

    private sealed class NativeApi : IDisposable
    {
        private readonly nint library;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SimpleCall();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int LoadCall(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] dictionary, int dictionaryBytes,
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] model, int modelBytes);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int SynthesizeCall(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] text, int textBytes,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] short[] samples, int capacity,
            out int count, out int sampleRate);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int PlanCall(
            [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] text, int textBytes,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8192 * 32)] byte[] slots, int phoneCapacity,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] starts,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] ends,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] double[] lf0,
            [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] byte[] voiced, int frameCapacity,
            out int phoneCount, out int frameCount, out int sampleRate, out int framePeriod);
        internal SimpleCall Version { get; }
        internal LoadCall Load { get; }
        internal SynthesizeCall Synthesize { get; }
        internal SimpleCall Clear { get; }
        internal PlanCall Plan { get; }
        internal bool Poisoned { get; set; }
        internal NativeApi(string path)
        {
            library = NativeLibrary.Load(path);
            try
            {
                Version = Export<SimpleCall>("toj_abi_version"); Load = Export<LoadCall>("toj_load");
                Synthesize = Export<SynthesizeCall>("toj_synthesize"); Clear = Export<SimpleCall>("toj_clear");
                Plan = Export<PlanCall>("toj_plan");
            }
            catch { NativeLibrary.Free(library); throw; }
        }
        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        public void Dispose()
        {
            // Terminal partial state/module belongs to OS process reclamation.
            if (Poisoned) return;
            var status = Clear();
            if (status == 0) NativeLibrary.Free(library);
        }
    }
}
