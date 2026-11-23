using Avalonia;

namespace Tanayomi.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AppPaths.ConfigureProcessStorage();
        if (args.Contains("--speech-worker")) return RunWorker(DesktopSpeech.RunWorker);
        if (args.Contains("--pcm-worker")) return RunWorker(DesktopPcmOutput.RunWorker);
        if (args.Contains("--world-compare-worker")) return RunWorker(() => WorldComparison.RunWorker(args));
        if (args.Contains("--world-prosody-worker")) return RunWorker(() => WorldComparison.RunProsodyWorker(args));
        if (args.Contains("--world-timing-worker")) return RunWorker(() => WorldComparison.RunTimingWorker(args));
        if (args.Contains("--world-join-worker")) return RunWorker(() => WorldComparison.RunJoinWorker(args));
        if (args.Contains("--world-context-worker")) return RunWorker(() => WorldComparison.RunContextWorker(args));
        if (args.Contains("--utau-context-pronunciation-worker")) return RunWorker(() => WorldComparison.RunContextPronunciationWorker(args));
        if (args.Contains("--utau-context-devoicing-worker")) return RunWorker(() => WorldComparison.RunContextPronunciationWorker(args));
        if (args.Contains("--utau-context-check-worker")) return RunWorker(() => WorldComparison.RunProductContextCheck(args));
        if (args.Contains("--world-utau-worker")) return RunWorker(() => WorldUtauComparison.RunWorker(args));
        if (args.Contains("--world-pcm-worker")) return RunWorker(() => WorldPcmWorker.RunWorker(args));
        if (args.Contains("--open-jtalk-pcm-worker")) return RunWorker(() => OpenJtalkPcmWorker.RunWorker(args));
        if (args.Contains("--open-jtalk-plan-worker")) return RunWorker(() => OpenJtalkPlanWorker.RunWorker(args));
        if (args.Contains(NeuralPcmWorker.Argument)) return RunWorker(() => NeuralPcmWorker.RunWorker(args));
        if (args.Contains(VoicevoxPcmWorker.WorkerArgument)) return RunWorker(() => VoicevoxPcmWorker.RunWorker(args));
        if (args.Contains(SupertonicPcmWorker.Argument)) return RunWorker(() => SupertonicPcmWorker.RunWorker(args));
        App.CreateSpeech = () => new DesktopSpeech();
        App.CreatePcm = () => new DesktopPcmOutput();
#if OPEN_JTALK_NATIVE
        App.CreateOpenJtalkSpeech = pcm =>
        {
            if (!OperatingSystem.IsWindows() || System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture != System.Runtime.InteropServices.Architecture.X64)
                return null;
            var voices = new List<DesktopBundledSpeechSelection.Voice>();
            var owners = new List<IAsyncDisposable>();
            var errors = new List<Exception>();
            TryAdd(() =>
            {
                var root = Path.Combine(AppContext.BaseDirectory, "resources", "open-jtalk");
                WorldComparison.RejectLinks(root);
                foreach (var relative in new[] { "tanayomi-open-jtalk.dll", "open-jtalk-build.json", "nitech_jp_atr503_m001.htsvoice",
                    "dictionary/char.bin", "dictionary/matrix.bin", "dictionary/sys.dic", "dictionary/unk.dic" })
                {
                    var path = Path.Combine(root, relative); WorldComparison.RejectLinks(path);
                    if (!File.Exists(path)) throw new FileNotFoundException("NIT音声の同梱ファイルがありません。");
                }
                var additional = OpenJtalkPcmWorker.AdditionalModels.Where(model => OpenJtalkPcmWorker.HasModel(root, model.Id))
                    .Select(model => new DesktopOpenJtalkSpeech(pcm, model.Id)).ToArray();
                Add(new DesktopOpenJtalkVoiceSelection(new DesktopOpenJtalkSpeech(pcm), additional));
            });
            TryAdd(() => { if (VoicevoxPcmWorker.HasPackage) Add(new VoicevoxSpeech(pcm)); });
            TryAdd(() => { if (SupertonicPcmWorker.HasAssets()) Add(new SupertonicSpeech(pcm)); });
            TryAdd(() =>
            {
                if (AivisSpeechWorker.TryCreate(AppContext.BaseDirectory, AppPaths.DataDirectory) is not { } host) return;
                owners.Add(host);
                foreach (var choice in host.Voices) voices.Add(new(choice, host.CreateVoice(choice.Id, pcm)));
            });
            if (voices.Count == 0) throw new AggregateException("同梱音声を初期化できません。", errors);
            return new DesktopBundledSpeechSelection(voices, owners, errors);

            void Add(ISpeechOutput output)
            {
                owners.Add(output);
                foreach (var choice in ((IOpenJtalkVoiceSelection)output).Voices) voices.Add(new(choice, output));
            }
            void TryAdd(Action create)
            {
                try { create(); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException)
                { errors.Add(error); }
            }
        };
#endif
        App.CreateUtauPostprocessor = () =>
        {
#if OPEN_JTALK_NATIVE
            var nativePlatform = OperatingSystem.IsWindows() &&
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64;
#endif
            var errors = new List<Exception>();
            var world = false;
            var prosody = false;
            var neural = false;
#if WORLD_NATIVE
            try { world = Available([WorldNativePlatform.LibraryPath(AppContext.BaseDirectory)]); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { errors.Add(error); }
#endif
#if OPEN_JTALK_NATIVE
            var root = Path.Combine(AppContext.BaseDirectory, "resources", "open-jtalk");
            prosody = nativePlatform && Available(new[] { "tanayomi-open-jtalk.dll", "open-jtalk-build.json",
                "nitech_jp_atr503_m001.htsvoice", "dictionary/char.bin", "dictionary/matrix.bin",
                "dictionary/sys.dic", "dictionary/unk.dic" }.Select(x => Path.Combine(root, x)));
            var neuralRoot = Path.Combine(AppContext.BaseDirectory, "resources", "openvoice");
            if (prosody && NeuralOpenVoiceAssets.Configured && Directory.Exists(neuralRoot))
                neural = Available(new[] { "reference.onnx", "converter.onnx", "onnxruntime.dll", "openvoice-build.json",
                    "OpenVoice-LICENSE.txt", "OpenVoice-NOTICE.txt" }.Select(x => Path.Combine(neuralRoot, x)));
#endif
            return new DesktopUtauModes(world, prosody, initializationErrors: errors,
                neuralAvailable: neural);

#if WORLD_NATIVE || OPEN_JTALK_NATIVE
            bool Available(IEnumerable<string> paths)
            {
                try
                {
                    foreach (var path in paths)
                    {
                        WorldComparison.RejectLinks(path);
                        if (!File.Exists(path)) throw new FileNotFoundException("音声方式に必要な同梱資産がありません。", path);
                    }
                    return true;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { errors.Add(error); return false; }
            }
#endif
        };
        App.CreateCredentialStore = () => new DesktopCredentialStore();
        return AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace()
            .StartWithClassicDesktopLifetime(args);
    }

    private static int RunWorker(Func<int> operation)
    {
        try { return operation(); }
        // A closed control pipe or failed native cleanup is reported by the parent
        // as worker failure. Do not let it become an OS exception dialog.
        catch (Exception) { return 1; }
    }
}

