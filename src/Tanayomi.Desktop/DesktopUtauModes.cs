namespace Tanayomi.Desktop;

internal sealed class DesktopUtauModes : IUtauPcmPostprocessor, IUtauModeProvider
{
    private readonly Dictionary<UtauSynthesisMode, IUtauPcmPostprocessor?> processors = new();
    private readonly List<IAsyncDisposable> owned = new();
    public IReadOnlyList<UtauModeChoice> Modes { get; }
    public IReadOnlyList<Exception> InitializationErrors { get; }

    public DesktopUtauModes(bool worldAvailable = true,
        bool prosodyAvailable = true, IReadOnlyList<Exception>? initializationErrors = null,
        bool neuralAvailable = false, string? neuralReferenceRoot = null)
    {
        InitializationErrors = Array.AsReadOnly((initializationErrors ?? []).Take(8).ToArray());
        var choices = new List<UtauModeChoice>();
        Add(UtauSynthesisMode.Simple, "シンプル（波形接続）", null);
        if (neuralAvailable && prosodyAvailable)
            Add(UtauSynthesisMode.OpenVoice, "NIT日本語＋OpenVoice", new DesktopNeuralUtau(neuralReferenceRoot));
        Add(UtauSynthesisMode.Context,
            worldAvailable ? prosodyAvailable ? "時間・抑揚（現行）" : "一定音程（WORLD）" : "従来方式",
            worldAvailable ? prosodyAvailable ? new DesktopUtauProsody() : new DesktopWorldPostprocessor(WorldReference.CreateAsync) : null);
        if (prosodyAvailable) Add(UtauSynthesisMode.Timing, "時間予測のみ（WORLDなし）", new DesktopUtauProsody(timingOnly: true));
        if (worldAvailable) Add(UtauSynthesisMode.Flat, "一定音程（WORLD）", new DesktopWorldPostprocessor(WorldReference.CreateAsync));
        if (worldAvailable && prosodyAvailable)
            Add(UtauSynthesisMode.FastContext, "時間・抑揚（DIO解析）", new DesktopUtauProsody(fastAnalysis: true));
        if (prosodyAvailable)
        {
            Add(UtauSynthesisMode.Psola, "TD-PSOLA", new DesktopUtauProsody(waveform: WaveformPitchMethod.Psola));
            Add(UtauSynthesisMode.PhaseVocoder, "位相ボコーダー", new DesktopUtauProsody(waveform: WaveformPitchMethod.PhaseVocoder));
        }
        if (prosodyAvailable)
        {
            var plan = new DesktopDirectUtauPlan();
            var direct = new UtauDirectPsola(plan.CreateAsync, plan);
            plan.PlanningFallback += direct.NotifyPlanningFallback;
            plan.TimingFallback += direct.NotifyTimingFallback;
            Add(UtauSynthesisMode.DirectPsola, "原音直接・周期同期", direct);
        }
        else Add(UtauSynthesisMode.DirectPsola, "原音直接・周期同期（一定音程）", new UtauDirectPsola());
        Modes = Array.AsReadOnly(choices.ToArray());

        void Add(UtauSynthesisMode mode, string label, IUtauPcmPostprocessor? processor)
        {
            processors.Add(mode, processor);
            choices.Add(new(mode, mode is UtauSynthesisMode.Simple or UtauSynthesisMode.OpenVoice ? label : label + "（実験）"));
            if (processor is not null) owned.Add(processor);
        }
    }

    public IUtauPcmPostprocessor? Resolve(UtauSynthesisMode mode) =>
        processors.TryGetValue(mode, out var processor) ? processor : throw new ArgumentOutOfRangeException(nameof(mode));
    public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) =>
        Resolve(UtauSynthesisMode.Context)?.ProcessAsync(input, bank, ct) ?? Task.FromResult(input);
    public async ValueTask DisposeAsync()
    {
        List<Exception>? errors = null;
        foreach (var processor in owned)
            try { await processor.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { (errors ??= []).Add(error); }
        if (errors is not null) throw new AggregateException(errors);
    }
}
