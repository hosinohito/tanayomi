using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tanayomi.Desktop;

/// <summary>Production text-to-PCM host. Its launching process owns timeout,
/// cancellation and reap. No native pointer, file output or native text escapes.</summary>
internal static class OpenJtalkPcmWorker
{
    internal const string DefaultVoiceId = "nit-atr503";
    internal const string TohokuVoiceId = "tohoku-f01-neutral";
    private const int MaximumModelBytes = 3 * 1024 * 1024;
    internal sealed record HtsModel(string Id, string Name, string Sha256, string? ResourceFile = null)
    {
        internal string FileName => ResourceFile ?? Id + ".htsvoice";
    }
    internal static IReadOnlyList<HtsModel> AdditionalModels { get; } = Array.AsReadOnly<HtsModel>([
        new(TohokuVoiceId, "東北大 f01（ノーマル）", "DED6ACB4243B93CC50896199D67B00A7C487662BFCDCD3F9F30A4FB0444B0B46"),
        new("tohoku-f01-angry", "東北大 f01（怒り）", "C246AFAC0D41ADA4010DB80CA673B6B51CB8E16E9F3A47487D0DF3D2CA5B7EC8"),
        new("tohoku-f01-happy", "東北大 f01（喜び）", "6BF9559A0757C5250165DE40DDA034A7E95504F3A3CE4ABCE58532CEE1040CC0"),
        new("tohoku-f01-sad", "東北大 f01（悲しみ）", "1C76D4D5646C46E13410651C4232B95AF8B878E67AFD370A605B5DE792BA17EE"),
        new("mei-normal", "Mei（ノーマル）", "B19E8E5DDEF4D9D9559D654F395818E2BB3C0BBDBBCA533F96086DE21AC5DB2B", "mei_normal.htsvoice"),
        new("mei-angry", "Mei（怒り）", "56A0759A4723CEC340E3BBA26199B64B9DF786F36C2851F7A15FB3D6D32B9471", "mei_angry.htsvoice"),
        new("mei-bashful", "Mei（照れ）", "20F7ECD45E3DE5D9C43F28E4D9175C8DFEFB7A002CF4F03208D1431443626FBB", "mei_bashful.htsvoice"),
        new("mei-happy", "Mei（喜び）", "93EC37412999548074249841C6339880E5ED34E946EEE1B4DFC19460307F8B2D", "mei_happy.htsvoice"),
        new("mei-sad", "Mei（悲しみ）", "C06F110F7832C6E970B8E1D0870FF4F68A4F4D06CEABF8B7190A3C3892CB65F2", "mei_sad.htsvoice"),
        new("takumi-normal", "Takumi（ノーマル）", "9130CC911C881CC2C4C442A67374D8601B436F9DD415B555FE3C2D2C94444D42", "takumi_normal.htsvoice"),
        new("takumi-angry", "Takumi（怒り）", "773FD59058B976D2B5E8B1091B30ABE80BB38C521C99BCE2BD806E9E2E6EA173", "takumi_angry.htsvoice"),
        new("takumi-happy", "Takumi（喜び）", "D96F34309F3EA83322C0CFF53AA43362E42680DB350F966CE0A660A3A1292794", "takumi_happy.htsvoice"),
        new("takumi-sad", "Takumi（悲しみ）", "F10A82F6B2A416FEB3332635C3F416698A14707E3337114A5ED81273C39E010D", "takumi_sad.htsvoice")]);
    internal static HtsModel? FindModel(string voiceId) => AdditionalModels.FirstOrDefault(model => model.Id == voiceId);

    internal static int RunWorker(string[] args)
    {
        try { return RunCore(args); }
        catch (EndOfStreamException) { return 3; }
        catch (InvalidDataException) { return 2; }
        catch (Exception) { return 5; }
    }

    private static int RunCore(string[] args)
    {
        NativeSession? native = null;
        try
        {
            if (args.Length is < 1 or > 2 || args[0] != "--open-jtalk-pcm-worker" ||
                args.Length == 2 && FindModel(args[1]) is null) return 2;
            var voiceId = args.Length == 2 ? args[1] : DefaultVoiceId;
            if (!ConfigureNativeHost()) return 4;
            using var input = Console.OpenStandardInput(); using var output = Console.OpenStandardOutput();
            return RunRequests(input, output, () => { native ??= new NativeSession(voiceId); },
                text => native!.Synthesize(text), text => native!.Synthesize(text, true));
        }
        finally { native?.Dispose(); } // RunWorker catches cleanup failures too.
    }

    // Fixed PCM-worker dispatch shared with offline protocol regression. Init
    // has no text/PCM operation; production closes its one native owner finally.
    internal static int RunRequests(Stream input, Stream output, Action initialize, Func<byte[], PcmAudio> synthesize,
        Func<byte[], PcmAudio>? synthesizeDrawnOut = null)
    {
        while (OpenJtalkPcmProtocol.ReadCommand(input) is { } request)
        {
            try
            {
                initialize();
                if (request.Preparation) OpenJtalkPcmProtocol.WritePreparationResponse(output, 0);
                else
                {
                    var audio = request.DrawnOutMode
                        ? (synthesizeDrawnOut ?? throw new InvalidDataException())(request.Text) : synthesize(request.Text);
                    OpenJtalkPcmProtocol.WriteResponse(output, 0, audio.Samples);
                }
            }
            catch (Exception error)
            {
                var status = error is OpenJtalkWorkerException { Status: >= 1 and <= 7 } nativeError ? nativeError.Status : 7;
                if (request.Preparation) OpenJtalkPcmProtocol.WritePreparationResponse(output, status);
                else OpenJtalkPcmProtocol.WriteResponse(output, status, []);
                return 4;
            }
        }
        return 0;
    }

    // Shared only by the two isolated Open JTalk workers. Neither their native
    // handles nor this session are instantiated by the launching/UI process.
    internal sealed class NativeSession : IDisposable
    {
        private readonly List<FileStream> pins = [];
        private short[]? samples;
        private nint library;
        private SimpleCall? clear;
        private SynthesizeCall? synthesize;
        private bool poisoned, loadAttempted;
        internal NativeSession(string voiceId = DefaultVoiceId)
        {
            try
            {
                var tohoku = FindModel(voiceId);
                var modelFile = voiceId == DefaultVoiceId ? "nitech_jp_atr503_m001.htsvoice" :
                    tohoku?.FileName ?? throw new ArgumentOutOfRangeException(nameof(voiceId));
                var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "resources", "open-jtalk"));
                WorldComparison.RejectLinks(root);
                var manifest = Pin(Contained(Path.Combine(root, "open-jtalk-build.json"), root), 256 * 1024);
                using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Count(p => p.NameEquals("dllSha256")) != 1)
                    throw new InvalidDataException();
                var expected = document.RootElement.GetProperty("dllSha256").GetString();
                if (expected is null || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c))) throw new InvalidDataException();
                var libraryPath = Contained(Path.Combine(root, "tanayomi-open-jtalk.dll"), root);
                var dll = Pin(libraryPath, 16 * 1024 * 1024);
                if (!Convert.ToHexString(SHA256.HashData(dll)).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
                var modelPath = Contained(Path.Combine(root, modelFile), root);
                var modelPin = Pin(modelPath, MaximumModelBytes);
                if (tohoku is not null && !MatchesModel(modelPin, tohoku)) throw new InvalidDataException();
                var dictionaryPath = Contained(Path.Combine(root, "dictionary"), root);
                foreach (var name in new[] { "char.bin", "matrix.bin", "sys.dic", "unk.dic" })
                    Pin(Contained(Path.Combine(dictionaryPath, name), root), 128L * 1024 * 1024);
                var dictionary = OpenJtalkPcmProtocol.Utf8.GetBytes(dictionaryPath);
                var model = OpenJtalkPcmProtocol.Utf8.GetBytes(modelPath);
                if (dictionary.Length > 32768 || model.Length > 32768) throw new InvalidDataException();
                library = NativeLibrary.Load(libraryPath);
                clear = Export<SimpleCall>("toj_clear");
                var version = Export<SimpleCall>("toj_abi_version");
                var load = Export<LoadCall>("toj_load");
                synthesize = Export<SynthesizeCall>("toj_synthesize");
                if (version() != 1) throw new InvalidDataException();
                loadAttempted = true;
                Check(load(dictionary, dictionary.Length, model, model.Length));
            }
            catch { Dispose(); throw; }
        }
        internal PcmAudio Synthesize(byte[] text, bool drawnOutMode = false)
        {
            samples ??= new short[OpenJtalkPcmProtocol.MaximumSamples];
            var render = drawnOutMode ? Export<SynthesizeCall>("toj_synthesize_drawn_out") : synthesize!;
            Check(render(text, text.Length, samples, samples.Length, out var count, out var rate));
            if (rate != OpenJtalkPcmProtocol.SampleRate || count is < 240 or > OpenJtalkPcmProtocol.MaximumSamples || count % 240 != 0)
                throw new InvalidDataException();
            var result = new PcmAudio(samples.AsSpan(0, count).ToArray(), rate);
            OpenJtalkPcmProtocol.ValidateAudio(result);
            return result;
        }
        internal OpenJtalkPlan Plan(byte[] text)
        {
            var plan = Export<PlanCall>("toj_plan"); // Additive v1 export, required only here.
            var slots = new byte[OpenJtalkPlanProtocol.MaximumPhones * OpenJtalkPlanProtocol.SlotBytes];
            var starts = new int[OpenJtalkPlanProtocol.MaximumPhones];
            var ends = new int[OpenJtalkPlanProtocol.MaximumPhones];
            var lf0 = new double[OpenJtalkPlanProtocol.MaximumFrames];
            var voiced = new byte[OpenJtalkPlanProtocol.MaximumFrames];
            Check(plan(text, text.Length, slots, starts.Length, starts, ends, lf0, voiced, lf0.Length,
                out var phones, out var frames, out var rate, out var period));
            return OpenJtalkPlanProtocol.FromNative(slots, starts, ends, lf0, voiced, phones, frames, rate, period);
        }
        private void Check(int status)
        {
            if (status == 0) return;
            if (status == 5 || status is < 1 or > 6) poisoned = true;
            throw new OpenJtalkWorkerException(status is >= 1 and <= 6 ? status : 7);
        }
        private T Export<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));
        private FileStream Pin(string path, long maximum)
        {
            WorldComparison.RejectLinks(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            pins.Add(stream);
            if (stream.Length < 1 || stream.Length > maximum) throw new InvalidDataException();
            return stream;
        }
        public void Dispose()
        {
            try
            {
                if (library == 0 || poisoned) return;
                if (loadAttempted && clear is not null && clear() != 0)
                { poisoned = true; throw new OpenJtalkWorkerException(7); }
                NativeLibrary.Free(library); library = 0;
            }
            finally { foreach (var pin in pins) pin.Dispose(); pins.Clear(); }
            // Poisoned native state/module is retained until this process exits.
        }
    }
    internal static bool HasModel(string root, string voiceId)
    {
        if (FindModel(voiceId) is not { } model) return false;
        try
        {
            var path = Contained(Path.Combine(root, model.FileName), root);
            WorldComparison.RejectLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return MatchesModel(stream, model);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException) { return false; }
    }

    private static bool MatchesModel(FileStream stream, HtsModel model) =>
        stream.Length is > 0 and <= MaximumModelBytes &&
        Convert.ToHexString(SHA256.HashData(stream)) == model.Sha256;

    internal static bool ConfigureNativeHost()
    {
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64) return false;
        SetErrorMode(0x0001 | 0x0002 | 0x8000);
        return SetCrtErrorMode(1) != -1;
    }
    private static string Contained(string path, string root)
    {
        var full = Path.GetFullPath(path); var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetRelativePath(boundary, full).Contains(':')) throw new InvalidDataException();
        WorldComparison.RejectLinks(full); return full;
    }
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint SetErrorMode(uint mode);
    [DllImport("msvcrt.dll", EntryPoint = "_set_error_mode", CallingConvention = CallingConvention.Cdecl)] private static extern int SetCrtErrorMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SimpleCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadCall(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] dictionary, int dictionaryBytes,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] model, int modelBytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SynthesizeCall(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] text, int textBytes,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] short[] samples, int capacity,
        out int count, out int sampleRate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PlanCall(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] text, int textBytes,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 8192 * 32)] byte[] slots, int phoneCapacity,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] starts,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] ends,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] double[] lf0,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] byte[] voiced, int frameCapacity,
        out int phones, out int frames, out int rate, out int period);
}

internal sealed class OpenJtalkWorkerException(int status) : IOException($"NIT音声ワーカーが失敗しました（状態{status}）。")
{ internal int Status { get; } = status; }

/// <summary>LE v1. Request OJPC/u32 version/i32 UTF8 byte count/body.
/// Response OJPR/u32 version/i32 status/i32 rate/i32 count/PCM16LE.
/// Preparation OJPP/u32 version/i32 zero has no body; OJPA/u32 version/i32
/// status acknowledges initialization without synthesizing or returning PCM.
/// Status 0 succeeds at 48k mono; error 1..7 has zero metadata/body and the
/// worker exits. Framing/UTF8 errors terminate without a response.</summary>
internal static class OpenJtalkPcmProtocol
{
    internal const int SampleRate = 48000, MaximumSamples = SampleRate * 45;
    internal const int RequestHeaderBytes = 12, ResponseHeaderBytes = 20;
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static byte[] EncodeRequest(string text, bool drawnOutMode = false)
        => EncodeTextRequest(text, drawnOutMode ? "OJPD"u8 : "OJPC"u8);
    internal static byte[] EncodeTextRequest(string text, ReadOnlySpan<byte> magic)
    {
        if (magic.Length != 4) throw Invalid();
        var byteCount = ValidateRequestText(text);
        var request = new byte[RequestHeaderBytes + byteCount];
        magic.CopyTo(request); BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(request.AsSpan(8), byteCount);
        Utf8.GetBytes(text.AsSpan(), request.AsSpan(RequestHeaderBytes));
        return request;
    }
    internal static int ValidateRequestText(string text)
    {
        if (text is null || text.Length is < 1 or > 2048 || text.Contains('\0')) throw Invalid();
        var remaining = text.AsSpan();
        var count = 0; var bytes = 0;
        while (!remaining.IsEmpty)
        {
            if (Rune.DecodeFromUtf16(remaining, out var rune, out var consumed) != System.Buffers.OperationStatus.Done ||
                ++count > 1024) throw Invalid();
            bytes += rune.Utf8SequenceLength;
            remaining = remaining[consumed..];
        }
        return bytes; // At most 1024 scalars * 4 UTF-8 bytes.
    }
    internal sealed record Command(bool Preparation, byte[] Text, bool DrawnOutMode = false);
    internal static byte[] EncodePreparation()
    {
        var request = new byte[RequestHeaderBytes]; "OJPP"u8.CopyTo(request);
        BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(4), 1); return request;
    }
    internal static Command? ReadCommand(Stream input)
    {
        Span<byte> header = stackalloc byte[RequestHeaderBytes];
        var first = input.ReadByte(); if (first < 0) return null;
        header[0] = (byte)first; input.ReadExactly(header[1..]);
        if (BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1) throw Invalid();
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (header[..4].SequenceEqual("OJPP"u8))
        {
            if (count != 0) throw Invalid();
            return new(true, []);
        }
        var drawnOutMode = header[..4].SequenceEqual("OJPD"u8);
        if (!(drawnOutMode || header[..4].SequenceEqual("OJPC"u8)) || count is < 1 or > 4096) throw Invalid();
        var body = new byte[count]; input.ReadExactly(body); ValidateText(body); return new(false, body, drawnOutMode);
    }
    internal static void WritePreparationResponse(Stream output, int status)
    {
        if (status is < 0 or > 7) throw Invalid();
        Span<byte> header = stackalloc byte[RequestHeaderBytes]; header.Clear(); "OJPA"u8.CopyTo(header);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], status); output.Write(header); output.Flush();
    }
    internal static async Task ReadPreparationResponseAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[RequestHeaderBytes]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (!header.AsSpan(0, 4).SequenceEqual("OJPA"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 || status is < 0 or > 7) throw Invalid();
        if (status != 0) throw new OpenJtalkWorkerException(status);
    }
    internal static byte[]? ReadRequest(Stream input)
        => ReadTextRequest(input, "OJPC"u8);
    internal static byte[]? ReadTextRequest(Stream input, ReadOnlySpan<byte> magic)
    {
        if (magic.Length != 4) throw Invalid();
        Span<byte> header = stackalloc byte[RequestHeaderBytes];
        var first = input.ReadByte(); if (first < 0) return null;
        header[0] = (byte)first; input.ReadExactly(header[1..]);
        var count = BinaryPrimitives.ReadInt32LittleEndian(header[8..]);
        if (!header[..4].SequenceEqual(magic) || BinaryPrimitives.ReadUInt32LittleEndian(header[4..]) != 1 || count is < 1 or > 4096)
            throw Invalid();
        var body = new byte[count]; input.ReadExactly(body); ValidateText(body); return body;
    }
    private static void ValidateText(byte[] bytes)
    {
        if (bytes.Length is < 1 or > 4096 || bytes.Contains((byte)0)) throw Invalid();
        try { if (Utf8.GetString(bytes).EnumerateRunes().Count() > 1024) throw Invalid(); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }
    internal static async Task<PcmAudio> ReadResponseAsync(Stream input, CancellationToken ct)
    {
        var header = new byte[ResponseHeaderBytes]; await input.ReadExactlyAsync(header, ct).ConfigureAwait(false);
        var status = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        var rate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)); var count = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(16));
        if (!header.AsSpan(0, 4).SequenceEqual("OJPR"u8) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) != 1 ||
            status is < 0 or > 7) throw Invalid();
        if (status != 0)
        {
            if (count != 0 || rate != 0) throw Invalid();
            throw new OpenJtalkWorkerException(status);
        }
        if (rate != SampleRate || count is < 240 or > MaximumSamples || count % 240 != 0) throw Invalid();
        var bytes = new byte[checked(count * 2)]; await input.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
        var samples = new short[count];
        if (BitConverter.IsLittleEndian)
            MemoryMarshal.Cast<byte, short>(bytes.AsSpan()).CopyTo(samples);
        else
            for (var i = 0; i < count; ++i) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2, 2));
        var result = new PcmAudio(samples, rate); ValidateAudio(result); return result;
    }
    internal static void ValidateAudio(PcmAudio audio)
    {
        if (audio is null || audio.SampleRate != SampleRate || audio.Samples is null ||
            audio.Samples.Length is < 240 or > MaximumSamples || audio.Samples.Length % 240 != 0) throw Invalid();
        ReadOnlySpan<short> samples = audio.Samples;
        var first = samples.IndexOfAnyExcept((short)0);
        if (first < 0 || samples[first..].IndexOfAnyExceptInRange((short)-30147, (short)30147) >= 0) throw Invalid();
    }
    internal static void WriteResponse(Stream output, int status, ReadOnlySpan<short> samples)
    {
        if (status is < 0 or > 7 || (status == 0 ? samples.Length is < 240 or > MaximumSamples || samples.Length % 240 != 0 : !samples.IsEmpty))
            throw Invalid();
        Span<byte> header = stackalloc byte[ResponseHeaderBytes];
        "OJPR"u8.CopyTo(header); BinaryPrimitives.WriteUInt32LittleEndian(header[4..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], status);
        BinaryPrimitives.WriteInt32LittleEndian(header[12..], status == 0 ? SampleRate : 0);
        BinaryPrimitives.WriteInt32LittleEndian(header[16..], samples.Length); output.Write(header);
        Span<byte> buffer = stackalloc byte[4096];
        for (var offset = 0; offset < samples.Length;)
        {
            var count = Math.Min(buffer.Length / 2, samples.Length - offset);
            if (BitConverter.IsLittleEndian)
                output.Write(MemoryMarshal.AsBytes(samples.Slice(offset, count)));
            else
            {
                for (var i = 0; i < count; ++i) BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i * 2, 2), samples[offset + i]);
                output.Write(buffer[..(count * 2)]);
            }
            offset += count;
        }
        output.Flush();
    }
    private static InvalidDataException Invalid() => new("NIT音声ワーカーの形式または上限が不正です。");
}
