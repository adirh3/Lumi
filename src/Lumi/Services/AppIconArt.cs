using System;
using System.Collections.Generic;
using System.IO;
using Lumi.Services.Appearance;
using SkiaSharp;

namespace Lumi.Services;

/// <summary>
/// Lumi's icon: Lumi's soft sparkle, met by a smaller one for you. Where they overlap, the meeting glows
/// in the palette's Strata-line colours. Drawn in a 1024-unit square, so it is crisp at any size. The
/// installed icons are written from it in Lumi's colours (<see cref="Lumi"/>); the mark inside the app
/// and the tray icon are drawn from it in the user's accent (<see cref="ColorsFor"/>).
/// </summary>
internal static class AppIconArt
{
    internal enum Shape { Squircle, Circle, Square }

    /// <summary>One icon's colours: its tile, the sparkles' shadow and shade, and the line where they meet.</summary>
    internal sealed record IconColors(
        SKColor TileStart, SKColor TileMiddle, SKColor TileEnd, SKColor TileDepth,
        SKColor Shadow, SKColor Tint,
        SKColor LineStart, SKColor LineMiddle, SKColor LineEnd);

    /// <summary>Lumi's own colours, worn wherever the icon doesn't follow the accent.</summary>
    internal static readonly IconColors Lumi = new(
        SKColor.Parse("#5D5CF2"), SKColor.Parse("#9C58F5"), SKColor.Parse("#D84DE8"), SKColor.Parse("#C026D3"),
        SKColor.Parse("#2B1484"), SKColor.Parse("#EEE6FF"),
        SKColor.Parse("#7C86FF"), SKColor.Parse("#B48CFF"), SKColor.Parse("#FF78C4"));

    /// <summary>Sizes up to this drop the shadow and enlarge the pair, so the meeting still reads in a tray.</summary>
    private const int SmallCutMaxSize = 32;

    private const float Unit = 1024f;
    private const float SmallCutScale = 1.1f;

    // The sparkle's profile (the soft four-point shape Lumi has always had), as harmonics of its radius.
    private static readonly double[] Harmonics = [0.9076039046, 0.0961679041, -0.0040758391, 0.0003167373, -0.0000127069];

    // Lumi's sparkle sits half-way between the tile's centre and the pair's centroid, which reads as centred.
    private static readonly SKPoint Big = new(502.4f, 521.6f);
    private static readonly SKPoint Small = new(679.18f, 344.82f);
    private static readonly SKPoint Meet = new(641.27f, 382.72f);
    private const float BigTip = 300f, SmallTip = 132f;
    private static readonly float MeetReach = SmallTip * 0.95f / MathF.Sqrt(2);

    private static readonly SKPoint SheenCentre = new(190, 110);
    private static readonly SKPoint DepthCentre = new(930, 1010);
    private const float SheenRadius = 720, SheenOpacity = 0.2f, DepthRadius = 560, DepthOpacity = 0.22f;
    private const float ShadowOffset = 22, ShadowBlur = 26, ShadowOpacity = 0.3f;
    private const float GlowBlur = 20, GlowOpacity = 0.5f, HighlightOpacity = 0.55f;
    private const float EdgeWidth = 6;

    /// <summary>The icon in <paramref name="palette"/>. Its tile blends the palette's light and dark tones,
    /// so one icon suits both themes; the meeting carries the palette's Strata line as the dark theme draws it.</summary>
    internal static IconColors ColorsFor(AccentPalettes.Palette palette)
    {
        if (palette.IsDefault)
            return Lumi;

        var (light, dark) = (palette.Light, palette.Dark);
        return new IconColors(
            Mix(light.Accent, dark.Accent, 0.3f), Mix(light.Secondary, dark.Secondary, 0.3f),
            Mix(light.Tertiary, dark.Tertiary, 0.3f), ToSk(light.Tertiary),
            ToSk(dark.Subtle), Mix(Avalonia.Media.Colors.White, light.Secondary, 0.1f),
            ToSk(dark.Accent), ToSk(dark.Secondary), ToSk(dark.Tertiary));
    }

    internal static SKBitmap Render(IconColors colors, int size, Shape shape = Shape.Squircle, float markScale = 1f)
    {
        var small = size <= SmallCutMaxSize;
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / Unit);
        Draw(canvas, colors, shape, small ? markScale * SmallCutScale : markScale, shadow: !small);
        return bitmap;
    }

    internal static byte[] EncodePng(SKBitmap bitmap)
    {
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>A Windows .ico with a PNG frame per size, so each surface shows a size drawn for it.</summary>
    internal static byte[] CreateIco(IconColors colors, IReadOnlyList<int> sizes)
    {
        var frames = new byte[sizes.Count][];
        for (var i = 0; i < sizes.Count; i++)
        {
            using var bitmap = Render(colors, sizes[i]);
            frames[i] = EncodePng(bitmap);
        }

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0);
        writer.Write((ushort)1);
        writer.Write((ushort)frames.Length);
        var offset = 6 + 16 * frames.Length;
        for (var i = 0; i < frames.Length; i++)
        {
            var dimension = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);
            writer.Write(dimension);
            writer.Write(dimension);
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((ushort)1);
            writer.Write((ushort)32);
            writer.Write(frames[i].Length);
            writer.Write(offset);
            offset += frames[i].Length;
        }
        foreach (var frame in frames)
            writer.Write(frame);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>The tile's outline in icon units: a superellipse, smoother than a rounded square.</summary>
    private static SKPath Squircle()
    {
        const int count = 64;
        var points = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            var angle = 2 * Math.PI * i / count;
            points[i] = new SKPoint(
                (float)(Unit / 2 + Unit / 2 * SignedPower(Math.Cos(angle), 0.4)),
                (float)(Unit / 2 + Unit / 2 * SignedPower(Math.Sin(angle), 0.4)));
        }
        return Smooth(points);
    }

    private static void Draw(SKCanvas canvas, IconColors colors, Shape shape, float markScale, bool shadow)
    {
        using var outline = Outline(shape);

        canvas.Save();
        if (outline is not null)
            canvas.ClipPath(outline, antialias: true);

        using (var tile = TileShader(colors))
        using (var sheen = Fade(SheenCentre, SheenRadius, SKColors.White, SheenOpacity))
        using (var depth = Fade(DepthCentre, DepthRadius, colors.TileDepth, DepthOpacity))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            foreach (var shader in new[] { tile, sheen, depth })
            {
                paint.Shader = shader;
                canvas.DrawRect(0, 0, Unit, Unit, paint);
            }
        }

        canvas.Save();
        canvas.Translate(Unit / 2, Unit / 2);
        canvas.Scale(markScale);
        canvas.Translate(-Unit / 2, -Unit / 2);
        DrawMark(canvas, colors, shadow);
        canvas.Restore();
        canvas.Restore();

        if (outline is null)
            return;

        // A light edge along the top of the tile, inside it.
        canvas.Save();
        canvas.ClipPath(outline, antialias: true);
        using (var shader = EdgeShader())
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = EdgeWidth, Shader = shader })
            canvas.DrawPath(outline, edge);
        canvas.Restore();
    }

    private static void DrawMark(SKCanvas canvas, IconColors colors, bool shadow)
    {
        using var big = Sparkle(Big, BigTip);
        using var small = Sparkle(Small, SmallTip);
        using var pair = big.Op(small, SKPathOp.Union);
        using var meeting = big.Op(small, SKPathOp.Intersect);

        if (shadow)
        {
            using var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, ShadowBlur);
            using var paint = new SKPaint
            {
                IsAntialias = true,
                Color = colors.Shadow.WithAlpha(Alpha(ShadowOpacity)),
                MaskFilter = blur,
            };
            canvas.Save();
            canvas.Translate(0, ShadowOffset);
            canvas.DrawPath(pair, paint);
            canvas.Restore();
        }

        using (var bigShade = Shade(Big.Y - BigTip, Big.Y + BigTip, colors.Tint))
        using (var smallShade = Shade(Small.Y - SmallTip, Small.Y + SmallTip, colors.Tint))
        using (var paint = new SKPaint { IsAntialias = true })
        {
            paint.Shader = bigShade;
            canvas.DrawPath(big, paint);
            paint.Shader = smallShade;
            canvas.DrawPath(small, paint);
        }

        // Where you meet: the overlap carries the line, glowing softly into both sparkles.
        using var line = LineShader(colors);
        using (var blur = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, GlowBlur))
        using (var glow = new SKPaint
        {
            IsAntialias = true,
            Shader = line,
            Color = SKColors.White.WithAlpha(Alpha(GlowOpacity)),
            MaskFilter = blur,
        })
        {
            canvas.Save();
            canvas.ClipPath(pair, antialias: true);
            canvas.DrawPath(meeting, glow);
            canvas.Restore();
        }
        using (var paint = new SKPaint { IsAntialias = true, Shader = line })
            canvas.DrawPath(meeting, paint);
        using (var highlight = Highlight())
        using (var paint = new SKPaint { IsAntialias = true, Shader = highlight })
            canvas.DrawPath(meeting, paint);
    }

    private static SKPath? Outline(Shape shape)
    {
        switch (shape)
        {
            case Shape.Squircle:
                return Squircle();
            case Shape.Circle:
                var circle = new SKPath();
                circle.AddCircle(Unit / 2, Unit / 2, Unit / 2);
                return circle;
            default:
                return null;
        }
    }

    private static SKPath Sparkle(SKPoint centre, float tip)
    {
        const int count = 160;
        var points = new SKPoint[count];
        for (var i = 0; i < count; i++)
        {
            var angle = 2 * Math.PI * i / count;
            var radius = 0d;
            for (var k = 0; k < Harmonics.Length; k++)
                radius += Harmonics[k] * Math.Cos(4 * k * angle);
            radius *= tip;
            points[i] = new SKPoint(centre.X + (float)(radius * Math.Cos(angle)), centre.Y + (float)(radius * Math.Sin(angle)));
        }
        return Smooth(points);
    }

    // A closed Catmull-Rom spline through the points.
    private static SKPath Smooth(SKPoint[] points)
    {
        var path = new SKPath();
        var count = points.Length;
        path.MoveTo(points[0]);
        for (var i = 0; i < count; i++)
        {
            var p0 = points[(i - 1 + count) % count];
            var p1 = points[i];
            var p2 = points[(i + 1) % count];
            var p3 = points[(i + 2) % count];
            path.CubicTo(
                p1.X + (p2.X - p0.X) / 6, p1.Y + (p2.Y - p0.Y) / 6,
                p2.X - (p3.X - p1.X) / 6, p2.Y - (p3.Y - p1.Y) / 6,
                p2.X, p2.Y);
        }
        path.Close();
        return path;
    }

    private static SKShader TileShader(IconColors colors)
        => SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(Unit, Unit),
            [colors.TileStart, colors.TileMiddle, colors.TileEnd], [0f, 0.52f, 1f], SKShaderTileMode.Clamp);

    private static SKShader LineShader(IconColors colors)
    {
        var (from, to) = LineEnds();
        return SKShader.CreateLinearGradient(
            from, to, [colors.LineStart, colors.LineMiddle, colors.LineEnd], [0f, 0.5f, 1f], SKShaderTileMode.Clamp);
    }

    private static (SKPoint From, SKPoint To) LineEnds()
        => (new SKPoint(Meet.X - MeetReach, Meet.Y - MeetReach), new SKPoint(Meet.X + MeetReach, Meet.Y + MeetReach));

    private static SKPoint HighlightCentre() => new(Meet.X - 6, Meet.Y - 10);

    private static SKShader Highlight() => Fade(HighlightCentre(), SmallTip * 0.6f, SKColors.White, HighlightOpacity);

    private static SKShader EdgeShader()
        => SKShader.CreateLinearGradient(
            new SKPoint(0, 0), new SKPoint(0, Unit),
            [SKColors.White.WithAlpha(Alpha(0.32f)), SKColors.White.WithAlpha(Alpha(0.06f)), SKColors.White.WithAlpha(0)],
            [0f, 0.3f, 1f], SKShaderTileMode.Clamp);

    private static SKShader Shade(float top, float bottom, SKColor tint)
        => SKShader.CreateLinearGradient(
            new SKPoint(0, top), new SKPoint(0, bottom), [SKColors.White, tint], [0f, 1f], SKShaderTileMode.Clamp);

    private static SKShader Fade(SKPoint centre, float radius, SKColor color, float opacity)
        => SKShader.CreateRadialGradient(
            centre, radius, [color.WithAlpha(Alpha(opacity)), color.WithAlpha(0)], [0f, 1f], SKShaderTileMode.Clamp);

    private static byte Alpha(float opacity) => (byte)Math.Round(opacity * 255);

    private static SKColor ToSk(Avalonia.Media.Color color) => new(color.R, color.G, color.B, color.A);

    private static SKColor Mix(Avalonia.Media.Color from, Avalonia.Media.Color to, float amount)
    {
        static byte Channel(byte a, byte b, float t) => (byte)Math.Round(a + (b - a) * t);
        return new SKColor(Channel(from.R, to.R, amount), Channel(from.G, to.G, amount), Channel(from.B, to.B, amount));
    }

    private static double SignedPower(double value, double power) => Math.Sign(value) * Math.Pow(Math.Abs(value), power);
}
