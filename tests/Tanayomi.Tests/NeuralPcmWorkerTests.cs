using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tanayomi;
using Tanayomi.Desktop;

internal static class NeuralPcmWorkerTests
{
    private static void CheckReferencePeak(Action<bool, string> check)
    {
        var width = System.Numerics.Vector<float>.Count;
        var random = new Random(82429);
        foreach (var length in new[] { 1, width - 1, width, width + 1, width * 2 + 1, 4096, 123948 })
        {
            var samples = Enumerable.Range(0, length).Select(_ => (float)(random.NextDouble() * 2 - 1)).ToArray();
            foreach (var position in new[] { 0, length / 2, length - 1 }.Distinct())
            {
                var original = samples[position];
                samples[position] = -1.25f;
                var expected = samples.Max(value => Math.Abs(value));
                check(BitConverter.SingleToInt32Bits(NeuralVoiceReferences.ReferencePeak(samples)) ==
                    BitConverter.SingleToInt32Bits(expected), "reference peak preserves scalar maximum bits across vector lanes and tails");
                samples[position] = original;
            }
        }
        check(NeuralVoiceReferences.ReferencePeak(new float[width + 1]) == 0 &&
            NeuralVoiceReferences.ReferencePeak([-0f, 0f, -float.Epsilon]) == float.Epsilon,
            "reference peak retains zero and subnormal levels without changing reference gain policy");
        foreach (var value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        for (var position = 0; position <= width * 2; position++)
        {
            var samples = Enumerable.Repeat(0.5f, width * 2 + 1).ToArray();
            samples[position] = value;
            check(Capture(() => NeuralVoiceReferences.ReferencePeak(samples)) is InvalidDataException error &&
                error.Message == "NEURAL_REFERENCE_SILENT", "reference peak rejects every nonfinite lane and tail with its existing error identifier");
        }
    }

    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckReferencePeak(check);
        CheckVerifiedModelBoundary(check);
        CheckHierarchicalConversionKey(check);
        var source = Audio(48000, 4800); var reference = Audio(44100, 4410); var converted = Audio(44100, 4410);
        using var request = new MemoryStream();
        await NeuralPcmProtocol.WriteRequestAsync(request, source, [reference], default);
        var bytes = request.ToArray(); request.Position = 0;
        var decoded = NeuralPcmProtocol.ReadRequest(request)!;
        check(decoded.Source.Samples.SequenceEqual(source.Samples) && decoded.References.Count == 1 &&
            decoded.References.CopyForInspection()[0].Samples.SequenceEqual(reference.Samples) && request.Position == request.Length &&
            bytes.Length == NeuralPcmProtocol.RequestHeaderBytes + source.Samples.Length * 2 + 4 + reference.Samples.Length * 2,
            "neural binary request preserves exact donor and reference PCM without text or filesystem paths");
        foreach (var offset in new[] { 0, 4, 8, 12, 16, 20, NeuralPcmProtocol.RequestHeaderBytes + source.Samples.Length * 2 })
        {
            var invalid = (byte[])bytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(offset), int.MaxValue);
            check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(invalid))) is InvalidDataException,
                "neural request rejects invalid magic version rate or oversized counts before body allocation");
        }
        check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(bytes[..^1]))) is EndOfStreamException &&
            NeuralPcmProtocol.ReadRequest(new MemoryStream()) is null,
            "neural request distinguishes clean EOF from a truncated PCM frame");
        var large = Audio(44100, 44100 * 30);
        check(Capture(() => NeuralPcmProtocol.ValidateRequest(source, Enumerable.Repeat(large, 7).ToArray())) is InvalidDataException &&
            Capture(() => NeuralPcmProtocol.ValidateRequest(source, Enumerable.Repeat(reference, 150).ToArray())) is InvalidDataException,
            "neural transport rejects reference totals above 180 seconds and counts above 149 without starting native inference");
        using var response = new MemoryStream(); NeuralPcmProtocol.WriteResponse(response, 0, converted); response.Position = 0;
        check((await NeuralPcmProtocol.ReadResponseAsync(response, default)).Samples.SequenceEqual(converted.Samples),
            "neural binary response returns exact bounded 44100Hz PCM");
        var responseBytes = response.ToArray();
        var limits = new short[1025];
        limits[1000] = -30147; limits[^1] = 30147;
        NeuralPcmProtocol.ValidateOutput(new(limits, 44100));
        foreach (var outside in new short[] { -30148, 30148, short.MinValue })
        {
            limits[^1] = outside;
            check(Capture(() => NeuralPcmProtocol.ValidateOutput(new(limits, 44100))) is InvalidDataException,
                "a late out-of-range PCM sample is rejected even after a valid negative peak and leading silence");
        }
        var multiChunk = Audio(44100, 32769);
        using var consecutive = new MemoryStream();
        NeuralPcmProtocol.WriteResponse(consecutive, 0, multiChunk);
        var boundary = consecutive.Position;
        NeuralPcmProtocol.WriteResponse(consecutive, 0, converted);
        consecutive.Position = 0;
        check((await NeuralPcmProtocol.ReadResponseAsync(consecutive, default)).Samples.SequenceEqual(multiChunk.Samples) &&
            consecutive.Position == boundary && (await NeuralPcmProtocol.ReadResponseAsync(consecutive, default)).Samples.SequenceEqual(converted.Samples),
            "multi-block neural PCM keeps the final short block and leaves the following response aligned");
        consecutive.SetLength(boundary - 1); consecutive.Position = 0;
        check(await CaptureAsync(() => NeuralPcmProtocol.ReadResponseAsync(consecutive, default)) is EndOfStreamException,
            "truncation in the final short PCM block cannot return partial speech");
        foreach (var offset in new[] { 0, 4, 8, 12, 16 })
        {
            var invalid = (byte[])responseBytes.Clone(); BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(offset), int.MaxValue);
            check(await CaptureAsync(() => NeuralPcmProtocol.ReadResponseAsync(new MemoryStream(invalid), default)) is InvalidDataException,
                "neural response rejects invalid status rate and declared extent before audio allocation");
        }
        using var failureResponse = new MemoryStream(); NeuralPcmProtocol.WriteResponse(failureResponse, 7, null); failureResponse.Position = 0;
        check(await CaptureAsync(() => NeuralPcmProtocol.ReadResponseAsync(failureResponse, default)) is NeuralWorkerException { Status: 7 } &&
            Capture(() => NeuralPcmProtocol.ValidateOutput(new(new short[4410], 44100))) is InvalidDataException,
            "neural terminal errors remain typed and silent output never becomes successful speech");
        await CheckAdapterAsync(source, reference, converted, check);
        await CheckDonorPreparationAsync(source, reference, converted, check);
        await CheckOwnedPreparationOverlapAsync(source, reference, converted, check);
        await CheckOwnedOverlapAsync(source, reference, converted, check);
        await CheckOwnedRepeatedTextAsync(source, reference, converted, check);
        await CheckSerialOverlapBoundariesAsync(source, reference, converted, check);
        await CheckLateReferenceReadAsync(source, reference, converted, check);
        using var preparation = new MemoryStream();
        await NeuralPcmProtocol.WritePreparationAsync(preparation, [reference], default); preparation.Position = 0;
        var preparedRequest = NeuralPcmProtocol.ReadRequest(preparation);
        check(preparedRequest is { Preparation: true } && preparedRequest.Source.Samples.Length == 0 &&
            preparedRequest.References.CopyForInspection()[0].Samples.SequenceEqual(reference.Samples),
            "neural reference preparation carries no dummy donor speech or synthesis request");
        using var preparedResponse = new MemoryStream(); NeuralPcmProtocol.WritePreparationResponse(preparedResponse, 0); preparedResponse.Position = 0;
        await NeuralPcmProtocol.ReadPreparationResponseAsync(preparedResponse, default);
        preparedResponse.Position = 0;
        check(await CaptureAsync(() => NeuralPcmProtocol.ReadResponseAsync(preparedResponse, default)) is InvalidDataException,
            "a preparation acknowledgement cannot be played as synthesized PCM");
        var badPreparation = preparation.ToArray(); BinaryPrimitives.WriteInt32LittleEndian(badPreparation.AsSpan(12), 1);
        check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(badPreparation))) is InvalidDataException,
            "preparation rejects a hidden donor payload before allocation");
        await CheckReferencesAsync(check);
        await CheckReferenceTransportAsync(source, reference, check);
        if (OperatingSystem.IsWindows())
        {
            await CheckOwnedWorkerRecoveryAsync(source, reference, check);
            await CheckStartErrorModeAsync(source, reference, check);
        }
        using var temporary = new TestDirectory();
        var data = Path.Combine(temporary.DirectoryPath, "data");
        var owned = Path.Combine(data, "tmp", "nv-0123456789ab"); Directory.CreateDirectory(owned);
        check(NeuralPcmWorker.ValidateTemporaryRoot(owned, data) == Path.GetFullPath(owned),
            "neural worker accepts only the existing owned nv temporary root under executable data/tmp");
        foreach (var invalid in new[] { Path.Combine(data, "tmp"), Path.Combine(data, "tmp", "nv-not-hex-root"),
            Path.Combine(temporary.DirectoryPath, "nv-0123456789ab"), Path.Combine(owned, "nv-0123456789ab") })
            check(Capture(() => NeuralPcmWorker.ValidateTemporaryRoot(invalid, data)) is InvalidDataException,
                "neural worker refuses shared missing nested or out-of-data temporary roots before loading native assets");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(await CaptureAsync(() => NeuralOpenVoiceAssets.CheckAvailableAsync(cancelled.Token)) is OperationCanceledException,
            "cancelled neural availability checks stop before inspecting or loading model assets");
        check(Capture(() => new DesktopNeuralUtau(temporary.DirectoryPath)) is InvalidDataException,
            "neural product-check override cannot expand ordinary reference access to an arbitrary directory");
    }

    private static void CheckHierarchicalConversionKey(Action<bool, string> check)
    {
        var source = Audio(48000, 4800); var first = Audio(44100, 4410); var second = Audio(44100, 8820);
        byte[] Key(PcmAudio donor, IReadOnlyList<PcmAudio> refs) => DesktopNeuralUtau.CreateConversionKey(donor, NeuralOpenVoiceSession.FingerprintReferences(refs));
        var original = Key(source, [first, second]);
        check(original.SequenceEqual(Key(new PcmAudio((short[])source.Samples.Clone(), 48000), [first, second])),
            "hierarchical conversion key is stable for identical private PCM and ordered references");
        var changedSource = new PcmAudio((short[])source.Samples.Clone(), source.SampleRate); changedSource.Samples[20]++;
        check(!original.SequenceEqual(Key(changedSource, [first, second])) &&
            !original.SequenceEqual(Key(new PcmAudio(source.Samples, 44100), [first, second])) &&
            !original.SequenceEqual(Key(new PcmAudio(source.Samples.Concat(new short[240]).ToArray(), 48000), [first, second])),
            "hierarchical conversion key retains donor content, rate and length identity");
        var changedReference = new PcmAudio((short[])first.Samples.Clone(), first.SampleRate); changedReference.Samples[20]++;
        var joined = first.Samples.Concat(second.Samples).ToArray();
        var shiftedBoundary = new[] { new PcmAudio(joined[..4409], 44100), new PcmAudio(joined[4409..], 44100) };
        check(!original.SequenceEqual(Key(source, [second, first])) && !original.SequenceEqual(Key(source, shiftedBoundary)) &&
            !original.SequenceEqual(Key(source, [changedReference, second])) &&
            !original.SequenceEqual(Key(source, [first, new PcmAudio(second.Samples, 48000)])),
            "hierarchical conversion key retains reference order, individual boundaries, content and rates");
        check(Capture(() => DesktopNeuralUtau.CreateConversionKey(source, new byte[31])) is InvalidDataException,
            "conversion key requires one complete SHA256 reference fingerprint");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        check(Capture(() => DesktopNeuralUtau.CreateConversionKey(source, new byte[32], stop.Token)) is OperationCanceledException,
            "hierarchical cache key never bypasses caller cancellation");
    }

    private static async Task CheckLateReferenceReadAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var bank = new Voicebank("late reference fixture", "unused", [], 0);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<IReadOnlyList<PcmAudio>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = new PcmAudio((short[])reference.Samples.Clone(), reference.SampleRate); changed.Samples[20]++;
        var reads = 0; var conversions = 0;
        await using var adapter = new DesktopNeuralUtau((_, _) => Task.FromResult(source), (_, _) =>
        {
            if (++reads == 1) { arrived.TrySetResult(); return late.Task; }
            return Task.FromResult<IReadOnlyList<PcmAudio>>([changed]);
        }, (_, _, _) => { conversions++; return Task.FromResult(converted); }, _ => Task.CompletedTask);
        try
        {
            using var stop = new CancellationTokenSource(); var abandoned = adapter.RenderAsync("遅延", bank, stop.Token);
            await arrived.Task.WaitAsync(TimeSpan.FromSeconds(3)); stop.Cancel();
            check(await CaptureAsync(() => abandoned.WaitAsync(TimeSpan.FromSeconds(3))) is OperationCanceledException,
                "cancelled reference provider cannot commit a partial conversion key");
            await adapter.RenderAsync("次", bank, default);
            late.TrySetResult([reference]);
            await adapter.RenderAsync("同PCM", bank, default);
            check(conversions == 1, "late completion of abandoned references cannot attach stale request data to the next cached conversion");
        }
        finally { late.TrySetResult([reference]); }
        var fail = false; conversions = 0;
        var mutableSource = new PcmAudio((short[])source.Samples.Clone(), source.SampleRate);
        await using var failed = new DesktopNeuralUtau((_, _) => Task.FromResult(mutableSource),
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), (_, _, _) =>
            { conversions++; return fail ? Task.FromException<PcmAudio>(new IOException("expected")) : Task.FromResult(converted); }, _ => Task.CompletedTask);
        await failed.RenderAsync("成功", bank, default);
        mutableSource.Samples[20]++; fail = true;
        check(await CaptureAsync(() => failed.RenderAsync("失敗", bank, default)) is IOException,
            "conversion failure never commits a newly computed request key");
        mutableSource.Samples[20]--; fail = false;
        await failed.RenderAsync("再開", bank, default);
        check(conversions == 3, "failed conversion discards the previous cached result before normal retry");
    }

    private static void CheckVerifiedModelBoundary(Action<bool, string> check)
    {
        check(Capture(() => NeuralVerifiedModel.ReadVerified(null!, NeuralModelKind.Reference)) is ArgumentNullException &&
            Capture(() => NeuralOpenVoiceSession.ValidateVerifiedModels(null, null)) is ArgumentNullException,
            "verified model boundary rejects null before native session initialization");
        using var empty = new MemoryStream();
        check(Capture(() => NeuralVerifiedModel.ReadVerified(empty, (NeuralModelKind)123)) is InvalidDataException && empty.Position == 0,
            "verified models reject unknown kind without reading caller input");
        check(Capture(() => NeuralVerifiedModel.ReadVerified(empty, NeuralModelKind.Reference)) is InvalidDataException && empty.CanRead,
            "verified model size rejection leaves stream ownership with its pinning caller");
        using var disposedStream = new MemoryStream(); disposedStream.Dispose();
        check(Capture(() => NeuralVerifiedModel.ReadVerified(disposedStream, NeuralModelKind.Reference)) is InvalidDataException,
            "verified models reject a disposed input stream before allocating model storage");
        using var damaged = new MemoryStream(new byte[NeuralOpenVoiceSession.ReferenceBytes]);
        check(Capture(() => NeuralVerifiedModel.ReadVerified(damaged, NeuralModelKind.Reference)) is InvalidDataException && damaged.CanRead,
            "verified models reject a fixed-size payload with the wrong hash without transferring stream ownership");
        using var wrongKindSize = new MemoryStream(new byte[NeuralOpenVoiceSession.ReferenceBytes]);
        check(Capture(() => NeuralVerifiedModel.ReadVerified(wrongKindSize, NeuralModelKind.Converter)) is InvalidDataException && wrongKindSize.Position == 0,
            "model kinds have fixed independent size contracts, never caller-selected hash or size");
        using var truncated = new AdvertisedModelStream([], NeuralOpenVoiceSession.ReferenceBytes);
        check(Capture(() => NeuralVerifiedModel.ReadVerified(truncated, NeuralModelKind.Reference)) is EndOfStreamException,
            "verified model input still rejects truncated payload after a declared-length check");
        using var stop = new CancellationTokenSource(); stop.Cancel();
        check(Capture(() => NeuralVerifiedModel.ReadVerified(empty, NeuralModelKind.Reference, stop.Token)) is OperationCanceledException,
            "verified model cancellation wins before stream access or allocation");
        using var during = new CancellationTokenSource();
        using var interrupted = new AdvertisedModelStream(new byte[65536], NeuralOpenVoiceSession.ReferenceBytes, during);
        check(Capture(() => NeuralVerifiedModel.ReadVerified(interrupted, NeuralModelKind.Reference, during.Token)) is OperationCanceledException && interrupted.CanRead,
            "verified model reading observes cancellation between bounded chunks and retains caller stream ownership");
        var type = typeof(NeuralVerifiedModel);
        var instanceFlags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        check(type.GetConstructors(instanceFlags).All(constructor => constructor.IsPrivate) &&
            type.GetMethods(instanceFlags).All(method => method.ReturnType != typeof(byte[])) &&
            type.GetFields(instanceFlags).Where(field => field.FieldType == typeof(byte[])).All(field => field.IsPrivate),
            "verified model construction is private and no instance API exposes its owned byte array");
        using var assets = new NeuralOpenVoiceAssets(); assets.Dispose();
        check(Capture(() => { _ = assets.Reference; }) is ObjectDisposedException &&
            Capture(() => { _ = assets.Converter; }) is ObjectDisposedException,
            "disposed or incomplete assets cannot expose a model as verified");
    }

    private sealed class AdvertisedModelStream(byte[] content, long advertisedLength, CancellationTokenSource? cancel = null) : MemoryStream(content)
    {
        public override long Length => advertisedLength;
        public override int Read(Span<byte> buffer)
        {
            var count = base.Read(buffer); cancel?.Cancel(); return count;
        }
    }

    private static async Task CheckOwnedOverlapAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
        var bank = new Voicebank("overlap fixture", "unused", [], 0);
        foreach (var mode in new[] { "success", "reference-failure", "sync-donor-failure", "sync-donor-only", "async-donor-only", "cancel", "dispose" })
        {
            var donorStarted = Signal(); var referenceStarted = Signal(); var release = Signal(); var donorJoined = Signal();
            var cleanupEntered = Signal(); var cleanupRelease = Signal(); var exactInputs = false;
            var donor = new DesktopOpenJtalkSpeech(new UnusedPcmOutput());
            var calls = 0;
            var adapter = new DesktopNeuralUtau((_, ct) =>
            {
                if (++calls > 1) return Task.FromResult(source);
                donorStarted.TrySetResult();
                if (mode is "sync-donor-failure" or "sync-donor-only") throw new IOException("donor fixture");
                return Pending(ct);
            }, (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
                (input, refs, _) =>
                {
                    exactInputs = ReferenceEquals(input.Samples, source.Samples) && input.SampleRate == source.SampleRate && input.Samples.SequenceEqual(source.Samples) &&
                        refs.Count == 1 && refs[0].SampleRate == reference.SampleRate && refs[0].Samples.SequenceEqual(reference.Samples);
                    return Task.FromResult(converted);
                }, _ => Task.CompletedTask);
            async Task<PcmAudio> Pending(CancellationToken ct)
            {
                try
                {
                    await release.Task.WaitAsync(ct);
                    if (mode == "async-donor-only") throw new IOException("asynchronous donor fixture");
                    return source;
                }
                finally
                {
                    if (mode is "reference-failure" or "cancel" or "dispose")
                    { cleanupEntered.TrySetResult(); await cleanupRelease.Task; }
                    donorJoined.TrySetResult();
                }
            }
            // The process is only a liveness sentinel: clear it before disposing
            // this fake donor; no child is started or terminated by this fixture.
            using var sentinel = System.Diagnostics.Process.GetCurrentProcess();
            typeof(DesktopOpenJtalkSpeech).GetField("worker", flags)!.SetValue(donor, sentinel);
            typeof(DesktopOpenJtalkSpeech).GetField("workerReady", flags)!.SetValue(donor, true);
            typeof(DesktopNeuralUtau).GetField("ownedDonor", flags)!.SetValue(adapter, donor);
            Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>> read = async (_, ct) =>
            {
                referenceStarted.TrySetResult();
                await donorStarted.Task.WaitAsync(ct);
                if (mode is "reference-failure" or "sync-donor-failure") throw new NeuralReferenceException(2);
                return new([reference], new string('A', 64));
            };
            typeof(DesktopNeuralUtau).GetField("ownedReferences", flags)!.SetValue(adapter, read);
            using var stop = new CancellationTokenSource();
            try
            {
                check(donor.IsPrepared, "owned overlap requires live worker and successful preparation ACK");
                var pending = adapter.RenderAsync("重ね合わせ", bank, stop.Token);
                await referenceStarted.Task.WaitAsync(TimeSpan.FromSeconds(3));
                if (mode is "success" or "async-donor-only") release.TrySetResult();
                if (mode == "cancel") stop.Cancel();
                var disposal = mode == "dispose" ? adapter.DisposeAsync().AsTask() : Task.CompletedTask;
                Task<PcmAudio>? queued = null;
                if (mode is "reference-failure" or "cancel" or "dispose")
                {
                    await cleanupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                    check(!pending.IsCompleted && (mode != "dispose" || !disposal.IsCompleted),
                        "Render and Dispose await donor finally cleanup: " + mode);
                    if (mode != "dispose")
                    {
                        queued = adapter.RenderAsync("ゲート待機", bank, default);
                        check(!queued.IsCompleted && calls == 1, "donor cleanup retains renderer gate: " + mode);
                    }
                    cleanupRelease.TrySetResult();
                }
                var error = await CaptureAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                await disposal.WaitAsync(TimeSpan.FromSeconds(3));
                check(mode == "success" ? error is null : mode is "cancel" or "dispose" ? error is OperationCanceledException : mode is "sync-donor-only" or "async-donor-only" ? error is IOException : error is NeuralReferenceException,
                    "owned overlap preserves reference-first failures, synchronous donor faults, and cancellation: " + mode);
                check(mode is "sync-donor-failure" or "sync-donor-only" || donorJoined.Task.IsCompleted,
                    "owned overlap joins donor completion before releasing renderer: " + mode);
                if (mode == "success")
                    check(exactInputs && pending.Result.Samples.SequenceEqual(converted.Samples) && pending.Result.SampleRate == converted.SampleRate,
                        "owned overlap transfers donor PCM without another clone and preserves every reference sample and exact converted PCM");
                if (queued is not null) await CaptureAsync(() => queued.WaitAsync(TimeSpan.FromSeconds(3)));
                if (mode is "reference-failure" or "sync-donor-failure" or "sync-donor-only" or "async-donor-only" or "cancel")
                {
                    typeof(DesktopNeuralUtau).GetField("ownedReferences", flags)!.SetValue(adapter,
                        (Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>>)((_, _) => Task.FromResult(new NeuralReferenceSnapshot([reference], new string('B', 64)))));
                    check((await adapter.RenderAsync("復旧", bank, default)).Samples.SequenceEqual(converted.Samples),
                        "owned overlap recovers after failed or cancelled pair: " + mode);
                }
                typeof(DesktopOpenJtalkSpeech).GetField("workerReady", flags)!.SetValue(donor, false);
                check(!donor.IsPrepared, "unprepared donor cannot enter overlap");
            }
            finally
            {
                release.TrySetResult(); cleanupRelease.TrySetResult();
                typeof(DesktopOpenJtalkSpeech).GetField("worker", flags)!.SetValue(donor, null);
                await adapter.DisposeAsync(); await donor.DisposeAsync();
            }
        }
    }
    private static async Task CheckOwnedRepeatedTextAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        var donor = new DesktopOpenJtalkSpeech(new UnusedPcmOutput());
        using var sentinel = Process.GetCurrentProcess();
        void Set(string field, object? value) => typeof(DesktopOpenJtalkSpeech).GetField(field, flags)!.SetValue(donor, value);
        var generation = new object();
        Set("worker", sentinel); Set("workerReady", true); Set("workerGeneration", generation);
        var syntheses = 0; var reads = 0; var conversions = 0; var availability = 0;
        var referenceVersion = 'A'; var currentReference = reference;
        Action? duringRead = null; var failRead = false; var failAvailable = false;
        var adapter = new DesktopNeuralUtau((_, _) => { syntheses++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
            (_, _, _) => { conversions++; return Task.FromResult(converted); },
            _ => { availability++; if (failAvailable) throw new IOException(); return Task.CompletedTask; });
        typeof(DesktopNeuralUtau).GetField("ownedDonor", flags)!.SetValue(adapter, donor);
        typeof(DesktopNeuralUtau).GetField("ownedReferences", flags)!.SetValue(adapter,
            (Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>>)((_, _) =>
            {
                reads++; duringRead?.Invoke();
                if (failRead) throw new NeuralReferenceException(2);
                return Task.FromResult(new NeuralReferenceSnapshot([currentReference], new string(referenceVersion, 64)));
            }));
        var bank = new Voicebank("repeat fixture", "unused", [], 0);
        try
        {
            check(ReferenceEquals(donor.PreparedGeneration, generation), "ready donor exposes one opaque process generation");
            var first = await adapter.RenderAsync("同文", bank, default);
            var second = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 1 && conversions == 1 && reads == 2 && availability == 2 &&
                second.Samples.SequenceEqual(first.Samples) && adapter.LastTimings?.DonorMilliseconds == 0,
                "same owned text skips donor only after fresh references and availability, preserving exact PCM");
            second.Samples[0] ^= 123;
            var third = await adapter.RenderAsync("同文", bank, default);
            check(third.Samples.SequenceEqual(converted.Samples) && syntheses == 1,
                "repeated-text output never lends the retained conversion array");
            _ = await adapter.RenderAsync("別文", bank, default);
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 3, "repeated-text memo retains only the immediately preceding text");
            currentReference = new((short[])reference.Samples.Clone(), reference.SampleRate);
            currentReference.Samples[0] ^= 321; referenceVersion = 'B';
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 4 && conversions == 2, "same text with changed reference content regenerates and converts");
            Set("workerGeneration", new object());
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 5, "restarted donor generation cannot reuse text proof from the preceding process");
            duringRead = () => Set("workerGeneration", new object());
            _ = await adapter.RenderAsync("同文", bank, default); duringRead = null;
            check(syntheses == 6, "donor generation is checked again after asynchronous reference work");
            Set("workerReady", false);
            check(donor.PreparedGeneration is null, "unprepared donor exposes no generation capability");
            _ = await adapter.RenderAsync("同文", bank, default);
            Set("workerReady", true);
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 8, "unprepared request cannot publish a repeated-text proof");
            Set("workerStopping", true);
            check(donor.PreparedGeneration is null, "stopping donor cannot authorize cached text");
            Set("workerStopping", false);
            failRead = true;
            check(await CaptureAsync(() => adapter.RenderAsync("同文", bank, default)) is NeuralReferenceException,
                "cached text still surfaces reference read failure");
            failRead = false;
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 9 && conversions == 3, "reference failure discards both text proof and successful conversion");
            using (var stop = new CancellationTokenSource())
            {
                duringRead = stop.Cancel;
                check(await CaptureAsync(() => adapter.RenderAsync("同文", bank, stop.Token)) is OperationCanceledException,
                    "cancellation during reference validation cannot return retained speech");
                duringRead = null;
            }
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 10 && conversions == 4, "cancelled repeated request clears cache before recovery");
            failAvailable = true;
            check(await CaptureAsync(() => adapter.RenderAsync("同文", bank, default)) is IOException,
                "repeated text cannot bypass required asset availability failure");
            failAvailable = false;
            _ = await adapter.RenderAsync("同文", bank, default);
            check(syntheses == 11 && conversions == 5, "availability failure invalidates the repeated result");
            Set("worker", null);
            check(donor.PreparedGeneration is null, "missing donor process invalidates a retained generation");
            await adapter.DisposeAsync();
            check(await CaptureAsync(() => adapter.RenderAsync("同文", bank, default)) is ObjectDisposedException,
                "disposed renderer cannot return a retained repeat");
        }
        finally
        {
            // This is only a liveness sentinel, never an owned test child.
            Set("worker", null); duringRead = null;
            await adapter.DisposeAsync(); await donor.DisposeAsync();
        }
        var injectedCalls = 0; var injectedConversions = 0;
        await using var injected = new DesktopNeuralUtau((_, _) => { injectedCalls++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
            (_, _, _) => { injectedConversions++; return Task.FromResult(converted); }, _ => Task.CompletedTask);
        _ = await injected.RenderAsync("同文", bank, default); _ = await injected.RenderAsync("同文", bank, default);
        check(injectedCalls == 2, "arbitrary injected donors still synthesize on every identical-text request");
        check(injectedConversions == 1, "identical donor PCM reuses one conversion within the same drawn-out mode");
        _ = await injected.RenderAsync("同文", bank, true, default);
        _ = await injected.RenderAsync("同文", bank, true, default);
        _ = await injected.RenderAsync("同文", bank, false, default);
        check(injectedConversions == 3, "changing drawn-out mode invalidates conversion memo in both directions even for identical donor PCM");
    }

    private static async Task CheckSerialOverlapBoundariesAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var privateUnprepared in new[] { false, true })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var calls = 0;
            var donor = new DesktopOpenJtalkSpeech(new UnusedPcmOutput());
            var adapter = new DesktopNeuralUtau((_, _) => { calls++; return Task.FromResult(source); },
                async (_, ct) => { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new[] { reference }; },
                (_, _, _) => Task.FromResult(converted), _ => Task.CompletedTask);
            if (privateUnprepared)
            {
                typeof(DesktopNeuralUtau).GetField("ownedDonor", flags)!.SetValue(adapter, donor);
                typeof(DesktopNeuralUtau).GetField("ownedReferences", flags)!.SetValue(adapter,
                    (Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>>)(async (_, ct) =>
                    { entered.TrySetResult(); await release.Task.WaitAsync(ct); return new([reference], new string('C', 64)); }));
            }
            try
            {
                var pending = adapter.RenderAsync("直列", new Voicebank("serial", "unused", [], 0), default);
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(calls == 0 && !pending.IsCompleted, "reference barrier prevents donor start for " + (privateUnprepared ? "unprepared private donor" : "injected provider"));
                release.TrySetResult();
                check((await pending.WaitAsync(TimeSpan.FromSeconds(3))).Samples.SequenceEqual(converted.Samples) && calls == 1,
                    "serial boundary resumes exact conversion after reference completion");
            }
            finally { release.TrySetResult(); await adapter.DisposeAsync(); await donor.DisposeAsync(); }
        }
    }

    private sealed class UnusedPcmOutput : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct) => throw new InvalidOperationException("No playback expected");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static async Task CheckOwnedPreparationOverlapAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        if (Environment.ProcessorCount < 8) return;
        foreach (var outcome in new[] { "success", "cancel", "failure" })
        {
            var donorEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var converterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var donorFinished = 0; var synthesis = 0;
            await using var donor = new DesktopOpenJtalkSpeech(new UnusedPcmOutput(),
                (_, _) => { synthesis++; return Task.FromResult(source); }, async ct =>
                {
                    donorEntered.TrySetResult();
                    try { await release.Task.WaitAsync(ct); }
                    finally { Interlocked.Exchange(ref donorFinished, 1); }
                });
            await using var adapter = new DesktopNeuralUtau((_, _) => { synthesis++; return Task.FromResult(source); },
                (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
                (_, _, _) => Task.FromResult(converted), _ => Task.CompletedTask,
                prepare: async (_, ct) =>
                {
                    converterEntered.TrySetResult();
                    await donorEntered.Task.WaitAsync(ct);
                    if (outcome == "failure") throw new IOException("expected preparation failure");
                    await release.Task.WaitAsync(ct);
                }, prepareDonor: donor.PrepareAsync);
            typeof(DesktopNeuralUtau).GetField("ownedDonor",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(adapter, donor);
            using var stop = new CancellationTokenSource();
            var pending = adapter.PrepareAsync(new Voicebank("overlap fixture", "unused", [], 0), stop.Token);
            try
            {
                await Task.WhenAll(donorEntered.Task, converterEntered.Task).WaitAsync(TimeSpan.FromSeconds(3));
                if (outcome != "failure") check(!pending.IsCompleted, "private donor and converter both enter preparation before either finishes");
                if (outcome == "cancel") stop.Cancel();
                if (outcome == "success") release.TrySetResult();
                var error = await CaptureAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
                check(outcome switch
                {
                    "success" => error is null,
                    "cancel" => error is OperationCanceledException,
                    _ => error is IOException
                }, "overlapped preparation preserves " + outcome + " outcome");
                check(Volatile.Read(ref donorFinished) == 1 && synthesis == 0,
                    "preparation joins private donor cleanup without synthesizing dummy speech");
            }
            finally
            {
                stop.Cancel(); release.TrySetResult();
                _ = await CaptureAsync(() => pending.WaitAsync(TimeSpan.FromSeconds(3)));
            }
        }
    }

    private static async Task CheckDonorPreparationAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var bank = new Voicebank("donor prepare fixture", "unused", [], 0);
        var attempts = 0; var syntheses = 0; var conversions = 0;
        await using (var adapter = new DesktopNeuralUtau((_, _) => { syntheses++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
            (_, _, _) => { conversions++; return Task.FromResult(converted); }, _ => Task.CompletedTask,
            prepareDonor: _ => ++attempts == 1 ? Task.FromException(new IOException("private-donor-model-path")) : Task.CompletedTask))
        {
            var failure = await CaptureAsync(() => adapter.PrepareAsync(bank, default));
            check(failure is IOException && !failure.ToString().Contains("private-donor-model-path", StringComparison.Ordinal) && syntheses == 0 && conversions == 0,
                "OpenVoice donor initialization failure remains a sanitized preparation failure with no dummy conversion or speech");
            check((await adapter.RenderAsync("再試行", bank, default)).Samples.SequenceEqual(converted.Samples) && syntheses == 1,
                "ordinary OpenVoice speech retries normally after donor preparation failure");
            await adapter.PrepareAsync(bank, default);
            check(attempts == 2 && syntheses == 1, "OpenVoice retries failed donor initialization rather than caching false readiness");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); attempts = 0; syntheses = 0;
        var owner = new Owner();
        var gated = new DesktopNeuralUtau((_, _) => { syntheses++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), (_, _, _) => Task.FromResult(converted), _ => Task.CompletedTask, owner,
            prepareDonor: async ct => { if (++attempts == 1) { entered.TrySetResult(); await release.Task.WaitAsync(ct); } });
        try
        {
            using var stop = new CancellationTokenSource(); var preparation = gated.PrepareAsync(bank, stop.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var pendingSpeech = gated.RenderAsync("待機", bank, default);
            check(syntheses == 0 && !pendingSpeech.IsCompleted, "OpenVoice donor preparation shares its gate with direct speech conversion");
            stop.Cancel();
            check(await CaptureAsync(() => preparation.WaitAsync(TimeSpan.FromSeconds(3))) is OperationCanceledException,
                "active OpenVoice donor preparation cancellation does not become fallback speech");
            check((await pendingSpeech.WaitAsync(TimeSpan.FromSeconds(3))).Samples.SequenceEqual(converted.Samples) && syntheses == 1,
                "cancelled OpenVoice preparation releases the queued normal utterance");
            await gated.PrepareAsync(bank, default);
            check(attempts == 2 && syntheses == 1, "OpenVoice can prepare its same donor again after cancellation without synthesizing it");
        }
        finally { release.TrySetResult(); await gated.DisposeAsync(); }
        check(owner.Disposes == 1, "OpenVoice preparation keeps its donor lifetime under the existing single owner");
        entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposing = new DesktopNeuralUtau((_, _) => Task.FromResult(source),
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), (_, _, _) => Task.FromResult(converted), _ => Task.CompletedTask,
            prepareDonor: _ => { entered.TrySetResult(); return late.Task; });
        try
        {
            var preparation = disposing.PrepareAsync(bank, default); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await disposing.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
            check(await CaptureAsync(() => preparation) is OperationCanceledException &&
                await CaptureAsync(() => disposing.PrepareAsync(bank, default)) is ObjectDisposedException,
                "OpenVoice disposal stops pending donor preparation and prohibits worker resurrection");
        }
        finally { late.TrySetResult(); await disposing.DisposeAsync(); }
    }

    private static async Task CheckReferenceTransportAsync(PcmAudio source, PcmAudio reference, Action<bool, string> check)
    {
        var references = new[] { reference };
        var key = NeuralOpenVoiceSession.FingerprintReferences(references);
        var acknowledgement = new NeuralReferenceAcknowledgement();
        using var cache = new NeuralPcmReferenceCache();
        using var full = new MemoryStream();
        await NeuralPcmProtocol.WriteRequestAsync(full, source, references, default, key, acknowledgement.CanReuse(key));
        full.Position = 0; var request = NeuralPcmProtocol.ReadRequest(full, cache)!;
        var inspected = request.References.CopyForInspection();
        var originalSample = reference.Samples[0]; var originalKey = (byte[])key.Clone();
        inspected[0].Samples[0] ^= 127; reference.Samples[0] ^= 63; key[0] ^= 1;
        try
        {
            check(request.References.CopyForInspection()[0].Samples[0] == originalSample &&
                request.References.Matches(originalKey, references.Length, reference.Samples.Length) &&
                !request.References.Matches(key, references.Length, reference.Samples.Length),
                "owned references expose only deep inspection copies and cannot be changed through caller PCM or fingerprint arrays");
        }
        finally { reference.Samples[0] = originalSample; key[0] ^= 1; }
        using (var stopped = new CancellationTokenSource())
        {
            stopped.Cancel();
            check(Capture(() => NeuralOpenVoiceSession.OwnedReferences.ReadVerified(Stream.Null, references.Length,
                reference.Samples.Length, key, source, false, stopped.Token)) is OperationCanceledException,
                "owned reference reader observes cancellation before allocating or reading reference PCM");
        }
        using var cached = new MemoryStream();
        await NeuralPcmProtocol.WriteRequestAsync(cached, source, references, default, key, reuseReferences: true);
        var cachedBytes = cached.ToArray();
        check(!acknowledgement.CanReuse(key) && Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(cachedBytes), cache)) is InvalidDataException,
            "neural reference transfer never reuses an unacknowledged or uncommitted bank");
        // The real worker commits only after conversion/preparation succeeds,
        // and the parent acknowledges only after parsing its success response.
        cache.Commit(request);
        using var success = new MemoryStream(); NeuralPcmProtocol.WritePreparationResponse(success, 0); success.Position = 0;
        await NeuralPcmProtocol.ReadPreparationResponseAsync(success, default); acknowledgement.Acknowledge(key);
        cached.Position = 0; var reused = NeuralPcmProtocol.ReadRequest(cached, cache)!;
        check(acknowledgement.CanReuse(key) && reused.Source.Samples.SequenceEqual(source.Samples) &&
            ReferenceEquals(reused.References, request.References) && reused.References.CopyForInspection()[0].Samples.SequenceEqual(reference.Samples) &&
            cached.Position == cached.Length && cached.Length == NeuralPcmProtocol.RequestHeaderBytes + source.Samples.Length * 2 &&
            full.Length - cached.Length == 4 + reference.Samples.Length * 2,
            "neural acknowledged references roundtrip exact PCM while omitting every reference sample and retaining only the reader-owned bank");
        using var manyFull = new MemoryStream(); using var manyCached = new MemoryStream();
        var many = Enumerable.Repeat(reference, NeuralPcmProtocol.MaximumReferenceCount).ToArray();
        var manyKey = NeuralOpenVoiceSession.FingerprintReferences(many);
        await NeuralPcmProtocol.WriteRequestAsync(manyFull, source, many, default, manyKey);
        await NeuralPcmProtocol.WriteRequestAsync(manyCached, source, many, default, manyKey, reuseReferences: true);
        check(manyFull.Length - manyCached.Length == (long)many.Length * (4 + reference.Samples.Length * 2) &&
            manyCached.Length == cached.Length,
            "at the 149-reference bound cached frames omit exactly all count fields and PCM bytes regardless of bank size");
        using var preparation = new MemoryStream();
        await NeuralPcmProtocol.WritePreparationAsync(preparation, references, default, key, acknowledgement.CanReuse(key));
        preparation.Position = 0; var prepared = NeuralPcmProtocol.ReadRequest(preparation, cache)!;
        check(prepared.Preparation && prepared.Source.Samples.Length == 0 && preparation.Length == NeuralPcmProtocol.RequestHeaderBytes &&
            ReferenceEquals(prepared.References, request.References),
            "neural cached preparation is exactly one header and cannot contain donor or reference PCM");
        foreach (var offset in new[] { 16, 20, 24 })
        {
            var invalid = (byte[])cachedBytes.Clone();
            if (offset < 24) BinaryPrimitives.WriteInt32LittleEndian(invalid.AsSpan(offset), BinaryPrimitives.ReadInt32LittleEndian(invalid.AsSpan(offset)) + 1);
            else invalid[offset] ^= 1;
            check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(invalid), cache)) is InvalidDataException,
                "neural cached requests reject mismatched fingerprint count and total rather than selecting an old bank");
        }
        var damagedFull = full.ToArray(); damagedFull[24] ^= 1;
        check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(damagedFull), cache)) is InvalidDataException,
            "neural full requests verify their declared SHA256 against all received ordered reference PCM");
        var damagedPcm = full.ToArray(); damagedPcm[^1] ^= 1;
        check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(damagedPcm), cache)) is InvalidDataException,
            "neural full requests reject modified reference samples even when their sizes and declared fingerprint are unchanged");
        var truncated = cachedBytes[..^1];
        check(Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(truncated), cache)) is EndOfStreamException,
            "neural cached requests still reject truncated donor samples");
        var silentDonor = (byte[])cachedBytes.Clone();
        silentDonor.AsSpan(NeuralPcmProtocol.RequestHeaderBytes).Clear();
        var shortDonor = cachedBytes[..(NeuralPcmProtocol.RequestHeaderBytes + sizeof(short))];
        BinaryPrimitives.WriteInt32LittleEndian(shortDonor.AsSpan(12), 1);
        foreach (var (bytes, errorId) in new[] { (silentDonor, "NEURAL_INPUT_SILENT"), (shortDonor, "NEURAL_SOURCE_LIMIT") })
        {
            using var invalid = new MemoryStream([.. bytes, 0xab]);
            check(Capture(() => NeuralPcmProtocol.ReadRequest(invalid, cache)) is InvalidDataException donorError &&
                donorError.Message.StartsWith(errorId + ": ", StringComparison.Ordinal) &&
                invalid.Position == bytes.Length && invalid.ReadByte() == 0xab,
                "neural cached reference reuse still validates the fresh donor and leaves the following frame untouched");
        }

        var changed = new PcmAudio((short[])reference.Samples.Clone(), reference.SampleRate); changed.Samples[20]++;
        var changedKey = NeuralOpenVoiceSession.FingerprintReferences([changed]);
        using var reordered = new MemoryStream();
        await NeuralPcmProtocol.WritePreparationAsync(reordered, [changed, reference], default,
            NeuralOpenVoiceSession.FingerprintReferences([reference, changed]));
        reordered.Position = 0;
        check(Capture(() => NeuralPcmProtocol.ReadRequest(reordered)) is InvalidDataException,
            "owned reference verification rejects reordered PCM with the original ordered fingerprint");
        using var replacement = new MemoryStream();
        await NeuralPcmProtocol.WriteRequestAsync(replacement, source, [changed], default, changedKey, acknowledgement.CanReuse(changedKey));
        replacement.Position = 0; var replaced = NeuralPcmProtocol.ReadRequest(replacement, cache)!; cache.Commit(replaced);
        check(!acknowledgement.CanReuse(changedKey) && !acknowledgement.CanReuse(key) && replacement.Length == full.Length &&
            replaced.References.CopyForInspection()[0].Samples.SequenceEqual(changed.Samples) &&
            Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(cachedBytes), cache)) is InvalidDataException,
            "same-length reference changes force a full transfer and evict the previous worker bank");
        using var failed = new MemoryStream(); NeuralPcmProtocol.WritePreparationResponse(failed, 7); failed.Position = 0;
        var error = await CaptureAsync(() => NeuralPcmProtocol.ReadPreparationResponseAsync(failed, default));
        acknowledgement.Clear(); cache.Dispose();
        check(error is NeuralWorkerException && !acknowledgement.CanReuse(key) && !acknowledgement.CanReuse(changedKey) &&
            Capture(() => NeuralPcmProtocol.ReadRequest(new MemoryStream(cachedBytes), cache)) is InvalidDataException,
            "failure or worker restart clears both reference acknowledgements and the single retained bank");
        using var fresh = new MemoryStream();
        await NeuralPcmProtocol.WriteRequestAsync(fresh, source, references, default, key, acknowledgement.CanReuse(key)); fresh.Position = 0;
        check(NeuralPcmProtocol.ReadRequest(fresh, cache)!.References.CopyForInspection()[0].Samples.SequenceEqual(reference.Samples) && fresh.Length == full.Length,
            "after worker restart the original bank must be sent and verified in full again");
        using var rejected = new MemoryStream();
        check(await CaptureAsync(() => NeuralPcmProtocol.WriteRequestAsync(rejected, source,
            [new PcmAudio(new short[reference.Samples.Length], reference.SampleRate)], default, key, reuseReferences: true)) is InvalidDataException && rejected.Length == 0,
            "reference reuse never bypasses parent raw PCM validation even when its previous fingerprint is supplied");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        check(await CaptureAsync(() => NeuralPcmProtocol.WritePreparationAsync(rejected, references, cancelled.Token, key, reuseReferences: true)) is OperationCanceledException &&
            rejected.Length == 0 && await CaptureAsync(() => NeuralPcmProtocol.WritePreparationAsync(rejected, references, default, new byte[31])) is InvalidDataException,
            "cached reference writes honor cancellation and require an exact SHA256 fingerprint before emitting bytes");
    }

    private static async Task CheckStartErrorModeAsync(PcmAudio source, PcmAudio reference, Action<bool, string> check)
    {
        uint original = GetErrorMode(), baseline = original & ~NativeChildProcess.RequiredErrorMode;
        // Exercise direct-exe conditions even when dev.ps1 already gave this
        // test process the required flags. No native failure is induced.
        SetErrorMode(baseline);
        try
        {
            uint during = 0, afterNested = 0;
            using (NativeChildProcess.Start(new ProcessStartInfo(), _ =>
            {
                during = GetErrorMode();
                using var nested = NativeChildProcess.Start(new ProcessStartInfo(), _ => new Process());
                afterNested = GetErrorMode();
                return new Process(); // Specialized start seam, no OS child.
            })) { }
            check((during & NativeChildProcess.RequiredErrorMode) == NativeChildProcess.RequiredErrorMode &&
                afterNested == during && GetErrorMode() == baseline,
                "native child start adds inherited flags before launch and nested starts restore the enclosing mode");
            var failure = Capture(() => NativeChildProcess.Start(new ProcessStartInfo(), _ => throw new IOException("expected injected start failure")));
            check(failure is IOException && GetErrorMode() == baseline,
                "native child launch exceptions restore the exact caller process error mode");

            WorldComparison.ConfigureWorker();
            check(GetErrorMode() == (baseline | NativeChildProcess.RequiredErrorMode),
                "WORLD worker configuration retains inherited flags and includes open-file error dialog suppression");
            SetErrorMode(baseline);
            var roots = new List<string>(); uint neuralMode = 0;
            await using var renderer = new DesktopNeuralUtau((_, _) => Task.FromResult(source),
                (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), convert: null,
                _ => Task.CompletedTask, launch: start =>
                {
                    roots.Add(start.WorkingDirectory); neuralMode = GetErrorMode();
                    throw new IOException("expected injected neural start failure");
                });
            var bank = new Voicebank("error mode fixture", "unused", [], 0);
            var error = await CaptureAsync(() => renderer.RenderAsync("fixture", bank, default));
            check(error is IOException && (neuralMode & NativeChildProcess.RequiredErrorMode) == NativeChildProcess.RequiredErrorMode &&
                GetErrorMode() == baseline && roots.Count == 1 && !Directory.Exists(roots[0]),
                "the neural renderer guards its actual injected launch and cleans only its own root when start fails");
        }
        finally { SetErrorMode(original); }
        check(GetErrorMode() == original,
            "native start checks restore the test runner's initially inherited error mode");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint SetErrorMode(uint mode);

    private static async Task CheckOwnedWorkerRecoveryAsync(PcmAudio source, PcmAudio reference, Action<bool, string> check)
    {
        var bank = new Voicebank("owned worker fixture", "unused", [], 0);
        var starts = 0; var donorCalls = 0; var roots = new List<string>(); var children = new List<int>();
        var failReference = false;
        var executable = Environment.ProcessPath ?? throw new IOException();
        var renderer = new DesktopNeuralUtau((_, _) => { donorCalls++; return Task.FromResult(source); }, (_, _) =>
            failReference ? throw new InvalidDataException("reference fixture") : Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
            convert: null, _ => Task.CompletedTask, launch: start =>
            {
                roots.Add(start.WorkingDirectory); starts++; start.ArgumentList.Clear();
                if (starts == 1)
                {
                    start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
                    foreach (var argument in new[] { "/d", "/c", "exit", "7" }) start.ArgumentList.Add(argument);
                }
                else
                {
                    start.FileName = executable;
                    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                        start.ArgumentList.Add(typeof(NeuralPcmWorkerTests).Assembly.Location);
                    start.ArgumentList.Add("--test-wait-child");
                }
                var process = Process.Start(start) ?? throw new IOException(); children.Add(process.Id); return process;
            });
        try
        {
            var failed = await CaptureAsync(() => renderer.RenderAsync("異常終了", bank, default));
            check(failed is IOException && starts == 1 && !Directory.Exists(roots[0]) && !Alive(children[0]),
                "neural owned-worker abnormal exit is reaped and its private root removed without touching another app process");
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var pending = renderer.RenderAsync("次の発話", bank, cancel.Token);
            while (starts < 2 && !pending.IsCompleted && !cancel.IsCancellationRequested) await Task.Delay(10);
            var marker = starts == 2 ? Path.Combine(roots[1], "started") : "";
            while (marker.Length > 0 && !File.Exists(marker) && !pending.IsCompleted && !cancel.IsCancellationRequested) await Task.Delay(10);
            var started = marker.Length > 0 && File.Exists(marker); cancel.Cancel();
            var cancelled = await CaptureAsync(() => pending);
            check(started && cancelled is OperationCanceledException && starts == 2 && !Directory.Exists(roots[1]) && !Alive(children[1]),
                "a failed neural worker restarts for the next utterance and cancellation kills and reaps only that new owned child before cleanup");
            var priorDonors = donorCalls;
            typeof(DesktopNeuralUtau).GetField("ownsReferenceSnapshots", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(renderer, true);
            using var cancelPreparation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var preparing = renderer.PrepareAsync(bank, cancelPreparation.Token);
            while (starts < 3 && !preparing.IsCompleted && !cancelPreparation.IsCancellationRequested) await Task.Delay(10);
            var preparationMarker = starts == 3 ? Path.Combine(roots[2], "started") : "";
            while (preparationMarker.Length > 0 && !File.Exists(preparationMarker) && !preparing.IsCompleted && !cancelPreparation.IsCancellationRequested) await Task.Delay(10);
            var preparationStarted = preparationMarker.Length > 0 && File.Exists(preparationMarker); cancelPreparation.Cancel();
            check(preparationStarted && await CaptureAsync(() => preparing) is OperationCanceledException && donorCalls == priorDonors &&
                !Directory.Exists(roots[2]) && !Alive(children[2]),
                "live neural warmup cancels and reaps its owned worker without synthesizing donor speech or leaving temporary files");
            if (Environment.ProcessorCount >= 8)
            {
                failReference = true;
                var rejected = await CaptureAsync(() => renderer.PrepareAsync(bank, default));
                check(rejected is IOException && rejected.Message.StartsWith("NEURAL_PREPARE_FAILED:", StringComparison.Ordinal) && starts == 4 && donorCalls == priorDonors &&
                    !Directory.Exists(roots[3]) && !Alive(children[3]),
                    "early model-loading worker is reaped when the private reference read fails before sending any preparation");
            }
        }
        finally { await renderer.DisposeAsync(); }

        static bool Alive(int id)
        {
            try { using var process = Process.GetProcessById(id); return !process.HasExited; }
            catch (ArgumentException) { return false; }
        }
    }

    private static async Task CheckAdapterAsync(PcmAudio source, PcmAudio reference, PcmAudio converted, Action<bool, string> check)
    {
        var bank = new Voicebank("fixed bank", "unused", [], 0); var donorCalls = 0; var conversions = 0; Voicebank? selected = null;
        var owner = new Owner();
        await using (var renderer = new DesktopNeuralUtau((text, ct) => { ct.ThrowIfCancellationRequested(); donorCalls++; return Task.FromResult(source); },
            (voice, _) => { selected = voice; return Task.FromResult<IReadOnlyList<PcmAudio>>([reference]); },
            (donor, clips, _) =>
            {
                conversions++; check(!ReferenceEquals(donor.Samples, source.Samples) && !ReferenceEquals(clips[0].Samples, reference.Samples),
                    "neural adapter snapshots donor and selected-bank PCM before conversion");
                return Task.FromResult(converted);
            }, _ => Task.CompletedTask, owner))
        {
            var actual = await renderer.RenderAsync("本人の発言", bank, default);
            check(ReferenceEquals(selected, bank) && donorCalls == 1 && conversions == 1 && actual.Samples.SequenceEqual(converted.Samples),
                "neural direct adapter converts complete NIT donor speech with the utterance's selected bank and performs no playback");
        }
        check(owner.Disposes == 1, "neural adapter disposes its owned donor exactly once");
        foreach (var preparing in new[] { false, true })
        {
            var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var borrowed = new[] { Audio(44100, 4410), Audio(44100, 8820) };
            var expectedFirst = (short[])borrowed[0].Samples.Clone(); var expectedSecond = (short[])borrowed[1].Samples.Clone();
            IReadOnlyList<PcmAudio>? observed = null;
            await using var isolated = new DesktopNeuralUtau(async (_, ct) =>
                { waiting.TrySetResult(); await resume.Task.WaitAsync(ct); return source; },
                (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>(borrowed),
                (_, clips, _) => { observed = clips; return Task.FromResult(converted); }, _ => Task.CompletedTask,
                prepare: (clips, _) => { observed = clips; return Task.CompletedTask; },
                prepareDonor: async ct => { waiting.TrySetResult(); await resume.Task.WaitAsync(ct); });
            Task pending = preparing ? isolated.PrepareAsync(bank, default) : isolated.RenderAsync("snapshot", bank, default);
            try
            {
                await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3));
                borrowed[0].Samples[20] ^= 127; borrowed[1].Samples[20] ^= 127;
                (borrowed[0], borrowed[1]) = (borrowed[1], borrowed[0]);
                resume.TrySetResult(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
                check(observed is { Count: 2 } && observed[0].Samples.SequenceEqual(expectedFirst) &&
                    observed[1].Samples.SequenceEqual(expectedSecond),
                    $"neural {(preparing ? "prepare" : "render")} snapshots isolate borrowed PCM and reference order while donor work waits");
            }
            finally { resume.TrySetResult(); }
        }
        var cacheSource = new PcmAudio((short[])source.Samples.Clone(), source.SampleRate);
        var cacheReference = new PcmAudio((short[])reference.Samples.Clone(), reference.SampleRate);
        var cacheCalls = 0;
        await using (var cached = new DesktopNeuralUtau((_, _) => Task.FromResult(cacheSource),
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([cacheReference]),
            (_, _, _) => { cacheCalls++; return Task.FromResult(new PcmAudio((short[])converted.Samples.Clone(), converted.SampleRate)); },
            _ => Task.CompletedTask))
        {
            var first = await cached.RenderAsync("同文", bank, default);
            first.Samples[10] ^= 123;
            var repeated = await cached.RenderAsync("同文", bank, default);
            check(cacheCalls == 1 && repeated.Samples.SequenceEqual(converted.Samples),
                "neural repeated exact PCM skips conversion and isolates caller mutations");
            repeated.Samples[20] ^= 123;
            check((await cached.RenderAsync("同文", bank, default)).Samples.SequenceEqual(converted.Samples),
                "neural cached results are defensively copied on every hit");
            cacheSource.Samples[10]++;
            await cached.RenderAsync("同文", bank, default);
            cacheReference.Samples[10]++;
            await cached.RenderAsync("同文", bank, default);
            check(cacheCalls == 3, "neural conversion cache invalidates on donor or reference content changes without length changes");
            using var cancelledCache = new CancellationTokenSource(); cancelledCache.Cancel();
            check(await CaptureAsync(() => cached.RenderAsync("同文", bank, cancelledCache.Token)) is OperationCanceledException,
                "neural cached audio never bypasses a stop request");
            cacheReference.Samples[10]--;
            await cached.RenderAsync("同文", bank, default);
            check(cacheCalls == 4, "neural cache retains only the latest conversion rather than growing across banks");
        }
        var preparations = 0; var preparationDonors = 0; var donorPreparations = 0;
        await using (var warmer = new DesktopNeuralUtau((_, _) => { preparationDonors++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), (_, _, _) => Task.FromResult(converted),
            _ => Task.CompletedTask, prepare: (clips, ct) => { ct.ThrowIfCancellationRequested(); preparations++; return Task.CompletedTask; },
            prepareDonor: ct => { ct.ThrowIfCancellationRequested(); donorPreparations++; return Task.CompletedTask; }))
        {
            await warmer.PrepareAsync(bank, default);
            check(preparations == 1 && donorPreparations == 1 && preparationDonors == 0, "neural selected-bank warmup never synthesizes or plays a donor utterance");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            check(await CaptureAsync(() => warmer.PrepareAsync(bank, cancelled.Token)) is OperationCanceledException && preparations == 1,
                "cancelled neural warmup cannot start additional preparation");
            check((await warmer.RenderAsync("続行", bank, default)).Samples.SequenceEqual(converted.Samples),
                "neural speech remains usable after a cancelled preparation request");
        }
        donorCalls = 0;
        await using (var unavailable = new DesktopNeuralUtau((_, _) => { donorCalls++; return Task.FromResult(source); },
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]), (_, _, _) => Task.FromResult(converted),
            _ => Task.FromException(new IOException("private-model-path-secret"))))
        {
            var error = await CaptureAsync(() => unavailable.RenderAsync("秘匿本文", bank, default));
            check(error is IOException && !error.ToString().Contains("private-model-path-secret", StringComparison.Ordinal) && donorCalls == 0,
                "missing neural assets fail before donor synthesis and sanitize transport diagnostics");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var late = new TaskCompletionSource<PcmAudio>(TaskCreationOptions.RunContinuationsAsynchronously);
        var hold = true;
        var cancellable = new DesktopNeuralUtau((_, _) => Task.FromResult(source), (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>([reference]),
            (_, _, _) => { if (hold) { entered.TrySetResult(); return late.Task; } return Task.FromResult(converted); }, _ => Task.CompletedTask);
        try
        {
            using var cancel = new CancellationTokenSource();
            var pending = cancellable.RenderAsync("取消", bank, cancel.Token); await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); cancel.Cancel();
            check(await CaptureAsync(() => pending) is OperationCanceledException,
                "neural caller cancellation wins even when a trusted conversion delegate completes late");
            hold = false;
            check((await cancellable.RenderAsync("再開", bank, default)).Samples.SequenceEqual(converted.Samples),
                "cancelled neural synthesis releases serialization for the next utterance");
            late.TrySetResult(converted);
        }
        finally { late.TrySetResult(converted); await cancellable.DisposeAsync(); }
        await using var limits = new DesktopNeuralUtau((_, _) => Task.FromResult(source),
            (_, _) => Task.FromResult<IReadOnlyList<PcmAudio>>(Enumerable.Repeat(reference, 150).ToArray()),
            (_, _, _) => Task.FromResult(converted), _ => Task.CompletedTask);
        check(await CaptureAsync(() => limits.RenderAsync("上限", bank, default)) is NeuralReferenceException,
            "neural reference-count failures retain a specific safe reason instead of selecting another bank");
    }

    private static async Task CheckReferencesAsync(Action<bool, string> check)
    {
        byte[]? scratch = null;
        var small = NeuralVoiceReferences.GetScratch(ref scratch, 44);
        Array.Fill(small, (byte)173);
        var grown = NeuralVoiceReferences.GetScratch(ref scratch, 247896);
        check(small.All(value => value == 0) && grown.Length >= 247896 && grown.Length < 1024 * 1024,
            "reference scratch growth erases retired raw audio and does not reserve a megabyte for bundled recordings");
        check(ReferenceEquals(grown, NeuralVoiceReferences.GetScratch(ref scratch, 44)),
            "smaller later references reuse the owned buffer without reallocating");
        Array.Fill(grown, (byte)219);
        var maximum = NeuralVoiceReferences.GetScratch(ref scratch, 1024 * 1024);
        check(grown.All(value => value == 0) && maximum.Length == 1024 * 1024,
            "reference scratch still accepts the previous inclusive size limit and erases replaced storage");
        CryptographicOperations.ZeroMemory(maximum);
        using var temporary = new TestDirectory(); var root = temporary.DirectoryPath; var bankRoot = Path.Combine(root, "voice"); Directory.CreateDirectory(bankRoot);
        var path = Path.Combine(bankRoot, "a.wav"); var wave = Audio(44100, 4410).ToWaveBytes(); await File.WriteAllBytesAsync(path, wave);
        var originalHash = Convert.ToHexString(SHA256.HashData(wave));
        var bank = new Voicebank("reference fixture", bankRoot, [new(path, "あ", 0, 0, 0, 0, 0), new(path, "別名", 0, 0, 0, 0, 0)], 0);
        var cache = new NeuralReferenceCache();
        var first = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache, diagnostics: true);
        var fingerprintMemo = new NeuralReferenceFingerprintMemo();
        var firstFingerprint = fingerprintMemo.Get(first, default);
        var expectedFingerprint = (byte[])firstFingerprint.Clone();
        check(!fingerprintMemo.LastHit && firstFingerprint.SequenceEqual(NeuralOpenVoiceSession.FingerprintReferences(first.Clips)),
            "trusted raw-content proof starts with the unchanged legacy ordered-PCM fingerprint");
        check(first.Clips.Count == 1 && first.Clips[0].SampleRate == 44100 && first.Clips[0].Samples.Any(x => x != 0) &&
            Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(path))) == originalHash,
            "neural reference inspection deduplicates OTO aliases while preserving original WAV bytes");
        var expected = (short[])first.Clips[0].Samples.Clone(); first.Clips[0].Samples[20] ^= 127;
        var unchanged = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache, diagnostics: true);
        firstFingerprint[0] ^= 255;
        check(fingerprintMemo.Get(unchanged, default).SequenceEqual(expectedFingerprint) && fingerprintMemo.LastHit,
            "unchanged raw WAV proof reuses the last reference fingerprint without lending its mutable key array");
        check(cache.Count == 1 && unchanged.ContentSha256 == first.ContentSha256 && unchanged.Clips[0].Samples.SequenceEqual(expected),
            "unchanged neural reference PCM is reused without retaining caller mutations");
        unchanged.Clips[0].Samples[30] ^= 127;
        var nextHit = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        check(nextHit.Clips[0].Samples.SequenceEqual(expected) && !ReferenceEquals(nextHit.Clips[0].Samples, unchanged.Clips[0].Samples),
            "neural decoded-cache hits transfer private PCM copies without lending cache arrays");
        var diagnosticUncached = await NeuralVoiceReferences.ReadAsync(bank, root, default, diagnostics: true);
        check(nextHit.Diagnostics is null && first.Diagnostics is { CacheHits: 0, CacheMisses: 1 } &&
            unchanged.Diagnostics is { CacheHits: 1, CacheMisses: 0 } &&
            diagnosticUncached.Diagnostics is { CacheHits: 0, CacheMisses: 1 } &&
            new[] { first.Diagnostics, unchanged.Diagnostics, diagnosticUncached.Diagnostics }.All(ConsistentDiagnostics),
            "reference read diagnostics are opt-in and classify cold warm and uncached successful reads consistently");
        check(diagnosticUncached.ContentSha256 == nextHit.ContentSha256 &&
            diagnosticUncached.Clips[0].Samples.SequenceEqual(nextHit.Clips[0].Samples) &&
            ReferenceEquals((first with { Clips = first.Clips }).Diagnostics, first.Diagnostics),
            "reference diagnostics preserve exact PCM hash and snapshot-with compatibility");
        using (var stop = new CancellationTokenSource())
        {
            stop.Cancel();
            check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, root, stop.Token, cache)) is OperationCanceledException,
                "neural warm decoded-cache reads retain cancellation before source reuse");
        }
        var modified = File.GetLastWriteTimeUtc(path); wave[44 + 4000] ^= 64; await File.WriteAllBytesAsync(path, wave); File.SetLastWriteTimeUtc(path, modified);
        var second = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        var changedFingerprint = fingerprintMemo.Get(second, default);
        check(!fingerprintMemo.LastHit && changedFingerprint.SequenceEqual(NeuralOpenVoiceSession.FingerprintReferences(second.Clips)) &&
            !changedFingerprint.SequenceEqual(expectedFingerprint), "raw WAV content change recomputes the legacy reference fingerprint");
        _ = fingerprintMemo.Get(nextHit, default);
        check(!fingerprintMemo.LastHit, "raw fingerprint memo keeps only its latest content token, not a growing history");
        using (var cancelledMemo = new CancellationTokenSource())
        {
            cancelledMemo.Cancel();
            check(Capture(() => fingerprintMemo.Get(nextHit, cancelledMemo.Token)) is OperationCanceledException,
                "reference fingerprint memo observes cancellation even on an unchanged content token");
        }
        _ = fingerprintMemo.Get(nextHit, default);
        check(!fingerprintMemo.LastHit, "cancelled fingerprint retrieval discards retained memo state");
        check(Capture(() => fingerprintMemo.Get(nextHit with { ContentSha256 = "invalid" }, default)) is InvalidDataException,
            "reference fingerprint memo rejects malformed content proof");
        _ = fingerprintMemo.Get(nextHit, default);
        check(!fingerprintMemo.LastHit, "failed fingerprint retrieval cannot leave successful memo readiness");
        fingerprintMemo.Clear(); _ = fingerprintMemo.Get(nextHit, default);
        check(!fingerprintMemo.LastHit, "explicit lifetime cleanup discards the reference fingerprint memo");
        check(first.ContentSha256 != second.ContentSha256 && cache.Count == 2 && !second.Clips[0].Samples.SequenceEqual(expected) && File.GetLastWriteTimeUtc(path) == modified,
            "neural references hash real contents and detect same-size same-timestamp replacements");
        var outside = bank with { Entries = [new(Path.Combine(root, "outside.wav"), "あ", 0, 0, 0, 0, 0)] };
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(outside, root, default)) is InvalidDataException &&
            await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, Path.Combine(root, "different-root"), default)) is InvalidDataException,
            "neural reference files must remain inside the selected data voicebank boundary");
        var excessive = bank with { Entries = Enumerable.Range(0, 150).Select(i => new OtoEntry(Path.Combine(bankRoot, i + ".wav"), "あ", 0, 0, 0, 0, 0)).ToArray() };
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(excessive, root, default)) is NeuralReferenceException,
            "neural reference count is rejected before opening nonexistent or excessive source files");
        cache.Clear(); var shortClip = Audio(44100, 4096);
        for (var i = 0; i < 150; i++) cache.Add(i.ToString(), shortClip);
        check(cache.Count == 149 && !cache.TryGet("0", out _) && cache.TryGet("149", out _),
            "decoded neural reference cache evicts old entries at its fixed file-count bound");
        cache.Clear(); var longClip = new PcmAudio(Enumerable.Repeat((short)1, 44100 * 30).ToArray(), 44100);
        for (var i = 0; i < 7; i++) cache.Add(i.ToString(), longClip);
        check(cache.Count == 6 && cache.Samples == 44100L * 180 && !cache.TryGet("0", out _),
            "decoded neural reference cache remains bounded by total PCM duration");
        cache.Clear(); check(cache.Count == 0 && cache.Samples == 0, "neural reference cache releases retained PCM on disposal");
        await CheckRendererReferenceCacheAsync(root, check);
        await CheckReferenceScratchAsync(root, check);
        await CheckParallelReferenceFailuresAsync(root, check);

        bool ConsistentDiagnostics(NeuralReferenceReadDiagnostics? value)
        {
            if (value is null || value.FileCount != 1 || value.RawBytes != wave.Length || value.CacheHits + value.CacheMisses != 1)
                return false;
            var parts = new[] { value.PathFileMilliseconds, value.ReadMilliseconds, value.RawHashMilliseconds,
                value.CacheMilliseconds, value.DecodeMilliseconds, value.BufferMilliseconds, value.UnclassifiedMilliseconds };
            return parts.All(part => double.IsFinite(part) && part >= 0) && double.IsFinite(value.TotalMilliseconds) &&
                Math.Abs(parts.Sum() - value.TotalMilliseconds) <= 0.000001;
        }
    }

    internal static NeuralReferenceCache GetRendererReferenceCache(DesktopNeuralUtau renderer) =>
        typeof(DesktopNeuralUtau).GetField("referenceCache",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(renderer)
            as NeuralReferenceCache ?? throw new InvalidOperationException("Default renderer reference cache is missing.");

    private static async Task CheckRendererReferenceCacheAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "renderer-cache"); Directory.CreateDirectory(directory);
        var firstPath = Path.Combine(directory, "a.wav"); var secondPath = Path.Combine(directory, "b.wav");
        var wave = Audio(44100, 4410).ToWaveBytes();
        await File.WriteAllBytesAsync(firstPath, wave); await File.WriteAllBytesAsync(secondPath, wave);
        var bank = new Voicebank("renderer cache", directory,
            [new(firstPath, "あ", 0, 0, 0, 0, 0), new(secondPath, "い", 0, 0, 0, 0, 0)], 0);
        await using var renderer = new DesktopNeuralUtau();
        var cache = GetRendererReferenceCache(renderer);
        var cold = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        var warm = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache, readConcurrency: 2);
        var isolated = await NeuralVoiceReferences.ReadAsync(bank, root, default, new NeuralReferenceCache());
        var borrowed = cold.Clips[0];
        check(cold.Clips.Count == 2 && warm.Clips.Count == 2 && cache.Count == 1 &&
            cold.Clips.Concat(warm.Clips).All(clip => ReferenceEquals(clip.Samples, borrowed.Samples)) &&
            cold.ContentSha256 == warm.ContentSha256 && warm.ContentSha256 == isolated.ContentSha256 &&
            NeuralOpenVoiceSession.FingerprintReferences(warm.Clips).AsSpan().SequenceEqual(
                NeuralOpenVoiceSession.FingerprintReferences(isolated.Clips)),
            "default renderer borrows one cache-owned PCM array for identical contents while retaining two ordered references");
        for (var index = 0; index < 149; index++) cache.Add(index.ToString(), borrowed);
        check(cache.Count == 149 && !cache.TryGet(Convert.ToHexString(SHA256.HashData(wave)), out _) &&
            borrowed.Samples.SequenceEqual(isolated.Clips[0].Samples),
            "evicting a cache entry leaves a borrowed renderer clip valid through the request");
        cache.Clear();
        check(cache.Count == 0 && cache.Samples == 0 && borrowed.Samples.SequenceEqual(isolated.Clips[0].Samples),
            "clearing the renderer cache releases its entries without modifying borrowed PCM");
        wave[44 + 4000] ^= 64; await File.WriteAllBytesAsync(secondPath, wave);
        var changed = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache, readConcurrency: 2);
        check(changed.ContentSha256 != warm.ContentSha256 && !ReferenceEquals(changed.Clips[0].Samples, changed.Clips[1].Samples) &&
            !changed.Clips[1].Samples.SequenceEqual(warm.Clips[1].Samples),
            "renderer borrowing rechecks each original and replaces changed reference content");
    }

    private static async Task CheckReferenceScratchAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "scratch-references"); Directory.CreateDirectory(directory);
        var entries = new List<OtoEntry>();
        foreach (var (name, count) in new[] { ("a-large", 11025), ("b-small", 4096), ("c-over-scratch", 600000), ("d-medium", 8192) })
        {
            var path = Path.Combine(directory, name + ".wav");
            await File.WriteAllBytesAsync(path, Audio(44100, count).ToWaveBytes());
            entries.Add(new(path, name, 0, 0, 0, 0, 0));
        }
        var bank = new Voicebank("mixed scratch fixture", directory, entries.AsEnumerable().Reverse().ToArray(), 0);
        var cache = new NeuralReferenceCache();
        var uncached = await NeuralVoiceReferences.ReadAsync(bank, root, default);
        var cold = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        var warm = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        var parallelCache = new NeuralReferenceCache();
        var parallelCold = await NeuralVoiceReferences.ReadAsync(bank, root, default, parallelCache, readConcurrency: 2);
        var parallelWarm = await NeuralVoiceReferences.ReadAsync(bank, root, default, parallelCache, readConcurrency: 2);
        var parallelUncached = await NeuralVoiceReferences.ReadAsync(bank, root, default, readConcurrency: 2);
        check(new[] { parallelCold, parallelWarm, parallelUncached }.All(snapshot =>
            snapshot.Diagnostics is null && snapshot.ContentSha256 == uncached.ContentSha256 && snapshot.Clips.Count == uncached.Clips.Count &&
            snapshot.Clips.Zip(uncached.Clips).All(pair => pair.First.SampleRate == pair.Second.SampleRate && pair.First.Samples.SequenceEqual(pair.Second.Samples))),
            "two private read slots preserve cold warm and uncached mixed-size PCM hashes and sorted boundaries");
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, root, default, readConcurrency: 0)) is ArgumentOutOfRangeException &&
            await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, root, default, readConcurrency: 3)) is ArgumentOutOfRangeException &&
            await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, root, default, diagnostics: true, readConcurrency: 2)) is ArgumentException,
            "reference reading rejects invalid concurrency and overlapping serial diagnostic stages before file work");
        check(uncached.ContentSha256 == cold.ContentSha256 && uncached.ContentSha256 == warm.ContentSha256 &&
            uncached.Clips.Select(clip => clip.Samples.Length).SequenceEqual(new[] { 11025, 4096, 600000, 8192 }) &&
            cold.Clips.Count == 4 && warm.Clips.Count == 4 &&
            uncached.Clips.Zip(cold.Clips, (expected, actual) => expected.SampleRate == actual.SampleRate && expected.Samples.SequenceEqual(actual.Samples)).All(equal => equal) &&
            uncached.Clips.Zip(warm.Clips, (expected, actual) => expected.SampleRate == actual.SampleRate && expected.Samples.SequenceEqual(actual.Samples)).All(equal => equal),
            "mixed WAV sizes keep sorted reference order and exact PCM/content hashes with no cache cold cache and warm cache");
        check(cold.Clips[0].Samples.Any(sample => sample != 0) &&
            cold.Clips[0].Samples.SequenceEqual(uncached.Clips[0].Samples) &&
            !ReferenceEquals(cold.Clips[0].Samples, warm.Clips[0].Samples),
            "cache misses retain independent PCM after scratch reuse and final clearing");

        // Preserve the preceding WAV's full RIFF/data declarations but truncate
        // its actual file to the header. An oversized decoder view would read
        // the preceding valid WAV's remaining bytes from scratch and accept it.
        var firstBytes = await File.ReadAllBytesAsync(entries[0].WavePath);
        var badPath = Path.Combine(directory, "z-truncated.wav");
        await File.WriteAllBytesAsync(badPath, firstBytes.AsMemory(0, 44).ToArray());
        var malformed = bank with { Entries = new[] { entries[0], new OtoEntry(badPath, "bad", 0, 0, 0, 0, 0) } };
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(malformed, root, default, cache)) is InvalidDataException && cache.Count == 4,
            "short malformed RIFF cannot decode a previous larger WAV from stale scratch tail or enter the PCM cache");
        var recovered = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        check(recovered.ContentSha256 == uncached.ContentSha256 && recovered.Clips.Count == uncached.Clips.Count &&
            recovered.Clips.Zip(uncached.Clips, (actual, expected) => actual.Samples.SequenceEqual(expected.Samples)).All(equal => equal),
            "reference inspection recovers after a truncated RIFF failure without poisoned cache or retained scratch PCM");
        var stamp = File.GetLastWriteTimeUtc(entries[0].WavePath); firstBytes[444] ^= 64;
        await File.WriteAllBytesAsync(entries[0].WavePath, firstBytes); File.SetLastWriteTimeUtc(entries[0].WavePath, stamp);
        var changedParallel = await NeuralVoiceReferences.ReadAsync(bank, root, default, parallelCache, readConcurrency: 2);
        var changedSerial = await NeuralVoiceReferences.ReadAsync(bank, root, default, cache);
        check(changedParallel.ContentSha256 != parallelWarm.ContentSha256 && changedParallel.ContentSha256 == changedSerial.ContentSha256 &&
            changedParallel.Clips.Zip(changedSerial.Clips).All(pair => pair.First.Samples.SequenceEqual(pair.Second.Samples)),
            "two-slot reads rehash same-size same-stamp source edits after earlier reads without reusing stale PCM");
    }

    private static async Task CheckParallelReferenceFailuresAsync(string root, Action<bool, string> check)
    {
        var directory = Path.Combine(root, "parallel-reference-failures"); Directory.CreateDirectory(directory);
        var wave = Audio(44100, 44100 * 30).ToWaveBytes();
        var entries = new List<OtoEntry>();
        for (var i = 0; i < 8; i++)
        {
            var path = Path.Combine(directory, $"{i}.wav"); await File.WriteAllBytesAsync(path, wave);
            entries.Add(new(path, i.ToString(), 0, 0, 0, 0, 0));
        }
        var bank = new Voicebank("parallel failures", directory, entries, 0);
        var missingPath = Path.Combine(directory, "missing.wav");
        var missing = bank with { Entries = new[] { entries[0], new OtoEntry(missingPath, "missing", 0, 0, 0, 0, 0) } };
        var cache = new NeuralReferenceCache();
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(missing, root, default, cache, readConcurrency: 2)) is FileNotFoundException &&
            FilesClosed(entries), "a later read-task failure retains its original exception and closes both read slots before returning");
        await File.WriteAllBytesAsync(missingPath, wave);
        var recovered = await NeuralVoiceReferences.ReadAsync(missing, root, default, cache, readConcurrency: 2);
        check(recovered.Clips.Count == 2 && recovered.Clips[0].Samples.SequenceEqual(recovered.Clips[1].Samples) && FilesClosed(missing.Entries),
            "two-slot source reading recovers after a later missing file is restored");
        check(await CaptureAsync(() => NeuralVoiceReferences.ReadAsync(bank, root, default, cache, readConcurrency: 2)) is NeuralReferenceException &&
            FilesClosed(entries), "parent reference-duration rejection joins later pending reads before closing and clearing both slots");
        using (var stop = new CancellationTokenSource())
        {
            var pending = NeuralVoiceReferences.ReadAsync(bank, root, stop.Token, cache, readConcurrency: 2); stop.Cancel();
            check(await CaptureAsync(() => pending) is OperationCanceledException && FilesClosed(entries),
                "two-slot cancellation completes pending reads and releases all source handles before returning");
        }
        var single = bank with { Entries = new[] { entries[0] } };
        check((await NeuralVoiceReferences.ReadAsync(single, root, default, cache, readConcurrency: 2)).Clips.Count == 1,
            "a cancelled two-slot reader leaves its bounded cache usable by the next read");

        // Exercise the actual owned-read boundary without model/native work or
        // exposing an ownership flag/provider replacement in the product API.
        await using var renderer = new DesktopNeuralUtau();
        var cleanupReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Func<Voicebank, CancellationToken, Task<NeuralReferenceSnapshot>> ownedRead = async (selected, ct) =>
        {
            try { return await NeuralVoiceReferences.ReadAsync(selected, root, ct, cache, readConcurrency: 2); }
            finally { cleanupReached.TrySetResult(); await releaseCleanup.Task; }
        };
        typeof(DesktopNeuralUtau).GetField("ownedReferences", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(renderer, ownedRead);
        var readMethod = typeof(DesktopNeuralUtau).GetMethod("ReadReferencesAsync", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        using (var stop = new CancellationTokenSource())
        {
            var pending = (Task)readMethod.Invoke(renderer, new object[] { bank, stop.Token })!;
            try
            {
                stop.Cancel(); await cleanupReached.Task.WaitAsync(TimeSpan.FromSeconds(3));
                check(!pending.IsCompleted, "the renderer awaits owned read cleanup instead of abandoning it through Observe on cancellation");
                releaseCleanup.TrySetResult();
                check(await CaptureAsync(() => pending) is OperationCanceledException && FilesClosed(entries),
                    "the cancelled owned reader finishes its real slot cleanup before completing the renderer boundary");
            }
            finally { releaseCleanup.TrySetResult(); await CaptureAsync(() => pending); }
        }

        static bool FilesClosed(IEnumerable<OtoEntry> sources)
        {
            try
            {
                foreach (var source in sources)
                {
                    using var exclusive = new FileStream(source.WavePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                }
                return true;
            }
            catch (IOException) { return false; }
        }
    }

    private static PcmAudio Audio(int rate, int count) => new(Enumerable.Range(0, count).Select(i => (short)(6000 * Math.Sin(2 * Math.PI * 210 * i / rate))).ToArray(), rate);
    private static Exception? Capture(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private static async Task<Exception?> CaptureAsync(Func<Task> action) { try { await action(); return null; } catch (Exception error) { return error; } }
    private sealed class Owner : IAsyncDisposable { internal int Disposes; public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; } }
}

