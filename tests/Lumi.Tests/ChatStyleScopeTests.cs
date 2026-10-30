using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
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
    public Task SetupTilesAboveTheComposerKeepTheirStylesAndActiveState() => WithWindow((window, chat) =>
    {
        var model = Assert.IsType<ChatViewModel>(chat.DataContext);
        var setup = new LaunchpadSetupItem(new LaunchpadSetupSpec(null, null, false, "gpt-6-luna", null),
            "Daily work", "Project and agent", null, StrataIcons.Branch, null, _ => { });
        model.Launchpad.Setups.Add(setup);
        model.Launchpad.HasSetups = true;
        Pump(window);

        var root = chat.FindControl<StackPanel>("LaunchpadSetups")!;
        var title = root.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Classes.Contains("launchpad-setup-title"));
        var meta = root.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Classes.Contains("launchpad-setup-meta"));
        var icon = root.GetVisualDescendants().OfType<PathIcon>()
            .Single(control => control.Classes.Contains("launchpad-setup-glyph"));
        var label = root.GetVisualDescendants().OfType<TextBlock>()
            .Single(control => control.Classes.Contains("launchpad-section-label"));
        Assert.Equal(12.5, title.FontSize);
        Assert.Equal(11, meta.FontSize);
        Assert.Equal(15, icon.Width);
        Assert.Equal(11.5, label.FontSize);
        AssertBrush(chat, "Brush.TextTertiary", meta.Foreground);
        setup.IsActive = true;
        Pump(window);
        AssertBrush(chat, "Brush.AccentDefault", meta.Foreground);
        setup.IsActive = false;
        Pump(window);
        AssertBrush(chat, "Brush.TextTertiary", meta.Foreground);
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
