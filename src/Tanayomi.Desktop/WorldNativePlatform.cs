using System.Runtime.InteropServices;

namespace Tanayomi.Desktop;

/// <summary>Fixed desktop WORLD inputs. Other RID assets remain disabled pending adoption.</summary>
internal static class WorldNativePlatform
{
    internal static string CurrentRid => Rid(OperatingSystem.IsWindows() ? "win" :
        OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "osx" : "", RuntimeInformation.ProcessArchitecture);

    internal static string Rid(string os, Architecture architecture) => (os, architecture) switch
    {
        ("win", Architecture.X64) => "win-x64",
        ("linux", Architecture.X64) => "linux-x64",
        ("linux", Architecture.Arm64) => "linux-arm64",
        ("osx", Architecture.X64) => "osx-x64",
        ("osx", Architecture.Arm64) => "osx-arm64",
        _ => throw new PlatformNotSupportedException("WORLDはこのOS・プロセス構成に対応していません。")
    };

    internal static string FileName(string rid) => rid switch
    {
        "win-x64" => "tanayomi-world.dll",
        "linux-x64" or "linux-arm64" => "libtanayomi-world.so",
        "osx-x64" or "osx-arm64" => "libtanayomi-world.dylib",
        _ => throw new PlatformNotSupportedException()
    };

    internal static string LibraryPath(string root) => Validate(root, CurrentRid);

    internal static string Validate(string root, string rid)
    {
        var filename = FileName(rid);
        // Other RID names are defined for future audited assets. No such assets
        // have been adopted: do not enable arbitrary local files or manifests.
        if (rid != "win-x64")
            throw new PlatformNotSupportedException("このOS向けWORLD資産はまだ監査・同梱されていません。");
        var path = Path.GetFullPath(Path.Combine(root, filename));
        WorldComparison.RejectLinks(path);
        if (!File.Exists(path)) throw new FileNotFoundException("WORLDの同梱資産がありません。", path);
        return path;
    }
}