using System.Text.Json;

namespace Tanayomi;

internal sealed record StartupSession(string Channel = "", string Route = "off", string Target = "",
    bool EventSub = false, string Engine = "standard", string Bank = "", string Video = "", string ClientId = "", string AccountId = "", string NitVoice = "nit-atr503",
    string SupertonicTermsAcceptance = "", bool RandomVoice = false)
{
    public static string ChannelName(string? text)
    {
        var value = text?.Trim().TrimStart('#') ?? "";
        return value.Length <= 25 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')
            ? value.ToLowerInvariant() : "";
    }


    public static StartupSession Read(string path)
    {
        CheckPath(path);
        using var file = File.OpenRead(path);
        if (file.Length is < 2 or > 4096) throw new InvalidDataException();
        var bytes = new byte[(int)file.Length];
        file.ReadExactly(bytes);
        var s = JsonSerializer.Deserialize<StartupSession>(bytes) ?? throw new InvalidDataException();
        if (s.Route is not ("off" or "anonymous" or "account") ||
            s.Engine is not ("standard" or "utau" or "nit" or "voicevox") ||
            s.AccountId is null || s.AccountId.Length > 32 || !s.AccountId.All(char.IsAsciiDigit) ||
            s.Channel != ChannelName(s.Channel) || s.Target != ChannelName(s.Target) ||
            s.Route == "anonymous" && s.Target.Length == 0 ||
            s.Bank is null || s.Bank.Length > 255 || s.Bank.IndexOfAny(['/', '\\']) >= 0 ||
            s.Video is null || s.Video.Length > 11 || s.Video.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-' && c != '_') ||
            s.ClientId is null || s.ClientId.Length > 64 || s.ClientId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidDataException();
        s = s with { NitVoice = s.NitVoice is "tohoku-f01-neutral" or "tohoku-f01-angry" or
            "tohoku-f01-happy" or "tohoku-f01-sad" or
            "mei-normal" or "mei-angry" or "mei-bashful" or "mei-happy" or "mei-sad" or
            "takumi-normal" or "takumi-angry" or "takumi-happy" or "takumi-sad" ||
            IsBundledVoiceId(s.NitVoice) ? s.NitVoice : "nit-atr503" };
        return s with {
            Engine = s.Engine == "voicevox" ? "standard" : s.Engine,
            SupertonicTermsAcceptance = IsTermsAcceptanceId(s.SupertonicTermsAcceptance) ? s.SupertonicTermsAcceptance : ""
        };
    }

    internal static bool IsTermsAcceptanceId(string? value) => value is { Length: 64 } &&
        value.All(c => char.IsAsciiDigit(c) || c is >= 'A' and <= 'F');

    // Validate storage syntax here; only the installed voice catalog can select
    // an actual engine. Unknown/unavailable entries fall back during restoration.
    private static bool IsBundledVoiceId(string? value)
    {
        if (value is null || value.Length > 80) return false;
        if (value.StartsWith("aivis-", StringComparison.Ordinal))
            return value.Length > 6 && value[6..].All(c => char.IsAsciiLetterOrDigit(c) || c == '-');
        if (value.StartsWith("voicevox-", StringComparison.Ordinal))
            return value.Length > 9 && value[9..].All(char.IsAsciiDigit) &&
                uint.TryParse(value[9..], out var style) && style <= 10008;
        return value.StartsWith("supertonic3-", StringComparison.Ordinal) && value.Length == 13 && char.IsAsciiDigit(value[12]);
    }

    public void Write(string path)
    {
        CheckPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        CheckPath(path);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var created = false;
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(file, this);
            }
            CheckPath(path);
            File.Move(temp, path, true);
            created = false;
        }
        finally { if (created) { CheckPath(temp); File.Delete(temp); } }
    }

    private static void CheckPath(string path)
    {
        for (string? p = path; p != null; p = Path.GetDirectoryName(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) throw new IOException("リンクされた設定保存先です。"); }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
