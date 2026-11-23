using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Tanayomi.Desktop;

/// <summary>Small independent SSIP client, written from the protocol specification.
/// No Speech Dispatcher client library or implementation code is included.</summary>
public sealed class LinuxSpeech : ISpeechVolumeControl
{
    private PlaybackVolume volume = new();
    public PlaybackVolume Volume { get => Volatile.Read(ref volume); set => Volatile.Write(ref volume, value ?? throw new ArgumentNullException(nameof(value))); }
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly Func<CancellationToken, Task<Stream>> connect;
    private readonly TimeSpan maximumDuration;
    public LinuxSpeech() : this(ConnectAsync) { }
    public LinuxSpeech(Func<CancellationToken, Task<Stream>> connect, TimeSpan? maximumDuration = null)
    {
        this.connect = connect;
        this.maximumDuration = maximumDuration ?? TimeSpan.FromSeconds(60);
        if (this.maximumDuration <= TimeSpan.Zero || this.maximumDuration > TimeSpan.FromSeconds(60)) throw new ArgumentOutOfRangeException(nameof(maximumDuration));
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(text);
        cancellationToken.ThrowIfCancellationRequested();
        var selectedVolume = Volume.Percent;
        if (text.Length is < 1 or > 2048 || text.Any(c => char.IsControl(c) && c is not '\r' and not '\n' and not '\t')) throw new ArgumentException("読み上げ文の形式が不正です。", nameof(text));
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var data = Utf8.GetBytes(string.Join("\r\n", normalized.Split('\n').Select(line => line.StartsWith('.') ? "." + line : line)) + "\r\n.\r\n");
        if (data.Length > 16 * 1024) throw new ArgumentException("読み上げ文が長すぎます。", nameof(text));
        if (selectedVolume == 0) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lifetime.CancelAfter(maximumDuration);
        var token = lifetime.Token;
        await using var stream = await OpenAsync(token).ConfigureAwait(false);
        var reader = new ReplyReader(stream);
        var events = new Queue<Reply>();
        long? messageId = null;
        var dataMode = false;
        var mayBeQueued = false;
        var queueReplyPending = false;
        var terminal = false;
        try
        {
            foreach (var command in new[] { "SET SELF CLIENT_NAME tanayomi:desktop:chat", "SET SELF LANGUAGE ja", "SET SELF SSML_MODE off", "SET SELF NOTIFICATION END on", "SET SELF NOTIFICATION CANCEL on" })
                RequireSuccess(await CommandAsync(command, token).ConfigureAwait(false));
            if (selectedVolume != 100)
            {
                var baseline = await CommandAsync("GET VOLUME", token).ConfigureAwait(false);
                if (baseline.Code != 251 || baseline.Parameters.Length != 1 ||
                    !int.TryParse(baseline.Parameters[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var level) || level is < -100 or > 100)
                    throw ProtocolError();
                RequireSuccess(await CommandAsync("SET SELF VOLUME " + AttenuatedVolume(level, selectedVolume).ToString(CultureInfo.InvariantCulture), token).ConfigureAwait(false));
            }
            var receiving = await CommandAsync("SPEAK", token).ConfigureAwait(false);
            if (receiving.Code != 230) throw ProtocolError();
            dataMode = true;
            token.ThrowIfCancellationRequested();
            mayBeQueued = true;
            await WriteAsync(data, token).ConfigureAwait(false);
            dataMode = false;
            queueReplyPending = true;
            var queued = await CommandReplyAsync(token).ConfigureAwait(false);
            queueReplyPending = false;
            if (queued.Code != 225 || queued.Parameters.Length < 1 || !TryId(queued.Parameters[0], out var id)) throw ProtocolError();
            messageId = id;
            while (true)
            {
                var notification = events.TryDequeue(out var pending) ? pending : await reader.ReadAsync(token).ConfigureAwait(false);
                if (notification.Code is not (702 or 703)) throw ProtocolError();
                if (notification.Parameters.Length < 2 || !TryId(notification.Parameters[0], out var related) || !TryId(notification.Parameters[1], out _)) throw ProtocolError();
                if (related != id) throw ProtocolError();
                terminal = true;
                if (notification.Code == 703) throw new IOException("Speech Dispatcherがこの発言を取り消しました。");
                break;
            }
            // END already confirms our only queued message finished. SSIP QUIT
            // is optional; dispose the connection rather than making completion
            // depend on another round trip to a peer that may already be gone.
            token.ThrowIfCancellationRequested();
        }
        catch (Exception)
        {
            if (!terminal && (dataMode || mayBeQueued))
            {
                // Keep this connection alive long enough to cancel only its own
                // message. Closing a socket alone does not cancel server playback.
                using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    var recoveringData = dataMode;
                    if (dataMode)
                    {
                        await WriteAsync("\r\n.\r\n"u8.ToArray(), stopping.Token).ConfigureAwait(false);
                        var recovered = await CommandReplyAsync(stopping.Token).ConfigureAwait(false);
                        if (recovered.Code == 225 && recovered.Parameters.Length > 0 && TryId(recovered.Parameters[0], out var recoveredId)) messageId = recoveredId;
                    }
                    else if (queueReplyPending)
                    {
                        var queued = await CommandReplyAsync(stopping.Token).ConfigureAwait(false);
                        if (queued.Code == 225 && queued.Parameters.Length > 0 && TryId(queued.Parameters[0], out var queuedId)) messageId = queuedId;
                    }
                    var cancelled = await CommandAsync("CANCEL SELF", stopping.Token).ConfigureAwait(false);
                    // An interrupted data write may already have delivered its
                    // terminator. Ignore only the bounded syntax-error replies
                    // from the extra recovery terminator, then require success.
                    for (var i = 0; recoveringData && cancelled.Code >= 400 && i < 3; i++)
                        cancelled = await CommandReplyAsync(stopping.Token).ConfigureAwait(false);
                    RequireSuccess(cancelled);
                    if (messageId is { } expected)
                    {
                        var observed = false;
                        for (var i = 0; i < 8 && !observed; i++)
                        {
                            var notice = events.TryDequeue(out var early) ? early : await reader.ReadAsync(stopping.Token).ConfigureAwait(false);
                            observed = (notice.Code is 702 or 703) && notice.Parameters.Length >= 2 && TryId(notice.Parameters[0], out var related) && related == expected && TryId(notice.Parameters[1], out _);
                        }
                        if (!observed) throw ProtocolError();
                    }
                }
                catch (Exception) { throw new IOException("Speech Dispatcherへ停止を要求しましたが、停止の確認ができませんでした。"); }
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (lifetime.IsCancellationRequested) throw new TimeoutException("Linux音声処理が時間内に完了しませんでした。");
            throw;
        }

        async Task WriteAsync(byte[] bytes, CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            // NetworkStream is unbuffered. A separate cancellable Flush after a
            // completed write would make a fully sent frame look only partially sent.
            try { await stream.WriteAsync(bytes, limit.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Speech Dispatcherへの送信が時間切れになりました。"); }
        }
        async Task<Reply> CommandAsync(string command, CancellationToken ct)
        {
            await WriteAsync(Utf8.GetBytes(command + "\r\n"), ct).ConfigureAwait(false);
            return await CommandReplyAsync(ct).ConfigureAwait(false);
        }
        async Task<Reply> CommandReplyAsync(CancellationToken ct)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
            limit.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                for (var i = 0; i < 8; i++)
                {
                    var reply = await reader.ReadAsync(limit.Token).ConfigureAwait(false);
                    if (reply.Code < 700) return reply;
                    if (events.Count >= 8) throw ProtocolError();
                    events.Enqueue(reply);
                }
                throw ProtocolError();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { throw new IOException("Speech Dispatcherの応答が時間切れになりました。"); }
        }
    }

    private async Task<Stream> OpenAsync(CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try { return await connect(limit.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { throw new IOException("Speech Dispatcherへ接続できません。Linux側で起動し、接続先設定を確認してください。"); }
    }

    internal static int AttenuatedVolume(int baseline, int percent)
    {
        if (baseline is < -100 or > 100 || percent is < 0 or > 100) throw new ArgumentOutOfRangeException();
        // SSIP defines an ordered control range, not PCM amplitude. Attenuate
        // within the current connection's baseline; do not claim equal loudness
        // or an acoustic percentage across synthesis modules.
        return (int)Math.Round(-100 + (baseline + 100) * percent / 100.0, MidpointRounding.AwayFromZero);
    }

    internal static EndPoint ResolveEndpoint(string? address, string? runtimeDirectory, string? cacheDirectory, string? homeDirectory)
    {
        if (address is { Length: > 4096 } || address?.Any(char.IsControl) == true) throw AddressError();
        if (string.IsNullOrEmpty(address) || address == "unix_socket" || address.StartsWith("unix_socket:", StringComparison.Ordinal))
        {
            var path = address?.StartsWith("unix_socket:", StringComparison.Ordinal) == true ? address[12..] : null;
            if (string.IsNullOrEmpty(path))
            {
                var runtime = !string.IsNullOrEmpty(runtimeDirectory) ? runtimeDirectory : cacheDirectory;
                if (string.IsNullOrEmpty(runtime))
                {
                    if (string.IsNullOrEmpty(homeDirectory)) throw AddressError();
                    runtime = homeDirectory.TrimEnd('/') + "/.cache";
                }
                path = runtime.TrimEnd('/') + "/speech-dispatcher/speechd.sock";
            }
            if (string.IsNullOrEmpty(path) || !path.StartsWith('/') || path.Any(char.IsControl) || Utf8.GetByteCount(path) > 107) throw AddressError();
            return new UnixDomainSocketEndPoint(path);
        }
        var parts = address.Split(':');
        if (parts[0] != "inet_socket" || parts.Length > 3) throw AddressError();
        var host = parts.Length > 1 && parts[1].Length > 0 ? parts[1] : "127.0.0.1";
        // SSIP has no transport authentication. Only explicitly local TCP peers
        // are accepted; do not resolve arbitrary hosts or send chat to the network.
        if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) host = "127.0.0.1";
        if (!IPAddress.TryParse(host, out var ip) || !IPAddress.IsLoopback(ip)) throw AddressError();
        var port = 6560;
        if (parts.Length == 3 && (!int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) throw AddressError();
        return new IPEndPoint(ip, port);
    }

    private static async Task<Stream> ConnectAsync(CancellationToken ct)
    {
        var endpoint = ResolveEndpoint(Environment.GetEnvironmentVariable("SPEECHD_ADDRESS"), Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR"),
            Environment.GetEnvironmentVariable("XDG_CACHE_HOME"), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        var socket = endpoint is UnixDomainSocketEndPoint ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) : new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        try { await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false); return new NetworkStream(socket, ownsSocket: true); }
        catch { socket.Dispose(); throw; }
    }

    private static bool TryId(string value, out long id) => long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out id) && id > 0;
    private static void RequireSuccess(Reply reply) { if (reply.Code is < 200 or >= 300) throw ProtocolError(); }
    private static IOException ProtocolError() => new("Speech Dispatcherの応答形式または状態が不正です。");
    private static IOException AddressError() => new("Speech Dispatcherの接続先が不正です。UnixソケットまたはループバックのTCPを指定してください。");
    private sealed record Reply(int Code, string[] Parameters);

    // Parser state survives a cancelled read so a subsequent CANCEL can consume
    // the remainder of that same reply instead of losing protocol framing.
    private sealed class ReplyReader(Stream stream)
    {
        private readonly byte[] buffer = new byte[1024], line = new byte[1024];
        private readonly List<string> parts = [];
        private int offset, available, lineLength, frameBytes;
        public async Task<Reply> ReadAsync(CancellationToken ct)
        {
            while (true)
            {
                if (offset == available)
                {
                    available = await stream.ReadAsync(buffer, ct).ConfigureAwait(false); offset = 0;
                    if (available == 0) throw new IOException("Speech Dispatcherとの接続が閉じられました。");
                }
                var value = buffer[offset++];
                if (++frameBytes > 16 * 1024 || lineLength >= line.Length) throw ProtocolError();
                line[lineLength++] = value;
                if (value != '\n') continue;
                if (lineLength < 6 || line[lineLength - 2] != '\r' || line[0] is < (byte)'0' or > (byte)'9' || line[1] is < (byte)'0' or > (byte)'9' || line[2] is < (byte)'0' or > (byte)'9' || (line[3] != (byte)' ' && line[3] != (byte)'-')) throw ProtocolError();
                var code = (line[0] - '0') * 100 + (line[1] - '0') * 10 + line[2] - '0';
                var more = line[3] == '-';
                if (code is < 100 or > 799 || parts.Count >= 32) throw ProtocolError();
                string text;
                try { text = Utf8.GetString(line, 4, lineLength - 6); }
                catch (DecoderFallbackException) { throw ProtocolError(); }
                lineLength = 0;
                if (more) { parts.Add(text); continue; }
                var result = new Reply(code, parts.ToArray()); parts.Clear(); frameBytes = 0;
                return result;
            }
        }
    }
}
