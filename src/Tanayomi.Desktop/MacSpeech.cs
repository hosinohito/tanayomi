using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

/// <summary>Worker-only AVSpeechSynthesizer bridge. All native calls and run-loop
/// pumping stay on the worker's main thread; no audio file or OS mixer change.</summary>
internal static class MacSpeech
{
    private const string ObjC = "/usr/lib/libobjc.A.dylib";
    private const string Core = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private static readonly Lazy<nint> Framework = new(() => NativeLibrary.Load("/System/Library/Frameworks/AVFoundation.framework/AVFoundation"));
    private static readonly Lazy<nint> DefaultMode = new(() => Marshal.ReadIntPtr(NativeLibrary.GetExport(NativeLibrary.Load(Core), "kCFRunLoopDefaultMode")));
    private static readonly Lazy<nint> DelegateClass = new(CreateDelegateClass);
    private static readonly ConcurrentDictionary<nint, UtteranceState> States = new();
    private static readonly FinishedCallback Finished = (self, _, sender, utterance) => Complete(self, sender, utterance, 1);
    private static readonly FinishedCallback Cancelled = (self, _, sender, utterance) => Complete(self, sender, utterance, 2);

    // Called synchronously from DesktopSpeech.RunWorkerCore before any await.
    internal static int RunWorker()
    {
        try
        {
            if (!OperatingSystem.IsMacOS() || pthread_main_np() != 1) throw new PlatformNotSupportedException("macOS speech requires the worker main thread.");
            using var input = new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false, true));
            Task<string?> ReadNext() => Task.Run(() => DesktopSpeech.ReadRequestLineAsync(input));
            var next = ReadNext();
            while (next.GetAwaiter().GetResult() is { } line)
            {
                var request = DesktopSpeech.ParseRequest(line);
                next = ReadNext();
                // Only one command is read ahead. EOF or an unexpected command
                // stops our synthesizer and terminates this worker, never others.
                if (!Speak(request.Text, request.Volume, () => next.IsCompleted)) return 0;
                if (next.IsCompleted) return 0;
                Console.WriteLine("ok");
                Console.Out.Flush();
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.WriteLine($"error:{error.GetType().Name}:{error.HResult:X8}");
            Console.Out.Flush();
            return 1;
        }
    }

    private static bool Speak(string text, int volume, Func<bool> stopRequested)
    {
        if (volume is < 0 or > 100 || text.Length > 2048 || text.Contains('\0')) throw new ArgumentException("Invalid speech input.");
        if (stopRequested()) return false;
        if (volume == 0) return true;
        _ = Framework.Value;
        var pool = New("NSAutoreleasePool");
        nint synth = 0, utterance = 0, observer = 0, nativeText = 0;
        var started = false;
        try
        {
            synth = New("AVSpeechSynthesizer");
            observer = SendPointer(SendPointer(DelegateClass.Value, Selector("alloc")), Selector("init"));
            if (observer == 0) throw new IOException("Speech delegate unavailable.");
            nativeText = CFStringCreateWithCharacters(0, text, text.Length);
            if (nativeText == 0) throw new IOException("Speech text unavailable.");
            var allocation = SendPointer(RequiredClass("AVSpeechUtterance"), Selector("alloc"));
            if (allocation == 0) throw new IOException("Speech utterance allocation failed.");
            utterance = SendPointerArgument(allocation, Selector("initWithString:"), nativeText);
            if (utterance == 0) throw new IOException("Speech utterance initialization failed.");
            SendFloat(utterance, Selector("setVolume:"), volume / 100f);
            var language = CFStringCreateWithCharacters(0, "ja-JP", 5);
            if (language == 0) throw new IOException("Speech language unavailable.");
            try
            {
                var voice = SendPointerArgument(RequiredClass("AVSpeechSynthesisVoice"), Selector("voiceWithLanguage:"), language);
                if (voice == 0) throw new IOException("Japanese speech voice unavailable.");
                SendVoidPointer(utterance, Selector("setVoice:"), voice);
            }
            finally { CFRelease(language); }
            var state = new UtteranceState(synth, utterance);
            if (!States.TryAdd(observer, state)) throw new IOException("Speech observer identity collision.");
            SendVoidPointer(synth, Selector("setDelegate:"), observer);
            if (stopRequested()) return false;
            started = true;
            SendVoidPointer(synth, Selector("speakUtterance:"), utterance);
            var deadline = Stopwatch.StartNew();
            while (true)
            {
                if (stopRequested()) return false;
                var outcome = Volatile.Read(ref state.Outcome);
                if (outcome == 1) return true;
                if (outcome != 0) throw new IOException("macOS speech was interrupted.");
                if (deadline.Elapsed > TimeSpan.FromSeconds(55)) throw new TimeoutException("macOS speech did not complete.");
                Pump();
            }
        }
        finally
        {
            // A successful worker exit acknowledges an actual native stop.
            // The parent must use EOF and wait, treating a forced kill as
            // unconfirmed, because the OS may synthesize outside this process.
            try
            {
                if (synth != 0 && started)
                {
                    var stopped = SendBoolInteger(synth, Selector("stopSpeakingAtBoundary:"), 0);
                    var wait = Stopwatch.StartNew();
                    while (SendBool(synth, Selector("isSpeaking")) && wait.Elapsed < TimeSpan.FromSeconds(2)) Pump();
                    if (SendBool(synth, Selector("isSpeaking")) || (!stopped && States.TryGetValue(observer, out var state) && Volatile.Read(ref state.Outcome) == 0))
                        throw new IOException("macOS speech stop could not be confirmed.");
                }
            }
            finally
            {
                try { if (synth != 0) SendVoidPointer(synth, Selector("setDelegate:"), 0); }
                finally
                {
                    if (observer != 0) States.TryRemove(observer, out _);
                    try { Release(synth); }
                    finally
                    {
                        try { Release(utterance); }
                        finally
                        {
                            try { Release(observer); }
                            finally
                            {
                                try { if (nativeText != 0) CFRelease(nativeText); }
                                finally { if (pool != 0) SendVoid(pool, Selector("drain")); }
                            }
                        }
                    }
                }
            }
        }
    }

    private sealed class UtteranceState(nint synth, nint utterance)
    {
        internal readonly nint Synth = synth, Utterance = utterance;
        internal int Outcome;
    }
    private static void Complete(nint self, nint sender, nint utterance, int outcome)
    {
        // No managed exception may cross the Objective-C callback boundary.
        try
        {
            if (States.TryGetValue(self, out var state) && state.Synth == sender && state.Utterance == utterance)
                Interlocked.CompareExchange(ref state.Outcome, outcome, 0);
        }
        catch (Exception) { }
    }
    private static nint CreateDelegateClass()
    {
        var type = objc_allocateClassPair(RequiredClass("NSObject"), "TanayomiSpeechObserver_" + Guid.NewGuid().ToString("N"), 0);
        if (type == 0) throw new IOException("Speech observer class allocation failed.");
        var protocol = objc_getProtocol("AVSpeechSynthesizerDelegate");
        if (protocol == 0 || !class_addProtocol(type, protocol) ||
            !class_addMethod(type, Selector("speechSynthesizer:didFinishSpeechUtterance:"), Marshal.GetFunctionPointerForDelegate(Finished), "v@:@@") ||
            !class_addMethod(type, Selector("speechSynthesizer:didCancelSpeechUtterance:"), Marshal.GetFunctionPointerForDelegate(Cancelled), "v@:@@"))
        { objc_disposeClassPair(type); throw new IOException("Speech observer methods unavailable."); }
        objc_registerClassPair(type);
        return type; // Class metadata and rooted callbacks live until worker exit.
    }
    private static nint Selector(string name) => sel_registerName(name);
    private static nint RequiredClass(string name) => objc_getClass(name) is var type && type != 0 ? type : throw new PlatformNotSupportedException("Required macOS speech API unavailable.");
    private static nint New(string name)
    {
        var value = SendPointer(SendPointer(RequiredClass(name), Selector("alloc")), Selector("init"));
        return value != 0 ? value : throw new IOException("macOS speech object unavailable.");
    }
    private static void Release(nint value) { if (value != 0) SendVoid(value, Selector("release")); }
    private static void Pump()
    {
        var mode = DefaultMode.Value;
        if (mode == 0) throw new IOException("Run loop mode unavailable.");
        _ = CFRunLoopRunInMode(mode, 0.01, false);
        Thread.Sleep(1); // A mode with no current sources may return immediately.
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate void FinishedCallback(nint self, nint command, nint sender, nint utterance);
    [DllImport(ObjC)] private static extern nint objc_getClass(string name);
    [DllImport(ObjC)] private static extern nint objc_getProtocol(string name);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addProtocol(nint type, nint protocol);
    [DllImport(ObjC)] private static extern nint sel_registerName(string name);
    [DllImport(ObjC)] private static extern nint objc_allocateClassPair(nint superclass, string name, nuint extraBytes);
    [DllImport(ObjC)] private static extern void objc_registerClassPair(nint type);
    [DllImport(ObjC)] private static extern void objc_disposeClassPair(nint type);
    [DllImport(ObjC)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool class_addMethod(nint type, nint selector, nint implementation, string types);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPointer(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern nint SendPointerArgument(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoidPointer(nint receiver, nint selector, nint value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendVoid(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern void SendFloat(nint receiver, nint selector, float value);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBool(nint receiver, nint selector);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] [return: MarshalAs(UnmanagedType.I1)] private static extern bool SendBoolInteger(nint receiver, nint selector, nint value);
    [DllImport(Core)] private static extern nint CFStringCreateWithCharacters(nint allocator, [MarshalAs(UnmanagedType.LPWStr)] string value, nint count);
    [DllImport(Core)] private static extern void CFRelease(nint value);
    [DllImport(Core)] private static extern int CFRunLoopRunInMode(nint mode, double seconds, [MarshalAs(UnmanagedType.I1)] bool returnAfterSourceHandled);
    [DllImport("/usr/lib/libSystem.B.dylib")] private static extern int pthread_main_np();
}
