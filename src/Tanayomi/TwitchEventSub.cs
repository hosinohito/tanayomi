using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tanayomi;

public static class TwitchEventSub
{
    public static async Task RunAsync(string channel, string token, string clientId, Action<ChatMessage> receive,
        Action<string> status, CancellationToken ct)
    {
        token = TwitchAuthentication.NormalizeToken(token);
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(token))
            throw new SourceConfigurationException("EventSubにはクライアントIDとuser:read:chat権限のトークンが必要です。");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(8), MaxResponseContentBufferSize = 1024 * 1024 };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        http.DefaultRequestHeaders.Add("Client-Id", clientId.Trim());
        using var self = await GetAsync(http, "https://api.twitch.tv/helix/users", ct);
        using var target = await GetAsync(http, "https://api.twitch.tv/helix/users?login=" + Uri.EscapeDataString(channel.Trim().TrimStart('#')), ct);
        var userId = UserId(self); var broadcaster = UserId(target);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Session? current = null;
        Task<Session>? replacement = null;
        Task<JsonDocument>? receiving = null;
        try
        {
            current = await ConnectAsync(new Uri("wss://eventsub.wss.twitch.tv/ws"), lifetime.Token);
            var subscription = new JsonObject
            {
                ["type"] = "channel.chat.message", ["version"] = "1",
                ["condition"] = new JsonObject { ["broadcaster_user_id"] = broadcaster, ["user_id"] = userId },
                ["transport"] = new JsonObject { ["method"] = "websocket", ["session_id"] = current.Id }
            };
            using var body = new StringContent(subscription.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.PostAsync("https://api.twitch.tv/helix/eventsub/subscriptions", body, lifetime.Token);
            CheckResponse(response);
            status("Twitchに接続しました · EventSub WebSocket");
            receiving = ReadAsync(current, lifetime.Token);
            while (!ct.IsCancellationRequested)
            {
                if (replacement is not null && await Task.WhenAny(receiving, replacement) == replacement)
                {
                    var next = await replacement;
                    replacement = null;
                    // Continue reading the old connection until the new one sends session_welcome.
                    current.Dispose();
                    current = next;
                    try { using var last = await receiving; Deliver(last.RootElement, receive); }
                    catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException) { }
                    receiving = ReadAsync(current, lifetime.Token);
                    status("EventSubの接続を引き継ぎました。");
                    continue;
                }
                using var message = await receiving;
                var root = message.RootElement;
                var type = root.GetProperty("metadata").GetProperty("message_type").GetString();
                switch (type)
                {
                    case "notification": Deliver(root, receive); break;
                    case "session_reconnect":
                        var url = root.GetProperty("payload").GetProperty("session").GetProperty("reconnect_url").GetString();
                        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "wss" || uri.Host != "eventsub.wss.twitch.tv")
                            throw new InvalidDataException("Invalid reconnect URL.");
                        replacement ??= ConnectAsync(uri, lifetime.Token);
                        break;
                    case "revocation": throw new SourceConfigurationException("EventSubの購読が無効になりました。再認証してください。");
                }
                receiving = ReadAsync(current, lifetime.Token);
            }
        }
        finally
        {
            lifetime.Cancel();
            current?.Dispose();
            if (receiving is not null) try { using var unused = await receiving; } catch (Exception) { }
            if (replacement is not null) try { (await replacement).Dispose(); } catch (Exception) { }
        }
    }

    public static ChatMessage? ParseNotification(JsonElement root)
    {
        if (!root.TryGetProperty("metadata", out var metadata) || !metadata.TryGetProperty("message_type", out var type) || type.GetString() != "notification" ||
            !root.TryGetProperty("payload", out var payload) || !payload.TryGetProperty("event", out var item) ||
            !item.TryGetProperty("message_id", out var id) || !item.TryGetProperty("broadcaster_user_id", out var channel) ||
            !item.TryGetProperty("message", out var message) || !message.TryGetProperty("text", out var text)) return null;
        return new("Twitch", channel.GetString() ?? "", id.GetString() ?? "",
            item.TryGetProperty("chatter_user_name", out var name) ? name.GetString() ?? "" : "", text.GetString() ?? "", "EventSub", Stopwatch.GetTimestamp())
            { AuthorId = item.TryGetProperty("chatter_user_id", out var authorId) ? authorId.GetString() ?? "" : "" };
    }

    private static void Deliver(JsonElement root, Action<ChatMessage> receive)
    {
        try { if (ParseNotification(root) is { } message) receive(message); }
        catch (InvalidOperationException) { /* Skip a malformed event while keeping this connection. */ }
    }

    private static string UserId(JsonDocument data)
    {
        var users = data.RootElement.GetProperty("data");
        if (users.GetArrayLength() == 0) throw new SourceConfigurationException("Twitchのユーザーが見つかりません。");
        return users[0].GetProperty("id").GetString()!;
    }
    private static async Task<JsonDocument> GetAsync(HttpClient http, string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        CheckResponse(response);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    private static void CheckResponse(HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.Unauthorized)
            throw new SourceConfigurationException("EventSubの認証が無効です（401）。再認証してください。");
        if (response.StatusCode == HttpStatusCode.Forbidden)
            throw new SourceConfigurationException("EventSubの権限が不足しています（403）。user:read:chatを許可して再認証してください。");
        if (response.StatusCode == HttpStatusCode.BadRequest)
            throw new SourceConfigurationException("EventSubの接続設定を受け付けられませんでした（400）。チャンネル名を確認してください。");
        response.EnsureSuccessStatusCode();
    }
    private static async Task<Session> ConnectAsync(Uri url, CancellationToken ct)
    {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = Timeout.InfiniteTimeSpan;
        var session = new Session(socket, "", 10);
        try
        {
            await socket.ConnectAsync(url, ct);
            using var welcome = await ReadAsync(session, ct);
            if (welcome.RootElement.GetProperty("metadata").GetProperty("message_type").GetString() != "session_welcome") throw new InvalidDataException();
            var data = welcome.RootElement.GetProperty("payload").GetProperty("session");
            var keepalive = data.TryGetProperty("keepalive_timeout_seconds", out var timeout) && timeout.TryGetInt32(out var seconds) ? seconds : 10;
            return new(socket, data.GetProperty("id").GetString()!, Math.Clamp(keepalive, 10, 600));
        }
        catch { session.Dispose(); throw; }
    }
    private static async Task<JsonDocument> ReadAsync(Session session, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(session.Keepalive + 2));
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        ValueWebSocketReceiveResult result;
        do
        {
            result = await session.Socket.ReceiveAsync(chunk.AsMemory(), timeout.Token);
            if (result.MessageType != WebSocketMessageType.Text) throw new IOException("WebSocket closed or unexpected payload.");
            if (buffer.Length + result.Count > 1024 * 1024) throw new InvalidDataException();
            buffer.Write(chunk, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(buffer.ToArray());
    }
    private sealed record Session(ClientWebSocket Socket, string Id, int Keepalive) : IDisposable
    {
        public void Dispose() { Socket.Abort(); Socket.Dispose(); }
    }
}
