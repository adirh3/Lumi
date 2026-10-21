using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Lumi.Mobile.Views;

/// <summary>
/// Lumi's app icon for the phone's views, decoded once at the largest size they draw it. The source is
/// 1024 px, which would otherwise hold 4 MB of pixels for every 56 dp mark on screen.
/// </summary>
public static class LumiAppIcon
{
    // 56 dp on a 4x screen, the densest a phone gets.
    private const int DecodeWidth = 224;

    public static Bitmap Image { get; } = Load();

    private static Bitmap Load()
    {
        using var stream = AssetLoader.Open(new Uri("avares://Lumi.Mobile/Assets/lumi-icon.png"));
        return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
    }
}
