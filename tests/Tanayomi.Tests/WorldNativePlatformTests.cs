using System.Runtime.InteropServices;
using Tanayomi.Desktop;

internal static class WorldNativePlatformTests
{
    internal static void Run(Action<bool, string> check)
    {
        foreach (var (os, architecture, expected) in new[]
        {
            ("win", Architecture.X64, "win-x64"),
            ("linux", Architecture.X64, "linux-x64"),
            ("linux", Architecture.Arm64, "linux-arm64"),
            ("osx", Architecture.X64, "osx-x64"),
            ("osx", Architecture.Arm64, "osx-arm64")
        }) check(WorldNativePlatform.Rid(os, architecture) == expected, "WORLD selects process RID " + expected);
        check(WorldNativePlatform.FileName("win-x64") == "tanayomi-world.dll", "WORLD preserves Windows filename");
        check(WorldNativePlatform.FileName("linux-arm64") == "libtanayomi-world.so", "WORLD Linux filename");
        check(WorldNativePlatform.FileName("osx-x64") == "libtanayomi-world.dylib", "WORLD macOS filename");
        foreach (var (os, architecture) in new[] { ("win", Architecture.Arm64), ("linux", Architecture.X86), ("android", Architecture.Arm64) })
        {
            var rejected = false;
            try { _ = WorldNativePlatform.Rid(os, architecture); }
            catch (PlatformNotSupportedException) { rejected = true; }
            check(rejected, "WORLD rejects unsupported process architecture");
        }
        // No local binary, manifest, or boolean flag can opt unadopted assets in.
        foreach (var rid in new[] { "linux-x64", "linux-arm64", "osx-x64", "osx-arm64" })
        {
            var rejected = false;
            try { _ = WorldNativePlatform.Validate(AppContext.BaseDirectory, rid); }
            catch (PlatformNotSupportedException) { rejected = true; }
            check(rejected, "WORLD keeps unaudited assets disabled " + rid);
        }
    }
}
