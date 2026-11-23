using System.Diagnostics;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Uses the system's secret-tool over private pipes; never bundles or links libsecret.</summary>
internal sealed class LinuxCredentialStore : ICredentialStore
{
    // secret-tool's stdin reader is bounded to 8192 bytes. Reject rather than truncate.
    private const int MaximumBytes = 8191;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly string installation;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Func<ProcessStartInfo, string?, CancellationToken, Task<CommandResult>> run;

    internal LinuxCredentialStore(string installation,
        Func<ProcessStartInfo, string?, CancellationToken, Task<CommandResult>>? run = null)
    {
        this.installation = installation;
        this.run = run ?? RunAsync;
    }

    public async Task<string?> ReadAsync(CancellationToken ct)
    {
        var result = await ExecuteAsync("lookup", null, ct).ConfigureAwait(false);
        if (IsMissing(result)) return null;
        EnsureSuccess(result);
        if (result.Output.Length == 0 || Utf8.GetByteCount(result.Output) > MaximumBytes) throw Failed();
        return result.Output;
    }

    public async Task WriteAsync(string value, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Length == 0 || Utf8.GetByteCount(value) > MaximumBytes)
            throw new IOException("Linuxの保管庫へ保存する認証情報のサイズが上限を超えています。");
        EnsureSuccess(await ExecuteAsync("store", value, ct).ConfigureAwait(false));
    }

    public async Task DeleteAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = await ExecuteCoreAsync("clear", null, ct).ConfigureAwait(false);
            if (!IsMissing(result)) { EnsureSuccess(result); return; }
            // clear ignores locked items. Its empty exit 1 alone cannot prove absence.
            // lookup may unlock a matching item; verify absence or retry clear once.
            var lookup = await ExecuteCoreAsync("lookup", null, ct).ConfigureAwait(false);
            if (IsMissing(lookup)) return;
            EnsureSuccess(lookup);
            if (lookup.Output.Length == 0 || Utf8.GetByteCount(lookup.Output) > MaximumBytes) throw Failed();
            EnsureSuccess(await ExecuteCoreAsync("clear", null, ct).ConfigureAwait(false));
        }
        finally { gate.Release(); }
    }

    private async Task<CommandResult> ExecuteAsync(string operation, string? value, CancellationToken ct)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try { return await ExecuteCoreAsync(operation, value, ct).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private Task<CommandResult> ExecuteCoreAsync(string operation, string? value, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // libsecret can force its file backend in these sandboxes, ahead of
        // SECRET_BACKEND. Do not silently change the authorized storage location.
        if (File.Exists("/.flatpak-info") || Environment.GetEnvironmentVariable("SNAP_NAME") is not null)
            throw new IOException("Flatpak/Snap環境の認証保管庫には対応していません。通常の配布版でログインしてください。ファイルへの退避は行いません。");
        var start = new ProcessStartInfo("/usr/bin/secret-tool")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = Utf8, StandardOutputEncoding = Utf8, StandardErrorEncoding = Utf8
        };
        // An inherited SECRET_BACKEND=file must not redirect credentials to files.
        start.Environment["SECRET_BACKEND"] = "service";
        start.ArgumentList.Add(operation);
        if (operation == "store")
        {
            start.ArgumentList.Add("--label=たなよみ Twitch認証");
            start.ArgumentList.Add("--collection=default");
        }
        foreach (var attribute in new[] { "application", "app.tanayomi", "credential", "twitch-v1", "installation", installation })
            start.ArgumentList.Add(attribute);
        // Tokens are only the pipe payload, never arguments or environment values.
        return run(start, value, ct);
    }

    private static bool IsMissing(CommandResult result) => result.ExitCode == 1 && !result.HasError && result.Output.Length == 0;
    private static void EnsureSuccess(CommandResult result)
    { if (result.ExitCode != 0 || result.HasError) throw Failed(); }
    private static IOException Failed() => new("OSの認証保管庫を利用できません。secret-toolとSecret Service、保管庫のロック状態を確認してください。平文には保存しません。");

    private static async Task<CommandResult> RunAsync(ProcessStartInfo start, string? value, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var token = timeout.Token;
        Process? process = null;
        Task<string>? output = null;
        Task<bool>? errors = null;
        try
        {
            process = Process.Start(start) ?? throw Failed();
            using var registration = token.Register(() => Kill(process));
            output = ReadBoundedAsync(process.StandardOutput, token);
            errors = DrainErrorsAsync(process.StandardError, token);
            if (value is not null) await process.StandardInput.WriteAsync(value.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            process.StandardInput.Close();
            await Task.WhenAll(output, errors, process.WaitForExitAsync(token)).ConfigureAwait(false);
            return new(process.ExitCode, await output.ConfigureAwait(false), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw Failed(); }
        finally
        {
            timeout.Cancel();
            if (process is not null)
            {
                Kill(process);
                // Observe pipe failures after cancellation; retain no native error strings.
                try { await Task.WhenAll(output ?? Task.FromResult(""), (Task?)errors ?? Task.CompletedTask).ConfigureAwait(false); }
                catch (Exception) { }
                process.Dispose();
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[MaximumBytes + 1];
        try
        {
            var length = 0;
            while (true)
            {
                var count = await reader.ReadAsync(buffer.AsMemory(length), ct).ConfigureAwait(false);
                if (count == 0) return new string(buffer, 0, length);
                length += count;
                if (length > MaximumBytes) throw Failed();
            }
        }
        finally { Array.Clear(buffer); }
    }

    private static async Task<bool> DrainErrorsAsync(StreamReader reader, CancellationToken ct)
    {
        var buffer = new char[512];
        var any = false;
        try { while (await reader.ReadAsync(buffer, ct).ConfigureAwait(false) > 0) any = true; return any; }
        finally { Array.Clear(buffer); }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }

    internal sealed class CommandResult(int exitCode, string output, bool hasError)
    {
        public int ExitCode { get; } = exitCode;
        public string Output { get; } = output;
        public bool HasError { get; } = hasError;
    }
}
