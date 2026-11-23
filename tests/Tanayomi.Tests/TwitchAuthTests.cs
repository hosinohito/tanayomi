using System.Net;
using System.Text;
using Tanayomi;

internal static class TwitchAuthTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        check(TwitchAuthentication.NormalizeToken(" abc123 ") == "abc123" &&
            TwitchAuthentication.NormalizeToken("OAuth:abc123") == "abc123", "Twitch raw and oauth tokens normalized");
        check(TwitchAuthentication.NormalizeToken("http://localhost:3000/#access_token=%61bc123&scope=chat%3Aread&token_type=bearer") == "abc123" &&
            TwitchAuthentication.NormalizeToken("#access_token=abc123&token_type=bearer") == "abc123", "Twitch pasted callback URL and encoded fragment extracted locally");
        foreach (var invalid in new[]
        {
            "", "abc123\n", "oauth:abc 123", "https://localhost/#access_token=abc123&access_token=secret456",
            "#access_token=abc123&ACCESS_TOKEN=secret456", "#access_token=abc%0A123", "#access_token=abc%ZZ",
            "http://localhost/?error=access_denied&error_description=secret456#access_token=abc123",
            "http://localhost/?access_token=secret456#access_token=abc123",
            "#access_token=abc123&error=secret456", "http://localhost/no-fragment", "file:///token#access_token=abc123"
        })
            await ExpectFailure(() => Task.FromResult(TwitchAuthentication.NormalizeToken(invalid)), typeof(SourceConfigurationException), check,
                "Twitch malformed token/callback rejected without exposing input", invalid);

        var waits = new List<TimeSpan>();
        var steps = new Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>>();
        steps.Enqueue(async request =>
        {
            var form = await Form(request);
            check(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://id.twitch.tv/oauth2/device" &&
                form["client_id"] == TwitchAuthentication.DefaultClientId && form["scopes"] == "chat:read user:read:chat" && !form.ContainsKey("client_secret"),
                "Twitch device flow requests receive-only scopes with public client ID");
            return Json("""{"device_code":"deviceSecret","user_code":"ABCDEFGH","verification_uri":"https://www.twitch.tv/activate?public=true&device-code=ABCDEFGH","expires_in":1800,"interval":5}""");
        });
        steps.Enqueue(async request =>
        {
            var form = await Form(request);
            check(request.RequestUri!.AbsolutePath == "/oauth2/token" && form["device_code"] == "deviceSecret" &&
                form["grant_type"] == "urn:ietf:params:oauth:grant-type:device_code" && form["scopes"] == "chat:read user:read:chat", "Twitch device token exchange uses documented fields");
            return Json("""{"status":400,"message":"authorization_pending"}""", 400);
        });
        steps.Enqueue(_ => Task.FromResult(Json("""{"error":"slow_down"}""", 400)));
        steps.Enqueue(_ => Task.FromResult(Tokens));
        steps.Enqueue(request =>
        {
            check(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/oauth2/validate" &&
                request.Headers.Authorization?.ToString() == "OAuth accessSecret", "Twitch validates issued token using authorization header");
            return Task.FromResult(Identity());
        });
        using (var http = new HttpClient(new ScriptHandler(steps)))
        using (var auth = new TwitchAuthentication(http, (wait, ct) => { ct.ThrowIfCancellationRequested(); waits.Add(wait); return Task.CompletedTask; }))
        {
            var device = await auth.StartAsync(TwitchAuthentication.DefaultClientId, CancellationToken.None);
            check(device.UserCode == "ABCDEFGH" && device.VerificationUri.Query.Contains("device-code=ABCDEFGH") &&
                !device.ToString()!.Contains("deviceSecret"), "Twitch activation URL preserves code and diagnostics omit device credential");
            var session = await auth.CompleteAsync(device, CancellationToken.None);
            check(waits.SequenceEqual(new[] { TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10) }) &&
                session.AccessToken == "accessSecret" && session.RefreshToken == "refreshSecret" && session.Identity.Login == "tester" &&
                !session.ToString()!.Contains("Secret"), "Twitch pending and slow-down honored before validated session returned");
            check(steps.Count == 0, "Twitch complete auth script consumed");
        }

        foreach (var error in new[] { "access_denied", "expired_token", "invalid device code", "authorization_declined" })
        {
            using var auth = Auth([_ => Task.FromResult(Json("{\"message\":\"" + error + "\",\"secret\":\"responseSecret\"}", 400))]);
            await ExpectFailure(() => auth.CompleteAsync(Device(), CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch denied/expired/used device code stops polling", "responseSecret");
        }
        using (var auth = Auth([_ => Task.FromResult(Json("""{"message":"responseSecret"}""", 401))]))
            await ExpectFailure(() => auth.ValidateAsync("accessSecret", CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch invalid token stops for reauthentication", "responseSecret", "accessSecret");

        foreach (var body in new[]
        {
            """{"client_id":"client","scopes":["chat:read"],"expires_in":3600}""",
            """{"client_id":"client","login":"tester","user_id":"1","scopes":[],"expires_in":3600}""",
            """{"client_id":"client","login":"tester","user_id":"1","scopes":["chat:read"],"expires_in":"wrong"}"""
        })
        {
            using var auth = Auth([_ => Task.FromResult(Json(body))]);
            await ExpectFailure(() => auth.ValidateAsync("accessSecret", CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch rejects app tokens, missing receive permissions and malformed expiration", "accessSecret");
        }
        using (var auth = Auth([_ => Task.FromResult(Tokens), _ => Task.FromResult(Identity("anotherClient"))]))
            await ExpectFailure(() => auth.CompleteAsync(Device(), CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch issued identity must match requesting client", "accessSecret", "refreshSecret");

        using (var auth = Auth([async request =>
        {
            var form = await Form(request);
            check(form["refresh_token"] == "old+refresh/token" && form["grant_type"] == "refresh_token" &&
                !form.ContainsKey("client_secret"), "Twitch public refresh encodes opaque token without a client secret");
            return Tokens;
        }, _ => Task.FromResult(Identity())]))
        {
            var renewed = await auth.RefreshAsync("old+refresh/token", TwitchAuthentication.DefaultClientId, CancellationToken.None);
            check(renewed.RefreshToken == "refreshSecret", "Twitch replaces single-use refresh token after successful exchange");
        }
        var refreshRequests = 0;
        using (var auth = Auth([_ => { refreshRequests++; throw new HttpRequestException("refreshSecret accessSecret"); }]))
        {
            await ExpectFailure(() => auth.RefreshAsync("refreshSecret", TwitchAuthentication.DefaultClientId, CancellationToken.None),
                typeof(SourceConfigurationException), check, "Twitch ambiguous refresh transport error requests reauthentication without retry", "refreshSecret", "accessSecret");
            check(refreshRequests == 1, "Twitch one-use refresh request attempted once");
        }
        using (var auth = Auth([_ => Task.FromResult(Tokens), _ => throw new HttpRequestException("accessSecret refreshSecret")]))
            await ExpectFailure(() => auth.RefreshAsync("oldRefresh", TwitchAuthentication.DefaultClientId, CancellationToken.None),
                typeof(SourceConfigurationException), check, "Twitch post-refresh validation outage requests reauthentication without reusing consumed token", "accessSecret", "refreshSecret");

        var requests = 0;
        using (var cancelled = new CancellationTokenSource())
        using (var http = new HttpClient(new ScriptHandler(new([_ => { requests++; return Task.FromResult(Tokens); }]))))
        using (var auth = new TwitchAuthentication(http, (_, ct) => { cancelled.Cancel(); return Task.FromCanceled(ct); }))
        {
            await ExpectFailure(() => auth.CompleteAsync(Device(), cancelled.Token), typeof(OperationCanceledException), check,
                "Twitch cancellation interrupts device polling", "deviceSecret");
            check(requests == 0, "Twitch cancelled poll sends no request");
        }
        using (var auth = Auth([]))
            await ExpectFailure(() => auth.CompleteAsync(Device(DateTimeOffset.UtcNow.AddSeconds(-1)), CancellationToken.None),
                typeof(SourceConfigurationException), check, "Twitch expired device code does not send request", "deviceSecret");

        foreach (var uri in new[] { "https://evil.example/activate", "http://www.twitch.tv/activate", "https://www.twitch.tv.evil.example/activate", "https://www.twitch.tv@evil.example/activate", "https://www.twitch.tv/settings" })
        {
            using var auth = Auth([_ => Task.FromResult(Json("{\"verification_uri\":\"" + uri + "\",\"device_code\":\"deviceSecret\",\"user_code\":\"CODE\",\"expires_in\":1800,\"interval\":5}"))]);
            await ExpectFailure(() => auth.StartAsync(TwitchAuthentication.DefaultClientId, CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch activation response only permits official HTTPS activation page", "deviceSecret");
        }
        using (var auth = Auth([_ => Task.FromResult(Json("{not json accessSecret"))]))
            await ExpectFailure(() => auth.ValidateAsync("accessSecret", CancellationToken.None), typeof(SourceConfigurationException), check,
                "Twitch malformed JSON sanitized", "accessSecret");
    }

    private static HttpResponseMessage Tokens => Json("""{"access_token":"accessSecret","refresh_token":"refreshSecret","token_type":"bearer","expires_in":3600}""");
    private static HttpResponseMessage Identity(string? client = null) => Json("{\"client_id\":\"" + (client ?? TwitchAuthentication.DefaultClientId) + "\",\"login\":\"tester\",\"user_id\":\"123\",\"scopes\":[\"chat:read\",\"user:read:chat\"],\"expires_in\":3600}");
    private static TwitchDeviceAuthorization Device(DateTimeOffset? expires = null) => new()
    {
        ClientId = TwitchAuthentication.DefaultClientId, DeviceCode = "deviceSecret", UserCode = "CODE",
        VerificationUri = new("https://www.twitch.tv/activate?device-code=CODE"), ExpiresAt = expires ?? DateTimeOffset.UtcNow.AddMinutes(30), Interval = TimeSpan.FromSeconds(5)
    };
    private static TwitchAuthentication Auth(IEnumerable<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps) =>
        new(new HttpClient(new ScriptHandler(new(steps))), (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; });
    private static HttpResponseMessage Json(string json, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static async Task<Dictionary<string, string>> Form(HttpRequestMessage request) => (await request.Content!.ReadAsStringAsync()).Split('&')
        .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0].Replace('+', ' ')), p => Uri.UnescapeDataString(p[1].Replace('+', ' ')));
    private static async Task ExpectFailure(Func<Task> operation, Type expected, Action<bool, string> check, string name, params string[] secrets)
    {
        try { await operation(); }
        catch (Exception ex)
        {
            check(expected.IsInstanceOfType(ex) && secrets.Where(s => s.Length > 0).All(s => !ex.ToString().Contains(s, StringComparison.Ordinal)), name);
            return;
        }
        check(false, name);
    }
    private sealed class ScriptHandler(Queue<Func<HttpRequestMessage, Task<HttpResponseMessage>>> steps) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!steps.TryDequeue(out var step)) throw new InvalidOperationException("Unexpected Twitch auth request");
            return step(request);
        }
    }
}
