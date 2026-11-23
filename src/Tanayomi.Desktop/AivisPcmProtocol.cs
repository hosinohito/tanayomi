using System.Buffers.Binary;
using System.Text;

namespace Tanayomi.Desktop;

internal static class AivisPcmProtocol
{
    internal const int SampleRate = 44100;
    internal const int MaxSamples = 45 * SampleRate;
    private const int MaxTextBytes = 4096;
    private const int MaxTextCharacters = 500;
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);

    internal static byte[] Encode(int voiceIndex, string? text)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(voiceIndex);
        var length = 0;
        if (text is not null)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length > 2 * MaxTextCharacters || text.Contains('\0') ||
                text.EnumerateRunes().Count() > MaxTextCharacters)
                throw new ArgumentException("Aivis音声に渡せない読み上げテキストです。", nameof(text));
            length = Utf8.GetByteCount(text);
            if (length > MaxTextBytes) throw new ArgumentException("Aivis音声の入力が長すぎます。", nameof(text));
        }
        var frame = new byte[16 + length];
        "AVQ1"u8.CopyTo(frame);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(4), text is null ? 0 : 1);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(8), voiceIndex);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), length);
        if (text is not null) Utf8.GetBytes(text, frame.AsSpan(16));
        return frame;
    }

    internal static async Task<PcmAudio?> ReadResponseAsync(Stream stream, bool preparation, CancellationToken ct)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        if (!header.AsSpan(0, 4).SequenceEqual("AVR1"u8)) throw InvalidFrame();
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (status == 1)
        {
            if (rate != 0 || length is < 1 or > 64) throw InvalidFrame();
            var category = new byte[length];
            await stream.ReadExactlyAsync(category, ct).ConfigureAwait(false);
            if (category.Any(value => value is not (>= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z' or
                    >= (byte)'0' and <= (byte)'9' or (byte)'_')))
                throw InvalidFrame();
            throw new IOException($"AIVIS-WORKER: Aivis音声ワーカーが失敗しました ({Encoding.ASCII.GetString(category)})。");
        }
        if (status != 0) throw InvalidFrame();
        if (preparation)
        {
            if (rate != 0 || length != 0) throw InvalidFrame();
            return null;
        }
        if (rate != SampleRate || length is <= 0 or > MaxSamples * 2 || (length & 1) != 0) throw InvalidFrame();
        var samples = new short[length / 2];
        var buffer = new byte[Math.Min(length, 32 * 1024)];
        for (var offset = 0; offset < length;)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(buffer.Length, length - offset);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), ct).ConfigureAwait(false);
            Buffer.BlockCopy(buffer, 0, samples, offset, count);
            offset += count;
        }
        ct.ThrowIfCancellationRequested();
        return new(samples, rate);
    }

    private static IOException InvalidFrame() => new("AIVIS-PROTOCOL: Aivis音声の応答が不正です。");
}
