using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Appearance;
using Lumi.ViewModels;
using Lumi.Views;
using SkiaSharp;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class AppIconArtTests
{
    // Points of a 256 px icon: the tile, Lumi's sparkle, yours, the model's dot, and two points of Lumi
    // in the same row, one under your sparkle and one away from it.
    private static readonly SKPointI Tile = new(30, 128);
    private static readonly SKPointI LumisSparkle = new(110, 150);
    private static readonly SKPointI YourSparkle = new(180, 76);
    private static readonly SKPointI Model = new(65, 191);
    private static readonly SKPointI UnderYou = new(177, 117);
    private static readonly SKPointI LumiBesideYou = new(100, 117);

    [Fact]
    public void Icon_IsLumiBetweenYouAndTheModel_WithYouFloatingAboveLumi()
    {
        using var icon = AppIconArt.Render(AppIconArt.Lumi, 256);

        Assert.Equal(0, icon.GetPixel(2, 2).Alpha);
        var tile = Pixel(icon, Tile);
        Assert.Equal(255, tile.Alpha);
        Assert.True(tile.Blue > tile.Green + 60, $"Lumi's tile is indigo-violet, not {tile}.");
        AssertNearWhite(Pixel(icon, LumisSparkle));
        AssertNearWhite(Pixel(icon, Model));
        AssertColor(AppIconArt.Lumi.You, Pixel(icon, YourSparkle));
        Assert.True(Brightness(Pixel(icon, LumiBesideYou)) - Brightness(Pixel(icon, UnderYou)) > 20,
            $"Your sparkle casts a shadow on Lumi: {Pixel(icon, UnderYou)} against {Pixel(icon, LumiBesideYou)}.");
    }

    [Fact]
    public void Icon_WearsTheAccentPalette()
    {
        Assert.Same(AppIconArt.Lumi, AppIconArt.ColorsFor(AccentPalettes.Find(AccentPalettes.DefaultId)));

        var forest = AppIconArt.ColorsFor(AccentPalettes.Find("forest"));
        using var icon = AppIconArt.Render(forest, 256);

        var tile = Pixel(icon, Tile);
        Assert.True(tile.Green > tile.Red && tile.Green > tile.Blue, $"A forest tile is green, not {tile}.");
        AssertNearWhite(Pixel(icon, LumisSparkle));
        var you = Pixel(icon, YourSparkle);
        AssertColor(forest.You, you);
        Assert.True(you.Green > you.Blue, $"Your sparkle takes a pale tint of the palette, not {you}.");
    }

    [Fact]
    public async Task TrayIcon_HasAFrameDrawnForEachDisplayScale()
    {
        int[] sizes = [16, 20, 24, 32, 40, 48];
        var ico = AppIconArt.CreateIco(AppIconArt.Lumi, sizes);

        Assert.Equal(1, BitConverter.ToUInt16(ico, 2));
        Assert.Equal(sizes.Length, BitConverter.ToUInt16(ico, 4));
        for (var i = 0; i < sizes.Length; i++)
        {
            var entry = 6 + 16 * i;
            Assert.Equal(sizes[i], ico[entry]);
            using var frame = SKBitmap.Decode(ico.AsSpan(BitConverter.ToInt32(ico, entry + 12), BitConverter.ToInt32(ico, entry + 8)));
            Assert.Equal(sizes[i], frame.Width);
            Assert.Equal(sizes[i], frame.Height);
        }

        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            var forest = AccentPalettes.Find("forest");
            Assert.NotNull(AppIcon.CreateTrayIcon(forest, isWindows: true));
            Assert.NotNull(AppIcon.CreateTrayIcon(forest, isWindows: false));
        }, CancellationToken.None);
    }

    [Fact]
    public async Task AccentPalette_RedrawsLumisMarkOnlyWhenItChanges()
    {
        using var session = HeadlessTestSession.Start();
        var marks = new List<object?>();

        await session.Dispatch(() =>
        {
            var resources = new ResourceDictionary();
            foreach (var id in new[] { AccentPalettes.DefaultId, AccentPalettes.DefaultId, "forest", "forest", AccentPalettes.DefaultId })
            {
                AccentPalettes.Apply(resources, id);
                marks.Add(resources[AppIcon.MarkResourceKey]);
            }
        }, CancellationToken.None);

        Assert.IsType<Bitmap>(marks[0]);
        Assert.Same(marks[0], marks[1]);
        Assert.NotSame(marks[1], marks[2]);
        Assert.Same(marks[2], marks[3]);
        Assert.NotSame(marks[3], marks[4]);
    }

    [Fact]
    public async Task LoadingWindow_ShowsLumisMarkInTheChosenAccent()
    {
        using var session = HeadlessTestSession.Start();
        object? shown = null, expected = null, shownAfterChange = null, expectedAfterChange = null;

        await session.Dispatch(() =>
        {
            var resources = Application.Current!.Resources;
            AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            var window = new StartupWindow();
            try
            {
                window.Show();
                var image = window.GetLogicalDescendants().OfType<Image>().Single();
                shown = image.Source;
                expected = resources[AppIcon.MarkResourceKey];

                AccentPalettes.Apply(resources, "ocean");
                Dispatcher.UIThread.RunJobs();
                shownAfterChange = image.Source;
                expectedAfterChange = resources[AppIcon.MarkResourceKey];
            }
            finally
            {
                window.Close();
                AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            }
        }, CancellationToken.None);

        Assert.NotNull(shown);
        Assert.Same(expected, shown);
        Assert.Same(expectedAfterChange, shownAfterChange);
        Assert.NotSame(shown, shownAfterChange);
    }

    [Fact]
    public async Task AboutPage_ShowsLumisMarkInTheChosenAccent()
    {
        using var session = HeadlessTestSession.Start();
        object? shown = null, expected = null, shownAfterChange = null, expectedAfterChange = null;

        await session.Dispatch(() =>
        {
            var resources = Application.Current!.Resources;
            AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            using var viewModel = new SettingsViewModel(
                new DataStore(new AppData()), TestCopilot.Shared, new BrowserService(), new UpdateService(), new FakeSecureKeyStore());
            viewModel.SelectedPageIndex = SettingsViewModel.AboutPageIndex;
            using var view = new SettingsView { DataContext = viewModel };
            var window = new Window { Content = view, Width = 1000, Height = 800 };
            try
            {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var icon = view.FindSettingsControl<Image>("AboutAppIcon")!;
                shown = icon.Source;
                expected = resources[AppIcon.MarkResourceKey];

                AccentPalettes.Apply(resources, "sunset");
                Dispatcher.UIThread.RunJobs();
                shownAfterChange = icon.Source;
                expectedAfterChange = resources[AppIcon.MarkResourceKey];
            }
            finally
            {
                window.Close();
                AccentPalettes.Apply(resources, AccentPalettes.DefaultId);
            }
        }, CancellationToken.None);

        Assert.NotNull(shown);
        Assert.Same(expected, shown);
        Assert.Same(expectedAfterChange, shownAfterChange);
        Assert.NotSame(shown, shownAfterChange);
    }

    private static SKColor Pixel(SKBitmap bitmap, SKPointI point) => bitmap.GetPixel(point.X, point.Y);

    private static int Brightness(SKColor color) => (color.Red + color.Green + color.Blue) / 3;

    private static void AssertColor(SKColor expected, SKColor actual)
        => Assert.True(
            Math.Abs(expected.Red - actual.Red) <= 3 && Math.Abs(expected.Green - actual.Green) <= 3
            && Math.Abs(expected.Blue - actual.Blue) <= 3,
            $"Expected {expected}, not {actual}.");

    private static void AssertNearWhite(SKColor color)
        => Assert.True(color.Red >= 225 && color.Green >= 225 && color.Blue >= 225, $"Expected white, not {color}.");
}
