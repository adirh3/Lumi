using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>
/// The keyboard for messages queued above the composer. Alt+↑ (⌥↑ on macOS) from the composer selects the
/// newest one; on a selected message Enter steers it into the reply now, E edits it (back into the
/// composer), Delete removes it, ↑/↓ move between them, and Esc or ↓ past the last returns to the composer.
/// </summary>
public partial class ChatView
{
    private ListBox? _queuedSendsList;

    private void InitializeQueuedSends()
    {
        _queuedSendsList = this.FindControl<ListBox>("QueuedSendsList");
        _queuedSendsList?.AddHandler(KeyDownEvent, OnQueuedSendsKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyDownEvent, OnComposerQueuedSendsKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>The shortcut that moves from the composer to its queued messages.</summary>
    internal static bool IsSelectQueuedSendGesture(Key key, KeyModifiers modifiers)
        => key == Key.Up && modifiers == KeyModifiers.Alt;

    private void OnComposerQueuedSendsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled
            || !IsSelectQueuedSendGesture(e.Key, e.KeyModifiers)
            || DataContext is not ChatViewModel { HasQueuedSends: true } vm
            || _queuedSendsList is null
            || e.Source is not Control source
            || !ReferenceEquals(source.FindAncestorOfType<StrataChatComposer>(includeSelf: true), _composer))
        {
            return;
        }

        e.Handled = true;
        SelectQueuedSend(vm.QueuedSends.Count - 1);
    }

    private void OnQueuedSendsKeyDown(object? sender, KeyEventArgs e)
    {
        // The keys act on the row that has the keyboard: a button inside a row can hold focus without
        // that row being the list's selection (clicking a button doesn't select its row).
        if (DataContext is not ChatViewModel vm
            || _queuedSendsList is null
            || e.KeyModifiers != KeyModifiers.None
            || ((e.Source as StyledElement)?.DataContext as QueuedSendItem
                ?? _queuedSendsList.SelectedItem as QueuedSendItem) is not { } item)
        {
            return;
        }

        var index = vm.QueuedSends.IndexOf(item);
        switch (e.Key)
        {
            case Key.Enter:
                item.SteerNowCommand.Execute(null);
                AfterQueuedSendLeft(vm, index);
                break;
            case Key.E or Key.F2:
                // Editing puts the message in the composer and focuses it.
                item.EditCommand.Execute(null);
                break;
            case Key.Delete or Key.Back:
                item.RemoveCommand.Execute(null);
                AfterQueuedSendLeft(vm, index);
                break;
            case Key.Escape:
                _composer?.FocusInputAtEnd();
                break;
            case Key.Down when index == vm.QueuedSends.Count - 1:
                _composer?.FocusInputAtEnd();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>After a message leaves the queue, selects its neighbour, or returns to the composer when none is left.</summary>
    private void AfterQueuedSendLeft(ChatViewModel vm, int index)
    {
        if (vm.QueuedSends.Count == 0)
            _composer?.FocusInputAtEnd();
        else
            SelectQueuedSend(Math.Min(index, vm.QueuedSends.Count - 1));
    }

    private void SelectQueuedSend(int index)
    {
        if (_queuedSendsList is null)
            return;

        _queuedSendsList.SelectedIndex = index;
        (_queuedSendsList.ContainerFromIndex(index) as InputElement)?.Focus(NavigationMethod.Directional);
    }
}
