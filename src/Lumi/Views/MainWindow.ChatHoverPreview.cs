using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Localization;
using Lumi.Models;
using Lumi.ViewModels;
using Microsoft.Data.Sqlite;

namespace Lumi.Views;

public partial class MainWindow
{
    private ListBoxItem? _chatHoverRow;
    private Chat? _chatHoverChat;
    private CancellationTokenSource? _chatHoverCancellation;

    private void InitializeChatHoverPreview()
    {
        ChatHoverPreview = this.GetControl<Popup>(nameof(ChatHoverPreview));
        ChatHoverTitle = this.GetControl<TextBlock>(nameof(ChatHoverTitle));
        ChatHoverStatus = this.GetControl<TextBlock>(nameof(ChatHoverStatus));
        ChatHoverReplyMeta = this.GetControl<Grid>(nameof(ChatHoverReplyMeta));
        ChatHoverReplyTime = this.GetControl<TextBlock>(nameof(ChatHoverReplyTime));
        ChatHoverReplyText = this.GetControl<TextBlock>(nameof(ChatHoverReplyText));
        ChatHoverPreview.Closed += OnChatHoverPreviewClosed;
    }

    private void OnChatListHoverMoved(object? sender, PointerEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Mouse || e.Source is not Visual source)
            return;

        var row = source.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        var buttons = e.GetCurrentPoint(this).Properties;
        if (row?.DataContext is not Chat chat
            || DataContext is not MainViewModel { IsSidebarCollapsed: false } vm
            || buttons.IsLeftButtonPressed || buttons.IsRightButtonPressed || buttons.IsMiddleButtonPressed
            || HasChatRowToolTip(source, row))
        {
            DismissChatHoverPreview();
            return;
        }

        if (ReferenceEquals(row, _chatHoverRow))
            return;

        DismissChatHoverPreview();
        _chatHoverRow = row;
        _chatHoverChat = chat;
        row.PointerExited += OnChatListHoverExited;
        row.DataContextChanged += OnChatHoverTargetChanged;
        row.DetachedFromVisualTree += OnChatHoverTargetChanged;
        chat.PropertyChanged += OnChatHoverIdentityChanged;
        LoadChatHoverPreview(vm, row, chat);
    }

    private void LoadChatHoverPreview(MainViewModel vm, ListBoxItem row, Chat chat)
    {
        var token = ReplaceCancellationTokenSource(ref _chatHoverCancellation).Token;
        _ = ShowChatHoverPreviewAsync(vm, row, chat, delay: !ChatHoverPreview.IsOpen, token);
    }

    private static bool HasChatRowToolTip(Visual source, ListBoxItem row)
    {
        // The small project/tag/activity badges still show their own tips.
        for (var visual = source; visual is not null && !ReferenceEquals(visual, row); visual = visual.GetVisualParent())
        {
            if (visual is Control control && ToolTip.GetTip(control) is not null)
                return true;
        }
        return false;
    }

    private async Task ShowChatHoverPreviewAsync(
        MainViewModel vm, ListBoxItem row, Chat chat, bool delay, CancellationToken token)
    {
        ChatMessage? reply = null;
        var unavailable = false;
        try
        {
            if (delay)
                await Task.Delay(TimeSpan.FromMilliseconds(450), token);
            reply = await vm.DataStore.ReadLatestChatReplyAsync(chat, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex) when (ex is SqliteException or JsonException or IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("[Chat hover] Could not read the latest reply for {0}: {1}", chat.Id, ex.Message);
            unavailable = true;
        }

        if (token.IsCancellationRequested)
            return;
        if (!row.IsPointerOver || !row.IsEffectivelyVisible || !row.IsAttachedToVisualTree())
        {
            DismissChatHoverPreview();
            return;
        }

        var excerpt = ManagementText.PlainPreview(reply?.Content, 240);
        ChatHoverReplyText.Text = excerpt.Length > 0
            ? excerpt
            : Loc.Get(reply is not null || unavailable ? "Sidebar_ChatHoverUnavailable" : "Sidebar_ChatHoverNoReply");
        ChatHoverReplyMeta.IsVisible = excerpt.Length > 0;
        ChatHoverReplyTime.Text = reply?.Timestamp.ToLocalTime().ToString("g", Loc.Culture);
        UpdateChatHoverIdentity(chat);
        ChatHoverPreview.PlacementTarget = row;
        ChatHoverPreview.IsOpen = true;
    }

    private void UpdateChatHoverIdentity(Chat chat)
    {
        ChatHoverTitle.Text = string.IsNullOrWhiteSpace(chat.Title) ? Loc.Sidebar_NewChat : chat.Title;
        var status = chat switch
        {
            { IsAwaitingApproval: true } or { IsAwaitingInput: true } => Loc.Get("TranscriptRail_Waiting"),
            { IsPaused: true } => Loc.Get("Chat_Paused"),
            { IsRunning: true } => Loc.Get("ToolGroup_Working"),
            { HasBackgroundActivity: true } => Loc.Get("Chat_BackgroundActivity"),
            _ => null
        };
        ChatHoverStatus.Text = status;
        ChatHoverStatus.IsVisible = status is not null;
    }

    private void OnChatHoverIdentityChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_chatHoverChat is not { } chat || !ReferenceEquals(sender, chat))
            return;

        if (e.PropertyName == nameof(Chat.IsRunning) && !chat.IsRunning
            && _chatHoverRow is { } row && DataContext is MainViewModel vm)
        {
            LoadChatHoverPreview(vm, row, chat);
        }
        else if (ChatHoverPreview.IsOpen && e.PropertyName is nameof(Chat.Title) or nameof(Chat.IsAwaitingApproval)
                or nameof(Chat.IsAwaitingInput) or nameof(Chat.IsPaused) or nameof(Chat.IsRunning)
                or nameof(Chat.HasBackgroundActivity))
        {
            UpdateChatHoverIdentity(chat);
        }
    }

    private void OnChatListHoverExited(object? sender, PointerEventArgs e) => DismissChatHoverPreview();
    private void OnChatHoverTargetChanged(object? sender, EventArgs e) => DismissChatHoverPreview();

    private void DismissChatHoverPreview()
    {
        if (_chatHoverRow is not { } row)
            return;

        DisposeCancellationTokenSource(ref _chatHoverCancellation);
        row.PointerExited -= OnChatListHoverExited;
        row.DataContextChanged -= OnChatHoverTargetChanged;
        row.DetachedFromVisualTree -= OnChatHoverTargetChanged;
        if (_chatHoverChat is { } chat)
            chat.PropertyChanged -= OnChatHoverIdentityChanged;
        _chatHoverRow = null;
        _chatHoverChat = null;
        ChatHoverPreview.IsOpen = false;
    }

    private void OnChatHoverPreviewClosed(object? sender, EventArgs e)
    {
        // Retargeting can close the old host while the next preview is being set up.
        Dispatcher.UIThread.Post(() =>
        {
            if (ChatHoverPreview.IsOpen || ChatHoverPreview.Child?.IsAttachedToVisualTree() == true)
                return;

            ChatHoverPreview.PlacementTarget = null;
            ChatHoverTitle.Text = null;
            ChatHoverStatus.Text = null;
            ChatHoverReplyTime.Text = null;
            ChatHoverReplyText.Text = null;
        }, DispatcherPriority.Background);
    }
}
