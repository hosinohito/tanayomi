using System.Text.Json;
using Tanayomi;

internal static class YouTubeParsingTests
{
    internal static void Run(Action<bool, string> check)
    {
        using var data = JsonDocument.Parse("""
        {"items":[null,42,{},
          {"id":"bad1","snippet":null},
          {"id":"bad2","snippet":{"displayMessage":false}},
          {"id":{},"snippet":{"displayMessage":"bad"}},
          {"id":" ","snippet":{"displayMessage":"bad"}},
          {"id":"a","snippet":{"displayMessage":"こんにちは"},"authorDetails":{"displayName":"名前","channelId":"stable"}},
          {"id":"b","snippet":{"displayMessage":"次の発言"},"authorDetails":false},
          {"id":"c","snippet":{"displayMessage":"最後"},"authorDetails":{"displayName":[],"channelId":42}}
        ]}
        """);
        var result = ChatSources.ParseYouTube(data.RootElement, "room", false);
        check(result.Count == 3 && result.Select(x => x.Id).SequenceEqual(new[] { "a", "b", "c" }),
            "YouTube skips malformed individual entries and retains later valid messages in order");
        check(result[0].Author == "名前" && result[0].AuthorId == "stable" && result.All(x => x.Channel == "room"),
            "YouTube retains valid display identity and stable author ID");
        check(result.Skip(1).All(x => x.Author == "" && x.AuthorId == ""),
            "YouTube malformed optional author fields do not fabricate identity or drop valid text");
        check(ChatSources.ParseYouTube(data.RootElement, "room", true).Count == 0,
            "YouTube initial history remains suppressed even with mixed malformed entries");
        using var invalid = JsonDocument.Parse("{\"items\":false}");
        var rejected = false;
        try { ChatSources.ParseYouTube(invalid.RootElement, "room", false); }
        catch (InvalidDataException) { rejected = true; }
        check(rejected, "YouTube invalid batch shape is distinguished from an individual malformed entry");
    }
}
