using System.Text.Json;

namespace Tanayomi;

public sealed class SpeechTextStore(string dataDirectory)
{
    // Across the three rule collections each slot has seven string fields.
    // One supplementary scalar can require two six-byte JSON escapes. Allow
    // every validated field at its limit plus bounded property/document overhead.
    private const int MaximumBytes = SpeechTextSettings.MaximumRules *
        (7 * SpeechTextSettings.MaximumRuleScalars * 12 + 256) +
        SpeechTextSettings.MaximumBodyPatterns * (SpeechTextSettings.MaximumRuleScalars * 12 + 4) + 1024;
    private readonly string root = Path.GetFullPath(dataDirectory);
    private readonly SemaphoreSlim gate = new(1, 1);
    private string SettingsPath => Path.Combine(root, "settings", "speech-text.json");

    public async Task<SpeechTextSettings> LoadAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            CheckPath(SettingsPath);
            if (!File.Exists(SettingsPath)) return SpeechTextSettings.Default;
            await using var stream = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaximumBytes) throw new InvalidDataException("文字列設定が大きすぎます。");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, ct).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], ct).ConfigureAwait(false) != 0) throw new InvalidDataException("文字列設定が変更されました。");
            var bomLength = bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF }) ? 3 : 0;
            var document = JsonSerializer.Deserialize<Document>(bytes.AsSpan(bomLength), new JsonSerializerOptions { MaxDepth = 16 })
                ?? throw new InvalidDataException("文字列設定が不正です。");
            if (document.Version != 1 || document.Names is null || document.Replacements is null || document.Muted is null)
                throw new InvalidDataException("文字列設定の形式が不正です。");
            return new SpeechTextSettings(document.NameReading, document.Names, document.Replacements, document.Muted,
                document.RomajiNames, document.RomajiBody, document.SuppressRepeatedMessages, document.OmitUrls, document.BodyLimit, document.MutedBodyPatterns, document.NameAfterBody);
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(SpeechTextSettings settings, CancellationToken ct = default)
    {
        var document = new Document(1, settings.NameReading, settings.NameReadings.ToArray(),
            settings.Replacements.ToArray(), settings.MutedAuthors.ToArray(), settings.RomajiNames, settings.RomajiBody,
            settings.SuppressRepeatedMessages, settings.OmitUrls, settings.BodyLimit, settings.MutedBodyPatterns.ToArray(), settings.NameAfterBody);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("文字列設定が大きすぎます。");
        await gate.WaitAsync(ct).ConfigureAwait(false);
        string? temporary = null;
        try
        {
            CheckPath(SettingsPath);
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, "speech-text-" + Guid.NewGuid().ToString("N") + ".tmp");
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            CheckPath(SettingsPath);
            File.Move(temporary, SettingsPath, overwrite: true);
            temporary = null;
        }
        finally
        {
            try { if (temporary is not null) File.Delete(temporary); }
            finally { gate.Release(); }
        }
    }

    private static void CheckPath(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("リンクされた設定保存先は使えません。");
    }

    private sealed record Document(int Version, NameReadingMode NameReading, SpeechNameReading[] Names,
        SpeechReplacement[] Replacements, SpeechAuthor[] Muted, bool RomajiNames = false, bool RomajiBody = false,
        bool SuppressRepeatedMessages = false, bool OmitUrls = false, int? BodyLimit = null, string[]? MutedBodyPatterns = null, bool NameAfterBody = false);
}
