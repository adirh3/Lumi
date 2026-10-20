using System.Diagnostics;
using Android.Content;
using Android.Graphics;
using Android.Widget;
using Avalonia.Platform;

namespace Lumi.Mobile.Android;

/// <summary>
/// The app's typeface for the text Android itself draws (the composer and the text-selection panel),
/// so it reads in the same Inter as everything Avalonia draws. Android loads a font only from a
/// file or its own assets, so each bundled face is copied out once.
/// </summary>
internal static class AndroidAppTypeface
{
    private const string RegularFile = "Inter-Regular.ttf";
    private const string SemiBoldFile = "Inter-SemiBold.ttf";
    private static readonly Dictionary<string, Typeface?> s_faces = new(StringComparer.Ordinal);

    public static void Apply(TextView view) => Apply(view, RegularFile);

    public static void ApplySemiBold(TextView view) => Apply(view, SemiBoldFile);

    private static void Apply(TextView view, string file)
    {
        if (view.Context is { } context && Load(context, file) is { } typeface)
            view.Typeface = typeface;
    }

    private static Typeface? Load(Context context, string file)
    {
        if (s_faces.TryGetValue(file, out var cached))
            return cached;

        Typeface? typeface = null;
        try
        {
            var directory = System.IO.Path.Combine(context.CacheDir!.AbsolutePath, "fonts");
            var path = System.IO.Path.Combine(directory, file);
            using (var source = AssetLoader.Open(new Uri($"avares://Lumi.Mobile/Assets/Fonts/{file}")))
            {
                if (!File.Exists(path) || new FileInfo(path).Length != source.Length)
                {
                    Directory.CreateDirectory(directory);
                    var partial = path + ".partial";
                    using (var target = File.Create(partial))
                        source.CopyTo(target);
                    File.Move(partial, path, overwrite: true);
                }
            }

            typeface = Typeface.CreateFromFile(path);
        }
        catch (Exception ex)
        {
            // The platform's own font is a fine fallback; never let a cache quirk cost the composer.
            Trace.TraceWarning($"[AndroidAppTypeface] Using the platform font for {file}: {ex.Message}");
        }

        s_faces[file] = typeface;
        return typeface;
    }
}
