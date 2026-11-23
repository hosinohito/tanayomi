using System.Globalization;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;

namespace Tanayomi;

// Report fields deliberately accept no user-controlled strings. Exception messages,
// stack traces, paths, source labels and AppNotice.Text never enter this model.
public enum DiagnosticCode
{
    ConnectionInterrupted, ConnectionRejected, SourceEnded, AuthenticationFailed,
    AuthenticationExpired, CredentialReadFailed, CredentialWriteFailed, CredentialDeleteFailed,
    VoicebankLoadFailed, VoicebankImportFailed, VoicePreparationFailed, SpeechFallback,
    SpeechFailed, SpeechTimedOut, StorageUnavailable, MobileInterrupted, ClipboardFailed, UiOperationFailed,
    StandardSpeechInitializationFailed, PcmInitializationFailed, CredentialStoreInitializationFailed,
    VoicebankMigrationFailed, MobileBackgroundUnavailable, MobileStateFailed, MobileAudioSessionFailed,
    TextSettingsReadFailed, TextSettingsWriteFailed, UtauPostprocessorInitializationFailed, ApplicationShutdownFailed,
    OpenJtalkSpeechInitializationFailed, UtauProsodyFallback, UtauProsodyTimingFallback, VoicebankPitchMappingRejected, PlaybackVolumeReadFailed, PlaybackVolumeWriteFailed, DrawnOutModeReadFailed, DrawnOutModeWriteFailed
}

public enum DiagnosticOperation
{
    TwitchConnection, TwitchIrc, TwitchEventSub, YouTubeConnection, TwitchAuthentication,
    CredentialRestore, CredentialSave, CredentialDelete, SessionValidation, SessionRefresh,
    VoicebankLoad, VoicebankImport, VoicePreparation, SpeechPrimary, SpeechFallback,
    SpeechProcessing, Storage, MobileLifecycle, DiagnosticCopy, StandardSpeechInitialization,
    PcmInitialization, CredentialStoreInitialization, VoicebankMigration, MobileBackgroundStart,
    MobileBackgroundStop, MobileAudioSessionSetup, MobileAudioSessionActivation, MobileAudioSessionRelease, TextSettings, ApplicationShutdown,
    OpenJtalkInitialization, PlaybackVolumeSettings, DrawnOutModeSettings
}

public enum DiagnosticRecovery
{
    Retrying, CheckConnectionSettings, Reauthenticate, SourceFinished, StandardVoice,
    NextUtterance, RetryOperation, SessionOnly, WithoutStorage, ReconnectOnForeground, AvailableFeatures, OtherVoicebanks, Stopped, FlatUtau, OrdinaryUtauTiming, SelectedUtauFallback
}

public enum DiagnosticException
{
    None, Exception, IOException, InvalidDataException, UnauthorizedAccessException,
    HttpRequestException, SocketException, AuthenticationException, JsonException,
    SourceConfigurationException, SourceEndedException, OperationCanceledException,
    TaskCanceledException, TimeoutException, ArgumentException, InvalidOperationException,
    NotSupportedException, PlatformNotSupportedException, ObjectDisposedException,
    DecoderFallbackException, OverflowException, FileNotFoundException, DirectoryNotFoundException
}

public sealed record DiagnosticEvent(DiagnosticCode Code, DiagnosticOperation Operation,
    DiagnosticRecovery Recovery, DiagnosticException Exception)
{
    public static DiagnosticEvent Create(DiagnosticCode code, DiagnosticOperation operation,
        DiagnosticRecovery recovery, Exception? error = null) => new(code, operation, recovery, Classify(error));

    private static DiagnosticException Classify(Exception? error)
    {
        // Exact known types avoid exporting arbitrary type names from third-party
        // exceptions. Unknown/derived types retain the safe Exception classification.
        if (error is null) return DiagnosticException.None;
        var type = error.GetType();
        if (type == typeof(InvalidDataException)) return DiagnosticException.InvalidDataException;
        if (type == typeof(IOException)) return DiagnosticException.IOException;
        if (type == typeof(FileNotFoundException)) return DiagnosticException.FileNotFoundException;
        if (type == typeof(DirectoryNotFoundException)) return DiagnosticException.DirectoryNotFoundException;
        if (type == typeof(DecoderFallbackException)) return DiagnosticException.DecoderFallbackException;
        if (type == typeof(OverflowException)) return DiagnosticException.OverflowException;
        if (type == typeof(UnauthorizedAccessException)) return DiagnosticException.UnauthorizedAccessException;
        if (type == typeof(HttpRequestException)) return DiagnosticException.HttpRequestException;
        if (type == typeof(SocketException)) return DiagnosticException.SocketException;
        if (type == typeof(AuthenticationException)) return DiagnosticException.AuthenticationException;
        if (type == typeof(JsonException)) return DiagnosticException.JsonException;
        if (type == typeof(SourceConfigurationException)) return DiagnosticException.SourceConfigurationException;
        if (type == typeof(SourceEndedException)) return DiagnosticException.SourceEndedException;
        if (type == typeof(TaskCanceledException)) return DiagnosticException.TaskCanceledException;
        if (type == typeof(OperationCanceledException)) return DiagnosticException.OperationCanceledException;
        if (type == typeof(TimeoutException)) return DiagnosticException.TimeoutException;
        if (type == typeof(ArgumentException)) return DiagnosticException.ArgumentException;
        if (type == typeof(ObjectDisposedException)) return DiagnosticException.ObjectDisposedException;
        if (type == typeof(InvalidOperationException)) return DiagnosticException.InvalidOperationException;
        if (type == typeof(PlatformNotSupportedException)) return DiagnosticException.PlatformNotSupportedException;
        if (type == typeof(NotSupportedException)) return DiagnosticException.NotSupportedException;
        return DiagnosticException.Exception;
    }
}

public sealed class DiagnosticBuffer
{
    public const int MaximumEntries = 128;
    private readonly object gate = new();
    private readonly Queue<(DateTimeOffset Time, DiagnosticEvent Event)> entries = new();
    private readonly int capacity;
    public static string BuildIdentity { get; } = GetBuildIdentity();
    public int Count { get { lock (gate) return entries.Count; } }

    public DiagnosticBuffer(int capacity = MaximumEntries)
    {
        if (capacity < 1 || capacity > MaximumEntries) throw new ArgumentOutOfRangeException(nameof(capacity));
        this.capacity = capacity;
    }

    public void Record(DiagnosticEvent diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        if (!Enum.IsDefined(diagnostic.Code) || !Enum.IsDefined(diagnostic.Operation) ||
            !Enum.IsDefined(diagnostic.Recovery) || !Enum.IsDefined(diagnostic.Exception))
            throw new ArgumentException("Invalid diagnostic field.", nameof(diagnostic));
        lock (gate)
        {
            if (entries.Count == capacity) entries.Dequeue();
            entries.Enqueue((DateTimeOffset.UtcNow, diagnostic));
        }
    }

    public string CreateReport()
    {
        (DateTimeOffset Time, DiagnosticEvent Event)[] snapshot;
        lock (gate) snapshot = entries.ToArray();
        var report = new StringBuilder();
        report.AppendLine("Tanayomi diagnostics v1");
        report.Append("Build: ").AppendLine(BuildIdentity);
        report.Append("Platform: ").AppendLine(OperatingSystem.IsWindows() ? "Windows" :
            OperatingSystem.IsMacOS() ? "macOS" : OperatingSystem.IsAndroid() ? "Android" :
            OperatingSystem.IsIOS() ? "iOS" : OperatingSystem.IsLinux() ? "Linux" : "Other");
        report.Append("Events: ").AppendLine(snapshot.Length.ToString(CultureInfo.InvariantCulture));
        foreach (var item in snapshot)
        {
            var diagnostic = item.Event;
            report.Append(item.Time.ToString("O", CultureInfo.InvariantCulture)).Append(" | ")
                .Append(CodeId(diagnostic.Code)).Append(" | cause=").Append(diagnostic.Code)
                .Append(" | operation=").Append(diagnostic.Operation)
                .Append(" | exception=").Append(diagnostic.Exception)
                .Append(" | recovery=").Append(diagnostic.Recovery).AppendLine();
        }
        return report.ToString();
    }

    public async Task<bool> CopyAsync(Func<string, Task>? writeClipboard)
    {
        try
        {
            if (writeClipboard is null) throw new NotSupportedException();
            await writeClipboard(CreateReport()).ConfigureAwait(false);
            return true;
        }
        catch (Exception error)
        {
            Record(DiagnosticEvent.Create(DiagnosticCode.ClipboardFailed, DiagnosticOperation.DiagnosticCopy,
                DiagnosticRecovery.RetryOperation, error));
            return false;
        }
    }

    public static string CodeId(DiagnosticCode code) => code switch
    {
        DiagnosticCode.ConnectionInterrupted => "NET001", DiagnosticCode.ConnectionRejected => "NET002",
        DiagnosticCode.SourceEnded => "NET003", DiagnosticCode.AuthenticationFailed => "AUTH001",
        DiagnosticCode.AuthenticationExpired => "AUTH002", DiagnosticCode.CredentialReadFailed => "STORE001",
        DiagnosticCode.CredentialWriteFailed => "STORE002", DiagnosticCode.CredentialDeleteFailed => "STORE003",
        DiagnosticCode.VoicebankLoadFailed => "VOICE001", DiagnosticCode.VoicebankImportFailed => "VOICE002",
        DiagnosticCode.VoicePreparationFailed => "VOICE003", DiagnosticCode.SpeechFallback => "SPEECH001",
        DiagnosticCode.SpeechFailed => "SPEECH002", DiagnosticCode.SpeechTimedOut => "SPEECH003",
        DiagnosticCode.StorageUnavailable => "STORE004", DiagnosticCode.MobileInterrupted => "MOBILE001",
        DiagnosticCode.ClipboardFailed => "UI001", DiagnosticCode.UiOperationFailed => "UI002",
        DiagnosticCode.StandardSpeechInitializationFailed => "APP001", DiagnosticCode.PcmInitializationFailed => "APP002",
        DiagnosticCode.CredentialStoreInitializationFailed => "APP003", DiagnosticCode.VoicebankMigrationFailed => "VOICE004",
        DiagnosticCode.VoicebankPitchMappingRejected => "VOICE009",
        DiagnosticCode.UtauPostprocessorInitializationFailed => "VOICE005",
        DiagnosticCode.OpenJtalkSpeechInitializationFailed => "VOICE006",
        DiagnosticCode.UtauProsodyFallback => "VOICE007",
        DiagnosticCode.UtauProsodyTimingFallback => "VOICE008",
        DiagnosticCode.ApplicationShutdownFailed => "APP004",
        DiagnosticCode.MobileBackgroundUnavailable => "MOBILE002", DiagnosticCode.MobileStateFailed => "MOBILE003",
        DiagnosticCode.MobileAudioSessionFailed => "MOBILE004",
        DiagnosticCode.TextSettingsReadFailed => "TEXT001", DiagnosticCode.TextSettingsWriteFailed => "TEXT002",
        DiagnosticCode.PlaybackVolumeReadFailed => "VOLUME001", DiagnosticCode.PlaybackVolumeWriteFailed => "VOLUME002",
        DiagnosticCode.DrawnOutModeReadFailed => "UTAU001", DiagnosticCode.DrawnOutModeWriteFailed => "UTAU002",
        _ => throw new ArgumentOutOfRangeException(nameof(code))
    };

    private static string GetBuildIdentity()
    {
        var assembly = typeof(DiagnosticBuffer).Assembly;
        // Numeric assembly version and MVID contain no source checkout path or
        // machine/user name, unlike unrestricted informational-version strings.
        var version = assembly.GetName().Version?.ToString() ?? "unknown";
        try { return $"{version}/{assembly.ManifestModule.ModuleVersionId:N}"; }
        catch (NotSupportedException) { return $"{version}/module-id-unavailable"; }
    }
}


