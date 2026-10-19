using System.Diagnostics;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.ViewModels;
using Lumi.Views;
using Xunit;

namespace Lumi.Tests;

[Collection("Headless UI")]
public sealed class MainWindowProjectBadgeTests
{
    [Theory]
    [InlineData("Lumi", null, false)]
    [InlineData("Fluent Search", null, false)]
    [InlineData("A project name that is much too long to fit inside the chats sidebar", null, true)]
    [InlineData("A project name that is much too long to fit inside the chats sidebar",
        "An important tag with a long name", true)]
    public async Task ProjectChipFitsItsLabelWithinTheSidebar(
        string projectName, string? tagName, bool shouldTrim)
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var project = new Project { Name = projectName };
            var tag = tagName is null ? null : new ChatTag { Name = tagName };
            var chat = new Chat
            {
                Title = "Project chip layout",
                ProjectId = project.Id,
                TagId = tag?.Id,
            };
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                },
                Projects = [project],
                Chats = [chat],
            };
            if (tag is not null)
                data.ChatTags.Add(tag);

            using var viewModel = new MainViewModel(
                new DataStore(data),
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                initializeCopilotOnStartup: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820,
            };

            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();

                Assert.Equal(TextRenderingMode.Unspecified,
                    TextOptions.GetTextRenderingMode(window.FindControl<Panel>("SidebarChat")!));
                Assert.Equal(TextRenderingMode.Unspecified,
                    TextOptions.GetTextRenderingMode(window.FindControl<Panel>("SidebarJobs")!));
                Assert.Equal(TextRenderingMode.Antialias,
                    TextOptions.GetTextRenderingMode(window.FindControl<Border>("SidebarBorder")!));
                Assert.Equal(TextRenderingMode.Unspecified, TextOptions.GetTextRenderingMode(window));

                var row = window.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Single(item => ReferenceEquals(item.DataContext, chat));
                var projectBadge = row.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "ProjectBadge");
                var label = AssertLabelFits(projectBadge);

                Assert.Equal(projectName, label.Text);
                Assert.Equal(projectName, ToolTip.GetTip(projectBadge));
                Assert.Equal(shouldTrim, label.TextLayout.TextLines.Any(line => line.HasCollapsed));

                var folder = projectBadge.GetVisualDescendants().OfType<PathIcon>().Single();
                var folderBrush = Assert.IsAssignableFrom<ISolidColorBrush>(folder.Foreground);
                Assert.Equal(LaunchpadProjectBadge.HueFor(project.Id), folderBrush.Color);

                var badgeOffset = projectBadge.TranslatePoint(default, row);
                Assert.NotNull(badgeOffset);
                Assert.True(badgeOffset.Value.X + projectBadge.Bounds.Width
                    <= row.Bounds.Width - row.Padding.Right + 1);

                // The project leads the line so its folder lines up from row to row; a tag follows it.
                Assert.InRange(projectBadge.Bounds.Left, 0, 1);
                var tagBadge = row.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "ChatTagBadge");
                Assert.Equal(tag is not null, tagBadge.IsVisible);
                if (tag is not null)
                {
                    var tagLabel = AssertLabelFits(tagBadge);
                    Assert.Contains(tagLabel.TextLayout.TextLines, line => line.HasCollapsed);
                    Assert.True(projectBadge.Bounds.Right <= tagBadge.Bounds.Left);
                    var tagOffset = tagBadge.TranslatePoint(default, row);
                    Assert.NotNull(tagOffset);
                    Assert.True(tagOffset.Value.X + tagBadge.Bounds.Width
                        <= row.Bounds.Width - row.Padding.Right + 1,
                        "The tag must trim inside the row instead of running past it.");
                }

                var surface = row.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "LayoutRoot");
                surface.Transitions = null;
                row.IsSelected = true;
                if (viewModel.OpenChatCommand.ExecutionTask is { } opening)
                    await opening;
                Assert.True(window.TryFindResource("Brush.SubtlePressed", window.ActualThemeVariant, out var selection));
                Assert.Equal(Assert.IsAssignableFrom<ISolidColorBrush>(selection).Color,
                    Assert.IsAssignableFrom<ISolidColorBrush>(surface.Background).Color);
                var marker = row.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "PART_SelectedPipe");
                Assert.True(marker.IsVisible);
                Assert.Equal(1, marker.Opacity);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    [Fact]
    public async Task RetitlingATaggedChatTypesTheTitleNotTheTag()
    {
        using var session = HeadlessTestSession.Start();

        await session.Dispatch(async () =>
        {
            Loc.Load("en");
            var tag = new ChatTag { Name = "Bug" };
            var chat = new Chat { Title = "Untitled chat", TagId = tag.Id };
            var data = new AppData
            {
                Settings = new UserSettings
                {
                    IsOnboarded = true,
                    AutoSaveChats = false,
                    EnableMemoryAutoSave = false,
                },
                Chats = [chat],
            };
            data.ChatTags.Add(tag);

            using var viewModel = new MainViewModel(
                new DataStore(data),
                TestCopilot.Shared,
                new UpdateService(),
                startBackgroundJobs: false,
                initializeCopilotOnStartup: false);
            var window = new MainWindow
            {
                DataContext = viewModel,
                Width = 1100,
                Height = 820,
            };

            window.Show();
            try
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                window.UpdateLayout();
                var row = window.GetVisualDescendants()
                    .OfType<ListBoxItem>()
                    .Single(item => ReferenceEquals(item.DataContext, chat));
                var title = row.GetVisualDescendants().OfType<TextBlock>()
                    .Single(text => text.Name == "ChatTitleText");
                var tagLabel = row.GetVisualDescendants().OfType<Border>()
                    .Single(border => border.Name == "ChatTagBadge")
                    .GetVisualDescendants().OfType<TextBlock>().Single();

                // A generated title is typed into the sidebar row, 30 ms a character. The binding shows the
                // whole title at once, so only a partial value proves the typing reached the title.
                var shown = new List<string?>();
                title.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TextBlock.TextProperty)
                        shown.Add(e.NewValue as string);
                };
                bool TypedIntoTitle() => shown.Any(text =>
                    text is { Length: > 0 and < 7 } && "Renamed".StartsWith(text, StringComparison.Ordinal));

                typeof(ChatViewModel)
                    .GetMethod("ApplyChatTitle", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(viewModel.ChatVM, [chat, "Renamed", null]);
                var typing = Stopwatch.StartNew();
                while (typing.ElapsedMilliseconds < 5000 && (!TypedIntoTitle() || title.Text != "Renamed"))
                    await Task.Delay(25);

                Assert.True(TypedIntoTitle(), "The new title should be typed into the row's title.");
                Assert.Equal("Renamed", title.Text);
                Assert.Equal("Bug", tagLabel.Text);
            }
            finally
            {
                window.Close();
            }
        }, CancellationToken.None);
    }

    private static TextBlock AssertLabelFits(Border badge)
    {
        Assert.True(badge.IsVisible);
        Assert.True(badge.Bounds.Width > 0);
        var label = badge.GetVisualDescendants().OfType<TextBlock>().Single();
        var offset = label.TranslatePoint(default, badge);
        Assert.NotNull(offset);
        Assert.True(offset.Value.X >= badge.Padding.Left);
        Assert.True(offset.Value.X + label.Bounds.Width
            <= badge.Bounds.Width - badge.Padding.Right + 1,
            $"Label extends past its chip: label right={offset.Value.X + label.Bounds.Width}, chip width={badge.Bounds.Width}.");
        Assert.True(label.TextLayout.Width <= label.Bounds.Width + 1);
        return label;
    }
}
