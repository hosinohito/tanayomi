using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Optional isolated WORLD postprocessing. Registration is separate from
/// implementation so an unaudited native runtime is never enabled implicitly.</summary>
public sealed class DesktopWorldPostprocessor : IUtauPcmPostprocessor
{
    private const int Rate = 44100, MaximumSamples = Rate * 45;
    private readonly Func<Voicebank, CancellationToken, Task<PcmAudio>> referenceFactory;
    private readonly Func<PcmAudio, double, CancellationToken, Task<(PcmAudio? Audio, double F0)>> exchange;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private readonly List<(string Key, double F0)> bases = [];
    private Process? worker;
    private bool workerStopping;
    private Task? disposal;
    private int disposed;

    private readonly bool fastAnalysis;
    public DesktopWorldPostprocessor(Func<Voicebank, CancellationToken, Task<PcmAudio>> referenceFactory, bool fastAnalysis = false)
    {
        this.referenceFactory = referenceFactory ?? throw new ArgumentNullException(nameof(referenceFactory));
        this.fastAnalysis = fastAnalysis;
        exchange = ExchangeAsync;
    }

    internal DesktopWorldPostprocessor(Func<Voicebank, CancellationToken, Task<PcmAudio>> referenceFactory,
        Func<PcmAudio, double, CancellationToken, Task<(PcmAudio? Audio, double F0)>> exchange)
        : this(referenceFactory) => this.exchange = exchange;

    public async Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken cancellationToken)
    {
        Validate(input);
        return await RunAsync(input, bank, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("音声処理の応答が不正です。");
    }

    public async Task PrepareAsync(Voicebank bank, CancellationToken cancellationToken)
    {
        _ = await RunAsync(null, bank, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PcmAudio> ProcessContourAsync(PcmAudio input, Voicebank bank,
        double[] contour, CancellationToken cancellationToken)
    {
        Validate(input);
        ArgumentNullException.ThrowIfNull(contour);
        // Validate and snapshot before crossing the asynchronous ownership boundary.
        WorldPcmProtocol.ValidateContour(input.Samples.Length, 200, contour);
        var snapshot = (double[])contour.Clone();
        return await RunAsync(input, bank, cancellationToken, snapshot).ConfigureAwait(false)
            ?? throw new InvalidDataException("音声処理の応答が不正です。");
    }

    private async Task<PcmAudio?> RunAsync(PcmAudio? input, Voicebank bank, CancellationToken cancellationToken,
        double[]? contour = null)
    {
        ArgumentNullException.ThrowIfNull(bank);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = timeout.Token;
        await serial.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            var key = BankKey(bank, ct);
            var index = bases.FindIndex(x => x.Key == key);
            double f0;
            if (index >= 0)
            {
                var entry = bases[index]; bases.RemoveAt(index); bases.Add(entry); f0 = entry.F0;
            }
            else
            {
                var reference = await referenceFactory(bank, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                Validate(reference);
                var measured = await exchange(reference, 0, ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                if (measured.Audio is not null || !ValidF0(measured.F0)) throw new InvalidDataException("音源の基準音程を取得できませんでした。");
                // Do not cache a measurement if files changed during calibration.
                if (BankKey(bank, ct) != key) throw new IOException("音源が処理中に変更されました。");
                ct.ThrowIfCancellationRequested();
                f0 = measured.F0;
                if (bases.Count == 8) bases.RemoveAt(0);
                bases.Add((key, f0));
            }
            if (input is null) return null; // Warmup calibrates only; never resynthesize an utterance.
            var result = contour is null
                ? await exchange(input, f0, ct).ConfigureAwait(false)
                : await ExchangeAsync(input, f0, ct, contour).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (result.Audio is not { } audio || audio.SampleRate != input.SampleRate || audio.Samples.Length != input.Samples.Length)
                throw new InvalidDataException("音声処理の応答が不正です。");
            return audio;
        }
        catch
        {
            // A failed StartWorkerAsync has already tried to reap the retained
            // process. Do not double its bounded wait in this same request.
            ExceptionDispatchInfo? cleanupFailure = null;
            try
            {
                if (workerStopping) throw new IOException("音程処理ワーカーの終了を確認できませんでした。");
                await StopWorkerAsync().ConfigureAwait(false);
            }
            catch (Exception error) { cleanupFailure = ExceptionDispatchInfo.Capture(error); }
            // Cancellation takes priority over cleanup failure so an explicit
            // stop never starts fallback speech. Ownership of an unreaped worker
            // remains intact and DisposeAsync can still report that failure.
            cancellationToken.ThrowIfCancellationRequested();
            lifetime.Token.ThrowIfCancellationRequested();
            if (timeout.IsCancellationRequested) throw new TimeoutException("音程処理が時間内に完了しませんでした。");
            cleanupFailure?.Throw();
            throw;
        }
        finally { serial.Release(); }
    }

    private static bool ValidF0(double value) => double.IsFinite(value) && value is >= 40 and <= 1200;
    private static void Validate(PcmAudio audio)
    {
        if (audio.SampleRate != Rate || audio.Samples.Length is < Rate / 10 or > MaximumSamples)
            throw new InvalidDataException("音程処理のPCM形式または長さが不正です。");
    }

    internal static string BankKey(Voicebank bank, CancellationToken ct)
    {
        if (bank.Entries.Count < 1) throw new InvalidDataException("音源の項目数が不正です。");
        using var data = new MemoryStream();
        using var writer = new BinaryWriter(data, Encoding.UTF8, leaveOpen: true);
        writer.Write("world-reference-v1"); writer.Write(Path.GetFullPath(bank.Directory));
        var oto = new FileInfo(Path.Combine(bank.Directory, "oto.ini"));
        writer.Write(oto.Exists);
        if (oto.Exists) { writer.Write(oto.Length); writer.Write(oto.LastWriteTimeUtc.Ticks); }
        foreach (var entry in bank.Entries)
        {
            ct.ThrowIfCancellationRequested();
            var source = new FileInfo(entry.WavePath);
            writer.Write(source.FullName); writer.Write(source.Exists);
            if (source.Exists) { writer.Write(source.Length); writer.Write(source.LastWriteTimeUtc.Ticks); }
            writer.Write(entry.Alias); writer.Write(entry.Offset); writer.Write(entry.Consonant);
            writer.Write(entry.Cutoff); writer.Write(entry.Preutterance); writer.Write(entry.Overlap);
        }
        writer.Flush();
        return Convert.ToHexString(SHA256.HashData(data.GetBuffer().AsSpan(0, checked((int)data.Length))));
    }

    private async Task<(PcmAudio? Audio, double F0)> ExchangeAsync(PcmAudio input, double target, CancellationToken ct)
        => await ExchangeAsync(input, target, ct, null).ConfigureAwait(false);

    private async Task<(PcmAudio? Audio, double F0)> ExchangeAsync(PcmAudio input, double target, CancellationToken ct,
        double[]? contour)
    {
        if (workerStopping || worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        var active = worker!;
        using var registration = ct.Register(() => Kill(active));
        var bytes = WorldPcmProtocol.EncodeRequest(input, target, contour);
        await active.StandardInput.BaseStream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await active.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        return await WorldPcmProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, input.Samples.Length, target, ct).ConfigureAwait(false);
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        await StopWorkerAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        _ = WorldNativePlatform.LibraryPath(AppContext.BaseDirectory);
        var executable = Environment.ProcessPath ?? throw new IOException("実行ファイルを取得できません。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--world-pcm-worker");
        if (fastAnalysis) start.ArgumentList.Add("--dio");
        var temp = Path.Combine(AppPaths.DataDirectory, "tmp");
        start.Environment["TEMP"] = temp; start.Environment["TMP"] = temp; start.Environment["TMPDIR"] = temp;
        worker = NativeChildProcess.Start(start) ?? throw new IOException("音程処理ワーカーを開始できません。");
        // The protocol uses binary stdout. stderr is discarded with fixed storage,
        // never ReadLine, so a malformed child cannot grow the parent's log buffer.
        _ = DrainAsync(worker.StandardError.BaseStream);
    }

    private static async Task DrainAsync(Stream stream)
    {
        try { var bytes = new byte[512]; while (await stream.ReadAsync(bytes).ConfigureAwait(false) != 0) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private async Task StopWorkerAsync()
    {
        if (worker is not { } active) return;
        workerStopping = true;
        Kill(active);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (!active.HasExited) throw new IOException();
            active.Dispose();
            worker = null;
            workerStopping = false;
        }
        catch (Exception ex) when (ex is OperationCanceledException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException)
        {
            // Retain ownership and prohibit replacement until exit is confirmed.
            // Do not expose OS exception messages or pretend cleanup succeeded.
            throw new IOException("音程処理ワーカーの終了を確認できませんでした。");
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                // Share completion, without running cancellation callbacks under
                // this lock or capturing a UI context while shutting down.
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
            try { await StopWorkerAsync().ConfigureAwait(false); bases.Clear(); }
            finally { serial.Release(); lifetime.Dispose(); }
        }
    }
}

