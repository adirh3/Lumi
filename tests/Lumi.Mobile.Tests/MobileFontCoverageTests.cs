using Avalonia.Platform;
using SkiaSharp;
using Xunit;

namespace Lumi.Mobile.Tests;

[Collection("Headless mobile UI")]
public sealed class MobileFontCoverageTests
{
    [Fact]
    public async Task TheAppTypefaceIsTheBundledInter4()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                // The desktop asks for Segoe UI Variable, which may not ship outside Windows; the phone
                // bundles Inter 4 in the weights the app draws (Normal, SemiBold, Bold).
                Assert.True(Avalonia.Controls.ResourceNodeExtensions.TryFindResource(
                    Avalonia.Application.Current!, "Font.Family", out var resource));
                var family = Assert.IsType<Avalonia.Media.FontFamily>(resource);
                Assert.Equal("Inter", family.Name);
                foreach (var (file, weight) in new[]
                         {
                             ("Inter-Regular.ttf", 400), ("Inter-SemiBold.ttf", 600), ("Inter-Bold.ttf", 700)
                         })
                {
                    using var stream = AssetLoader.Open(new Uri($"avares://Lumi.Mobile/Assets/Fonts/{file}"));
                    using var typeface = SKTypeface.FromStream(stream, 0);
                    Assert.NotNull(typeface);
                    // The SemiBold file names itself "Inter SemiBold" for old menus; its typographic family,
                    // which the app resolves by (below), is Inter.
                    Assert.StartsWith("Inter", typeface.FamilyName);
                    Assert.Equal(weight, typeface.FontWeight);
                    // Inter 4 is drawn on a 2048-unit em; the theme's own Inter 3 on 2816.
                    Assert.Equal(2048, typeface.UnitsPerEm);
                    foreach (var character in "AaZz09·—…’“”éüñ→←€")
                        Assert.NotEqual(0, typeface.GetGlyph(character));
                }

                // Each weight the app draws resolves to its own bundled face: never a fallback, the theme's
                // Inter 3 or a weight synthesised from Regular. Medium resolves to Regular, as Segoe UI's
                // missing Medium does on the desktop.
                foreach (var (requested, drawn) in new[]
                         {
                             (Avalonia.Media.FontWeight.Normal, Avalonia.Media.FontWeight.Normal),
                             (Avalonia.Media.FontWeight.Medium, Avalonia.Media.FontWeight.Normal),
                             (Avalonia.Media.FontWeight.SemiBold, Avalonia.Media.FontWeight.SemiBold),
                             (Avalonia.Media.FontWeight.Bold, Avalonia.Media.FontWeight.Bold)
                         })
                {
                    Assert.True(Avalonia.Media.FontManager.Current.TryGetGlyphTypeface(
                        new Avalonia.Media.Typeface(family, Avalonia.Media.FontStyle.Normal, requested), out var face));
                    Assert.StartsWith("Inter", face.FamilyName);
                    Assert.Equal(drawn, face.Weight);
                    Assert.Equal(2048, face.Metrics.DesignEmHeight);
                    Assert.Equal(Avalonia.Media.FontSimulations.None, face.FontSimulations);
                }

                // On the phone the root is the theme's view, not a styled window: text that no style
                // names (the conversation's selectable text) inherits the theme's family, which is
                // the bundled Inter 4 too rather than the theme's own Inter 3.
                Assert.True(Avalonia.Controls.ResourceNodeExtensions.TryFindResource(
                    Avalonia.Application.Current!, "ContentControlThemeFontFamily", out var rootFamily));
                Assert.Equal(family, Assert.IsType<Avalonia.Media.FontFamily>(rootFamily));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw failure;
    }

    [Fact]
    public async Task BundledHebrewFallbackContainsTheHebrewAlphabet()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri("avares://Lumi.Mobile/Assets/Fonts/NotoSansHebrew-Regular.ttf"));
                using var typeface = SKTypeface.FromStream(stream, 0);

                Assert.NotNull(typeface);
                Assert.Equal(400, typeface.FontWeight);
                foreach (var character in "אבגדהוזחטיכלמנסעפצקרשת")
                    Assert.NotEqual(0, typeface.GetGlyph(character));
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw failure;
    }

    [Fact]
    public async Task BundledEmojiFallbackContainsRepresentativeEmojiSequences()
    {
        using var session = HeadlessMobileSession.Start();
        Exception? failure = null;

        await session.Dispatch(() =>
        {
            try
            {
                using var stream = AssetLoader.Open(
                    new Uri("avares://Lumi.Mobile/Assets/Fonts/NotoColorEmoji-Regular.ttf"));
                using var typeface = SKTypeface.FromStream(stream, 0);

                Assert.NotNull(typeface);
                Assert.Equal(400, typeface.FontWeight);
                foreach (var codepoint in new[]
                         {
                             0x1F44B, // Waving hand
                             0x1F600, // Grinning face
                             0x2764,  // Heart
                             0x2600,  // Sun
                             0x1F3FB, // Skin tone modifier
                             0x1F1FA, // Regional indicator U
                             0x1F1F8, // Regional indicator S
                             0x200D,  // Zero-width joiner
                             0x20E3   // Combining enclosing keycap
                         })
                {
                    Assert.NotEqual(0, typeface.GetGlyph(codepoint));
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        }, CancellationToken.None);

        if (failure is not null)
            throw failure;
    }
}
