using System;
using System.Collections.Generic;
using System.IO;
using Lumi.Services.Appearance;
using SkiaSharp;

namespace Lumi.Services;

/// <summary>
/// Lumi's icon: Lumi's soft sparkle, with a smaller lilac sparkle for you floating at its shoulder and a dot for
/// the model on the other side, so Lumi stands between you and the model. Drawn in a 1024-unit square, so it is
/// crisp at any size. The installed icons are written from it in Lumi's colours (<see cref="Lumi"/>); the mark
/// inside the app and the tray icon are drawn from it in the user's accent (<see cref="ColorsFor"/>), and the
/// loading window draws its parts apart (<see cref="RenderPart"/>) to move them.
/// </summary>
internal static class AppIconArt
{
    internal enum Shape { Squircle, Circle, Square }

    /// <summary>The parts of the icon, bottom to top, which the loading window moves on their own.</summary>
    internal enum Part { Tile, Model, Lumi, You }

    /// <summary>One icon's colours: its tile, the shadows and shade of Lumi and the model, and your sparkle.</summary>
    internal sealed record IconColors(
        SKColor TileStart, SKColor TileMiddle, SKColor TileEnd, SKColor TileDepth,
        SKColor Shadow, SKColor Tint, SKColor You);

    /// <summary>Lumi's own colours, worn wherever the icon doesn't follow the accent.</summary>
    internal static readonly IconColors Lumi = new(
        SKColor.Parse("#5D5CF2"), SKColor.Parse("#9C58F5"), SKColor.Parse("#D84DE8"), SKColor.Parse("#C026D3"),
        SKColor.Parse("#2B1484"), SKColor.Parse("#EEE6FF"), SKColor.Parse("#E4CFFF"));

    /// <summary>Sizes up to this drop the shadows on the tile and enlarge the mark, so it still reads in a tray.</summary>
    private const int SmallCutMaxSize = 32;

    private const float Unit = 1024f;
    private const float SmallCutScale = 1.1f;

    // The sparkle's profile (the soft four-point shape Lumi has always had), as harmonics of its radius.
    private static readonly double[] Harmonics = [0.9076039046, 0.0961679041, -0.0040758391, 0.0003167373, -0.0000127069];

    // Half of your sparkle lies over Lumi, and the model sits on the opposite diagonal. Lumi sits half-way between
    // the tile's centre and the centroid of Lumi and you, which reads as centred.
    private static readonly SKPoint LumiCentre = new(503.81f, 520.19f);
    private static readonly SKPoint YouCentre = new(709.09f, 314.91f);
    private static readonly SKPoint ModelCentre = new(259.15f, 764.85f);
    private const float LumiTip = 350f, YouTip = 132f, ModelRadius = 26f;

    private static readonly SKPoint SheenCentre = new(190, 110);
    private static readonly SKPoint DepthCentre = new(930, 1010);
    private const float SheenRadius = 720, SheenOpacity = 0.2f, DepthRadius = 560, DepthOpacity = 0.22f;

    // Lumi and the model rest on the tile; you float above Lumi, casting a soft shadow and a close one.
    private const float ShadowOffset = 22, ShadowBlur = 26, ShadowOpacity = 0.3f;
    private const float ModelShadowBlur = 6, ModelShadowOpacity = 0.32f;
    private const float FloatOffset = 40, FloatBlur = 30, FloatOpacity = 0.3f;
    private const float ContactOffset = 8, ContactBlur = 8, ContactOpacity = 0.12f;
    private const float EdgeWidth = 6;

    /// <summary>The icon in <paramref name="palette"/>. Its tile blends the palette's light and dark tones,
    /// so one icon suits both themes; your sparkle is a pale tint of the palette's secondary colour.</summary>
    internal static IconColors ColorsFor(AccentPalettes.Palette palette)
    {
        if (palette.IsDefault)
            return Lumi;

        var (light, dark) = (palette.Light, palette.Dark);
        var white = Avalonia.Media.Colors.White;
        return new IconColors(
            Mix(light.Accent, dark.Accent, 0.3f), Mix(light.Secondary, dark.Secondary, 0.3f),
            Mix(light.Tertiary, dark.Tertiary, 0.3f), ToSk(light.Tertiary),
            ToSk(dark.Subtle), Mix(white, light.Secondary, 0.1f), Mix(white, light.Secondary, 0.25f));
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

    /// <summary>
    /// One part of the squircle icon on a transparent square of the icon's size, so the parts stacked in
    /// <see cref="Part"/> order draw the whole icon. Lumi, you and the model carry their own shadows and
    /// aren't clipped to the tile, so they can move off it.
    /// </summary>
    internal static SKBitmap RenderPart(IconColors colors, Part part, int size)
    {
        var bitmap = new SKBitmap(new SKImageInfo(size, size, SKColorType.Bgra8888, SKAlphaType.Premul));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.Transparent);
        canvas.Scale(size / Unit);
        switch (part)
        {
            case Part.Tile:
                using (var outline = Squircle())
                {
                    canvas.Save();
                    canvas.ClipPath(outline, antialias: true);
                    FillTile(canvas, colors);
                    canvas.Restore();
                    DrawEdge(canvas, outline);
                }
                break;
            case Part.Model:
                using (var model = ModelDot())
                {
                    DrawModelShadow(canvas, model, colors);
                    PaintModel(canvas, model, colors);
                }
                break;
            case Part.Lumi:
                using (var lumi = Sparkle(LumiCentre, LumiTip))
                {
                    DrawShadow(canvas, lumi, colors.Shadow, ShadowOffset, ShadowBlur, ShadowOpacity);
                    PaintLumi(canvas, lumi, colors);
                }
                break;
            case Part.You:
                using (var you = Sparkle(YouCentre, YouTip))
                    PaintYou(canvas, you, colors);
                break;
        }
        return bitmap;
    }

    /// <summary>The centre of a part, as a fraction of the icon's side from its top-left corner.</summary>
    internal static Avalonia.Point CentreOf(Part part)
    {
        var centre = part switch
        {
            Part.Lumi => LumiCentre,
            Part.You => YouCentre,
            Part.Model => ModelCentre,
            _ => new SKPoint(Unit / 2, Unit / 2),
        };
        return new Avalonia.Point(centre.X / Unit, centre.Y / Unit);
    }

    /// <summary>The model's radius, as a fraction of the icon's side.</summary>
    internal static double ModelRadiusFraction => ModelRadius / Unit;

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

        FillTile(canvas, colors);

        canvas.Save();
        canvas.Translate(Unit / 2, Unit / 2);
        canvas.Scale(markScale);
        canvas.Translate(-Unit / 2, -Unit / 2);
        DrawMark(canvas, colors, shadow);
        canvas.Restore();
        canvas.Restore();

        if (outline is not null)
            DrawEdge(canvas, outline);
    }

    private static void FillTile(SKCanvas canvas, IconColors colors)
    {
        using var tile = TileShader(colors);
        using var sheen = Fade(SheenCentre, SheenRadius, SKColors.White, SheenOpacity);
        using var depth = Fade(DepthCentre, DepthRadius, colors.TileDepth, DepthOpacity);
        using var paint = new SKPaint { IsAntialias = true };
        foreach (var shader in new[] { tile, sheen, depth })
        {
            paint.Shader = shader;
            canvas.DrawRect(0, 0, Unit, Unit, paint);
        }
    }

    /// <summary>A light edge along the top of the tile, inside it.</summary>
    private static void DrawEdge(SKCanvas canvas, SKPath outline)
    {
        canvas.Save();
        canvas.ClipPath(outline, antialias: true);
        using (var shader = EdgeShader())
        using (var edge = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = EdgeWidth, Shader = shader })
            canvas.DrawPath(outline, edge);
        canvas.Restore();
    }

    private static void DrawMark(SKCanvas canvas, IconColors colors, bool shadow)
    {
        using var lumi = Sparkle(LumiCentre, LumiTip);
        using var you = Sparkle(YouCentre, YouTip);
        using var model = ModelDot();

        if (shadow)
        {
            DrawShadow(canvas, lumi, colors.Shadow, ShadowOffset, ShadowBlur, ShadowOpacity);
            DrawModelShadow(canvas, model, colors);
        }

        PaintModel(canvas, model, colors);
        PaintLumi(canvas, lumi, colors);
        PaintYou(canvas, you, colors);
    }

    private static SKPath ModelDot()
    {
        var model = new SKPath();
        model.AddCircle(ModelCentre.X, ModelCentre.Y, ModelRadius);
        return model;
    }

    private static void DrawModelShadow(SKCanvas canvas, SKPath model, IconColors colors)
        => DrawShadow(canvas, model, colors.Shadow, ModelRadius * 0.3f, ModelShadowBlur, ModelShadowOpacity);

    private static void PaintModel(SKCanvas canvas, SKPath model, IconColors colors)
    {
        using var shade = Shade(ModelCentre.Y - ModelRadius, ModelCentre.Y + ModelRadius, colors.Tint);
        using var paint = new SKPaint { IsAntialias = true, Shader = shade };
        canvas.DrawPath(model, paint);
    }

    private static void PaintLumi(SKCanvas canvas, SKPath lumi, IconColors colors)
    {
        using var shade = Shade(LumiCentre.Y - LumiTip, LumiCentre.Y + LumiTip, colors.Tint);
        using var paint = new SKPaint { IsAntialias = true, Shader = shade };
        canvas.DrawPath(lumi, paint);
    }

    /// <summary>Your sparkle floats above Lumi, casting a soft shadow and a close one.</summary>
    private static void PaintYou(SKCanvas canvas, SKPath you, IconColors colors)
    {
        DrawShadow(canvas, you, colors.Shadow, ContactOffset, ContactBlur, ContactOpacity);
        DrawShadow(canvas, you, colors.Shadow, FloatOffset, FloatBlur, FloatOpacity);
        using var paint = new SKPaint { IsAntialias = true, Color = colors.You };
        canvas.DrawPath(you, paint);
    }

    private static void DrawShadow(SKCanvas canvas, SKPath shape, SKColor color, float offset, float blur, float opacity)
    {
        using var mask = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, blur);
        using var paint = new SKPaint { IsAntialias = true, Color = color.WithAlpha(Alpha(opacity)), MaskFilter = mask };
        canvas.Save();
        canvas.Translate(0, offset);
        canvas.DrawPath(shape, paint);
        canvas.Restore();
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
