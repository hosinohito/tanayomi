using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Tanayomi.Desktop;

internal sealed class AivisSpeechWorker : IAsyncDisposable
{
    private readonly string packageDirectory;
    private readonly string dataDirectory;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Process? process;
    private bool processStopping;
    private Task? disposal;

    private AivisSpeechWorker(string packageDirectory, string dataDirectory, IReadOnlyList<OpenJtalkVoiceChoice> voices)
    {
        this.packageDirectory = packageDirectory;
        this.dataDirectory = dataDirectory;
        Voices = voices;
        lifetimeToken = lifetime.Token;
    }

    internal IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; }

    internal static AivisSpeechWorker? TryCreate(string baseDirectory, string dataDirectory)
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        var package = Path.GetFullPath(Path.Combine(baseDirectory, "resources", "aivis"));
        if (!Directory.Exists(package) && !File.Exists(package)) return null;
        try
        {
            foreach (var name in new[] { "python/python.exe", "worker.py", "voices.json" })
                WorldComparison.RejectLinks(Path.Combine(package, name));
            if (!File.Exists(Path.Combine(package, "python", "python.exe")) || !File.Exists(Path.Combine(package, "worker.py")))
                throw InvalidBundle();
            using var file = File.OpenRead(Path.Combine(package, "voices.json"));
            if (file.Length is <= 0 or > 65536) throw InvalidBundle();
            using var manifest = JsonDocument.Parse(file);
            var root = manifest.RootElement;
            var voices = root.GetProperty("voices");
            if (root.GetProperty("version").GetInt32() != 1 || voices.GetArrayLength() is < 1 or > 8) throw InvalidBundle();
            var choices = new List<OpenJtalkVoiceChoice>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (var voice in voices.EnumerateArray())
            {
                var id = voice.GetProperty("id").GetString();
                var name = voice.GetProperty("name").GetString();
                var model = voice.GetProperty("model").GetString();
                var credit = voice.GetProperty("credit").GetString();
                var usage = voice.GetProperty("usage").GetString();
                var terms = voice.GetProperty("termsUrl").GetString();
                if (id is null || !id.StartsWith("aivis-", StringComparison.Ordinal) || id.Length > 80 || !ids.Add(id) ||
                    string.IsNullOrWhiteSpace(name) || name.Length > 100 || !Guid.TryParseExact(model, "D", out _) ||
                    string.IsNullOrWhiteSpace(credit) || string.IsNullOrWhiteSpace(usage) || string.IsNullOrWhiteSpace(terms))
                    throw InvalidBundle();
                var modelPath = Path.Combine(package, "models", model + ".aivmx");
                WorldComparison.RejectLinks(modelPath);
                if (!File.Exists(modelPath)) throw InvalidBundle();
                choices.Add(new(id, name, credit, usage, terms));
            }
            return new(package, Path.GetFullPath(dataDirectory), choices.AsReadOnly());
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                      KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw InvalidBundle();
        }
    }

    private static IOException InvalidBundle() => new("AIVIS-BUNDLE: 同梱Aivis音声のモデル一覧が不正か、モデルが不足しています。");

    internal AivisSpeech CreateVoice(string id, IPcmOutput output)
    {
        for (var index = 0; index < Voices.Count; index++)
            if (Voices[index].Id == id) return new(this, index, Voices[index].Name, output);
        throw new ArgumentOutOfRangeException(nameof(id));
    }

    internal async Task<PcmAudio?> ExchangeAsync(int index, string? text, CancellationToken ct)
    {
        lifetimeToken.ThrowIfCancellationRequested();
        var request = AivisPcmProtocol.Encode(index, text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        var work = deadline.Token;
        try
        {
            await serial.WaitAsync(work).ConfigureAwait(false);
            try
            {
                work.ThrowIfCancellationRequested();
                var active = StartWorker();
                using var cancelPipe = work.UnsafeRegister(static state => KillForCancellation((Process)state!), active);
                await active.StandardInput.BaseStream.WriteAsync(request, work).ConfigureAwait(false);
                await active.StandardInput.BaseStream.FlushAsync(work).ConfigureAwait(false);
                var result = await AivisPcmProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, text is null, work)
                    .ConfigureAwait(false);
                work.ThrowIfCancellationRequested();
                return result;
            }
            catch
            {
                ExceptionDispatchInfo? cleanup = null;
                try { await StopWorkerAsync(graceful: false).ConfigureAwait(false); }
                catch (IOException error) { cleanup = ExceptionDispatchInfo.Capture(error); }
                ct.ThrowIfCancellationRequested();
                lifetimeToken.ThrowIfCancellationRequested();
                work.ThrowIfCancellationRequested();
                cleanup?.Throw();
                throw;
            }
            finally { serial.Release(); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !lifetimeToken.IsCancellationRequested)
        {
            throw new TimeoutException("AIVIS-TIMEOUT: Aivis音声が時間内に応答しませんでした。");
        }
    }

    private Process StartWorker()
    {
        if (process is { HasExited: false })
        {
            if (processStopping) throw new IOException("AIVIS-STOP: 前回のAivis音声ワーカーが終了していません。");
            return process;
        }
        process?.Dispose();
        process = null;
        processStopping = false;
        var python = Path.Combine(packageDirectory, "python", "python.exe");
        var script = Path.Combine(packageDirectory, "worker.py");
        WorldComparison.RejectLinks(python);
        WorldComparison.RejectLinks(script);
        var temporary = Path.Combine(dataDirectory, "tmp", "aivis");
        WorldComparison.RejectLinks(temporary);
        Directory.CreateDirectory(temporary);
        var start = new ProcessStartInfo(python)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = packageDirectory,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("-B");
        start.ArgumentList.Add(script);
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "SystemDrive", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE" })
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } value) start.Environment[name] = value;
        start.Environment["PATH"] = Path.GetDirectoryName(python) + Path.PathSeparator + Environment.SystemDirectory;
        foreach (var name in new[] { "TEMP", "TMP", "TMPDIR", "TANAYOMI_AIVIS_TEMP", "HF_HOME", "XDG_CACHE_HOME", "APPDATA", "LOCALAPPDATA", "USERPROFILE" })
            start.Environment[name] = temporary;
        start.Environment["HF_HUB_OFFLINE"] = "1";
        start.Environment["TRANSFORMERS_OFFLINE"] = "1";
        start.Environment["HF_HUB_DISABLE_TELEMETRY"] = "1";
        process = NativeChildProcess.Start(start) ?? throw new IOException("AIVIS-START: Aivis音声ワーカーを開始できません。");
        _ = DrainAsync(process.StandardError.BaseStream);
        return process;
    }

    private static void KillForCancellation(Process active)
    {
        try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    private static async Task DrainAsync(Stream stream)
    {
        try
        {
            var buffer = new byte[512];
            while (await stream.ReadAsync(buffer).ConfigureAwait(false) != 0) { }
        }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }

    private async Task StopWorkerAsync(bool graceful)
    {
        if (process is not { } active) return;
        processStopping = true;
        try
        {
            if (graceful && !active.HasExited)
            {
                active.StandardInput.Close();
                using var grace = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
                try { await active.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (active.HasExited) { }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await active.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            throw new IOException("AIVIS-STOP: Aivis音声ワーカーの終了を確認できません。");
        }
        active.Dispose();
        process = null;
        processStopping = false;
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
            return new(disposal ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel();
        await serial.WaitAsync().ConfigureAwait(false);
        try { await StopWorkerAsync(graceful: true).ConfigureAwait(false); }
        finally
        {
            serial.Release();
            lifetime.Dispose();
        }
    }
}

internal sealed class AivisSpeech(AivisSpeechWorker worker, int index, string name, IPcmOutput output) : ISpeechOutput, ISpeechPreparation
{
    public string Name => name;

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        var pcm = await worker.ExchangeAsync(index, text, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("AIVIS-PROTOCOL: Aivis音声ワーカーが音声を返しませんでした。");
        await output.PlayAsync(pcm.Samples, pcm.SampleRate, cancellationToken).ConfigureAwait(false);
    }

    public async Task PrepareAsync(CancellationToken cancellationToken) =>
        _ = await worker.ExchangeAsync(index, null, cancellationToken).ConfigureAwait(false);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
