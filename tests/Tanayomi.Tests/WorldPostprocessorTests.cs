using System.Buffers.Binary;
using Tanayomi;
using Tanayomi.Desktop;

public static class WorldPostprocessorTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var owned = new TestDirectory();
        Voicebank Bank(string name)
        {
            var directory = Path.Combine(owned.DirectoryPath, name);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, "source.wav");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            File.WriteAllText(Path.Combine(directory, "oto.ini"), "source.wav=あ,0,80,-250,15,8");
            return new(name, directory, [new(path, "あ", 0, 80, -250, 15, 8)], 0);
        }
        var input = Audio(4410);
        var a = Bank("A"); var b = Bank("B");
        var references = new List<string>();
        var targets = new List<double>();
        await using (var processor = new DesktopWorldPostprocessor((bank, _) =>
        {
            references.Add(bank.Name);
            var reference = Audio(4410); reference.Samples[0] = bank.Name == "A" ? (short)220 : (short)330;
            return Task.FromResult(reference);
        }, (audio, target, _) =>
        {
            if (target == 0) return Reply(null, audio.Samples[0]);
            targets.Add(target); return Reply(audio, target);
        }))
        {
            await processor.ProcessAsync(input, a, default);
            await processor.ProcessAsync(input, b, default);
            await processor.ProcessAsync(Audio(8820), a, default);
            check(references.SequenceEqual(["A", "B"]) && targets.SequenceEqual([220d, 330d, 220d]),
                "WORLD retains one reference F0 per bank rather than deriving it from each utterance");
            File.WriteAllBytes(a.Entries[0].WavePath, [1, 2, 3, 4, 5]);
            await processor.ProcessAsync(input, a, default);
            check(references.Count == 3, "WORLD source length changes invalidate a bank's calibration");
            File.SetLastWriteTimeUtc(a.Entries[0].WavePath, File.GetLastWriteTimeUtc(a.Entries[0].WavePath).AddSeconds(2));
            await processor.ProcessAsync(input, a, default);
            check(references.Count == 4, "WORLD source timestamp changes invalidate calibration without relying on length");
            a = a with { Entries = [a.Entries[0] with { Preutterance = 16 }] };
            await processor.ProcessAsync(input, a, default);
            check(references.Count == 5, "WORLD changed parsed OTO timing invalidates the prior reference");
            File.AppendAllText(Path.Combine(a.Directory, "oto.ini"), "\n");
            await processor.ProcessAsync(input, a, default);
            check(references.Count == 6, "WORLD external oto.ini edits invalidate a retained bank reference");
        }

        var unusedPath = Path.Combine(a.Directory, "unused.wav");
        var withMissingUnused = a with { Entries = [.. a.Entries, new(unusedPath, "未使用", 0, 80, -250, 15, 8)] };
        var missingReferences = 0;
        await using (var processor = new DesktopWorldPostprocessor((_, _) =>
        {
            missingReferences++; return Task.FromResult(input);
        }, (audio, target, _) => Reply(target == 0 ? null : audio, target == 0 ? 255 : target)))
        {
            check((await processor.ProcessAsync(input, withMissingUnused, default)).Samples.Length == input.Samples.Length && missingReferences == 1,
                "WORLD does not reject usable speech solely because an unused bank source has gone missing");
            File.WriteAllBytes(unusedPath, [1, 2, 3, 4]);
            await processor.ProcessAsync(input, withMissingUnused, default);
            check(missingReferences == 2, "WORLD source reappearance changes the key and invalidates missing-source calibration");
            File.Delete(unusedPath);
            check((await processor.ProcessAsync(input, withMissingUnused, default)).Samples.Length == input.Samples.Length,
                "WORLD source disappearance is represented by a key marker without making unused files mandatory");
        }

        var cached = new Dictionary<string, int>();
        var banks = Enumerable.Range(0, 9).Select(i => Bank("lru-" + i)).ToArray();
        await using (var processor = new DesktopWorldPostprocessor((bank, _) =>
        {
            cached[bank.Name] = cached.GetValueOrDefault(bank.Name) + 1;
            return Task.FromResult(input);
        }, (audio, target, _) => Reply(target == 0 ? null : audio, target == 0 ? 250 : target)))
        {
            for (var i = 0; i < 8; i++) await processor.ProcessAsync(input, banks[i], default);
            await processor.ProcessAsync(input, banks[0], default); // Keep the oldest bank recently used.
            await processor.ProcessAsync(input, banks[8], default);
            await processor.ProcessAsync(input, banks[0], default);
            await processor.ProcessAsync(input, banks[1], default);
            check(cached.Count == 9 && cached[banks[0].Name] == 1 && cached[banks[1].Name] == 2 && cached.Values.Sum() == 10,
                "WORLD reference cache holds eight entries and evicts the least recently used bank");
        }

        var calibrationCalls = 0; var fail = true;
        await using (var processor = new DesktopWorldPostprocessor((_, _) =>
        {
            calibrationCalls++; return Task.FromResult(input);
        }, (audio, target, _) =>
        {
            if (target == 0) return Reply(null, 260);
            if (fail) { fail = false; throw new IOException("fake native exchange failure"); }
            return Reply(audio, target);
        }))
        {
            await ThrowsAsync<IOException>(() => processor.ProcessAsync(input, a, default), check,
                "WORLD exchange failures propagate for queue fallback rather than returning fake audio");
            var recovered = await processor.ProcessAsync(input, a, default);
            check(recovered.Samples.SequenceEqual(input.Samples) && calibrationCalls == 1,
                "WORLD continues after exchange failure while retaining a completed bank calibration");
        }

        using (var cancel = new CancellationTokenSource())
        {
            var calls = 0; var cancelMeasurement = true;
            await using var processor = new DesktopWorldPostprocessor((_, _) =>
            {
                calls++; return Task.FromResult(input);
            }, (audio, target, _) =>
            {
                if (target == 0 && cancelMeasurement) { cancelMeasurement = false; cancel.Cancel(); }
                return Reply(target == 0 ? null : audio, target == 0 ? 270 : target);
            });
            await ThrowsAsync<OperationCanceledException>(() => processor.ProcessAsync(input, a, cancel.Token), check,
                "WORLD observes cancellation even when a measurement delegate completes normally at the boundary");
            await processor.ProcessAsync(input, a, default);
            check(calls == 2, "WORLD never caches a measurement completed after its request was cancelled");
        }

        using (var cancel = new CancellationTokenSource())
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var block = true;
            await using var processor = new DesktopWorldPostprocessor((_, _) => Task.FromResult(input), async (audio, target, ct) =>
            {
                if (target == 0) return (null, 280d);
                if (block) { entered.TrySetResult(); await Task.Delay(Timeout.Infinite, ct); }
                return (audio, target);
            });
            var pending = processor.ProcessAsync(input, a, cancel.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancel.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => pending, check, "WORLD cancels an active exchange promptly");
            block = false;
            var recovered = await processor.ProcessAsync(input, a, default);
            check(recovered.Samples.Length == input.Samples.Length, "WORLD accepts the next request after a cancelled exchange");
        }

        var unstable = true; var factoryCalls = 0;
        await using (var processor = new DesktopWorldPostprocessor((bank, _) =>
        {
            factoryCalls++;
            if (unstable) { File.AppendAllText(Path.Combine(bank.Directory, "oto.ini"), "\n"); unstable = false; }
            return Task.FromResult(input);
        }, (audio, target, _) => Reply(target == 0 ? null : audio, target == 0 ? 240 : target)))
        {
            await ThrowsAsync<IOException>(() => processor.ProcessAsync(input, a, default), check,
                "WORLD rejects calibration if the bank changes while the reference is being prepared");
            await processor.ProcessAsync(input, a, default);
            check(factoryCalls == 2, "WORLD retries changed-bank calibration without keeping its stale result");
        }

        var badCalibration = true;
        await using (var processor = new DesktopWorldPostprocessor((_, _) => Task.FromResult(input), (audio, target, _) =>
            Reply(target == 0 ? null : audio, target == 0 ? (badCalibration ? double.NaN : 230) : target)))
        {
            await ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(input, a, default), check,
                "WORLD rejects a nonfinite calibrated F0 before processing an utterance");
            badCalibration = false;
            check((await processor.ProcessAsync(input, a, default)).Samples.Length == input.Samples.Length,
                "WORLD can recover after a malformed calibration response");
        }

        await using (var processor = new DesktopWorldPostprocessor((_, _) => Task.FromResult(input),
            (audio, target, _) => Reply(target == 0 ? null : audio, target == 0 ? 220 : target)))
        {
            var manyEntries = a with { Entries = Enumerable.Repeat(a.Entries[0], 10001).ToArray() };
            check((await processor.ProcessAsync(input, manyEntries, default)).Samples.Length == input.Samples.Length,
                "WORLD does not narrow the existing OTO import allowance with an independent 10000-entry cap");
            var longest = Audio(WorldPcmProtocol.MaximumSamples);
            var result = await processor.ProcessAsync(longest, a, default);
            check(result.SampleRate == 44100 && result.Samples.Length == 44100 * 45 && result.Samples.SequenceEqual(longest.Samples),
                "WORLD accepts exactly 45 seconds and preserves the full PCM duration");
            await ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(Audio(4409), a, default), check,
                "WORLD excludes inputs shorter than the native 100ms analysis bound");
            await ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(Audio(WorldPcmProtocol.MaximumSamples + 1), a, default), check,
                "WORLD rejects PCM beyond 45 seconds before native exchange");
            await ThrowsAsync<InvalidDataException>(() => processor.ProcessAsync(new(input.Samples, 22050), a, default), check,
                "WORLD rejects an incompatible sample rate rather than changing duration");
        }
        await CheckWarmupAsync(input, a, check);
        await CheckDisposalAsync(input, a, check);
        await CheckProtocolAsync(input, check);
    }

    private static async Task CheckWarmupAsync(PcmAudio input, Voicebank bank, Action<bool, string> check)
    {
        var references = 0; var analyses = 0; var syntheses = 0;
        await using (var processor = new DesktopWorldPostprocessor((_, _) =>
        {
            references++; return Task.FromResult(input);
        }, (audio, target, _) =>
        {
            if (target == 0) analyses++; else syntheses++;
            return Reply(target == 0 ? null : audio, target == 0 ? 245 : target);
        }))
        {
            await processor.PrepareAsync(bank, default);
            await processor.PrepareAsync(bank, default);
            check(references == 1 && analyses == 1 && syntheses == 0,
                "WORLD bank warmup calibrates once without synthesizing or replaying an utterance");
            await processor.ProcessAsync(input, bank, default);
            check(references == 1 && analyses == 1 && syntheses == 1,
                "WORLD speech reuses the exact calibration cache populated by warmup");
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        analyses = 0; syntheses = 0;
        await using (var processor = new DesktopWorldPostprocessor((_, _) => Task.FromResult(input), async (audio, target, ct) =>
        {
            if (target == 0)
            {
                analyses++; entered.TrySetResult(); await release.Task.WaitAsync(ct); return (null, 245d);
            }
            syntheses++; return (audio, target);
        }))
        {
            var preparing = processor.PrepareAsync(bank, default);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
                using var cancel = new CancellationTokenSource();
                var waitingSpeech = processor.ProcessAsync(input, bank, cancel.Token);
                cancel.Cancel();
                await ThrowsAsync<OperationCanceledException>(() => waitingSpeech, check,
                    "cancelling speech queued behind WORLD warmup cancels only that waiting request");
                check(!preparing.IsCompleted && analyses == 1 && syntheses == 0,
                    "speech cancellation cannot abort a different request's in-flight WORLD calibration");
                var followingSpeech = processor.ProcessAsync(input, bank, default);
                release.TrySetResult();
                await preparing.WaitAsync(TimeSpan.FromSeconds(3));
                await followingSpeech.WaitAsync(TimeSpan.FromSeconds(3));
                check(analyses == 1 && syntheses == 1,
                    "concurrent WORLD warmup and speech serialize around one shared calibration");
            }
            finally { release.TrySetResult(); }
        }
        var fail = true; references = 0;
        await using (var processor = new DesktopWorldPostprocessor((_, _) =>
        {
            references++; return Task.FromResult(input);
        }, (audio, target, _) =>
        {
            if (target == 0 && fail) { fail = false; throw new IOException("fake warmup failure"); }
            return Reply(target == 0 ? null : audio, target == 0 ? 245 : target);
        }))
        {
            await ThrowsAsync<IOException>(() => processor.PrepareAsync(bank, default), check,
                "WORLD warmup failures remain explicit and do not create a successful cache entry");
            await processor.ProcessAsync(input, bank, default);
            check(references == 2, "speech retries calibration normally after an unsuccessful warmup");
        }
    }

    private static async Task CheckDisposalAsync(PcmAudio input, Voicebank bank, Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var processor = new DesktopWorldPostprocessor((_, _) => Task.FromResult(input), async (audio, target, ct) =>
        {
            if (target == 0) return (null, 250d);
            entered.TrySetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            catch (OperationCanceledException)
            {
                cancelling.TrySetResult();
                await release.Task; // Simulate resource cleanup which has not finished yet.
                throw;
            }
            return (audio, target);
        });
        Task<PcmAudio>? pending = null;
        try
        {
            pending = processor.ProcessAsync(input, bank, default);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            var first = processor.DisposeAsync().AsTask();
            var second = processor.DisposeAsync().AsTask();
            await cancelling.Task.WaitAsync(TimeSpan.FromSeconds(3));
            check(!first.IsCompleted && !second.IsCompleted,
                "WORLD concurrent disposal calls both wait until the active exchange releases resources");
            release.TrySetResult();
            await ThrowsAsync<OperationCanceledException>(() => pending!, check,
                "WORLD disposal cancels an active request rather than returning partial audio");
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(3));
            await ThrowsAsync<ObjectDisposedException>(() => processor.ProcessAsync(input, bank, default), check,
                "WORLD rejects new processing after shared disposal completes");
        }
        finally
        {
            release.TrySetResult();
            await processor.DisposeAsync();
            if (pending is not null)
                try { await pending; } catch (OperationCanceledException) { }
        }
    }

    private static async Task CheckProtocolAsync(PcmAudio input, Action<bool, string> check)
    {
        var request = WorldPcmProtocol.EncodeRequest(input, 275);
        using (var fragmented = new FragmentedStream(request))
        {
            var decoded = WorldPcmProtocol.ReadRequest(fragmented)!;
            check(decoded.SampleCount == input.Samples.Length && decoded.TargetF0 == 275 &&
                BinaryPrimitives.ReadInt16LittleEndian(decoded.Pcm) == input.Samples[0] &&
                BinaryPrimitives.ReadInt16LittleEndian(decoded.Pcm.AsSpan(decoded.Pcm.Length - 2)) == input.Samples[^1],
                "WORLD binary request survives fragmented reads with exact signed PCM16 endpoints");
            check(WorldPcmProtocol.ReadRequest(fragmented) is null, "WORLD treats EOF only between complete requests as normal shutdown");
        }
        using (var truncated = new MemoryStream(request[..^1]))
            await ThrowsAsync<EndOfStreamException>(() => { WorldPcmProtocol.ReadRequest(truncated); return Task.CompletedTask; }, check,
                "WORLD rejects a truncated request body instead of accepting partial PCM");
        using (var truncated = new MemoryStream(request[..10]))
            await ThrowsAsync<EndOfStreamException>(() => { WorldPcmProtocol.ReadRequest(truncated); return Task.CompletedTask; }, check,
                "WORLD distinguishes a partial request header from clean EOF");
        var badMagic = request[..WorldPcmProtocol.RequestHeaderBytes]; badMagic[0] = 0;
        using (var source = new MemoryStream(badMagic))
            await ThrowsAsync<InvalidDataException>(() => { WorldPcmProtocol.ReadRequest(source); return Task.CompletedTask; }, check,
                "WORLD rejects an unrelated request protocol before reading a body");
        foreach (var (offset, value) in new[] { (4, 3), (8, int.MaxValue), (8, -1), (8, 4409) })
        {
            var bad = request[..WorldPcmProtocol.RequestHeaderBytes];
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), value);
            using var source = new MemoryStream(bad);
            await ThrowsAsync<InvalidDataException>(() => { WorldPcmProtocol.ReadRequest(source); return Task.CompletedTask; }, check,
                "WORLD validates request version and sample bounds before allocating or reading its body");
        }
        foreach (var frequency in new[] { double.NaN, double.PositiveInfinity, -1, 39, 1201 })
        {
            var bad = request[..WorldPcmProtocol.RequestHeaderBytes];
            BinaryPrimitives.WriteDoubleLittleEndian(bad.AsSpan(12), frequency);
            using var source = new MemoryStream(bad);
            await ThrowsAsync<InvalidDataException>(() => { WorldPcmProtocol.ReadRequest(source); return Task.CompletedTask; }, check,
                "WORLD rejects nonfinite or out-of-range target F0 before reading a body");
        }
        using var response = new MemoryStream();
        WorldPcmProtocol.WriteResponse(response, WorldPcmProtocol.Success, 275, request.AsSpan(WorldPcmProtocol.RequestHeaderBytes));
        using (var fragmented = new FragmentedStream(response.ToArray()))
        {
            var decoded = await WorldPcmProtocol.ReadResponseAsync(fragmented, input.Samples.Length, 275, default);
            check(decoded.F0 == 275 && decoded.Audio!.Samples.SequenceEqual(input.Samples),
                "WORLD binary response preserves all PCM samples through fragmented pipe reads");
        }
        using (var truncated = new MemoryStream(response.ToArray()[..^1]))
            await ThrowsAsync<EndOfStreamException>(() => WorldPcmProtocol.ReadResponseAsync(truncated, input.Samples.Length, 275, default), check,
                "WORLD rejects a truncated response body rather than shortening a spoken utterance");
        foreach (var frequency in new[] { double.NaN, double.PositiveInfinity, 276 })
        {
            var bad = response.ToArray()[..WorldPcmProtocol.ResponseHeaderBytes];
            BinaryPrimitives.WriteDoubleLittleEndian(bad.AsSpan(16), frequency);
            using var source = new MemoryStream(bad);
            await ThrowsAsync<InvalidDataException>(() => WorldPcmProtocol.ReadResponseAsync(source, input.Samples.Length, 275, default), check,
                "WORLD rejects nonfinite or mismatched synthesis F0 before reading a body");
        }
        response.SetLength(0); response.Position = 0;
        WorldPcmProtocol.WriteResponse(response, WorldPcmProtocol.Success, 0, request.AsSpan(WorldPcmProtocol.RequestHeaderBytes));
        response.Position = 0;
        var passthrough = await WorldPcmProtocol.ReadResponseAsync(response, input.Samples.Length, 275, default);
        check(passthrough.F0 == 0 && passthrough.Audio!.Samples.SequenceEqual(input.Samples), "WORLD accepts exact all-unvoiced PCM passthrough with F0 zero");
        response.SetLength(0); response.Position = 0;
        WorldPcmProtocol.WriteResponse(response, WorldPcmProtocol.Success, 246.5, []);
        response.Position = 0;
        var analysis = await WorldPcmProtocol.ReadResponseAsync(response, input.Samples.Length, 0, default);
        check(analysis.Audio is null && analysis.F0 == 246.5, "WORLD calibration returns only a finite median F0 without PCM");
        response.SetLength(0); response.Position = 0;
        WorldPcmProtocol.WriteResponse(response, WorldPcmProtocol.InsufficientVoicing, 0, []);
        response.Position = 0;
        await ThrowsAsync<InvalidDataException>(() => WorldPcmProtocol.ReadResponseAsync(response, input.Samples.Length, 0, default), check,
            "WORLD insufficient calibration voicing becomes an explicit failure rather than a fabricated F0");
        foreach (var (offset, value) in new[] { (0, 0), (4, 2), (8, 1), (12, int.MaxValue), (12, -1) })
        {
            var bad = new byte[WorldPcmProtocol.ResponseHeaderBytes];
            "TWPR"u8.CopyTo(bad); BinaryPrimitives.WriteUInt32LittleEndian(bad.AsSpan(4), 1);
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(12), input.Samples.Length);
            BinaryPrimitives.WriteDoubleLittleEndian(bad.AsSpan(16), 275);
            BinaryPrimitives.WriteInt32LittleEndian(bad.AsSpan(offset), value);
            using var source = new MemoryStream(bad);
            await ThrowsAsync<InvalidDataException>(() => WorldPcmProtocol.ReadResponseAsync(source, input.Samples.Length, 275, default), check,
                "WORLD validates response magic version status and exact bounded count before allocating PCM");
        }
        var truncatedResponse = response.ToArray()[..10];
        using (var source = new MemoryStream(truncatedResponse))
            await ThrowsAsync<EndOfStreamException>(() => WorldPcmProtocol.ReadResponseAsync(source, input.Samples.Length, 0, default), check,
                "WORLD rejects a partial response header");
    }

    private static PcmAudio Audio(int count)
    {
        var samples = new short[count];
        for (var i = 0; i < count; i++) samples[i] = (short)(i % 101 * 503 - 25000);
        return new(samples, 44100);
    }
    private static Task<(PcmAudio? Audio, double F0)> Reply(PcmAudio? audio, double f0) => Task.FromResult((audio, f0));
    private static async Task ThrowsAsync<T>(Func<Task> action, Action<bool, string> check, string message) where T : Exception
    {
        var failed = false;
        try { await action().WaitAsync(TimeSpan.FromSeconds(5)); }
        catch (T) { failed = true; }
        check(failed, message);
    }
    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(3, buffer.Length)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(3, buffer.Length)], ct);
    }
}
