using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

// SetErrorMode is process-wide. Serialize only the synchronous start so native
// launches inherit the required flags without racing each other's restoration.
// The caller owns all lifetime, cancellation, pipe and termination handling.
internal static class NativeChildProcess
{
    private static readonly object StartGate = new();
    internal const uint RequiredErrorMode = 0x8003;

    internal static Process Start(ProcessStartInfo start, Func<ProcessStartInfo, Process>? launch = null)
    {
        ArgumentNullException.ThrowIfNull(start);
        if (!OperatingSystem.IsWindows()) return Launch();
        lock (StartGate)
        {
            var previous = GetErrorMode();
            SetErrorMode(previous | RequiredErrorMode);
            try { return Launch(); }
            finally { SetErrorMode(previous); }
        }
        Process Launch() => (launch is null ? Process.Start(start) : launch(start))
            ?? throw new IOException("NATIVE_CHILD_START_FAILED");
    }

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint GetErrorMode();
    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint SetErrorMode(uint mode);
}
