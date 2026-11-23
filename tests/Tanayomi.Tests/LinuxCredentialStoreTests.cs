using System.Diagnostics;
using Tanayomi.Desktop;

internal static class LinuxCredentialStoreTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        const string secret = "{\"accessToken\":\"test-only-secret\",\"refreshToken\":\"test-only-refresh\"}";
        var commands = new List<(ProcessStartInfo Start, string? Input)>();
        var response = new LinuxCredentialStore.CommandResult(0, "", false);
        var store = new LinuxCredentialStore("installation-test", (start, input, ct) =>
        {
            ct.ThrowIfCancellationRequested();
            commands.Add((start, input));
            return Task.FromResult(response);
        });
        await store.WriteAsync(secret, CancellationToken.None);
        var write = commands.Single();
        check(write.Input == secret && write.Start.ArgumentList.All(a => !a.Contains("test-only")) &&
            write.Start.Environment.Values.All(v => v is null || !v.Contains("test-only-secret")), "credential passes through stdin only, without token arguments/environment");
        check(write.Start.FileName == "/usr/bin/secret-tool" && !write.Start.UseShellExecute && write.Start.CreateNoWindow &&
            write.Start.RedirectStandardInput && write.Start.RedirectStandardOutput && write.Start.RedirectStandardError,
            "credential command avoids shell and uses private pipes");
        check(write.Start.Environment["SECRET_BACKEND"] == "service", "credential command cannot inherit a file-backend override");
        response = new(0, secret, false);
        check(await store.ReadAsync(CancellationToken.None) == secret, "credential lookup retains exact payload");
        check(commands[^1].Start.ArgumentList[0] == "lookup" && commands[^1].Start.ArgumentList.Contains("installation-test"),
            "credential lookup is scoped to installation");
        response = new(1, "", false);
        check(await store.ReadAsync(CancellationToken.None) is null, "absent secret-tool entry is not a failed login");
        await store.DeleteAsync(CancellationToken.None);
        check(commands[^2].Start.ArgumentList[0] == "clear" && commands[^1].Start.ArgumentList[0] == "lookup" && commands[^1].Input is null,
            "credential deletion verifies absence instead of assuming locked item was deleted");
        response = new(1, secret, true);
        try { await store.ReadAsync(CancellationToken.None); check(false, "failed lookup rejected"); }
        catch (IOException ex) { check(!ex.ToString().Contains("test-only"), "failed lookup excludes native output and secrets from exception"); }
        response = new(0, "", false);
        await RejectAsync(() => store.ReadAsync(CancellationToken.None), check, "empty successful lookup is rejected");
        var before = commands.Count;
        await RejectAsync(() => store.WriteAsync(new string('あ', 3000), CancellationToken.None), check, "secret-tool UTF8 limit checked before write");
        check(before == commands.Count, "oversized credential never starts external command");
        response = new(0, "", true);
        await RejectAsync(() => store.WriteAsync(secret, CancellationToken.None), check, "store warnings are not treated as reliable persistence");

        var unlockResponses = new Queue<LinuxCredentialStore.CommandResult>([new(1, "", false), new(0, secret, false), new(0, "", false)]);
        var unlockCommands = new List<string>();
        var locked = new LinuxCredentialStore("locked-installation", (start, _, _) =>
        {
            unlockCommands.Add(start.ArgumentList[0]);
            return Task.FromResult(unlockResponses.Dequeue());
        });
        await locked.DeleteAsync(CancellationToken.None);
        check(unlockCommands.SequenceEqual(["clear", "lookup", "clear"]), "locked credential is retried after lookup unlocks it");
        var unavailable = new LinuxCredentialStore("locked-installation", (start, _, _) =>
            Task.FromResult(new LinuxCredentialStore.CommandResult(1, "", start.ArgumentList[0] == "lookup")));
        await RejectAsync(() => unavailable.DeleteAsync(CancellationToken.None), check, "locked unavailable credential is not reported deleted");

        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var serial = new LinuxCredentialStore("other-installation", async (_, _, ct) =>
        {
            Interlocked.Increment(ref calls); entered.TrySetResult();
            await release.Task.WaitAsync(ct);
            return new(0, "", false);
        });
        var saving = serial.WriteAsync(secret, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            using var cancelled = new CancellationTokenSource();
            var deletion = serial.DeleteAsync(cancelled.Token);
            cancelled.Cancel();
            try { await deletion; check(false, "queued delete cancelled"); }
            catch (OperationCanceledException) { check(calls == 1, "cancelled queued delete cannot overtake credential save"); }
        }
        finally { release.TrySetResult(); await saving; }
    }

    private static async Task RejectAsync(Func<Task> action, Action<bool, string> check, string name)
    {
        try { await action(); check(false, name); }
        catch (IOException) { check(true, name); }
    }
}
