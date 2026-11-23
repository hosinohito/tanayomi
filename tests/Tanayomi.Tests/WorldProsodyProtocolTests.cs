using System.Buffers.Binary;
using Tanayomi;
using Tanayomi.Desktop;

internal static class WorldProsodyProtocolTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        var pcm = Enumerable.Range(0, 4542).Select(i => (short)(i % 1000 - 500)).ToArray();
        var audio = new PcmAudio(pcm, 44100);
        var count = WorldPcmProtocol.ContourFrames(pcm.Length);
        var contour = Enumerable.Range(0, count).Select(i => 200.0 + i * 4).ToArray();
        var flat = WorldPcmProtocol.EncodeRequest(audio, 260);
        var optionalFlat = WorldPcmProtocol.EncodeRequest(audio, 260, null);
        var golden = new byte[20 + pcm.Length * 2];
        "TWPC"u8.CopyTo(golden); BinaryPrimitives.WriteUInt32LittleEndian(golden.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(golden.AsSpan(8), pcm.Length);
        BinaryPrimitives.WriteDoubleLittleEndian(golden.AsSpan(12), 260);
        for (var i = 0; i < pcm.Length; ++i) BinaryPrimitives.WriteInt16LittleEndian(golden.AsSpan(20 + i * 2), pcm[i]);
        check(flat.SequenceEqual(optionalFlat) && flat.SequenceEqual(golden), "WORLD flat v1 request bytes remain unchanged when contour is absent");
        using (var stream = new MemoryStream(flat))
            check(WorldPcmProtocol.ReadRequest(stream) is { Contour: null, TargetF0: 260 }, "WORLD v1 decode retains its original flat request semantics");
        var encoded = WorldPcmProtocol.EncodeRequest(audio, 260, contour);
        using (var stream = new Fragmented(encoded))
        {
            var decoded = WorldPcmProtocol.ReadRequest(stream)!;
            check(decoded.SampleCount == pcm.Length && decoded.TargetF0 == 260 && decoded.Contour!.SequenceEqual(contour) &&
                decoded.Pcm.SequenceEqual(golden.AsSpan(20).ToArray()), "WORLD v2 accepts fragmented PCM and exact-grid positive context contours");
            check(WorldPcmProtocol.ReadRequest(stream) is null, "WORLD v2 clean EOF between requests remains normal shutdown");
        }
        foreach (var badCount in new[] { -1, 0, count - 1, count + 1, 9002, int.MaxValue })
        {
            var header = encoded[..24]; BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(20), badCount);
            using var stream = new MemoryStream(header);
            check(Fails(() => WorldPcmProtocol.ReadRequest(stream)) is InvalidDataException && stream.Position == 24,
                "WORLD v2 rejects contour dimensions before reading or allocating a PCM body");
        }
        foreach (var value in new[] { 0.0, -1.0, 39.9, 1200.1, double.NaN, double.PositiveInfinity })
        {
            var malformed = encoded.ToArray(); BinaryPrimitives.WriteDoubleLittleEndian(malformed.AsSpan(24 + pcm.Length * 2), value);
            using var stream = new MemoryStream(malformed);
            check(Fails(() => WorldPcmProtocol.ReadRequest(stream)) is InvalidDataException,
                "WORLD v2 refuses nonpositive, nonfinite or unsupported-Hz contour frames");
        }
        var analysis = encoded[..24]; BinaryPrimitives.WriteDoubleLittleEndian(analysis.AsSpan(12), 0);
        using (var stream = new MemoryStream(analysis))
            check(Fails(() => WorldPcmProtocol.ReadRequest(stream)) is InvalidDataException,
                "WORLD v2 cannot request calibration with target zero");
        using (var truncated = new Fragmented(encoded[..^1]))
            check(Fails(() => WorldPcmProtocol.ReadRequest(truncated)) is EndOfStreamException,
                "WORLD v2 rejects a partial final contour double instead of synthesizing partial metadata");
        check(Fails(() => WorldPcmProtocol.EncodeRequest(audio, 0, contour)) is InvalidDataException &&
            Fails(() => WorldPcmProtocol.EncodeRequest(audio, 260, contour[..^1])) is InvalidDataException,
            "WORLD v2 encoder enforces target and exact frame shape before transport");

        var detected = Enumerable.Range(0, count).Select(i => i % 4 == 0 ? 0.0 : 220.0).ToArray();
        var originalMask = detected.Select(value => value > 0).ToArray(); var originalContour = contour.ToArray();
        WorldPcmWorker.ApplyContextContour(detected, contour, pcm.Length, 260);
        double duration = 0, sum = 0;
        for (var i = 0; i < detected.Length; ++i)
        {
            if (!originalMask[i]) continue;
            var weight = Math.Min(0.005, Math.Max(0, pcm.Length / 44100.0 - i * 0.005));
            duration += weight; sum += detected[i] * weight;
        }
        check(detected.Select(value => value > 0).SequenceEqual(originalMask) && Math.Abs(sum / duration - 260) < 1e-9 &&
            contour.SequenceEqual(originalContour) && detected.Where(value => value > 0).Distinct().Count() > 1,
            "WORLD context F0 preserves native VUV and curve shape while matching the time-weighted voiced mean");
        foreach (var target in new[] { 40.0, 1200.0 })
        {
            var voiced = Enumerable.Repeat(220.0, count).ToArray();
            WorldPcmWorker.ApplyContextContour(voiced, Enumerable.Repeat(200.0, count).ToArray(), pcm.Length, target);
            check(voiced.All(value => Math.Abs(value - target) < 1e-9), "WORLD contour normalization supports constant curves at its exact frequency endpoints");
        }
        foreach (var voices in new[] { 0, 1, 9 })
        {
            var sparse = Enumerable.Range(0, count).Select(i => i < voices ? 200.0 : 0).ToArray(); var snapshot = sparse.ToArray();
            check(Fails(() => WorldPcmWorker.ApplyContextContour(sparse, contour, pcm.Length, 260)) is InvalidDataException && sparse.SequenceEqual(snapshot),
                "WORLD contour application refuses insufficient native voicing without partially modifying the F0 track");
        }
        var extreme = Enumerable.Repeat(200.0, count).ToArray(); var before = extreme.ToArray();
        var wide = Enumerable.Range(0, count).Select(i => i % 2 == 0 ? 40.0 : 1200.0).ToArray();
        check(Fails(() => WorldPcmWorker.ApplyContextContour(extreme, wide, pcm.Length, 40)) is InvalidDataException && extreme.SequenceEqual(before),
            "WORLD refuses out-of-range normalized pitch rather than clipping contour shape or committing partial output");
        using var response = new MemoryStream();
        WorldPcmProtocol.WriteResponse(response, WorldPcmProtocol.Success, 260, golden.AsSpan(20)); response.Position = 0;
        var result = await WorldPcmProtocol.ReadResponseAsync(response, pcm.Length, 260, CancellationToken.None);
        check(result.Audio!.Samples.SequenceEqual(pcm), "WORLD v2 requests retain the existing v1 PCM response decoder");
    }
    private static Exception? Fails(Action action) { try { action(); return null; } catch (Exception error) { return error; } }
    private sealed class Fragmented(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(3, buffer.Length)]);
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(3, count));
    }
}
