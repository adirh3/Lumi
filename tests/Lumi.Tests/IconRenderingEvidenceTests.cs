using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Lumi.Services;
using SkiaSharp;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// CI investigation only: records how this machine's Skia draws the icon and a few primitives, so x64 and
/// ARM64 renderings can be compared pixel by pixel. Writes to TestResults/icon-evidence/&lt;architecture&gt;.
/// </summary>
public sealed class IconRenderingEvidenceTests
{
    private const int Layer = 432, Shown = 288, Offset = (Layer - Shown) / 2;
    private const BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Static;

    [Fact]
    public void RecordsHowThisMachineDrawsTheIcon()
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var root = Environment.GetEnvironmentVariable("GITHUB_WORKSPACE") is { Length: > 0 } workspace
            ? Path.Combine(workspace, "TestResults", "icon-evidence", architecture)
            : Path.Combine(Path.GetTempPath(), "lumi-icon-evidence", architecture);
        Directory.CreateDirectory(root);
        var report = new Dictionary<string, object>
        {
            ["architecture"] = architecture,
            ["os"] = RuntimeInformation.OSDescription,
            ["skiaSharp"] = typeof(SKBitmap).Assembly.GetName().Version?.ToString() ?? "",
        };

        // The test's own comparison, in 8888 as the product draws it.
        using var square = AppIconArt.Render(AppIconArt.Lumi, Shown, AppIconArt.Shape.Square);
        using var background = AppIconArt.RenderAdaptiveLayer(AppIconArt.Lumi, Layer, foreground: false);
        using var foreground = AppIconArt.RenderAdaptiveLayer(AppIconArt.Lumi, Layer, foreground: true);
        using var composite = Composite(background, foreground, SKColorType.Bgra8888);
        Save(root, "square.png", square);
        Save(root, "adaptive-background.png", background);
        Save(root, "adaptive-foreground.png", foreground);
        Save(root, "adaptive-composite.png", composite);
        report["bgra8888"] = Compare(square, composite, foreground);

        // The same two paths in half-float, where the mark's layer keeps its precision before compositing.
        using var squareF16 = RenderSquare(SKColorType.RgbaF16);
        using var backgroundF16 = RenderLayer(SKColorType.RgbaF16, foreground: false);
        using var foregroundF16 = RenderLayer(SKColorType.RgbaF16, foreground: true);
        using var compositeF16 = Composite(backgroundF16, foregroundF16, SKColorType.RgbaF16);
        report["rgbaF16"] = Compare(squareF16, compositeF16, foregroundF16);

        report["primitives"] = Primitives(root);
        File.WriteAllText(Path.Combine(root, "report.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static Dictionary<string, object> Compare(SKBitmap square, SKBitmap composite, SKBitmap foreground)
    {
        var histogram = new int[256];
        var worst = new List<object>();
        for (var y = 0; y < Shown; y++)
        for (var x = 0; x < Shown; x++)
        {
            var (a, b) = (square.GetPixel(x, y), composite.GetPixel(x + Offset, y + Offset));
            var difference = new[] { Math.Abs(a.Red - b.Red), Math.Abs(a.Green - b.Green), Math.Abs(a.Blue - b.Blue) }.Max();
            histogram[difference]++;
            if (difference >= 3)
                worst.Add(new
                {
                    x, y, difference,
                    square = a.ToString(),
                    composite = b.ToString(),
                    markAlpha = foreground.GetPixel(x + Offset, y + Offset).Alpha,
                });
        }
        return new Dictionary<string, object>
        {
            ["max"] = Array.FindLastIndex(histogram, count => count > 0),
            ["histogram"] = histogram.Select((count, difference) => (count, difference))
                .Where(entry => entry.count > 0).ToDictionary(entry => entry.difference.ToString(), entry => entry.count),
            ["pixelsAtLeast3"] = worst,
        };
    }

    private static SKBitmap Composite(SKBitmap background, SKBitmap foreground, SKColorType type)
    {
        var icon = new SKBitmap(new SKImageInfo(Layer, Layer, type, SKAlphaType.Premul));
        using var canvas = new SKCanvas(icon);
        canvas.Clear(SKColors.Transparent);
        canvas.DrawBitmap(background, 0, 0);
        canvas.DrawBitmap(foreground, 0, 0);
        return icon;
    }

    // AppIconArt.Render and RenderAdaptiveLayer, drawn into another colour type.
    private static SKBitmap RenderSquare(SKColorType type)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Shown, Shown, type, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Shown / 1024f);
        typeof(AppIconArt).GetMethod("Draw", Private)!
            .Invoke(null, [canvas, AppIconArt.Lumi, AppIconArt.Shape.Square, 1f, true]);
        return bitmap;
    }

    private static SKBitmap RenderLayer(SKColorType type, bool foreground)
    {
        var bitmap = new SKBitmap(new SKImageInfo(Layer, Layer, type, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(Layer / 1024f);
        canvas.Translate(512, 512);
        canvas.Scale(AppIconArt.AdaptiveShownFraction);
        canvas.Translate(-512, -512);
        if (foreground)
            typeof(AppIconArt).GetMethod("DrawMark", Private)!.Invoke(null, [canvas, AppIconArt.Lumi, true]);
        else
            typeof(AppIconArt).GetMethod("FillTile", Private)!
                .Invoke(null, [canvas, AppIconArt.Lumi, (1 / AppIconArt.AdaptiveShownFraction - 1) * 512]);
        return bitmap;
    }

    /// <summary>One 256 px image per kind of drawing the icon does, to see which ones differ between machines.</summary>
    private static Dictionary<string, object> Primitives(string root)
    {
        var results = new Dictionary<string, object>();

        // A translucent solid colour over every background value (x: alpha, y: background).
        results["solidOver"] = Probe(root, "probe-solid-over.png", canvas =>
        {
            using var paint = new SKPaint();
            for (var y = 0; y < 256; y++)
            {
                paint.Color = new SKColor((byte)y, (byte)(255 - y), (byte)(y * 7 % 256));
                canvas.DrawRect(0, y, 256, 1, paint);
            }
            for (var x = 0; x < 256; x++)
            {
                paint.Color = new SKColor(200, 60, 140, (byte)x);
                canvas.DrawRect(x, 0, 1, 256, paint);
            }
        });

        // The tile's gradients.
        results["gradients"] = Probe(root, "probe-gradients.png", canvas =>
        {
            canvas.Scale(256 / 1024f);
            typeof(AppIconArt).GetMethod("FillTile", Private)!.Invoke(null, [canvas, AppIconArt.Lumi, 0f]);
        });

        // A blurred translucent shadow on a solid tile, as the sparkles cast.
        results["blurredShadow"] = Probe(root, "probe-blurred-shadow.png", canvas =>
        {
            canvas.Clear(new SKColor(0x9C, 0x58, 0xF5));
            using var mask = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 8);
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0x2B, 0x14, 0x84, 77), MaskFilter = mask };
            canvas.DrawCircle(128, 128, 70, paint);
        });

        // An anti-aliased edge in a solid colour.
        results["antialiasedEdge"] = Probe(root, "probe-aa-edge.png", canvas =>
        {
            canvas.Clear(new SKColor(0x9C, 0x58, 0xF5));
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(0xE4, 0xCF, 0xFF) };
            canvas.DrawCircle(128.3f, 127.7f, 90.4f, paint);
        });

        // A translucent premultiplied layer composited over a solid tile, as the adaptive icon's two layers are.
        results["layerOver"] = Probe(root, "probe-layer-over.png", canvas =>
        {
            canvas.Clear(new SKColor(0x9C, 0x58, 0xF5));
            using var layer = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
            using (var layerCanvas = new SKCanvas(layer))
            {
                layerCanvas.Clear(SKColors.Transparent);
                using var paint = new SKPaint();
                for (var y = 0; y < 256; y++)
                for (var x = 0; x < 256; x += 16)
                {
                    paint.Color = new SKColor((byte)(x * 3), (byte)y, (byte)(255 - y), (byte)y);
                    layerCanvas.DrawRect(x, y, 16, 1, paint);
                }
            }
            canvas.DrawBitmap(layer, 0, 0);
        });

        return results;
    }

    private static object Probe(string root, string name, Action<SKCanvas> draw)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(256, 256, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Transparent);
            draw(canvas);
        }
        Save(root, name, bitmap);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bitmap.Bytes));
    }

    private static void Save(string root, string name, SKBitmap bitmap)
    {
        File.WriteAllBytes(Path.Combine(root, name), AppIconArt.EncodePng(bitmap));
        File.WriteAllBytes(Path.Combine(root, Path.ChangeExtension(name, ".bgra")), bitmap.Bytes);
    }
}
