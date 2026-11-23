using System.Diagnostics;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

/// <summary>Isolated NIT/Open JTalk speech. The PCM output is borrowed; its
/// owner must outlive this adapter. No native code is loaded in this process.</summary>
public sealed class DesktopOpenJtalkSpeech : ISpeechOutput, ISpeechPreparation
{
    private readonly IPcmOutput output;
    private readonly string voiceId;
    private readonly Func<string, CancellationToken, Task<PcmAudio>> synthesize;
    private readonly Func<CancellationToken, Task> prepare;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Process? worker;
    private object? workerGeneration;
    private bool workerStopping, workerReady;
    private int disposed;
    private Task? disposal;
    // A successful ACK and a live private worker are both required. Process
    // exit can race this hint; Exchange still performs its ordinary restart checks.
    internal bool IsPrepared
    {
        get
        {
            if (Volatile.Read(ref disposed) != 0 || workerStopping || !workerReady) return false;
            try { return worker is { HasExited: false }; }
            catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { return false; }
        }
    }
    // Identity belongs to one successfully started private process, not its PID.
    // A stopped/restarted or not-yet-ready process cannot authorize reuse.
    internal object? PreparedGeneration
    {
        get
        {
            var generation = Volatile.Read(ref workerGeneration);
            return generation is not null && IsPrepared &&
                ReferenceEquals(generation, Volatile.Read(ref workerGeneration)) ? generation : null;
        }
    }
    internal string VoiceId => voiceId;
    public string Name { get; }

    public DesktopOpenJtalkSpeech(IPcmOutput output) : this(output, OpenJtalkPcmWorker.DefaultVoiceId) { }

    internal DesktopOpenJtalkSpeech(IPcmOutput output, string voiceId)
    {
        Name = voiceId == OpenJtalkPcmWorker.DefaultVoiceId ? "NIT日本語 (Open JTalk)" :
            OpenJtalkPcmWorker.FindModel(voiceId)?.Name ?? throw new ArgumentOutOfRangeException(nameof(voiceId));
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.voiceId = voiceId;
        lifetimeToken = lifetime.Token;
        synthesize = ExchangeAsync; prepare = ExchangePreparationAsync;
    }
    internal DesktopOpenJtalkSpeech(IPcmOutput output, Func<string, CancellationToken, Task<PcmAudio>> synthesize,
        Func<CancellationToken, Task>? prepare = null, string voiceId = OpenJtalkPcmWorker.DefaultVoiceId) : this(output, voiceId)
    {
        this.synthesize = synthesize ?? throw new ArgumentNullException(nameof(synthesize));
        this.prepare = prepare ?? (_ => Task.CompletedTask);
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken) =>
        _ = await ProcessAsync(text, cancellationToken, play: true).ConfigureAwait(false);

    internal Task<PcmAudio> SynthesizeAsync(string text, CancellationToken cancellationToken) =>
        ProcessAsync(text, cancellationToken, play: false);

    internal Task<PcmAudio> SynthesizeAsync(string text, bool drawnOutMode, CancellationToken cancellationToken) =>
        ProcessAsync(text, cancellationToken, play: false, drawnOutMode);

    Task ISpeechPreparation.PrepareAsync(CancellationToken ct) => PrepareAsync(ct);

    internal async Task PrepareAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); var ct = deadline.Token; var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); entered = true; ct.ThrowIfCancellationRequested();
            var pending = prepare(ct);
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            await pending.WaitAsync(ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo? cleanup = null;
            if (entered)
            {
                try
                {
                    if (workerStopping) throw new IOException("NIT音声ワーカーの終了を確認できませんでした。");
                    await StopWorkerAsync().ConfigureAwait(false);
                }
                catch (Exception failure) { cleanup = ExceptionDispatchInfo.Capture(failure); }
            }
            cancellationToken.ThrowIfCancellationRequested(); lifetimeToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("NIT音声の準備が時間内に完了しませんでした。");
            cleanup?.Throw();
            if (error is OpenJtalkWorkerException) throw;
            throw new IOException("NIT音声の準備に失敗しました。読み上げ時に再試行します。");
        }
        finally { if (entered) serial.Release(); }
    }

    private async Task<PcmAudio> ProcessAsync(string text, CancellationToken cancellationToken, bool play, bool drawnOutMode = false)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        // Validate before starting any child or allocating an unbounded body.
        _ = OpenJtalkPcmProtocol.ValidateRequestText(text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); entered = true;
            ct.ThrowIfCancellationRequested();
            var pending = drawnOutMode ? ExchangeAsync(text, ct, drawnOutMode: true) : synthesize(text, ct);
            // Observe even a late fault from a trusted injected transport.
            _ = pending.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            var audio = await pending.WaitAsync(ct).ConfigureAwait(false);
            OpenJtalkPcmProtocol.ValidateAudio(audio);
            ct.ThrowIfCancellationRequested();
            if (play)
            {
                var playback = output.PlayAsync(audio.Samples, audio.SampleRate, ct);
                _ = playback.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await playback.WaitAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return audio;
        }
        catch (Exception error)
        {
            ExceptionDispatchInfo? cleanup = null;
            if (entered)
            {
                try
                {
                    if (workerStopping) throw new IOException("NIT音声ワーカーの終了を確認できませんでした。");
                    await StopWorkerAsync().ConfigureAwait(false);
                }
                catch (Exception cleanupError) { cleanup = ExceptionDispatchInfo.Capture(cleanupError); }
            }
            // Explicit stop/disposal must never trigger fallback speech.
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("NIT音声の処理が時間内に完了しませんでした。");
            cleanup?.Throw();
            if (error is OpenJtalkWorkerException) throw;
            throw new IOException("NIT音声の処理に失敗しました。次の発言で再試行できます。");
        }
        finally { if (entered) serial.Release(); }
    }

    private async Task ExchangePreparationAsync(CancellationToken ct)
    {
        if (workerStopping || worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        if (workerReady) return;
        var active = worker!;
        using var registration = ct.Register(() => Kill(active));
        await active.StandardInput.BaseStream.WriteAsync(OpenJtalkPcmProtocol.EncodePreparation(), ct).ConfigureAwait(false);
        await active.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        await OpenJtalkPcmProtocol.ReadPreparationResponseAsync(active.StandardOutput.BaseStream, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); workerReady = true;
    }

    private Task<PcmAudio> ExchangeAsync(string text, CancellationToken ct) => ExchangeAsync(text, ct, false);
    private async Task<PcmAudio> ExchangeAsync(string text, CancellationToken ct, bool drawnOutMode)
    {
        if (workerStopping || worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        var active = worker!;
        using var registration = ct.Register(() => Kill(active));
        var request = OpenJtalkPcmProtocol.EncodeRequest(text, drawnOutMode);
        await active.StandardInput.BaseStream.WriteAsync(request, ct).ConfigureAwait(false);
        await active.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        var audio = await OpenJtalkPcmProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); workerReady = true; return audio;
    }

    private async Task StartWorkerAsync(CancellationToken ct)
    {
        await StopWorkerAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("NIT音声はこの環境では利用できません。");
        var executable = Environment.ProcessPath ?? throw new IOException("実行ファイルを取得できません。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--open-jtalk-pcm-worker");
        if (voiceId != OpenJtalkPcmWorker.DefaultVoiceId) start.ArgumentList.Add(voiceId);
        var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
        WorldComparison.RejectLinks(temporary);
        Directory.CreateDirectory(temporary);
        start.Environment["TEMP"] = temporary; start.Environment["TMP"] = temporary; start.Environment["TMPDIR"] = temporary;
        worker = NativeChildProcess.Start(start) ?? throw new IOException("NIT音声ワーカーを開始できません。");
        Volatile.Write(ref workerGeneration, new object());
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
        Volatile.Write(ref workerGeneration, null);
        workerReady = false;
        if (worker is not { } active) return;
        workerStopping = true; Kill(active);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            if (!active.HasExited) throw new IOException();
            active.Dispose(); worker = null; workerStopping = false;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        { throw new IOException("NIT音声ワーカーの終了を確認できませんでした。"); }
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
        // The borrowed PCM output is intentionally not disposed here.
    }
}

internal sealed class DesktopOpenJtalkVoiceSelection : ISpeechOutput, ISpeechPreparation, IOpenJtalkVoiceSelection
{
    private readonly DesktopOpenJtalkSpeech nit;
    private readonly IReadOnlyList<DesktopOpenJtalkSpeech> voices;
    private readonly object disposalGate = new();
    private DesktopOpenJtalkSpeech selectedVoice;
    private Task? disposal;

    internal DesktopOpenJtalkVoiceSelection(DesktopOpenJtalkSpeech nit, params DesktopOpenJtalkSpeech[] tohoku)
    {
        if (nit.VoiceId != OpenJtalkPcmWorker.DefaultVoiceId ||
            tohoku.Any(voice => voice.VoiceId == OpenJtalkPcmWorker.DefaultVoiceId) ||
            tohoku.Select(voice => voice.VoiceId).Distinct(StringComparer.Ordinal).Count() != tohoku.Length)
            throw new ArgumentException("Open JTalkの音声構成が不正です。", nameof(tohoku));
        this.nit = selectedVoice = nit;
        voices = [nit, .. tohoku];
        Voices = Array.AsReadOnly(voices.Select(voice => Choice(voice.VoiceId, voice.Name)).ToArray());
    }

    private static OpenJtalkVoiceChoice Choice(string id, string name) => id.StartsWith("mei-", StringComparison.Ordinal)
        ? new(id, name, "HTS Voice \"Mei\" (c) 2009-2018 Nagoya Institute of Technology / MMDAgent Project Team, CC BY 3.0",
            "音声を公開する際は配信概要等にクレジットと許諾リンクを表示してください。加工した音声も同じ許諾条件で配布してください。",
            "https://creativecommons.org/licenses/by/3.0/")
        : id.StartsWith("takumi-", StringComparison.Ordinal)
        ? new(id, name, "HTS Voice \"Takumi\" (c) 2017-2018 Nagoya Institute of Technology / MMDAgent Project Team, CC BY 3.0",
            "音声を公開する際は配信概要等にクレジットと許諾リンクを表示してください。加工した音声も同じ許諾条件で配布してください。",
            "https://creativecommons.org/licenses/by/3.0/")
        : new(id, name);

    public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; }
    public string VoiceId
    {
        get => SelectedVoice.VoiceId;
        set => Volatile.Write(ref selectedVoice, voices.FirstOrDefault(voice => voice.VoiceId == value) ?? nit);
    }

    private DesktopOpenJtalkSpeech SelectedVoice => Volatile.Read(ref selectedVoice);
    public string Name => SelectedVoice.Name;
    public Task SpeakAsync(string text, CancellationToken cancellationToken) => SelectedVoice.SpeakAsync(text, cancellationToken);
    public Task PrepareAsync(CancellationToken cancellationToken) => SelectedVoice.PrepareAsync(cancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (disposalGate) return new ValueTask(disposal ??= DisposeVoicesAsync());
    }

    private async Task DisposeVoicesAsync()
    {
        if (voices.Count == 1) { await nit.DisposeAsync().ConfigureAwait(false); return; }
        var all = Task.WhenAll(voices.Select(voice => voice.DisposeAsync().AsTask()));
        try { await all.ConfigureAwait(false); }
        catch when (all.Exception is { } failures) { throw failures; }
    }
}

