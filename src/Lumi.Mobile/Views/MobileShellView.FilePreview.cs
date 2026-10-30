using System.ComponentModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Rendering.Composition;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using StrataTheme;

namespace Lumi.Mobile.Views;

/// <summary>
/// The file preview pane. Over the chat it slides in from the trailing edge on the render thread, as a
/// page does; beside it, the pane grows from the trailing edge while the chat list steps aside, so the
/// chat glides into the list's place and the file opens where the chat was. A closing pane keeps
/// showing its file until it is gone, and only then lets the preview release what it holds.
/// </summary>
public partial class MobileShellView
{
    private static readonly TimeSpan PreviewOpenDuration = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan PreviewCloseDuration = TimeSpan.FromMilliseconds(230);

    private MobileShellViewModel? _previewSource;
    private FilePreviewViewModel? _shownPreview;
    private bool _previewBeside;
    private bool _previewClosing;
    private bool _previewWidthMoving;
    private double _previewPaneWidth;
    private int _previewMotionVersion;
    private IDisposable? _previewMotionTimer;
    private CompositionVisual? _previewSlideVisual;

    private Panel PreviewHost => this.FindControl<Panel>("FilePreviewHost")!;

    private Border PreviewFrame => this.FindControl<Border>("FilePreviewFrame")!;

    private FilePreviewView PreviewPane => this.FindControl<FilePreviewView>("FilePreviewPane")!;

    private void AttachFilePreview(MobileShellViewModel? shell)
    {
        if (_previewSource is not null)
        {
            _previewSource.PropertyChanged -= OnPreviewShellChanged;
            _previewSource.Chat.PropertyChanged -= OnPreviewChatChanged;
        }

        _previewSource = shell;
        if (shell is not null)
        {
            shell.PropertyChanged += OnPreviewShellChanged;
            shell.Chat.PropertyChanged += OnPreviewChatChanged;
        }

        SyncFilePreview(animate: false);
    }

    private void OnPreviewShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileShellViewModel.IsFilePreviewBeside)
            or nameof(MobileShellViewModel.FilePreviewPaneWidth)
            or nameof(MobileShellViewModel.FilePreviewPaneMargin)
            or nameof(MobileShellViewModel.SafeArea))
        {
            SyncFilePreview(animate: true);
        }

        if (e.PropertyName == nameof(MobileShellViewModel.ReservesHingeForNavigation))
            UpdateNavigationLayout();
    }

    private void OnPreviewChatChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MobileChatViewModel.FilePreview))
            SyncFilePreview(animate: true);
    }

    private void SyncFilePreview(bool animate)
    {
        var shell = _previewSource;
        var target = shell?.Chat.FilePreview;
        if (shell is null || target is null)
        {
            if (_shownPreview is not null && !_previewClosing)
                ClosePreviewPane(animate);
            return;
        }

        var beside = shell.IsFilePreviewBeside;
        if (_shownPreview is null || _previewClosing)
        {
            // Opening, or opening again before the last pane had finished leaving.
            var reopening = _previewClosing;
            StopPreviewMotion();
            _previewClosing = false;
            ShowPreview(target);
            PlacePreview(shell, beside, collapsed: animate && beside && !reopening);
            if (animate && this.IsAttachedToVisualTree())
                PlayPreviewEntrance(beside);
            return;
        }

        if (!ReferenceEquals(_shownPreview, target))
            ShowPreview(target);

        // The same pane after a fold, a rotation or new insets: it lands where the layout now puts it.
        if (beside != _previewBeside)
            StopPreviewMotion();
        PlacePreview(shell, beside, collapsed: false);
    }

    /// <summary>Shows a preview in the pane, and only then lets the one it replaces release its file.</summary>
    private void ShowPreview(FilePreviewViewModel? preview)
    {
        var previous = _shownPreview;
        if (ReferenceEquals(previous, preview))
            return;

        _shownPreview = preview;
        preview?.AttachPresenter();
        PreviewPane.DataContext = preview;
        previous?.DetachPresenter();
    }

    private void PlacePreview(MobileShellViewModel shell, bool beside, bool collapsed)
    {
        var host = PreviewHost;
        var frame = PreviewFrame;
        _previewBeside = beside;
        Grid.SetColumn(host, beside ? 4 : 2);
        this.FindControl<Border>("FilePreviewSurface")?.Classes.Set("beside", beside);
        PreviewPane.IsBesideChat = beside;
        if (beside)
        {
            // The card keeps its final width and slides with the pane's leading edge as the pane grows.
            _previewPaneWidth = shell.FilePreviewPaneWidth;
            frame.Width = _previewPaneWidth;
            frame.HorizontalAlignment = HorizontalAlignment.Left;
            frame.Padding = shell.FilePreviewPaneMargin;
            PreviewPane.Insets = default;
            if (!_previewWidthMoving)
                host.Width = collapsed ? 0 : _previewPaneWidth;
        }
        else
        {
            frame.Width = double.NaN;
            frame.HorizontalAlignment = HorizontalAlignment.Stretch;
            frame.Padding = default;
            PreviewPane.Insets = shell.SafeArea;
            host.Width = double.NaN;
        }

        host.IsVisible = true;
        // The chat's own sheets open over the chat, which beside a preview is on the leading side.
        if (this.FindControl<Panel>("ChatSettingsOverlay") is { } chatSheets)
            chatSheets.HorizontalAlignment = beside ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        UpdateNavigationLayout();
    }

    private void PlayPreviewEntrance(bool beside)
    {
        if (beside)
            AnimatePreviewWidth(opening: true, completed: null);
        else
            SlidePreview(opening: true, completed: null);
    }

    private void ClosePreviewPane(bool animate)
    {
        _previewClosing = true;
        if (!animate || !PreviewHost.IsVisible || !this.IsAttachedToVisualTree())
        {
            FinishPreviewClose();
            return;
        }

        StopPreviewMotion();
        if (_previewBeside)
            AnimatePreviewWidth(opening: false, completed: FinishPreviewClose);
        else
            SlidePreview(opening: false, completed: FinishPreviewClose);
    }

    private void FinishPreviewClose()
    {
        StopPreviewMotion();
        _previewClosing = false;
        PreviewHost.IsVisible = false;
        PreviewHost.Width = double.NaN;
        ShowPreview(null);
        if (this.FindControl<Panel>("ChatSettingsOverlay") is { } chatSheets)
            chatSheets.HorizontalAlignment = HorizontalAlignment.Right;
        UpdateNavigationLayout();
    }

    /// <summary>
    /// Beside the chat: the pane's width follows a frame-locked curve, like the chat list's drawer it
    /// replaces, so the chat narrows and the card slides in on the same frames.
    /// </summary>
    private void AnimatePreviewWidth(bool opening, Action? completed)
    {
        var version = ++_previewMotionVersion;
        var host = PreviewHost;
        var from = double.IsFinite(host.Width) ? host.Width : opening ? 0 : _previewPaneWidth;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            host.Width = opening ? _previewPaneWidth : 0;
            completed?.Invoke();
            return;
        }

        var duration = opening ? PreviewOpenDuration : PreviewCloseDuration;
        Easing easing = opening ? new CubicEaseOut() : new CubicEaseInOut();
        var started = Stopwatch.GetTimestamp();
        _previewWidthMoving = true;
        SetPreviewMoving(true);
        host.Width = from;

        void Frame(TimeSpan _)
        {
            if (version != _previewMotionVersion)
                return;

            var progress = Math.Clamp(Stopwatch.GetElapsedTime(started) / duration, 0, 1);
            var target = opening ? _previewPaneWidth : 0;
            host.Width = from + (target - from) * easing.Ease(progress);
            if (progress < 1)
            {
                topLevel.RequestAnimationFrame(Frame);
                return;
            }

            _previewWidthMoving = false;
            SetPreviewMoving(false);
            completed?.Invoke();
        }

        topLevel.RequestAnimationFrame(Frame);
    }

    /// <summary>Over the chat: the page slides in from the trailing edge on the render thread.</summary>
    private void SlidePreview(bool opening, Action? completed)
    {
        var version = ++_previewMotionVersion;
        var frame = PreviewFrame;
        SetPreviewMoving(true);
        if (opening)
        {
            // Off screen until the compositor takes over, so the first frame never flashes it in place.
            frame.Opacity = 0;
        }

        // Measure first: the pane may have only just become visible.
        Dispatcher.UIThread.Post(() =>
        {
            if (version != _previewMotionVersion)
                return;

            var width = (float)Math.Max(1, PreviewHost.Bounds.Width);
            if (ElementComposition.GetElementVisual(frame) is not { } visual)
            {
                frame.Opacity = 1;
                SetPreviewMoving(false);
                completed?.Invoke();
                return;
            }

            visual.Compositor.RequestCompositionUpdate(() =>
            {
                if (version != _previewMotionVersion)
                    return;

                frame.Opacity = 1;
                var slide = visual.Compositor.CreateStableVector3DKeyFrameAnimation();
                Easing easing = opening ? new CubicEaseOut() : new CubicEaseIn();
                slide.Duration = opening ? PreviewOpenDuration : PreviewCloseDuration;
                slide.StopBehavior = AnimationStopBehavior.SetToFinalValue;
                slide.InsertKeyFrame(0, new Vector3D(opening ? width : 0, 0, 0), easing);
                slide.InsertKeyFrame(1, new Vector3D(opening ? 0 : width, 0, 0), easing);
                _previewSlideVisual = visual;
                visual.Translation = default;
                visual.StartAnimation(nameof(CompositionVisual.Translation), slide);
            });

            _previewMotionTimer?.Dispose();
            _previewMotionTimer = DispatcherTimer.RunOnce(() =>
            {
                if (version != _previewMotionVersion)
                    return;
                if (opening)
                    StopPreviewSlide();
                SetPreviewMoving(false);
                completed?.Invoke();
            }, opening ? PreviewOpenDuration : PreviewCloseDuration);
        }, DispatcherPriority.Loaded);
    }

    private void StopPreviewMotion()
    {
        _previewMotionVersion++;
        _previewMotionTimer?.Dispose();
        _previewMotionTimer = null;
        _previewWidthMoving = false;
        StopPreviewSlide();
        PreviewFrame.Opacity = 1;
        SetPreviewMoving(false);
    }

    private void StopPreviewSlide()
    {
        if (_previewSlideVisual is not { } visual)
            return;

        _previewSlideVisual = null;
        visual.StopAnimation(nameof(CompositionVisual.Translation));
        // Unchanged values are not sent to the compositor, so the resting value is written twice.
        visual.Translation = new Vector3D(1, 0, 0);
        visual.Translation = default;
    }

    private void SetPreviewMoving(bool moving)
    {
        if (_previewSource is { } shell)
            shell.IsFilePreviewMoving = moving;
    }

    private void DetachFilePreview()
    {
        if (_previewClosing || _shownPreview is not null && _previewSource?.Chat.FilePreview is null)
            FinishPreviewClose();
        else
            StopPreviewMotion();
    }
}
