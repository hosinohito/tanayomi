using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Tanayomi.Desktop;

internal static class VoicevoxProtocol
{
    internal const int SampleRate = 24000, MaximumSamples = SampleRate * 45;
    internal const int MaximumWaveBytes = MaximumSamples * 2 + 4096;
    private const int HeaderLength = 16;
    internal sealed record Command(VoicevoxVoice Voice, string? Text);

    internal static byte[] Encode(VoicevoxVoice voice, string? text)
    {
        if (VoicevoxCatalog.Find(voice.StyleId) != voice) throw Invalid();
        var count = text is null ? 0 : OpenJtalkPcmProtocol.ValidateRequestText(text);
        var frame = new byte[HeaderLength + count];
        "VVWQ"u8.CopyTo(frame);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), voice.StyleId);
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(12), count);
        if (text is not null) OpenJtalkPcmProtocol.Utf8.GetBytes(text.AsSpan(), frame.AsSpan(HeaderLength));
        return frame;
    }

    internal static Command? ReadRequest(Stream input)
    {
        Span<byte> frame = stackalloc byte[HeaderLength];
        var first = input.ReadByte();
        if (first < 0) return null;
        frame[0] = (byte)first;
        input.ReadExactly(frame[1..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(frame[12..]);
        var voice = VoicevoxCatalog.Find(BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]));
        if (!frame[..4].SequenceEqual("VVWQ"u8) || BinaryPrimitives.ReadUInt32LittleEndian(frame[4..]) != 1 ||
            count is < 0 or > 4096 || voice is null) throw Invalid();
        if (count == 0) return new(voice, null);
        Span<byte> body = stackalloc byte[count];
        input.ReadExactly(body);
        string text;
        try { text = OpenJtalkPcmProtocol.Utf8.GetString(body); }
        catch (DecoderFallbackException) { throw Invalid(); }
        _ = OpenJtalkPcmProtocol.ValidateRequestText(text);
        return new(voice, text);
    }

    internal static void WriteResponse(Stream output, int status, PcmAudio? audio)
    {
        if (audio is not null) ValidateAudio(audio);
        if (status is < 0 or > 255 || status != 0 && audio is not null) throw Invalid();
        Span<byte> header = stackalloc byte[HeaderLength];
        "VVWR"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], audio?.Samples.Length ?? 0);
        output.Write(header);
        if (audio is not null) output.Write(MemoryMarshal.AsBytes(audio.Samples.AsSpan()));
        output.Flush();
    }

    internal static async Task<PcmAudio?> ReadResponseAsync(Stream input, bool preparation, CancellationToken ct)
    {
        var header = new byte[HeaderLength];
        await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        if (!header.AsSpan(0, 4).SequenceEqual("VVWR"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 ||
            status is < 0 or > 255 || count is < 0 or > MaximumSamples) throw Invalid();
        if (status != 0)
        {
            if (count != 0) throw Invalid();
            throw new VoicevoxWorkerException(status);
        }
        if (preparation && count == 0) return null;
        if (preparation || count == 0) throw Invalid();
        var samples = new short[count];
        var length = count * sizeof(short);
        var buffer = new byte[Math.Min(length, 32 * 1024)];
        for (var offset = 0; offset < length;)
        {
            var size = Math.Min(buffer.Length, length - offset);
            await input.ReadExactlyAsync(buffer.AsMemory(0, size), ct).ConfigureAwait(false);
            Buffer.BlockCopy(buffer, 0, samples, offset, size);
            offset += size;
        }
        var audio = new PcmAudio(samples, SampleRate);
        ValidateAudio(audio);
        return audio;
    }

    internal static PcmAudio DecodeWave(ReadOnlySpan<byte> wave) => DecodeWave(wave, 0, wave.Length);

    internal static PcmAudio DecodeNativeWave(nint address, nuint size)
    {
        if (address == 0 || size is < 46 or > MaximumWaveBytes) throw Invalid();
        return DecodeWave(default, address, (int)size);
    }

    private static PcmAudio DecodeWave(ReadOnlySpan<byte> wave, nint address, int length)
    {
        if (length is < 46 or > MaximumWaveBytes) throw Invalid();
        var scratch = address == 0 ? null : new byte[16];
        var header = ReadWaveHeader(wave, address, scratch, 0, 12);
        if (!header[..4].SequenceEqual("RIFF"u8) ||
            BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != length - 8 ||
            !header.Slice(8, 4).SequenceEqual("WAVE"u8)) throw Invalid();
        var pcmOffset = 0;
        var pcmSize = 0;
        var hasFormat = false;
        for (var offset = 12; offset < length;)
        {
            var remaining = length - offset;
            if (remaining < 8) throw Invalid();
            var chunk = ReadWaveHeader(wave, address, scratch, offset, 8);
            var size = BinaryPrimitives.ReadInt32LittleEndian(chunk[4..]);
            if (size < 0 || size > remaining - 8 || size + (size & 1) > remaining - 8) throw Invalid();
            offset += 8;
            if (chunk[..4].SequenceEqual("fmt "u8))
            {
                if (hasFormat || size < 16) throw Invalid();
                var body = ReadWaveHeader(wave, address, scratch, offset, 16);
                if (BinaryPrimitives.ReadUInt16LittleEndian(body) != 1 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(body[2..]) != 1 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(body[4..]) != SampleRate ||
                    BinaryPrimitives.ReadUInt32LittleEndian(body[8..]) != SampleRate * 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(body[12..]) != 2 ||
                    BinaryPrimitives.ReadUInt16LittleEndian(body[14..]) != 16) throw Invalid();
                hasFormat = true;
            }
            else if (chunk[..4].SequenceEqual("data"u8))
            {
                if (!hasFormat || pcmSize != 0 || size == 0 || (size & 1) != 0 ||
                    size > MaximumSamples * 2) throw Invalid();
                pcmOffset = offset;
                pcmSize = size;
            }
            offset += size + (size & 1);
        }
        if (pcmSize == 0) throw Invalid();
        short[] samples;
        if (address == 0) samples = MemoryMarshal.Cast<byte, short>(wave.Slice(pcmOffset, pcmSize)).ToArray();
        else
        {
            samples = new short[pcmSize / sizeof(short)];
            Marshal.Copy(address + pcmOffset, samples, 0, samples.Length);
        }
        var result = new PcmAudio(samples, SampleRate);
        ValidateAudio(result);
        return result;
    }

    private static ReadOnlySpan<byte> ReadWaveHeader(ReadOnlySpan<byte> wave, nint address, byte[]? scratch, int offset, int size)
    {
        if (scratch is null) return wave.Slice(offset, size);
        Marshal.Copy(address + offset, scratch, 0, size);
        return scratch.AsSpan(0, size);
    }

    internal static void ValidateAudio(PcmAudio audio)
    {
        if (audio.SampleRate != SampleRate || audio.Samples.Length is < 1 or > MaximumSamples ||
            audio.Samples.AsSpan().IndexOfAnyExcept((short)0) < 0) throw Invalid();
    }

    private static InvalidDataException Invalid() => new("VOICEVOX音声ワーカーの形式または上限が不正です。");
}

internal sealed class VoicevoxWorkerException(int status) :
    IOException($"VOICEVOX音声ワーカーが失敗しました（状態{status}）。")
{
    internal int Status { get; } = status;
}
