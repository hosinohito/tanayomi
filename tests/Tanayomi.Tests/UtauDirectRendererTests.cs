using Tanayomi;

internal static class UtauDirectRendererTests
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(3);

    public static async Task RunAsync(Action<bool, string> check)
    {
        // Deliberately no OTO aliases or WAV files: a complete renderer owns
        // pronunciation/source selection and must precede the ordinary renderer.
        var bank = new Voicebank("direct-only", "unused-direct-bank", Array.Empty<OtoEntry>(), 0);
        var pcm = new PcmAudio([17, -29, 43, -71], 44100);
        var renderer = new Renderer { Handle = (_, _, _) => Task.FromResult(pcm) };
        var output = new Output();
        var reports = 0; var fallbacks = 0;
        await using (var voice = new UtauSpeech(output, renderer) { Voicebank = bank })
        {
            voice.SynthesizedMilliseconds += _ => throw new IOException("subscriber failure");
            voice.SynthesizedMilliseconds += elapsed => { if (double.IsFinite(elapsed) && elapsed >= 0) reports++; };
            voice.ProsodyFallback += _ => fallbacks++;
            var actual = await voice.SynthesizeAsync("通常経路にない読み");
            check(ReferenceEquals(actual, pcm) && ReferenceEquals(renderer.Bank, bank) &&
                renderer.Text == "通常経路にない読み" && renderer.FlatCalls == 0 && reports == 1 && output.Calls == 0,
                "complete direct PCM bypasses missing aliases and ordinary processing unchanged and reports synthesis once");
            await voice.SpeakAsync("あ", default);
            check(output.Calls == 1 && output.Last!.Samples.SequenceEqual(pcm.Samples) && reports == 2 && fallbacks == 0,
                "only the validated direct waveform reaches playback without a postprocess fallback");
        }
        check(renderer.Disposes == 1 && output.Disposes == 1,
            "direct renderer retains processor and playback ownership on disposal");
        await CheckInvalidAsync(bank, pcm, check);
        await CheckCancellationAndSnapshotAsync(bank, pcm, check);
    }

    private static async Task CheckInvalidAsync(Voicebank bank, PcmAudio valid, Action<bool, string> check)
    {
        var invalid = new PcmAudio?[] { null, new(null!, 44100), new([1], 22050), new([], 44100),
            new(new short[4], 44100), new(new short[44100 * 45 + 1], 44100) };
        foreach (var bad in invalid)
        {
            var renderer = new Renderer { Handle = (_, _, _) => Task.FromResult(bad!) };
            var output = new Output();
            await using var voice = new UtauSpeech(output, renderer) { Voicebank = bank };
            var measured = 0; var fallbacks = 0;
            voice.SynthesizedMilliseconds += _ => measured++;
            voice.ProsodyFallback += _ => fallbacks++;
            var error = await CaptureAsync(() => voice.SpeakAsync("あ", default));
            check(error is InvalidDataException && output.Calls == 0 && renderer.FlatCalls == 0 && measured == 0 && fallbacks == 0,
                "null malformed empty silent or oversized direct PCM is rejected without playback or an ordinary fallback");
            renderer.Handle = (_, _, _) => Task.FromResult(valid);
            check(ReferenceEquals(await voice.SynthesizeAsync("い"), valid) && measured == 1,
                "a rejected direct waveform releases synthesis state for the next utterance");
        }
        var failing = new Renderer { Handle = (_, _, _) => throw new IOException("external renderer failed") };
        await using var failedVoice = new UtauSpeech(new Output(), failing) { Voicebank = bank };
        check(await CaptureAsync(() => failedVoice.SynthesizeAsync("あ")) is IOException && failing.FlatCalls == 0,
            "external rendering failure propagates without reusing the ordinary PCM processor");
        // The documented upper bound is inclusive, and one nonzero sample is
        // enough to distinguish a waveform from wholly silent output.
        var maximum = new short[44100 * 45]; maximum[^1] = 1;
        failing.Handle = (_, _, _) => Task.FromResult(new PcmAudio(maximum, 44100));
        check(ReferenceEquals((await failedVoice.SynthesizeAsync("い")).Samples, maximum),
            "the inclusive 45-second direct waveform bound is accepted without truncation");
    }

    private static async Task CheckCancellationAndSnapshotAsync(Voicebank bank, PcmAudio pcm, Action<bool, string> check)
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderer = new Renderer { Handle = async (_, _, ct) => { entered.SetResult(); await resume.Task.WaitAsync(ct); return pcm; } };
        var output = new Output();
        await using var voice = new UtauSpeech(output, renderer) { Voicebank = bank };
        var pending = voice.SynthesizeAsync("あ");
        await entered.Task.WaitAsync(Limit);
        check(!voice.DrawnOutMode && !renderer.DrawnOutMode, "drawn-out mode defaults off for the first utterance");
        voice.DrawnOutMode = true;
        voice.Mode = UtauSynthesisMode.Simple;
        voice.Voicebank = new("changed", "unused-changed-bank", Array.Empty<OtoEntry>(), 0);
        resume.SetResult();
        check(ReferenceEquals(await pending.WaitAsync(Limit), pcm) && ReferenceEquals(renderer.Bank, bank) && renderer.Calls == 1,
            "active direct rendering retains the selected mode and voicebank snapshots during UI changes");
        check(!renderer.DrawnOutMode, "changing drawn-out mode does not mutate the active renderer request");
        await CaptureAsync(() => voice.SynthesizeAsync("あ"));
        check(renderer.Calls == 1, "a new simple-mode utterance no longer invokes the direct renderer");
        voice.Mode = UtauSynthesisMode.Context; voice.Voicebank = bank;
        renderer.Handle = (_, _, _) => Task.FromResult(pcm);
        await voice.SynthesizeAsync("い");
        check(renderer.DrawnOutMode, "the next utterance receives the changed drawn-out mode snapshot");
        using var canceled = new CancellationTokenSource();
        renderer.Handle = (_, _, _) => { canceled.Cancel(); throw new IOException("failure during cancellation"); };
        check(await CaptureAsync(() => voice.SynthesizeAsync("あ", canceled.Token)) is OperationCanceledException && renderer.FlatCalls == 0 && output.Calls == 0,
            "cancellation takes priority over concurrent direct-renderer failure and cannot start fallback processing");
        using var late = new CancellationTokenSource();
        renderer.Handle = (_, _, _) => { late.Cancel(); return Task.FromResult(pcm); };
        check(await CaptureAsync(() => voice.SpeakAsync("あ", late.Token)) is OperationCanceledException && output.Calls == 0,
            "a renderer returning PCM after cancellation cannot reach playback");
        var calls = renderer.Calls;
        check(await CaptureAsync(() => voice.SynthesizeAsync("あ", late.Token)) is OperationCanceledException && renderer.Calls == calls,
            "a pre-cancelled utterance never enters the direct renderer");
        renderer.Handle = (_, _, _) => Task.FromResult(pcm);
        check(ReferenceEquals(await voice.SynthesizeAsync("い"), pcm) && renderer.FlatCalls == 0,
            "cancellation leaves the direct synthesis gate usable by the next utterance");
    }

    private static async Task<Exception?> CaptureAsync(Func<Task> action)
    {
        try { await action().WaitAsync(Limit); return null; }
        catch (Exception error) { return error; }
    }

    private sealed class Renderer : IUtauPcmPostprocessor, IUtauDirectRenderer
    {
        public Func<string, Voicebank, CancellationToken, Task<PcmAudio>> Handle { get; set; } = null!;
        public int Calls { get; private set; }
        public int FlatCalls { get; private set; }
        public int Disposes { get; private set; }
        public string? Text { get; private set; }
        public Voicebank? Bank { get; private set; }
        public bool DrawnOutMode { get; private set; }
        public Task<PcmAudio> RenderAsync(string text, Voicebank bank, bool drawnOutMode, CancellationToken cancellationToken)
        { DrawnOutMode = drawnOutMode; return RenderAsync(text, bank, cancellationToken); }
        public Task<PcmAudio> RenderAsync(string text, Voicebank bank, CancellationToken cancellationToken)
        { Calls++; Text = text; Bank = bank; return Handle(text, bank, cancellationToken); }
        public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken cancellationToken)
        { FlatCalls++; throw new InvalidOperationException("complete renderer must not postprocess"); }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }

    private sealed class Output : IPcmOutput
    {
        public int Calls { get; private set; }
        public int Disposes { get; private set; }
        public PcmAudio? Last { get; private set; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Last = new(samples.ToArray(), sampleRate); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { Disposes++; return ValueTask.CompletedTask; }
    }
}
