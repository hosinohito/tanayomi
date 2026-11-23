using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Tanayomi.Desktop;

internal enum OpenJtalkContextFailure { Platform, Input, Audit, Library, Version, Load, Plan, Result, Cleanup, Terminal }

/// <summary>Fixed diagnostics only: never contains input, native text or paths.</summary>
internal sealed class OpenJtalkContextException(OpenJtalkContextFailure failure, int nativeStatus = -1)
    : IOException($"Open JTalk comparison failed ({failure}; status {nativeStatus}).")
{
    internal OpenJtalkContextFailure Failure { get; } = failure;
    internal int NativeStatus { get; } = nativeStatus;
}

/// <summary>Comparison-worker-only loader. Its caller supplies the existing
/// hidden-process timeout/reap boundary. Never call from the UI process.
/// The worker's working directory must be the repository root.</summary>
internal static class OpenJtalkContext
{
    private const int MaximumPhones = 8192, MaximumFrames = 9000, SlotBytes = 32;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private static readonly object Gate = new();
    private static bool terminal;

    internal static OpenJtalkPlan Read(string auditDirectory, string fixedText)
    {
        lock (Gate)
        {
            if (terminal) throw new OpenJtalkContextException(OpenJtalkContextFailure.Terminal);
            var stage = OpenJtalkContextFailure.Platform;
            try
            {
                if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
                    throw new OpenJtalkContextException(stage);
                stage = OpenJtalkContextFailure.Input;
                if (fixedText is null || fixedText.Length is < 1 or > 2048 || fixedText.Contains('\0'))
                    throw new OpenJtalkContextException(stage);
                var text = Utf8.GetBytes(fixedText);
                if (text.Length is < 1 or > 4096 || fixedText.EnumerateRunes().Count() > 1024)
                    throw new OpenJtalkContextException(stage);
                stage = OpenJtalkContextFailure.Audit;
                using var audit = AuditFiles.Open(auditDirectory);
                // Must precede NativeLibrary.Load, including static CRT setup.
                SetErrorMode(0x0001 | 0x0002);
                if (SetCrtErrorMode(1) == -1) throw new OpenJtalkContextException(OpenJtalkContextFailure.Library);
                stage = OpenJtalkContextFailure.Library;
                return ReadNative(audit, text);
            }
            catch (OpenJtalkContextException) { throw; }
            catch (Exception) { throw new OpenJtalkContextException(stage); }
        }
    }

    private static OpenJtalkPlan ReadNative(AuditFiles audit, byte[] text)
    {
        nint library = 0;
        SimpleCall? clear = null;
        var loadAttempted = false;
        var stage = OpenJtalkContextFailure.Library;
        try
        {
            library = NativeLibrary.Load(audit.Library);
            // Resolve all required entries before allowing a model to load.
            clear = Export<SimpleCall>(library, "toj_clear");
            var version = Export<SimpleCall>(library, "toj_abi_version");
            var load = Export<LoadCall>(library, "toj_load");
            var plan = Export<PlanCall>(library, "toj_plan");
            stage = OpenJtalkContextFailure.Version;
            if (version() != 1) throw new OpenJtalkContextException(stage);
            stage = OpenJtalkContextFailure.Load;
            var dictionary = Utf8.GetBytes(audit.Dictionary); var model = Utf8.GetBytes(audit.Model);
            if (dictionary.Length > 32768 || model.Length > 32768) throw new OpenJtalkContextException(stage);
            loadAttempted = true;
            Check(load(dictionary, dictionary.Length, model, model.Length), stage);
            var slots = new byte[MaximumPhones * SlotBytes];
            var starts = new int[MaximumPhones]; var ends = new int[MaximumPhones];
            var lf0 = new double[MaximumFrames]; var voiced = new byte[MaximumFrames];
            stage = OpenJtalkContextFailure.Plan;
            Check(plan(text, text.Length, slots, MaximumPhones, starts, ends, lf0, voiced, MaximumFrames,
                out var phones, out var frames, out var rate, out var period), stage);
            stage = OpenJtalkContextFailure.Result;
            if (phones is < 1 or > MaximumPhones || frames is < 1 or > MaximumFrames || rate != 48000 || period != 240)
                throw new OpenJtalkContextException(stage);
            var result = new OpenJtalkPhone[phones];
            var previous = 0;
            for (var i = 0; i < phones; ++i)
            {
                var slot = slots.AsSpan(i * SlotBytes, SlotBytes);
                var length = slot.IndexOf((byte)0);
                if (length is < 1 or > 31 || starts[i] != previous || ends[i] <= starts[i] || ends[i] > frames)
                    throw new OpenJtalkContextException(stage);
                for (var j = 0; j < SlotBytes; ++j)
                    if (j < length ? !PhoneCharacter(slot[j]) : slot[j] != 0)
                        throw new OpenJtalkContextException(stage);
                result[i] = new(Encoding.ASCII.GetString(slot[..length]), starts[i], ends[i]);
                previous = ends[i];
            }
            if (previous != frames) throw new OpenJtalkContextException(stage);
            for (var i = 0; i < frames; ++i)
                if (voiced[i] > 1 || !double.IsFinite(lf0[i]) ||
                    (voiced[i] == 0 ? lf0[i] != 0 : lf0[i] < Math.Log(40) || lf0[i] > Math.Log(1200)))
                    throw new OpenJtalkContextException(stage);
            // The DTO snapshots these used ranges into read-only collections.
            return new OpenJtalkPlan(result, lf0.AsSpan(0, frames).ToArray(), voiced.AsSpan(0, frames).ToArray(), rate, period);
        }
        catch (OpenJtalkContextException) { throw; }
        catch (Exception) { throw new OpenJtalkContextException(stage); }
        finally
        {
            if (library != 0 && !terminal)
            {
                // Status 5 leaves partially initialized native state/module to
                // OS reclamation. Any unconfirmed cleanup also forbids reuse.
                try
                {
                    if (loadAttempted && clear is not null)
                    {
                        var status = clear();
                        if (status != 0)
                        {
                            terminal = true;
                            throw new OpenJtalkContextException(OpenJtalkContextFailure.Cleanup, SafeStatus(status));
                        }
                    }
                    NativeLibrary.Free(library);
                }
                catch (OpenJtalkContextException) { throw; }
                catch (Exception)
                {
                    terminal = true;
                    throw new OpenJtalkContextException(OpenJtalkContextFailure.Cleanup);
                }
            }
        }
    }

    private static bool PhoneCharacter(byte c) => c is >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'
        or >= (byte)'0' and <= (byte)'9' or (byte)'_';
    private static int SafeStatus(int status) => status is >= 0 and <= 6 ? status : -1;
    private static void Check(int status, OpenJtalkContextFailure stage)
    {
        if (status == 0) return;
        if (status == 5 || SafeStatus(status) == -1) terminal = true;
        throw new OpenJtalkContextException(stage, SafeStatus(status));
    }
    private static T Export<T>(nint library, string name) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(NativeLibrary.GetExport(library, name));

    private sealed class AuditFiles : IDisposable
    {
        private readonly List<FileStream> pins = [];
        internal required string Library { get; init; }
        internal required string Dictionary { get; init; }
        internal required string Model { get; init; }
        internal static AuditFiles Open(string path)
        {
            var root = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local", "build", "open-jtalk"));
            var audit = Contained(path, root);
            if (!Directory.Exists(audit)) throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
            var files = new AuditFiles
            {
                Library = Contained(Path.Combine(audit, "tanayomi-open-jtalk.dll"), audit),
                Dictionary = Contained(Path.Combine(audit, "dictionary"), audit),
                Model = Contained(Path.Combine(audit, "nitech_jp_atr503_m001.htsvoice"), audit)
            };
            try
            {
                var manifest = files.Pin(Contained(Path.Combine(audit, "open-jtalk-build.json"), audit), 256 * 1024);
                using var document = JsonDocument.Parse(manifest, new JsonDocumentOptions { MaxDepth = 16 });
                if (document.RootElement.ValueKind != JsonValueKind.Object ||
                    document.RootElement.EnumerateObject().Count(p => p.NameEquals("dllSha256")) != 1)
                    throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
                var expected = document.RootElement.GetProperty("dllSha256").GetString();
                if (expected is null || expected.Length != 64 || expected.Any(c => !Uri.IsHexDigit(c)))
                    throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
                var library = files.Pin(files.Library, 16 * 1024 * 1024);
                if (!Convert.ToHexString(SHA256.HashData(library)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                    throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
                files.Pin(files.Model, 2 * 1024 * 1024);
                foreach (var name in new[] { "char.bin", "matrix.bin", "sys.dic", "unk.dic" })
                    files.Pin(Contained(Path.Combine(files.Dictionary, name), audit), 128L * 1024 * 1024);
                return files;
            }
            catch { files.Dispose(); throw; }
        }
        private FileStream Pin(string path, long maximum)
        {
            WorldComparison.RejectLinks(path);
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            pins.Add(stream);
            if (stream.Length < 1 || stream.Length > maximum) throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
            return stream;
        }
        public void Dispose() { foreach (var stream in pins) stream.Dispose(); }
    }

    private static string Contained(string path, string root)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
        var full = Path.GetFullPath(path); var boundary = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        if (!full.StartsWith(boundary + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            Path.GetRelativePath(boundary, full).Contains(':')) throw new OpenJtalkContextException(OpenJtalkContextFailure.Audit);
        WorldComparison.RejectLinks(full);
        return full;
    }

    [DllImport("kernel32.dll", ExactSpelling = true)] private static extern uint SetErrorMode(uint mode);
    [DllImport("msvcrt.dll", EntryPoint = "_set_error_mode", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetCrtErrorMode(int mode);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int SimpleCall();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int LoadCall(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] dictionary, int dictionaryBytes,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] byte[] model, int modelBytes);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] private delegate int PlanCall(
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] text, int textBytes,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeConst = MaximumPhones * SlotBytes)] byte[] slots, int phoneCapacity,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] starts,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 3)] int[] ends,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] double[] lf0,
        [In, Out, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 8)] byte[] voiced, int frameCapacity,
        out int phoneCount, out int frameCount, out int sampleRate, out int framePeriod);
}
