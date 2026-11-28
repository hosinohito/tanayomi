using System.Collections.ObjectModel;
using System.Threading.Channels;

namespace Tanayomi;

public sealed partial class MainViewModel
{
    public sealed record KnownAuthor(string Platform, string Id, string Name)
    {
        public override string ToString() => $"{Name} ({Platform}:{Id})";
    }

    private readonly Channel<KnownAuthor> authors = Channel.CreateBounded<KnownAuthor>(
        new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.DropOldest });
    private SpeechTextStore? textStore;
    private Task textLoading = Task.CompletedTask;
    private Task textSaving = Task.CompletedTask;
    public ObservableCollection<KnownAuthor> RecentAuthors { get; } = [];
    public KnownAuthor? SelectedAuthor { get; set; }
    public string SelectedAuthorReading { get; set; } = "";
    private bool readAuthorNames;
    public bool ReadAuthorNames { get => readAuthorNames; set { readAuthorNames = value; Changed(); } }
    public bool NamesOnChange { get; set; }
    public bool NameAfterBody { get; set; }
    public string BodyLimitText { get; set; } = "";
    public bool RomajiNames { get; set; }
    public bool RomajiBody { get; set; }
    public bool SuppressRepeatedMessages { get; set; }
    public bool OmitUrls { get; set; }
    public string NameRulesText { get; set; } = "";
    public string ReplacementRulesText { get; set; } = "";
    public string MutedRulesText { get; set; } = "";
    public string MutedBodyPatternsText { get; set; } = "";
    private bool textSettingsReady;
    public bool TextSettingsReady { get => textSettingsReady; private set { textSettingsReady = value; Changed(); } }
    private string textSettingsStatus = "";
    public string TextSettingsStatus { get => textSettingsStatus; private set { textSettingsStatus = value; Changed(); } }

    private async Task LoadTextSettingsAsync()
    {
        try
        {
            var settings = textStore is null ? SpeechTextSettings.Default : await textStore.LoadAsync(lifetime.Token).ConfigureAwait(false);
            OnUi(() =>
            {
                speech.TextSettings = settings;
                ReadAuthorNames = settings.NameReading != NameReadingMode.Off;
                NamesOnChange = settings.NameReading == NameReadingMode.OnSpeakerChange;
                NameAfterBody = settings.NameAfterBody;
                BodyLimitText = settings.BodyLimit?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "";
                RomajiNames = settings.RomajiNames;
                RomajiBody = settings.RomajiBody;
                SuppressRepeatedMessages = settings.SuppressRepeatedMessages;
                OmitUrls = settings.OmitUrls;
                NameRulesText = string.Join('\n', settings.NameReadings.Select(x => $"{x.Platform}:{x.AuthorId}={x.Reading}"));
                ReplacementRulesText = string.Join('\n', settings.Replacements.Select(x => $"{x.Key}={x.Reading}"));
                MutedRulesText = string.Join('\n', settings.MutedAuthors.Select(x => $"{x.Platform}:{x.AuthorId}"));
                Changed(nameof(ReadAuthorNames)); Changed(nameof(NameRulesText));
                Changed(nameof(NamesOnChange)); Changed(nameof(NameAfterBody)); Changed(nameof(BodyLimitText));
                Changed(nameof(RomajiNames)); Changed(nameof(RomajiBody));
                MutedBodyPatternsText = string.Join('\n', settings.MutedBodyPatterns);
                Changed(nameof(MutedBodyPatternsText));
                Changed(nameof(SuppressRepeatedMessages));
                Changed(nameof(OmitUrls));
                Changed(nameof(ReplacementRulesText)); Changed(nameof(MutedRulesText));
            }, lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Record(DiagnosticCode.TextSettingsReadFailed, DiagnosticOperation.TextSettings,
                DiagnosticRecovery.WithoutStorage, error);
            OnUi(() => TextSettingsStatus = "文字列設定を読み込めませんでした。既定設定で続けます。保存済みファイルは変更していません。", lifetime.Token);
        }
        finally { OnUi(() => TextSettingsReady = true, lifetime.Token); }
    }

    public Task ApplyTextSettingsAsync()
    {
        if (disposed || !TextSettingsReady) return Task.CompletedTask;
        TextSettingsReady = false;
        return textSaving = SaveTextSettingsAsync();
    }

    private async Task SaveTextSettingsAsync()
    {
        try
        {
            var names = Lines(NameRulesText).Select(line =>
            {
                var (key, reading) = Pair(line);
                var author = AuthorKey(key);
                return new SpeechNameReading(author.Platform, author.AuthorId, reading);
            }).ToArray();
            var replacements = Lines(ReplacementRulesText).Select(line =>
            {
                var (key, reading) = Pair(line);
                return new SpeechReplacement(key, reading);
            }).ToArray();
            var muted = Lines(MutedRulesText).Select(AuthorKey).ToArray();
            int? limit = null;
            if (!string.IsNullOrWhiteSpace(BodyLimitText))
            {
                if (!int.TryParse(BodyLimitText.Trim(), System.Globalization.NumberStyles.None,
                        System.Globalization.CultureInfo.InvariantCulture, out var number) || number is < 1 or > SpeechText.MaximumSpokenScalars)
                {
                    OnUi(() => TextSettingsStatus = "本文の上限は1～180の整数か空欄にしてください。以前の設定を使います。", lifetime.Token);
                    return;
                }
                limit = number;
            }
            var mode = !ReadAuthorNames ? NameReadingMode.Off : NamesOnChange ? NameReadingMode.OnSpeakerChange : NameReadingMode.EveryMessage;
            var settings = new SpeechTextSettings(mode,
                names, replacements, muted, RomajiNames, RomajiBody, SuppressRepeatedMessages, OmitUrls, limit,
                MutedBodyPatternsText.Split(['\r', '\n']).Where(x => !string.IsNullOrWhiteSpace(x)), NameAfterBody);
            if (textStore is not null) await textStore.SaveAsync(settings, lifetime.Token).ConfigureAwait(false);
            // Saving is atomic. Do not replace active rules if validation or persistence failed.
            speech.TextSettings = settings;
            OnUi(() => TextSettingsStatus = textStore is null ? "今回の起動中の設定を適用しました。" :
                "保存しました。次に受信する発言から適用します。", lifetime.Token);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ArgumentException error) when (error.ParamName == "mutedBodyPatterns")
        {
            OnUi(() => TextSettingsStatus = error.Message.Split('\n')[0] + " 以前の設定を使います。", lifetime.Token);
        }
        catch (ArgumentException)
        {
            OnUi(() => TextSettingsStatus = "設定の形式、重複、文字数を確認してください。各種類128件、各項目128文字までです。以前の設定を使います。", lifetime.Token);
        }
        catch (Exception error)
        {
            Record(DiagnosticCode.TextSettingsWriteFailed, DiagnosticOperation.TextSettings,
                DiagnosticRecovery.RetryOperation, error);
            OnUi(() => TextSettingsStatus = "文字列設定を保存できませんでした。以前の設定を使います。", lifetime.Token);
        }
        finally { OnUi(() => TextSettingsReady = true, lifetime.Token); }
    }

    public void AddAuthorReading()
    {
        if (!TextSettingsReady || SelectedAuthor is not { } author || string.IsNullOrWhiteSpace(SelectedAuthorReading)) return;
        var key = $"{author.Platform}:{author.Id}=";
        NameRulesText = string.Join('\n', NameRulesText.Split('\n').Where(x => !SameAuthorKey(x.Split('=')[0], key.TrimEnd('=')))
            .Where(x => !string.IsNullOrWhiteSpace(x)).Append(key + SelectedAuthorReading.Trim()));
        Changed(nameof(NameRulesText));
    }

    public void AddMutedAuthor()
    {
        if (!TextSettingsReady || SelectedAuthor is not { } author) return;
        var key = $"{author.Platform}:{author.Id}";
        MutedRulesText = string.Join('\n', MutedRulesText.Split('\n').Where(x => !string.IsNullOrWhiteSpace(x) && !SameAuthorKey(x, key))
            .Append(key));
        Changed(nameof(MutedRulesText));
    }

    private static bool SameAuthorKey(string left, string right)
    {
        var a = left.Trim().Split(':', 2);
        var b = right.Trim().Split(':', 2);
        return a.Length == 2 && b.Length == 2 && a[0].Equals(b[0], StringComparison.OrdinalIgnoreCase) && a[1] == b[1];
    }

    private void ReceiveChat(ChatMessage message)
    {
        if (!string.IsNullOrWhiteSpace(message.AuthorId) && message.AuthorId.Length <= 128 && message.Author.Length <= 256)
        {
            try { authors.Writer.TryWrite(new(message.Platform, message.AuthorId, SpeechQueue.PrepareText(message.Author))); }
            catch (Exception error) when (error is ArgumentException or System.Text.RegularExpressions.RegexMatchTimeoutException)
            { /* An invalid display name must not interrupt chat reception. */ }
        }
        speech.Enqueue(message);
    }

    private void DrainAuthors()
    {
        for (var count = 0; count < 32 && authors.Reader.TryRead(out var author); count++)
        {
            var existing = RecentAuthors.FirstOrDefault(x => x.Platform == author.Platform && x.Id == author.Id);
            if (existing is not null)
            {
                if (existing.Name == author.Name) continue;
                RecentAuthors[RecentAuthors.IndexOf(existing)] = author;
                if (SelectedAuthor == existing) { SelectedAuthor = author; Changed(nameof(SelectedAuthor)); }
            }
            else
            {
                if (RecentAuthors.Count == 128) RecentAuthors.RemoveAt(0);
                RecentAuthors.Add(author);
            }
        }
    }

    private static IEnumerable<string> Lines(string text)
    {
        if (text.Length > 65536) throw new ArgumentException();
        var lines = text.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).ToArray();
        if (lines.Length > SpeechTextSettings.MaximumRules) throw new ArgumentException();
        return lines;
    }
    private static (string Key, string Reading) Pair(string line)
    {
        var equals = line.IndexOf('=');
        if (equals <= 0) throw new ArgumentException();
        return (line[..equals].Trim(), line[(equals + 1)..].Trim());
    }
    private static SpeechAuthor AuthorKey(string key)
    {
        var colon = key.IndexOf(':');
        if (colon <= 0 || colon == key.Length - 1) throw new ArgumentException();
        var platform = key[..colon].Trim();
        platform = platform.Equals("Twitch", StringComparison.OrdinalIgnoreCase) ? "Twitch" :
            platform.Equals("YouTube", StringComparison.OrdinalIgnoreCase) ? "YouTube" : throw new ArgumentException();
        return new(platform, key[(colon + 1)..].Trim());
    }
}
