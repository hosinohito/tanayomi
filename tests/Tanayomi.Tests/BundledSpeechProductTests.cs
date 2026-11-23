using System.Buffers.Binary;
using System.Diagnostics;
using Tanayomi;
using Tanayomi.Desktop;

internal static class BundledSpeechProductTests
{
    internal static async Task RunAsync(string executable, Action<bool, string> check, string engine = "all")
    {
        if (engine is not ("all" or "aivis" or "voicevox" or "supertonic"))
            throw new ArgumentException("Unknown bundled speech test engine.", nameof(engine));
        var workspace = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, ".local")) + Path.DirectorySeparatorChar;
        executable = Path.GetFullPath(executable);
        if (!executable.StartsWith(workspace, StringComparison.OrdinalIgnoreCase) ||
            Path.GetFileName(executable) != "Tanayomi.Desktop.exe" || !File.Exists(executable))
            throw new ArgumentException("Specify a product exe under the workspace .local directory.");
        WorldComparison.RejectLinks(executable);
        var directory = Path.GetDirectoryName(executable)!;
        var selected = new[] { "supertonic", "aivis", "voicevox" }.Where(name =>
            (engine == "all" || engine == name) && Directory.Exists(Path.Combine(directory, "resources", name))).ToHashSet();
        if (selected.Count == 0) throw new FileNotFoundException("The selected bundled speech package is absent.");
        if (selected.Contains("supertonic"))
            await SupertonicWorkerTests.RunProductAsync(executable, check);
        if (selected.Contains("aivis"))
            await AivisPcmProtocolTests.RunProductAsync(executable, check);
        if (selected.Contains("voicevox"))
        {
            await VoicevoxTests.RunProductAsync(executable, check);
            await WithWorker(executable, VoicevoxPcmWorker.WorkerArgument, async (child, ct) =>
            {
                var forged = VoicevoxProtocol.Encode(VoicevoxCatalog.Find(3u)!, "x");
                BinaryPrimitives.WriteUInt32LittleEndian(forged.AsSpan(8), uint.MaxValue);
                await Send(child, forged, ct);
                return 2;
            }, check);
        }
    }

    private static async Task Send(Process child, byte[] bytes, CancellationToken ct)
    {
        await child.StandardInput.BaseStream.WriteAsync(bytes, ct);
        await child.StandardInput.BaseStream.FlushAsync(ct);
    }

    private static async Task WithWorker(string executable, string argument,
        Func<Process, CancellationToken, Task<int>> operation, Action<bool, string> check)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add(argument);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(4));
        var ct = timeout.Token;
        using var child = NativeChildProcess.Start(start) ?? throw new IOException("Product worker start failed.");
        var stderr = Drain(child.StandardError.BaseStream, ct);
        try
        {
            var expected = await operation(child, ct);
            child.StandardInput.Close();
            var trailing = new byte[1];
            check(await child.StandardOutput.BaseStream.ReadAsync(trailing, ct) == 0,
                "bundled product worker emits no trailing or rejected PCM");
            await child.WaitForExitAsync(ct);
            await stderr;
            check(child.ExitCode == expected, "bundled product worker returns the expected clean or rejected-input status");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await child.WaitForExitAsync(stop.Token);
            }
            try { await stderr; } catch (OperationCanceledException) { }
        }
    }

    private static async Task Drain(Stream input, CancellationToken ct)
    {
        var buffer = new byte[1024];
        while (await input.ReadAsync(buffer, ct) != 0) { }
    }
}
