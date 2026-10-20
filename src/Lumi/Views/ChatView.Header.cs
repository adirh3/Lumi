using System;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The floating glass header: the title pill's chat menu, the live pill that jumps back to what Lumi
/// is doing, and a compact layout for narrow islands.
/// </summary>
public partial class ChatView
{
    /// <summary>
    /// Below this width the pills take less room: the title and live pill narrow, and the agents pill
    /// shows only its avatars.
    /// </summary>
    private const double CompactHeaderWidth = 760;

    private Panel? _chatHeaderLayer;

    private void InitializeHeader()
    {
        _chatHeaderLayer = this.FindControl<Panel>("ChatHeaderLayer");
        SizeChanged += (_, e) => UpdateHeaderDensity(e.NewSize.Width);

        // Pills that appear mid-chat drop in rather than pop.
        foreach (var name in new[] { "HeaderLiveStatusButton", "HeaderAgentsButton" })
        {
            if (this.FindControl<Button>(name) is not { } pill)
                continue;

            pill.PropertyChanged += (_, e) =>
            {
                if (e.Property == IsVisibleProperty && pill.IsVisible
                    && _subscribedVm?.AreAnimationsEnabled != false)
                    SlideFadeEntrance.Play(pill, offsetY: -6);
            };
        }
    }

    private void UpdateHeaderDensity(double width)
        => _chatHeaderLayer?.Classes.Set("compact", width > 0 && width < CompactHeaderWidth);

    private void OnHeaderLiveStatusClick(object? sender, RoutedEventArgs e) => JumpToLatest(focusComposer: false);

    /// <summary>
    /// Points the title menu at the open chat. As in the sidebar's chat menu, the actions belong to the
    /// main window's view model, and the tag and project lists are rebuilt from live data on each open.
    /// </summary>
    private void OnChatTitleMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu
            || _subscribedVm?.CurrentChat is not { } chat
            || TopLevel.GetTopLevel(this)?.DataContext is not MainViewModel main)
            return;

        _subscribedVm.RefreshHeaderDetails();

        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            switch (item.Name)
            {
                case "ChatTitleRenameItem":
                    Bind(item, main.StartRenameChatCommand);
                    break;
                case "ChatTitlePinItem":
                    item.Command = main.ToggleChatPinCommand;
                    ChatMenuBuilder.UpdatePinItem(item, chat, FindIconGeometry);
                    break;
                case "ChatTitleDuplicateItem":
                    Bind(item, main.DuplicateChatCommand);
                    break;
                case "ChatTitleMoveItem":
                    ChatMenuBuilder.PopulateMoveToProjectMenu(item, main, chat, FindIconGeometry);
                    break;
                case "ChatTitleTagItem":
                    ChatMenuBuilder.PopulateTagMenu(item, main, chat, FindIconGeometry);
                    break;
                case "ChatTitleNewWindowItem":
                    Bind(item, main.OpenChatInNewWindowCommand);
                    break;
                case "ChatTitleDeleteItem":
                    Bind(item, main.DeleteChatCommand);
                    break;
            }
        }

        void Bind(MenuItem item, ICommand command)
        {
            item.Command = command;
            item.CommandParameter = chat;
        }
    }

    private Geometry? FindIconGeometry(string resourceKey)
        => this.TryFindResource(resourceKey, ActualThemeVariant, out var resource) && resource is Geometry geometry
            ? geometry
            : null;
}
