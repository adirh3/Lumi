using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using StrataTheme.Animation;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

// The search palette must keep a stable width while the user types: result rows with long titles used to
// widen the card and short results shrank it again, so the overlay visibly jittered on every keystroke.
[Collection("Headless UI")]
public sealed class SearchOverlayLayoutTests
{
    private const string LongTitle =
        "Document conversion pipeline with a very long descriptive chat title that keeps going and going";

    [Fact]
    public async Task PaletteWidth_StaysFixed_WhileResultsChange()
    {
        using var session = HeadlessTestSession.Start();

        double emptyWidth = -1;
        double shortResultWidth = -1;
        double longResultWidth = -1;
        int shortResultCount = 0;
        int longResultCount = 0;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            var card = FindCard(overlay);
            emptyWidth = card.Bounds.Width;

            vm.SearchQuery = "docker";
            shortResultCount = await WaitForResultsAsync(vm);
            await PumpAsync();
            shortResultWidth = card.Bounds.Width;

            vm.SearchQuery = "document";
            longResultCount = await WaitForResultsAsync(vm);
            await PumpAsync();
            longResultWidth = card.Bounds.Width;

            window.Close();
        }, CancellationToken.None);

        Assert.True(shortResultCount > 0, "The short query should produce results.");
        Assert.True(longResultCount > 0, "The long-title query should produce results.");
        Assert.Equal(640d, emptyWidth, 3);
        Assert.Equal(emptyWidth, shortResultWidth, 3);
        Assert.Equal(emptyWidth, longResultWidth, 3);
    }

    [Fact]
    public async Task PaletteWidth_ShrinksWithinNarrowWindow()
    {
        using var session = HeadlessTestSession.Start();

        double width = -1;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 500, Height = 700, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            width = FindCard(overlay).Bounds.Width;

            window.Close();
        }, CancellationToken.None);

        // 500px window minus the palette's 24px side margins.
        Assert.Equal(452d, width, 3);
    }

    [Fact]
    public async Task ResultRows_OptOutOfSharedButtonChrome_OnHover()
    {
        using var session = HeadlessTestSession.Start();

        bool auroraVisible = true;
        bool pointerOver = false;
        object? hoverBrush = null;
        object? expectedHover = null;
        object? selectedBrush = null;
        object? expectedSelected = null;
        int surfaceShadowCount = -1;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            vm.SearchQuery = "doc";
            await WaitForResultsAsync(vm);
            await PumpAsync();

            var rows = overlay.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("search-result-item"))
                .ToList();

            // Row 0 is keyboard-selected, so hover row 1 to observe the plain hover treatment.
            var row = rows[1];
            var center = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window);
            window.MouseMove(center!.Value);
            await PumpAsync();

            pointerOver = row.IsPointerOver;
            auroraVisible = FindTemplateBorder(row, "PART_Aurora")!.IsVisible;

            var surface = FindTemplateBorder(row, "PART_Surface")!;
            surfaceShadowCount = surface.BoxShadow.Count;
            hoverBrush = surface.Background;
            selectedBrush = FindTemplateBorder(rows[0], "PART_Surface")!.Background;
            overlay.TryFindResource("Search.RowHover", overlay.ActualThemeVariant, out expectedHover);
            overlay.TryFindResource("Search.RowSelected", overlay.ActualThemeVariant, out expectedSelected);

            window.Close();
        }, CancellationToken.None);

        Assert.True(pointerOver, "The pointer should hover the second result row.");
        Assert.False(auroraVisible, "Palette rows must not show the shared button accent line.");
        Assert.Equal(0, surfaceShadowCount);
        // Hover is a neutral wash and the keyboard selection the Strata wash, so the two never read alike.
        Assert.IsAssignableFrom<ISolidColorBrush>(expectedHover);
        Assert.Same(expectedHover, hoverBrush);
        Assert.IsAssignableFrom<IGradientBrush>(expectedSelected);
        Assert.Same(expectedSelected, selectedBrush);
    }

    // Only the palette is glass: it frosts what lies beneath it and lets that show through its translucent
    // fill, while the rest of Lumi stays exactly as it is (and a click there still closes the palette). The
    // Strata line under the field stays quiet while it is empty and comes up once there is a query.
    [Fact]
    public async Task Palette_FrostsOnlyItsOwnGlass_AndBrightensTheStrataLineWhileTyping()
    {
        using var session = HeadlessTestSession.Start();

        int frostCount = -1;
        bool frostBacksTheCard = false;
        double cardFrost = -1;
        double fillOpacity = -1;
        bool windowUntinted = false;
        bool lineActiveWhenEmpty = true;
        bool lineActiveWithQuery = false;
        bool closedByOutsideClick = false;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            var frosts = overlay.GetVisualDescendants().OfType<StrataBackdropBlur>().ToList();
            frostCount = frosts.Count;
            if (frosts.Count == 1)
            {
                frostBacksTheCard = frosts[0].GetVisualParent() is Panel { Name: "CardHost" };
                cardFrost = frosts[0].BlurRadius;
            }

            if (FindCard(overlay).Background is ISolidColorBrush fill)
                fillOpacity = fill.Opacity * fill.Color.A / 255d;

            windowUntinted = overlay.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Name == "Scrim")
                .Background is ISolidColorBrush { Color.A: 0 };

            var line = overlay.GetVisualDescendants()
                .OfType<Border>()
                .Single(border => border.Name == "StratumLine");
            lineActiveWhenEmpty = line.Classes.Contains("active");

            vm.SearchQuery = "docker";
            await PumpAsync();
            lineActiveWithQuery = line.Classes.Contains("active");

            // Below the palette, where only the untinted window shows.
            window.MouseDown(new Point(600, 760), MouseButton.Left);
            window.MouseUp(new Point(600, 760), MouseButton.Left);
            await PumpAsync();
            closedByOutsideClick = !vm.IsOpen;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(1, frostCount);
        Assert.True(frostBacksTheCard, "The only frost should sit behind the palette's glass, not over the window.");
        Assert.True(cardFrost > 0, "The palette's glass should frost what lies beneath it.");
        Assert.InRange(fillOpacity, 0.01, 0.75);
        Assert.True(windowUntinted, "Lumi should stay as it is behind the palette.");
        Assert.True(closedByOutsideClick, "Clicking outside the palette should still close it.");
        Assert.False(lineActiveWhenEmpty, "The Strata line should stay quiet while the field is empty.");
        Assert.True(lineActiveWithQuery, "The Strata line should come up once there is a query.");
    }

    // Results and filter chips wear their category's hue, so a project reads as a project at a glance.
    [Fact]
    public async Task ResultTilesAndFilterChips_WearTheirCategoryHue()
    {
        using var session = HeadlessTestSession.Start();

        object? chatTileHue = null;
        object? projectTileHue = null;
        object? projectChipHue = null;
        object? expectedChatHue = null;
        object? expectedProjectHue = null;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateMixedAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            vm.SearchQuery = "atlas";
            await WaitForCategoriesAsync(vm, GlobalSearchCategory.Chats, GlobalSearchCategory.Projects);
            await PumpAsync();

            var tiles = overlay.GetVisualDescendants()
                .OfType<Panel>()
                .Where(panel => panel.Classes.Contains("result-tile"))
                .ToList();
            chatTileHue = tiles
                .First(tile => SearchHue.GetCategory(tile) == GlobalSearchCategory.Chats)
                .GetValue(TextElement.ForegroundProperty);
            projectTileHue = tiles
                .First(tile => SearchHue.GetCategory(tile) == GlobalSearchCategory.Projects)
                .GetValue(TextElement.ForegroundProperty);
            projectChipHue = overlay.GetVisualDescendants()
                .OfType<StrataLineIcon>()
                .First(icon => icon.Classes.Contains("chip-icon")
                               && SearchHue.GetCategory(icon) == GlobalSearchCategory.Projects)
                .Foreground;

            overlay.TryFindResource("Search.Hue.Chats", overlay.ActualThemeVariant, out expectedChatHue);
            overlay.TryFindResource("Search.Hue.Projects", overlay.ActualThemeVariant, out expectedProjectHue);

            window.Close();
        }, CancellationToken.None);

        Assert.NotNull(expectedChatHue);
        Assert.NotNull(expectedProjectHue);
        Assert.NotSame(expectedChatHue, expectedProjectHue);
        Assert.Same(expectedChatHue, chatTileHue);
        Assert.Same(expectedProjectHue, projectTileHue);
        Assert.Same(expectedProjectHue, projectChipHue);
    }

    [Fact]
    public async Task ClickingAFilterChip_NarrowsResultsToItsCategory()
    {
        using var session = HeadlessTestSession.Start();

        GlobalSearchCategory? selectedCategory = null;
        bool onlyProjects = false;
        string? selectedChip = null;
        bool inputKeptFocus = false;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateMixedAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            var input = overlay.GetVisualDescendants()
                .OfType<TextBox>()
                .Single(control => control.Name == "SearchOverlayInput");
            input.Focus();

            vm.SearchQuery = "atlas";
            await WaitForCategoriesAsync(vm, GlobalSearchCategory.Chats, GlobalSearchCategory.Projects);
            await PumpAsync();

            var chip = overlay.GetVisualDescendants()
                .OfType<Button>()
                .First(button => button.Classes.Contains("search-chip")
                                 && button.Tag is SearchCategoryFilterItem { CategoryKey: GlobalSearchCategory.Projects });
            var center = chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value;
            window.MouseDown(center, MouseButton.Left);
            window.MouseUp(center, MouseButton.Left);
            await PumpAsync();

            selectedCategory = vm.SelectedCategory;
            onlyProjects = vm.FlatResults.Count > 0
                           && vm.FlatResults.All(result => result.CategoryKey == GlobalSearchCategory.Projects);
            selectedChip = overlay.GetVisualDescendants()
                .OfType<Button>()
                .Where(button => button.Classes.Contains("search-chip") && button.Classes.Contains("selected"))
                .Select(button => (button.Tag as SearchCategoryFilterItem)?.Label)
                .SingleOrDefault();
            inputKeptFocus = input.IsFocused;

            window.Close();
        }, CancellationToken.None);

        Assert.Equal(GlobalSearchCategory.Projects, selectedCategory);
        Assert.True(onlyProjects, "Only projects should remain once the Projects chip is chosen.");
        Assert.Equal("Projects", selectedChip);
        Assert.True(inputKeptFocus, "Choosing a chip should leave the caret in the search field.");
    }

    // The indeterminate progress line used to collapse to a ~12px stub parked at the left edge, because the
    // theme never bound the indicator width or animated the sweep. It read as a stuck sliver, not as loading.
    // The sweep itself is a compositor animation, so its on-screen travel is not observable through the
    // headless client-side visual; what is assertable here is the geometry plus the start/stop lifecycle.
    [Fact]
    public async Task LoadingIndicator_FillsTheHeaderAndSweepsOnlyWhileVisible()
    {
        using var session = HeadlessTestSession.Start();

        double barWidth = -1;
        double barHeight = -1;
        double indicatorWidth = -1;
        double indicatorTargetWidth = -1;
        bool indicatorWidthIsBound = false;
        bool trackIsInvisible = false;
        bool sweepRunsWhileVisible = false;
        bool sweepStopsWhenHidden = true;

        await session.Dispatch(async () =>
        {
            Loc.Load("en");

            var vm = CreateViewModel(CreateAppData());
            var overlay = new SearchOverlay { DataContext = vm };
            var window = new Window { Width = 1200, Height = 800, Content = overlay };
            window.Show();

            vm.IsOpen = true;
            await PumpAsync();

            vm.IsSearchIndicatorVisible = true;
            await PumpAsync();

            var bar = overlay.GetVisualDescendants()
                .OfType<ProgressBar>()
                .Single(progressBar => progressBar.IsIndeterminate);

            var indicator = bar.GetVisualDescendants()
                .OfType<Border>()
                .First(border => border.Name == "PART_Indicator");

            // The theme binds the indicator width through TemplateSettings and disables the determinate
            // width transition while indeterminate, so a single render pass settles the final geometry.
            Tick(ticks: 1);

            barWidth = bar.Bounds.Width;
            barHeight = bar.Bounds.Height;
            trackIsInvisible = bar.Background is null or ISolidColorBrush { Color.A: 0 };
            indicatorWidth = indicator.Bounds.Width;
            indicatorTargetWidth = bar.TemplateSettings.ContainerWidth;
            indicatorWidthIsBound = !double.IsNaN(indicator.Width);
            sweepRunsWhileVisible = LifecycleOffsetSweep.IsRunning(indicator);

            vm.IsSearchIndicatorVisible = false;
            await PumpAsync();
            Tick(ticks: 3);
            sweepStopsWhenHidden = !LifecycleOffsetSweep.IsRunning(indicator);

            window.Close();
        }, CancellationToken.None);

        Assert.True(barWidth > 400, $"The progress line should span the palette header, was {barWidth}.");
        Assert.Equal(2d, barHeight, 3);
        Assert.True(trackIsInvisible, "The progress track must not double the header divider.");
        Assert.True(indicatorWidthIsBound, "The theme must give the indeterminate indicator an explicit width.");
        Assert.InRange(indicatorWidth, barWidth * 0.2, barWidth * 0.8);
        Assert.InRange(indicatorWidth, indicatorTargetWidth - 1.5, indicatorTargetWidth + 1.5);
        Assert.True(sweepRunsWhileVisible, "The indeterminate sweep should run while the progress line shows.");
        Assert.True(sweepStopsWhenHidden, "The sweep must stop once the progress line is hidden.");
    }

    private static void Tick(int ticks)
    {
        for (var index = 0; index < ticks; index++)
        {
            try
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            }
            catch
            {
                // Render timer not available on this platform variant; the dispatcher pump still advances.
            }

            Dispatcher.UIThread.RunJobs();
        }
    }

    private static Border? FindTemplateBorder(Button row, string name)
        => row.GetVisualDescendants().OfType<Border>().FirstOrDefault(border => border.Name == name);

    private static async Task WaitForCategoriesAsync(SearchOverlayViewModel vm, params GlobalSearchCategory[] categories)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await PumpAsync();
            if (categories.All(category => vm.FlatResults.Any(result => result.CategoryKey == category)))
                return;

            await Task.Delay(25);
        }
    }

    private static async Task<int> WaitForResultsAsync(SearchOverlayViewModel vm)
    {
        for (var attempt = 0; attempt < 60; attempt++)
        {
            await PumpAsync();
            if (vm.ResultGroups.Count > 0)
                return vm.ResultCount;

            await Task.Delay(25);
        }

        return vm.ResultCount;
    }

    private static Border FindCard(SearchOverlay overlay)
        => overlay.GetVisualDescendants()
            .OfType<Border>()
            .Single(border => border.Name == "OverlayCard");

    private static async Task PumpAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Input);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
    }

    private static AppData CreateAppData()
    {
        var now = DateTimeOffset.Now;
        return new AppData
        {
            Chats =
            [
                new Chat { Title = "Docker", UpdatedAt = now },
                new Chat { Title = LongTitle, UpdatedAt = now.AddMinutes(-1) }
            ]
        };
    }

    private static AppData CreateMixedAppData()
    {
        var now = DateTimeOffset.Now;
        return new AppData
        {
            Chats =
            [
                new Chat { Title = "Atlas launch notes", UpdatedAt = now },
                new Chat { Title = "Atlas retro", UpdatedAt = now.AddMinutes(-3) }
            ],
            Projects =
            [
                new Project { Name = "Atlas" }
            ]
        };
    }

    private static SearchOverlayViewModel CreateViewModel(AppData appData)
    {
        var service = new GlobalSearchService(
            () => appData,
            chatContent: null);
        return new SearchOverlayViewModel(service, () => 0);
    }
}
