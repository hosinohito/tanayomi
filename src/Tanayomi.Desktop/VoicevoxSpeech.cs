using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;

namespace Tanayomi.Desktop;

internal sealed class VoicevoxSpeech : ISpeechOutput, ISpeechPreparation, IOpenJtalkVoiceSelection
{
    private readonly IPcmOutput output;
    private readonly string? executableOverride;
    private readonly Func<ProcessStartInfo, Process>? launchWorker;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private VoicevoxVoice selected = VoicevoxCatalog.Find(3u)!;
    private Process? worker;
    private uint? preparedStyle;
    private bool workerStopping;
    private int disposed;
    private Task? disposal;

    internal VoicevoxSpeech(IPcmOutput output, string? executableOverride = null,
        Func<ProcessStartInfo, Process>? launchWorker = null)
    {
        this.output = output ?? throw new ArgumentNullException(nameof(output));
        this.executableOverride = executableOverride is null ? null : Path.GetFullPath(executableOverride);
        this.launchWorker = launchWorker;
        lifetimeToken = lifetime.Token;
    }

    public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } = Array.AsReadOnly(VoicevoxCatalog.Voices
        .Select(voice => new OpenJtalkVoiceChoice(voice.Id, voice.Name, voice.Credit, voice.Usage, voice.TermsUrl))
        .ToArray());

    public string VoiceId
    {
        get => Volatile.Read(ref selected).Id;
        set => Volatile.Write(ref selected, VoicevoxCatalog.Find(value) ?? throw new ArgumentOutOfRangeException(nameof(value)));
    }

    public string Name => Volatile.Read(ref selected).Name;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        _ = await ProcessAsync(text, play: true, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PcmAudio> SynthesizeAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        return (await ProcessAsync(text, play: false, cancellationToken).ConfigureAwait(false))!;
    }

    public async Task PrepareAsync(CancellationToken cancellationToken)
    {
        _ = await ProcessAsync(null, play: false, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PcmAudio?> ProcessAsync(string? text, bool play, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        var voice = Volatile.Read(ref selected);
        var request = VoicevoxProtocol.Encode(voice, text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetimeToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var ct = deadline.Token;
        var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            ct.ThrowIfCancellationRequested();
            if (workerStopping || worker is null || worker.HasExited)
            {
                await StopWorkerAsync().ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                StartWorker();
            }
            if (text is null && preparedStyle == voice.StyleId)
            {
                ct.ThrowIfCancellationRequested();
                return null;
            }
            var active = worker!;
            PcmAudio? audio;
            using (ct.Register(() => Kill(active)))
            {
                var input = active.StandardInput.BaseStream;
                await input.WriteAsync(request, ct).ConfigureAwait(false);
                await input.FlushAsync(ct).ConfigureAwait(false);
                audio = await VoicevoxProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, text is null, ct)
                    .ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
            }
            preparedStyle = voice.StyleId;
            if (play)
            {
                var playback = output.PlayAsync(audio!.Samples, audio.SampleRate, ct);
                _ = playback.ContinueWith(task => { _ = task.Exception; }, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                await playback.WaitAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            return audio;
        }
        catch (Exception error)
        {
            IOException? stopFailure = null;
            if (entered)
            {
                try { await StopWorkerAsync().ConfigureAwait(false); }
                catch (IOException failure) { stopFailure = failure; }
            }
            cancellationToken.ThrowIfCancellationRequested();
            lifetimeToken.ThrowIfCancellationRequested();
            if (deadline.IsCancellationRequested) throw new TimeoutException("VOICEVOX音声の処理が時間内に完了しませんでした。");
            if (stopFailure is not null) throw stopFailure;
            if (error is VoicevoxWorkerException) throw;
            throw new IOException("VOICEVOX音声の処理に失敗しました。次の発言で再試行できます。");
        }
        finally { if (entered) serial.Release(); }
    }

    private void StartWorker()
    {
        var executable = executableOverride ?? Environment.ProcessPath ?? throw new IOException("実行ファイルを取得できません。");
        var baseDirectory = executableOverride is null ? AppContext.BaseDirectory : Path.GetDirectoryName(executable)!;
        var package = Path.Combine(baseDirectory, "resources", "voicevox");
        if (!VoicevoxPcmWorker.IsSupported || !VoicevoxPcmWorker.HasPackageAt(package))
            throw new IOException("この環境ではVOICEVOX音声を利用できません。");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = baseDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(VoicevoxPcmWorker.WorkerArgument);
        var temporary = Path.Combine(baseDirectory, "data", "tmp");
        WorldComparison.RejectLinks(temporary);
        Directory.CreateDirectory(temporary);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "DOTNET_ROOT", "DOTNET_ROOT_X64" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value)
                start.Environment[name] = value;
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" }) start.Environment[name] = temporary;
        start.Environment["RUST_LOG"] = "off";
        start.Environment["RUST_BACKTRACE"] = "0";
        worker = NativeChildProcess.Start(start, launchWorker);
        _ = DrainAsync(worker.StandardError.BaseStream);
    }

    private static async Task DrainAsync(Stream input)
    {
        try
        {
            var buffer = new byte[1024];
            while (await input.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private static void Kill(Process active)
    {
        try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or Win32Exception) { }
    }

    private async Task StopWorkerAsync()
    {
        preparedStyle = null;
        if (worker is not { } active) return;
        workerStopping = true;
        Kill(active);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            active.Dispose();
            worker = null;
            workerStopping = false;
        }
        catch (Exception error) when (error is OperationCanceledException or InvalidOperationException or Win32Exception)
        {
            throw new IOException("VOICEVOX音声ワーカーの終了を確認できませんでした。");
        }
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
