using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Tanayomi.Desktop;

internal sealed class SupertonicSpeech(IPcmOutput output, string? executableOverride = null) : ISpeechOutput, ISpeechPreparation, IOpenJtalkVoiceSelection
{
    internal const string Credit = "Supertonic 3 © 2026 Supertone Inc. / sherpa-onnx INT8";
    internal const string TermsUrl = "https://huggingface.co/Supertone/supertonic-3/blob/724fb5abbf5502583fb520898d45929e62f02c0b/LICENSE";
    internal const string Usage = "機械生成音声であることを配信で明示してください。OpenRAIL-Mの用途制限が適用されます。";
    private static IReadOnlyList<OpenJtalkVoiceChoice> LoadVoices(string? executable)
    {
        var terms = SupertonicPcmWorker.ReadTerms(executable);
        return Array.AsReadOnly(Enumerable.Range(0, SupertonicProtocol.SpeakerCount)
            .Select(speaker => new OpenJtalkVoiceChoice($"supertonic3-{speaker}", $"Supertonic 3 日本語 ({speaker + 1})",
                Credit, Usage, TermsUrl, SupertonicPcmWorker.TermsSha256, terms)).ToArray());
    }

    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposeGate = new();
    private Process? worker;
    private string? temporary;
    private bool stopping;
    private Task? disposal;
    private int disposed;
    private string voiceId = "supertonic3-0";

    public string Name => "Supertonic 3 日本語";
    public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } = LoadVoices(executableOverride);
    public string VoiceId
    {
        get => Volatile.Read(ref voiceId);
        set
        {
            _ = ResolveSpeaker(value);
            Volatile.Write(ref voiceId, value);
        }
    }

    public Task SpeakAsync(string text, CancellationToken ct) => SpeakAsync(VoiceId, text, ct);

    internal Task<PcmAudio> SynthesizeAsync(string voiceId, string text, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(text);
        return ExchangeAsync(SupertonicProtocol.Encode(ResolveSpeaker(voiceId), text), ct);
    }

    internal async Task SpeakAsync(string voiceId, string text, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetime.Token);
        var audio = await SynthesizeAsync(voiceId, text, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        await output.PlayAsync(audio.Samples, audio.SampleRate, linked.Token).ConfigureAwait(false);
    }

    Task ISpeechPreparation.PrepareAsync(CancellationToken ct) => PrepareAsync(ct);
    internal async Task PrepareAsync(CancellationToken ct) =>
        _ = await ExchangeAsync(SupertonicProtocol.Encode(0, null), ct).ConfigureAwait(false);

    private int ResolveSpeaker(string id)
    {
        for (var index = 0; index < Voices.Count; index++)
            if (Voices[index].Id == id) return index;
        throw new ArgumentOutOfRangeException(nameof(id));
    }

    private async Task<PcmAudio> ExchangeAsync(byte[] request, CancellationToken cancellation)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        try { await serial.WaitAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && !lifetime.IsCancellationRequested)
        { throw new TimeoutException("SUPERTONIC_TIMEOUT: 音声処理の待機が時間内に完了しませんでした。"); }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (stopping || worker is null || worker.HasExited)
            {
                await StopWorkerAsync().ConfigureAwait(false);
                StartWorker();
            }
            var active = worker!;
            using var registration = ct.Register(() => Kill(active));
            var input = active.StandardInput.BaseStream;
            await input.WriteAsync(request, ct).ConfigureAwait(false);
            await input.FlushAsync(ct).ConfigureAwait(false);
            var preparation = request.Length == SupertonicProtocol.HeaderBytes;
            var audio = await SupertonicProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, preparation, ct)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return audio;
        }
        catch (Exception error)
        {
            await StopWorkerAsync().ConfigureAwait(false);
            cancellation.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested)
                throw new TimeoutException("SUPERTONIC_TIMEOUT: 音声処理が時間内に完了しませんでした。");
            if (error is SupertonicWorkerException) throw;
            throw new IOException($"SUPERTONIC_WORKER: 音声処理に失敗しました ({error.GetType().Name})。");
        }
        finally { serial.Release(); }
    }

    private void StartWorker()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("SUPERTONIC_PLATFORM");
        var executable = executableOverride ?? Environment.ProcessPath ?? throw new IOException("SUPERTONIC_EXECUTABLE");
        var data = executableOverride is null
            ? AppPaths.DataDirectory
            : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executable))!, "data");
        var temporaryParent = Path.Combine(data, "tmp");
        WorldComparison.RejectLinks(temporaryParent);
        Directory.CreateDirectory(temporaryParent);
        temporary = Path.Combine(temporaryParent, "st-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = temporary,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(SupertonicPcmWorker.Argument);
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(key) is { Length: > 0 } value) start.Environment[key] = value;
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[key] = temporary;
        start.Environment["ORT_DISABLE_TELEMETRY"] = "1";
        worker = NativeChildProcess.Start(start) ?? throw new IOException("SUPERTONIC_START");
        _ = DiscardAsync(worker.StandardError.BaseStream);
    }

    private static async Task DiscardAsync(Stream input)
    {
        try { await input.CopyToAsync(Stream.Null).ConfigureAwait(false); }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private async Task StopWorkerAsync()
    {
        stopping = true;
        if (worker is { } active)
        {
            Kill(active);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try { await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
            catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { throw new IOException("SUPERTONIC_STOP: 音声ワーカーの終了を確認できませんでした。"); }
            active.Dispose();
            worker = null;
        }
        if (temporary is { } path)
        {
            WorldComparison.RejectLinks(path);
            Directory.Delete(path);
            temporary = null;
        }
        stopping = false;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposeGate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                lifetime.Cancel();
                disposal = DisposeCoreAsync();
            }
            return new ValueTask(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await serial.WaitAsync().ConfigureAwait(false);
        try { await StopWorkerAsync().ConfigureAwait(false); }
        finally { serial.Release(); }
    }
}

internal sealed class SupertonicWorkerException(int status)
    : IOException($"SUPERTONIC_{status}: 音声ワーカーの処理に失敗しました。");

internal static class SupertonicProtocol
{
    internal const int SpeakerCount = 10;
    internal const int SampleRate = 44100;
    internal const int MaximumSamples = SampleRate * 45;
    internal const int MaximumTextBytes = 2048;
    internal const int HeaderBytes = 8;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(int speaker, string? text)
    {
        ValidateSpeaker(speaker);
        var count = text is null ? 0 : ValidateText(text);
        var frame = new byte[HeaderBytes + count];
        BinaryPrimitives.WriteInt32LittleEndian(frame, speaker);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), count);
        if (text is not null) Utf8.GetBytes(text, frame.AsSpan(HeaderBytes));
        return frame;
    }

    internal static (int Speaker, string? Text)? ReadRequest(Stream input)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        if (input.Read(header[..1]) == 0) return null;
        input.ReadExactly(header[1..]);
        var speaker = BinaryPrimitives.ReadInt32LittleEndian(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[4..]);
        ValidateSpeaker(speaker);
        if (count is < 0 or > MaximumTextBytes) throw new InvalidDataException("SUPERTONIC_TEXT_SIZE");
        if (count == 0) return (speaker, null);
        var bytes = new byte[count];
        input.ReadExactly(bytes);
        var text = Utf8.GetString(bytes);
        _ = ValidateText(text);
        return (speaker, text);
    }

    private static void ValidateSpeaker(int speaker)
    {
        if ((uint)speaker >= SpeakerCount) throw new InvalidDataException("SUPERTONIC_SPEAKER");
    }

    private static int ValidateText(string text)
    {
        if (text.Length > MaximumTextBytes || string.IsNullOrWhiteSpace(text) || text.Contains('\0'))
            throw new InvalidDataException("SUPERTONIC_TEXT");
        var count = Utf8.GetByteCount(text);
        if (count > MaximumTextBytes) throw new InvalidDataException("SUPERTONIC_TEXT_SIZE");
        return count;
    }

    internal static void WriteResponse(Stream output, int status, ReadOnlySpan<short> pcm)
    {
        Span<byte> header = stackalloc byte[HeaderBytes];
        BinaryPrimitives.WriteInt32LittleEndian(header, status);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], pcm.Length);
        output.Write(header);
        output.Write(MemoryMarshal.AsBytes(pcm));
        output.Flush();
    }

    internal static async Task<PcmAudio> ReadResponseAsync(Stream input, bool preparation, CancellationToken ct)
    {
        var header = new byte[HeaderBytes];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (status is >= 1 and <= 3 && count == 0) throw new SupertonicWorkerException(status);
        if (status != 0 || count < 0 || count > MaximumSamples || (count == 0) != preparation)
            throw new InvalidDataException("SUPERTONIC_PCM_FRAME");
        if (preparation) return new PcmAudio([], SampleRate);
        var samples = new short[count];
        var length = count * sizeof(short);
        var buffer = new byte[Math.Min(length, 32 * 1024)];
        for (var offset = 0; offset < length;)
        {
            var size = Math.Min(buffer.Length, length - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
            Buffer.BlockCopy(buffer, 0, samples, offset, size);
            offset += size;
        }
        return new PcmAudio(samples, SampleRate);
    }
}
