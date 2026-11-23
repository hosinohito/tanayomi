using System.Net;
using System.Text;
using System.Text.Json;
using Tanayomi;

public static class TwitchAccountTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using (var auth = Auth([]))
        {
            var unavailable = new TwitchAccount(auth, null);
            check(await unavailable.RestoreAsync(CancellationToken.None) is null, "account without protected storage starts signed out");
            await Failure<IOException>(() => unavailable.SaveAsync(Session(), CancellationToken.None), check, "unavailable protected storage is reported without plaintext fallback");
            await unavailable.ForgetAsync(CancellationToken.None);
        }

        var saved = new MemoryStore();
        using (var auth = Auth([request =>
        {
            check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/oauth2/validate" && request.Headers.Authorization?.Parameter == "accessSecret",
                "saved account is validated live before it is restored");
            return Task.FromResult(Identity());
        }]))
        {
            var account = new TwitchAccount(auth, saved);
            check(await account.RestoreAsync(CancellationToken.None) is null, "empty credential store starts signed out");
            await account.SaveAsync(Session(), CancellationToken.None);
            var restored = await new TwitchAccount(auth, saved).RestoreAsync(CancellationToken.None);
            check(restored?.Identity.Login == "freshname" && restored.Identity.UserId == "123" && restored.AccessToken == "accessSecret" && restored.RefreshToken == "refreshSecret",
                "account restoration uses freshly validated identity and saved refresh credential");
            check(saved.Value!.Length < 16384 && !saved.Value.Contains("Login", StringComparison.OrdinalIgnoreCase) &&
                !account.ToString()!.Contains("Secret") && !restored!.ToString()!.Contains("Secret"), "saved account JSON is bounded and diagnostic text contains no credential");
            await account.ForgetAsync(CancellationToken.None);
            check(saved.Value is null, "forget account deletes saved credentials");
        }

        var rotated = await SavedStore(Session(DateTimeOffset.UtcNow.AddMinutes(-1)));
        using (var auth = Auth([async request =>
        {
            var form = await request.Content!.ReadAsStringAsync();
            check(request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/oauth2/token" && form.Contains("refresh_token=refreshSecret") && form.Contains("grant_type=refresh_token"),
                "known expired account refreshes once before validating an access token");
            return Tokens();
        }, request =>
        {
            check(request.Headers.Authorization?.Parameter == "newAccess", "account validates the newly issued access token");
            return Task.FromResult(Identity());
        }]))
        {
            var result = await new TwitchAccount(auth, rotated).RestoreAsync(CancellationToken.None);
            check(result?.RefreshToken == "newRefresh" && rotated.Value!.Contains("newRefresh") && !rotated.Value.Contains("refreshSecret"),
                "rotated one-use refresh credential is stored before restored session is returned");
        }

        foreach (var nearExpiry in new[] { DateTimeOffset.UtcNow.AddMinutes(1), DateTimeOffset.UtcNow.AddDays(-1) })
        {
            var store = await SavedStore(Session(nearExpiry));
            var calls = 0;
            using var auth = Auth([request => { calls++; return Task.FromResult(Tokens()); }, request => { calls++; return Task.FromResult(Identity()); }]);
            var result = await new TwitchAccount(auth, store).RestoreAsync(CancellationToken.None);
            check(calls == 2 && result?.AccessToken == "newAccess", "near-expiry and expired accounts take a single refresh exchange");
        }

        var revoked = await SavedStore(Session());
        var revokedCalls = 0;
        using (var auth = Auth([request => { revokedCalls++; return Task.FromResult(Json("{\"message\":\"serverSecret\"}", 401)); }]))
            await Failure<SourceConfigurationException>(() => new TwitchAccount(auth, revoked).RestoreAsync(CancellationToken.None), check, "revoked saved access token requires a fresh login without a refresh attempt");
        check(revoked.Value is null && revokedCalls == 1, "revoked account is deleted and is not retried");

        var offline = await SavedStore(Session());
        var original = offline.Value;
        using (var auth = Auth([_ => throw new HttpRequestException("networkSecret")]))
            await Failure<HttpRequestException>(() => new TwitchAccount(auth, offline).RestoreAsync(CancellationToken.None), check, "temporary account-validation outage remains retryable");
        check(offline.Value == original && offline.Deletes == 0, "validation network failure preserves saved credentials");

        foreach (var afterExchange in new[] { false, true })
        {
            var ambiguous = await SavedStore(Session(DateTimeOffset.UtcNow.AddDays(-1)));
            var calls = 0;
            var steps = new List<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
            if (afterExchange) steps.Add(_ => { calls++; return Task.FromResult(Tokens()); });
            steps.Add(_ => { calls++; throw new HttpRequestException("networkSecret"); });
            using var auth = Auth(steps);
            await Failure<SourceConfigurationException>(() => new TwitchAccount(auth, ambiguous).RestoreAsync(CancellationToken.None), check, "ambiguous one-use refresh outcome requests a fresh login");
            check(ambiguous.Value is null && calls == (afterExchange ? 2 : 1), "ambiguous refresh credential is deleted without retrying the exchange");
        }

        var cancelled = await SavedStore(Session(DateTimeOffset.UtcNow.AddDays(-1)));
        using (var cancellation = new CancellationTokenSource())
        using (var auth = Auth([_ => { cancellation.Cancel(); throw new OperationCanceledException(cancellation.Token); }]))
        {
            await Failure<OperationCanceledException>(() => new TwitchAccount(auth, cancelled).RestoreAsync(cancellation.Token), check, "cancelling a refresh propagates cancellation");
            check(cancelled.Value is null, "cancelled in-flight refresh never leaves a possibly consumed credential saved");
        }

        var validJson = (await SavedStore(Session())).Value!;
        foreach (var invalid in new[] { "not json secret", new string('x', 16385), "[]", "{\"version\":1,\"version\":1}", validJson.Replace("accessSecret", "oauth:accessSecret"), validJson.Replace("\"version\":1", "\"version\":2") })
        {
            var corrupt = new MemoryStore { Value = invalid };
            using var auth = Auth([]);
            await Failure<SourceConfigurationException>(() => new TwitchAccount(auth, corrupt).RestoreAsync(CancellationToken.None), check, "corrupt or unsupported saved credentials are rejected locally");
            check(corrupt.Value is null, "invalid saved credentials are removed before another restore");
        }

        var mismatch = await SavedStore(Session());
        using (var auth = Auth([_ => Task.FromResult(Identity("differentclient"))]))
            await Failure<SourceConfigurationException>(() => new TwitchAccount(auth, mismatch).RestoreAsync(CancellationToken.None), check, "saved account client ID must match live validation");
        check(mismatch.Value is null, "mismatched saved client is deleted");

        var writeFailure = await SavedStore(Session());
        writeFailure.FailWrite = true;
        using (var auth = Auth([]))
            await Failure<IOException>(() => new TwitchAccount(auth, writeFailure).SaveAsync(Session(), CancellationToken.None), check, "credential write failure is sanitized and reported");
        check(writeFailure.Value is null, "failed credential replacement removes the old possibly consumed refresh token");
        var rotatedWriteFailure = await SavedStore(Session(DateTimeOffset.UtcNow.AddDays(-1)));
        rotatedWriteFailure.FailWrite = true;
        using (var auth = Auth([_ => Task.FromResult(Tokens()), _ => Task.FromResult(Identity())]))
            await Failure<IOException>(() => new TwitchAccount(auth, rotatedWriteFailure).RestoreAsync(CancellationToken.None), check, "restore does not return rotated credentials whose persistence failed");
        check(rotatedWriteFailure.Value is null, "failed rotated credential save removes its obsolete predecessor");

        var locked = new MemoryStore { FailRead = true };
        using (var auth = Auth([]))
            await Failure<IOException>(() => new TwitchAccount(auth, locked).RestoreAsync(CancellationToken.None), check, "credential read failure cannot leak store paths or secrets");

        // A restore owns the store until validation finishes. Forget must wait,
        // then delete, rather than racing with an outstanding restore operation.
        var serialized = await SavedStore(Session());
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var auth = Auth([async _ => { started.SetResult(); await release.Task; return Identity(); }]))
        {
            var account = new TwitchAccount(auth, serialized);
            var restore = account.RestoreAsync(CancellationToken.None);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var forget = account.ForgetAsync(CancellationToken.None);
            check(!forget.IsCompleted, "account store operations are serialized across restore and logout");
            release.SetResult();
            await Task.WhenAll(restore, forget);
            check(serialized.Value is null, "logout remains deleted after a concurrent restore completes");
        }
    }

    private static TwitchAuthorization Session(DateTimeOffset? expiry = null) => new()
    {
        AccessToken = "accessSecret", RefreshToken = "refreshSecret",
        Identity = new("oldname", "123", "client123", ["chat:read"], expiry ?? DateTimeOffset.UtcNow.AddHours(1))
    };

    private static async Task<MemoryStore> SavedStore(TwitchAuthorization session)
    {
        var store = new MemoryStore();
        using var auth = Auth([]);
        await new TwitchAccount(auth, store).SaveAsync(session, CancellationToken.None);
        return store;
    }

    private static TwitchAuthentication Auth(IEnumerable<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps) => new(new HttpClient(new ScriptHandler(steps)));
    private static HttpResponseMessage Identity(string client = "client123") => Json(JsonSerializer.Serialize(new { login = "freshname", user_id = "123", client_id = client, scopes = new[] { "chat:read", "user:read:chat" }, expires_in = 3600 }));
    private static HttpResponseMessage Tokens() => Json("{\"access_token\":\"newAccess\",\"refresh_token\":\"newRefresh\",\"token_type\":\"bearer\"}");
    private static HttpResponseMessage Json(string text, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };

    private static async Task Failure<T>(Func<Task> operation, Action<bool, string> check, string name) where T : Exception
    {
        try { await operation(); }
        catch (T error)
        {
            check(!error.ToString().Contains("Secret", StringComparison.OrdinalIgnoreCase) && !error.ToString().Contains("not json", StringComparison.Ordinal), name);
            return;
        }
        check(false, name);
    }

    private sealed class MemoryStore : ICredentialStore
    {
        public string? Value { get; set; }
        public int Deletes { get; private set; }
        public bool FailWrite { get; set; }
        public bool FailRead { get; set; }
        public Task<string?> ReadAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailRead) throw new IOException("storeSecret"); return Task.FromResult(Value); }
        public Task WriteAsync(string value, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); if (FailWrite) throw new IOException("storeSecret"); Value = value; return Task.CompletedTask; }
        public Task DeleteAsync(CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Deletes++; Value = null; return Task.CompletedTask; }
    }

    private sealed class ScriptHandler(IEnumerable<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> remaining = new(steps);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!remaining.TryDequeue(out var next)) throw new InvalidOperationException("Unexpected authentication request.");
            return next(request);
        }
    }
}
