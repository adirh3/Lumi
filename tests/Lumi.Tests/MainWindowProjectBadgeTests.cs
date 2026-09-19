using Avalonia;
using Avalonia.Controls;
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

                var badgeOffset = projectBadge.TranslatePoint(default, row);
                Assert.NotNull(badgeOffset);
                Assert.True(badgeOffset.Value.X + projectBadge.Bounds.Width
                    <= row.Bounds.Width - row.Padding.Right + 1);

                var tagBadge = row.GetVisualDescendants()
                    .OfType<Border>()
                    .Single(border => border.Name == "ChatTagBadge");
                Assert.Equal(tag is not null, tagBadge.IsVisible);
                if (tag is not null)
                {
                    var tagLabel = AssertLabelFits(tagBadge);
                    Assert.Contains(tagLabel.TextLayout.TextLines, line => line.HasCollapsed);
                    Assert.True(tagBadge.Bounds.Right <= projectBadge.Bounds.Left);
                }
                else
                {
                    Assert.InRange(projectBadge.Bounds.Left, 0, 1);
                }
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
