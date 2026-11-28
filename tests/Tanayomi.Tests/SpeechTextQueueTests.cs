using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Tanayomi;

public static class SpeechTextQueueTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await FilterBeforeCapacityAsync(check);
        await PreparedFallbackAsync(check);
        await ArrivalSnapshotAsync(check);
        await RepeatDefaultsAndWindowAsync(check);
        await RepeatScopeAsync(check);
        await RepeatCapacityAsync(check);
        await RepeatSettingsAndRejectedAsync(check);
        await RepeatBoundAsync(check);
        await RepeatReentrantObserverAsync(check);
        await RepeatDistinctIdObserverAsync(check);
        await SpeakerChangesAsync(check);
    }

    private static async Task SpeakerChangesAsync(Action<bool, string> check)
    {
        var output = new Probe();
        await using var queue = new SpeechQueue(output)
        { TextSettings = new(NameReadingMode.OnSpeakerChange, bodyLimit: 2) };
        async Task<string> Read(ChatMessage message)
        {
            if (!queue.Enqueue(message)) throw new InvalidOperationException("test message rejected");
            return await output.NextAsync();
        }
        check(await Read(Message("1", "A", "😀です")) == "表示、😀で、以下省略", "body limit counts Unicode scalars independently of the speaker name");
        var stale = Message("old", "B", "old") with { ReceivedAt = Stopwatch.GetTimestamp() - Stopwatch.Frequency * 300 };
        queue.Enqueue(stale);
        check(await Read(Message("2", "A", "次") with { Transport = "EventSub" }) == "次",
            "an expired message does not switch speakers and IRC/EventSub share identity");
        check(await Read(Message("3", "B", "次")) == "表示、次" &&
            await Read(Message("4", "B", "次") with { Channel = "other" }) == "表示、次",
            "same display name with a different ID or channel is announced again");
        check(await Read(ChatMessage.Manual("手動入力")) == "手動入力" &&
            await Read(Message("5", "B", "次") with { Channel = "other" }) == "表示、次",
            "manual input keeps its normal length and interrupts the run of a chat speaker");
        check(await Read(Message("6", "", "次")) == "表示、次" && await Read(Message("7", "", "次")) == "表示、次",
            "messages without a stable ID never suppress a speaker based only on display name");
        queue.TextSettings = new(NameReadingMode.OnSpeakerChange, mutedBodyPatterns: ["^[#]"], nameAfterBody: true);
        check(await Read(Message("suffix1", "C", "本文")) == "本文、表示", "chat queue speaks the author's name after the body");
        check(!queue.Enqueue(Message("excluded", "D", "#command")), "a regex-matching chat does not enter the speech queue");
        check(await Read(Message("suffix2", "C", "次")) == "次",
            "a filtered speaker does not change speaker history when names are read last");
        check(await Read(Message("suffix3", "D", "本文")) == "本文、表示", "the next audible speaker still gets the suffix name");
    }

    private static async Task RepeatDefaultsAndWindowAsync(Action<bool, string> check)
    {
        var output = new Probe(); var clock = new Clock();
        await using var queue = new SpeechQueue(output, timeProvider: clock);
        check(!SpeechTextSettings.Default.SuppressRepeatedMessages &&
            queue.Enqueue(Message("default-a", "42", "same")) && queue.Enqueue(Message("default-b", "42", "same")),
            "repeat suppression is off by default and repeated text remains readable");
        await output.NextAsync(); await output.NextAsync();
        queue.TextSettings = new(suppressRepeatedMessages: true);
        check(queue.Enqueue(Message("first", "42", "same")), "repeat suppression records an accepted message");
        await output.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        var repeated = Message("suppressed", "42", "same");
        check(!queue.Enqueue(repeated), "same-author identical text is suppressed inside five seconds");
        clock.Advance(TimeSpan.FromSeconds(1));
        check(queue.Enqueue(repeated with { Transport = "EventSub" }),
            "five-second expiry is exact and a suppressed message ID remains eligible for a later accepted retry");
        await output.NextAsync();
        clock.Advance(TimeSpan.FromSeconds(4));
        check(!queue.Enqueue(Message("last", "42", "same")), "repeat window restarts from the latest accepted copy");
        clock.Advance(TimeSpan.FromSeconds(1));
        check(queue.Enqueue(Message("last", "42", "same")), "suppressed retries do not prolong a repeat window");
        await output.NextAsync();
    }

    private static async Task RepeatScopeAsync(Action<bool, string> check)
    {
        var output = new Probe();
        await using var queue = new SpeechQueue(output, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        var first = Message("scope-first", "42", "same", "表示");
        check(queue.Enqueue(first), "repeat fixture accepts the first stable-author message"); await output.NextAsync();
        check(!queue.Enqueue(first with { Id = "renamed", Author = "変更した表示名" }),
            "repeat suppression follows stable author ID rather than display name");
        check(!queue.Enqueue(first with { Id = "platform-case", Platform = "twitch" }),
            "repeat suppression normalizes service identity consistently with author rules");
        foreach (var other in new[]
        {
            first with { Id = "other-author", AuthorId = "43" },
            first with { Id = "other-channel", Channel = "another" },
            first with { Id = "other-service", Platform = "YouTube" },
            first with { Id = "missing-author", AuthorId = "" },
            first with { Id = "missing-author-2", AuthorId = "" },
            first with { Id = "invalid-author", AuthorId = "bad id" },
            first with { Id = "invalid-author-2", AuthorId = "bad id" },
            first with { Id = "local-input", Transport = "local" },
            first with { Id = "local-input-2", Transport = "local" },
            ChatMessage.Manual("same") with { AuthorId = "42" },
            ChatMessage.Manual("same") with { AuthorId = "42" }
        })
        {
            check(queue.Enqueue(other), "other authors channels services manual input and absent stable IDs are not conflated");
            await output.NextAsync();
        }
        foreach (var (text, expected) in new[]
        {
            ("a b", "a b"), (" a\nb ", "a b"),
            ("https://first.example/a", "リンク"), ("https://second.example/b", "リンク"),
            ("\ud800", "\ufffd"), ("\ud801", "\ufffd")
        })
        {
            check(queue.Enqueue(Message(Guid.NewGuid().ToString("N"), "42", text)) && await output.NextAsync() == expected,
                "repeat matching hashes original UTF16 exactly rather than normalized speech or URL output");
        }
    }

    private static async Task RepeatCapacityAsync(Action<bool, string> check)
    {
        var started = Signal(); var release = Signal();
        var output = new Probe(async (text, ct) =>
        { if (text == "hold") { started.TrySetResult(); await release.Task.WaitAsync(ct); } });
        await using var queue = new SpeechQueue(output, capacity: 2, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        queue.Enqueue(ChatMessage.Manual("hold")); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(queue.Enqueue(Message("repeat-first", "42", "same")) && queue.Enqueue(Message("survivor", "43", "keep")),
            "repeat fixture fills both waiting slots with accepted messages");
        check(!queue.Enqueue(Message("repeat-rejected", "42", "same")) && queue.PendingCount == 2,
            "suppressed repeated messages cannot evict either waiting message");
        release.TrySetResult();
        check(new[] { await output.NextAsync(), await output.NextAsync(), await output.NextAsync() }.SequenceEqual(["hold", "same", "keep"]),
            "repeat filtering preserves waiting speech order and capacity");
    }

    private static async Task RepeatSettingsAndRejectedAsync(Action<bool, string> check)
    {
        var output = new Probe();
        await using var queue = new SpeechQueue(output, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        var message = Message("retry-after-suspend", "42", "same");
        queue.SetSuspended(true);
        check(!queue.Enqueue(message), "suspended queues do not accept a repeat candidate");
        queue.SetSuspended(false);
        check(queue.Enqueue(message), "a rejected arrival neither seeds repeat history nor occupies its transport ID");
        await output.NextAsync();
        check(!queue.Enqueue(message with { Id = "suppressed-before-change" }), "repeat fixture has a live suppression entry");
        queue.TextSettings = new(suppressRepeatedMessages: true);
        check(queue.Enqueue(message with { Id = "suppressed-before-change" }), "publishing replacement settings invalidates prior repeat history");
        await output.NextAsync();
        check(!queue.Enqueue(message with { Transport = "EventSub" }), "settings changes preserve existing cross-transport ID deduplication");

        queue.TextSettings = new(NameReadingMode.EveryMessage, suppressRepeatedMessages: true);
        var invalidName = Message("invalid-name-retry", "42", "body", new string('a', SpeechText.MaximumInputScalars + 1));
        check(!queue.Enqueue(invalidName), "failed text preparation is rejected before repeat history changes");
        check(queue.Enqueue(invalidName with { Author = "短い名前" }) && await output.NextAsync() == "短い名前、body",
            "preparation failure cannot poison later valid text with the same author body and message ID");
        check(!queue.Enqueue(invalidName with { Id = "new-id-same-body", Author = "別の表示名" }),
            "author-name formatting does not turn an original repeated body into a different message");
        var duplicateId = Message("duplicate-body-a", "44", "A");
        check(queue.Enqueue(duplicateId), "repeat fixture accepts the first transport ID"); await output.NextAsync();
        check(!queue.Enqueue(duplicateId with { Text = "B" }) && queue.Enqueue(duplicateId with { Id = "fresh-body-b", Text = "B" }),
            "an ID-rejected body does not seed repeat history for an otherwise eligible new ID");
        await output.NextAsync();
    }

    private static async Task RepeatBoundAsync(Action<bool, string> check)
    {
        var started = Signal(); var release = Signal();
        var output = new Probe(async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); });
        await using var queue = new SpeechQueue(output, capacity: 600, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        queue.Enqueue(ChatMessage.Manual("hold")); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            var seed = Message("seed", "42", "oldest");
            var allAccepted = queue.Enqueue(seed);
            for (var i = 1; i < 512; i++) allAccepted &= queue.Enqueue(Message("bounded-" + i, "42", "body-" + i));
            check(allAccepted && !queue.Enqueue(seed with { Id = "still-retained" }), "repeat history retains all of its first 512 accepted keys");
            check(queue.Enqueue(Message("overflow", "42", "overflow")) && queue.Enqueue(seed with { Id = "evicted" }),
                "repeat history evicts the oldest accepted key at its fixed 512-entry bound");
            check(!queue.Enqueue(seed with { Transport = "EventSub" }),
                "repeat-key eviction does not erase the independent transport-ID duplicate history");
        }
        finally { queue.Clear(); release.TrySetResult(); }
    }

    private static async Task RepeatReentrantObserverAsync(Action<bool, string> check)
    {
        var started = Signal(); var release = Signal();
        var output = new Probe(async (text, ct) =>
        { if (text == "hold") { started.TrySetResult(); await release.Task.WaitAsync(ct); } });
        await using var queue = new SpeechQueue(output, capacity: 1, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        queue.Enqueue(ChatMessage.Manual("hold")); await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.Enqueue(Message("pending", "43", "waiting"));
        var outer = Message("outer", "42", "epoch");
        var changed = false; var duplicateRejected = false;
        queue.Notice += notice =>
        {
            if (notice.Kind != "省略" || changed) return;
            changed = true;
            queue.TextSettings = new(suppressRepeatedMessages: true);
            duplicateRejected = !queue.Enqueue(outer);
        };
        check(queue.Enqueue(outer) && changed && duplicateRejected,
            "reentrant capacity observers preserve the in-flight transport ID reservation");
        check(queue.Enqueue(outer with { Id = "new-epoch" }),
            "an arrival prepared before an observer's settings swap cannot seed the new repeat epoch");
        release.TrySetResult(); await output.NextAsync(); await output.NextAsync();
    }

    private static async Task RepeatDistinctIdObserverAsync(Action<bool, string> check)
    {
        var started = Signal(); var release = Signal();
        var output = new Probe(async (text, ct) =>
        { if (text == "hold") { started.TrySetResult(); await release.Task.WaitAsync(ct); } });
        await using var queue = new SpeechQueue(output, capacity: 2, timeProvider: new Clock())
        { TextSettings = new(suppressRepeatedMessages: true) };
        queue.Enqueue(ChatMessage.Manual("hold"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            queue.Enqueue(Message("observer-oldest", "41", "oldest"));
            queue.Enqueue(Message("observer-survivor", "43", "keep"));
            var outer = Message("observer-outer", "42", "same");
            var attempted = false; var nestedAccepted = false; var capacityNotices = 0;
            queue.Notice += notice =>
            {
                if (notice.Kind != "省略") return;
                capacityNotices++;
                if (attempted) return;
                attempted = true;
                // Keep the same settings epoch and original author/body, but
                // change the transport ID so only repeat suppression can reject it.
                nestedAccepted = queue.Enqueue(outer with { Id = "observer-inner" });
            };
            check(queue.Enqueue(outer) && attempted && !nestedAccepted && capacityNotices == 1 && queue.PendingCount == 2,
                "capacity notice observers see committed repeat history and reject a distinct-ID copy without evicting speech");
            release.TrySetResult();
            var spoken = new[] { await output.NextAsync(), await output.NextAsync(), await output.NextAsync() };
            check(spoken.SequenceEqual(new[] { "hold", "keep", "same" }),
                "distinct-ID observer reentry preserves the surviving utterance and reads the repeated body only once");
        }
        finally { release.TrySetResult(); }
    }

    private static async Task FilterBeforeCapacityAsync(Action<bool, string> check)
    {
        var started = Signal();
        var release = Signal();
        var output = new Probe(async (text, ct) =>
        {
            if (text == "hold") { started.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        await using var queue = new SpeechQueue(output, capacity: 2)
        {
            TextSettings = new(replacements: [new("消す", "")], mutedAuthors: [new("Twitch", "muted")], suppressRepeatedMessages: true, omitUrls: true)
        };
        check(queue.Enqueue(ChatMessage.Manual("hold")), "text queue accepts the controlled active utterance");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(queue.Enqueue(Message("first", "1", "先")) && queue.Enqueue(Message("second", "2", "後")),
            "text queue fills both pending slots behind active output");
        var muted = Message("muted-id", "muted", "ミュート対象");
        var allRejected = true;
        for (var i = 0; i < 20; i++) allRejected &= !queue.Enqueue(muted with { Id = "muted-" + i });
        allRejected &= !queue.Enqueue(muted);
        check(allRejected && queue.PendingCount == 2, "muted arrivals never consume queue capacity or evict waiting speech");
        var deleted = Message("deleted-id", "3", "消す");
        check(!queue.Enqueue(deleted) && queue.PendingCount == 2, "dictionary-empty arrivals do not evict waiting speech");
        var urlOnly = Message("url-only-id", "3", "https://example.test/a");
        check(!queue.Enqueue(urlOnly) && !queue.Enqueue(ChatMessage.Manual(urlOnly.Text)) && queue.PendingCount == 2,
            "URL-only chat and manual input do not consume capacity when URL omission is enabled");
        var oversized = Message("oversized-id", "4", new string('a', SpeechText.MaximumInputScalars + 1));
        check(!queue.Enqueue(oversized) && queue.PendingCount == 2, "invalid oversized speech is isolated before queue insertion");

        release.TrySetResult();
        var spoken = new[] { await output.NextAsync(), await output.NextAsync(), await output.NextAsync() };
        check(spoken.SequenceEqual(new[] { "hold", "先", "後" }), "eligible waiting messages retain order despite filtered traffic");
        queue.TextSettings = SpeechTextSettings.Default;
        check(queue.Enqueue(muted), "muted arrivals did not occupy cross-transport deduplication state");
        check(await output.NextAsync() == "ミュート対象", "unmuting permits the same previously filtered message ID");
        check(queue.Enqueue(deleted with { Transport = "EventSub" }) && await output.NextAsync() == "消す",
            "dictionary-empty arrivals did not mark an unsaid message as already received");
        check(queue.Enqueue(urlOnly with { Transport = "EventSub" }) && await output.NextAsync() == "リンク",
            "URL-only omitted arrivals do not occupy deduplication state and can be read after disabling omission");
        check(queue.Enqueue(oversized with { Text = "短い" }) && await output.NextAsync() == "短い",
            "invalid preparation does not poison a subsequent valid message with the same ID");
    }

    private static async Task PreparedFallbackAsync(Action<bool, string> check)
    {
        var failed = Signal();
        var release = Signal();
        var primary = new Probe(async (_, ct) =>
        {
            failed.TrySetResult();
            await release.Task.WaitAsync(ct);
            throw new IOException("test primary failure");
        });
        var fallback = new Probe();
        await using var queue = new SpeechQueue(primary, fallback)
        {
            TextSettings = new(NameReadingMode.EveryMessage, [new("Twitch", "42", "あき")],
                [new("hello", "こんにちは"), new("リンク", "URL")], omitUrls: true)
        };
        var notices = new ConcurrentQueue<AppNotice>();
        queue.Notice += notices.Enqueue;
        var message = Message("fallback", "42", "hello https://example.test/a", "表示名");
        check(queue.Enqueue(message), "prepared fallback test accepts a real-author message");
        await failed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        queue.TextSettings = new(replacements: [new("こんにちは", "再処理しない"), new("hello", "別設定")]);
        release.TrySetResult();
        var spoken = await fallback.NextAsync();
        check(spoken == "あき、こんにちは" && primary.Attempts.Single() == spoken,
            "primary and fallback receive the identical once-prepared name and body despite settings changes");
        check(notices.Any(x => x.Kind == "受信" && x.Text.Contains(message.Text, StringComparison.Ordinal)) &&
            notices.Any(x => x.Kind == "読み上げ" && x.Text == spoken),
            "receive display keeps original chat while speech notice exposes only the prepared utterance");
        check(!queue.Enqueue(message with { Transport = "EventSub" }),
            "cross-transport duplicate remains rejected after a settings change and fallback");
    }

    private static async Task ArrivalSnapshotAsync(Action<bool, string> check)
    {
        var started = Signal();
        var release = Signal();
        var output = new Probe(async (text, ct) =>
        {
            if (text == "hold") { started.TrySetResult(); await release.Task.WaitAsync(ct); }
        });
        await using var queue = new SpeechQueue(output)
        {
            TextSettings = new(NameReadingMode.EveryMessage, [new("Twitch", "42", "旧よみ")], [new("word", "旧本文")])
        };
        queue.Enqueue(ChatMessage.Manual("hold"));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        check(queue.Enqueue(Message("old", "42", "word https://example.test/a")), "message arrival captures the old text settings");
        queue.TextSettings = new(NameReadingMode.EveryMessage, [new("Twitch", "42", "新よみ")], [new("word", "新本文")], omitUrls: true);
        check(queue.Enqueue(Message("new", "42", "word https://example.test/a")), "later arrival uses the replacement text snapshot");
        queue.TextSettings = new(NameReadingMode.EveryMessage, mutedAuthors: [new("Twitch", "42"), new("手入力", "manual-id")]);
        check(queue.Enqueue(ChatMessage.Manual("手動") with { Author = "付けない名前", AuthorId = "manual-id" }),
            "queue manual input bypasses author naming and author-ID mute rules");
        release.TrySetResult();
        var spoken = new[] { await output.NextAsync(), await output.NextAsync(), await output.NextAsync(), await output.NextAsync() };
        check(spoken.SequenceEqual(new[] { "hold", "旧よみ、旧本文 リンク", "新よみ、新本文", "手動" }),
            "waiting utterances keep arrival-time text and are not retroactively muted or rewritten");
    }

    private static ChatMessage Message(string id, string authorId, string text, string author = "表示") =>
        new("Twitch", "channel", id, author, text, "IRC", Stopwatch.GetTimestamp()) { AuthorId = authorId };

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class Clock : TimeProvider
    {
        private long timestamp;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Interlocked.Read(ref timestamp);
        public void Advance(TimeSpan elapsed) => Interlocked.Add(ref timestamp, elapsed.Ticks);
    }

    private sealed class Probe(Func<string, CancellationToken, Task>? behavior = null) : ISpeechOutput
    {
        private readonly Channel<string> completed = Channel.CreateUnbounded<string>();
        public ConcurrentQueue<string> Attempts { get; } = new();
        public string Name => "queue text test";
        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            Attempts.Enqueue(text);
            if (behavior is not null) await behavior(text, ct);
            completed.Writer.TryWrite(text);
        }
        public Task<string> NextAsync() => completed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3));
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
