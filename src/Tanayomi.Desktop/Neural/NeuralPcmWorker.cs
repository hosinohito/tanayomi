using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.ML.OnnxRuntime;

namespace Tanayomi.Desktop;

// This worker alone loads ORT and owns its model sessions. Its parent owns the
// wall deadline and process reap; no path or text arrives on this binary pipe.
internal static class NeuralPcmWorker
{
    internal const string Argument = "--neural-openvoice-pcm-worker";
    internal static int RunWorker(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] != Argument) return 2;
            var temporary = ValidateTemporaryRoot(Environment.CurrentDirectory, AppPaths.DataDirectory);
            // Main's ordinary storage setup runs before worker dispatch. Restore
            // the parent's checked, uniquely owned working root for native code.
            foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(name, temporary);
            if (!OpenJtalkPcmWorker.ConfigureNativeHost()) return 4;
            using var assets = NeuralOpenVoiceAssets.OpenVerified();
            assets.LoadNativeRuntime();
            using var session = new NeuralOpenVoiceSession(assets.Reference, assets.Converter,
                NeuralOpenVoiceSession.WorkerThreads(Environment.ProcessorCount))
                { ParallelReferenceFeatures = NeuralOpenVoiceSession.WorkerParallelReferenceFeatures(Environment.ProcessorCount) };
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            using var references = new NeuralPcmReferenceCache();
            while (NeuralPcmProtocol.ReadRequest(input, references) is { } request)
            {
                try
                {
                    if (request.Preparation)
                    {
                        request.References.Prepare(session);
                        references.Commit(request);
                        NeuralPcmProtocol.WritePreparationResponse(output, 0); continue;
                    }
                    var converted = request.References.Convert(session, request.Source);
                    var result = NeuralOpenVoiceSession.ToUtauAudio(converted);
                    references.Commit(request);
                    NeuralPcmProtocol.WriteResponse(output, 0, result);
                }
                catch (Exception)
                {
                    if (request.Preparation) NeuralPcmProtocol.WritePreparationResponse(output, 7);
                    else NeuralPcmProtocol.WriteResponse(output, 7, null);
                    return 4;
                }
            }
            return 0;
        }
        catch (EndOfStreamException) { return 3; }
        catch (InvalidDataException) { return 2; }
        catch (Exception) { return 5; }
    }

    internal static string ValidateTemporaryRoot(string workingDirectory, string dataDirectory)
    {
        var parent = Path.GetFullPath(Path.Combine(dataDirectory, "tmp"));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        var name = Path.GetFileName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(Path.GetDirectoryName(full), parent, comparison) || name.Length != 15 ||
            !name.StartsWith("nv-", StringComparison.Ordinal) || !name.AsSpan(3).ToString().All(Uri.IsHexDigit) || !Directory.Exists(full))
            throw new InvalidDataException("NEURAL_TEMP_ROOT");
        WorldComparison.RejectLinks(full); return full;
    }
}

internal enum NeuralModelKind { Reference, Converter }

// A verified model has no caller-owned byte alias. Only this factory creates
// its private payload; callers can create sessions, never obtain its bytes.
internal sealed class NeuralVerifiedModel : IDisposable
{
    private readonly object gate = new();
    private byte[]? bytes;
    private readonly NeuralModelKind kind;
    private NeuralVerifiedModel(byte[] bytes, NeuralModelKind kind) { this.bytes = bytes; this.kind = kind; }

    internal static NeuralVerifiedModel ReadVerified(Stream stream, NeuralModelKind kind, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var (length, hash) = kind switch
        {
            NeuralModelKind.Reference => (NeuralOpenVoiceSession.ReferenceBytes, NeuralOpenVoiceSession.ReferenceSha256),
            NeuralModelKind.Converter => (NeuralOpenVoiceSession.ConverterBytes, NeuralOpenVoiceSession.ConverterSha256),
            _ => throw new InvalidDataException("NEURAL_MODEL_KIND")
        };
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead || !stream.CanSeek || stream.Position != 0 || stream.Length != length)
            throw new InvalidDataException("NEURAL_ASSET_SIZE");
        var owned = new byte[length];
        for (var at = 0; at < owned.Length;)
        {
            ct.ThrowIfCancellationRequested();
            var count = stream.Read(owned.AsSpan(at, Math.Min(owned.Length - at, 65536)));
            if (count == 0) throw new EndOfStreamException();
            at += count;
        }
        ct.ThrowIfCancellationRequested();
        if (stream.Length != length || !Convert.ToHexString(SHA256.HashData(owned)).Equals(hash, StringComparison.Ordinal))
            throw new InvalidDataException("NEURAL_ASSET_HASH");
        ct.ThrowIfCancellationRequested();
        return new NeuralVerifiedModel(owned, kind);
    }

    internal void ValidateKind(NeuralModelKind expected)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            if (kind != expected) throw new InvalidDataException("NEURAL_MODEL_KIND");
        }
    }

    internal InferenceSession CreateNativeSession(SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(bytes is null, this);
            return new InferenceSession(bytes!, options);
        }
    }

    public void Dispose() { lock (gate) bytes = null; }
}

internal sealed class NeuralOpenVoiceAssets : IDisposable
{
    // A successful development build does not authorize redistribution. Fill
    // these only after the native/CRT adoption decision and exact-file audit.
    internal const string ApprovedNativeSha256 = "1054ABA195985786A160E1F0220527B34734FACD5CA32D8DBEF094A53A07846C";
    internal const int ApprovedNativeBytes = 10176000;
    internal static bool Configured => ApprovedNativeSha256.Length == 64 && ApprovedNativeBytes > 0;
    private static readonly (string Name, int Bytes, string Hash)[] RuntimeFiles =
    [
        ("onnxruntime.dll", ApprovedNativeBytes, ApprovedNativeSha256),
        ("msvcp140.dll", 565648, "43A9666C2F405258F165B53082860AC7838039A75FD2E85286085EDE0DA0273B"),
        ("vcruntime140.dll", 97160, "C865C3366A98431EC3A5959CB5AC3966081A43B82DFCD8BFEFAFE0146B1508DB"),
        ("vcruntime140_1.dll", 37256, "6C1E531C25AB2934A4EA9970598BC751D924D7CC5650DF3E1282B61D6CD24F42")
    ];
    private readonly List<FileStream> pins = [];
    private string nativePath = "";
    private nint native;
    private NeuralVerifiedModel? reference, converter;
    internal NeuralVerifiedModel Reference => reference ?? throw new ObjectDisposedException(nameof(NeuralOpenVoiceAssets));
    internal NeuralVerifiedModel Converter => converter ?? throw new ObjectDisposedException(nameof(NeuralOpenVoiceAssets));

    internal static async Task CheckAvailableAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var root = CheckedRoot();
        foreach (var item in new[]
        {
            ("reference.onnx", NeuralOpenVoiceSession.ReferenceBytes, NeuralOpenVoiceSession.ReferenceSha256),
            ("converter.onnx", NeuralOpenVoiceSession.ConverterBytes, NeuralOpenVoiceSession.ConverterSha256)
        }.Concat(RuntimeFiles))
        {
            ct.ThrowIfCancellationRequested(); var path = Path.Combine(root, item.Item1); WorldComparison.RejectLinks(path);
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous);
            if (stream.Length != item.Item2) throw new InvalidDataException("NEURAL_ASSET_SIZE");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int count;
            while ((count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0) hash.AppendData(buffer.AsSpan(0, count));
            if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(item.Item3, StringComparison.Ordinal)) throw new InvalidDataException("NEURAL_ASSET_HASH");
        }
        using var notices = new NeuralOpenVoiceAssets();
        foreach (var name in new[] { "OpenVoice-LICENSE.txt", "OpenVoice-NOTICE.txt", "openvoice-build.json" }) notices.PinDocument(Path.Combine(root, name));
        ct.ThrowIfCancellationRequested();
    }

    internal static NeuralOpenVoiceAssets OpenVerified(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var root = CheckedRoot();
        var assets = new NeuralOpenVoiceAssets();
        try
        {
            assets.reference = assets.Pin(Path.Combine(root, "reference.onnx"), NeuralModelKind.Reference, ct);
            assets.converter = assets.Pin(Path.Combine(root, "converter.onnx"), NeuralModelKind.Converter, ct);
            assets.nativePath = Path.Combine(root, "onnxruntime.dll");
            foreach (var item in RuntimeFiles) assets.PinVerified(Path.Combine(root, item.Name), item.Bytes, item.Hash, ct);
            // Licenses/model provenance travel with an adopted asset set.
            foreach (var name in new[] { "OpenVoice-LICENSE.txt", "OpenVoice-NOTICE.txt", "openvoice-build.json" })
                assets.PinDocument(Path.Combine(root, name));
            return assets;
        }
        catch { assets.Dispose(); throw; }
    }

    private static string CheckedRoot()
    {
        if (!Configured) throw new IOException("NEURAL_ASSET_NOT_READY: 採用確認済みのニューラル実行資産がありません。");
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("NEURAL_PLATFORM: このニューラル実行資産はWindows x64専用です。");
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "resources", "openvoice")); WorldComparison.RejectLinks(root); return root;
    }
    private void PinVerified(string path, int bytes, string expectedHash, CancellationToken ct)
    {
        WorldComparison.RejectLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(stream);
        if (stream.Length != bytes || bytes is < 1 or > 256 * 1024 * 1024) throw new InvalidDataException("NEURAL_ASSET_SIZE");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); var buffer = new byte[65536]; int count;
        while ((count = stream.Read(buffer)) != 0) { ct.ThrowIfCancellationRequested(); hash.AppendData(buffer.AsSpan(0, count)); }
        if (!Convert.ToHexString(hash.GetHashAndReset()).Equals(expectedHash, StringComparison.Ordinal)) throw new InvalidDataException("NEURAL_ASSET_HASH");
    }

    private NeuralVerifiedModel Pin(string path, NeuralModelKind kind, CancellationToken ct)
    {
        WorldComparison.RejectLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(stream);
        return NeuralVerifiedModel.ReadVerified(stream, kind, ct);
    }
    private void PinDocument(string path)
    {
        WorldComparison.RejectLinks(path);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); pins.Add(stream);
        if (stream.Length is < 1 or > 1024 * 1024) throw new InvalidDataException("NEURAL_ASSET_NOTICE");
    }
    internal void LoadNativeRuntime()
    {
        if (native != 0) throw new InvalidOperationException();
        native = NativeLibrary.Load(nativePath, typeof(InferenceSession).Assembly,
            DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
        NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, (name, _, _) =>
            name is "onnxruntime" or "onnxruntime.dll" ? native : throw new DllNotFoundException("NEURAL_NATIVE_NAME"));
    }
    public void Dispose()
    {
        // ORT's managed process-wide environment can outlive an individual
        // session. Retain the loaded module until this isolated process exits.
        reference?.Dispose(); converter?.Dispose(); reference = null; converter = null;
        foreach (var pin in pins) pin.Dispose(); pins.Clear();
    }
}

internal sealed class NeuralWorkerException(int status) : IOException($"NEURAL_WORKER_STATUS: 声質変換ワーカーが失敗しました（状態{status}）。")
{ internal int Status { get; } = status; }
internal sealed record NeuralPcmRequest(PcmAudio Source, NeuralOpenVoiceSession.OwnedReferences References, bool Preparation);

// Only successful responses authorize the parent to omit reference PCM. This
// state belongs to one owned worker and is cleared before every stop/restart.
internal sealed class NeuralReferenceAcknowledgement
{
    private byte[]? key;
    internal bool CanReuse(byte[] fingerprint)
    {
        if (key is not null && fingerprint.AsSpan().SequenceEqual(key)) return true;
        key = null; return false;
    }
    internal void Acknowledge(byte[] fingerprint) => key = (byte[])fingerprint.Clone();
    internal void Clear() => key = null;
}

// Full frames are newly allocated by the reader, so the worker can transfer
// their ownership here without another whole-bank copy. No caller PCM or file
// handle is retained, and replacing/disposing drops the previous bank.
internal sealed class NeuralPcmReferenceCache : IDisposable
{
    private NeuralOpenVoiceSession.OwnedReferences? clips;
    internal void Commit(NeuralPcmRequest request)
    {
        clips = request.References;
    }
    internal NeuralOpenVoiceSession.OwnedReferences Resolve(ReadOnlySpan<byte> fingerprint, int count, int samples)
    {
        if (clips is null || !clips.Matches(fingerprint, count, samples))
            throw new InvalidDataException("NEURAL_REFERENCE_CACHE_MISS");
        return clips;
    }
    public void Dispose() { clips = null; }
}

// v2 full NVCQ/NVPQ and cached NVCK/NVPK carry rate/count/reference count/total
// then a 32-byte content fingerprint. Cached requests omit all reference bodies.
// NVCR/NVPR retain their v1 response layout. Counts are checked before allocation.
internal static class NeuralPcmProtocol
{
    internal const int SourceRate = 48000, ReferenceRate = 44100;
    internal const int MaximumSourceSamples = SourceRate * 45, MaximumReferenceSamples = ReferenceRate * 30;
    internal const int MaximumReferenceCount = 149, MaximumTotalReferenceSamples = ReferenceRate * 180;
    internal const int RequestHeaderBytes = 56, ResponseHeaderBytes = 20;
    internal const int MaximumRequestBytes = RequestHeaderBytes + MaximumSourceSamples * 2 + MaximumReferenceCount * 4 + MaximumTotalReferenceSamples * 2;

    internal static Task WriteRequestAsync(Stream output, PcmAudio source, IReadOnlyList<PcmAudio> references, CancellationToken ct,
        byte[]? referenceFingerprint = null, bool reuseReferences = false) =>
        WriteRequestCoreAsync(output, source, references, false, ct, referenceFingerprint, reuseReferences);

    internal static Task WritePreparationAsync(Stream output, IReadOnlyList<PcmAudio> references, CancellationToken ct,
        byte[]? referenceFingerprint = null, bool reuseReferences = false) =>
        WriteRequestCoreAsync(output, new PcmAudio([], SourceRate), references, true, ct, referenceFingerprint, reuseReferences);

    private static async Task WriteRequestCoreAsync(Stream output, PcmAudio source, IReadOnlyList<PcmAudio> references, bool preparation,
        CancellationToken ct, byte[]? referenceFingerprint, bool reuseReferences)
    {
        ct.ThrowIfCancellationRequested();
        var total = preparation ? ValidateReferenceRequest(references) : ValidateRequest(source, references);
        var key = referenceFingerprint ?? NeuralOpenVoiceSession.FingerprintReferences(references, ct);
        if (key.Length != 32) throw Invalid();
        var header = new byte[RequestHeaderBytes];
        (preparation ? (reuseReferences ? "NVPK"u8 : "NVPQ"u8) : (reuseReferences ? "NVCK"u8 : "NVCQ"u8)).CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 2); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), source.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), source.Samples.Length); BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(16), references.Count);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), total);
        key.CopyTo(header, 24);
        await output.WriteAsync(header, ct).ConfigureAwait(false);
        if (source.Samples.Length != 0 || !reuseReferences)
        {
            var buffer = new byte[8192];
            await WriteSamplesAsync(output, source.Samples, buffer, ct).ConfigureAwait(false);
            if (!reuseReferences) foreach (var item in references)
            {
                BinaryPrimitives.WriteInt32LittleEndian(buffer, item.Samples.Length);
                await output.WriteAsync(buffer.AsMemory(0, sizeof(int)), ct).ConfigureAwait(false);
                await WriteSamplesAsync(output, item.Samples, buffer, ct).ConfigureAwait(false);
            }
        }
        await output.FlushAsync(ct).ConfigureAwait(false);
    }

    internal static NeuralPcmRequest? ReadRequest(Stream input, NeuralPcmReferenceCache? cache = null)
    {
        Span<byte> header = stackalloc byte[RequestHeaderBytes];
        var first = input.ReadByte(); if (first < 0) return null;
        header[0] = (byte)first; input.ReadExactly(header[1..]);
        var sourceRate = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        var sourceCount = BinaryPrimitives.ReadInt32LittleEndian(header[12..]);
        var references = BinaryPrimitives.ReadInt32LittleEndian(header[16..]); var total = BinaryPrimitives.ReadInt32LittleEndian(header[20..]);
        var cached = header[..4].SequenceEqual("NVCK"u8) || header[..4].SequenceEqual("NVPK"u8);
        var preparation = header[..4].SequenceEqual("NVPQ"u8) || header[..4].SequenceEqual("NVPK"u8);
        if ((!cached && !preparation && !header[..4].SequenceEqual("NVCQ"u8)) || BinaryPrimitives.ReadInt32LittleEndian(header[4..]) != 2 || sourceRate != SourceRate ||
            (preparation ? sourceCount != 0 : sourceCount is < 1 or > MaximumSourceSamples) || references is < 1 or > MaximumReferenceCount || total is < 1 or > MaximumTotalReferenceSamples || total < references)
            throw Invalid();
        var key = header[24..].ToArray();
        // Resolve before allocating donor PCM; unknown keys never trigger a
        // full-frame fallback or consume a different bank's reference bodies.
        var retained = cached ? (cache ?? throw Invalid()).Resolve(key, references, total) : null;
        var source = new PcmAudio(ReadSamples(input, sourceCount), sourceRate);
        if (retained is not null)
        {
            // Resolve returns the same privately owned, fully verified bank.
            // Only the donor PCM belongs to this new frame.
            if (!preparation) NeuralOpenVoiceSession.ValidateSource(source);
            return new(source, retained, preparation);
        }
        var owned = NeuralOpenVoiceSession.OwnedReferences.ReadVerified(input, references, total, key, source, preparation);
        return new(source, owned, preparation);
    }

    internal static int ValidateRequest(PcmAudio source, IReadOnlyList<PcmAudio> references)
    {
        if (source is null || source.Samples is null || source.SampleRate != SourceRate || source.Samples.Length is < 1 or > MaximumSourceSamples) throw Invalid();
        NeuralOpenVoiceSession.ValidateInputs(source, references);
        return ValidateReferenceFrame(references);
    }
    internal static int ValidateReferenceRequest(IReadOnlyList<PcmAudio> references)
    {
        var total = ValidateReferenceFrame(references);
        NeuralOpenVoiceSession.ValidateReferences(references);
        return total;
    }
    private static int ValidateReferenceFrame(IReadOnlyList<PcmAudio> references)
    {
        if (references is null || references.Count is < 1 or > MaximumReferenceCount) throw Invalid();
        var total = 0;
        foreach (var item in references)
        {
            if (item is null || item.Samples is null || item.SampleRate != ReferenceRate || item.Samples.Length is < 1 or > MaximumReferenceSamples ||
                (long)total + item.Samples.Length > MaximumTotalReferenceSamples) throw Invalid();
            total += item.Samples.Length;
        }
        return total;
    }

    internal static void WritePreparationResponse(Stream output, int status)
    {
        if (status is < 0 or > 7) throw Invalid();
        Span<byte> header = stackalloc byte[ResponseHeaderBytes]; header.Clear(); "NVPR"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 1); BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        output.Write(header); output.Flush();
    }
    internal static async Task ReadPreparationResponseAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[ResponseHeaderBytes]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (!header.AsSpan(0, 4).SequenceEqual("NVPR"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 1 ||
            status is < 0 or > 7 || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != 0 ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16)) != 0) throw Invalid();
        if (status != 0) throw new NeuralWorkerException(status);
    }

    internal static async Task<PcmAudio> ReadResponseAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[ResponseHeaderBytes]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)); var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (!header.AsSpan(0, 4).SequenceEqual("NVCR"u8) || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4)) != 1 || status is < 0 or > 7) throw Invalid();
        if (status != 0) { if (rate != 0 || count != 0) throw Invalid(); throw new NeuralWorkerException(status); }
        if (rate != ReferenceRate || count is < 1 or > ReferenceRate * 45) throw Invalid();
        var samples = new short[count];
        var buffer = new byte[Math.Min(count * sizeof(short), 32 * 1024)];
        for (var offset = 0; offset < count;)
        {
            ct.ThrowIfCancellationRequested();
            var length = Math.Min(buffer.Length / sizeof(short), count - offset);
            var byteLength = length * sizeof(short);
            await input.ReadExactlyAsync(buffer.AsMemory(0, byteLength), ct).ConfigureAwait(false);
            buffer.AsSpan(0, byteLength).CopyTo(MemoryMarshal.AsBytes(samples.AsSpan(offset, length)));
            if (!BitConverter.IsLittleEndian)
                BinaryPrimitives.ReverseEndianness(samples.AsSpan(offset, length), samples.AsSpan(offset, length));
            offset += length;
        }
        ct.ThrowIfCancellationRequested();
        var audio = new PcmAudio(samples, rate); ValidateOutput(audio); return audio;
    }
    internal static void WriteResponse(Stream output, int status, PcmAudio? audio)
    {
        if (status is < 0 or > 7 || (status == 0 ? audio is null : audio is not null)) throw Invalid();
        if (audio is not null) ValidateOutput(audio);
        Span<byte> header = stackalloc byte[ResponseHeaderBytes]; header.Clear(); "NVCR"u8.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], 1); BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], audio?.SampleRate ?? 0); BinaryPrimitives.WriteInt32LittleEndian(header[16..], audio?.Samples.Length ?? 0);
        output.Write(header);
        if (audio is not null && BitConverter.IsLittleEndian)
            output.Write(MemoryMarshal.AsBytes(audio.Samples.AsSpan()));
        else if (audio is not null)
        {
            var bytes = new byte[audio.Samples.Length * 2];
            for (var i = 0; i < audio.Samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), audio.Samples[i]);
            output.Write(bytes);
        }
        output.Flush();
    }
    internal static void ValidateOutput(PcmAudio? audio)
    {
        if (audio is null || audio.SampleRate != ReferenceRate || audio.Samples is null ||
            audio.Samples.Length is < 1 or > ReferenceRate * 45) throw Invalid();
        ReadOnlySpan<short> samples = audio.Samples;
        var first = samples.IndexOfAnyExcept((short)0);
        if (first < 0 || samples[first..].IndexOfAnyExceptInRange((short)-30147, (short)30147) >= 0) throw Invalid();
    }
    internal static short[] ReadSamples(Stream input, int count)
    {
        var result = new short[count];
        input.ReadExactly(MemoryMarshal.AsBytes(result.AsSpan()));
        if (!BitConverter.IsLittleEndian)
            BinaryPrimitives.ReverseEndianness(result.AsSpan(), result.AsSpan());
        return result;
    }
    private static async Task WriteSamplesAsync(Stream output, short[] samples, byte[] buffer, CancellationToken ct)
    {
        for (var start = 0; start < samples.Length;)
        {
            ct.ThrowIfCancellationRequested(); var count = Math.Min(buffer.Length / 2, samples.Length - start);
            if (BitConverter.IsLittleEndian)
                MemoryMarshal.AsBytes(samples.AsSpan(start, count)).CopyTo(buffer);
            else
                for (var i = 0; i < count; i++) BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(i * 2), samples[start + i]);
            await output.WriteAsync(buffer.AsMemory(0, count * 2), ct).ConfigureAwait(false); start += count;
        }
    }
    private static InvalidDataException Invalid() => new("NEURAL_PROTOCOL: 声質変換ワーカーの形式または上限が不正です。");
}
