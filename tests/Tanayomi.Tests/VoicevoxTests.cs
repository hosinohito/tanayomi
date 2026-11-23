using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tanayomi;
using Tanayomi.Desktop;

internal static class VoicevoxTests
{
    internal static async Task RunProductAsync(string executable, Action<bool, string> check)
    {
        executable = Path.GetFullPath(executable);
        var workspace = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local")) + Path.DirectorySeparatorChar;
        if (!executable.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable) != "Tanayomi.Desktop.exe" || !File.Exists(executable))
            throw new ArgumentException("Specify a product exe under the workspace .local directory.");
        WorldComparison.RejectLinks(executable);
        var productDirectory = Path.GetDirectoryName(executable)!;
        var temporary = Path.Combine(productDirectory, "data", "tmp");
        var allowed = new HashSet<string>(
            ["SystemRoot", "WINDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64", "TEMP", "TMP", "TMPDIR", "RUST_LOG", "RUST_BACKTRACE"],
            StringComparer.OrdinalIgnoreCase);
        var confined = true;
        var launches = 0;
        var workerPid = 0;
        await using var output = new PcmProbe();
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        Process? lastWorker = null;
        try
        {
            await using (var speech = new VoicevoxSpeech(output, executable, start =>
            {
                confined &= start.Environment.Keys.All(allowed.Contains) &&
                    start.Environment["TEMP"] == temporary && start.Environment["TMP"] == temporary &&
                    start.Environment["TMPDIR"] == temporary && start.Environment["RUST_LOG"] == "off";
                var process = Process.Start(start) ?? throw new IOException("VOICEVOX product worker start failed.");
                workerPid = process.Id;
                launches++;
                return process;
            }))
            {
                using var cancel = new CancellationTokenSource();
                var interrupted = speech.SpeakAsync("途中で読み上げを取り消します。", cancel.Token);
                using var coldWorker = workerPid == 0 ? null : Process.GetProcessById(workerPid);
                _ = coldWorker?.SafeHandle;
                cancel.Cancel();
                var cancelled = false;
                try { await interrupted; }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested) { cancelled = true; }
                check(cancelled && output.Calls == 0 && launches == 1 && coldWorker?.HasExited == true,
                    "VOICEVOX product cancels a cold synthesis, reaps the child and produces no playback");

                await speech.SpeakAsync("こんにちは。", deadline.Token);
                lastWorker = Process.GetProcessById(workerPid);
                _ = lastWorker.SafeHandle;
                check(launches == 2 && output.Calls == 1 && output.HasAudio && confined,
                    "VOICEVOX product resumes on the next utterance with a fresh child and restricted environment");
                var initialVoice = speech.VoiceId;
                var initial = await speech.SynthesizeAsync("こんにちは。", deadline.Token);

                foreach (var voice in VoicevoxCatalog.Voices.GroupBy(x => x.ModelFile).SelectMany(model => model))
                {
                    speech.VoiceId = voice.Id;
                    var before = output.Calls;
                    await speech.SpeakAsync("こんにちは。", deadline.Token);
                    check(output.Calls == before + 1 && output.HasAudio,
                        $"VOICEVOX product adapter returns bounded non-silent 24kHz PCM: {voice.Id}");
                }
                check(speech.Voices.Count == 95 && launches == 2,
                    "VOICEVOX product reuses one live worker across all 95 selectable styles");

                speech.VoiceId = initialVoice;
                var reloaded = await speech.SynthesizeAsync("こんにちは。", deadline.Token);
                check(initial.SampleRate == reloaded.SampleRate && initial.Samples.AsSpan().SequenceEqual(reloaded.Samples) && launches == 2,
                    "VOICEVOX returns identical PCM after reselecting its first model on the same worker");

                using var warmCancel = new CancellationTokenSource();
                var warmInterrupted = speech.SynthesizeAsync("読み上げの途中で取り消します。", warmCancel.Token);
                warmCancel.Cancel();
                var warmCancelled = false;
                try { _ = await warmInterrupted; }
                catch (OperationCanceledException) when (warmCancel.IsCancellationRequested) { warmCancelled = true; }
                check(warmCancelled && lastWorker.HasExited,
                    "VOICEVOX cancellation reaps the worker after opening all selectable models");

                var recovered = await speech.SynthesizeAsync("こんにちは。", deadline.Token);
                lastWorker.Dispose();
                lastWorker = Process.GetProcessById(workerPid);
                _ = lastWorker.SafeHandle;
                check(launches == 3 && recovered.SampleRate == initial.SampleRate && recovered.Samples.AsSpan().SequenceEqual(initial.Samples),
                    "VOICEVOX reopens its model on a new worker after warm cancellation and preserves PCM");
            }
            check(!output.Disposed && lastWorker?.HasExited == true,
                "VOICEVOX product releases its child and leaves the borrowed PCM output to its owner");
        }
        finally { lastWorker?.Dispose(); }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        using (var fixture = new TestDirectory())
        {
            var package = Path.Combine(fixture.DirectoryPath, "voicevox");
            check(!VoicevoxPcmWorker.HasPackageAt(package), "VOICEVOX only treats a completely absent package as unavailable");
            Directory.CreateDirectory(package);
            check(Fails(() => VoicevoxPcmWorker.HasPackageAt(package)) is FileNotFoundException,
                "VOICEVOX reports a present package with a missing manifest");
            var manifest = Path.Combine(package, "voicevox-assets.json");
            Directory.CreateDirectory(manifest);
            check(Fails(() => VoicevoxPcmWorker.HasPackageAt(package)) is IOException,
                "VOICEVOX reports a directory in place of the manifest");
            Directory.Delete(manifest);
            File.WriteAllText(manifest, "{}");
            check(VoicevoxPcmWorker.HasPackageAt(package), "VOICEVOX detects the installed package for native validation");
        }

        var catalog = VoicevoxCatalog.Voices;
        check(catalog.Count == 95 && catalog.Select(x => x.Speaker).Distinct().Count() == 41 &&
            catalog.Select(x => x.ModelFile).Distinct().Count() == 21 &&
            catalog.All(x => VoicevoxCatalog.Find(x.Id) == x && VoicevoxCatalog.Find(x.StyleId) == x &&
                VoicevoxCatalog.ModelSha256(x.ModelFile).Length == 64 && x.Credit.StartsWith("VOICEVOX", StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(x.Usage) && Uri.TryCreate(x.TermsUrl, UriKind.Absolute, out _)),
            "VOICEVOX catalog exposes the fixed audited voice and credit set");
        check(new uint[] { 13, 20, 55, 58, 74, 89, 90, 102, 110, 126, 3000 }.All(id => VoicevoxCatalog.Find(id) is null),
            "VOICEVOX catalog excludes unapproved mixed-file styles and song IDs");
        check(new uint[] { 8, 9, 21, 23, 24, 25, 26, 43, 44, 45, 51, 65, 67, 99, 101 }
            .All(id => VoicevoxCatalog.Find(id) is not null) &&
            VoicevoxCatalog.Find(51u)?.Credit == "VOICEVOX †聖騎士 紅桜†" &&
            VoicevoxCatalog.Find(99u)?.Credit == "VOICEVOX 離途",
            "VOICEVOX catalog includes all newly audited styles and uses the rights holders' credits");

        var voice = VoicevoxCatalog.Find(3u)!;
        var request = VoicevoxProtocol.Encode(voice, "テスト");
        using (var input = new MemoryStream(request))
        {
            var command = VoicevoxProtocol.ReadRequest(input);
            check(command?.Voice == voice && command.Text == "テスト" && VoicevoxProtocol.ReadRequest(input) is null,
                "VOICEVOX worker request preserves the selected style and validated UTF-8");
        }
        var forged = request.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(8), 13);
        using (var input = new MemoryStream(forged))
        {
            var processed = false;
            using var response = new MemoryStream();
            check(Fails(() => VoicevoxPcmWorker.RunRequests(input, response, (_, _) => { processed = true; return null; })) is InvalidDataException &&
                !processed && response.Length == 0 && input.Position == 16,
                "VOICEVOX worker rejects excluded styles before consuming text or invoking synthesis");
        }
        using (var input = new MemoryStream(request[..^1]))
            check(Fails(() => VoicevoxProtocol.ReadRequest(input)) is EndOfStreamException, "VOICEVOX worker rejects truncated requests");
        check(Fails(() => VoicevoxProtocol.Encode(voice, "abc\0def")) is InvalidDataException &&
            Fails(() => VoicevoxProtocol.Encode(voice, new string('あ', 1025))) is InvalidDataException,
            "VOICEVOX worker rejects embedded NUL and overlong input");

        var pcm = new short[] { -11, 0, 11, 5 };
        var wave = CreateWave(pcm);
        var audio = VoicevoxProtocol.DecodeWave(wave);
        check(audio.SampleRate == 24000 && audio.Samples.SequenceEqual(pcm),
            "VOICEVOX WAV parser preserves mono PCM16 samples");
        check(Fails(() => VoicevoxProtocol.DecodeWave(wave[..^1])) is InvalidDataException,
            "VOICEVOX WAV parser rejects truncated samples");
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 2);
        check(Fails(() => VoicevoxProtocol.DecodeWave(wave)) is InvalidDataException,
            "VOICEVOX WAV parser rejects unexpected channel count");
        CheckNativeWave(check);

        using var commands = new MemoryStream([.. VoicevoxProtocol.Encode(voice, null), .. request]);
        using var responses = new MemoryStream();
        var called = 0;
        check(VoicevoxPcmWorker.RunRequests(commands, responses, (selected, text) =>
        {
            if (selected != voice) throw new InvalidDataException();
            called++;
            return text is null ? null : audio;
        }) == 0 && called == 2, "VOICEVOX worker keeps a session across prepare and synthesis commands");
        responses.Position = 0;
        check(await VoicevoxProtocol.ReadResponseAsync(responses, true, CancellationToken.None) is null,
            "VOICEVOX worker prepares without returning audio");
        check((await VoicevoxProtocol.ReadResponseAsync(responses, false, CancellationToken.None))?.Samples.SequenceEqual(pcm) == true,
            "VOICEVOX worker returns bounded PCM");

        using var failure = new MemoryStream();
        using var failedRequest = new MemoryStream(request);
        check(VoicevoxPcmWorker.RunRequests(failedRequest, failure, (_, _) => throw new IOException("secret text")) == 4,
            "VOICEVOX worker contains unexpected exceptions");
        check(failure.Length == 16, "VOICEVOX error response contains only a fixed numeric status");
        failure.Position = 0;
        try
        {
            await VoicevoxProtocol.ReadResponseAsync(failure, false, CancellationToken.None);
            check(false, "VOICEVOX failures cannot appear as successful PCM");
        }
        catch (VoicevoxWorkerException error) { check(error.Status == 241, "VOICEVOX reports a stable sanitized worker failure"); }

        await BundledPcmResponseTests.CheckAsync("VOICEVOX", VoicevoxProtocol.SampleRate, VoicevoxProtocol.MaximumSamples,
            (stream, samples) => VoicevoxProtocol.WriteResponse(stream, 0,
                samples is null ? null : new PcmAudio(samples, VoicevoxProtocol.SampleRate)),
            VoicevoxProtocol.ReadResponseAsync, check);
    }

    private static byte[] CreateWave(short[] pcm)
    {
        var wave = new byte[44 + pcm.Length * 2];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), wave.Length - 8);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), 24000);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), 48000);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), pcm.Length * 2);
        for (var i = 0; i < pcm.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(44 + i * 2), pcm[i]);
        return wave;
    }

    private static void CheckNativeWave(Action<bool, string> check)
    {
        var expected = new short[] { short.MinValue, 0, short.MaxValue };
        var ordinary = CreateWave(expected);
        byte[] padded = [.. ordinary.AsSpan(0, 12), .. "JUNK\x01\0\0\0"u8, 123, 0, .. ordinary.AsSpan(12)];
        BinaryPrimitives.WriteInt32LittleEndian(padded.AsSpan(4), padded.Length - 8);
        var maximum = new short[VoicevoxProtocol.MaximumSamples];
        maximum[0] = short.MinValue; maximum[^1] = short.MaxValue;
        foreach (var (wave, pcm) in new[] { (ordinary, expected), (padded, expected), (CreateWave(maximum), maximum) })
            check(Decode(wave).Samples.AsSpan().SequenceEqual(pcm),
                $"VOICEVOX native WAV preserves signed PCM in a {wave.Length}-byte RIFF frame");

        var invalidSize = ordinary.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(invalidSize.AsSpan(40), int.MaxValue);
        var invalidRate = ordinary.ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(invalidRate.AsSpan(24), 44100);
        var duplicate = ordinary.Concat(ordinary[36..]).ToArray();
        BinaryPrimitives.WriteInt32LittleEndian(duplicate.AsSpan(4), duplicate.Length - 8);
        byte[] dataFirst = [.. ordinary.AsSpan(0, 12), .. ordinary.AsSpan(36), .. ordinary.AsSpan(12, 24)];
        check(new[] { ordinary[..^1], invalidSize, invalidRate, duplicate, dataFirst, CreateWave([0, 0]) }
            .All(wave => Fails(() => Decode(wave)) is InvalidDataException),
            "VOICEVOX native WAV rejects truncation, invalid chunk size or format, duplicate data and silence");
        check(Fails(() => VoicevoxProtocol.DecodeNativeWave(0, 46)) is InvalidDataException &&
            Fails(() => VoicevoxProtocol.DecodeNativeWave(1, 45)) is InvalidDataException &&
            Fails(() => VoicevoxProtocol.DecodeNativeWave(1, VoicevoxProtocol.MaximumWaveBytes + 1)) is InvalidDataException,
            "VOICEVOX native WAV rejects invalid pointer or bounds before reading memory");

        static PcmAudio Decode(byte[] wave)
        {
            var address = Marshal.AllocHGlobal(wave.Length);
            try
            {
                Marshal.Copy(wave, 0, address, wave.Length);
                return VoicevoxProtocol.DecodeNativeWave(address, (nuint)wave.Length);
            }
            finally { Marshal.FreeHGlobal(address); }
        }
    }

    private static Exception? Fails(Action action)
    {
        try { action(); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class PcmProbe : IPcmOutput
    {
        internal int Calls { get; private set; }
        internal bool HasAudio { get; private set; }
        internal bool Disposed { get; private set; }

        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            HasAudio = sampleRate == VoicevoxProtocol.SampleRate && samples.Length <= VoicevoxProtocol.MaximumSamples &&
                samples.Span.ContainsAnyExcept((short)0);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}

internal static class BundledPcmResponseTests
{
    internal static async Task CheckAsync(string engine, int rate, int maximum,
        Action<Stream, short[]?> write, Func<Stream, bool, CancellationToken, Task<PcmAudio?>> read,
        Action<bool, string> check)
    {
        foreach (var count in new[] { 431, 16383, 16384, 16385, maximum })
        {
            var samples = Samples(count);
            using var reply = new MemoryStream();
            write(reply, samples);
            var frameLength = reply.Length;
            write(reply, null);
            using var stream = new FragmentedResponse(reply.ToArray());
            var audio = await read(stream, false, default);
            check(audio?.SampleRate == rate && audio.Samples.AsSpan().SequenceEqual(samples),
                $"{engine} preserves {count} PCM samples across seven-byte fragments");
            var atNextFrame = stream.Position == frameLength;
            var acknowledgment = await read(stream, true, default);
            check(atNextFrame && (acknowledgment is null || acknowledgment.Samples.Length == 0) &&
                stream.Position == stream.Length, $"{engine} preserves the next response frame after {count} samples");
        }

        using var response = new MemoryStream();
        write(response, Samples(16385));
        var frame = response.ToArray();
        using var truncated = new FragmentedResponse(frame[..^1]);
        var eof = false;
        try { _ = await read(truncated, false, default); }
        catch (EndOfStreamException) { eof = true; }
        check(eof, $"{engine} rejects EOF after a complete PCM chunk and part of the final sample");

        using var cancel = new CancellationTokenSource();
        using var interrupted = new FragmentedResponse(frame, cancel);
        var cancelled = false;
        try { _ = await read(interrupted, false, cancel.Token); }
        catch (OperationCanceledException error) when (error.CancellationToken == cancel.Token) { cancelled = true; }
        check(cancelled && interrupted.Position >= 32 * 1024 && interrupted.Position < frame.Length,
            $"{engine} observes caller cancellation in a partially received PCM response");
    }

    private static short[] Samples(int count)
    {
        var samples = new short[count];
        for (var i = 0; i < samples.Length; i++) samples[i] = unchecked((short)(short.MinValue + i * 257));
        return samples;
    }

    private sealed class FragmentedResponse(byte[] frame, CancellationTokenSource? interrupt = null)
        : MemoryStream(frame, writable: false)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var count = Read(buffer.Span[..Math.Min(buffer.Length, 7)]);
            if (Position >= 32 * 1024) interrupt?.Cancel();
            return ValueTask.FromResult(count);
        }
    }
}
