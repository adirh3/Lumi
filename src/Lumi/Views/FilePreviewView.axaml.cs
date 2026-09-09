using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Lumi.Localization;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.Views;

public partial class FilePreviewView : UserControl, IDisposable
{
    private CancellationTokenSource? _loadCts;
    private NativeFilePreviewHost? _nativeHost;
    private Bitmap? _image;
    private string? _filePath;

    public FilePreviewView() => InitializeComponent();

    public async Task ShowFileAsync(string filePath)
    {
        Clear();
        _filePath = filePath;
        PreviewFileName.Text = Path.GetFileName(filePath);
        ToolTip.SetTip(PreviewFileName, filePath);
        ShowStatus(Loc.Preview_Loading);
        var cts = _loadCts = new CancellationTokenSource();

        try
        {
            var content = await FilePreviewContent.LoadAsync(filePath, cts.Token);
            if (!ReferenceEquals(_loadCts, cts))
                return;

            PreviewTruncationNotice.IsVisible = content.IsTruncated;
            switch (content.Kind)
            {
                case FilePreviewKind.Markdown:
                    FilePreviewContentHost.Content = WrapContent(new StrataMarkdown
                    {
                        Name = "FilePreviewMarkdown",
                        IsInline = true,
                        ImageBaseDirectory = Path.GetDirectoryName(Path.GetFullPath(filePath)),
                        Markdown = content.Text,
                        Margin = new Thickness(20, 16)
                    });
                    break;
                case FilePreviewKind.Text:
                    var extension = Path.GetExtension(filePath).TrimStart('.');
                    FilePreviewContentHost.Content = WrapContent(new StrataCodeBlock
                    {
                        Name = "FilePreviewCode",
                        Language = extension,
                        Text = content.Text,
                        Margin = new Thickness(20, 16)
                    }, horizontalScroll: true);
                    break;
                case FilePreviewKind.Image:
                    var bitmap = await Task.Run(() =>
                    {
                        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        return Bitmap.DecodeToWidth(stream, 1600);
                    });
                    if (!ReferenceEquals(_loadCts, cts))
                    {
                        bitmap.Dispose();
                        return;
                    }
                    _image = bitmap;
                    FilePreviewContentHost.Content = new Image
                    {
                        Name = "FilePreviewImage", Source = bitmap, Stretch = Stretch.Uniform,
                        Margin = new Thickness(16)
                    };
                    break;
                case FilePreviewKind.Native:
                    if (!OperatingSystem.IsWindows())
                    {
                        ShowStatus(Loc.Preview_Unavailable, Loc.Preview_PlatformUnavailable);
                        break;
                    }
                    _nativeHost = new NativeFilePreviewHost(filePath);
                    var native = _nativeHost;
                    native.PreviewFailed += message => Dispatcher.UIThread.Post(() =>
                    {
                        if (ReferenceEquals(_nativeHost, native))
                        {
                            ReleaseNativeHost();
                            ShowStatus(Loc.Preview_Unavailable, Loc.Preview_NativeUnavailable + "\n\n" + message);
                        }
                    });
                    FilePreviewContentHost.Content = native;
                    break;
            }

            if (content.Kind is FilePreviewKind.Markdown or FilePreviewKind.Text && content.Text?.Length == 0)
                ShowStatus(Loc.Preview_Empty);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                   or NotSupportedException)
        {
            if (ReferenceEquals(_loadCts, cts))
                ShowStatus(Loc.Preview_Unavailable, ex.Message);
        }
    }

    public void Clear()
    {
        var cts = _loadCts;
        _loadCts = null;
        cts?.Cancel();
        cts?.Dispose();
        ReleaseNativeHost();
        FilePreviewContentHost.Content = null;
        _image?.Dispose();
        _image = null;
        _filePath = null;
        PreviewTruncationNotice.IsVisible = false;
    }

    private void ReleaseNativeHost()
    {
        var native = _nativeHost;
        _nativeHost = null;
        native?.Dispose();
    }

    private static ScrollViewer WrapContent(Control content, bool horizontalScroll = false) => new()
    {
        Content = content,
        HorizontalScrollBarVisibility = horizontalScroll
            ? Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
            : Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto
    };

    private void ShowStatus(string title, string? detail = null)
    {
        var text = new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel
        {
            Spacing = 10, Margin = new Thickness(28),
            VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center,
            MaxWidth = 440, Children = { text }
        };
        if (!string.IsNullOrWhiteSpace(detail))
            panel.Children.Add(new SelectableTextBlock { Text = detail, TextWrapping = TextWrapping.Wrap, FontSize = 12, Opacity = 0.7 });
        FilePreviewContentHost.Content = panel;
    }

    private async void OnRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (_filePath is { } path)
            await ShowFileAsync(path);
    }

    private void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_filePath is not { } path)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            ShowStatus(Loc.Preview_Unavailable, ex.Message);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Clear();
        base.OnDetachedFromVisualTree(e);
    }

    public void Dispose()
    {
        Clear();
        GC.SuppressFinalize(this);
    }
}
