using System.Text;
using Tanayomi;

internal static class SpeechTextStoreTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        using var temporary = new TestDirectory();
        var store = new SpeechTextStore(temporary.DirectoryPath);
        check((await store.LoadAsync()).NameReading == NameReadingMode.Off, "absent text settings preserve the existing unnamed reading default");
        var settings = new SpeechTextSettings(NameReadingMode.EveryMessage,
            [new("Twitch", "123", "たろう")], [new("API", "えーぴーあい")], [new("YouTube", "other-id")], romajiNames: true, romajiBody: true,
            suppressRepeatedMessages: true, omitUrls: true, mutedBodyPatterns: ["^!"]);
        await store.SaveAsync(settings);
        var loaded = await store.LoadAsync();
        check(loaded.RomajiNames && loaded.RomajiBody, "romaji opt-in settings round-trip independently");
        check(loaded.SuppressRepeatedMessages, "optional repeated-message suppression round-trips through settings storage");
        check(loaded.MutedBodyPatterns.SequenceEqual(["^!"]) && new SpeechText(loaded).Prepare("Twitch", "123", "Name", "!test").IsMuted,
            "body patterns persist and are revalidated on load");
        check(loaded.OmitUrls, "optional URL omission round-trips through settings storage");
        check(new SpeechText(loaded).Prepare("Twitch", "123", "Changed", "API").Text == "たろう、えーぴーあい" &&
            new SpeechText(loaded).Prepare("YouTube", "other-id", "Name", "text").IsMuted,
            "text settings restore names, literal rules and service-scoped stable IDs");
        var path = Path.Combine(temporary.DirectoryPath, "settings", "speech-text.json");
        var saved = await File.ReadAllBytesAsync(path);
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        try { await store.SaveAsync(SpeechTextSettings.Default, canceled.Token); throw new Exception("cancel not honored"); }
        catch (OperationCanceledException) { }
        var afterCancellation = await File.ReadAllBytesAsync(path);
        check(saved.SequenceEqual(afterCancellation) &&
            !Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "cancelled settings save keeps the previous file without abandoned temporary output");
        await File.WriteAllTextAsync(path, """{"Version":1,"NameReading":0,"Names":[],"Replacements":[],"Muted":[]}""", Encoding.UTF8);
        var legacy = await store.LoadAsync();
        check(!legacy.RomajiNames && !legacy.RomajiBody, "version-one settings without new fields retain romaji disabled");
        check(!legacy.SuppressRepeatedMessages, "legacy settings do not silently enable repeated-message suppression");
        check(legacy.MutedBodyPatterns.Count == 0, "legacy settings never silently enable regex exclusion");
        check(!legacy.OmitUrls, "legacy settings preserve reading URLs as links");
        var fullField = string.Concat(Enumerable.Repeat("😀", SpeechTextSettings.MaximumRuleScalars));
        var idPrefix = string.Concat(Enumerable.Repeat("😀", SpeechTextSettings.MaximumRuleScalars - 3));
        var maximal = new SpeechTextSettings(NameReadingMode.EveryMessage,
            Enumerable.Range(0, SpeechTextSettings.MaximumRules).Select(i => new SpeechNameReading(fullField, idPrefix + i.ToString("D3"), fullField)),
            Enumerable.Range(0, SpeechTextSettings.MaximumRules).Select(_ => new SpeechReplacement(fullField, fullField)),
            Enumerable.Range(0, SpeechTextSettings.MaximumRules).Select(i => new SpeechAuthor(fullField, idPrefix + i.ToString("D3"))));
        await store.SaveAsync(maximal);
        var maximalLoaded = await store.LoadAsync();
        check(new FileInfo(path).Length > 256 * 1024 &&
            maximalLoaded.NameReadings.SequenceEqual(maximal.NameReadings) &&
            maximalLoaded.Replacements.SequenceEqual(maximal.Replacements) &&
            maximalLoaded.MutedAuthors.SequenceEqual(maximal.MutedAuthors),
            "all valid rule collections at maximum escaped Unicode field sizes save and reload without an accidental byte-limit rejection");
        await File.WriteAllTextAsync(path, "{broken", Encoding.UTF8);
        try { await store.LoadAsync(); throw new Exception("corrupt accepted"); }
        catch (System.Text.Json.JsonException) { check((await File.ReadAllTextAsync(path)) == "{broken", "corrupt settings are reported without replacing the user's file"); }
        await File.WriteAllBytesAsync(path, new byte[2 * 1024 * 1024 + 1]);
        try { await store.LoadAsync(); throw new Exception("oversize accepted"); }
        catch (InvalidDataException) { check(true, "settings file input is bounded before JSON allocation"); }

        var irc = ChatSources.ParseIrc("@id=m;room-id=4;user-id=123;display-name=Name :login!x PRIVMSG #room :hello");
        check(irc?.AuthorId == "123" && irc.Id == "m", "IRC retains the author ID separately from the message ID");
        using var eventJson = System.Text.Json.JsonDocument.Parse("""{"metadata":{"message_type":"notification"},"payload":{"event":{"message_id":"m","broadcaster_user_id":"4","chatter_user_id":"123","chatter_user_name":"Renamed","message":{"text":"hello"}}}}""");
        check(TwitchEventSub.ParseNotification(eventJson.RootElement)?.AuthorId == irc?.AuthorId,
            "IRC and EventSub identify a renamed author by the same stable ID");
        using var youtube = System.Text.Json.JsonDocument.Parse("""{"items":[{"id":"m","snippet":{"displayMessage":"hello"},"authorDetails":{"displayName":"Name","channelId":"UC123"}}]}""");
        check(ChatSources.ParseYouTube(youtube.RootElement, "chat", false)[0].AuthorId == "UC123",
            "YouTube retains the author's channel ID independently of display name");
    }
}
