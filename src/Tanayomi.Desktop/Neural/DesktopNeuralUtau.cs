using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;

namespace Tanayomi.Desktop;

internal sealed class DesktopNeuralUtau : IUtauPcmPostprocessor, IUtauDirectRenderer
{
    private readonly Func<string, CancellationToken, Task<PcmAudio>> synthesize;
    private readonly Func<Voicebank, CancellationToken, Task<IReadOnlyList<PcmAudio>>>? references;
    private readonly Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>>? ownedReferences;
    private readonly Func<PcmAudio, IReadOnlyList<PcmAudio>, CancellationToken, Task<PcmAudio>>? convert;
    private readonly Func<IReadOnlyList<PcmAudio>, CancellationToken, Task>? prepare;
    private readonly Func<CancellationToken, Task> available;
    private readonly IAsyncDisposable? owner;
    private readonly NeuralReferenceCache? referenceCache;
    private readonly bool ownsReferenceSnapshots;
    private readonly Func<ProcessStartInfo, Process> launch = start => Process.Start(start) ?? throw new IOException("NEURAL_WORKER_START");
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly object disposalGate = new();
    private Process? worker;
    private Task? errorDrain, disposal;
    private string? workerRoot;
    private bool workerStopping;
    private bool workerReady;
    private int disposed;
    private byte[]? lastConversionKey;
    private PcmAudio? lastConversion;
    private bool lastDrawnOutMode;
    private sealed record RepeatedRequest(string Text, object DonorGeneration, byte[] ReferenceFingerprint);
    private RepeatedRequest? lastRequest;
    private readonly NeuralReferenceAcknowledgement referenceAcknowledgement = new();
    private readonly NeuralReferenceFingerprintMemo referenceFingerprintMemo = new();
    private readonly Func<CancellationToken, Task> prepareDonor;
    private readonly DesktopOpenJtalkSpeech? ownedDonor;
    internal NeuralStageTimings? LastTimings { get; private set; }

    // The default renderer uses these clips only for validation, fingerprinting
    // and pipe writes while holding serial. No caller receives their arrays.
    private sealed class RendererReferenceCache() : NeuralReferenceCache(isolateCaller: false) { }

    internal DesktopNeuralUtau(string? referenceRoot = null)
    {
        var allowedRoot = ReferenceRoot(referenceRoot);
        var donor = new DesktopOpenJtalkSpeech(new NoPlayback()); owner = donor; ownedDonor = donor;
        synthesize = donor.SynthesizeAsync; prepareDonor = donor.PrepareAsync;
        available = NeuralOpenVoiceAssets.CheckAvailableAsync;
        referenceCache = new RendererReferenceCache();
        // Only this private provider may use the cache-owned PCM directly.
        ownsReferenceSnapshots = true;
        ownedReferences = (bank, ct) => NeuralVoiceReferences.ReadAsync(bank, allowedRoot, ct, referenceCache,
            readConcurrency: Environment.ProcessorCount >= 8 ? 2 : 1);
    }
    private static string ReferenceRoot(string? selected)
    {
        if (selected is null) return Voicebanks.DefaultDirectory;
        // The only override is an explicitly owned product-check fixture. The
        // ordinary constructor continues to accept installed data banks only.
        var parent = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        var name = Path.GetFileName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison) || name.Length != 40 ||
            !name.StartsWith("context-", StringComparison.Ordinal) || !name.AsSpan(8).ToString().All(Uri.IsHexDigit))
            throw new InvalidDataException("NEURAL_REFERENCE_FIXTURE_ROOT");
        WorldComparison.RejectLinks(full); return full;
    }
    internal DesktopNeuralUtau(Func<string, CancellationToken, Task<PcmAudio>> synthesize,
        Func<Voicebank, CancellationToken, Task<IReadOnlyList<PcmAudio>>> references,
        Func<PcmAudio, IReadOnlyList<PcmAudio>, CancellationToken, Task<PcmAudio>>? convert,
        Func<CancellationToken, Task> available, IAsyncDisposable? owner = null, Func<ProcessStartInfo, Process>? launch = null,
        Func<IReadOnlyList<PcmAudio>, CancellationToken, Task>? prepare = null, Func<CancellationToken, Task>? prepareDonor = null)
    {
        this.prepareDonor = prepareDonor ?? (_ => Task.CompletedTask);
        this.synthesize = synthesize; this.references = references; this.convert = convert;
        this.available = available; this.owner = owner;
        this.prepare = prepare;
        if (this.prepare is null && convert is not null) this.prepare = (_, _) => Task.CompletedTask;
        if (launch is not null) this.launch = launch;
    }

    public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) =>
        throw new InvalidOperationException("ニューラル声質変換は文章と選択音源から直接合成する必要があります。");
    public async Task PrepareAsync(Voicebank bank, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); var ct = deadline.Token; var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); entered = true; ct.ThrowIfCancellationRequested();
            if (workerStopping) throw new IOException("NEURAL_WORKER_UNREAPED");
            if (!HasVerifiedWorker) await Observe(available(ct), ct).ConfigureAwait(false);
            // The private reader owns its PCM snapshots. Let the isolated
            // worker load its verified models while those snapshots are read.
            // The enclosing failure path reaps this worker if reading fails.
            if (ownsReferenceSnapshots && prepare is null && Environment.ProcessorCount >= 8 &&
                (worker is null || worker.HasExited))
                await StartWorkerAsync(ct).ConfigureAwait(false);
            var request = await ReadReferencesAsync(bank, ct).ConfigureAwait(false);
            var clips = request.Clips;
            if (ownedDonor is not null && Environment.ProcessorCount >= 8)
            {
                using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var donorPreparation = ownedDonor.PrepareAsync(pair.Token);
                try
                {
                    await Observe(prepare is null ? PrepareWorkerAsync(clips, request.Fingerprint, pair.Token) :
                        prepare(clips, pair.Token), pair.Token).ConfigureAwait(false);
                    await donorPreparation.ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                }
                finally
                {
                    // Only the private, bounded donor may overlap. Join its
                    // cancellation/cleanup before releasing the renderer gate.
                    pair.Cancel();
                    try { await donorPreparation.ConfigureAwait(false); } catch { }
                }
            }
            else
            {
                await Observe(prepare is null ? PrepareWorkerAsync(clips, request.Fingerprint, ct) : prepare(clips, ct), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                await Observe(prepareDonor(ct), ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
            }
        }
        catch
        {
            if (entered) { lastConversion = null; lastConversionKey = null; lastRequest = null; referenceFingerprintMemo.Clear(); }
            Exception? cleanup = null;
            if (entered) try { await StopWorkerAsync().ConfigureAwait(false); } catch (Exception error) { cleanup = error; }
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
            if (deadline.IsCancellationRequested) throw new TimeoutException("NEURAL_PREPARE_TIMEOUT: 声質変換の事前準備が60秒以内に完了しませんでした。");
            if (cleanup is not null) throw new IOException(workerStopping ? "NEURAL_WORKER_UNREAPED" : "NEURAL_CLEANUP_FAILED");
            throw new IOException("NEURAL_PREPARE_FAILED: 声質変換の事前準備に失敗しました。読み上げ時に再試行します。");
        }
        finally { if (entered) serial.Release(); }
    }
    private bool HasVerifiedWorker => workerReady && worker is { HasExited: false };
    public Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken cancellationToken) =>
        RenderAsync(text, bank, false, cancellationToken);
    public async Task<PcmAudio> RenderAsync(string text, Voicebank bank, bool drawnOutMode, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        _ = OpenJtalkPcmProtocol.ValidateRequestText(text);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(60)); var ct = deadline.Token;
        var entered = false;
        try
        {
            await serial.WaitAsync(ct).ConfigureAwait(false); entered = true; ct.ThrowIfCancellationRequested();
            LastTimings = null;
            if (lastDrawnOutMode != drawnOutMode)
            {
                lastConversion = null; lastConversionKey = null; lastRequest = null;
                lastDrawnOutMode = drawnOutMode;
            }
            if (workerStopping) throw new IOException("NEURAL_WORKER_UNREAPED");
            var stage = Stopwatch.GetTimestamp();
            // A successful owned worker keeps every approved model/native/CRT
            // file open without write/delete sharing for its entire lifetime.
            // Its already loaded, validated assets need no second parent hash.
            if (!HasVerifiedWorker) await Observe(available(ct), ct).ConfigureAwait(false);
            var assetsMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds; stage = Stopwatch.GetTimestamp();
            ReferenceRequest request;
            PcmAudio source;
            double referencesMs;
            if (ownedReferences is not null && lastRequest is { } repeated && lastConversion is { } readyAudio &&
                string.Equals(text, repeated.Text, StringComparison.Ordinal) &&
                ReferenceEquals(ownedDonor?.PreparedGeneration, repeated.DonorGeneration))
            {
                // Every raw reference is still checked. Only the fixed owned
                // NIT worker is deterministic for identical text in one generation.
                request = await ReadReferencesAsync(bank, ct).ConfigureAwait(false);
                referencesMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds; stage = Stopwatch.GetTimestamp();
                if (request.Fingerprint.AsSpan().SequenceEqual(repeated.ReferenceFingerprint) &&
                    ReferenceEquals(ownedDonor?.PreparedGeneration, repeated.DonorGeneration))
                {
                    var copy = new PcmAudio((short[])readyAudio.Samples.Clone(), readyAudio.SampleRate);
                    NeuralPcmProtocol.ValidateOutput(copy); ct.ThrowIfCancellationRequested();
                    if (ReferenceEquals(ownedDonor?.PreparedGeneration, repeated.DonorGeneration))
                    {
                        LastTimings = new(assetsMs, referencesMs, 0, Stopwatch.GetElapsedTime(stage).TotalMilliseconds);
                        return copy;
                    }
                }
                // A changed reference/generation must synthesize again. This
                // uncommon same-text miss forgoes the ordinary read/donor overlap.
                lastRequest = null;
                source = await StartOwnedDonorAsync(text, drawnOutMode, ct).ConfigureAwait(false);
            }
            else if (ownedReferences is not null && ownedDonor is { IsPrepared: true })
            {
                using var pair = CancellationTokenSource.CreateLinkedTokenSource(ct);
                Task<PcmAudio>? donorTask = null;
                try
                {
                    // Start an async wrapper so synchronous donor throws cannot
                    // escape before the owned reference task has been joined.
                    donorTask = StartOwnedDonorAsync(text, drawnOutMode, pair.Token);
                    request = await ReadReferencesAsync(bank, pair.Token).ConfigureAwait(false);
                    referencesMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds;
                    stage = Stopwatch.GetTimestamp();
                    source = await donorTask.ConfigureAwait(false);
                }
                finally
                {
                    // Reference errors win over donor errors. Never release the
                    // renderer gate while its private donor can still complete.
                    pair.Cancel();
                    if (donorTask is not null)
                        try { await donorTask.ConfigureAwait(false); } catch { }
                }
            }
            else
            {
                request = await ReadReferencesAsync(bank, ct).ConfigureAwait(false);
                referencesMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds; stage = Stopwatch.GetTimestamp();
                source = ownedDonor is null
                    ? await Observe(synthesize(text, ct), ct).ConfigureAwait(false)
                    : await StartOwnedDonorAsync(text, drawnOutMode, ct).ConfigureAwait(false);
            }
            var clips = request.Clips;
            var donorGeneration = ownedReferences is not null ? ownedDonor?.PreparedGeneration : null;
            lastRequest = null;
            // References records its wall interval; donor records only the
            // remaining wait plus validation/clone, so stage sums do not double count.
            // The private default donor validates its fresh response in both
            // the protocol reader and ProcessAsync, then returns it without
            // playback or external sharing. Injected donors keep validation
            // before their defensive copy.
            if (ownedDonor is null)
            {
                OpenJtalkPcmProtocol.ValidateAudio(source);
                source = new((short[])source.Samples.Clone(), source.SampleRate);
            }
            NeuralPcmProtocol.ValidateRequest(source, clips);
            var donorMs = Stopwatch.GetElapsedTime(stage).TotalMilliseconds; stage = Stopwatch.GetTimestamp();
            // Content, rate, length and ordered reference boundaries are all
            // hashed. Keep one successful result only, never caller-owned PCM.
            var key = CreateConversionKey(source, request.Fingerprint, ct);
            PcmAudio audio;
            if (lastConversion is { } cached && lastConversionKey is { } previous && key.AsSpan().SequenceEqual(previous))
                audio = new((short[])cached.Samples.Clone(), cached.SampleRate);
            else
            {
                audio = await Observe(convert is null ? ExchangeAsync(source, clips, request.Fingerprint, ct) : convert(source, clips, ct), ct).ConfigureAwait(false);
                NeuralPcmProtocol.ValidateOutput(audio); ct.ThrowIfCancellationRequested();
                lastConversion = new((short[])audio.Samples.Clone(), audio.SampleRate);
                lastConversionKey = key;
            }
            NeuralPcmProtocol.ValidateOutput(audio); ct.ThrowIfCancellationRequested();
            if (donorGeneration is not null && ReferenceEquals(donorGeneration, ownedDonor?.PreparedGeneration))
                lastRequest = new(text, donorGeneration, (byte[])request.Fingerprint.Clone());
            LastTimings = new(assetsMs, referencesMs, donorMs, Stopwatch.GetElapsedTime(stage).TotalMilliseconds);
            return audio;
        }
        catch (Exception error)
        {
            if (entered) { lastConversion = null; lastConversionKey = null; lastRequest = null; referenceFingerprintMemo.Clear(); }
            Exception? cleanup = null;
            if (entered) try { await StopWorkerAsync().ConfigureAwait(false); } catch (Exception failure) { cleanup = failure; }
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) throw new OperationCanceledException(lifetime.Token);
            if (deadline.IsCancellationRequested) throw new TimeoutException("NEURAL_TIMEOUT: 声質変換が60秒以内に完了しませんでした。");
            if (cleanup is not null)
                throw new IOException(workerStopping
                    ? "NEURAL_WORKER_UNREAPED: ワーカーの終了未確認のため、この方式の再実行を停止しました。"
                    : "NEURAL_CLEANUP_FAILED: 終了した声質変換ワーカーの一時領域を清掃できませんでした。");
            if (error is NeuralReferenceException) throw;
            if (error is NeuralWorkerException) throw;
            throw new IOException("NEURAL_SYNTHESIS_FAILED: 声質変換に必要な資産・参照音源または処理を確認できませんでした。");
        }
        finally { if (entered) serial.Release(); }
    }

    private async Task<PcmAudio> StartOwnedDonorAsync(string text, bool drawnOutMode, CancellationToken ct) =>
        await (drawnOutMode && ownedDonor is not null ? ownedDonor.SynthesizeAsync(text, true, ct) : synthesize(text, ct)).ConfigureAwait(false);

    private sealed record ReferenceRequest(IReadOnlyList<PcmAudio> Clips, byte[] Fingerprint);
    private async Task<ReferenceRequest> ReadReferencesAsync(Voicebank bank, CancellationToken ct)
    {
        if (ownedReferences is not null)
        {
            // Owned reads join their private tasks and close/clear all buffers
            // before releasing the renderer gate, including cancellation.
            var snapshot = await ownedReferences(bank, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            var clips = SnapshotReferences(snapshot.Clips, ct);
            var fingerprint = referenceFingerprintMemo.Get(snapshot with { Clips = clips }, ct);
            ct.ThrowIfCancellationRequested();
            return new(clips, fingerprint);
        }
        var borrowed = await Observe(references!(bank, ct), ct).ConfigureAwait(false);
        var isolated = SnapshotReferences(borrowed, ct);
        // Injected providers have no raw-content/ownership proof. Recompute
        // their ordered PCM fingerprint even when they reuse mutable arrays.
        var key = NeuralOpenVoiceSession.FingerprintReferences(isolated, ct);
        ct.ThrowIfCancellationRequested();
        return new(isolated, key);
    }

    internal static byte[] CreateConversionKey(PcmAudio source, ReadOnlySpan<byte> referenceFingerprint, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (source is null || source.Samples is null || source.Samples.Length is < 1 or > NeuralPcmProtocol.MaximumSourceSamples ||
            referenceFingerprint.Length != 32) throw new InvalidDataException("NEURAL_CONVERSION_KEY");
        var sourceKey = NeuralOpenVoiceSession.FingerprintReferences([source], ct);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("Tanayomi.OpenVoice.ConversionKey.v1\0"u8);
        hash.AppendData(sourceKey); hash.AppendData(referenceFingerprint);
        var result = hash.GetHashAndReset(); ct.ThrowIfCancellationRequested(); return result;
    }

    private IReadOnlyList<PcmAudio> SnapshotReferences(IReadOnlyList<PcmAudio> selected, CancellationToken ct)
    {
        if (selected is null || selected.Count is < 1 or > NeuralPcmProtocol.MaximumReferenceCount) throw new NeuralReferenceException(1);
        var clips = new PcmAudio[selected.Count]; var total = 0L;
        for (var i = 0; i < clips.Length; i++)
        {
            ct.ThrowIfCancellationRequested(); var item = selected[i];
            if (item is null || item.Samples is null || item.SampleRate != 44100) throw new InvalidDataException();
            if (item.Samples.Length is < 4096 or > 44100 * 30) throw new NeuralReferenceException(3);
            if ((total += item.Samples.Length) > 44100L * 180) throw new NeuralReferenceException(2);
            // Injected providers remain borrowed and must still be cloned.
            clips[i] = ownsReferenceSnapshots ? item : new((short[])item.Samples.Clone(), item.SampleRate);
        }
        return Array.AsReadOnly(clips);
    }

    private async Task PrepareWorkerAsync(IReadOnlyList<PcmAudio> clips, byte[] key, CancellationToken ct)
    {
        if (worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        var active = worker!;
        using var cancellation = ct.Register(() => Kill(active));
        await NeuralPcmProtocol.WritePreparationAsync(active.StandardInput.BaseStream, clips, ct,
            key, referenceAcknowledgement.CanReuse(key)).ConfigureAwait(false);
        await NeuralPcmProtocol.ReadPreparationResponseAsync(active.StandardOutput.BaseStream, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); referenceAcknowledgement.Acknowledge(key);
        workerReady = true;
    }

    private async Task<PcmAudio> ExchangeAsync(PcmAudio source, IReadOnlyList<PcmAudio> clips, byte[] key, CancellationToken ct)
    {
        if (worker is null || worker.HasExited) await StartWorkerAsync(ct).ConfigureAwait(false);
        var active = worker!;
        using var cancellation = ct.Register(() => Kill(active));
        await NeuralPcmProtocol.WriteRequestAsync(active.StandardInput.BaseStream, source, clips, ct,
            key, referenceAcknowledgement.CanReuse(key)).ConfigureAwait(false);
        var audio = await NeuralPcmProtocol.ReadResponseAsync(active.StandardOutput.BaseStream, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); referenceAcknowledgement.Acknowledge(key);
        workerReady = true; return audio;
    }
    private async Task StartWorkerAsync(CancellationToken ct)
    {
        await StopWorkerAsync().ConfigureAwait(false); ct.ThrowIfCancellationRequested();
        var parent = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp"));
        WorldComparison.RejectLinks(parent); Directory.CreateDirectory(parent); WorldComparison.RejectLinks(parent);
        var root = Path.Combine(parent, "nv-" + Guid.NewGuid().ToString("N")[..12]);
        WorldComparison.RejectLinks(root);
        if (Directory.Exists(root) || File.Exists(root)) throw new IOException("NEURAL_WORKER_ROOT");
        Directory.CreateDirectory(root); workerRoot = root;
        var executable = Environment.ProcessPath ?? throw new IOException("NEURAL_EXECUTABLE");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add(NeuralPcmWorker.Argument);
        start.Environment["TEMP"] = root; start.Environment["TMP"] = root; start.Environment["TMPDIR"] = root;
        worker = NativeChildProcess.Start(start, launch);
        errorDrain = DrainAsync(worker.StandardError.BaseStream);
    }
    private async Task StopWorkerAsync()
    {
        lastRequest = null;
        workerReady = false;
        referenceAcknowledgement.Clear();
        if (worker is { } active)
        {
            workerStopping = true; Kill(active);
            using var reap = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await active.WaitForExitAsync(reap.Token).ConfigureAwait(false);
                if (!active.HasExited) throw new IOException();
                active.Dispose(); worker = null; workerStopping = false;
            }
            catch (Exception) { throw new IOException("NEURAL_WORKER_UNREAPED"); }
        }
        if (errorDrain is { } drain)
        {
            try { await drain.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch (Exception) { }
            errorDrain = null;
        }
        if (workerRoot is { } root)
        {
            var parent = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp")); var full = Path.GetFullPath(root);
            if (!full.StartsWith(parent + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
                Path.GetFileName(full).Length != 15 || !Path.GetFileName(full).StartsWith("nv-", StringComparison.Ordinal)) throw new IOException("NEURAL_CLEANUP_ROOT");
            DeleteOwned(full, full); workerRoot = null;
        }
    }
    private static void DeleteOwned(string path, string root)
    {
        var full = Path.GetFullPath(path);
        if (full != root && !full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("NEURAL_CLEANUP_ROOT");
        WorldComparison.RejectLinks(Path.GetDirectoryName(full)!);
        var attributes = File.GetAttributes(full);
        if ((attributes & FileAttributes.Directory) == 0) { File.Delete(full); return; }
        if ((attributes & FileAttributes.ReparsePoint) == 0)
            foreach (var child in Directory.EnumerateFileSystemEntries(full)) DeleteOwned(child, root);
        Directory.Delete(full);
    }
    private static void Kill(Process active)
    {
        try { if (!active.HasExited) active.Kill(entireProcessTree: true); }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static async Task DrainAsync(Stream input)
    {
        try { var buffer = new byte[1024]; while (await input.ReadAsync(buffer).ConfigureAwait(false) != 0) { } }
        catch (Exception error) when (error is IOException or ObjectDisposedException) { }
    }
    private static async Task Observe(Task task, CancellationToken ct)
    {
        _ = task.ContinueWith(done => { _ = done.Exception; }, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        await task.WaitAsync(ct).ConfigureAwait(false);
    }
    private static async Task<T> Observe<T>(Task<T> task, CancellationToken ct)
    {
        await Observe((Task)task, ct).ConfigureAwait(false); return await task.ConfigureAwait(false);
    }
    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null) { Interlocked.Exchange(ref disposed, 1); disposal = Task.Run(DisposeCoreAsync); }
            return new(disposal);
        }
    }
    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel(); await serial.WaitAsync().ConfigureAwait(false);
        try { try { await StopWorkerAsync().ConfigureAwait(false); } finally { if (owner is not null) await owner.DisposeAsync().ConfigureAwait(false); } }
        finally { lastConversion = null; lastConversionKey = null; lastRequest = null; referenceFingerprintMemo.Clear(); referenceCache?.Clear(); serial.Release(); lifetime.Dispose(); }
    }
    private sealed class NoPlayback : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken ct) => throw new InvalidOperationException("NEURAL_DONOR_PLAYBACK");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed record NeuralStageTimings(double AssetsMilliseconds, double ReferencesMilliseconds,
    double DonorMilliseconds, double ConversionMilliseconds);

// Only the fixed private reader may use this content proof. It hashes each
// ordered raw WAV every request; unchanged bytes deterministically produce the
// same rate/length/PCM. This memo grants no worker-ACK or ownership capability.
internal sealed class NeuralReferenceFingerprintMemo
{
    private string? rawContent;
    private byte[]? fingerprint;
    internal bool LastHit { get; private set; }
    internal byte[] Get(NeuralReferenceSnapshot snapshot, CancellationToken ct)
    {
        try
        {
            ct.ThrowIfCancellationRequested(); LastHit = false;
            if (snapshot is null || snapshot.ContentSha256 is not { Length: 64 } token || !token.All(Uri.IsHexDigit))
                throw new InvalidDataException("NEURAL_REFERENCE_CONTENT_TOKEN");
            if (fingerprint is not null && string.Equals(rawContent, token, StringComparison.Ordinal))
            {
                var retained = (byte[])fingerprint.Clone(); ct.ThrowIfCancellationRequested(); LastHit = true; return retained;
            }
            var computed = NeuralOpenVoiceSession.FingerprintReferences(snapshot.Clips, ct);
            ct.ThrowIfCancellationRequested();
            fingerprint = (byte[])computed.Clone(); rawContent = token;
            return computed;
        }
        catch { Clear(); throw; }
    }
    internal void Clear() { rawContent = null; fingerprint = null; LastHit = false; }
}

internal sealed record NeuralReferenceSnapshot(IReadOnlyList<PcmAudio> Clips, string ContentSha256)
{
    internal NeuralReferenceReadDiagnostics? Diagnostics { get; init; }
}
internal sealed record NeuralReferenceReadDiagnostics(double PathFileMilliseconds, double ReadMilliseconds,
    double RawHashMilliseconds, double CacheMilliseconds, double DecodeMilliseconds, double BufferMilliseconds,
    double TotalMilliseconds, double UnclassifiedMilliseconds, int FileCount, long RawBytes, int CacheHits, int CacheMisses);
internal sealed class NeuralReferenceException(int reason) : IOException(reason switch
{
    1 => "NEURAL_REFERENCE_COUNT: 声質変換の参照は重複しないWAVを1～149個含む音源が必要です。",
    2 => "NEURAL_REFERENCE_TOTAL: 声質変換の参照WAVの合計は180秒以下である必要があります。",
    _ => "NEURAL_REFERENCE_LENGTH: 声質変換の参照WAVは各30秒以下で約0.093秒以上必要です。"
});
internal static class NeuralVoiceReferences
{
    internal static async Task<NeuralReferenceSnapshot> ReadAsync(Voicebank bank, string allowedRoot, CancellationToken ct,
        NeuralReferenceCache? cache = null, bool diagnostics = false, int readConcurrency = 1)
    {
        if (readConcurrency is not (1 or 2)) throw new ArgumentOutOfRangeException(nameof(readConcurrency));
        if (diagnostics && readConcurrency != 1) throw new ArgumentException("Reference diagnostics require serial reading.");
        var started = diagnostics ? Stopwatch.GetTimestamp() : 0;
        var stage = started;
        long pathTicks = 0, readTicks = 0, hashTicks = 0, cacheTicks = 0, decodeTicks = 0, bufferTicks = 0, rawBytes = 0;
        var hits = 0; var misses = 0;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(allowedRoot));
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bank.Directory));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new InvalidDataException("NEURAL_REFERENCE_ROOT");
        WorldComparison.RejectLinks(directory);
        var paths = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var item in bank.Entries)
        {
            ct.ThrowIfCancellationRequested(); var path = Path.GetFullPath(item.WavePath);
            if (!path.StartsWith(directory + Path.DirectorySeparatorChar, comparison) || !Path.GetExtension(path).Equals(".wav", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("NEURAL_REFERENCE_PATH");
            if (paths.Add(path) && paths.Count > 149) throw new NeuralReferenceException(1);
        }
        if (paths.Count < 1) throw new NeuralReferenceException(1);
        if (diagnostics) pathTicks += Stopwatch.GetTimestamp() - stage;
        if (readConcurrency == 2)
            return await ReadTwoAsync(paths.OrderBy(path => path, StringComparer.Ordinal).ToArray(), cache, ct).ConfigureAwait(false);
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var clips = new List<PcmAudio>(); var total = 0L;
        byte[]? scratch = null;
        string contentHash;
        IReadOnlyList<PcmAudio> completeClips;
        try
        {
            foreach (var path in paths.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (diagnostics) stage = Stopwatch.GetTimestamp();
                ct.ThrowIfCancellationRequested(); WorldComparison.RejectLinks(path);
                // ReadExactly fills our own whole-file array; no second managed buffer is needed.
                var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                try
                {
                    var fileLength = file.Length;
                    if (fileLength is < 44 or > 32 * 1024 * 1024) throw new InvalidDataException("NEURAL_REFERENCE_WAV_SIZE");
                    var length = (int)fileLength;
                    if (diagnostics) { pathTicks += Stopwatch.GetTimestamp() - stage; rawBytes += length; stage = Stopwatch.GetTimestamp(); }
                    var useScratch = cache is not null && length <= 1024 * 1024;
                    var bytes = useScratch ? GetScratch(ref scratch, length) : new byte[length];
                    var payload = bytes.AsMemory(0, length);
                    if (diagnostics) { bufferTicks += Stopwatch.GetTimestamp() - stage; stage = Stopwatch.GetTimestamp(); }
                    // Awaited wall time includes I/O and continuation scheduling.
                    await file.ReadExactlyAsync(payload, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                    if (diagnostics) { readTicks += Stopwatch.GetTimestamp() - stage; stage = Stopwatch.GetTimestamp(); }
                    var rawHash = SHA256.HashData(payload.Span); content.AppendData(rawHash);
                    var key = Convert.ToHexString(rawHash);
                    if (diagnostics) { hashTicks += Stopwatch.GetTimestamp() - stage; stage = Stopwatch.GetTimestamp(); }
                    PcmAudio cached = null!;
                    var hit = cache?.TryGet(key, out cached) == true;
                    if (diagnostics) cacheTicks += Stopwatch.GetTimestamp() - stage;
                    if (hit)
                    {
                        if (diagnostics) hits++;
                        if ((total += cached!.Samples.Length) > 44100L * 180) throw new NeuralReferenceException(2);
                        clips.Add(cached); continue;
                    }
                    if (diagnostics) { misses++; stage = Stopwatch.GetTimestamp(); }
                    var clip = DecodeReference(bytes.AsSpan(0, length), ct, ref total); clips.Add(clip);
                    ct.ThrowIfCancellationRequested(); cache?.Add(key, clip);
                    if (diagnostics) decodeTicks += Stopwatch.GetTimestamp() - stage;
                }
                finally
                {
                    if (diagnostics) stage = Stopwatch.GetTimestamp();
                    await file.DisposeAsync().ConfigureAwait(false);
                    if (diagnostics) pathTicks += Stopwatch.GetTimestamp() - stage;
                }
            }
            if (diagnostics) stage = Stopwatch.GetTimestamp();
            contentHash = Convert.ToHexString(content.GetHashAndReset());
            if (diagnostics) hashTicks += Stopwatch.GetTimestamp() - stage;
            completeClips = Array.AsReadOnly(clips.ToArray());
        }
        finally
        {
            if (diagnostics) stage = Stopwatch.GetTimestamp();
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            if (diagnostics) bufferTicks += Stopwatch.GetTimestamp() - stage;
        }
        NeuralReferenceReadDiagnostics? detail = null;
        if (diagnostics)
        {
            var totalTicks = Stopwatch.GetTimestamp() - started;
            var classifiedTicks = pathTicks + readTicks + hashTicks + cacheTicks + decodeTicks + bufferTicks;
            var millisecondsPerTick = 1000.0 / Stopwatch.Frequency;
            detail = new(pathTicks * millisecondsPerTick, readTicks * millisecondsPerTick, hashTicks * millisecondsPerTick,
                cacheTicks * millisecondsPerTick, decodeTicks * millisecondsPerTick, bufferTicks * millisecondsPerTick,
                totalTicks * millisecondsPerTick, Math.Max(0, totalTicks - classifiedTicks) * millisecondsPerTick,
                paths.Count, rawBytes, hits, misses);
        }
        return new(completeClips, contentHash) { Diagnostics = detail };
    }

    internal static byte[] GetScratch(ref byte[]? scratch, int length)
    {
        if (length is < 1 or > 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(length));
        if (scratch is not null && scratch.Length >= length) return scratch;
        var capacity = 64 * 1024;
        while (capacity < length) capacity *= 2;
        var replacement = new byte[capacity];
        if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
        return scratch = replacement;
    }

    private static PcmAudio DecodeReference(ReadOnlySpan<byte> bytes, CancellationToken ct, ref long total)
    {
        // The codec sees an exact-length span, never a slot's unused tail.
        var decoded = UtauSpeech.DecodeWave(bytes, ct);
        if (decoded.Length < 4096) throw new NeuralReferenceException(3);
        if ((total += decoded.Length) > 44100L * 180) throw new NeuralReferenceException(2);
        var peak = ReferencePeak(decoded);
        if (!float.IsFinite(peak) || peak < 0.0001f) throw new InvalidDataException("NEURAL_REFERENCE_SILENT");
        var gain = Math.Min(1, 0.98f / peak);
        var samples = NeuralOpenVoiceSession.QuantizeValidatedSamples(decoded, gain, ct);
        return new(samples, 44100);
    }

    internal static float ReferencePeak(ReadOnlySpan<float> decoded)
    {
        var peak = 0f;
        var index = 0;
        if (Vector.IsHardwareAccelerated && decoded.Length >= Vector<float>.Count)
        {
            var maxima = Vector<float>.Zero;
            var finiteLimit = new Vector<float>(float.MaxValue);
            for (; index <= decoded.Length - Vector<float>.Count; index += Vector<float>.Count)
            {
                var absolute = Vector.Abs(new Vector<float>(decoded.Slice(index)));
                // Reject NaN before maximum reduction can hide it.
                if (!Vector.LessThanOrEqualAll(absolute, finiteLimit))
                    throw new InvalidDataException("NEURAL_REFERENCE_SILENT");
                maxima = Vector.Max(maxima, absolute);
            }
            for (var lane = 0; lane < Vector<float>.Count; lane++) peak = Math.Max(peak, maxima[lane]);
        }
        for (; index < decoded.Length; index++)
        {
            var sample = decoded[index];
            if (!float.IsFinite(sample)) throw new InvalidDataException("NEURAL_REFERENCE_SILENT");
            peak = Math.Max(peak, Math.Abs(sample));
        }
        return peak;
    }

    private static async Task<NeuralReferenceSnapshot> ReadTwoAsync(string[] paths, NeuralReferenceCache? cache, CancellationToken ct)
    {
        using var pendingCancellation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = pendingCancellation.Token;
        var slots = new[] { new ReferenceReadSlot(), new ReferenceReadSlot() };
        using var content = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var clips = new List<PcmAudio>(paths.Length); var total = 0L; var failed = false;
        try
        {
            slots[0].Start(paths[0], cache is not null, token);
            if (paths.Length > 1) slots[1].Start(paths[1], cache is not null, token);
            for (var index = 0; index < paths.Length; index++)
            {
                token.ThrowIfCancellationRequested();
                var slot = slots[index % 2];
                var read = await slot.Pending!.ConfigureAwait(false);
                slot.Pending = null;
                token.ThrowIfCancellationRequested();
                content.AppendData(read.RawHash);
                var key = Convert.ToHexString(read.RawHash);
                // Only this ordered parent loop ever touches the shared cache.
                if (cache?.TryGet(key, out var cached) == true)
                {
                    if ((total += cached.Samples.Length) > 44100L * 180) throw new NeuralReferenceException(2);
                    clips.Add(cached);
                }
                else
                {
                    var clip = DecodeReference(read.Bytes.AsSpan(0, read.Length), token, ref total);
                    token.ThrowIfCancellationRequested(); cache?.Add(key, clip); clips.Add(clip);
                }
                // No borrower survives decoding. A slot is reusable only now.
                read = null!;
                if (index + 2 < paths.Length) slot.Start(paths[index + 2], cache is not null, token);
            }
            token.ThrowIfCancellationRequested();
            return new(Array.AsReadOnly(clips.ToArray()), Convert.ToHexString(content.GetHashAndReset()));
        }
        catch { failed = true; throw; }
        finally
        {
            Exception? cleanupError = null;
            try { pendingCancellation.Cancel(); } catch (Exception error) { cleanupError = error; }
            foreach (var slot in slots)
            {
                // Task completion includes its await-using stream close. Observe
                // secondary failures without replacing the original exception.
                if (slot.Pending is { } pending)
                    try { await pending.ConfigureAwait(false); } catch (Exception error) { cleanupError ??= error; }
                slot.Pending = null;
                slot.Clear();
            }
            if (!failed && cleanupError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupError).Throw();
        }
    }

    private sealed record ReferenceFile(byte[] Bytes, int Length, byte[] RawHash);
    private sealed class ReferenceReadSlot
    {
        private byte[]? scratch;
        internal Task<ReferenceFile>? Pending;
        internal void Start(string path, bool useCache, CancellationToken ct)
        {
            if (Pending is not null) throw new InvalidOperationException("Reference slot is still in use.");
            Pending = Task.Run(async () =>
            {
                ct.ThrowIfCancellationRequested(); WorldComparison.RejectLinks(path);
                // The slot already owns a whole-file array; disable duplicate managed buffering.
                await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.Asynchronous);
                var fileLength = file.Length;
                if (fileLength is < 44 or > 32 * 1024 * 1024) throw new InvalidDataException("NEURAL_REFERENCE_WAV_SIZE");
                var length = (int)fileLength;
                var usesScratch = useCache && length <= 1024 * 1024;
                var bytes = usesScratch ? GetScratch(ref scratch, length) : new byte[length];
                var payload = bytes.AsMemory(0, length);
                await file.ReadExactlyAsync(payload, ct).ConfigureAwait(false); ct.ThrowIfCancellationRequested();
                var rawHash = SHA256.HashData(payload.Span);
                ct.ThrowIfCancellationRequested();
                return new ReferenceFile(bytes, length, rawHash);
            }, ct);
        }
        internal void Clear()
        {
            if (scratch is not null) CryptographicOperations.ZeroMemory(scratch);
            scratch = null;
        }
    }

}

// Owned by one serialized renderer. Raw bytes are rehashed before every hit;
// neither file timestamps nor caller-owned PCM can make stale audio reusable.
internal class NeuralReferenceCache
{
    private readonly Dictionary<string, PcmAudio> entries = new(StringComparer.Ordinal);
    private readonly Queue<string> order = new();
    private readonly bool isolateCaller;
    internal NeuralReferenceCache() : this(isolateCaller: true) { }
    protected NeuralReferenceCache(bool isolateCaller) => this.isolateCaller = isolateCaller;

    internal int Count => entries.Count;
    internal long Samples { get; private set; }
    internal bool TryGet(string key, out PcmAudio clip)
    {
        if (entries.TryGetValue(key, out var stored))
        { clip = Isolate(stored); return true; }
        clip = null!; return false;
    }
    internal void Add(string key, PcmAudio clip)
    {
        if (entries.ContainsKey(key)) return;
        if (clip.SampleRate != 44100 || clip.Samples.Length is < 4096 or > 44100 * 30) throw new InvalidDataException();
        while (entries.Count >= 149 || Samples + clip.Samples.Length > 44100L * 180)
        {
            var oldest = order.Dequeue(); Samples -= entries[oldest].Samples.Length; entries.Remove(oldest);
        }
        entries.Add(key, Isolate(clip)); order.Enqueue(key); Samples += clip.Samples.Length;
    }
    internal void Clear() { entries.Clear(); order.Clear(); Samples = 0; }

    private PcmAudio Isolate(PcmAudio clip) => isolateCaller ? new((short[])clip.Samples.Clone(), clip.SampleRate) : clip;
}
