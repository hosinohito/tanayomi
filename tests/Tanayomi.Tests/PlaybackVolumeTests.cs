using Tanayomi;

internal static class PlaybackVolumeTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        CheckAllAttenuationValues(check);        foreach (var gain in new[] { 101, 200, 400 })
        {
            var pcm = Enumerable.Range(short.MinValue, 65536).Select(x => (short)x).ToArray();
            var amplified = new PlaybackVolume { Percent = gain }.Apply(pcm);
            check(amplified.Samples.Span.ToArray().Select((value, index) =>
                value == Math.Clamp(Math.Round((index + short.MinValue) * gain / 100.0, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue)
                && pcm[index] == index + short.MinValue).All(x => x),
                $"{gain}% gain amplifies all PCM16 values with saturation, symmetric rounding and unchanged input");
        }
        var volume = new PlaybackVolume();
        short[] original = [short.MinValue, -3, -1, 0, 1, 3, short.MaxValue];
        var before = original.ToArray();
        ReadOnlyMemory<short> source = original.AsMemory(1, 5);
        var unchanged = volume.Apply(source);
        check(volume.Percent == 100 && !unchanged.IsMuted && unchanged.Samples.Equals(source),
            "default volume preserves the exact original PCM memory including its slice");

        volume.Percent = 50;
        var half = volume.Apply(original);
        check(!half.IsMuted && half.Samples.Span.SequenceEqual(new short[] { -16384, -2, -1, 0, 1, 2, 16384 }),
            "attenuation rounds positive and negative half-samples symmetrically without PCM16 overflow");
        check(original.SequenceEqual(before), "attenuation leaves synthesis/cache PCM unchanged");
        original[1] = 31;
        check(half.Samples.Span[1] == -2, "attenuated output does not alias the synthesis/cache buffer");
        original[1] = before[1];
        foreach (var invalid in new[] { -1, 401, int.MinValue, int.MaxValue })
        {
            Exception? error = null;
            try { volume.Percent = invalid; } catch (Exception caught) { error = caught; }
            check(error is ArgumentOutOfRangeException && volume.Percent == 50,
                "invalid volume is rejected without changing the last valid value");
        }

        var output = new RecordingOutput();
        var adapter = new VolumePcmOutput(output, volume);
        await adapter.PlayAsync(original, 44100, default);
        check(output.Calls == 1 && output.Samples.Span.SequenceEqual(half.Samples.Span) && output.Rate == 44100,
            "volume adapter sends attenuated PCM and preserves the sample rate");
        volume.Percent = 100;
        await adapter.PlayAsync(source, 22050, default);
        check(output.Calls == 2 && output.Samples.Equals(source), "100 percent adapter preserves exact PCM memory");
        volume.Percent = 0;
        var muted = volume.Apply(original);
        await adapter.PlayAsync(original, 44100, default);
        check(muted.IsMuted && muted.Samples.IsEmpty && output.Calls == 2,
            "mute is explicit successful suppression and never calls native playback");
        Exception? invalidPcm = null;
        try { await adapter.PlayAsync(original, 0, default); } catch (Exception error) { invalidPcm = error; }
        check(invalidPcm is ArgumentOutOfRangeException && output.Calls == 2,
            "mute does not turn invalid PCM format into successful playback");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Exception? cancelled = null;
        try { await adapter.PlayAsync(original, 44100, cancellation.Token); }
        catch (Exception error) { cancelled = error; }
        check(cancelled is OperationCanceledException && output.Calls == 2,
            "stop takes priority over muted successful suppression");
        cancelled = null;
        try { volume.Apply(original, cancellation.Token); } catch (Exception error) { cancelled = error; }
        check(cancelled is OperationCanceledException, "PCM transformation observes stop before its mute fast path");
        volume.Percent = 100;
        await adapter.PlayAsync(original, 44100, default);
        check(output.Calls == 3 && output.Samples.Span.SequenceEqual(before),
            "unmuting plays the unmodified original PCM without cumulative attenuation");
        var nativeFailure = new IOException("test output unavailable");
        output.Failure = nativeFailure;
        Exception? propagated = null;
        try { await adapter.PlayAsync(original, 44100, default); } catch (Exception error) { propagated = error; }
        check(ReferenceEquals(propagated, nativeFailure),
            "unmuted output failure remains observable for the existing fallback policy");
        output.Failure = null;
        await adapter.DisposeAsync();
        await adapter.DisposeAsync();
        check(output.Disposals == 0, "disposing a borrowed volume adapter never disposes the shared native output");
        Exception? disposed = null;
        try { await adapter.PlayAsync(original, 44100, default); } catch (Exception error) { disposed = error; }
        check(disposed is ObjectDisposedException && output.Calls == 3, "disposed volume adapter rejects new native playback");
        await output.PlayAsync(original, 44100, default);
        await output.DisposeAsync();
        check(output.Calls == 4 && output.Disposals == 1, "the actual output owner remains able to play and dispose exactly once");
        var ownedRaw = new RecordingOutput();
        var owned = new VolumePcmOutput(ownedRaw, new PlaybackVolume(), ownsOutput: true);
        await owned.DisposeAsync();
        await owned.DisposeAsync();
        check(ownedRaw.Disposals == 1, "an owning volume adapter disposes its raw output exactly once across repeated disposal");
        await FactorySharedVolume(check);
    }

    private static void CheckAllAttenuationValues(Action<bool, string> check)
    {
        var input = Enumerable.Range(short.MinValue, 65536).Select(value => (short)value).ToArray();
        var volume = new PlaybackVolume();
        for (var percent = 1; percent < 100; percent++)
        {
            volume.Percent = percent;
            var actual = volume.Apply(input);
            var same = !actual.IsMuted && actual.Samples.Length == input.Length;
            for (var i = 0; i < input.Length && same; i++)
            {
                var original = short.MinValue + i;
                var expected = (short)Math.Round(original * percent / 100.0, MidpointRounding.AwayFromZero);
                same &= actual.Samples.Span[i] == expected && input[i] == original;
            }
            check(same, $"volume {percent}% preserves original rounding for all PCM16 values and leaves input intact");
        }
    }

    private static async Task FactorySharedVolume(Action<bool, string> check)
    {
        var oldSpeech = App.CreateSpeech; var oldPcm = App.CreatePcm;
        var oldCredentials = App.CreateCredentialStore; var oldPostprocessor = App.CreateUtauPostprocessor;
        var oldOpenJtalk = App.CreateOpenJtalkSpeech;
        var raw = new RecordingOutput(); var standard = new VolumeSpeech(); IPcmOutput? donorOutput = null;
        try
        {
            App.CreateSpeech = () => standard; App.CreatePcm = () => raw;
            App.CreateCredentialStore = null; App.CreateUtauPostprocessor = null;
            App.CreateOpenJtalkSpeech = pcm => { donorOutput = pcm; return new PcmSpeech(pcm); };
            var services = App.InitializeServices();
            try
            {
                check(services.Pcm is VolumePcmOutput wrapper && ReferenceEquals(wrapper.Volume, services.Volume) &&
                    ReferenceEquals(standard.Volume, services.Volume) && ReferenceEquals(donorOutput, services.Pcm),
                    "app factories share one volume state with standard fallback and the PCM output passed to NIT");
                services.Volume.Percent = 25;
                await services.OpenJtalk!.SpeakAsync("fixed donor", default);
                await services.Speech.SpeakAsync("fixed fallback", default);
                check(raw.Samples.Span.SequenceEqual(new short[] { 250, -250 }) && standard.LastVolume == 25,
                    "one shared volume change reaches NIT PCM and standard fallback without modifying synthesis PCM");
                services.Volume.Percent = 0;
                await services.OpenJtalk.SpeakAsync("fixed muted donor", default);
                check(raw.Calls == 1, "factory NIT output respects mute through the same PCM adapter");
            }
            finally
            {
                if (services.OpenJtalk is not null) await services.OpenJtalk.DisposeAsync();
                await services.Speech.DisposeAsync();
                if (services.Pcm is not null) await services.Pcm.DisposeAsync();
            }
            check(raw.Disposals == 1, "factory PCM wrapper owns and releases its raw output once");
        }
        finally
        {
            App.CreateSpeech = oldSpeech; App.CreatePcm = oldPcm; App.CreateCredentialStore = oldCredentials;
            App.CreateUtauPostprocessor = oldPostprocessor; App.CreateOpenJtalkSpeech = oldOpenJtalk;
        }
    }

    private sealed class VolumeSpeech : ISpeechOutput, ISpeechVolumeControl
    {
        public string Name => "fake standard volume";
        public PlaybackVolume Volume { get; set; } = new();
        public int LastVolume { get; private set; }
        public Task SpeakAsync(string text, CancellationToken ct) { ct.ThrowIfCancellationRequested(); LastVolume = Volume.Percent; return Task.CompletedTask; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class PcmSpeech(IPcmOutput output) : ISpeechOutput
    {
        public string Name => "fake NIT PCM";
        public Task SpeakAsync(string text, CancellationToken ct) => output.PlayAsync(new short[] { 1000, -1000 }, 48000, ct);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class RecordingOutput : IPcmOutput
    {
        public int Calls { get; private set; }
        public int Disposals { get; private set; }
        public int Rate { get; private set; }
        public Exception? Failure { get; set; }
        public ReadOnlyMemory<short> Samples { get; private set; }
        public Task PlayAsync(ReadOnlyMemory<short> samples, int sampleRate, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is { } failure) throw failure;
            Calls++; Samples = samples; Rate = sampleRate;
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync() { Disposals++; return ValueTask.CompletedTask; }
    }
}
