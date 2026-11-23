using System.Text;
using System.Text.RegularExpressions;
using Tanayomi;

public static class TwitchAnonymousTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var statuses = new List<string>();
        var received = new List<ChatMessage>();
        using (var wire = new DuplexScript("""
            :tmi.twitch.tv 001 anonymous :Welcome, GLHF!
            :tmi.twitch.tv CAP * ACK :twitch.tv/tags twitch.tv/commands
            PING :tmi.twitch.tv
            @emote-only=0;room-id=42 :tmi.twitch.tv ROOMSTATE #hoshinohito
            @id=msg-1;room-id=42;display-name=Name\sA :name!name@name.tmi.twitch.tv PRIVMSG #hoshinohito :hello
            @id=msg-2;room-id=42;display-name=Name :name!name@name.tmi.twitch.tv PRIVMSG #hoshinohito :text with RECONNECT and ROOMSTATE #hoshinohito
            :tmi.twitch.tv RECONNECT
            """))
        {
            await ChatSources.TwitchIrcSessionAsync(wire, " #Hoshinohito ", "invalid login", "secret\ninvalid", received.Add, statuses.Add, CancellationToken.None, anonymous: true);
            var commands = wire.Written.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            check(commands.Length == 4 && Regex.IsMatch(commands[0], "^NICK justinfan[0-9]+$") &&
                commands[1] == "CAP REQ :twitch.tv/tags twitch.tv/commands" && commands[2] == "JOIN #hoshinohito" && commands[3] == "PONG :tmi.twitch.tv",
                "Anonymous IRC uses generated nickname, metadata capabilities, channel and PONG");
            check(!wire.Written.Contains("PASS", StringComparison.Ordinal) && !wire.Written.Contains("secret", StringComparison.Ordinal) &&
                !wire.Written.Contains("PRIVMSG", StringComparison.Ordinal), "Anonymous IRC sends no token, password or chat text");
            check(statuses.Count == 2 && statuses.All(s => s.Contains("匿名", StringComparison.Ordinal)), "Anonymous IRC reports socket welcome and channel confirmation separately");
            check(received.Count == 2 && received[0] is { Id: "msg-1", Author: "Name A", Channel: "42", Text: "hello", Transport: "IRC" } &&
                received[1].Id == "msg-2", "Anonymous IRC shares tagged message parser and treats command words in chat as text");
        }
        using (var wire = new DuplexScript(":tmi.twitch.tv RECONNECT\r\n"))
        {
            await ChatSources.TwitchIrcSessionAsync(wire, "channel", "Login", "oauth:accessSecret", _ => { }, _ => { }, CancellationToken.None);
            check(wire.Written == "PASS oauth:accessSecret\r\nNICK login\r\nCAP REQ :twitch.tv/tags twitch.tv/commands\r\nJOIN #channel\r\n",
                "Authenticated IRC retains OAuth PASS and user login handshake");
        }
        foreach (var channel in new[] { "", "#", "channel\r\nPRIVMSG #other :bad", "has space", "https://twitch.tv/channel" })
        {
            using var wire = new DuplexScript("");
            await ExpectFailure(() => ChatSources.TwitchIrcSessionAsync(wire, channel, "", "", _ => { }, _ => { }, CancellationToken.None, true),
                typeof(SourceConfigurationException), check, "Anonymous IRC rejects absent or malformed channel before sending");
            check(wire.Written.Length == 0, "Invalid anonymous channel sends no protocol data");
        }
        using (var wire = new DuplexScript(":tmi.twitch.tv NOTICE * :Login authentication failed\r\n"))
            await ExpectFailure(() => ChatSources.TwitchIrcSessionAsync(wire, "channel", "", "", _ => { }, _ => { }, CancellationToken.None, true),
                typeof(SourceConfigurationException), check, "Anonymous server rejection becomes actionable authentication fallback");
        using (var wire = new DuplexScript(new string('x', 16385) + "\r\n"))
            await ExpectFailure(() => ChatSources.TwitchIrcSessionAsync(wire, "channel", "", "", _ => { }, _ => { }, CancellationToken.None, true),
                typeof(InvalidDataException), check, "Anonymous IRC shares bounded server-line reader");
        using (var wire = new DuplexScript(""))
            await ExpectFailure(() => ChatSources.TwitchIrcSessionAsync(wire, "channel", "", "", _ => { }, _ => { }, CancellationToken.None, true),
                typeof(IOException), check, "Anonymous server EOF triggers existing reconnect supervision");
        using (var wire = new DuplexScript("", blockAtEnd: true))
        using (var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30)))
            await ExpectFailure(() => ChatSources.TwitchIrcSessionAsync(wire, "channel", "", "", _ => { }, _ => { }, cancellation.Token, true),
                typeof(OperationCanceledException), check, "Anonymous idle read responds to disconnect cancellation");
    }

    private static async Task ExpectFailure(Func<Task> action, Type type, Action<bool, string> check, string name)
    {
        try { await action(); }
        catch (Exception ex) { check(type.IsInstanceOfType(ex), name); return; }
        check(false, name);
    }

    private sealed class DuplexScript(string script, bool blockAtEnd = false) : Stream
    {
        private readonly MemoryStream input = new(Encoding.UTF8.GetBytes(script.Replace("\r\n", "\n").Replace("\n", "\r\n")));
        private readonly MemoryStream output = new();
        public string Written => Encoding.UTF8.GetString(output.ToArray());
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (blockAtEnd && input.Position == input.Length) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await input.ReadAsync(buffer, cancellationToken);
        }
        public override void Write(byte[] buffer, int offset, int count) => output.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => output.WriteAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { input.Dispose(); output.Dispose(); } base.Dispose(disposing); }
    }
}
