using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
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
    public Task TheStartersRowKeepsItsStyles_NeverOutgrowsTheComposer_AndGathersTheRestIntoItsShelf() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        var launchpad = model.Launchpad;
        var setups = chat.FindControl<StackPanel>("LaunchpadSetups")!;
        var row = chat.FindControl<StackPanel>("LaunchpadSetupRow")!;
        var tiles = chat.FindControl<ItemsControl>("LaunchpadSetupTiles")!;
        // Kept starters first, in the user's order, then the places Lumi learned, Just chat among them.
        Assert.Equal(["UI dev", "Lumi"], launchpad.Setups.Where(setup => setup.IsKept).Select(setup => setup.Title));
        Assert.Contains(launchpad.Setups, setup => setup.IsJustChat);

        var uiDev = Capsule(tiles, launchpad.Setups[0]);
        Assert.Equal(new CornerRadius(19), uiDev.CornerRadius);
        Assert.Equal(38, uiDev.Height);
        var title = uiDev.GetVisualDescendants().OfType<TextBlock>().Single(text => text.Classes.Contains("launchpad-setup-title"));
        Assert.Equal("UI dev", title.Text);
        Assert.Equal(13, title.FontSize);
        Assert.Equal(FontWeight.SemiBold, title.FontWeight);
        // The icon its user gave it is drawn as Strata's line icons are.
        Assert.True(uiDev.GetVisualDescendants().OfType<StrataLineIcon>()
            .Single(icon => icon.Classes.Contains("launchpad-badge-line")).IsEffectivelyVisible);

        // The kept Lumi starts in a worktree and the Lumi Lumi learned doesn't: a branch on its badge, and ⑂ after its name.
        var keptLumi = Capsule(tiles, launchpad.Setups[1]);
        Assert.True(keptLumi.GetVisualDescendants().OfType<Border>()
            .Single(branch => branch.Classes.Contains("launchpad-setup-branch")).IsEffectivelyVisible);
        Assert.True(keptLumi.GetVisualDescendants().OfType<StrataLineIcon>()
            .Single(icon => icon.Classes.Contains("launchpad-setup-tell-branch")).IsEffectivelyVisible);
        // The draft starts in Lumi's folder: the place Lumi learned there is lit, on the row beside the kept starters.
        var learnedLumi = launchpad.Setups.Single(setup => setup is { IsKept: false, Title: "Lumi" });
        model.SetProjectId(learnedLumi.Spec.ProjectId!.Value);
        Pump(window);
        Assert.Same(learnedLumi, launchpad.ActiveSetup);
        Assert.True(learnedLumi.IsInRow);
        Assert.True(learnedLumi.HasDividerBefore, "A hairline sets the learned starters apart from the kept ones");
        Assert.Single(launchpad.Setups, setup => setup.HasDividerBefore);
        Assert.False(Capsule(tiles, learnedLumi).GetVisualDescendants().OfType<StrataLineIcon>()
            .Single(icon => icon.Classes.Contains("launchpad-setup-tell-branch")).IsVisible);

        // A starter's ⋯ shows on hover above its corner, whole: the row's clip leaves it room, and nothing between it and
        // that clip cuts it (an items control clips by default).
        window.MouseMove(Center(window, uiDev));
        Pump(window);
        var more = MoreOf(uiDev);
        Assert.True(more.IsEffectivelyVisible);
        Assert.True(more.TranslatePoint(default, setups)!.Value.Y >= 0, "The ⋯ rises above the row's clip");
        Assert.Empty(ClippingBetween(more, setups));
        window.MouseMove(new Point(0, 0));
        Pump(window);
        Assert.False(more.IsVisible);

        // However many starters there are, the row is never wider than the composer: the rest gather into the stack,
        // which takes the place of the row's +.
        Assert.True(launchpad.HasOverflow);
        Assert.True(row.Bounds.Width <= setups.Bounds.Width + 0.5, $"{row.Bounds.Width} > {setups.Bounds.Width}");
        var overflowed = launchpad.Setups.Where(setup => !setup.IsInRow).ToList();
        Assert.Equal(launchpad.OverflowCount, overflowed.Count);
        Assert.Equal("+" + overflowed.Count, launchpad.OverflowLabel);
        var stack = chat.FindControl<Button>("LaunchpadStackButton")!;
        Assert.True(stack.IsEffectivelyVisible);
        Assert.False(chat.FindControl<Button>("LaunchpadNewStarterButton")!.IsVisible);
        // Its badges overlap, the last one past the end of their panel, and show whole.
        var lastBadge = stack.GetVisualDescendants().OfType<Border>().Last(badge => badge.Classes.Contains("launchpad-stack-badge"));
        Assert.Empty(ClippingBetween(lastBadge, stack));

        // The stack's shelf unfolds them in order, then +, no wider than the row.
        var shelf = chat.FindControl<Popup>("LaunchpadShelf")!;
        var shelfClosed = 0;
        shelf.Closed += (_, _) => shelfClosed++;
        stack.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Pump(window);
        try
        {
            Assert.True(shelf.IsOpen);
            Assert.Contains("open", stack.Classes);
            Assert.Equal(overflowed.Cast<object>().Append(LaunchpadNewStarterTile.Instance), launchpad.ShelfItems);
            Assert.Equal(setups.Bounds.Width, chat.FindControl<Border>("LaunchpadShelfRoot")!.MaxWidth);
            Assert.Equal(overflowed.Count + 1, chat.FindControl<ItemsControl>("LaunchpadShelfTiles")!
                .GetVisualDescendants().OfType<Button>().Count(button => button.Classes.Contains("launchpad-setup")));
        }
        finally
        {
            // The shelf fades out before it closes.
            shelf.IsOpen = false;
            RenderUntil(window, () => shelfClosed > 0);
        }

        Assert.Equal(1, shelfClosed);
        Assert.DoesNotContain("open", stack.Classes);

        // A learned starter lit from the stack comes onto the row only in a learned one's place: the kept starters never
        // leave the row for it, and while it has no room there the stack wears its light.
        var far = launchpad.Setups.Last(setup => setup is { IsKept: false, IsInRow: false, IsJustChat: false });
        model.SetProjectId(far.Spec.ProjectId!.Value);
        Pump(window);
        Assert.Same(far, launchpad.ActiveSetup);
        Assert.All(launchpad.Setups.Where(static setup => setup.IsKept), setup => Assert.True(setup.IsInRow));
        Assert.Equal(!far.IsInRow, stack.Classes.Contains("active"));
    }, SeedStarters);

    [Fact]
    public Task AStartersCard_OpensOnARightClickOrItsMore_ForALearnedOrAKeptStarter() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        var launchpad = model.Launchpad;
        var tiles = chat.FindControl<ItemsControl>("LaunchpadSetupTiles")!;
        var card = Assert.IsAssignableFrom<PopupFlyoutBase>(FlyoutBase.GetAttachedFlyout(chat.FindControl<Control>("LaunchpadSetupRow")!));
        try
        {
            // A learned starter's card offers to keep, rename or stop suggesting it. (Lumi, the first place learned, is on the row.)
            var learned = launchpad.Setups.Single(setup => setup is { IsKept: false, Title: "Lumi" });
            model.SetProjectId(learned.Spec.ProjectId!.Value);
            Pump(window);
            Assert.True(learned.IsInRow);
            var point = Center(window, Capsule(tiles, learned));
            window.MouseDown(point, MouseButton.Right);
            window.MouseUp(point, MouseButton.Right);
            Pump(window);
            Assert.True(card.IsOpen);
            Assert.Same(learned, launchpad.Editor.Target);
            Assert.True(chat.FindControl<Button>("LaunchpadStarterKeepButton")!.IsEffectivelyVisible);
            Assert.True(chat.FindControl<Button>("LaunchpadStarterRenameButton")!.IsEffectivelyVisible);
            Assert.True(chat.FindControl<Button>("LaunchpadStarterHideButton")!.IsEffectivelyVisible);
            Assert.False(chat.FindControl<TextBox>("LaunchpadStarterName")!.IsEffectivelyVisible);
            var cardContent = chat.FindControl<Control>("LaunchpadStarterCard")!;
            card.Hide();
            RenderUntil(window, () => TopLevel.GetTopLevel(cardContent) is null);
            Assert.Null(launchpad.Editor.Target);

            // A kept one's ⋯ opens its card: its name, then what it starts with, the default model first.
            var uiDev = Capsule(tiles, launchpad.Setups[0]);
            window.MouseMove(Center(window, uiDev));
            Pump(window);
            var moreButton = MoreOf(uiDev);
            var more = Center(window, moreButton);
            window.MouseDown(more, MouseButton.Left);
            window.MouseUp(more, MouseButton.Left);
            Pump(window);
            Assert.True(card.IsOpen, $"⋯ visible: {moreButton.IsEffectivelyVisible}, at {more}, bounds {moreButton.Bounds}; "
                + $"card target: {launchpad.Editor.Target?.Title ?? "none"}; old card attached: {TopLevel.GetTopLevel(cardContent) is not null}");
            Assert.Same(launchpad.Setups[0], launchpad.Editor.Target);
            var name = chat.FindControl<TextBox>("LaunchpadStarterName")!;
            Assert.True(name.IsEffectivelyVisible);
            Assert.Equal("UI dev", name.Text);
            Assert.False(chat.FindControl<Button>("LaunchpadStarterKeepButton")!.IsEffectivelyVisible);
            var models = chat.FindControl<ItemsControl>("LaunchpadStarterModels")!;
            var defaultModel = Assert.IsType<LaunchpadModelChoice>(models.Items[0]);
            Assert.Null(defaultModel.ModelId);
            Assert.Equal(Loc.Launchpad_StarterDefaultModel, defaultModel.Title);
        }
        finally
        {
            card.Hide();
            Pump(window);
        }

        Assert.Null(launchpad.Editor.Target);
    }, SeedStarters);

    [Fact]
    public Task TheNewStarterCard_IsTheStartersCard_OpenedFromThePlusAboveTheRowsEnd() => WithWindow((window, chat) =>
    {
        var launchpad = Assert.IsType<ChatViewModel>(chat.DataContext).Launchpad;
        var row = chat.FindControl<StackPanel>("LaunchpadSetupRow")!;
        var card = Assert.IsAssignableFrom<PopupFlyoutBase>(FlyoutBase.GetAttachedFlyout(row));
        var plus = chat.FindControl<Button>("LaunchpadNewStarterButton")!;
        Assert.True(plus.IsEffectivelyVisible);
        // The window focuses the composer a moment after it opens; that settles before the + is used, as it would for a user.
        var input = chat.GetVisualDescendants().OfType<TextBox>().First(static box => box.Name == "PART_Input");
        RenderUntil(window, () => input.IsFocused);
        try
        {
            var point = Center(window, plus);
            window.MouseDown(point, MouseButton.Left);
            window.MouseUp(point, MouseButton.Left);
            Pump(window);
            Assert.True(card.IsOpen);
            Assert.True(launchpad.Editor.IsNew);

            // A kept starter's form, its name first, with its place to choose and Add where Remove would be.
            var name = chat.FindControl<TextBox>("LaunchpadStarterName")!;
            Assert.True(name.IsEffectivelyVisible);
            RenderUntil(window, () => name.IsFocused);
            Assert.True(name.IsFocused);
            Assert.Equal("", name.Text);
            Assert.Equal("Just chat", name.PlaceholderText);
            var place = chat.FindControl<Button>("LaunchpadStarterPlaceButton")!;
            Assert.True(place.IsEffectivelyVisible);
            Assert.True(chat.FindControl<Button>("LaunchpadAddStarterButton")!.IsEffectivelyVisible);
            Assert.True(chat.FindControl<ItemsControl>("LaunchpadStarterModels")!.IsEffectivelyVisible);
            Assert.False(chat.FindControl<Button>("LaunchpadStarterRemoveButton")!.IsEffectivelyVisible);
            Assert.False(chat.FindControl<Button>("LaunchpadStarterKeepButton")!.IsEffectivelyVisible);

            // It hangs above the row's end, its rim in line with the +.
            var content = chat.FindControl<Control>("LaunchpadStarterCard")!;
            var cardRight = content.PointToScreen(new Point(content.Bounds.Width, 0)).X;
            var plusRight = plus.PointToScreen(new Point(plus.Bounds.Width, 0)).X;
            Assert.InRange(cardRight - plusRight, -8, 8);

            // Starts in opens its places in place of the form, the search first; Escape there goes back to the form.
            place.Command!.Execute(null);
            Pump(window);
            var search = chat.FindControl<TextBox>("LaunchpadPlaceSearch")!;
            Assert.True(search.IsEffectivelyVisible);
            RenderUntil(window, () => search.IsFocused);
            Assert.True(search.IsFocused);
            Assert.False(name.IsEffectivelyVisible);
            Assert.Equal(["Lumi"], launchpad.Editor.PlaceProjects.Select(static item => item.Title));
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = search });
            Pump(window);
            Assert.True(card.IsOpen);
            Assert.False(launchpad.Editor.IsChoosingPlace);
            Assert.True(name.IsEffectivelyVisible);
        }
        finally
        {
            card.Hide();
            Pump(window);
        }

        Assert.False(launchpad.Editor.IsNew);
    }, data =>
    {
        var lumi = new Project { Name = "Lumi" };
        data.Projects.Add(lumi);
        for (var use = 0; use < 3; use++)
        {
            data.Chats.Add(new Chat
            {
                Title = $"Lumi {use}", ProjectId = lumi.Id, MessageCount = 6,
                CreatedAt = DateTimeOffset.Now.AddDays(-1 - use), UpdatedAt = DateTimeOffset.Now.AddDays(-1 - use),
            });
        }
    });

    /// <summary>
    /// A profile with more starters than the composer holds: two kept ones (UI dev, on Opus with an icon of its own, and
    /// Lumi in a worktree), then the places the user started chats in this week.
    /// </summary>
    private static void SeedStarters(AppData data)
    {
        string[] names = ["Lumi", "Fluent Search", "Research notes", "Trips and travel", "Home automation", "Reading list"];
        var projects = names.Select(name => new Project { Name = name }).ToList();
        data.Projects.AddRange(projects);
        for (var index = 0; index < projects.Count; index++)
        {
            for (var use = 0; use < 7 - index; use++)
            {
                data.Chats.Add(new Chat
                {
                    Title = $"{projects[index].Name} {use}",
                    ProjectId = projects[index].Id,
                    MessageCount = 6,
                    CreatedAt = DateTimeOffset.Now.AddDays(-1 - use),
                    UpdatedAt = DateTimeOffset.Now.AddDays(-1 - use),
                });
            }
        }

        data.PinnedSetups.Add(new LaunchpadSetupPin
        {
            ProjectId = projects[0].Id, UseWorktree = true, ModelId = "claude-opus-5.5", Name = "UI dev", Icon = "layers",
        });
        data.PinnedSetups.Add(new LaunchpadSetupPin { ProjectId = projects[0].Id, UseWorktree = true });
    }

    private static Button Capsule(ItemsControl tiles, LaunchpadSetupItem setup) => tiles.GetVisualDescendants().OfType<Button>()
        .Single(button => button.Classes.Contains("launchpad-setup") && ReferenceEquals(button.DataContext, setup));

    private static Button MoreOf(Button capsule) => capsule.GetVisualParent()!.GetVisualChildren().OfType<Button>()
        .Single(button => button.Classes.Contains("launchpad-setup-more"));

    /// <summary>The visuals between <paramref name="control"/> and <paramref name="owner"/> that would cut it at their bounds.</summary>
    private static List<string> ClippingBetween(Visual control, Visual owner) => control.GetVisualAncestors()
        .TakeWhile(ancestor => !ReferenceEquals(ancestor, owner))
        .Where(static ancestor => ancestor.ClipToBounds)
        .Select(static ancestor => ancestor.GetType().Name + (ancestor is Control { Name: { } name } ? "#" + name : ""))
        .ToList();

    private static Point Center(Window window, Control control) => control.TranslatePoint(
        new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;

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

    private static async Task WithWindow(Action<MainWindow, ChatView> check, Action<AppData>? seed = null)
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
            var data = new AppData { Settings = settings };
            seed?.Invoke(data);
            using var main = new MainViewModel(new DataStore(data),
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

    /// <summary>Renders frames, so animations (a popup's fade-out, say) run, until <paramref name="done"/> or three seconds.</summary>
    private static void RenderUntil(Window window, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!done() && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(16);
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Pump(window);
        }
    }

    private static void Pump(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }
}
