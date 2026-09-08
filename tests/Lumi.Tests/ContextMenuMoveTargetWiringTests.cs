using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Lumi.Models;
using Xunit;

namespace Lumi.Tests;

/// <summary>
/// Reproduces the per-row context-menu wiring and verifies that dynamic submenus remain cheap
/// placeholders until the menu opens, when the row's <see cref="Chat"/> is resolved from Tag.
/// </summary>
[Collection("Headless UI")]
public sealed class ContextMenuMoveTargetWiringTests
{
    [Fact]
    public async Task Opening_ResolvesRowChatFromTag_AndLazilyPopulatesSubmenus()
    {
        using var session = HeadlessTestSession.Start();

        bool openingFired = false;
        string dcTypeAtOpening = "(handler never ran)";
        int tagItemsBeforeOpen = -1;
        int moveItemsBeforeOpen = -1;
        int tagItemsAfterOpen = -1;
        int moveItemsAfterOpen = -1;

        await session.Dispatch(() =>
        {
            var chat = new Chat { Title = "Row chat", ProjectId = null };

            var placeholder = new MenuItem { IsVisible = false, IsEnabled = false };
            var tagMenu = new MenuItem { Name = "ChatTagMenu", Header = "Tag" };
            tagMenu.Items.Add(placeholder);
            var moveMenu = new MenuItem { Name = "MoveToProjectMenu", Header = "Move to Project" };
            moveMenu.Items.Add(new MenuItem { IsVisible = false, IsEnabled = false });
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Rename" });
            menu.Items.Add(tagMenu);
            menu.Items.Add(moveMenu);

            var panel = new Panel { Background = Avalonia.Media.Brushes.Transparent, ContextMenu = menu };

            // Production fix: stash the row's Chat on the menu's Tag when the row's DataContext is set,
            // because a ContextMenu does not inherit its owner's DataContext until it opens.
            panel.DataContextChanged += (_, _) => menu.Tag = panel.DataContext as Chat;
            panel.DataContext = chat;

            tagItemsBeforeOpen = tagMenu.Items.Count;
            moveItemsBeforeOpen = moveMenu.Items.Count;

            menu.Opening += (_, _) =>
            {
                openingFired = true;
                var resolved = menu.Tag as Chat ?? menu.DataContext as Chat;
                dcTypeAtOpening = resolved?.GetType().Name ?? "null";

                if (resolved is not null)
                {
                    tagMenu.Items.Clear();
                    tagMenu.Items.Add(new MenuItem { Header = "Work" });
                    moveMenu.Items.Clear();
                    moveMenu.Items.Add(new MenuItem { Header = "All projects" });
                }
            };

            var window = new Window { Width = 300, Height = 200, Content = panel };
            window.Show();

            Dispatcher.UIThread.RunJobs();

            // Drive the same path a real right-click takes: ContextRequested -> framework opens the menu.
            panel.RaiseEvent(new ContextRequestedEventArgs());
            Dispatcher.UIThread.RunJobs();

            tagItemsAfterOpen = tagMenu.Items.Count;
            moveItemsAfterOpen = moveMenu.Items.Count;

            window.Close();
        }, CancellationToken.None);

        Assert.True(openingFired, "ContextMenu.Opening should fire when the menu is requested.");
        Assert.Equal("Chat", dcTypeAtOpening); // Tag carries the row's Chat even though DataContext is null
        Assert.Equal(1, tagItemsBeforeOpen);
        Assert.Equal(1, moveItemsBeforeOpen);
        Assert.True(tagItemsAfterOpen > 0, $"Tag submenu should be populated on open (was {tagItemsAfterOpen}).");
        Assert.True(moveItemsAfterOpen > 0, $"Move submenu should be populated on open (was {moveItemsAfterOpen}).");
    }
}
