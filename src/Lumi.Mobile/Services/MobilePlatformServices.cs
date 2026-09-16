using System.Security.Cryptography;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using Lumi.Mobile.Views;

namespace Lumi.Mobile.Services;

public interface IProducedFileOpener
{
    Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken);
}

public interface ITextSelectionPresenter
{
    void Show(string text);

    void Dismiss();
}

internal readonly record struct NativeTextInputOverlayOptions(
    bool IsMultiline,
    int MaxLength,
    string Placeholder,
    string InputMode,
    string EnterKeyHint,
    bool IsSensitive,
    bool AutoCapitalization,
    bool ShowSuggestions,
    bool IsEnabled,
    string FontFamily,
    double FontSize,
    int FontWeight,
    string FontStyle,
    double LineHeight,
    double LetterSpacing,
    Thickness Padding,
    string TextAlignment,
    string Direction,
    bool IsDark);

internal interface INativeTextInputOverlaySession : IDisposable
{
    int CaretIndex { get; }

    void Show(
        Rect bounds,
        Rect clipBounds,
        string value,
        NativeTextInputOverlayOptions options);

    void Hide();

    void FocusAt(int caretIndex);

    void Blur();
}

internal interface INativeTextInputOverlayPresenter
{
    bool IsAvailable { get; }

    INativeTextInputOverlaySession Create(
        Action<string, int> textChanged,
        Func<Key, KeyModifiers, bool> keyPressed,
        Action<bool> focusChanged);
}

public interface IMobileHostEnvironment
{
    bool HasFixedEndpoint { get; }

    string? FixedBaseUrl { get; }

    string FixedEndpointName { get; }
}

public interface IRemoteDownloadStore
{
    string? TryGet(string category, string key);

    Task<string> StoreAsync(
        string category,
        string key,
        string fileName,
        string contentType,
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken);

    void Release(string category, string key);
}

internal interface INativeComposerEditorFactory
{
    bool IsAvailable { get; }

    IPlatformHandle Create(
        NativeComposerEditorHost host,
        IPlatformHandle parent);

    void Destroy(
        NativeComposerEditorHost host,
        IPlatformHandle control);

    void ApplyText(NativeComposerEditorHost host, string text);

    void ApplyPlaceholder(
        NativeComposerEditorHost host,
        string placeholder);

    int GetCaretIndex(NativeComposerEditorHost host);

    void FocusAt(NativeComposerEditorHost host, int caretIndex);

    void FocusAtEnd(NativeComposerEditorHost host);

    void Blur(NativeComposerEditorHost host);
}

public static class MobilePlatformServices
{
    private static readonly object TextSelectionGestureSync = new();
    private static string? _armedTextSelection;
    private static bool _isTextSelectionGestureActive;

    public static IProducedFileOpener ProducedFileOpener { get; set; } =
        new DefaultProducedFileOpener();

    public static ITextSelectionPresenter TextSelectionPresenter { get; set; } =
        new DefaultTextSelectionPresenter();

    internal static INativeTextInputOverlayPresenter TextInputOverlayPresenter { get; set; } =
        new DefaultNativeTextInputOverlayPresenter();

    public static IMobileHostEnvironment HostEnvironment { get; set; } =
        new DefaultMobileHostEnvironment();

    public static string? DeviceNameOverride { get; set; }

    internal static INativeComposerEditorFactory NativeComposerEditorFactory { get; set; } =
        new DefaultNativeComposerEditorFactory();

    public static void ResetTextSelectionPresenter(
        ITextSelectionPresenter? expected = null)
    {
        if (expected is null || ReferenceEquals(TextSelectionPresenter, expected))
            TextSelectionPresenter = new DefaultTextSelectionPresenter();
    }

    internal static void ResetTextInputOverlayPresenter(
        INativeTextInputOverlayPresenter? expected = null)
    {
        if (expected is null || ReferenceEquals(TextInputOverlayPresenter, expected))
            TextInputOverlayPresenter = new DefaultNativeTextInputOverlayPresenter();
    }

    internal static void ResetNativeComposerEditorFactory(
        INativeComposerEditorFactory? expected = null)
    {
        if (expected is null || ReferenceEquals(NativeComposerEditorFactory, expected))
            NativeComposerEditorFactory = new DefaultNativeComposerEditorFactory();
    }

    internal static void ArmTextSelectionGesture(string text)
    {
        lock (TextSelectionGestureSync)
        {
            _armedTextSelection = text;
            _isTextSelectionGestureActive = true;
        }
    }

    internal static string? TakeTextSelectionGesture()
    {
        lock (TextSelectionGestureSync)
        {
            var text = _armedTextSelection;
            _armedTextSelection = null;
            return text;
        }
    }

    internal static bool IsTextSelectionGestureActive()
    {
        lock (TextSelectionGestureSync)
            return _isTextSelectionGestureActive;
    }

    internal static void ClearTextSelectionGesture()
    {
        lock (TextSelectionGestureSync)
        {
            _armedTextSelection = null;
            _isTextSelectionGestureActive = false;
        }
    }
}

internal static class ProducedFileExport
{
    public static async Task<long> CopyAndVerifyAsync(
        string sourcePath,
        Func<Task<Stream>> openWriteAsync,
        Func<Task<Stream>> openReadAsync,
        CancellationToken cancellationToken)
    {
        var expectedLength = new FileInfo(sourcePath).Length;
        byte[] expectedHash;
        var buffer = new byte[64 * 1024];
        using var sourceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = File.OpenRead(sourcePath))
        await using (var output = await openWriteAsync())
        {
            if (output.CanSeek)
                output.SetLength(0);

            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                sourceHash.AppendData(buffer, 0, read);
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }

            await output.FlushAsync(cancellationToken);
            expectedHash = sourceHash.GetHashAndReset();
        }

        long actualLength = 0;
        byte[] actualHash;
        using var destinationHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var verification = await openReadAsync())
        {
            while (true)
            {
                var read = await verification.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                    break;

                destinationHash.AppendData(buffer, 0, read);
                actualLength += read;
            }

            actualHash = destinationHash.GetHashAndReset();
        }

        if (actualLength != expectedLength
            || !CryptographicOperations.FixedTimeEquals(expectedHash, actualHash))
        {
            throw new IOException(
                $"The saved file did not match the download ({actualLength} of {expectedLength} bytes).");
        }

        return actualLength;
    }
}

internal sealed class DefaultProducedFileOpener : IProducedFileOpener
{
    public Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken) =>
        Task.FromResult(false);
}

internal sealed class DefaultTextSelectionPresenter : ITextSelectionPresenter
{
    public void Show(string text)
    {
    }

    public void Dismiss()
    {
    }
}

internal sealed class DefaultNativeTextInputOverlayPresenter : INativeTextInputOverlayPresenter
{
    public bool IsAvailable => false;

    public INativeTextInputOverlaySession Create(
        Action<string, int> textChanged,
        Func<Key, KeyModifiers, bool> keyPressed,
        Action<bool> focusChanged) =>
        throw new PlatformNotSupportedException(
            "A native text input overlay is not registered on this platform.");
}

internal sealed class DefaultMobileHostEnvironment : IMobileHostEnvironment
{
    public bool HasFixedEndpoint => false;

    public string? FixedBaseUrl => null;

    public string FixedEndpointName => "Lumi";
}

internal sealed class DefaultNativeComposerEditorFactory : INativeComposerEditorFactory
{
    public bool IsAvailable => false;

    public IPlatformHandle Create(
        NativeComposerEditorHost host,
        IPlatformHandle parent) =>
        throw new PlatformNotSupportedException(
            "A native composer editor is not registered on this platform.");

    public void Destroy(
        NativeComposerEditorHost host,
        IPlatformHandle control)
    {
    }

    public void ApplyText(NativeComposerEditorHost host, string text)
    {
    }

    public void ApplyPlaceholder(
        NativeComposerEditorHost host,
        string placeholder)
    {
    }

    public int GetCaretIndex(NativeComposerEditorHost host) => host.Text.Length;

    public void FocusAt(NativeComposerEditorHost host, int caretIndex)
    {
    }

    public void FocusAtEnd(NativeComposerEditorHost host)
    {
    }

    public void Blur(NativeComposerEditorHost host) => host.SetInputFocusFromNative(false);
}
