using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tanayomi.Desktop;

internal static class SupertonicPcmWorker
{
    internal const string Argument = "--supertonic-pcm-worker";
    internal const string TermsSha256 = "0D944A9110FED9A9602D60E0423A272903E7BD21AB060490774EFC77C2275E9F";
    private const string TermsPath = "licenses/Supertonic-3-OpenRAIL-M.txt";
    private const int TermsBytes = 15007;

    internal static int RunWorker(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] != Argument || !IsSupported) return 2;
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Environment.CurrentDirectory));
            var parent = Path.GetFullPath(Path.Combine(AppPaths.DataDirectory, "tmp"));
            var name = Path.GetFileName(temporary);
            if (!string.Equals(Path.GetDirectoryName(temporary), parent, StringComparison.OrdinalIgnoreCase) ||
                name.Length != 35 || !name.StartsWith("st-", StringComparison.Ordinal) ||
                !Guid.TryParseExact(name[3..], "N", out _)) return 2;
            WorldComparison.RejectLinks(temporary);
            foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" }) Environment.SetEnvironmentVariable(key, temporary);
            if (!OpenJtalkPcmWorker.ConfigureNativeHost()) return 4;
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            NativeSession? session = null;
            try
            {
                return RunRequests(input, output, () => session ??= new NativeSession(),
                    (speaker, text) => session!.Synthesize(speaker, text));
            }
            finally { session?.Dispose(); }
        }
        catch (EndOfStreamException) { return 3; }
        catch (Exception error) when (error is InvalidDataException or System.Text.DecoderFallbackException) { return 2; }
        catch (Exception) { return 5; }
    }

    internal static bool IsSupported =>
        OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    private static string ResourceRoot => Path.Combine(AppContext.BaseDirectory, "resources", "supertonic");

    internal static string ReadTerms(string? executableOverride)
    {
        var root = executableOverride is null ? ResourceRoot :
            Path.Combine(Path.GetDirectoryName(Path.GetFullPath(executableOverride))!, "resources", "supertonic");
        var path = Path.Combine(root, TermsPath);
        WorldComparison.RejectLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != TermsBytes) throw new InvalidDataException("SUPERTONIC_TERMS_LENGTH");
        var bytes = new byte[TermsBytes];
        file.ReadExactly(bytes);
        if (Convert.ToHexString(SHA256.HashData(bytes)) != TermsSha256)
            throw new InvalidDataException("SUPERTONIC_TERMS_HASH");
        return new System.Text.UTF8Encoding(false, true).GetString(bytes);
    }

    internal static bool HasAssets()
    {
        if (!IsSupported) return false;
        FileAttributes attributes;
        try { attributes = File.GetAttributes(ResourceRoot); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        WorldComparison.RejectLinks(ResourceRoot);
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("SUPERTONIC_PACKAGE: 音声パッケージがフォルダーではありません。");
        foreach (var (name, _, _) in Assets)
        {
            var path = Path.Combine(ResourceRoot, name);
            WorldComparison.RejectLinks(path);
            if (!File.Exists(path))
                throw new FileNotFoundException($"SUPERTONIC_PACKAGE: 音声パッケージのファイルがありません ({name})。");
        }
        return true;
    }

    internal static int RunRequests(Stream input, Stream output, Action initialize, Func<int, string, short[]> synthesize)
    {
        while (SupertonicProtocol.ReadRequest(input) is { } request)
        {
            try { initialize(); }
            catch (Exception)
            {
                SupertonicProtocol.WriteResponse(output, 1, []);
                return 4;
            }
            short[] pcm;
            try
            {
                pcm = request.Text is null ? [] : synthesize(request.Speaker, request.Text);
                if (request.Text is not null && (pcm.Length is < 1 or > SupertonicProtocol.MaximumSamples))
                    throw new InvalidDataException();
            }
            catch (Exception)
            {
                SupertonicProtocol.WriteResponse(output, 2, []);
                return 4;
            }
            SupertonicProtocol.WriteResponse(output, 0, pcm);
        }
        return 0;
    }

    internal static short[] CopyNativePcm(nint source, int length, int sampleRate)
    {
        if (source == 0 || length is < 1 or > SupertonicProtocol.MaximumSamples ||
            sampleRate != SupertonicProtocol.SampleRate)
            throw new InvalidDataException("SUPERTONIC_PCM_BOUNDS");

        const int chunkSamples = 32 * 1024 / sizeof(float);
        var samples = new float[Math.Min(length, chunkSamples)];
        var pcm = new short[length];
        for (var offset = 0; offset < length; offset += samples.Length)
        {
            var count = Math.Min(samples.Length, length - offset);
            Marshal.Copy(source + offset * sizeof(float), samples, 0, count);
            for (var i = 0; i < count; i++)
            {
                var sample = samples[i];
                if (!float.IsFinite(sample)) throw new InvalidDataException("SUPERTONIC_PCM_VALUE");
                pcm[offset + i] = (short)Math.Clamp(MathF.Round(Math.Clamp(sample, -1, 1) * 32768f), short.MinValue, short.MaxValue);
            }
        }
        return pcm;
    }

    private static readonly (string Name, int Length, string Sha256)[] Assets =
    [
        ("duration_predictor.int8.onnx", 3700147, "C3EB91414D5FF8A7A239B7FE9E34E7E2BF8A8140D8375FFB14718B1C639325DB"),
        ("text_encoder.int8.onnx", 36416150, "C7BEFD5EA8C3119769E8A6C1486C4EDC6A3BC8365C67621C881BBB774B9902FF"),
        ("vector_estimator.int8.onnx", 78400833, "20CD86FA5C6EFFEDFDA0E7CFFE5B0569CA401C440A0C3A1D72BF39286C0DB3FD"),
        ("vocoder.int8.onnx", 25991073, "E923D60F53F95EB1CE235F1DC33EC56D9C057823C96FA6F8ACF98F32B0DA6152"),
        ("tts.json", 8253, "42078D3AEF1CD43AB43021F3C54F47D2D75CEB4E75F627F118890128B06A0D09"),
        ("unicode_indexer.bin", 262144, "8402CA48E5189A8950138580B0FFF64DB6F072F24AC07CD54BA8B2FBB9883B30"),
        ("voice.bin", 517168, "67D5209B0EE8CE6C74105FFBE12FE6A7628AEA3B4BA2FCB308A4A67938A93CE8"),
        ("onnxruntime.dll", 17136128, "422D776AB0E3218260F7F628FCB84606AAA5C21116F720C8619A6DA5E0B2E0F9"),
        ("onnxruntime_providers_shared.dll", 10752, "0190137DEE4933261C065C5D030C3568A68C7AA43CAD942B4726A07011738BC2"),
        ("sherpa-onnx-c-api.dll", 3925504, "CF49E9AB5BAC5E2D177FE14AE070CC4F52C791490333252150A745F9D03C1111"),
        ("vcruntime140.dll", 124544, "D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066"),
        ("vcruntime140_1.dll", 49792, "1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F"),
        ("msvcp140.dll", 557728, "0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9"),
        ("msvcp140_1.dll", 35952, "BFAD5AEF4C63A669E3C140655CDFDF395B6C979B400A447BD5DCB65ED8826C3D"),
        (TermsPath, TermsBytes, TermsSha256),
        ("licenses/Microsoft-VC-Runtime-LICENSE.txt", 6696, "38C113F791FDD03FA9316F0F743201CB54A1C8C9B683B615A906EA2FEA7C8B6E")
    ];

    private sealed class NativeSession : IDisposable
    {
        private readonly List<FileStream> pins = [];
        private readonly List<nint> modules = [];
        private nint library, tts;
        private Release? destroy, destroyAudio;
        private Generate? generate;
        private readonly nint languageJson;

        internal NativeSession()
        {
            languageJson = Marshal.StringToCoTaskMemUTF8("{\"lang\":\"ja\"}");
            var strings = new List<nint>(8);
            try
            {
                var root = ResourceRoot;
                WorldComparison.RejectLinks(root);
                foreach (var (name, length, hash) in Assets)
                {
                    var path = Path.Combine(root, name);
                    WorldComparison.RejectLinks(path);
                    var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    pins.Add(stream);
                    if (stream.Length != length || Convert.ToHexString(SHA256.HashData(stream)) != hash)
                        throw new InvalidDataException("SUPERTONIC_ASSET_HASH");
                }
                foreach (var name in new[] { "vcruntime140.dll", "vcruntime140_1.dll", "msvcp140.dll", "msvcp140_1.dll", "onnxruntime.dll" })
                    Load(name);
                library = Load("sherpa-onnx-c-api.dll");
                if (Marshal.PtrToStringUTF8(Export<Version>("SherpaOnnxGetVersionStr")()) != "2.0.1" ||
                    Marshal.PtrToStringUTF8(Export<Version>("SherpaOnnxGetOnnxruntimeVersionStr")()) != "1.28.2")
                    throw new InvalidDataException("SUPERTONIC_NATIVE_VERSION");
                destroy = Export<Release>("SherpaOnnxDestroyOfflineTts");
                destroyAudio = Export<Release>("SherpaOnnxDestroyOfflineTtsGeneratedAudio");
                generate = Export<Generate>("SherpaOnnxOfflineTtsGenerateWithConfig");
                var config = new Config
                {
                    Threads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                    Provider = Text("cpu"),
                    DurationPredictor = Asset(0),
                    TextEncoder = Asset(1),
                    VectorEstimator = Asset(2),
                    Vocoder = Asset(3),
                    TtsJson = Asset(4),
                    UnicodeIndexer = Asset(5),
                    VoiceStyle = Asset(6),
                    MaxSentences = 1,
                    SilenceScale = 0.2f
                };
                tts = Export<Create>("SherpaOnnxCreateOfflineTts")(ref config);
                if (tts == 0 ||
                    Export<ReadInt>("SherpaOnnxOfflineTtsSampleRate")(tts) != SupertonicProtocol.SampleRate ||
                    Export<ReadInt>("SherpaOnnxOfflineTtsNumSpeakers")(tts) != SupertonicProtocol.SpeakerCount)
                    throw new InvalidDataException("SUPERTONIC_NATIVE_MODEL");
                nint Asset(int index) => Text(Path.Combine(root, Assets[index].Name));
                nint Load(string name)
                {
                    const DllImportSearchPath search = DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32;
                    var handle = NativeLibrary.Load(Path.Combine(root, name), typeof(SupertonicPcmWorker).Assembly, search);
                    modules.Add(handle);
                    return handle;
                }
                nint Text(string text)
                {
                    var value = Marshal.StringToCoTaskMemUTF8(text);
                    strings.Add(value);
                    return value;
                }
            }
            catch { Dispose(); throw; }
            finally { foreach (var value in strings) Marshal.FreeCoTaskMem(value); }
        }

        internal short[] Synthesize(int speaker, string text)
        {
            var config = new GenerationConfig { SilenceScale = 0.2f, Speed = 1, Speaker = speaker, Steps = 8, Extra = languageJson };
            var result = generate!(tts, text, ref config, 0, 0);
            if (result == 0) throw new InvalidDataException("SUPERTONIC_PCM_NULL");
            try
            {
                var audio = Marshal.PtrToStructure<Audio>(result);
                return CopyNativePcm(audio.Samples, audio.Length, audio.SampleRate);
            }
            finally { destroyAudio!(result); }
        }

        public void Dispose()
        {
            try { if (tts != 0) destroy!(tts); }
            finally
            {
                for (var index = modules.Count - 1; index >= 0; index--) NativeLibrary.Free(modules[index]);
                Marshal.FreeCoTaskMem(languageJson);
                foreach (var pin in pins) pin.Dispose();
            }
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        // v2.0.1 (af2d8d81) c-api.h, Windows x64. Unused model and reference-audio fields
        // stay zero; named offsets document which parts of the ABI we populate.
        [StructLayout(LayoutKind.Explicit, Size = 456)]
        private struct Config
        {
            [FieldOffset(56)] internal int Threads;
            [FieldOffset(64)] internal nint Provider;
            [FieldOffset(368)] internal nint DurationPredictor;
            [FieldOffset(376)] internal nint TextEncoder;
            [FieldOffset(384)] internal nint VectorEstimator;
            [FieldOffset(392)] internal nint Vocoder;
            [FieldOffset(400)] internal nint TtsJson;
            [FieldOffset(408)] internal nint UnicodeIndexer;
            [FieldOffset(416)] internal nint VoiceStyle;
            [FieldOffset(432)] internal int MaxSentences;
            [FieldOffset(448)] internal float SilenceScale;
        }

        [StructLayout(LayoutKind.Explicit, Size = 104)]
        private struct GenerationConfig
        {
            [FieldOffset(0)] internal float SilenceScale;
            [FieldOffset(4)] internal float Speed;
            [FieldOffset(8)] internal int Speaker;
            [FieldOffset(40)] internal int Steps;
            [FieldOffset(48)] internal nint Extra;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct Audio
        {
            internal nint Samples;
            internal int Length;
            internal int SampleRate;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Version();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint Create(ref Config config);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int ReadInt(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void Release(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate nint Generate(nint handle, [MarshalAs(UnmanagedType.LPUTF8Str)] string text,
            ref GenerationConfig config, nint callback, nint arg);
    }
}
