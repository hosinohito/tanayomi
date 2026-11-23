namespace Tanayomi;

public static class AppPaths
{
    // Desktop data travels with the app folder. Mobile OSes prohibit writing to
    // the signed application bundle, so only those adapters use their sandbox.
    public static string DataDirectory => OperatingSystem.IsAndroid() || OperatingSystem.IsIOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tanayomi")
        : Path.Combine(AppContext.BaseDirectory, "data");

    public static string? StorageWarning { get; private set; }

    public static void ConfigureProcessStorage()
    {
        try
        {
            var temporary = Path.Combine(DataDirectory, "tmp");
            // Even when creation fails, children must not inherit an OS-wide
            // temporary directory and silently write outside this app folder.
            Environment.SetEnvironmentVariable("TEMP", temporary);
            Environment.SetEnvironmentVariable("TMP", temporary);
            Environment.SetEnvironmentVariable("TMPDIR", temporary);
            Directory.CreateDirectory(temporary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StorageWarning = "実行ファイル横のdataに書き込めません。書き込み可能なフォルダーへアプリ一式を移してください。別の場所へは保存しません。";
        }
    }
}
