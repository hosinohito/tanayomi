using System.Buffers.Binary;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

/// <summary>Native audio runs outside the UI process, so cancellation can terminate a hung driver.</summary>
public sealed class DesktopPcmOutput : IPcmOutput
{
    private const int MaxSeconds = 60;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private readonly Func<ProcessStartInfo, IWorker> startWorker;
    private readonly TimeSpan stopTimeout;
    private IWorker? worker;
    private bool workerStopping;
    private Task? disposal;
    private int disposed;
    // Used only inside the isolated Linux worker after failed child cleanup.
    private static Process? unreapedLinuxPlayer;

    public DesktopPcmOutput() : this(start => new ProcessWorker(start)) { }

    internal DesktopPcmOutput(Func<ProcessStartInfo, IWorker> startWorker, TimeSpan? stopTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(startWorker);
        this.startWorker = startWorker;
        lifetimeToken = lifetime.Token;
        this.stopTimeout = stopTimeout ?? TimeSpan.FromSeconds(3);
        if (this.stopTimeout <= TimeSpan.Zero || this.stopTimeout > TimeSpan.FromSeconds(3))
            throw new ArgumentOutOfRangeException(nameof(stopTimeout));
    }

    internal interface IWorker : IDisposable
    {
        Stream Input { get; }
        Stream Output { get; }
        Stream Error { get; }
        bool HasExited { get; }
        void Kill();
        Task WaitForExitAsync(CancellationToken ct);
    }

    private sealed class ProcessWorker : IWorker
    {
        private readonly Process process;
        public ProcessWorker(ProcessStartInfo start) => process = NativeChildProcess.Start(start) ?? throw new IOException("PCM再生ワーカーを起動できませんでした。");
        public Stream Input => process.StandardInput.BaseStream;
        public Stream Output => process.StandardOutput.BaseStream;
        public Stream Error => process.StandardError.BaseStream;
        public bool HasExited => process.HasExited;
        public void Kill() => process.Kill(entireProcessTree: true);
        public Task WaitForExitAsync(CancellationToken ct) => process.WaitForExitAsync(ct);
        public void Dispose() => process.Dispose();
    }

    public async Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        Validate(samples.Length, sampleRate);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        timeout.CancelAfter(TimeSpan.FromSeconds((double)samples.Length / sampleRate + 5));
        var token = timeout.Token;
        var entered = false;
        try
        {
            await serial.WaitAsync(token).ConfigureAwait(false);
            entered = true;
            token.ThrowIfCancellationRequested();
            if (worker is not null && (workerStopping || worker.HasExited)) await StopWorkerAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (worker is null) StartWorker();
            var active = worker!;
            using var registration = token.Register(() => RequestKill(active));
            var bytes = new byte[checked(8 + samples.Length * 2)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, sampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), samples.Length);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(samples.Span).CopyTo(bytes.AsSpan(8));
            else
                for (var i = 0; i < samples.Length; i++)
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(8 + i * 2), samples.Span[i]);
            await active.Input.WriteAsync(bytes, token).ConfigureAwait(false);
            await active.Input.FlushAsync(token).ConfigureAwait(false);
            var response = await ReadReplyAsync(active.Output, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (response != "ok")
                throw new IOException(response == "unavailable" && OperatingSystem.IsLinux()
                    ? "PCM再生にはLinux側のaplayが必要です。" : "PCM再生ワーカーで音声出力に失敗しました。");
        }
        catch
        {
            Exception? cleanup = null;
            // A previous failed reap already performed its bounded wait. Keep
            // that child rather than launching a replacement or waiting twice.
            if (entered && worker is not null && !workerStopping)
                try { await StopWorkerAsync().ConfigureAwait(false); }
                catch (IOException error) { cleanup = error; }
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("PCM音声出力が時間内に完了しませんでした。");
            if (cleanup is not null) throw cleanup;
            throw;
        }
        finally { if (entered) serial.Release(); }
    }

    private static void Validate(int count, int rate)
    {
        if (rate is < 8000 or > 192000 || count < 1 || count > rate * MaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(count), "PCMは8–192kHz、1サンプル以上60秒以下のモノラル音声に限ります。");
    }

    private void StartWorker()
    {
        if (worker is not null) throw new IOException("前のPCM再生ワーカーの終了を確認できませんでした。");
        var executable = Environment.ProcessPath ?? throw new IOException("Executable unavailable.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--pcm-worker");
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
        worker = startWorker(start) ?? throw new IOException("PCM再生ワーカーを起動できませんでした。");
        _ = DrainAsync(worker.Error);
    }

    internal static async Task DrainAsync(Stream stream)
    {
        var buffer = new byte[512];
        try { while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException or OperationCanceledException) { }
    }

    private static async Task<string> ReadReplyAsync(Stream stream, CancellationToken ct)
    {
        var buffer = new byte[256]; var one = new byte[1]; var count = 0;
        while (count < buffer.Length)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0 || one[0] > 127)
                throw new IOException("PCM再生ワーカーの応答を確認できませんでした。");
            if (one[0] == '\n')
            {
                if (count > 0 && buffer[count - 1] == '\r') count--;
                return System.Text.Encoding.ASCII.GetString(buffer, 0, count);
            }
            buffer[count++] = one[0];
        }
        throw new IOException("PCM再生ワーカーの応答が長すぎます。");
    }

    private static void RequestKill(IWorker process)
    {
        // The cancellation callback only requests termination. Async cleanup
        // confirms exit; exceptions here must not escape CancellationToken.Cancel.
        try { if (!process.HasExited) process.Kill(); }
        catch (Exception) { }
    }

    private async Task StopWorkerAsync()
    {
        if (worker is not { } active) return;
        workerStopping = true;
        RequestKill(active);
        try
        {
            using var deadline = new CancellationTokenSource(stopTimeout);
            await active.WaitForExitAsync(deadline.Token).WaitAsync(stopTimeout).ConfigureAwait(false);
            if (!active.HasExited) throw new IOException();
            active.Dispose(); worker = null; workerStopping = false;
        }
        catch (Exception) { throw new IOException("PCM再生ワーカーの終了を確認できませんでした。再生を再開できません。"); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    internal static int RunWorker()
    {
        using var input = Console.OpenStandardInput();
        var header = new byte[8];
        while (true)
        {
            var first = input.ReadByte();
            if (first < 0) return 0;
            header[0] = (byte)first;
            try
            {
                input.ReadExactly(header.AsSpan(1));
                var rate = BinaryPrimitives.ReadInt32LittleEndian(header);
                var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
                Validate(count, rate);
                if (OperatingSystem.IsWindows())
                {
                    if (!PlaySound(ReadWindowsWave(input, count, rate), 0, 0x0004 | 0x0002))
                        throw new IOException("Windows audio output failed.");
                }
                else
                {
                    var pcm = new byte[checked(count * 2)];
                    input.ReadExactly(pcm);
                    if (OperatingSystem.IsLinux()) PlayLinux(pcm, rate);
                    else if (OperatingSystem.IsMacOS()) PlayMac(pcm, rate);
                    else throw new PlatformNotSupportedException();
                }
                Console.WriteLine("ok");
                Console.Out.Flush();
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex is System.ComponentModel.Win32Exception { NativeErrorCode: 2 }
                    ? "unavailable" : $"error:{ex.GetType().Name}:{ex.HResult:X8}");
                Console.Out.Flush();
                // A bad/truncated frame cannot be resynchronized safely.
                if (unreapedLinuxPlayer is not null)
                {
                    // Keep aplay parented until the controller handles this
                    // failure and kills our own tree. A failed reply always
                    // enters the controller's explicit kill + bounded reap;
                    // its playback deadline also covers reply validation.
                    try { while (input.ReadByte() >= 0) { } }
                    catch (IOException) { }
                }
                return 1;
            }
        }
    }

    internal static byte[] ReadWindowsWave(Stream input, int count, int rate)
    {
        Validate(count, rate);
        var length = checked(count * 2);
        var wave = new byte[checked(44 + length)];
        "RIFF"u8.CopyTo(wave);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(4), 36 + length);
        "WAVEfmt "u8.CopyTo(wave.AsSpan(8));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(16), 16);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(20), 1);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(22), 1);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(24), rate);
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(28), rate * 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(32), 2);
        BinaryPrimitives.WriteInt16LittleEndian(wave.AsSpan(34), 16);
        "data"u8.CopyTo(wave.AsSpan(36));
        BinaryPrimitives.WriteInt32LittleEndian(wave.AsSpan(40), length);
        input.ReadExactly(wave.AsSpan(44));
        return wave;
    }

    private static void PlayLinux(byte[] pcm, int rate)
    {
        var start = new ProcessStartInfo("aplay")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { "--quiet", "--file-type", "raw", "--format", "S16_LE", "--channels", "1", "--rate", rate.ToString(System.Globalization.CultureInfo.InvariantCulture) })
            start.ArgumentList.Add(arg);
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
        var process = Process.Start(start) ?? throw new IOException("aplay could not start.");
        _ = DrainAsync(process.StandardOutput.BaseStream);
        _ = DrainAsync(process.StandardError.BaseStream);
        try
        {
            process.StandardInput.BaseStream.Write(pcm);
            process.StandardInput.Close();
            process.WaitForExit();
            if (process.ExitCode != 0) throw new IOException("aplay failed.");
        }
        catch
        {
            // Dispose closes handles but does not stop an output process which failed
            // during transfer. Do not let partial audio continue into OS fallback.
            Kill(process);
            var exited = false;
            try { exited = process.WaitForExit(3000); }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
            if (!exited)
            {
                unreapedLinuxPlayer = process;
                throw new IOException("aplayの終了を確認できませんでした。");
            }
            throw;
        }
        finally { if (!ReferenceEquals(unreapedLinuxPlayer, process)) process.Dispose(); }
    }

    private static void PlayMac(byte[] pcm, int rate)
    {
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        AudioQueueCallback callback = (_, _, _) => completed.TrySetResult();
        var format = new AudioFormat
        {
            SampleRate = rate, FormatId = 0x6c70636d, FormatFlags = 0x0c,
            BytesPerPacket = 2, FramesPerPacket = 1, BytesPerFrame = 2, ChannelsPerFrame = 1, BitsPerChannel = 16
        };
        CheckAudio(AudioQueueNewOutput(ref format, callback, 0, 0, 0, 0, out var queue));
        try
        {
            CheckAudio(AudioQueueAllocateBuffer(queue, (uint)pcm.Length, out var buffer));
            var info = Marshal.PtrToStructure<AudioBuffer>(buffer);
            Marshal.Copy(pcm, 0, info.AudioData, pcm.Length);
            Marshal.WriteInt32(buffer, (int)Marshal.OffsetOf<AudioBuffer>(nameof(AudioBuffer.AudioDataByteSize)), pcm.Length);
            CheckAudio(AudioQueueEnqueueBuffer(queue, buffer, 0, 0));
            var deadline = Stopwatch.StartNew();
            var maxDuration = TimeSpan.FromSeconds((double)pcm.Length / 2 / rate + 3);
            CheckAudio(AudioQueueStart(queue, 0));
            completed.Task.WaitAsync(maxDuration).GetAwaiter().GetResult();
            CheckAudio(AudioQueueStop(queue, false));
            // Stop(false) drains the audio already queued to the output device.
            uint running;
            do
            {
                uint size = 4;
                CheckAudio(AudioQueueGetProperty(queue, 0x6171726e, out running, ref size));
                if (running != 0) Thread.Sleep(5);
                if (deadline.Elapsed > maxDuration) throw new TimeoutException("AudioQueue did not stop.");
            } while (running != 0);
        }
        finally
        {
            var released = AudioQueueDispose(queue, true);
            GC.KeepAlive(callback);
            CheckAudio(released);
        }
    }

    private static void CheckAudio(int status)
    { if (status != 0) throw new ExternalException("AudioQueue output failed.", status); }

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

    [DllImport("winmm.dll", EntryPoint = "PlaySoundW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(byte[] sound, nint module, uint flags);

    private const string AudioToolbox = "/System/Library/Frameworks/AudioToolbox.framework/AudioToolbox";
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioFormat
    {
        public double SampleRate;
        public uint FormatId, FormatFlags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct AudioBuffer
    { public uint AudioDataBytesCapacity; public nint AudioData; public uint AudioDataByteSize; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void AudioQueueCallback(nint userData, nint queue, nint buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueNewOutput(ref AudioFormat format, AudioQueueCallback callback, nint userData, nint runLoop, nint mode, uint flags, out nint queue);
    [DllImport(AudioToolbox)] private static extern int AudioQueueAllocateBuffer(nint queue, uint size, out nint buffer);
    [DllImport(AudioToolbox)] private static extern int AudioQueueEnqueueBuffer(nint queue, nint buffer, uint count, nint descriptions);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStart(nint queue, nint startTime);
    [DllImport(AudioToolbox)] private static extern int AudioQueueStop(nint queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
    [DllImport(AudioToolbox)] private static extern int AudioQueueGetProperty(nint queue, uint property, out uint value, ref uint size);
    [DllImport(AudioToolbox)] private static extern int AudioQueueDispose(nint queue, [MarshalAs(UnmanagedType.I1)] bool immediate);
}

