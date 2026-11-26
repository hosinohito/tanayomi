using System.Diagnostics;

namespace Tanayomi;

internal sealed class UtauPlaybackStartedException(Exception cause)
    : IOException("発声開始後の音声処理に失敗したため、この発言の再読み上げを中止しました。", cause);

public sealed partial class UtauSpeech
{
    internal bool SimplePrefetchEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_PREFETCH_TRIAL") != "0";
    internal bool SimpleVoicedJoinsEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_VOICED_JOIN") != "0";

    internal bool SimplePitchContourEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_CONTINUOUS_PITCH") != "0";

    internal bool SimpleComplementaryEnvelopeEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_COMPLEMENTARY_ENVELOPE") != "0";

    internal bool SimpleAttackEnvelopeEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_ATTACK_ENVELOPE") != "0";

    internal bool SimpleLeadHandoffEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_LEAD_HANDOFF") != "0";

    internal bool SimpleLexicalAccentEnabled { get; init; } = OperatingSystem.IsWindows() &&
        Environment.GetEnvironmentVariable("TANAYOMI_UTAU_LEXICAL_ACCENT") != "0";

    // The renderer resets the previous-vowel overlap at a pause. A sokuon,
    // space or middot is not a safe/intentional audible segmentation point.
    internal static Range[] SimplePhraseRanges(UtauReadingUnit[] reading)
    {
        var ranges = new List<Range>();
        var start = 0;
        var spoken = false;
        var remaining = reading.Count(unit => unit.Alias is not null);
        for (var i = 0; i < reading.Length; i++)
        {
            var unit = reading[i];
            if (unit.Alias is not null) { remaining--; spoken = true; }
            if (!spoken || remaining == 0 || unit.Pause is not ('。' or '、' or '?')) continue;
            // Keep consecutive punctuation and silence markers together.
            if (reading[i + 1].Alias is null) continue;
            ranges.Add(start..(i + 1));
            start = i + 1;
            spoken = false;
        }
        ranges.Add(start..reading.Length);
        return [.. ranges];
    }

    private async Task SpeakSimplePrefetchedAsync(string text, Voicebank bank, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        var token = operation.Token;
        await synthesis.WaitAsync(token).ConfigureAwait(false);
        Task<PcmAudio>? next = null;
        var playbackSubmitted = false;
        var synthElapsed = 0.0;
        try
        {
            token.ThrowIfCancellationRequested();
            Volatile.Write(ref synthesisStarted, 1);
            simpleRendering = true;
            legacyRendering = acousticContextRendering = false;
            var started = Stopwatch.GetTimestamp();
            var reading = await Task.Run(() => ReadSimpleOrStandard(text, token), token)
                .ConfigureAwait(false);
            var ranges = SimplePhraseRanges(reading);
            var aliases = Aliases(bank);
            if (reading.Any(unit => unit.Alias is not null && !aliases.ContainsKey(unit.Alias)))
                throw new InvalidDataException("音源に必要な単独音がありません。");
            synthElapsed += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var totalSamples = 0;
            Task<PcmAudio> Render(int index) => Task.Run(() =>
            {
                var renderStarted = Stopwatch.GetTimestamp();
                var edges = ranges.Length > 1 ? new PrefetchEdges(index == 0, index == ranges.Length - 1) : (PrefetchEdges?)null;
                var result = Synthesize(text, bank, token, reading[ranges[index]], edges, aliases);
                synthElapsed += Stopwatch.GetElapsedTime(renderStarted).TotalMilliseconds;
                return result;
            }, token);

            next = Render(0);
            Func<ReadOnlyMemory<short>, int, CancellationToken, Task>? play = null;
            for (var index = 0; index < ranges.Length; index++)
            {
                var audio = await next!.ConfigureAwait(false);
                next = null;
                token.ThrowIfCancellationRequested();
                totalSamples += audio.Samples.Length;
                if (totalSamples > MaximumSamples) throw new InvalidDataException("合成音声が長すぎます。");
                play ??= output is VolumePcmOutput volume ? volume.SnapshotPlayback() : output.PlayAsync;
                // Treat a sink that throws synchronously as potentially audible as well.
                playbackSubmitted = true;
                var playback = play(audio.Samples, audio.SampleRate, token);
                if (index + 1 < ranges.Length && !playback.IsFaulted && !playback.IsCanceled) next = Render(index + 1);
                await playback.ConfigureAwait(false);
            }
            token.ThrowIfCancellationRequested();
            ReportSynthesisTime(synthElapsed);
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();
            token.ThrowIfCancellationRequested();
            if (playbackSubmitted) throw new UtauPlaybackStartedException(ex);
            throw;
        }
        finally
        {
            try
            {
                if (next is not null)
                {
                    await operation.CancelAsync().ConfigureAwait(false);
                    try { await next.ConfigureAwait(false); } catch { /* The foreground failure owns the result. */ }
                }
            }
            finally { synthesis.Release(); }
        }
    }

    private readonly record struct PrefetchEdges(bool First, bool Last);
}
