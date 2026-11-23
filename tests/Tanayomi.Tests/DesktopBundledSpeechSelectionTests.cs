using Tanayomi;
using Tanayomi.Desktop;

internal static class DesktopBundledSpeechSelectionTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        var host = new Probe();
        var error = new IOException("fixture");
        var entries = host.Voices.Select(x => new DesktopBundledSpeechSelection.Voice(x, host));
        await using (var selection = new DesktopBundledSpeechSelection(entries, [host, host], [error]))
        {
            check(selection.Voices.Count == 2 && selection.VoiceId == "nit-atr503" &&
                ReferenceEquals(selection.InitializationErrors.Single(), error),
                "bundled catalog keeps default, credits and recoverable initialization failure");
            var first = selection.SpeakAsync("first", default);
            await host.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            selection.VoiceId = "voicevox-3";
            var second = selection.SpeakAsync("second", default);
            selection.VoiceId = "nit-atr503";
            check(host.VoiceId == "nit-atr503" && host.Calls.Count == 1,
                "changing bundled voice cannot mutate the current utterance or start concurrent native work");
            host.Release.TrySetResult();
            await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(2));
            check(host.Calls.SequenceEqual(new[] { "nit-atr503:first", "voicevox-3:second" }),
                "queued bundled call keeps the voice captured when it starts waiting");
            selection.VoiceId = "voicevox-3";
            await selection.PrepareAsync(default);
            check(host.Prepared == "voicevox-3", "bundled preparation targets the selected model without playback");
            selection.VoiceId = "unavailable";
            check(selection.VoiceId == "nit-atr503", "unavailable saved bundled voice falls back to installed default");
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { await selection.SpeakAsync("canceled", canceled.Token); check(false, "cancellation required"); }
            catch (OperationCanceledException) { check(host.Calls.Count == 2, "canceled bundled call never reaches its host"); }
        }
        check(host.Disposals == 1, "bundled catalog disposes a shared host exactly once");

        var failed = new Probe { FailDispose = true };
        var remaining = new Probe();
        var failing = new DesktopBundledSpeechSelection(
            failed.Voices.Select(x => new DesktopBundledSpeechSelection.Voice(x, failed)), [failed, remaining]);
        try { await failing.DisposeAsync(); check(false, "disposal failure required"); }
        catch (IOException) { check(failed.Disposals == 1 && remaining.Disposals == 1,
            "one synchronous disposal failure does not skip the other bundled owners"); }
        try { await failing.PrepareAsync(default); check(false, "disposed host rejected"); }
        catch (ObjectDisposedException) { check(true, "disposed bundled catalog cannot restart a host"); }
    }

    private sealed class Probe : ISpeechOutput, ISpeechPreparation, IOpenJtalkVoiceSelection
    {
        public IReadOnlyList<OpenJtalkVoiceChoice> Voices { get; } = [new("nit-atr503", "NIT"), new("voicevox-3", "VOICEVOX", "credit")];
        public string VoiceId { get; set; } = "nit-atr503";
        public string Name => VoiceId;
        public string Prepared = "";
        public List<string> Calls { get; } = [];
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Disposals;
        public bool FailDispose;
        public async Task SpeakAsync(string text, CancellationToken ct)
        {
            Calls.Add(VoiceId + ":" + text);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
        }
        public Task PrepareAsync(CancellationToken ct) { ct.ThrowIfCancellationRequested(); Prepared = VoiceId; return Task.CompletedTask; }
        public ValueTask DisposeAsync()
        {
            Disposals++;
            if (FailDispose) throw new IOException("fixture");
            return ValueTask.CompletedTask;
        }
    }
}
