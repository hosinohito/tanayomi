using System.Diagnostics;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>A hung native synthesizer can be killed without stopping the UI or receivers.</summary>
public sealed class DesktopSpeech : ISpeechOutput, ISpeechVolumeControl
{
    // SVSFIsNotXML. No async/file/XML flags: chat content is synchronous text,
    // even when its first character is '<' (SAPI's default XML autodetection).
    private const int WindowsSynchronousPlainTextFlags = 16;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly Func<ProcessStartInfo, IDesktopSpeechWorker> startWorker;
    private readonly bool gracefulStop;
    private readonly TimeSpan stopTimeout;
    private readonly object disposalGate = new();
    private IDesktopSpeechWorker? worker;
    private bool workerStopping, nativeStopUnconfirmed;
    private Task? disposal;
    private int disposed;
    public string Name => "OS標準音声";
    private PlaybackVolume volume = new();
    public PlaybackVolume Volume { get => Volatile.Read(ref volume); set => Volatile.Write(ref volume, value ?? throw new ArgumentNullException(nameof(value))); }

    public DesktopSpeech() : this(start => new ProcessWorker(start), OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) { }
    internal DesktopSpeech(Func<ProcessStartInfo, IDesktopSpeechWorker> startWorker, bool gracefulStop = false,
        TimeSpan? stopTimeout = null)
    {
        this.startWorker = startWorker ?? throw new ArgumentNullException(nameof(startWorker));
        this.gracefulStop = gracefulStop;
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(3);
        if (this.stopTimeout <= TimeSpan.Zero || this.stopTimeout > TimeSpan.FromSeconds(3)) throw new ArgumentOutOfRangeException(nameof(stopTimeout));
        lifetimeToken = lifetime.Token;
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (text is null || text.Length > 2048) throw new ArgumentException("標準音声の入力が長すぎます。", nameof(text));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        var entered = false;
        try
        {
            await serial.WaitAsync(token).ConfigureAwait(false); entered = true;
            token.ThrowIfCancellationRequested();
            var selectedVolume = Volume.Percent;
            if (selectedVolume == 0) return;
            if (workerStopping || worker is null || worker.HasExited) await StartWorkerAsync(token).ConfigureAwait(false);
            var active = worker!;
            using var registration = token.Register(() => RequestStop(active));
            var request = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { text, volume = selectedVolume }) + "\n");
            await active.Input.WriteAsync(request, token).ConfigureAwait(false);
            await active.Input.FlushAsync(token).ConfigureAwait(false);
            var result = await ReadReplyAsync(active.Output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (result != "ok") throw new IOException("標準音声ワーカーが発言を処理できませんでした。");
        }
        catch
        {
            ExceptionDispatchInfo? cleanup = null;
            if (entered && !workerStopping)
            {
                try { await StopWorkerAsync().ConfigureAwait(false); }
                catch (Exception error) { cleanup = ExceptionDispatchInfo.Capture(error); }
            }
            // A failed start already attempted to reap the retained process.
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("標準音声が時間内に完了しませんでした。");
            cleanup?.Throw();
            throw;
        }
        finally { if (entered) serial.Release(); }
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        await StopWorkerAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        var executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--speech-worker");
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        WorldComparison.RejectLinks(temporary);
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
        worker = startWorker(start);
        _ = DrainAsync(worker.Error);
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    private void RequestStop(IDesktopSpeechWorker process)
    {
        if (!gracefulStop) { process.Kill(); return; }
        // EOF asks the managed Linux worker to CANCEL SELF before disconnecting
        // from Speech Dispatcher. Killing only its client leaves daemon audio playing.
        try { process.Input.Close(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) when (ex is IOException or InvalidOperationException) { process.Kill(); }
    }

    private async Task StopWorkerAsync()
    {
        if (worker is not { } process) return;
        workerStopping = true;
        if (nativeStopUnconfirmed) throw StopFailure();
        RequestStop(process);
        try
        {
            using var deadline = new CancellationTokenSource(stopTimeout);
            await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (!process.HasExited) throw new IOException();
            if (gracefulStop && process.ExitCode != 0) { nativeStopUnconfirmed = true; throw StopFailure(); }
            process.Dispose(); worker = null; workerStopping = false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            if (gracefulStop && !nativeStopUnconfirmed)
            {
                // Give EOF -> CANCEL SELF its chance first. A forced exit is
                // reaped too, but does not prove the daemon stopped our speech.
                nativeStopUnconfirmed = true; process.Kill();
                using var deadline = new CancellationTokenSource(stopTimeout);
                try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
                catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
            }
            throw StopFailure(); // Retain ownership; never replace an unconfirmed worker.
        }
    }
    private IOException StopFailure() => new(gracefulStop
        ? "標準音声の停止を確認できませんでした。OSの音声サービスを確認してアプリを再起動してください。"
        : "標準音声ワーカーの終了を確認できませんでした。再試行するかアプリを終了してください。");

    internal static async Task DrainAsync(Stream input)
    {
        try { var buffer = new byte[512]; while (await input.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    internal static async Task<string?> ReadReplyAsync(Stream input, CancellationToken ct)
    {
        var reply = new byte[256]; var next = new byte[1]; var count = 0;
        while (true)
        {
            if (await input.ReadAsync(next, ct).ConfigureAwait(false) == 0)
            { if (count == 0) return null; throw new IOException("標準音声ワーカーの応答が途中で終了しました。"); }
            if (next[0] == '\n') return Encoding.ASCII.GetString(reply, 0, count > 0 && reply[count - 1] == '\r' ? count - 1 : count);
            if (next[0] > 127 || count == reply.Length) throw new IOException("標準音声ワーカーの応答が不正です。");
            reply[count++] = next[0];
        }
    }

    internal static int RunWorker()
    {
        try { return RunWorkerCore(); }
        catch (Exception ex) { Console.WriteLine($"error:{ex.GetType().Name}:{ex.HResult:X8}"); return 1; }
    }

    private static int RunWorkerCore()
    {
        if (OperatingSystem.IsLinux()) return RunLinuxWorkerAsync().GetAwaiter().GetResult();
        if (OperatingSystem.IsMacOS()) return MacSpeech.RunWorker();
        string? line;
        while ((line = ReadRequestLineAsync(Console.In).GetAwaiter().GetResult()) is not null)
        {
            try
            {
                var request = ParseRequest(line);
                if (request.Volume == 0) { Console.WriteLine("ok"); continue; }
                if (OperatingSystem.IsWindows()) SpeakWindows(request.Text, request.Volume);
                else throw new PlatformNotSupportedException();
                Console.WriteLine("ok");
            }
            catch (Exception ex) { Console.WriteLine($"error:{ex.GetType().Name}:{ex.HResult:X8}"); }
        }
        return 0;
    }

    private static async Task<int> RunLinuxWorkerAsync()
    {
        // Read ahead only one command so stdin closure interrupts an active utterance.
        // The parent sends each new utterance only after receiving its predecessor's reply.
        // Console.In is a synchronized reader whose ReadLineAsync may block inline.
        using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true));
        var next = ReadRequestLineAsync(input);
        while (await next.ConfigureAwait(false) is { } line)
        {
            using var cancellation = new CancellationTokenSource();
            try
            {
                var request = ParseRequest(line);
                next = ReadRequestLineAsync(input);
                var speaking = new LinuxSpeech { Volume = new PlaybackVolume { Percent = request.Volume } }.SpeakAsync(request.Text, cancellation.Token);
                var finished = await Task.WhenAny(speaking, next).ConfigureAwait(false);
                if (finished == next)
                {
                    // EOF or an out-of-order command both terminate this worker.
                    cancellation.Cancel();
                    try { await speaking.ConfigureAwait(false); }
                    catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
                    return 0;
                }
                await speaking.ConfigureAwait(false);
                Console.WriteLine("ok");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"error:{ex.GetType().Name}:{ex.HResult:X8}");
                Console.Out.Flush();
                return 1;
            }
        }
        return 0;
    }

    [SupportedOSPlatform("windows")]
    private static void SpeakWindows(string text, int volume)
    {
        var type = Type.GetTypeFromProgID("SAPI.SpVoice") ?? throw new PlatformNotSupportedException();
        var instance = Activator.CreateInstance(type) ?? throw new IOException("Speech unavailable.");
        try
        {
            dynamic voice = instance;
            // SAPI tokens use hexadecimal LCIDs. Fall back to the user's default if Japanese is absent.
            dynamic voices = voice.GetVoices("Language=411", "");
            try { if (voices.Count > 0) voice.Voice = voices.Item(0); }
            finally { Marshal.FinalReleaseComObject(voices); }
            if (volume <= 100) SpeakWindowsText(instance, text, volume);
            else
            {
                var streamType = Type.GetTypeFromProgID("SAPI.SpMemoryStream") ?? throw new PlatformNotSupportedException();
                var stream = Activator.CreateInstance(streamType) ?? throw new IOException("Speech stream unavailable.");
                try
                {
                    var pcm = SynthesizeWindowsPcm(instance, stream, text);
                    var wave = CreateBoostedWindowsWave(pcm, volume);
                    // Synchronous memory playback remains inside the killable speech worker.
                    if (!PlaySound(wave, 0, 0x0004 | 0x0002)) throw new IOException("Windows audio output failed.");
                }
                finally
                {
                    try { voice.AudioOutputStream = null; }
                    finally { Marshal.FinalReleaseComObject(stream); }
                }
            }
        }
        finally { Marshal.FinalReleaseComObject(instance); }
    }

    // The same dynamic call is exercised with a managed fake voice, without
    // creating a COM engine or producing sound in the regression tests.
    internal static void SpeakWindowsText(object voice, string text, int volume = 100)
    {
        if (volume is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(volume));
        if (volume == 0) return;
        ((dynamic)voice).Volume = volume;
        ((dynamic)voice).Speak(text, WindowsSynchronousPlainTextFlags);
    }

    // SAFT22kHz16BitMono = 22; GetData returns raw PCM rather than a WAV file.
    // The managed seam also verifies plain-text flags without native playback.
    internal static byte[] SynthesizeWindowsPcm(object voice, object stream, string text)
    {
        dynamic memory = stream;
        object format = memory.Format;
        try { ((dynamic)format).Type = 22; }
        finally { if (OperatingSystem.IsWindows() && Marshal.IsComObject(format)) Marshal.ReleaseComObject(format); }
        ((dynamic)voice).AudioOutputStream = memory;
        SpeakWindowsText(voice, text, 100);
        byte[] pcm = memory.GetData();
        if (pcm.Length < 2 || (pcm.Length & 1) != 0 || pcm.Length > 22050 * 60 * 2)
            throw new InvalidDataException("Windows speech PCM bounds.");
        return pcm;
    }

    internal static byte[] CreateBoostedWindowsWave(byte[] pcm, int volume)
    {
        if (volume is <= 100 or > 400) throw new ArgumentOutOfRangeException(nameof(volume));
        if (pcm.Length < 2 || (pcm.Length & 1) != 0 || pcm.Length > 22050 * 60 * 2)
            throw new InvalidDataException("Windows speech PCM bounds.");
        var samples = new short[pcm.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2));
        var adjusted = new PlaybackVolume { Percent = volume }.Apply(samples).Samples;
        var bytes = new byte[pcm.Length];
        for (var i = 0; i < adjusted.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), adjusted.Span[i]);
        using var input = new MemoryStream(bytes, writable: false);
        return DesktopPcmOutput.ReadWindowsWave(input, samples.Length, 22050);
    }

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, nint module, uint flags);

    internal static (string Text, int Volume) ParseRequest(string line)
    {
        if (line.Length > 16 * 1024) throw new InvalidDataException();
        using var document = JsonDocument.Parse(line);
        var root = document.RootElement;
        var text = root.ValueKind == JsonValueKind.String ? root.GetString() : root.GetProperty("text").GetString();
        var volume = root.ValueKind == JsonValueKind.String ? 100 : root.GetProperty("volume").GetInt32();
        if (text is null || text.Length > 2048 || volume is < 0 or > 400) throw new InvalidDataException();
        return (text, volume);
    }

    internal static async Task<string?> ReadRequestLineAsync(TextReader reader)
    {
        var line = new StringBuilder(); var next = new char[1];
        while (await reader.ReadAsync(next.AsMemory()).ConfigureAwait(false) != 0)
        {
            if (next[0] == '\n') return line.ToString().TrimEnd('\r');
            if (line.Length >= 16 * 1024) throw new InvalidDataException("Standard speech request limit.");
            line.Append(next[0]);
        }
        return line.Length == 0 ? null : throw new EndOfStreamException("Incomplete standard speech request.");
    }

    private static void SpeakCommand(string executable, string text)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("--");
        start.ArgumentList.Add(text);
        using var process = Process.Start(start) ?? throw new IOException();
        // Child output must never be interpreted as the worker's ok/error protocol.
        var stdout = DrainAsync(process.StandardOutput.BaseStream);
        var stderr = DrainAsync(process.StandardError.BaseStream);
        try
        {
            process.WaitForExit();
            Task.WhenAll(stdout, stderr).GetAwaiter().GetResult();
            if (process.ExitCode != 0) throw new IOException();
        }
        catch { Kill(process); throw; }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                disposal = Task.Run(DisposeCoreAsync);
            }
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        try { lifetime.Cancel(); }
        finally
        {
            await serial.WaitAsync().ConfigureAwait(false);
            try { await StopWorkerAsync().ConfigureAwait(false); }
            finally { serial.Release(); lifetime.Dispose(); }
        }
    }

    private sealed class ProcessWorker : IDesktopSpeechWorker
    {
        private readonly Process process;
        internal ProcessWorker(ProcessStartInfo start)
        {
            var temporary = start.Environment["TEMP"] ?? throw new IOException("標準音声の一時保存先がありません。");
            WorldComparison.RejectLinks(temporary); Directory.CreateDirectory(temporary);
            process = NativeChildProcess.Start(start) ?? throw new IOException("標準音声ワーカーを開始できません。");
        }
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public Stream Error => process.StandardError.BaseStream;
        public bool HasExited => process.HasExited;
        public int ExitCode => process.ExitCode;
        public void Kill() => DesktopSpeech.Kill(process);
        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
        public void Dispose() => process.Dispose();
    }
}

// Small process seam for lifecycle regressions; production always owns the
// concrete same-executable child. No process or native speech runs in fake tests.
internal interface IDesktopSpeechWorker : IDisposable
{
    Stream Input { get; }
    Stream Output { get; }
    Stream Error { get; }
    bool HasExited { get; }
    int ExitCode { get; }
    void Kill();
    Task WaitForExitAsync(CancellationToken ct);
}


