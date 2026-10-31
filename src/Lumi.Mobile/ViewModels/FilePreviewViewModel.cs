using System.Diagnostics;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Mobile.Services;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;
using Strings = Lumi.Mobile.Localization.ChatSessionStrings;

namespace Lumi.Mobile.ViewModels;

/// <summary>A file Lumi made, brought over from the PC, or why it could not be.</summary>
public readonly record struct ProducedFileDownload(string? Path, string? Error);

/// <summary>Narrow seam so a preview can fetch its file without owning the transport.</summary>
public interface IRemoteProducedFileSink
{
    Task<ProducedFileDownload> DownloadProducedFileAsync(
        Guid chatId,
        Guid messageId,
        string fileName,
        CancellationToken cancellationToken);
}

/// <summary>
/// One file Lumi made, previewed in its own pane as the desktop's Workspace previews it: images, Markdown,
/// code and text, CSV tables and (where the phone can draw them) PDF pages. Anything else says so and
/// offers another app.
/// </summary>
/// <remarks>
/// The preview owns what it loads — the downloaded copy, the decoded image and the open PDF — and gives
/// them back once it is closed and no view still shows it, so a pane sliding away never draws a
/// released bitmap.
/// </remarks>
public sealed partial class FilePreviewViewModel : ObservableObject
{
    /// <summary>Sharp at full screen on a tablet and through a 2× zoom on a phone, and no larger.</summary>
    internal static readonly PixelSize MaxImagePixels = new(2048, 3072);

    private readonly Func<CancellationToken, Task<ProducedFileDownload>> _download;
    private readonly IPdfPreviewRenderer _pdfRenderer;
    private readonly CancellationTokenSource _lifetime = new();
    private Task<ProducedFileDownload>? _downloadTask;
    private long _loadVersion;
    private int _presenters;
    private bool _released;

    public FilePreviewViewModel(
        RemoteAttachment file,
        Guid chatId,
        Func<CancellationToken, Task<ProducedFileDownload>> download,
        IPdfPreviewRenderer? pdfRenderer = null)
    {
        File = file;
        ChatId = chatId;
        _download = download;
        _pdfRenderer = pdfRenderer ?? MobilePlatformServices.PdfPreviewRenderer;
        FileName = string.IsNullOrWhiteSpace(file.FileName) ? Strings.Get("FilePreviewFile") : file.FileName;
        _kind = InitialKind();
    }

    public RemoteAttachment File { get; }

    public Guid ChatId { get; }

    public Guid? MessageId => File.MessageId;

    public string FileName { get; }

    /// <summary>The file's type as its chip names it ("PNG", "MD"), or "File" without an extension.</summary>
    public string TypeLabel => Path.GetExtension(FileName).TrimStart('.') is { Length: > 0 } extension
        ? extension.ToUpperInvariant()
        : Strings.Get("FilePreviewFile");

    /// <summary>"PDF · 12 pages · 1.4 MB": the type, what it holds once read, and its size once downloaded.</summary>
    public string Subtitle => string.Join(" · ",
        new[] { TypeLabel, Pdf?.PageCountText ?? Table?.RowCountText, SizeText }
            .Where(part => !string.IsNullOrEmpty(part)));

    /// <summary>The downloaded copy on this device, once there is one.</summary>
    public string? LocalPath { get; private set; }

    public bool IsClosed { get; private set; }

    [ObservableProperty] private FilePreviewKind _kind;

    [ObservableProperty] private bool _isLoading;

    [ObservableProperty] private string? _errorText;

    [ObservableProperty] private string? _sizeText;

    [ObservableProperty] private string? _text;

    [ObservableProperty] private string? _codeLanguage;

    [ObservableProperty] private bool _isTruncated;

    [ObservableProperty] private Bitmap? _image;

    [ObservableProperty] private CsvPreviewTable? _table;

    [ObservableProperty] private PdfPreviewViewModel? _pdf;

    /// <summary>An action that needs the file (open, share, save) is fetching or handing it over.</summary>
    [ObservableProperty] private bool _isActionRunning;

    /// <summary>A short word about the last action ("Saved a copy", or why it failed), shown over the file.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionMessage))]
    private string? _actionMessage;

    /// <summary>
    /// The pane is still opening. A file that has arrived waits behind the loading state until it has
    /// landed, so building a long file never stalls the motion.
    /// </summary>
    [ObservableProperty] private bool _isSettling;

    public bool HasActionMessage => !string.IsNullOrWhiteSpace(ActionMessage);

    public bool HasError => !string.IsNullOrWhiteSpace(ErrorText);

    public bool ShowLoading => !HasError && (IsLoading || (IsSettling && Kind != FilePreviewKind.Unsupported));

    private bool CanShowContent => !HasError && !IsSettling;

    public bool ShowImage => CanShowContent && Kind == FilePreviewKind.Image && Image is not null;

    public bool ShowMarkdown => CanShowContent && Kind == FilePreviewKind.Markdown && !string.IsNullOrEmpty(Text);

    public bool ShowCode => CanShowContent && Kind == FilePreviewKind.Code && !string.IsNullOrEmpty(Text);

    public bool ShowText => CanShowContent && Kind == FilePreviewKind.Text && !string.IsNullOrEmpty(Text);

    public bool ShowCsv => CanShowContent && Kind == FilePreviewKind.Csv && Table is not null;

    public bool ShowPdf => CanShowContent && Kind == FilePreviewKind.Pdf && Pdf is not null;

    public bool ShowUnsupported => !HasError && !IsLoading && Kind == FilePreviewKind.Unsupported;

    public bool ShowEmpty => CanShowContent && !IsLoading && IsTextual && Text is { Length: 0 };

    /// <summary>The file is on screen, so its actions float over it rather than standing in a card.</summary>
    public bool ShowContentActions =>
        ShowImage || ShowMarkdown || ShowCode || ShowText || ShowCsv || ShowPdf || ShowEmpty;

    private bool IsTextual => Kind is FilePreviewKind.Markdown or FilePreviewKind.Code or FilePreviewKind.Text;

    public bool ShowLimitNotice => CanShowContent && (IsTruncated || Table?.IsLimited == true || Pdf?.IsLimited == true);

    public string LimitNoticeText => Pdf?.IsLimited == true
        ? Strings.Get("FilePreviewPagesLimited", Pdf.Pages.Count)
        : Table?.IsLimited == true
            ? Strings.Get("FilePreviewRowsLimited", Table.Rows.Count)
            : Strings.Get("FilePreviewTruncated");

    /// <summary>This device can hand the file to another app's share sheet.</summary>
    public bool CanShare => MobilePlatformServices.ProducedFileOpener.CanShare;

    /// <summary>Opening already saves the file (a browser download), so Save would only repeat it.</summary>
    public bool CanSave => !MobilePlatformServices.ProducedFileOpener.OpensBySaving;

    public string OpenLabel => Strings.Get(
        MobilePlatformServices.ProducedFileOpener.OpensBySaving ? "FilePreviewDownload" : "FilePreviewOpenWith");

    private static readonly string[] StateProperties =
    [
        nameof(HasError), nameof(ShowLoading), nameof(ShowImage), nameof(ShowMarkdown), nameof(ShowCode),
        nameof(ShowText), nameof(ShowCsv), nameof(ShowPdf), nameof(ShowUnsupported), nameof(ShowEmpty),
        nameof(ShowContentActions), nameof(ShowLimitNotice), nameof(LimitNoticeText), nameof(Subtitle)
    ];

    partial void OnKindChanged(FilePreviewKind value) => NotifyState();

    partial void OnIsLoadingChanged(bool value) => NotifyState();

    partial void OnErrorTextChanged(string? value) => NotifyState();

    partial void OnSizeTextChanged(string? value) => OnPropertyChanged(nameof(Subtitle));

    partial void OnTextChanged(string? value) => NotifyState();

    partial void OnIsTruncatedChanged(bool value) => NotifyState();

    partial void OnImageChanged(Bitmap? value) => NotifyState();

    partial void OnTableChanged(CsvPreviewTable? value) => NotifyState();

    partial void OnPdfChanged(PdfPreviewViewModel? value) => NotifyState();

    partial void OnIsSettlingChanged(bool value) => NotifyState();

    private void NotifyState()
    {
        foreach (var property in StateProperties)
            OnPropertyChanged(property);
    }

    /// <summary>Downloads (if it can show the file) and reads it. Safe to call again to retry.</summary>
    public async Task LoadAsync()
    {
        if (IsClosed)
            return;

        var version = ++_loadVersion;
        ErrorText = null;
        Kind = InitialKind();
        if (Kind == FilePreviewKind.Unsupported)
        {
            IsLoading = false;
            return;
        }

        IsLoading = true;
        try
        {
            var download = await EnsureDownloadedAsync().ConfigureAwait(true);
            if (!IsCurrent(version))
                return;
            if (download.Path is not { } path)
            {
                ErrorText = download.Error ?? Strings.Get("DownloadFileFailed");
                return;
            }

            await ReadAsync(path, version).ConfigureAwait(true);
        }
        catch (OperationCanceledException) when (IsClosed)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrent(version))
                ErrorText = ex is OperationCanceledException ? Strings.Get("ConnectionChangedAction") : Describe(ex);
            Trace.TraceWarning($"[Mobile] File preview failed for {FileName}: {ex.Message}");
        }
        finally
        {
            if (IsCurrent(version))
                IsLoading = false;
        }
    }

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    /// <summary>
    /// The file on this device, downloading it first if the preview had no reason to (a format the
    /// phone can't draw). One download serves the preview and every action.
    /// </summary>
    public async Task<ProducedFileDownload> EnsureDownloadedAsync()
    {
        if (IsClosed)
            return new ProducedFileDownload(null, null);
        if (LocalPath is { } existing)
            return new ProducedFileDownload(existing, null);

        if (_downloadTask is not { } task || task.IsFaulted || task.IsCanceled
            || (task.IsCompletedSuccessfully && task.Result.Path is null))
        {
            _downloadTask = task = _download(_lifetime.Token);
        }

        var result = await task.ConfigureAwait(true);
        if (result.Path is { } path)
        {
            if (IsClosed)
            {
                DeleteQuietly(path);
                return new ProducedFileDownload(null, null);
            }

            if (LocalPath is null)
            {
                LocalPath = path;
                SizeText = FilePreviewContent.FormatSize(new FileInfo(path).Length);
            }
        }

        return result.Path is null ? result : new ProducedFileDownload(LocalPath, null);
    }

    private async Task ReadAsync(string path, long version)
    {
        var token = _lifetime.Token;
        switch (Kind)
        {
            case FilePreviewKind.Image:
                var bitmap = await StrataImageViewport.LoadImageAsync(path, MaxImagePixels, token).ConfigureAwait(true);
                if (!IsCurrent(version))
                {
                    bitmap.Dispose();
                    return;
                }
                Image = bitmap;
                return;

            case FilePreviewKind.Pdf:
                var pdf = await PdfPreviewViewModel.OpenAsync(_pdfRenderer, path, token).ConfigureAwait(true);
                if (!IsCurrent(version))
                {
                    pdf.Dispose();
                    return;
                }
                Pdf = pdf;
                return;

            case FilePreviewKind.Csv:
                var csvText = await FilePreviewText.ReadAsync(path, token).ConfigureAwait(true);
                if (!IsCurrent(version))
                    return;
                if (csvText is null)
                {
                    Kind = FilePreviewKind.Unsupported;
                    return;
                }
                var delimiter = Path.GetExtension(FileName).Equals(".tsv", StringComparison.OrdinalIgnoreCase)
                    ? '\t' : (char?)null;
                var table = await Task.Run(() => CsvPreviewTable.Parse(csvText.Text, csvText.IsTruncated, delimiter, token), token)
                    .ConfigureAwait(true);
                if (!IsCurrent(version))
                    return;
                IsTruncated = csvText.IsTruncated && !table.IsLimited;
                Table = table;
                return;

            default:
                var text = await FilePreviewText.ReadAsync(path, token).ConfigureAwait(true);
                if (!IsCurrent(version))
                    return;
                if (text is null)
                {
                    Kind = FilePreviewKind.Unsupported;
                    return;
                }
                if (Kind == FilePreviewKind.Unknown)
                    Kind = FilePreviewKind.Text;
                CodeLanguage = Kind == FilePreviewKind.Code && text.Text.Length <= FilePreviewContent.MaxHighlightedCharacters
                    ? FilePreviewContent.CodeLanguage(FileName)
                    : null;
                IsTruncated = text.IsTruncated;
                Text = text.Text;
                return;
        }
    }

    private FilePreviewKind InitialKind()
    {
        var kind = FilePreviewContent.Classify(FileName);
        return kind == FilePreviewKind.Pdf && !_pdfRenderer.IsAvailable ? FilePreviewKind.Unsupported : kind;
    }

    private bool IsCurrent(long version) => !IsClosed && version == _loadVersion;

    private static string Describe(Exception ex) => ex switch
    {
        InvalidDataException or CsvPreviewFormatException => Strings.Get("FilePreviewInvalid"),
        IOException or UnauthorizedAccessException => Strings.Get("FilePreviewUnreadable"),
        _ => Strings.Get("FilePreviewFailed")
    };

    // ── Ownership ────────────────────────────────────────────────────────────────────────────

    /// <summary>A view started showing this preview; its resources stay until the view lets go.</summary>
    internal void AttachPresenter() => _presenters++;

    /// <summary>A view stopped showing this preview, so a closed one can give back what it holds.</summary>
    internal void DetachPresenter()
    {
        if (_presenters > 0)
            _presenters--;
        if (IsClosed && _presenters == 0)
            ReleaseResources();
    }

    /// <summary>The preview left the chat. Work stops at once; resources go once nothing shows them.</summary>
    internal void Close()
    {
        if (IsClosed)
            return;

        IsClosed = true;
        _lifetime.Cancel();
        if (_presenters == 0)
            ReleaseResources();
    }

    private void ReleaseResources()
    {
        if (_released)
            return;

        _released = true;
        var image = Image;
        Image = null;
        image?.Dispose();
        var pdf = Pdf;
        Pdf = null;
        pdf?.Dispose();
        if (LocalPath is { } path)
            DeleteQuietly(path);
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (System.IO.File.Exists(path))
                System.IO.File.Delete(path);
        }
        catch (IOException ex)
        {
            Trace.TraceWarning($"[Mobile] Preview copy could not be deleted: {ex.Message}");
        }
        catch (UnauthorizedAccessException ex)
        {
            Trace.TraceWarning($"[Mobile] Preview copy could not be deleted: {ex.Message}");
        }
    }
}
