#if DEBUG
using System;
using System.IO;
using SkiaSharp;

namespace Lumi.Services;

/// <summary>
/// Writes Lumi's installed icons from <see cref="AppIconArt"/> in Lumi's own colours, so they always match
/// the icon the app draws: <c>dotnet run --project src\Lumi\Lumi.csproj -- --write-app-icons</c>.
/// </summary>
internal static class AppIconAssets
{
    private const string Flag = "--write-app-icons";

    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];
    private static readonly (string Density, int Size)[] AndroidSizes =
        [("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)];

    // The installer splash keeps its words; only the icon above them is redrawn.
    private static readonly SKRectI SplashIconArea = new(250, 32, 350, 130);
    private static readonly SKPointI SplashIconOrigin = new(260, 40);
    private const int SplashIconSize = 80;

    // A maskable icon may be cropped to a circle of 80% of its width; this keeps the mark as large
    // within that circle as it is within the tile.
    private const float MaskableMarkScale = 0.8f;

    internal static bool IsFlag(string arg) => string.Equals(arg, Flag, StringComparison.OrdinalIgnoreCase);

    internal static int Write()
    {
        var root = FindRepositoryRoot();
        if (root is null)
        {
            Console.Error.WriteLine("[app-icons] Run this from the Lumi repository: src/Lumi/Assets was not found.");
            return 1;
        }

        var colors = AppIconArt.Lumi;
        var assets = Path.Combine(root, "src", "Lumi", "Assets");
        WriteFile(Path.Combine(assets, "lumi-icon.png"), Png(AppIconArt.Render(colors, 1024)));
        WriteFile(Path.Combine(assets, "lumi-icon.ico"), AppIconArt.CreateIco(colors, IcoSizes));
        WriteFile(Path.Combine(assets, "installer-splash.png"), Png(Splash(Path.Combine(assets, "installer-splash.png"), colors)));

        var android = Path.Combine(root, "src", "Lumi.Mobile.Android", "Resources");
        foreach (var (density, size) in AndroidSizes)
        {
            var folder = Path.Combine(android, "mipmap-" + density);
            WriteFile(Path.Combine(folder, "ic_launcher.png"), Png(AppIconArt.Render(colors, size)));
            WriteFile(Path.Combine(folder, "ic_launcher_round.png"), Png(AppIconArt.Render(colors, size, AppIconArt.Shape.Circle)));
        }

        // The web app shows the phone's launcher artwork, byte for byte, plus a full-bleed maskable icon.
        var web = Path.Combine(root, "src", "Lumi.Mobile.Browser", "wwwroot", "icons");
        WriteFile(Path.Combine(web, "lumi-192.png"), File.ReadAllBytes(Path.Combine(android, "mipmap-xxxhdpi", "ic_launcher.png")));
        WriteFile(Path.Combine(web, "lumi-512.png"), Png(AppIconArt.Render(colors, 512)));
        WriteFile(Path.Combine(web, "lumi-maskable-512.png"),
            Png(AppIconArt.Render(colors, 512, AppIconArt.Shape.Square, MaskableMarkScale)));
        return 0;
    }

    private static SKBitmap Splash(string path, AppIconArt.IconColors colors)
    {
        using var source = SKBitmap.Decode(path)
            ?? throw new InvalidOperationException($"Could not read {path}.");
        var splash = new SKBitmap(new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(splash);
        canvas.DrawBitmap(source, 0, 0);

        // Clear the old icon: each row takes the background just left and right of the icon's area.
        var area = SplashIconArea;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            var left = splash.GetPixel(area.Left - 1, y);
            var right = splash.GetPixel(area.Right, y);
            for (var x = area.Left; x < area.Right; x++)
            {
                var t = (x - area.Left + 1f) / (area.Width + 1);
                splash.SetPixel(x, y, new SKColor(
                    Lerp(left.Red, right.Red, t), Lerp(left.Green, right.Green, t), Lerp(left.Blue, right.Blue, t)));
            }
        }

        using var icon = AppIconArt.Render(colors, SplashIconSize);
        canvas.DrawBitmap(icon, SplashIconOrigin.X, SplashIconOrigin.Y);
        return splash;
    }

    private static byte Lerp(byte from, byte to, float t) => (byte)Math.Round(from + (to - from) * t);

    private static byte[] Png(SKBitmap bitmap)
    {
        using (bitmap)
            return AppIconArt.EncodePng(bitmap);
    }

    private static void WriteFile(string path, byte[] data)
    {
        File.WriteAllBytes(path, data);
        Console.WriteLine($"[app-icons] {path}");
    }

    private static string? FindRepositoryRoot()
    {
        // The build output first: it lies inside the checkout whose art is running, wherever the command was started.
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, "src", "Lumi", "Assets"))
                    && Directory.Exists(Path.Combine(directory.FullName, "src", "Lumi.Mobile.Android")))
                    return directory.FullName;
            }
        }
        return null;
    }
}
#endif
