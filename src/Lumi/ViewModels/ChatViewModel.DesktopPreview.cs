using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    // This is a snapshot, not the last delta: NewFrame holds the last matching capture.
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDesktopPreview))]
    [NotifyPropertyChangedFor(nameof(ShowDesktopToggle))]
    [NotifyPropertyChangedFor(nameof(DesktopPreviewFrame))]
    [NotifyPropertyChangedFor(nameof(DesktopPreviewWindowTitle))]
    [NotifyPropertyChangedFor(nameof(DesktopPreviewCapturedAtText))]
    private DesktopPreviewUpdate? _desktopPreview;

    [ObservableProperty] private bool _isDesktopOpen;
    [ObservableProperty] private bool _isRefreshingDesktopPreview;
    [ObservableProperty] private string? _desktopPreviewError;
    private int _desktopPreviewRefreshVersion;
    private int _visibleDesktopPreviewHostCount;

    public bool HasDesktopPreview => DesktopPreview is not null;
    public bool ShowDesktopToggle => OperatingSystem.IsWindows() && HasDesktopPreview;
    /// <summary>True while a host is visibly presenting the Desktop page, not merely remembering it as open.</summary>
    public bool HasVisibleDesktopPreviewHost => Volatile.Read(ref _visibleDesktopPreviewHostCount) > 0;
    public DesktopPreviewFrame? DesktopPreviewFrame => DesktopPreview?.NewFrame;
    public string DesktopPreviewWindowTitle => DesktopPreview?.WindowTitle ?? Loc.Desktop_NoTarget;
    public string DesktopPreviewCapturedAtText => DesktopPreviewFrame is { } frame
        ? string.Format(Loc.Culture, Loc.Desktop_LastCapture, frame.CapturedAt.ToLocalTime().ToString("g", Loc.Culture))
        : Loc.Desktop_NotCaptured;

    public event Action<Guid>? DesktopShowRequested;
    public event Action? DesktopHideRequested;

    internal Func<Task>? DesktopPreviewRefreshAsync { get; set; }

    // Each controller balances its own contribution on the UI thread; multiple windows can share us.
    internal void AddVisibleDesktopPreviewHost()
    {
        if (Interlocked.Increment(ref _visibleDesktopPreviewHostCount) == 1)
            OnPropertyChanged(nameof(HasVisibleDesktopPreviewHost));
    }

    internal void RemoveVisibleDesktopPreviewHost()
    {
        if (_visibleDesktopPreviewHostCount > 0
            && Interlocked.Decrement(ref _visibleDesktopPreviewHostCount) == 0)
        {
            OnPropertyChanged(nameof(HasVisibleDesktopPreviewHost));
        }
    }

    /// <summary>
    /// Stores a passive update on its owning chat surface. The snapshot retains the last frame only
    /// while the target matches; publication never opens a panel or requests another capture.
    /// </summary>
    public void PublishDesktopPreview(DesktopPreviewUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        if (_isDisposed)
            return;

        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => PublishDesktopPreview(update));
            return;
        }

        if (CurrentChat?.Id != update.ChatId)
            return;

        var previous = DesktopPreview;
        if (previous is not null && update.UpdatedAt < previous.UpdatedAt)
            return;

        var sameTarget = previous?.WindowKey == update.WindowKey;
        if (previous is not null && !sameTarget)
            InvalidateDesktopPreviewRefresh();

        var frame = update.NewFrame ?? (sameTarget ? previous?.NewFrame : null);
        if (sameTarget && previous?.NewFrame is { } currentFrame
            && frame is not null && frame.CapturedAt < currentFrame.CapturedAt)
        {
            frame = currentFrame;
        }

        DesktopPreview = update with { NewFrame = frame };
        if (!sameTarget || update.NewFrame is not null)
            DesktopPreviewError = null;
    }

    partial void OnDesktopPreviewChanged(DesktopPreviewUpdate? value) => NotifyWorkspaceVisibilityChanged();

    [RelayCommand]
    public void RequestShowDesktop()
    {
        var chatId = CurrentChat?.Id;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (CurrentChat?.Id == chatId)
                    RequestShowDesktop();
            });
            return;
        }

        if (_isDisposed || chatId is null)
            return;

        IsDesktopOpen = true;
        DesktopShowRequested?.Invoke(chatId.Value);
        _ = RefreshDesktopPreviewAsync();
    }

    public void ToggleDesktop()
    {
        if (IsDesktopOpen)
            CloseDesktopPreview();
        else
            RequestShowDesktop();
    }

    [RelayCommand]
    public void CloseDesktopPreview()
    {
        var chatId = CurrentChat?.Id;
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (CurrentChat?.Id == chatId)
                    CloseDesktopPreview();
            });
            return;
        }

        IsDesktopOpen = false;
        DesktopHideRequested?.Invoke();
    }

    [RelayCommand]
    private async Task RefreshDesktopPreviewAsync()
    {
        if (_isDisposed || !IsDesktopOpen || IsRefreshingDesktopPreview || CurrentChat is not { } chat)
            return;

        var refresh = DesktopPreviewRefreshAsync;
        var version = ++_desktopPreviewRefreshVersion;
        IsRefreshingDesktopPreview = true;
        DesktopPreviewError = null;
        try
        {
            // A captured synchronization context can prioritize Task.Yield above the queued show.
            // Wait below normal UI work so the empty/loading page is presented before capture.
            await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);
            if (!IsCurrentDesktopPreviewRefresh(chat.Id, version))
                return;

            if (refresh is null)
                DesktopPreviewError = Loc.Desktop_RefreshUnavailable;
            else
                await refresh();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (IsCurrentDesktopPreviewRefresh(chat.Id, version))
                DesktopPreviewError = ex.Message;
        }
        finally
        {
            if (IsCurrentDesktopPreviewRefresh(chat.Id, version))
                IsRefreshingDesktopPreview = false;
        }
    }

    private bool IsCurrentDesktopPreviewRefresh(Guid chatId, int version)
        => !_isDisposed && IsDesktopOpen && CurrentChat?.Id == chatId
            && _desktopPreviewRefreshVersion == version;

    partial void OnIsDesktopOpenChanged(bool value)
    {
        if (!value)
            InvalidateDesktopPreviewRefresh();
    }

    partial void OnCurrentChatChanging(Chat? oldValue, Chat? newValue)
    {
        if (oldValue?.Id != newValue?.Id)
            ResetDesktopPreview();
    }

    private void InvalidateDesktopPreviewRefresh()
    {
        _desktopPreviewRefreshVersion++;
        IsRefreshingDesktopPreview = false;
    }

    private void ResetDesktopPreview()
    {
        InvalidateDesktopPreviewRefresh();
        IsDesktopOpen = false;
        DesktopPreview = null;
        DesktopPreviewError = null;
        DesktopPreviewRefreshAsync = null;
        DesktopHideRequested?.Invoke();
    }
}
