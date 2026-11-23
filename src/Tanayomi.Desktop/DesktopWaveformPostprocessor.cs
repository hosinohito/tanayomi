namespace Tanayomi.Desktop;

// Called only inside UtauSpeech's synthesis/preparation gate. Retains bounded
// numeric calibration, never utterance PCM. All waveform work runs off the UI.
internal sealed class DesktopWaveformPostprocessor(WaveformPitchMethod method) : IUtauPcmPostprocessor
{
    private readonly List<(string Key, double Pitch)> bases = [];
    private bool disposed;
    public async Task PrepareAsync(Voicebank bank, CancellationToken ct) => _ = await Basis(bank, ct);
    public Task<PcmAudio> ProcessAsync(PcmAudio input, Voicebank bank, CancellationToken ct) => ProcessContourAsync(input, bank, null, ct);
    public async Task<PcmAudio> ProcessContourAsync(PcmAudio input, Voicebank bank, double[]? contour, CancellationToken ct)
    {
        var snapshot = contour is null ? null : (double[])contour.Clone();
        var pitch = await Basis(bank, ct).ConfigureAwait(false);
        return await Task.Run(() => WaveformPitch.Process(input, pitch, snapshot, method, ct), ct).ConfigureAwait(false);
    }
    private async Task<double> Basis(Voicebank bank, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(disposed, this); ct.ThrowIfCancellationRequested();
        var key = DesktopWorldPostprocessor.BankKey(bank, ct);
        var index = bases.FindIndex(x => x.Key == key);
        if (index >= 0) { var value = bases[index]; bases.RemoveAt(index); bases.Add(value); return value.Pitch; }
        var reference = await WorldReference.CreateAsync(bank, ct).ConfigureAwait(false);
        var pitch = await Task.Run(() => WaveformPitch.Measure(reference, ct), ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (key != DesktopWorldPostprocessor.BankKey(bank, ct)) throw new IOException("音源が処理中に変更されました。");
        if (bases.Count == 8) bases.RemoveAt(0);
        bases.Add((key, pitch)); return pitch;
    }
    public ValueTask DisposeAsync() { disposed = true; bases.Clear(); return ValueTask.CompletedTask; }
}
