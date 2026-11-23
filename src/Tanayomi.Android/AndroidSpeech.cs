using Android.Speech.Tts;
using Avalonia.Threading;

namespace Tanayomi.Android;

public sealed class AndroidSpeech : Java.Lang.Object, ISpeechOutput, ISpeechVolumeControl, TextToSpeech.IOnInitListener
{
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TextToSpeech engine;
    private readonly Progress progress = new();
    public PlaybackVolume Volume { get; set; } = new();
    public string Name => "Android標準音声";
    public AndroidSpeech()
    {
        engine = new TextToSpeech(global::Android.App.Application.Context, this);
        engine.SetOnUtteranceProgressListener(progress);
    }
    public void OnInit(OperationResult status)
    {
        if (status == OperationResult.Success) ready.TrySetResult();
        else ready.TrySetException(new IOException("音声エンジンを初期化できません。"));
    }
    public async Task SpeakAsync(string text, CancellationToken cancellationToken)
    {
        await ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var request = new UtteranceRequest(Guid.NewGuid().ToString("N"), completion);
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percent = Volume.Percent;
                if (percent == 0) { completion.TrySetResult(); return; }
                if ((int)engine.SetLanguage(Java.Util.Locale.Japanese) < 0) throw new IOException("日本語音声がありません。");
                using var parameters = new global::Android.OS.Bundle();
                parameters.PutFloat(TextToSpeech.Engine.KeyParamVolume, percent / 100f);
                progress.Set(request);
                if (engine.Speak(text, QueueMode.Flush, parameters, request.Id) != OperationResult.Success) throw new IOException("音声を開始できません。");
            });
            await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
        }
        finally
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Clear before Stop, which may synchronously deliver a callback.
                // A delayed cancellation must not stop or clear a newer utterance.
                if (progress.TryClear(request)) engine.Stop();
            });
        }
    }
    public async ValueTask DisposeAsync() => await Dispatcher.UIThread.InvokeAsync(() =>
    {
        try { engine.Stop(); }
        finally
        {
            try { engine.Shutdown(); }
            finally
            {
                try { engine.Dispose(); }
                finally
                {
                    try { progress.Dispose(); }
                    finally { Dispose(); }
                }
            }
        }
    });

    private sealed record UtteranceRequest(string Id, TaskCompletionSource Completion);

    private sealed class Progress : UtteranceProgressListener
    {
        private UtteranceRequest? current;
        public void Set(UtteranceRequest request) => Volatile.Write(ref current, request);
        public bool TryClear(UtteranceRequest request) => ReferenceEquals(Interlocked.CompareExchange(ref current, null, request), request);
        public override void OnStart(string? utteranceId) { }
        public override void OnDone(string? utteranceId)
        {
            var request = Volatile.Read(ref current);
            if (request is not null && request.Id == utteranceId) request.Completion.TrySetResult();
        }
        public override void OnError(string? utteranceId)
        {
            var request = Volatile.Read(ref current);
            if (request is not null && request.Id == utteranceId) request.Completion.TrySetException(new IOException("音声処理に失敗しました。"));
        }
    }
}
