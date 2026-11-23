using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

/// <summary>Isolated product-resource context planner. Registration and use by
/// UTAU are separate decisions; this client never loads a native library.</summary>
internal sealed class DesktopOpenJtalkPlanner : IAsyncDisposable
{
    private readonly Func<string, CancellationToken, Task<OpenJtalkPlan>> exchange;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Process? worker;
    private bool workerStopping;
    private int disposed;
    private Task? disposal;

    internal DesktopOpenJtalkPlanner()
    {
        lifetimeToken = lifetime.Token;
        exchange = ExchangeAsync;
    }
    internal DesktopOpenJtalkPlanner(Func<string, CancellationToken, Task<OpenJtalkPlan>> exchange)
        : this() => this.exchange = exchange ?? throw new ArgumentNullException(nameof(exchange));

    internal async Task<OpenJtalkPlan> PlanAsync(string text, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        _ = OpenJtalkPcmProtocol.ValidateRequestText(text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token; var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); entered = true;
            ct.ThrowIfCancellationRequested();
            var pending = exchange(text, ct);
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var result = await pending.WaitAsync(ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            return OpenJtalkPlanProtocol.Validate(result);
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo? cleanup = null;
            if (entered)
            {
                try
                {
                    if (workerStopping) throw new IOException("文脈計画ワーカーの終了を確認できませんでした。");
                    await StopWorkerAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError) { cleanup = ExceptionDispatchInfo.Capture(cleanupError); }
            }
            cancellationToken.ThrowIfCancellationRequested(); lifetimeToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("文脈計画の処理が時間内に完了しませんでした。");
            cleanup?.Throw();
            if (error is OpenJtalkWorkerException) throw;
            throw new IOException("文脈計画を取得できませんでした。次の発言で再試行できます。");
        }
        finally { if (entered) serial.Release(); }
    }

    private async Task<OpenJtalkPlan> ExchangeAsync(string text, CancellationToken ct)
    {
        if (workerStopping || worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        var active = worker!;
        using var registration = ct.Register(() => Kill(active));
        var request = OpenJtalkPlanProtocol.EncodeRequest(text);
        await active.StandardInput.BaseStream.WriteAsync(request, ct).ConfigureAwait(false);
        await active.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        return await OpenJtalkPlanProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, ct).ConfigureAwait(false);
    }
    private async Task StartWorkerAsync(CancellationToken ct)
    {
        await StopWorkerAsync().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("文脈計画はこの環境では利用できません。");
        var executable = Environment.ProcessPath ?? throw new IOException("実行ファイルを取得できません。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--open-jtalk-plan-worker");
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        WorldComparison.RejectLinks(temporary); Directory.CreateDirectory(temporary);
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
        worker = NativeChildProcess.Start(start) ?? throw new IOException("文脈計画ワーカーを開始できません。");
        _ = DrainAsync(worker.StandardError.BaseStream);
    }
    private static async Task DrainAsync(Stream input)
    {
        try { var buffer = new byte[512]; while (await input.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private async Task StopWorkerAsync()
    {
        if (worker is not { } active) return;
        workerStopping = true; Kill(active);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await active.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
            if (!active.HasExited) throw new IOException();
            active.Dispose(); worker = null; workerStopping = false;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { throw new IOException("文脈計画ワーカーの終了を確認できませんでした。"); }
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
}

