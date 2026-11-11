using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Lumi.Views.Controls;
using StrataTheme.Controls;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class ChatStyleScopeTests
{
    [Fact]
    public Task SurfaceStylesStayWithTheirOwnersIncludingTheDerivedTranscriptControl() => WithWindow((window, chat) =>
    {
        var unrelated = new Border { Classes = { "project-switcher-surface", "launchpad-card" } };
        var header = chat.FindControl<Panel>("ChatHeaderLayer")!;
        header.IsVisible = true;
        header.Children.Add(unrelated);
        Pump(window);
        Assert.Null(unrelated.Background);
        Assert.Null(unrelated.BorderBrush);
        Assert.NotNull(window.FindControl<Border>("UnreadPanel")!.Background);

        var turn = new TranscriptTurn("style-ownership");
        turn.Items.Add(new JobWakeItem(new ChatMessageViewModel(new ChatMessage
        {
            Role = "user", Author = "Lumi Job - Style check", Content = "A scheduled wake.",
        }), showTimestamps: false));
        var transcript = chat.FindControl<TranscriptItemsControl>("Transcript")!;
        transcript.ItemsSource = new[] { turn };
        Pump(window);
        foreach (var host in transcript.GetVisualDescendants().OfType<TranscriptTurnControl>())
            host.IsViewportManaged = false;
        Pump(window);
        var card = transcript.GetVisualDescendants().OfType<Border>()
            .Single(control => control.Classes.Contains("job-wake"));
        Assert.NotNull(card.Background);
        Assert.NotNull(card.BorderBrush);
        Assert.Equal(new CornerRadius(16), card.CornerRadius);
    });

    [Fact]
    public Task SetupCapsulesOnTheComposerKeepTheirStyles_AndTheChatsSetupOpensToSayWhatItStartsWith() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        // Hold the launchpad still, so these capsules stay the ones on screen.
        model.Launchpad.Deactivate();
        model.Launchpad.Setups.Clear();
        var setup = NewSetup(_ => { });
        model.Launchpad.Setups.Add(setup);
        model.Launchpad.HasSetups = true;
        Pump(window);

        var tiles = chat.FindControl<ItemsControl>("LaunchpadSetupTiles")!;
        var capsule = tiles.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("launchpad-setup"));
        var title = tiles.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Classes.Contains("launchpad-setup-title"));
        var meta = tiles.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Classes.Contains("launchpad-setup-meta"));
        var detail = tiles.GetVisualDescendants().OfType<Border>()
            .Single(control => control.Classes.Contains("launchpad-setup-detail"));
        var icon = tiles.GetVisualDescendants().OfType<PathIcon>()
            .Single(control => control.Classes.Contains("launchpad-setup-glyph") && control.IsVisible);
        Assert.Equal(new CornerRadius(19), capsule.CornerRadius);
        Assert.Equal(38, capsule.Height);
        Assert.Equal(13, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        Assert.Equal(12, meta.FontSize);
        Assert.Equal("GPT 6 Luna", meta.Text);
        Assert.Equal(14, icon.Width);

        // Only the chat's setup says what it starts with, in the accent; the others are their names, so the row stays light.
        Assert.Equal(0d, detail.GetBaseValue(Layoutable.MaxWidthProperty).Value);
        setup.IsActive = true;
        Pump(window);
        Assert.True(detail.GetBaseValue(Layoutable.MaxWidthProperty).Value > 100);
        Assert.Equal(1d, detail.GetBaseValue(Visual.OpacityProperty).Value);
        AssertBrush(chat, "Brush.AccentDefault", meta.Foreground);
        setup.IsActive = false;
        Pump(window);
        Assert.Equal(0d, detail.GetBaseValue(Layoutable.MaxWidthProperty).Value);

        // A variant (one project kept on two models) always says what it starts with, quietly until it's the chat's.
        var variant = NewSetup(_ => { }, title: "Lumi", detail: "Claude Opus 5.5", isVariant: true);
        model.Launchpad.Setups.Add(variant);
        Pump(window);
        var variantCapsule = tiles.GetVisualDescendants().OfType<Button>()
            .Single(button => button.Classes.Contains("launchpad-setup") && button.DataContext == variant);
        var variantDetail = variantCapsule.GetVisualDescendants().OfType<Border>()
            .Single(control => control.Classes.Contains("launchpad-setup-detail"));
        var variantMeta = variantDetail.GetVisualDescendants().OfType<TextBlock>().Single();
        Assert.Contains("variant", variantCapsule.Classes);
        Assert.True(variantDetail.GetBaseValue(Layoutable.MaxWidthProperty).Value > 100);
        AssertBrush(chat, "Brush.TextTertiary", variantMeta.Foreground);

        // Keep stays closed (no room, no focus) until Other shows a setup the row doesn't hold.
        var keep = chat.FindControl<Button>("LaunchpadKeepSetupButton")!;
        Assert.Equal(0d, keep.GetBaseValue(Layoutable.MaxWidthProperty).Value);
        Assert.False(keep.Focusable);
        var row = chat.FindControl<Control>("LaunchpadSetupRow")!;
        Assert.Equal(HorizontalAlignment.Center, row.HorizontalAlignment);

        // A setup's ⋯ shows on hover above its corner, whole: nothing between it and the rail's room clips it.
        window.MouseMove(Center(capsule));
        Pump(window);
        var more = capsule.GetVisualParent()!.GetVisualChildren().OfType<Button>()
            .Single(button => button.Classes.Contains("launchpad-setup-more"));
        Assert.True(more.IsEffectivelyVisible);
        var scroller = chat.FindControl<ScrollViewer>("LaunchpadSetupScroller")!;
        Assert.True(more.TranslatePoint(default, tiles)!.Value.Y >= 0, "The ⋯ rises above the setups' own clip");
        Assert.True(more.TranslatePoint(default, scroller)!.Value.Y >= 0, "The ⋯ rises above the rail's clip");
        window.MouseMove(new Point(0, 0));
        Pump(window);

        // However many setups the user keeps, the row is never wider than the composer: Just chat and Other hold its
        // ends and the setups between them scroll, faded where more lie, by the wheel or the arrows.
        for (var index = 0; index < 6; index++)
            model.Launchpad.Setups.Add(NewSetup(_ => { }, title: "Lumi", detail: $"A rather long model name {index}", isVariant: true));
        Pump(window);
        var composer = chat.FindControl<StackPanel>("ComposerContainer")!;
        var rail = chat.FindControl<Panel>("LaunchpadSetupRail")!;
        Assert.True(row.Bounds.Width <= composer.Bounds.Width + 0.5, $"{row.Bounds.Width} > {composer.Bounds.Width}");
        Assert.True(scroller.Extent.Width > scroller.Viewport.Width + 1);
        Assert.Contains("can-forward", rail.Classes);
        Assert.DoesNotContain("can-back", rail.Classes);
        Assert.NotNull(scroller.OpacityMask);
        Assert.True(chat.FindControl<Button>("LaunchpadJustChat")!.IsEffectivelyVisible);
        Assert.True(chat.FindControl<Button>("LaunchpadOtherButton")!.IsEffectivelyVisible);
        Assert.True(chat.FindControl<Button>("LaunchpadSetupsForward")!.IsVisible);
        Assert.False(chat.FindControl<Button>("LaunchpadSetupsBack")!.IsVisible);

        window.MouseWheel(Center(rail), new Vector(0, -1));
        Pump(window);
        Assert.True(scroller.Offset.X > 0);
        Assert.Contains("can-back", rail.Classes);
        Assert.True(chat.FindControl<Button>("LaunchpadSetupsBack")!.IsVisible);

        chat.FindControl<Button>("LaunchpadSetupsForward")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Assert.True(scroller.Offset.X > scroller.Viewport.Width / 2);
        chat.FindControl<Button>("LaunchpadSetupsBack")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        chat.FindControl<Button>("LaunchpadSetupsBack")!.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        Assert.Equal(0, scroller.Offset.X);
        Assert.DoesNotContain("can-back", rail.Classes);

        Point Center(Control control) => control.TranslatePoint(
            new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
    });

    [Fact]
    public Task TheOpenTabsPillPassesBeneathTheOtherTabs_AndSwitchingTabsMovesNothing() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        var tabs = chat.FindControl<ListBox>("LaunchpadCategories")!;
        Assert.True(StrataTheme.SelectionIndicatorAnimationHelper.GetMatchSize(tabs));
        var launchpad = model.Launchpad;
        launchpad.SelectedCategory = launchpad.Categories[1];
        Pump(window);

        // The open tab draws first, so the pill sliding in passes beneath the other tabs' words, and it has no hover
        // wash of its own: the pill is the only shape that moves.
        var items = tabs.GetVisualDescendants().OfType<ListBoxItem>().ToList();
        Assert.Equal(-1, items[1].ZIndex);
        Assert.All(items.Where(item => item != items[1]), item => Assert.Equal(0, item.ZIndex));
        Assert.False(Hover(items[1]).IsVisible);
        Assert.True(Hover(items[0]).IsVisible);

        // The starters keep room for three lines and the line under them, so a tab with fewer leaves the tabs in place.
        launchpad.Deactivate();
        var suggestions = launchpad.Suggestions;
        suggestions.Clear();
        suggestions.Add(new LaunchpadStarterItem(LaunchpadStarterCatalog.Find("plan.trip")!, _ => { }));
        Pump(window);
        var body = chat.FindControl<StackPanel>("LaunchpadIdeasBody")!;
        var tabsAt = tabs.TranslatePoint(default, window)!.Value;
        Assert.Equal(144, body.Bounds.Height, 0.5);
        foreach (var id in (string[])["plan.week", "plan.event"])
            suggestions.Add(new LaunchpadStarterItem(LaunchpadStarterCatalog.Find(id)!, _ => { }));
        Pump(window);
        Assert.Equal(tabsAt, tabs.TranslatePoint(default, window)!.Value);
        Assert.Equal(144, body.Bounds.Height, 0.5);

        static Border Hover(ListBoxItem item) => item.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "PART_Hover");
    });

    [Fact]
    public Task StarterCapsulesTakeTheAccentOnlyForTheBrief_AndAShortWindowKeepsFour() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        // Hold the launchpad still, so these capsules stay the ones on screen.
        model.Launchpad.Deactivate();
        var suggestions = model.Launchpad.Suggestions;
        suggestions.Clear();
        suggestions.Add(new LaunchpadStarterItem(LaunchpadStarterCatalog.Find("plan.trip")!, _ => { }));
        suggestions.Add(new LaunchpadStarterItem(
            new LaunchpadStarterSpec("🧭", "Publish 1.0.2", "Publish 1.0.2") { Reason = "From Lumi's brief" }, _ => { }));
        foreach (var id in (string[])["shop.choose", "learn.explain", "write.email", "docs.summarize"])
            suggestions.Add(new LaunchpadStarterItem(LaunchpadStarterCatalog.Find(id)!, _ => { }));
        Pump(window);

        var capsules = chat.FindControl<ItemsControl>("WelcomeSuggestions")!.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Classes.Contains("launchpad-idea")).ToList();
        Assert.Equal(6, capsules.Count);
        Assert.All(capsules, capsule => Assert.Equal(new CornerRadius(17), capsule.CornerRadius));

        // The ideas sit with the greeting, where Lumi's presence pools, not on the composer.
        Assert.Contains(chat.FindControl<StackPanel>("WelcomeGreeting")!,
            chat.FindControl<ItemsControl>("WelcomeSuggestions")!.GetVisualAncestors());

        // A plan from the brief wears the accent; any other starter's icon stays quiet until hovered.
        Assert.DoesNotContain("brief", capsules[0].Classes);
        AssertBrush(capsules[0], "Brush.TextTertiary", Icon(capsules[0]).Foreground);
        Assert.Contains("brief", capsules[1].Classes);
        AssertBrush(capsules[1], "Brush.AccentDefault", Icon(capsules[1]).Foreground);

        // A blank reads in the accent, and a capsule's words keep to one line.
        var trip = Label(capsules[0]);
        Assert.Equal("Plan a trip to where?", string.Concat(trip.Inlines!.OfType<Run>().Select(run => run.Text)));
        var blank = Assert.Single(trip.Inlines!.OfType<Run>(), run => run.Text == "where?");
        AssertBrush(capsules[0], "Brush.AccentDefault", blank.Foreground);
        Assert.Equal(TextWrapping.NoWrap, trip.TextWrapping);

        // A short window keeps four starters.
        chat.FindControl<Control>("LaunchpadIdeas")!.Classes.Add("compact");
        Pump(window);
        Assert.Equal(4, capsules.Count(capsule => capsule.IsEffectivelyVisible));
        chat.FindControl<Control>("LaunchpadIdeas")!.Classes.Remove("compact");
        Pump(window);

        // Resting on a starter says why For you offers it, on one quiet line under the starters; leaving clears it.
        var reason = chat.FindControl<TextBlock>("LaunchpadIdeaReason")!;
        window.MouseMove(Center(capsules[1]));
        Pump(window);
        Assert.Equal("From Lumi's brief", reason.Text);
        Assert.Contains("shown", reason.Classes);
        window.MouseMove(Center(capsules[0]));
        Pump(window);
        Assert.DoesNotContain("shown", reason.Classes);
        window.MouseMove(new Point(0, 0));
        Pump(window);
        Assert.DoesNotContain("shown", reason.Classes);

        Point Center(Control capsule) => capsule.TranslatePoint(
            new Point(capsule.Bounds.Width / 2, capsule.Bounds.Height / 2), window)!.Value;
        static StrataLineIcon Icon(Control capsule) => capsule.GetVisualDescendants().OfType<StrataLineIcon>().Single();
        static LaunchpadStarterLabel Label(Control capsule) => capsule.GetVisualDescendants().OfType<LaunchpadStarterLabel>().Single();
    });

    [Fact]
    public Task ASetupsMenuKeepsItChangesWhatItStartsWithOrStopsSuggestingIt() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        var calls = new List<string>();
        model.Launchpad.Setups.Add(NewSetup(calls.Add));
        model.Launchpad.HasSetups = true;
        Pump(window);

        var tile = chat.FindControl<ItemsControl>("LaunchpadSetupTiles")!;
        var chip = tile.GetVisualDescendants().OfType<Button>().Single(button => button.Classes.Contains("launchpad-setup"));
        var menu = Assert.IsType<ContextMenu>(chip.ContextMenu);
        menu.Open(chip);
        Pump(window);
        try
        {
            var items = menu.Items.OfType<MenuItem>().ToList();
            Assert.Equal(["Keep on new chats", "Starts with", "Start in a worktree", "Don't suggest this"],
                items.Select(item => item.Header as string));
            Assert.All(items, item => Assert.True(item.IsVisible));
            var models = Assert.IsAssignableFrom<IEnumerable<LaunchpadModelChoice>>(items[1].ItemsSource).ToList();
            Assert.Equal(["GPT 6 Luna", "GPT 6 Sol"], models.Select(choice => choice.Title));
            Assert.True(models[0].IsCurrent);

            items[0].Command!.Execute(null);
            models[1].SelectCommand.Execute(null);
            items[2].Command!.Execute(null);
            items[3].Command!.Execute(null);
            Assert.Equal(["keep", "gpt-6-sol", "worktree:True", "hide"], calls);
        }
        finally
        {
            menu.Close();
        }
    });

    private static LaunchpadSetupItem NewSetup(Action<string> calls, string title = "Daily work", string detail = "GPT 6 Luna",
        bool isVariant = false)
    {
        LaunchpadSetupItem? setup = null;
        setup = new LaunchpadSetupItem(
            new LaunchpadSetupChoice(new LaunchpadSetupSpec(null, null, false, "gpt-6-luna", null), title, "Project and agent"),
            detail, isVariant, null, StrataIcons.Branch, null, canUseWorktree: true,
            [
                new LaunchpadModelChoice("gpt-6-luna", "GPT 6 Luna", true, () => setup!.StartWith("gpt-6-luna")),
                new LaunchpadModelChoice("gpt-6-sol", "GPT 6 Sol", false, () => setup!.StartWith("gpt-6-sol")),
            ],
            new LaunchpadSetupActions(
                _ => calls("select"),
                _ => calls("keep"),
                _ => calls("hide"),
                (_, modelId) => calls(modelId),
                (_, worktree) => calls($"worktree:{worktree}")));
        return setup;
    }

    [Theory]
    [InlineData("HeaderGitChangesButton")]
    [InlineData("ComposerGitChangesButton")]
    [InlineData("GitMenu")]
    public Task GitStatsKeepTheirColorsOutsideTheTranscript(string surface) => WithWindow((window, chat) =>
    {
        if (surface != "GitMenu")
        {
            if (surface == "ComposerGitChangesButton")
            {
                chat.FindControl<Border>("ComposerGitChip")!.IsVisible = true;
                chat.FindControl<StrataChatComposer>("Composer")!.ProjectName = "Project";
                Pump(window);
            }
            var button = chat.FindControl<Button>(surface)!;
            button.ApplyTemplate();
            Pump(window);
            AssertStats(Assert.IsType<Panel>(button.Content));
            return;
        }

        var header = chat.FindControl<Panel>("ChatHeaderLayer")!;
        header.IsVisible = true;
        var menu = new MenuFlyoutPresenter
        {
            Theme = Assert.IsType<ControlTheme>(chat.Resources["ChatGitCardPresenter"]),
            DataContext = chat.DataContext,
        };
        header.Children.Add(menu);
        menu.ApplyTemplate();
        Pump(window);
        AssertStats(menu);

        void AssertStats(Control root)
        {
            var added = root.GetVisualDescendants().OfType<TextBlock>()
                .Single(control => control.Classes.Contains("diff-added"));
            var removed = root.GetVisualDescendants().OfType<TextBlock>()
                .Single(control => control.Classes.Contains("diff-removed"));
            AssertBrush(chat, "Brush.SuccessDefault", added.Foreground);
            AssertBrush(chat, "Brush.DangerDefault", removed.Foreground);
        }
    });

    private static async Task WithWindow(Action<MainWindow, ChatView> check)
    {
        using var session = HeadlessTestSession.Start();
        await session.Dispatch(() =>
        {
            Loc.Load("en");
            var settings = new UserSettings
            {
                IsOnboarded = true, AutoSaveChats = false, EnableMemoryAutoSave = false,
                EnableDailyBriefs = false, AllowAppSetupSuggestions = false, ShowAnimations = false,
            };
            using var main = new MainViewModel(new DataStore(new AppData { Settings = settings }),
                TestCopilot.Shared, new UpdateService(), startBackgroundJobs: false, initializeCopilotOnStartup: false);
            var window = new MainWindow { DataContext = main, Width = 1100, Height = 820 };
            window.Show();
            try
            {
                Pump(window);
                check(window, window.GetVisualDescendants().OfType<ChatView>().Single());
            }
            finally { window.Close(); }
        }, CancellationToken.None);
    }

    private static void AssertBrush(Control control, string key, IBrush? actual)
    {
        Assert.True(control.TryFindResource(key, control.ActualThemeVariant, out var expected));
        Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(expected).Color,
            Assert.IsAssignableFrom<ISolidColorBrush>(actual).Color);
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
