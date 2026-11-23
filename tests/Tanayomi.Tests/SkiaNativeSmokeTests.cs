using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SkiaSharp;

internal static class SkiaNativeSmokeTests
{
    internal static int RunNativeCheck(string libraryPath, string skiaSourceRoot)
    {
        var pins = new List<FileStream>();
        try
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This smoke audits the Windows x64 candidate.");
            string root = Path.GetFullPath(skiaSourceRoot);
            var nativePath = Path.GetFullPath(libraryPath);
            pins.Add(OpenPinned(nativePath, 16 * 1024 * 1024,
                "828EF8C611AD23EC33CA1A6581A8CFE8F0CBB45925C0AE87CFC074BAED8C2458",
                "7DEC3BA900AB353491E6446F0083739924C6F8DD668832E2F09D38EBFFDBBE1C"));
            string nativeDirectory = Path.GetDirectoryName(nativePath) ?? throw new InvalidDataException("Native directory missing.");
            foreach (var runtime in new Dictionary<string, string>
            {
                ["msvcp140.dll"] = "43A9666C2F405258F165B53082860AC7838039A75FD2E85286085EDE0DA0273B",
                ["vcruntime140.dll"] = "C865C3366A98431EC3A5959CB5AC3966081A43B82DFCD8BFEFAFE0146B1508DB",
                ["vcruntime140_1.dll"] = "6C1E531C25AB2934A4EA9970598BC751D924D7CC5650DF3E1282B61D6CD24F42",
            }) pins.Add(OpenPinned(Path.Combine(nativeDirectory, runtime.Key), 1024 * 1024, runtime.Value));
            pins.Add(OpenPinned(typeof(SKBitmap).Assembly.Location, 1024 * 1024,
                "AAAAA18C68BA1F3A3408B00DFF28B11D5705198E17BA9D3AA59222BFD35407C8"));
            var native = NativeLibrary.Load(nativePath, typeof(SkiaNativeSmokeTests).Assembly,
                DllImportSearchPath.UseDllDirectoryForDependencies | DllImportSearchPath.System32);
            NativeLibrary.SetDllImportResolver(typeof(SKBitmap).Assembly, (name, _, _) =>
                name == "libSkiaSharp" || name == "libSkiaSharp.dll" ? native : IntPtr.Zero);
            var decodes = new List<object>();
            var fixtures = new Dictionary<string, string>
            {
                ["rgba.png"] = "third_party/externals/libpng/contrib/pngsuite/basn6a08.png",
                ["rgb.jpg"] = "third_party/externals/libjpeg-turbo/testimages/testorig.jpg",
                ["still.webp"] = "third_party/externals/libwebp/webp_js/test_webp_js.webp",
                ["still.gif"] = "modules/canvaskit/tests/assets/color_wheel.gif",
                ["animated.gif"] = "modules/canvaskit/tests/assets/flightAnim.gif",
            };
            var fixtureHashes = new Dictionary<string, string>
            {
                ["rgba.png"] = "756A03364C02E3C9F85D6F4029EB3CEF2488C081DC537D46E102BEBCB9E02732",
                ["rgb.jpg"] = "ACC6EC555D41D15B368320EDAA3B20958EE6FA97CB6E4A18D1213D5AE8BEC73B",
                ["still.webp"] = "C8A4935DCE6A58BAB5FC20A400BFFC70C6270A23F7AB772C0E90225E1927294E",
                ["still.gif"] = "2B7B70F98476B56CF4DE4F2C87986C300A7BDF516787B981657237ED4260CABB",
                ["animated.gif"] = "2D3A207D212B0DB8F521D285D33C9F292CC3378FBF0DD4CD8FF6467379D0E90E",
            };
            foreach (var fixture in fixtures)
            {
                string name = fixture.Key;
                using var file = OpenPinned(Path.Combine(root, fixture.Value), 2 * 1024 * 1024, fixtureHashes[name]);
                byte[] input = new byte[checked((int)file.Length)];
                file.ReadExactly(input);
                using var stream = new SKMemoryStream(input);
                using var codec = SKCodec.Create(stream);
                if (codec == null || codec.Info.Width < 1 || codec.Info.Height < 1 ||
                    codec.Info.Width > 2048 || codec.Info.Height > 2048) throw new InvalidDataException("Invalid codec bounds.");
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var bitmap = new SKBitmap(info);
                var result = codec.GetPixels(info, bitmap.GetPixels());
                if (result != SKCodecResult.Success) throw new InvalidDataException("First frame decode failed: " + name + ":" + result);
                string first = Hash(bitmap.Bytes);
                string? second = null;
                int frames = codec.FrameCount;
                if (name == "animated.gif")
                {
                    if (frames < 2) throw new InvalidDataException("Animated fixture did not expose multiple frames.");
                    result = codec.GetPixels(info, bitmap.GetPixels(), new SKCodecOptions(1, 0));
                    if (result != SKCodecResult.Success) throw new InvalidDataException("Second animation frame failed.");
                    second = Hash(bitmap.Bytes);
                    if (second == first) throw new InvalidDataException("Animation frame did not change pixels.");
                }
                decodes.Add(new { Name = name, InputSha256 = Hash(input), Width = info.Width, Height = info.Height,
                    Format = codec.EncodedFormat.ToString(), Frames = frames, FirstPixelSha256 = first, SecondPixelSha256 = second });
            }
            using (var invalidStream = new SKMemoryStream(new byte[] { 0x47, 0x49, 0x46 }))
            using (var invalidCodec = SKCodec.Create(invalidStream))
                if (invalidCodec != null) throw new InvalidDataException("Malformed GIF header was accepted.");
            using var rendered = new SKBitmap(new SKImageInfo(640, 96, SKColorType.Bgra8888, SKAlphaType.Premul));
            string fontPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "meiryo.ttc");
            using var face = SKTypeface.FromFile(fontPath) ?? throw new InvalidDataException("Existing Japanese font unavailable.");
            using var font = new SKFont(face, 34);
            const string text = "日本語 こんにちは 読み上げ 漢字かなカナ";
            ushort[] glyphs = font.GetGlyphs(text);
            if (glyphs.Length != text.Length || glyphs.Any(glyph => glyph == 0)) throw new InvalidDataException("Japanese glyph coverage failed.");
            using (var canvas = new SKCanvas(rendered))
            using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
            {
                canvas.Clear(SKColors.White);
                canvas.DrawText(text, 8, 58, SKTextAlign.Left, font, paint);
                canvas.Flush();
            }
            int dark = 0;
            foreach (var pixel in rendered.Pixels) if (pixel.Red < 220 || pixel.Green < 220 || pixel.Blue < 220) dark++;
            if (dark < 500 || dark > 20000) throw new InvalidDataException("Japanese raster coverage is implausible.");
            var encodes = new List<object>();
            using var image = SKImage.FromBitmap(rendered);
            foreach (var format in new[] { SKEncodedImageFormat.Png, SKEncodedImageFormat.Jpeg, SKEncodedImageFormat.Webp, SKEncodedImageFormat.Gif })
            {
                using var encoded = image.Encode(format, 90);
                bool supported = encoded != null && encoded.Size > 0;
                if (!supported && format != SKEncodedImageFormat.Gif) throw new InvalidDataException("Required encoder unavailable: " + format);
                string? encodedHash = null, decodedHash = null;
                long length = 0;
                if (supported)
                {
                    var bytes = encoded!.ToArray(); length = bytes.Length; encodedHash = Hash(bytes);
                    using var decoded = SKBitmap.Decode(bytes) ?? throw new InvalidDataException("Encoded result did not decode.");
                    if (decoded.Width != rendered.Width || decoded.Height != rendered.Height) throw new InvalidDataException("Roundtrip size changed.");
                    decodedHash = Hash(decoded.Bytes);
                    if (format == SKEncodedImageFormat.Png && decodedHash != Hash(rendered.Bytes)) throw new InvalidDataException("PNG lossless roundtrip changed pixels.");
                }
                encodes.Add(new { Format = format.ToString(), Supported = supported, Bytes = length, EncodedSha256 = encodedHash, RoundtripPixelSha256 = decodedHash });
            }
            // The native module remains alive until this finite worker exits, after all managed Skia objects are disposed.
            Console.WriteLine(JsonSerializer.Serialize(new { ManagedVersion = typeof(SKBitmap).Assembly.GetName().Version?.ToString() ?? "unknown",
                ManagedPackage = "3.119.4", NativePath = nativePath, MalformedInputRejected = true, Decodes = decodes, Japanese = new { Typeface = face.FamilyName, Glyphs = glyphs.Length,
                    MissingGlyphs = 0, DarkPixels = dark, PixelSha256 = Hash(rendered.Bytes) }, Encodes = encodes,
                GpuTested = false, AvaloniaTested = false }));
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("SKIA-MANAGED-SMOKE-FAILED " + error.GetType().Name + ": " + error.Message);
            return 1;
        }
        finally { foreach (var pin in pins) pin.Dispose(); }
    }
    private static FileStream OpenPinned(string path, int maximumBytes, params string[] allowedHashes)
    {
        path = Path.GetFullPath(path);
        for (string? current = path; current != null; current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked Skia audit input is refused.");
        var information = new FileInfo(path);
        if (!information.Exists || information.Length < 1 || information.Length > maximumBytes)
            throw new InvalidDataException("Skia audit input size is invalid.");
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.SequentialScan);
        try
        {
            if (stream.Length != information.Length || stream.Length > maximumBytes)
                throw new InvalidDataException("Skia audit input changed before pinning.");
            string hash = Convert.ToHexString(SHA256.HashData(stream));
            if (!allowedHashes.Contains(hash, StringComparer.Ordinal)) throw new InvalidDataException("Skia audit input hash mismatch.");
            stream.Position = 0;
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
