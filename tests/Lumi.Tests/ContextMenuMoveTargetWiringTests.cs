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
/// Diagnostic for the "Move to Project does nothing" bug. Reproduces the exact per-row wiring
/// (a Panel whose <see cref="Control.ContextMenu"/> hosts a named submenu, populated on the
/// ContextMenu's Opening event) and inspects whether the ContextMenu's DataContext is the row's
/// Chat at Opening time — the moment the submenu must be populated so it renders with a flyout arrow.
/// </summary>
[Collection("Headless UI")]
public sealed class ContextMenuMoveTargetWiringTests
{
    [Fact]
    public async Task Opening_ResolvesRowChatFromTag_AndPopulatesSubmenu()
    {
        using var session = HeadlessTestSession.Start();

        bool openingFired = false;
        string dcTypeAtOpening = "(handler never ran)";
        int itemsAfterOpen = -1;

        await session.Dispatch(() =>
        {
            var chat = new Chat { Title = "Row chat", ProjectId = null };

            var moveMenu = new MenuItem { Name = "MoveToProjectMenu", Header = "Move to Project" };
            var menu = new ContextMenu();
            menu.Items.Add(new MenuItem { Header = "Rename" });
            menu.Items.Add(moveMenu);

            var panel = new Panel { Background = Avalonia.Media.Brushes.Transparent, ContextMenu = menu };

            // Production fix: stash the row's Chat on the menu's Tag when the row's DataContext is set,
            // because a ContextMenu does not inherit its owner's DataContext until it opens.
            panel.DataContextChanged += (_, _) => menu.Tag = panel.DataContext as Chat;
            panel.DataContext = chat;

            menu.Opening += (_, _) =>
            {
                openingFired = true;
                var resolved = menu.Tag as Chat ?? menu.DataContext as Chat;
                dcTypeAtOpening = resolved?.GetType().Name ?? "null";

                if (resolved is not null)
                {
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

            itemsAfterOpen = moveMenu.Items.Count;

            window.Close();
        }, CancellationToken.None);

        Assert.True(openingFired, "ContextMenu.Opening should fire when the menu is requested.");
        Assert.Equal("Chat", dcTypeAtOpening); // Tag carries the row's Chat even though DataContext is null
        Assert.True(itemsAfterOpen > 0, $"Submenu should be populated on open (was {itemsAfterOpen}).");
    }
}
