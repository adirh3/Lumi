using System;
using System.Linq;
using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
using Lumi.ViewModels;
using StrataTheme.Animation;

namespace Lumi.Views;

/// <summary>
/// The floating glass header: the title pill's chat menu, the running pill and its card, the live pill
/// that jumps back to what Lumi is doing, and a compact layout for narrow islands.
/// </summary>
public partial class ChatView
{
    /// <summary>
    /// Below this width the pills take less room: the title and live pill narrow, the running pill shows
    /// only its faces, and the git pill only its branch and counts.
    /// </summary>
    private const double CompactHeaderWidth = 760;

    private Panel? _chatHeaderLayer;
    private Button? _headerRunningButton;

    private void InitializeHeader()
    {
        _chatHeaderLayer = this.FindControl<Panel>("ChatHeaderLayer");
        SizeChanged += (_, e) => UpdateHeaderDensity(e.NewSize.Width);

        _headerRunningButton = this.FindControl<Button>("HeaderRunningButton");
        if (_headerRunningButton is not null && FlyoutBase.GetAttachedFlyout(_headerRunningButton) is { } runningCard)
            runningCard.Opened += OnRunningCardOpened;

        // Pills that appear mid-chat drop in rather than pop.
        foreach (var name in new[] { "HeaderLiveStatusButton", "HeaderRunningButton", "HeaderGitPill" })
        {
            if (this.FindControl<Control>(name) is not { } pill)
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
    /// While Lumi replies, the running pill opens the agents at work in the Workspace. Once Lumi is ready it
    /// opens the card of everything still running, which can stop it.
    /// </summary>
    private void OnHeaderRunningClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button pill || _subscribedVm is not { } vm)
            return;

        if (vm.HasBackgroundActivity)
            FlyoutBase.ShowAttachedFlyout(pill);
        else
            vm.OpenWorkspaceAgentsCommand.Execute(null);
    }

    private async void OnRunningCardOpened(object? sender, EventArgs e)
    {
        if (_subscribedVm is { } vm)
            await vm.RefreshBackgroundActivityAsync();
    }

    private void HideRunningCard()
    {
        if (_headerRunningButton is not null)
            FlyoutBase.GetAttachedFlyout(_headerRunningButton)?.Hide();
    }

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
