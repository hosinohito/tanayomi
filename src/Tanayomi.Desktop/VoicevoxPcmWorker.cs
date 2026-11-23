using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Tanayomi.Desktop;

internal static class VoicevoxPcmWorker
{
    internal const string WorkerArgument = "--voicevox-pcm-worker";
    private const string ManifestName = "voicevox-assets.json";
    private static string AssetRoot => Path.Combine(AppContext.BaseDirectory, "resources", "voicevox");
    internal static bool IsSupported => OperatingSystem.IsWindows() &&
        RuntimeInformation.ProcessArchitecture == Architecture.X64;
    internal static bool HasPackage => IsSupported && HasPackageAt(AssetRoot);

    internal static bool HasPackageAt(string packageRoot)
    {
        RejectPackageLinks(packageRoot);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(packageRoot); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        if ((attributes & FileAttributes.Directory) == 0)
            throw new IOException("VOICEVOX音声パッケージの配置が不正です。");

        var manifest = Path.Combine(packageRoot, ManifestName);
        RejectPackageLinks(manifest);
        try { attributes = File.GetAttributes(manifest); }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException)
        {
            throw new FileNotFoundException("VOICEVOX音声パッケージのマニフェストがありません。");
        }
        if ((attributes & FileAttributes.Directory) != 0)
            throw new IOException("VOICEVOX音声パッケージのマニフェストが不正です。");
        return true;
    }

    private static void RejectPackageLinks(string path)
    {
        try { WorldComparison.RejectLinks(path); }
        catch (InvalidDataException) { throw new IOException("VOICEVOX音声パッケージのパスにリンクが含まれています。"); }
    }

    internal static int RunWorker(string[] args)
    {
        try
        {
            if (args.Length != 1 || args[0] != WorkerArgument) return 2;
            if (!OpenJtalkPcmWorker.ConfigureNativeHost()) return 4;
            var temporary = Path.Combine(AppPaths.DataDirectory, "tmp");
            WorldComparison.RejectLinks(temporary);
            Directory.CreateDirectory(temporary);
            foreach (var name in new[] { "TEMP", "TMP", "TMPDIR" })
                Environment.SetEnvironmentVariable(name, temporary);
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            NativeSession? session = null;
            try
            {
                return RunRequests(input, output, (voice, text) =>
                {
                    session ??= new NativeSession();
                    session.Select(voice);
                    return text is null ? null : session.Synthesize(voice, text);
                });
            }
            finally { session?.Dispose(); }
        }
        catch (EndOfStreamException) { return 3; }
        catch (InvalidDataException) { return 2; }
        catch (Exception) { return 5; }
    }

    internal static int RunRequests(Stream input, Stream output, Func<VoicevoxVoice, string?, PcmAudio?> process)
    {
        while (VoicevoxProtocol.ReadRequest(input) is { } command)
        {
            PcmAudio? audio;
            try
            {
                audio = process(command.Voice, command.Text);
                if ((audio is null) != (command.Text is null)) throw new InvalidDataException();
                if (audio is not null) VoicevoxProtocol.ValidateAudio(audio);
            }
            catch (Exception error)
            {
                VoicevoxProtocol.WriteResponse(output, error is VoicevoxWorkerException native ? native.Status : 241, null);
                return 4;
            }
            VoicevoxProtocol.WriteResponse(output, 0, audio);
        }
        return 0;
    }

    private sealed class NativeSession : IDisposable
    {
        private const string CoreDll = "voicevox_core.dll", RuntimeDll = "voicevox_onnxruntime.dll";
        private static readonly Dictionary<string, string> ExpectedDlls = new(StringComparer.Ordinal)
        {
            [CoreDll] = "EC9E730AE9E1AE897AECF78BF380B9D9C34120F8B4BE2D3844F52C9EFDE5BC91",
            [RuntimeDll] = "53BC7F528EC804FE4E80A57ED03BCECB580B1ED6F8EAE4B08613487E9135453E",
            ["msvcp140.dll"] = "0F885B509A685D2BBFA652FED26B5FB31D88FBDAB0A978C641D1C7B8AA460AA9",
            ["msvcp140_1.dll"] = "BFAD5AEF4C63A669E3C140655CDFDF395B6C979B400A447BD5DCB65ED8826C3D",
            ["vcruntime140.dll"] = "D5E4D9A3E835FA679450145D6A7D94E36573A509317111904D9B3712C30D9066",
            ["vcruntime140_1.dll"] = "1F2D41C4AA5DB0BC33EBF7B66D72943A817D7CE6CBE880502A9403823633093F"
        };
        private readonly List<FileStream> pins = [];
        // The catalog bounds this cache to its fixed VVM files. A cached CORE file
        // handle reuses the verified file without resolving its path on each load.
        private readonly Dictionary<string, VerifiedModelFile> modelFiles = new(StringComparer.Ordinal);
        private sealed record VerifiedModelFile(FileStream Pin, nint Handle, byte[] Id);
        private nint library, dllDirectory, jtalk, synthesizer;
        private string? modelFile;
        private byte[]? modelId;
        private readonly OpenResource openModel;
        private readonly ModelIdentifier getModelId;
        private readonly DeleteHandle deleteModel, deleteSynthesizer, deleteOpenJtalk, freeBytes;
        private readonly LoadModel loadModel;
        private readonly UnloadModel unloadModel;
        private readonly TtsCall tts;
        private readonly TtsOptions ttsOptions;

        internal NativeSession()
        {
            try
            {
                var root = AssetRoot;
                using var manifest = Pin(Path.Combine(root, ManifestName), 16 * 1024);
                using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 4 });
                var fields = document.RootElement;
                if (fields.GetProperty("coreVersion").GetString() != "0.17.1" ||
                    fields.GetProperty("runtimeVersion").GetString() != "1.23.2" ||
                    fields.GetProperty("crtVersion").GetString() != "14.44.35211" ||
                    fields.GetProperty("modelVersion").GetString() != "0.16.4") throw new InvalidDataException();
                var checkedFiles = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in fields.GetProperty("dlls").EnumerateObject())
                {
                    if (!ExpectedDlls.TryGetValue(file.Name, out var expected) ||
                        !checkedFiles.Add(file.Name) || file.Value.GetString() != expected) throw new InvalidDataException();
                    pins.Add(VerifyFile(Path.Combine(root, file.Name), expected, 32 * 1024 * 1024));
                }
                if (checkedFiles.Count != ExpectedDlls.Count) throw new InvalidDataException();
                // Remove the current directory and PATH from dependent DLL resolution.
                if (!SetDefaultDllDirectories(0x0400 | 0x0800) ||
                    (dllDirectory = AddDllDirectory(root)) == 0) throw new IOException();
                library = NativeLibrary.Load(Path.Combine(root, CoreDll));
                if (Marshal.PtrToStringUTF8(Export<GetVersion>("voicevox_get_version")()) != "0.17.1")
                    throw new InvalidDataException();
                deleteSynthesizer = Export<DeleteHandle>("voicevox_synthesizer_delete");
                deleteOpenJtalk = Export<DeleteHandle>("voicevox_open_jtalk_rc_delete");
                openModel = Export<OpenResource>("voicevox_voice_model_file_open");
                getModelId = Export<ModelIdentifier>("voicevox_voice_model_file_id");
                deleteModel = Export<DeleteHandle>("voicevox_voice_model_file_delete");
                loadModel = Export<LoadModel>("voicevox_synthesizer_load_voice_model");
                unloadModel = Export<UnloadModel>("voicevox_synthesizer_unload_voice_model");
                tts = Export<TtsCall>("voicevox_synthesizer_tts");
                ttsOptions = Export<DefaultTtsOptions>("voicevox_make_default_tts_options")();
                freeBytes = Export<DeleteHandle>("voicevox_bytes_free");

                var runtimePath = Marshal.StringToCoTaskMemUTF8(Path.Combine(root, RuntimeDll));
                nint runtime;
                try { Check(Export<LoadRuntime>("voicevox_onnxruntime_load_once")(new(runtimePath), out runtime)); }
                finally { Marshal.FreeCoTaskMem(runtimePath); }
                if (runtime == 0) throw new InvalidDataException();

                var dictionary = Path.Combine(AppContext.BaseDirectory, "resources", "open-jtalk", "dictionary");
                foreach (var file in new[] { "char.bin", "matrix.bin", "sys.dic", "unk.dic" })
                    pins.Add(Pin(Path.Combine(dictionary, file), 128 * 1024 * 1024));
                Check(Export<OpenResource>("voicevox_open_jtalk_rc_new")(dictionary, out jtalk));
                if (jtalk == 0) throw new InvalidDataException();
                const int cpuMode = 1;
                var threads = (ushort)Math.Clamp(Environment.ProcessorCount, 1, 4);
                Check(Export<NewSynthesizer>("voicevox_synthesizer_new")(runtime, jtalk,
                    new(cpuMode, threads), out synthesizer));
                if (synthesizer == 0) throw new InvalidDataException();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void Select(VoicevoxVoice voice)
        {
            if (VoicevoxCatalog.Find(voice.StyleId) != voice) throw new InvalidDataException();
            if (modelFile == voice.ModelFile) return;
            if (!modelFiles.TryGetValue(voice.ModelFile, out var model))
            {
                var path = Path.Combine(AssetRoot, "models", voice.ModelFile);
                var pin = VerifyFile(path, VoicevoxCatalog.ModelSha256(voice.ModelFile), 128 * 1024 * 1024);
                nint handle = 0;
                try
                {
                    Check(openModel(path, out handle));
                    if (handle == 0) throw new InvalidDataException();
                    var identifier = new byte[16];
                    getModelId(handle, identifier);
                    model = new VerifiedModelFile(pin, handle, identifier);
                    modelFiles.Add(voice.ModelFile, model);
                }
                catch
                {
                    try { if (handle != 0) deleteModel(handle); }
                    finally { pin.Dispose(); }
                    throw;
                }
            }
            if (modelId is not null)
            {
                Check(unloadModel(synthesizer, modelId));
                modelFile = null;
                modelId = null;
            }

            // Only the selected model is loaded in the synthesizer. The other
            // cached handles own file metadata and a descriptor, not inference sessions.
            Check(loadModel(synthesizer, model.Handle, new(0)));
            modelId = model.Id;
            modelFile = voice.ModelFile;
        }

        internal PcmAudio Synthesize(VoicevoxVoice voice, string text)
        {
            if (VoicevoxCatalog.Find(voice.StyleId) != voice || modelFile != voice.ModelFile) throw new InvalidDataException();
            Check(tts(synthesizer, text, voice.StyleId, ttsOptions, out var size, out var wave));
            if (wave == 0) throw new InvalidDataException();
            try
            {
                return VoicevoxProtocol.DecodeNativeWave(wave, size);
            }
            finally { freeBytes(wave); }
        }

        private T Export<T>(string name) where T : Delegate =>
            Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

        private static void Check(int status)
        {
            if (status != 0) throw new VoicevoxWorkerException(status is > 0 and < 240 ? status : 240);
        }

        private static FileStream Pin(string path, long maximum)
        {
            WorldComparison.RejectLinks(path);
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length > 0 && file.Length <= maximum) return file;
            file.Dispose();
            throw new InvalidDataException();
        }

        private static FileStream VerifyFile(string path, string? expected, long maximum)
        {
            var file = Pin(path, maximum);
            try
            {
                if (expected is null || expected.Length != 64 ||
                    !string.Equals(expected, Convert.ToHexString(SHA256.HashData(file)), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException();
                return file;
            }
            catch { file.Dispose(); throw; }
        }

        public void Dispose()
        {
            try
            {
                if (synthesizer != 0) deleteSynthesizer(synthesizer);
                if (jtalk != 0) deleteOpenJtalk(jtalk);
            }
            finally
            {
                try
                {
                    foreach (var model in modelFiles.Values)
                    {
                        try { deleteModel(model.Handle); }
                        finally { model.Pin.Dispose(); }
                    }
                }
                finally
                {
                    // CORE's process-wide runtime remains loaded until the isolated host exits.
                    if (dllDirectory != 0) RemoveDllDirectory(dllDirectory);
                    foreach (var pin in pins) pin.Dispose();
                }
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct RuntimeOptions(nint Filename);
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct InitializeOptions(int AccelerationMode, ushort CpuThreads);
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct LoadModelOptions(int OnExisting);
        [StructLayout(LayoutKind.Sequential)]
        private readonly record struct TtsOptions(byte InterrogativeUpspeak);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate nint GetVersion();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate TtsOptions DefaultTtsOptions();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void DeleteHandle(nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadRuntime(RuntimeOptions options, out nint runtime);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int OpenResource([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out nint handle);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int NewSynthesizer(nint runtime, nint dictionary, InitializeOptions options, out nint synthesizer);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void ModelIdentifier(nint model, [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] byte[] modelId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadModel(nint synthesizer, nint model, LoadModelOptions options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int UnloadModel(nint synthesizer, [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] byte[] modelId);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int TtsCall(nint synthesizer,
            [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint styleId, TtsOptions options, out nuint size, out nint wave);

        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetDefaultDllDirectories(uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern nint AddDllDirectory(string path);
        [DllImport("kernel32.dll", ExactSpelling = true)] [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool RemoveDllDirectory(nint cookie);
    }
}
