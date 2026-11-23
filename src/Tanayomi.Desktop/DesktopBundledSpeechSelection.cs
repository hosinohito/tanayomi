namespace Tanayomi.Desktop;

// A single saved voice list; each native/Python host stays in its own process.
internal sealed class DesktopBundledSpeechSelection : ISpeechOutput, ISpeechPreparation, IOpenJtalkVoiceSelection
{
    internal sealed record Voice(OpenJtalkVoiceChoice Choice, ISpeechOutput Output);
    private readonly Voice[] voices;
    private readonly IAsyncDisposable[] owners;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly CancellationToken lifetimeToken;
    private readonly object disposalGate = new();
    private Voice selected;
    private Task? disposal;
    private int disposed;

    internal DesktopBundledSpeechSelection(IEnumerable<Voice> voices, IEnumerable<IAsyncDisposable> owners,
        IEnumerable<Exception>? initializationErrors = null)
    {
        this.voices = voices.ToArray();
        if (this.voices.Length == 0 || this.voices.Select(x => x.Choice.Id).Distinct(StringComparer.Ordinal).Count() != this.voices.Length)
            throw new ArgumentException("同梱音声の構成が不正です。", nameof(voices));
        this.owners = owners.Distinct(ReferenceEqualityComparer.Instance).Cast<IAsyncDisposable>().ToArray();
        selected = this.voices[0];
        Voices = Array.AsReadOnly(this.voices.Select(x => x.Choice).ToArray());
        InitializationErrors = Array.AsReadOnly((initializationErrors ?? []).Take(8).ToArray());
        lifetimeToken = lifetime.Token;
    }

    public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; }
    public IReadOnlyList<Exception> InitializationErrors { get; }
    public string Name => Volatile.Read(ref selected).Choice.Name;
    public string VoiceId
    {
        get => Volatile.Read(ref selected).Choice.Id;
        set => Volatile.Write(ref selected, voices.FirstOrDefault(x => x.Choice.Id == value) ?? voices[0]);
    }

    public Task SpeakAsync(string text, CancellationToken ct) => ProcessAsync(Volatile.Read(ref selected), text, ct);
    public Task PrepareAsync(CancellationToken ct) => ProcessAsync(Volatile.Read(ref selected), null, ct);

    private async Task ProcessAsync(Voice voice, string? text, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, lifetimeToken);
        await serial.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            if (voice.Output is IOpenJtalkVoiceSelection selector) selector.VoiceId = voice.Choice.Id;
            if (text is not null) await voice.Output.SpeakAsync(text, linked.Token).ConfigureAwait(false);
            else if (voice.Output is ISpeechPreparation preparation)
                await preparation.PrepareAsync(linked.Token).ConfigureAwait(false);
            linked.Token.ThrowIfCancellationRequested();
        }
        finally { serial.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (disposalGate)
        {
            if (disposal is null)
            {
                Volatile.Write(ref disposed, 1);
                disposal = Task.Run(DisposeCoreAsync);
            }
            return new(disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        try { lifetime.Cancel(); }
        finally
        {
            // Dispose every owner even if one throws synchronously. Owners also
            // cancel their transports; waiting callers cannot start another one.
            var all = Task.WhenAll(owners.Select(async owner => await owner.DisposeAsync().ConfigureAwait(false)));
            try { await all.ConfigureAwait(false); }
            finally { lifetime.Dispose(); }
        }
    }
}
