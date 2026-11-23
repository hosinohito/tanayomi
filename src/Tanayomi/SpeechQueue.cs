using System.Diagnostics;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace Tanayomi;

public sealed record ChatMessage(string Platform, string Channel, string Id, string Author,
    string Text, string Transport, long ReceivedAt)
{
    public string AuthorId { get; init; } = "";
    public static ChatMessage Manual(string text) =>
        new("手入力", "local", Guid.NewGuid().ToString("N"), "", text, "local", Stopwatch.GetTimestamp());
}

public sealed record AppNotice(string Kind, string Text, double? Milliseconds = null, DiagnosticEvent? Diagnostic = null);
public sealed record PlatformCapabilities(bool DirectoryVoicebanks, bool BackgroundListening, bool ProcessIsolation);
internal interface ISpeechPreparation
{
    Task PrepareAsync(CancellationToken cancellationToken);
}

public sealed record OpenJtalkVoiceChoice(string Id, string Name, string Credit = "", string Usage = "", string TermsUrl = "",
    string AcceptanceId = "", string TermsText = "")
{
    public override string ToString() => Name;
}

public interface IOpenJtalkVoiceSelection
{
    IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; }
    IReadOnlyList<Exception> InitializationErrors => [];
    string VoiceId { get; set; }
}

public interface ISpeechOutput : IAsyncDisposable
{
    string Name { get; }
    bool CanFallback => true;
    Task SpeakAsync(string text, CancellationToken cancellationToken);
}

/// <summary>The receiver never waits for audio. One consumer owns playback order.</summary>
public sealed class SpeechQueue : IAsyncDisposable
{
    private sealed record PendingSpeech(ChatMessage Message, SpeechTextResult Prepared, long Generation);
    private SpeechText textProcessor = new(SpeechTextSettings.Default);
    public SpeechTextSettings TextSettings
    {
        set
        {
            var next = new SpeechText(value);
            lock (gate)
            {
                Volatile.Write(ref textProcessor, next);
                repeated.Clear(); repeatedOrder.Clear();
            }
        }
    }
    private readonly Channel<PendingSpeech> pending;
    private readonly ISpeechOutput primary;
    private readonly ISpeechOutput? fallback;
    private readonly CancellationTokenSource lifetime = new();
    private readonly object gate = new();
    private readonly HashSet<string> seen = new();
    private readonly Queue<string> seenOrder = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, long Accepted)>> repeated = new();
    private readonly LinkedList<(string Key, long Accepted)> repeatedOrder = new();
    private readonly TimeProvider timeProvider;
    private const int MaximumRepeatedEntries = 512;
    private static readonly TimeSpan RepeatedWindow = TimeSpan.FromSeconds(5);
    private readonly Task consumer;
    private CancellationTokenSource? current;
    private bool disposed;
    private bool suspended;
    private long generation;
    private int droppedNotices;
    public event Action<AppNotice>? Notice;
    public event Action? ActivityChanged;
    public TimeSpan MaximumAge { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan SpeechTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int PendingCount => pending.Reader.Count;
    public bool IsBusy
    {
        get { lock (gate) return !disposed && !suspended && (current is { IsCancellationRequested: false } || pending.Reader.Count > 0); }
    }

    public SpeechQueue(ISpeechOutput primary, ISpeechOutput? fallback = null, int capacity = 32, TimeProvider? timeProvider = null)
    {
        this.primary = primary;
        this.fallback = fallback;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        pending = Channel.CreateBounded<PendingSpeech>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = false,
            AllowSynchronousContinuations = false
        }, _ => ++droppedNotices);
        consumer = ConsumeAsync();
    }

    public bool Enqueue(ChatMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Text)) return false;
        var processor = Volatile.Read(ref textProcessor);
        SpeechTextResult prepared;
        try
        {
            prepared = processor.Prepare(message.Platform, message.AuthorId,
                message.Author, message.Text, message.Platform == "手入力");
        }
        catch (Exception error) when (error is ArgumentException or RegexMatchTimeoutException)
        {
            Publish(new("省略", "この発言の文字処理を完了できなかったため、省略しました。", Diagnostic:
                DiagnosticEvent.Create(DiagnosticCode.UiOperationFailed, DiagnosticOperation.TextSettings,
                    DiagnosticRecovery.RetryOperation, error)));
            return false;
        }
        if (prepared.IsMuted || string.IsNullOrWhiteSpace(prepared.Text)) return false;
        var repeatKey = processor.SuppressRepeatedMessages && message.Platform != "手入力" && message.Transport != "local" &&
            SpeechText.TryIdentity(message.Platform, message.AuthorId, out var identity)
            ? RepeatedKey(identity.Platform, message.Channel, identity.AuthorId, message.Text) : null;
        bool accepted;
        int dropped;
        lock (gate)
        {
            if (disposed || suspended) return false;
            // Transport is deliberately excluded: IRC and EventSub share the same chat ID.
            var key = $"{message.Platform}\0{message.Channel}\0{message.Id}";
            if (seen.Contains(key)) return false;
            // Preserve arrival-time text, but never seed a newer settings epoch
            // with a message prepared using the previous immutable snapshot.
            if (!ReferenceEquals(processor, textProcessor)) repeatKey = null;
            var now = repeatKey is null ? 0 : timeProvider.GetTimestamp();
            if (repeatKey is not null && repeated.TryGetValue(repeatKey, out var previous))
            {
                var elapsed = timeProvider.GetElapsedTime(previous.Value.Accepted, now);
                if (elapsed >= TimeSpan.Zero && elapsed < RepeatedWindow) return false;
            }
            // Reserve the transport ID before writing; roll back rejected writes.
            seen.Add(key);
            accepted = pending.Writer.TryWrite(new(message, prepared, generation));
            if (!accepted) seen.Remove(key);
            if (accepted)
            {
                seenOrder.Enqueue(key);
                if (seenOrder.Count > 2048) seen.Remove(seenOrder.Dequeue());
                if (repeatKey is not null && !disposed && ReferenceEquals(processor, textProcessor))
                {
                    if (repeated.Remove(repeatKey, out var old)) repeatedOrder.Remove(old);
                    if (repeated.Count == MaximumRepeatedEntries)
                    {
                        repeated.Remove(repeatedOrder.First!.Value.Key);
                        repeatedOrder.RemoveFirst();
                    }
                    repeated.Add(repeatKey, repeatedOrder.AddLast((repeatKey, now)));
                }
            }
            // Channel's drop callback runs inside TryWrite. Notify observers only
            // after both ID and repeat histories reflect the accepted message.
            dropped = droppedNotices;
            droppedNotices = 0;
        }
        for (var i = 0; i < dropped; ++i)
            Publish(new("省略", "待ち行列の上限に達したため、古い発言を省略しました。"));
        if (accepted)
        {
            PublishActivity();
            Publish(new("受信", $"[{message.Transport}] {message.Author}: {message.Text}"));
        }
        return accepted;
    }

    private static string RepeatedKey(string platform, string channel, string authorId, string text)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendHash(hash, platform); AppendHash(hash, channel); AppendHash(hash, authorId); AppendHash(hash, text);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AppendHash(IncrementalHash hash, string value)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(length, value.Length);
        hash.AppendData(length);
        // Hash exact UTF16 code units, including distinct malformed sequences;
        // normalization or UTF8 replacement must not conflate original messages.
        hash.AppendData(MemoryMarshal.AsBytes(value.AsSpan()));
    }


    public void Skip()
    {
        lock (gate) current?.Cancel();
        PublishActivity();
    }

    public void Clear()
    {
        lock (gate) ClearCore();
        PublishActivity();
        Publish(new("停止", "読み上げと待ち行列を停止しました。"));
    }

    public void SetSuspended(bool value)
    {
        lock (gate)
        {
            suspended = value;
            if (value) ClearCore();
        }
        PublishActivity();
    }
    private void ClearCore()
    {
        ++generation;
        while (pending.Reader.TryRead(out _)) { }
        current?.Cancel();
    }

    public static string PrepareText(string text)
    {
        return SpeechText.Truncate(SpeechText.NormalizeText(text));
    }

    private async Task ConsumeAsync()
    {
        (string Platform, string Channel, string Author, long Generation)? previous = null;
        try
        {
            while (await pending.Reader.WaitToReadAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var item = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                PendingSpeech pendingSpeech;
                bool accept;
                lock (gate)
                {
                    if (!pending.Reader.TryRead(out pendingSpeech!)) continue;
                    accept = !disposed && !suspended && pendingSpeech.Generation == generation;
                    // Dequeue and register current atomically so IsBusy never sees a false
                    // idle gap between the last queued item and its synthesis/playback.
                    if (accept) current = item;
                }
                PublishActivity();
                if (!accept) continue;
                var message = pendingSpeech.Message;
                var age = Stopwatch.GetElapsedTime(message.ReceivedAt);
                var started = Stopwatch.GetTimestamp();
                item.CancelAfter(SpeechTimeout);
                try
                {
                    if (age > MaximumAge)
                    {
                        Publish(new("省略", "古くなった発言を省略しました。"));
                        continue;
                    }
                    (string Platform, string Channel, string Author, long Generation)? speaker =
                        message.Platform != "手入力" && SpeechText.TryIdentity(message.Platform, message.AuthorId, out var id)
                            ? (id.Platform, message.Channel, id.AuthorId, pendingSpeech.Generation) : null;
                    var prepared = pendingSpeech.Prepared;
                    var text = speaker != null && speaker == previous ? prepared.SameSpeakerText ?? prepared.Text : prepared.Text;
                    previous = null;
                    if (text.Length == 0) continue;
                    // This measures dispatch, not physical sound onset.
                    Publish(new("読み上げ", text, age.TotalMilliseconds));
                    try { await primary.SpeakAsync(text, item.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (item.IsCancellationRequested) { throw; }
                    catch (Exception error) when (fallback is not null && primary.CanFallback)
                    {
                        Publish(new("警告", "選択した音声が使えないため、標準音声へ切り替えます。", Diagnostic:
                            DiagnosticEvent.Create(DiagnosticCode.SpeechFallback, DiagnosticOperation.SpeechPrimary,
                                DiagnosticRecovery.StandardVoice, error)));
                        await fallback.SpeakAsync(text, item.Token).ConfigureAwait(false);
                    }
                    if (!item.IsCancellationRequested && prepared.SameSpeakerText != null) previous = speaker;
                    Publish(new("完了", "読み上げが完了しました。"));
                }
                catch (OperationCanceledException) when (item.IsCancellationRequested)
                {
                    var diagnostic = !lifetime.IsCancellationRequested && Stopwatch.GetElapsedTime(started) >= SpeechTimeout
                        ? DiagnosticEvent.Create(DiagnosticCode.SpeechTimedOut, DiagnosticOperation.SpeechProcessing,
                            DiagnosticRecovery.NextUtterance, new TimeoutException()) : null;
                    Publish(new("省略", "停止要求または時間切れのため、発言をスキップしました。", Diagnostic: diagnostic));
                }
                catch (Exception ex)
                {
                    // Do not expose credentials, input text, or paths embedded in exception messages.
                    Publish(new("エラー", $"音声処理に失敗しました（{ex.GetType().Name}）。次の発言を続けます。", Diagnostic:
                        DiagnosticEvent.Create(DiagnosticCode.SpeechFailed,
                            fallback is not null && primary.CanFallback ? DiagnosticOperation.SpeechFallback : DiagnosticOperation.SpeechPrimary,
                            DiagnosticRecovery.NextUtterance, ex)));
                }
                finally
                {
                    lock (gate) current = null;
                    PublishActivity();
                }
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private void PublishActivity()
    {
        // Observers may take the lifecycle lock and read IsBusy. Never invoke
        // them under gate; queued UI work must read the latest state, not an old bool.
        if (ActivityChanged is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { /* A view must not stop audio. */ }
    }

    private void Publish(AppNotice notice)
    {
        if (Notice is not { } handlers) return;
        foreach (Action<AppNotice> handler in handlers.GetInvocationList())
            try { handler(notice); } catch (Exception) { /* A view must not stop audio. */ }
    }

    public async ValueTask DisposeAsync()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            repeated.Clear(); repeatedOrder.Clear();
        }
        PublishActivity();
        try
        {
            Exception? cancellationError = null;
            try { lifetime.Cancel(); }
            catch (Exception error) { cancellationError = error; }
            finally { pending.Writer.TryComplete(); }
            await consumer.ConfigureAwait(false);
            if (cancellationError is not null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cancellationError).Throw();
        }
        finally
        {
            try { await primary.DisposeAsync().ConfigureAwait(false); }
            finally
            {
                try
                {
                    if (fallback is not null && !ReferenceEquals(primary, fallback))
                        await fallback.DisposeAsync().ConfigureAwait(false);
                }
                finally { lifetime.Dispose(); }
            }
        }
    }
}
