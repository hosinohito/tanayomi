using Tanayomi;
using Tanayomi.Desktop;

internal static class UtauModeMenuTests
{
    internal static async Task RunAsync(Action<bool, string> check)
    {
        await using var desktop = new DesktopUtauModes(neuralAvailable: true);
        check(desktop.Modes.Select(choice => choice.Mode).SequenceEqual(new[]
        {
            UtauSynthesisMode.Simple, UtauSynthesisMode.OpenVoice, UtauSynthesisMode.Context,
            UtauSynthesisMode.Timing, UtauSynthesisMode.Flat, UtauSynthesisMode.FastContext,
            UtauSynthesisMode.Psola, UtauSynthesisMode.PhaseVocoder, UtauSynthesisMode.DirectPsola
        }), "Simple, OpenVoice, then the other desktop choices in their existing relative order");
        check(desktop.Modes[0].Label == "シンプル（波形接続）" && desktop.Modes[1].Label == "NIT日本語＋OpenVoice",
            "primary labels are unchanged");
        CheckExperimental(desktop.Modes.Skip(2));
        check(desktop.Modes.Any(choice => choice.Label == "TD-PSOLA（実験）") &&
              desktop.Modes.Any(choice => choice.Label == "位相ボコーダー（実験）"),
            "previous trial labels have exactly the new suffix");

        await using var unavailable = new DesktopUtauModes(worldAvailable: false, prosodyAvailable: false, neuralAvailable: true);
        check(unavailable.Modes.Select(choice => choice.Mode).SequenceEqual(new[]
        {
            UtauSynthesisMode.Simple, UtauSynthesisMode.Context, UtauSynthesisMode.DirectPsola
        }), "OpenVoice remains hidden when unavailable");
        CheckExperimental(unavailable.Modes.Skip(1));

        await using var portable = new UtauSpeech(new SilentOutput());
        check(portable.Modes.Select(choice => choice.Mode).SequenceEqual(new[]
        {
            UtauSynthesisMode.Simple, UtauSynthesisMode.Context, UtauSynthesisMode.DirectPsola
        }), "portable fallback also puts Simple first");
        CheckExperimental(portable.Modes.Skip(1));
        portable.Mode = UtauSynthesisMode.Context;
        check(portable.Mode == UtauSynthesisMode.Context, "labels and order do not alter mode identity");

        void CheckExperimental(IEnumerable<UtauModeChoice> choices) =>
            check(choices.All(choice => choice.Label.EndsWith("（実験）", StringComparison.Ordinal) &&
                !choice.Label.StartsWith("その他", StringComparison.Ordinal) &&
                !choice.Label.Contains("（試験）", StringComparison.Ordinal)),
                "other modes keep their names and use the experimental suffix");
    }

    private sealed class SilentOutput : IPcmOutput
    {
        public Task PlayAsync(ReadOnlyMemory<short> samples, int rate, CancellationToken ct) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}