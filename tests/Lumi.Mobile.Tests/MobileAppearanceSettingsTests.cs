using System.Runtime.ExceptionServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.Appearance;
using Lumi.Mobile.Layout;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Mobile.Tests;

/// <summary>
/// Settings → Appearance makes Lumi the user's own on the phone: the theme, the desktop's accent palettes,
/// Lumi's text size and its glow, each remembered, shown at once and put back by Restore default look.
/// </summary>
[Collection("Headless mobile UI")]
public sealed class MobileAppearanceSettingsTests
{
    [Fact]
    public async Task TheLookIsRememberedAndRestoreLeavesTheLanguageAlone()
    {
        using var session = HeadlessMobileSession.Start();
        var store = session.NewStore();
        await using (var shell = new MobileShellViewModel(store: store, post: action => action()))
        {
            Assert.False(shell.IsAppearanceCustomized);
            Assert.Equal("System · Lumi", shell.AppearanceSummary);
            Assert.Equal(new[] { "lumi" }, shell.AccentChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id));

            shell.SelectThemeCommand.Execute("Dark");
            shell.AccentChoices.Single(choice => choice.Id == "sunset").SelectCommand.Execute(null);
            shell.TextSizeChoices.Single(choice => choice.Percent == 125).SelectCommand.Execute(null);
            shell.SelectGlowCommand.Execute("Vivid");
            shell.AnimateGlowWhileWorking = true;

            Assert.True(shell.IsAppearanceCustomized);
            Assert.Equal("Dark · Sunset", shell.AppearanceSummary);
            Assert.Equal(new[] { "sunset" }, shell.AccentChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id));
            Assert.Equal(new[] { 125 }, shell.TextSizeChoices.Where(choice => choice.IsSelected).Select(choice => choice.Percent));
            Assert.Equal("Larger", shell.TextSizeLabel);
            Assert.Equal("Vivid", shell.GlowLabel);
            Assert.Equal(1.45, shell.GlowMultiplier);
        }

        await using (var reopened = new MobileShellViewModel(store: store, post: action => action()))
        {
            Assert.Equal(MobileShellViewModel.ThemePreference.Dark, reopened.Theme);
            Assert.Equal("sunset", reopened.AccentColor);
            Assert.Equal(125, reopened.TextSize);
            Assert.Equal(MobileShellViewModel.GlowPreference.Vivid, reopened.Glow);
            Assert.True(reopened.AnimateGlowWhileWorking);
            Assert.Equal(new[] { "sunset" }, reopened.AccentChoices.Where(choice => choice.IsSelected).Select(choice => choice.Id));

            reopened.SelectLanguageCommand.Execute("de");
            reopened.RestoreDefaultLookCommand.Execute(null);

            Assert.False(reopened.IsAppearanceCustomized);
            Assert.Equal(MobileShellViewModel.ThemePreference.System, reopened.Theme);
            Assert.Equal(MobileAccentPalettes.DefaultId, reopened.AccentColor);
            Assert.Equal(MobileShellViewModel.DefaultTextSize, reopened.TextSize);
            Assert.Equal(MobileShellViewModel.GlowPreference.Balanced, reopened.Glow);
            Assert.False(reopened.AnimateGlowWhileWorking);
            // The language is not a look.
            Assert.Equal("de", reopened.Language);
        }

        var saved = store.Load();
        Assert.Equal(("System", "lumi", 100, "Balanced", false, "de"),
            (saved.Theme, saved.AccentColor, saved.TextSize, saved.Glow, saved.AnimateGlowWhileWorking, saved.Language));
    }

    [Fact]
    public async Task ALookAnotherBuildWroteFallsBackToLumisOwn()
    {
        using var session = HeadlessMobileSession.Start();
        var store = session.NewStore();
        var settings = store.Load();
        settings.AccentColor = "neon";
        settings.TextSize = 117;
        settings.Glow = "7";
        store.Save(settings);

        await using var shell = new MobileShellViewModel(store: store, post: action => action());

        Assert.Equal(MobileAccentPalettes.DefaultId, shell.AccentColor);
        Assert.Equal(MobileShellViewModel.DefaultTextSize, shell.TextSize);
        Assert.Equal(MobileShellViewModel.GlowPreference.Balanced, shell.Glow);
        Assert.False(shell.IsAppearanceCustomized);
        Assert.Single(shell.AccentChoices, choice => choice.IsSelected);
        Assert.Single(shell.TextSizeChoices, choice => choice.IsSelected);
    }

    /// <summary>
    /// A palette is one merged dictionary, keyed by theme, that recolours the desktop's accent tokens and the
    /// phone's own tints; Lumi's own palette writes nothing, so its tuned indigo comes back exactly.
    /// </summary>
    [Fact]
    public Task AnAccentRecoloursTheAppAndLumisOwnWritesNothing() => RunAsync(async session =>
    {
        var application = Application.Current!;
        var resources = application.Resources;
        var dictionaries = resources.MergedDictionaries.Count;
        var tunedBubble = Resolve<ISolidColorBrush>(application, "Brush.ChatUserSurface", ThemeVariant.Dark).Color;
        var tunedSelection = Resolve<ISolidColorBrush>(application, "Brush.NavigationSelected", ThemeVariant.Light).Color;
        await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
        try
        {
            shell.AccentChoices.Single(choice => choice.Id == "ocean").SelectCommand.Execute(null);
            App.ApplyAppearance(application, shell);

            var ocean = MobileAccentPalettes.Find("ocean");
            Assert.Equal("ocean", MobileAccentPalettes.AppliedId(resources));
            Assert.Equal(ocean.Dark.Accent, Resolve<Color>(application, "Color.AccentDefault", ThemeVariant.Dark));
            Assert.Equal(ocean.Light.Accent, Resolve<Color>(application, "Color.AccentDefault", ThemeVariant.Light));
            Assert.Equal(ocean.Dark.Accent, Resolve<ISolidColorBrush>(application, "Brush.AccentDefault", ThemeVariant.Dark).Color);
            // The glow's colours, which the presence reads.
            Assert.Equal(ocean.Dark.Secondary, Resolve<Color>(application, "Color.AccentViolet", ThemeVariant.Dark));
            Assert.Equal(ocean.Dark.Tertiary, Resolve<Color>(application, "Color.AccentRose", ThemeVariant.Dark));
            // The phone's tints stay tints, now of the new accent.
            var selection = Resolve<ISolidColorBrush>(application, "Brush.NavigationSelected", ThemeVariant.Light).Color;
            Assert.Equal((ocean.Light.Accent.R, ocean.Light.Accent.G, ocean.Light.Accent.B), (selection.R, selection.G, selection.B));
            Assert.Equal(tunedSelection.A, selection.A);
            Assert.NotEqual(tunedBubble, Resolve<ISolidColorBrush>(application, "Brush.ChatUserSurface", ThemeVariant.Dark).Color);
            Assert.Equal(ocean.Light.Bubble, Resolve<ISolidColorBrush>(application, "Brush.ChatUserSurface", ThemeVariant.Light).Color);

            // Another palette replaces the first instead of stacking on it.
            shell.AccentChoices.Single(choice => choice.Id == "sunset").SelectCommand.Execute(null);
            App.ApplyAppearance(application, shell);
            Assert.Equal(dictionaries + 1, resources.MergedDictionaries.Count);
            Assert.Equal(MobileAccentPalettes.Find("sunset").Dark.Accent,
                Resolve<Color>(application, "Color.AccentDefault", ThemeVariant.Dark));

            shell.RestoreDefaultLookCommand.Execute(null);
            App.ApplyAppearance(application, shell);
            Assert.Equal(dictionaries, resources.MergedDictionaries.Count);
            Assert.Equal(tunedBubble, Resolve<ISolidColorBrush>(application, "Brush.ChatUserSurface", ThemeVariant.Dark).Color);
            Assert.Equal(tunedSelection, Resolve<ISolidColorBrush>(application, "Brush.NavigationSelected", ThemeVariant.Light).Color);
        }
        finally
        {
            MobileAccentPalettes.Apply(resources, MobileAccentPalettes.DefaultId);
        }
    });

    /// <summary>
    /// Lumi's text size multiplies every rung on top of the phone's own font size, keeps the line heights in
    /// proportion, survives the phone's font size changing underneath it, and the defaults remove every restatement.
    /// </summary>
    [Fact]
    public Task TextSizeScalesEveryRungOnTopOfThePhonesFontSize() => RunAsync(async session =>
    {
        var application = Application.Current!;
        double Size(string key) => Assert.IsType<double>(application.FindResource(key));
        await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action => action());
        try
        {
            Assert.Equal(16, Size("Font.SizeBody"));
            shell.TextSizeChoices.Single(choice => choice.Percent == 125).SelectCommand.Execute(null);
            App.ApplyAppearance(application, shell);
            Assert.Equal(20, Size("Font.SizeBody"));
            Assert.Equal(16.25, Size("Font.SizeCaption"));
            Assert.Equal(27.5, Size("Font.SizeTitle"));
            Assert.Equal(26.6, Size("Font.LineHeightBody"));

            // The phone's font size changes underneath: the two multiply instead of one replacing the other.
            MobileTypeScale.Apply(application, size => size * 1.1);
            Assert.Equal(22, Size("Font.SizeBody"));

            shell.TextSizeChoices.Single(choice => choice.Percent == 100).SelectCommand.Execute(null);
            App.ApplyAppearance(application, shell);
            Assert.Equal(17.6, Size("Font.SizeBody"));

            MobileTypeScale.Apply(application, size => size);
            Assert.Equal(16, Size("Font.SizeBody"));
            Assert.DoesNotContain(MobileTypeScale.Keys, key => application.Resources.ContainsKey(key));
            Assert.DoesNotContain(MobileTypeScale.LineHeightKeys, pair => application.Resources.ContainsKey(pair.LineHeight));
        }
        finally
        {
            MobileTypeScale.ApplyTextScale(application, 1);
            MobileTypeScale.Apply(application, size => size);
        }
    });

    /// <summary>
    /// The chat's one presence follows the glow: brighter or quieter by the desktop's multipliers, gone when off,
    /// and moving while Lumi works only when asked to and only while it can be seen.
    /// </summary>
    [Fact]
    public Task TheChatsGlowFollowsTheSetting() => RunUiAsync(412, 892, async (shell, view, window) =>
    {
        shell.IsNavigationOpen = false;
        await SettleAsync(window);
        Assert.True(shell.IsChatPage);
        Assert.False(shell.IsNavigationCoveringContent);
        var presence = view.GetVisualDescendants().OfType<StrataPresence>().Single();
        Assert.True(presence.IsVisible);
        Assert.Equal(0.48, presence.Intensity, 3);
        Assert.False(presence.AnimateWhileWorking);

        shell.SelectGlowCommand.Execute("Subtle");
        Pump(window);
        Assert.Equal(0.48 * 0.6, presence.Intensity, 3);

        shell.SelectGlowCommand.Execute("Vivid");
        shell.AnimateGlowWhileWorking = true;
        Pump(window);
        Assert.Equal(0.48 * 1.45, presence.Intensity, 3);
        Assert.True(presence.AnimateWhileWorking);

        // Strata's drift loops until stopped, so it rests while the chat is out of sight and resumes on return.
        shell.ShowPageCommand.Execute("Settings");
        await SettleAsync(window);
        Assert.False(presence.AnimateWhileWorking);
        shell.ShowPageCommand.Execute("Chat");
        await SettleAsync(window);
        Assert.True(presence.AnimateWhileWorking);

        shell.SelectGlowCommand.Execute("Off");
        Pump(window);
        Assert.False(presence.IsVisible);
        Assert.False(presence.AnimateWhileWorking);
        Assert.True(shell.AnimateGlowWhileWorking);

        shell.SelectGlowCommand.Execute("Balanced");
        Pump(window);
        Assert.True(presence.IsVisible);
        Assert.Equal(0.48, presence.Intensity, 3);
        Assert.True(presence.AnimateWhileWorking);
        // The shell keeps exactly one presence: the Appearance preview draws its glow instead.
        Assert.Single(view.GetVisualDescendants().OfType<StrataPresence>());
    });

    [Fact]
    public Task TheAppearancePageOffersEveryChoiceAtTouchSize() => RunUiAsync(412, 1800, async (shell, view, window) =>
    {
        shell.ShowPageCommand.Execute("Settings");
        shell.OpenSettingsSectionCommand.Execute("Appearance");
        await SettleAsync(window);
        var settings = Required<MobileSettingsView>(view, "SettingsPage");

        // The look, previewed in the chat's own bubble at the body rung.
        var preview = Required<Border>(settings, "AppearancePreview");
        Assert.True(preview.IsEffectivelyVisible);
        Assert.Equal(16, Required<Border>(settings, "PreviewUserBubble").GetVisualDescendants().OfType<TextBlock>().Single().FontSize);

        var swatches = Buttons(Required<ItemsControl>(settings, "AccentSwatches"));
        Assert.Equal(8, swatches.Count);
        Assert.All(swatches, swatch => Assert.True(swatch.Bounds is { Width: >= 48, Height: >= 48 }, $"A swatch is {swatch.Bounds.Size}."));
        // Two rows of four on a phone.
        Assert.Equal(4, Assert.IsType<UniformGrid>(Required<ItemsControl>(settings, "AccentSwatches").ItemsPanelRoot).Columns);
        Tap(window, swatches[1]);
        Assert.Equal("ocean", shell.AccentColor);
        Assert.Equal("Ocean", Required<TextBlock>(settings, "AccentNameText").Text);
        Assert.Equal(new[] { 1 }, swatches.Select((swatch, index) => (swatch, index))
            .Where(pair => pair.swatch.Classes.Contains("selected")).Select(pair => pair.index));

        var sizes = Buttons(Required<ItemsControl>(settings, "TextSizeOptions"));
        Assert.Equal(5, sizes.Count);
        Assert.All(sizes, size => Assert.True(size.Bounds.Height >= 48, $"A text size is {size.Bounds.Height} tall."));
        Tap(window, sizes[3]);
        Assert.Equal(125, shell.TextSize);
        Assert.Equal("Larger", Required<TextBlock>(settings, "TextSizeText").Text);

        var animate = Required<Grid>(settings, "AnimateGlowRow");
        Assert.True(animate.IsEffectivelyEnabled);
        Tap(window, Required<Button>(settings, "GlowOffButton"));
        Assert.Equal(MobileShellViewModel.GlowPreference.Off, shell.Glow);
        Assert.False(animate.IsEffectivelyEnabled);
        Assert.False(Required<GlowPreview>(settings, "AppearancePreviewGlow").IsVisible);

        var restore = Required<Button>(settings, "RestoreDefaultLookButton");
        Assert.True(restore.IsEffectivelyVisible);
        Tap(window, restore);
        Assert.False(shell.IsAppearanceCustomized);
        Assert.False(restore.IsVisible);
        Assert.True(Required<GlowPreview>(settings, "AppearancePreviewGlow").IsVisible);
    });

    /// <summary>Beside the list (OpenCanvasDesignTests checks the two panes share one canvas), the eight palettes fit one row.</summary>
    [Fact]
    public Task WideAppearancePageKeepsEveryPaletteInOneRow() => RunUiAsync(984, 1092, async (shell, view, window) =>
    {
        shell.ShowPageCommand.Execute("Settings");
        await SettleAsync(window);
        Assert.True(shell.IsTwoPane);
        var settings = Required<MobileSettingsView>(view, "SettingsPage");
        Assert.True(Required<Grid>(settings, "SettingsListPane").IsEffectivelyVisible);
        Assert.True(Required<Grid>(settings, "SettingsDetailPane").IsEffectivelyVisible);
        Assert.Equal(8, Assert.IsType<UniformGrid>(Required<ItemsControl>(settings, "AccentSwatches").ItemsPanelRoot).Columns);
    });

    private static List<Button> Buttons(ItemsControl items) =>
        items.GetVisualDescendants().OfType<Button>().ToList();

    private static T Resolve<T>(Application application, string key, ThemeVariant variant)
    {
        Assert.True(application.TryGetResource(key, variant, out var value), $"{key} is missing.");
        return Assert.IsAssignableFrom<T>(value);
    }

    private static async Task RunAsync(Func<HeadlessMobileSession, Task> body)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            try
            {
                await body(session);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static async Task RunUiAsync(
        double width, double height, Func<MobileShellViewModel, MobileShellView, Window, Task> test)
    {
        using var session = HeadlessMobileSession.Start();
        ExceptionDispatchInfo? failure = null;
        await session.Dispatch(async () =>
        {
            await using var shell = new MobileShellViewModel(store: session.NewStore(), post: action =>
            {
                if (Dispatcher.UIThread.CheckAccess())
                    action();
                else
                    Dispatcher.UIThread.Post(action);
            });
            shell.IsPaired = shell.IsConnected = shell.IsHostReady = true;
            shell.HostName = "Preview PC";
            shell.UserName = "Adir";
            shell.Chat.Reset(Guid.NewGuid(), "Appearance check");
            var view = new MobileShellView { DataContext = shell };
            var window = new Window { Width = width, Height = height, Content = view };
            try
            {
                window.Show();
                Pump(window);
                await test(shell, view, window);
            }
            catch (Exception ex)
            {
                failure = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
        failure?.Throw();
    }

    private static T Required<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static void Pump(Window window)
    {
        window.InvalidateMeasure();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Lets a page's entrance and bindings finish, so what is tapped is where it rests.</summary>
    private static async Task SettleAsync(Window window)
    {
        for (var pass = 0; pass < 12; pass++)
        {
            window.UpdateLayout();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(30);
        }

        Pump(window);
    }

    private static void Tap(Window window, Button button)
    {
        var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window);
        Assert.NotNull(center);
        var hit = Assert.IsAssignableFrom<Visual>(window.InputHitTest(center.Value));
        Assert.True(ReferenceEquals(hit, button) || hit.GetVisualAncestors().Contains(button),
            $"{button.Name ?? "A choice"} at {center} is covered by {hit.GetType().Name} {(hit as Control)?.Name}.");
        window.MouseDown(center.Value, MouseButton.Left);
        window.MouseUp(center.Value, MouseButton.Left);
        Pump(window);
    }
}
