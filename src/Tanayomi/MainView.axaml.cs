using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Tanayomi;

public partial class MainView : UserControl
{
    public MainView() => InitializeComponent();
    private void ResetUtauVoiceTuning(object? sender, RoutedEventArgs args)
    {
        if (DataContext is MainViewModel model) model.ResetUtauVoiceTuning();
    }
    private async void AuthenticateTwitch(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.TwitchAuthentication, async model =>
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await model.AuthenticateTwitchAsync(uri => top.Launcher.LaunchUriAsync(uri));
    });
    private async void ConnectTwitch(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.TwitchConnection, async model =>
    {
        if (TopLevel.GetTopLevel(this) is { } top)
            await model.ConnectWithSavedAccountAsync(uri => top.Launcher.LaunchUriAsync(uri));
    });
    private async void ForgetTwitch(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.CredentialDelete, model => model.ForgetTwitchAsync());
    private async void CopyDiagnostics(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.DiagnosticCopy, model =>
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        return model.CopyDiagnosticsAsync(clipboard is null ? null : text => clipboard.SetTextAsync(text));
    });
    private async void ApplyTextSettings(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.TextSettings, model => model.ApplyTextSettingsAsync());
    private async void AddAuthorReading(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.TextSettings, model => { model.AddAuthorReading(); return Task.CompletedTask; });
    private async void AddMutedAuthor(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.TextSettings, model => { model.AddMutedAuthor(); return Task.CompletedTask; });
    private async void ImportVoicebank(object? sender, RoutedEventArgs args) =>
        await RunUiAsync(DiagnosticOperation.VoicebankImport, async model =>
    {
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        try
        {
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "UTAU音源ZIPを追加", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"], MimeTypes = ["application/zip"] }]
            });
            if (files.Count == 0) return;
            await using var stream = await files[0].OpenReadAsync();
            await model.ImportVoicebankAsync(stream);
        }
        catch (Exception ex) { model.ReportFileError(ex); }
    });

    private async Task RunUiAsync(DiagnosticOperation operation, Func<MainViewModel, Task> action)
    {
        MainViewModel? model = null;
        try
        {
            model = DataContext as MainViewModel;
            if (model is not null) await action(model);
        }
        catch (Exception error)
        {
            // A failed dispatcher/report during teardown must also remain inside
            // the async-void native UI event boundary.
            try { model?.ReportUiError(operation, error); } catch (Exception) { }
        }
    }
}
