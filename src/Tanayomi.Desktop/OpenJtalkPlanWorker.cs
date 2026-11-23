using System.Buffers.Binary;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Text-to-context-plan host. No synthesis/playback, audit paths or
/// files are supplied by callers. All assets come from the product resources.</summary>
internal static class OpenJtalkPlanWorker
{
    internal static int RunWorker(string[] args)
    {
        try { return RunCore(args); }
        catch (EndOfStreamException) { return 3; }
        catch (InvalidDataException) { return 2; }
        catch (Exception) { return 5; }
    }
    private static int RunCore(string[] args)
    {
        OpenJtalkPcmWorker.NativeSession? native = null;
        try
        {
            if (args.Length != 1 || args[0] != "--open-jtalk-plan-worker") return 2;
            if (!OpenJtalkPcmWorker.ConfigureNativeHost()) return 4;
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            while (OpenJtalkPlanProtocol.ReadRequest(input) is { } text)
            {
                try
                {
                    native ??= new OpenJtalkPcmWorker.NativeSession();
                    var plan = native.Plan(text);
                    OpenJtalkPlanProtocol.WriteResponse(output, 0, plan);
                }
                catch (OpenJtalkWorkerException error)
                {
                    OpenJtalkPlanProtocol.WriteResponse(output, error.Status, null); return 4;
                }
                catch (Exception)
                {
                    OpenJtalkPlanProtocol.WriteResponse(output, 7, null); return 4;
                }
            }
            return 0;
        }
        finally { native?.Dispose(); } // Outer boundary catches cleanup failures too.
    }
}

/// <summary>LE v1. Request OJLC/u32 version/i32 UTF8 bytes/body. Response
/// OJLR/u32 version/i32 status/rate/period/phone count/frame count (28 bytes).
/// Success body: each phone = 32-byte zero-padded ASCII slot + i32 start/end;
/// each frame = f64 natural-log Hz + u8 VUV. Maximum body is 408680 bytes.
/// Errors 1..7 have zero rate/period/counts and no body; worker then exits.</summary>
internal static class OpenJtalkPlanProtocol
{
    internal const int MaximumPhones = 8192, MaximumFrames = 9000, SlotBytes = 32;
    internal const int RequestHeaderBytes = 12, ResponseHeaderBytes = 28, MaximumBodyBytes = 408680;
    internal static byte[] EncodeRequest(string text) => OpenJtalkPcmProtocol.EncodeTextRequest(text, "OJLC"u8);
    internal static byte[]? ReadRequest(Stream input) => OpenJtalkPcmProtocol.ReadTextRequest(input, "OJLC"u8);

    internal static async Task<OpenJtalkPlan> ReadResponseAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[ResponseHeaderBytes]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12));
        var period = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        var phones = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(20));
        var frames = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(24));
        if (!header.AsSpan(0, 4).SequenceEqual("OJLR"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 ||
            status is < 0 or > 7) throw Invalid();
        if (status != 0)
        {
            if (rate != 0 || period != 0 || phones != 0 || frames != 0) throw Invalid();
            throw new OpenJtalkWorkerException(status);
        }
        ValidateLayout(phones, frames, rate, period);
        var bytes = new byte[checked(phones * 40 + frames * 9)];
        await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var decoded = new OpenJtalkPhone[phones]; var lf0 = new double[frames]; var voiced = new byte[frames];
        var offset = 0;
        for (var i = 0; i < phones; ++i)
        {
            var symbol = ReadSymbol(bytes.AsSpan(offset, SlotBytes));
            var start = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 32));
            var end = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 36));
            decoded[i] = new(symbol, start, end); offset += 40;
        }
        for (var i = 0; i < frames; ++i)
        {
            lf0[i] = BinaryPrimitives.ReadDoubleLittleEndian(bytes.AsSpan(offset)); voiced[i] = bytes[offset + 8]; offset += 9;
        }
        return new OpenJtalkPlan(decoded, lf0, voiced, rate, period);
    }

    internal static OpenJtalkPlan FromNative(byte[] slots, int[] starts, int[] ends, double[] lf0, byte[] voiced,
        int phones, int frames, int rate, int period)
    {
        ValidateLayout(phones, frames, rate, period);
        if (slots.Length < phones * SlotBytes || starts.Length < phones || ends.Length < phones || lf0.Length < frames || voiced.Length < frames)
            throw Invalid();
        var decoded = new OpenJtalkPhone[phones];
        for (var i = 0; i < phones; ++i)
            decoded[i] = new(ReadSymbol(slots.AsSpan(i * SlotBytes, SlotBytes)), starts[i], ends[i]);
        return new OpenJtalkPlan(decoded, lf0.AsSpan(0, frames).ToArray(), voiced.AsSpan(0, frames).ToArray(), rate, period);
    }
    internal static OpenJtalkPlan Validate(OpenJtalkPlan plan)
    {
        if (plan is null) throw Invalid();
        ValidateLayout(plan.Phones.Count, plan.FrameCount, plan.SampleRate, plan.FramePeriod);
        foreach (var phone in plan.Phones)
            if (phone is null || phone.Symbol.Length is < 1 or > 31 || phone.Symbol.Any(c => c > 127 || !PhoneCharacter((byte)c)))
                throw Invalid();
        return new OpenJtalkPlan(plan.Phones, plan.Lf0, plan.Voiced, plan.SampleRate, plan.FramePeriod);
    }
    internal static void WriteResponse(Stream output, int status, OpenJtalkPlan? plan)
    {
        if (status is < 0 or > 7 || (status == 0 ? plan is null : plan is not null)) throw Invalid();
        if (plan is not null) plan = Validate(plan);
        Span<byte> header = stackalloc byte[ResponseHeaderBytes];
        "OJLR"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], plan?.SampleRate ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], plan?.FramePeriod ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], plan?.Phones.Count ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], plan?.FrameCount ?? 0);
        // Build a complete bounded body before writing any successful header.
        byte[]? body = null;
        if (plan is not null)
        {
            body = new byte[checked(plan.Phones.Count * 40 + plan.FrameCount * 9)]; var offset = 0;
            foreach (var phone in plan.Phones)
            {
                Encoding.ASCII.GetBytes(phone.Symbol, body.AsSpan(offset, SlotBytes));
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 32), phone.StartFrame);
                BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(offset + 36), phone.EndFrame); offset += 40;
            }
            for (var i = 0; i < plan.FrameCount; ++i)
            {
                BinaryPrimitives.WriteDoubleLittleEndian(body.AsSpan(offset), plan.Lf0[i]); body[offset + 8] = plan.Voiced[i]; offset += 9;
            }
        }
        output.Write(header); if (body is not null) output.Write(body); output.Flush();
    }
    private static void ValidateLayout(int phones, int frames, int rate, int period)
    {
        if (phones is < 1 or > MaximumPhones || frames is < 1 or > MaximumFrames || rate != 48000 || period != 240 ||
            checked(phones * 40 + frames * 9) > MaximumBodyBytes) throw Invalid();
    }
    private static string ReadSymbol(ReadOnlySpan<byte> slot)
    {
        var end = slot.IndexOf((byte)0);
        if (end is < 1 or > 31) throw Invalid();
        for (var i = 0; i < SlotBytes; ++i)
            if (i < end ? !PhoneCharacter(slot[i]) : slot[i] != 0) throw Invalid();
        return Encoding.ASCII.GetString(slot[..end]);
    }
    private static bool PhoneCharacter(byte c) => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
        or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    private static InvalidDataException Invalid() => new("文脈計画ワーカーの形式または上限が不正です。");
}
