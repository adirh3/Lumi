using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using Lumi.ViewModels;

namespace Lumi.Views;

public partial class DesktopPreviewView : UserControl, IDisposable
{
    private readonly Func<DesktopPreviewFrame, Task<Bitmap>> _decodeFrame;
    private ChatViewModel? _viewModel;
    private CancellationTokenSource? _loadCts;
    private DesktopPreviewFrame? _frame;
    private Bitmap? _image;
    private string? _decodeError;
    private bool _isAttached;
    private bool _isDisposed;

    public DesktopPreviewView() : this(DecodeFrameAsync) { }

    internal DesktopPreviewView(Func<DesktopPreviewFrame, Task<Bitmap>> decodeFrame)
    {
        _decodeFrame = decodeFrame;
        InitializeComponent();
    }

    internal async Task ShowFrameAsync(DesktopPreviewFrame? frame)
    {
        if (_isDisposed || ReferenceEquals(_frame, frame))
            return;

        Clear();
        _frame = frame;
        if (frame is null)
            return;

        var cts = _loadCts = new CancellationTokenSource();
        UpdateStatus();
        try
        {
            var bitmap = await _decodeFrame(frame);
            if (_isDisposed || !ReferenceEquals(_loadCts, cts))
            {
                bitmap.Dispose();
                return;
            }

            _image = bitmap;
            DesktopPreviewImage.Source = bitmap;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_loadCts, cts))
                _decodeError = ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                cts.Dispose();
                UpdateStatus();
            }
        }
    }

    private static Task<Bitmap> DecodeFrameAsync(DesktopPreviewFrame frame) => Task.Run(() =>
    {
        using var stream = new MemoryStream(frame.PngBytes.ToArray(), writable: false);
        return Bitmap.DecodeToWidth(stream, Math.Min(frame.PixelWidth, 1600));
    });

    public void Clear()
    {
        var cts = _loadCts;
        _loadCts = null;
        cts?.Cancel();
        cts?.Dispose();
        DesktopPreviewImage.Source = null;
        _image?.Dispose();
        _image = null;
        _frame = null;
        _decodeError = null;
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var error = _viewModel?.DesktopPreviewError ?? _decodeError;
        var loading = _loadCts is not null || _viewModel?.IsRefreshingDesktopPreview == true;
        DesktopPreviewPlaceholder.IsVisible = _image is null;
        DesktopPreviewPlaceholderText.Text = error is not null ? Loc.Desktop_Unavailable
            : loading ? Loc.Desktop_Loading : Loc.Desktop_NoImage;
        DesktopPreviewStateText.Text = error ?? (loading ? Loc.Desktop_Loading : Loc.Desktop_PassiveNotice);
    }

    private void AttachViewModel()
    {
        UnwireViewModel();
        if (!_isAttached || _isDisposed || DataContext is not ChatViewModel viewModel)
            return;

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        UpdatePreview();
    }

    private void UnwireViewModel()
    {
        if (_viewModel is not null)
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ChatViewModel.DesktopPreview)
            or nameof(ChatViewModel.IsRefreshingDesktopPreview)
            or nameof(ChatViewModel.DesktopPreviewError)))
        {
            return;
        }

        if (Dispatcher.UIThread.CheckAccess())
        {
            UpdatePreview();
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(sender, _viewModel))
                    UpdatePreview();
            });
        }
    }

    private void UpdatePreview()
    {
        if (_viewModel is null || _isDisposed)
            return;

        _ = ShowFrameAsync(_viewModel.DesktopPreviewFrame);
        UpdateStatus();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        Clear();
        AttachViewModel();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _isAttached = true;
        AttachViewModel();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        UnwireViewModel();
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        UnwireViewModel();
        Clear();
        DataContext = null;
        GC.SuppressFinalize(this);
    }
}
