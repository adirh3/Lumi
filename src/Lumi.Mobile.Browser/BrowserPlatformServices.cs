using System.Net;
using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Styling;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using Lumi.Remote.Protocol;

namespace Lumi.Mobile.Browser;

internal sealed class BrowserHostEnvironment(string origin) : IMobileHostEnvironment
{
    public bool HasFixedEndpoint => true;

    public string? FixedBaseUrl { get; } = origin.TrimEnd('/');

    public string FixedEndpointName => "This Lumi PC";
}

internal sealed class BrowserDiscoveryClient : ILumiDiscoveryClient
{
    public Task<IReadOnlyList<RemoteBeacon>> DiscoverAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<RemoteBeacon>>([]);
}

internal sealed class BrowserSameOriginRouteVerifier : IRemoteRouteVerifier
{
    // Browsers cannot inspect the host OS route table. The PWA is fixed to its own origin, while
    // Lumi Desktop performs the authoritative loopback/Tailscale/LAN socket verification.
    public bool IsTrustedTailscaleRoute(IPAddress targetAddress) => true;
}

internal sealed class BrowserMobileSettingsStore(string origin) : IMobileSettingsStore
{
    private const string StorageKey = "lumi.mobile.connection.v1";

    public MobileConnectionSettings Load()
    {
        MobileConnectionSettings? settings = null;
        var json = BrowserInterop.GetStorageItem(StorageKey);
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                settings = JsonSerializer.Deserialize(
                    json,
                    MobileSettingsJsonContext.Default.MobileConnectionSettings);
            }
            catch (JsonException)
            {
            }
        }

        settings ??= new MobileConnectionSettings();
        settings.BaseUrl = origin;
        if (settings.DeviceId.Length == 0)
            settings.DeviceId = Guid.NewGuid().ToString("n");
        if (settings.DeviceName.Length == 0)
            settings.DeviceName = "Lumi Web App";
        return settings;
    }

    public void Save(MobileConnectionSettings settings)
    {
        settings.BaseUrl = origin;
        BrowserInterop.SetStorageItem(
            StorageKey,
            JsonSerializer.Serialize(
                settings,
                MobileSettingsJsonContext.Default.MobileConnectionSettings));
    }
}

internal sealed class BrowserProducedFileOpener : IProducedFileOpener
{
    public Task<bool> TryOpenAsync(
        string downloadedPath,
        string displayName,
        CancellationToken cancellationToken)
    {
        BrowserInterop.DownloadUrl(downloadedPath, displayName);
        return Task.FromResult(true);
    }
}

internal sealed class BrowserTextSelectionPresenter : ITextSelectionPresenter
{
    public void Show(string text) => BrowserInterop.ShowTextSelection(text);

    public void Dismiss() => BrowserInterop.DismissTextSelection();
}

internal sealed class BrowserNativeTextInputOverlayPresenter : INativeTextInputOverlayPresenter
{
    private readonly Dictionary<int, Session> _sessions = [];
    private int _nextId;

    public bool IsAvailable => true;

    public INativeTextInputOverlaySession Create(
        Action<string, int> textChanged,
        Func<Key, KeyModifiers, bool> keyPressed,
        Action<bool> focusChanged)
    {
        var id = Interlocked.Increment(ref _nextId);
        var session = new Session(
            this,
            id,
            textChanged,
            keyPressed,
            focusChanged);
        _sessions.Add(id, session);
        return session;
    }

    internal void SetTextFromBrowser(int id, string value, int caretIndex)
    {
        if (_sessions.TryGetValue(id, out var session))
            session.TextChanged(value, caretIndex);
    }

    internal bool HandleKeyFromBrowser(
        int id,
        string key,
        bool control,
        bool alt,
        bool shift,
        bool meta)
    {
        if (!_sessions.TryGetValue(id, out var session))
            return false;

        var parsedKey = key switch
        {
            "Enter" => Key.Enter,
            "Escape" => Key.Escape,
            _ => Key.None
        };
        if (parsedKey == Key.None)
            return false;

        var modifiers = KeyModifiers.None;
        if (control)
            modifiers |= KeyModifiers.Control;
        if (alt)
            modifiers |= KeyModifiers.Alt;
        if (shift)
            modifiers |= KeyModifiers.Shift;
        if (meta)
            modifiers |= KeyModifiers.Meta;
        return session.KeyPressed(parsedKey, modifiers);
    }

    internal void SetFocusFromBrowser(int id, bool focused)
    {
        if (_sessions.TryGetValue(id, out var session))
            session.FocusChanged(focused);
    }

    private void Remove(int id)
    {
        _sessions.Remove(id);
        BrowserInterop.DestroyNativeTextInput(id);
    }

    private sealed class Session(
        BrowserNativeTextInputOverlayPresenter owner,
        int id,
        Action<string, int> textChanged,
        Func<Key, KeyModifiers, bool> keyPressed,
        Action<bool> focusChanged)
        : INativeTextInputOverlaySession
    {
        private bool _disposed;

        public Action<string, int> TextChanged { get; } = textChanged;

        public Func<Key, KeyModifiers, bool> KeyPressed { get; } = keyPressed;

        public Action<bool> FocusChanged { get; } = focusChanged;

        public int CaretIndex => _disposed ? 0 : BrowserInterop.GetNativeTextInputCaretIndex(id);

        public void Show(
            Rect bounds,
            Rect clipBounds,
            string value,
            NativeTextInputOverlayOptions options)
        {
            if (_disposed)
                return;

            BrowserInterop.ShowNativeTextInput(
                id,
                bounds.X,
                bounds.Y,
                bounds.Width,
                bounds.Height,
                clipBounds.X,
                clipBounds.Y,
                clipBounds.Width,
                clipBounds.Height,
                value,
                options.Placeholder,
                options.IsMultiline,
                options.MaxLength,
                options.InputMode,
                options.EnterKeyHint,
                options.IsSensitive,
                options.AutoCapitalization,
                options.ShowSuggestions,
                options.IsEnabled,
                options.FontFamily,
                options.FontSize,
                options.FontWeight,
                options.FontStyle,
                options.LineHeight,
                options.LetterSpacing,
                options.Padding.Top,
                options.Padding.Right,
                options.Padding.Bottom,
                options.Padding.Left,
                options.TextAlignment,
                options.Direction,
                options.IsDark);
        }

        public void Hide()
        {
            if (!_disposed)
                BrowserInterop.HideNativeTextInput(id);
        }

        public void FocusAt(int caretIndex)
        {
            if (!_disposed)
                BrowserInterop.FocusNativeTextInput(id, caretIndex);
        }

        public void Blur()
        {
            if (!_disposed)
                BrowserInterop.BlurNativeTextInput(id);
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            owner.Remove(id);
        }
    }
}

internal sealed class BrowserRemoteDownloadStore : IRemoteDownloadStore
{
    private const long MarkdownImageCacheByteLimit = 64L * 1024 * 1024;
    private readonly object _sync = new();
    private readonly Dictionary<string, CachedObjectUrl> _locations = new(StringComparer.Ordinal);
    private readonly LinkedList<string> _usage = [];
    private long _cachedBytes;

    public string? TryGet(string category, string key)
    {
        lock (_sync)
        {
            if (!_locations.TryGetValue($"{category}:{key}", out var cached))
                return null;

            cached.References++;
            Touch(cached);
            return cached.Location;
        }
    }

    public async Task<string> StoreAsync(
        string category,
        string key,
        string fileName,
        string contentType,
        Stream source,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var rented = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(rented.AsMemory(0, rented.Length), cancellationToken);
                if (read == 0)
                    break;
                total += read;
                if (total > maxBytes)
                    throw new InvalidDataException("The download exceeds the browser limit.");
                await buffer.WriteAsync(rented.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(rented);
        }

        var location = BrowserInterop.CreateObjectUrl(buffer.ToArray(), contentType);
        if (!string.Equals(category, "markdown-images", StringComparison.Ordinal))
            return location;

        var cacheKey = $"{category}:{key}";
        var locationsToRevoke = new List<string>();
        string result;
        lock (_sync)
        {
            if (_locations.TryGetValue(cacheKey, out var cached))
            {
                cached.References++;
                Touch(cached);
                locationsToRevoke.Add(location);
                result = cached.Location;
            }
            else
            {
                var usageNode = _usage.AddLast(cacheKey);
                _locations.Add(cacheKey, new CachedObjectUrl(location, total, usageNode)
                {
                    References = 1
                });
                _cachedBytes += total;
                result = location;
                CollectEvictions(locationsToRevoke);
            }
        }

        foreach (var expired in locationsToRevoke)
            BrowserInterop.RevokeObjectUrl(expired);

        return result;
    }

    public void Release(string category, string key)
    {
        var locationsToRevoke = new List<string>();
        lock (_sync)
        {
            if (!_locations.TryGetValue($"{category}:{key}", out var cached))
                return;

            if (cached.References > 0)
                cached.References--;
            CollectEvictions(locationsToRevoke);
        }

        foreach (var expired in locationsToRevoke)
            BrowserInterop.RevokeObjectUrl(expired);
    }

    private void Touch(CachedObjectUrl cached)
    {
        _usage.Remove(cached.UsageNode);
        _usage.AddLast(cached.UsageNode);
    }

    private void CollectEvictions(List<string> locationsToRevoke)
    {
        while (_cachedBytes > MarkdownImageCacheByteLimit)
        {
            var candidateNode = _usage.First;
            while (candidateNode is not null
                   && _locations[candidateNode.Value].References > 0)
            {
                candidateNode = candidateNode.Next;
            }

            if (candidateNode is null)
                return;

            var expired = _locations[candidateNode.Value];
            _usage.Remove(candidateNode);
            _locations.Remove(candidateNode.Value);
            _cachedBytes -= expired.Size;
            locationsToRevoke.Add(expired.Location);
        }
    }

    private sealed class CachedObjectUrl(
        string location,
        long size,
        LinkedListNode<string> usageNode)
    {
        public string Location { get; } = location;

        public long Size { get; } = size;

        public LinkedListNode<string> UsageNode { get; } = usageNode;

        public int References { get; set; }
    }
}

internal static partial class BrowserInterop
{
    internal static BrowserNativeTextInputOverlayPresenter? NativeTextInputOverlayPresenter { get; set; }

    [JSImport("getOrigin", "./browserHost.js")]
    internal static partial string GetOrigin();

    [JSImport("getStorageItem", "./browserHost.js")]
    internal static partial string? GetStorageItem(string key);

    [JSImport("setStorageItem", "./browserHost.js")]
    internal static partial void SetStorageItem(string key, string value);

    [JSImport("downloadUrl", "./browserHost.js")]
    internal static partial void DownloadUrl(string url, string fileName);

    [JSImport("createObjectUrl", "./browserHost.js")]
    internal static partial string CreateObjectUrl(byte[] bytes, string contentType);

    [JSImport("revokeObjectUrl", "./browserHost.js")]
    internal static partial void RevokeObjectUrl(string url);

    [JSImport("showTextSelection", "./browserHost.js")]
    internal static partial void ShowTextSelection(string text);

    [JSImport("dismissTextSelection", "./browserHost.js")]
    internal static partial void DismissTextSelection();

    [JSImport("showNativeTextInput", "./browserHost.js")]
    internal static partial void ShowNativeTextInput(
        int id,
        double x,
        double y,
        double width,
        double height,
        double clipX,
        double clipY,
        double clipWidth,
        double clipHeight,
        string value,
        string placeholder,
        bool multiline,
        int maxLength,
        string inputMode,
        string enterKeyHint,
        bool sensitive,
        bool autoCapitalization,
        bool showSuggestions,
        bool enabled,
        string fontFamily,
        double fontSize,
        int fontWeight,
        string fontStyle,
        double lineHeight,
        double letterSpacing,
        double paddingTop,
        double paddingRight,
        double paddingBottom,
        double paddingLeft,
        string textAlignment,
        string direction,
        bool dark);

    [JSImport("hideNativeTextInput", "./browserHost.js")]
    internal static partial void HideNativeTextInput(int id);

    [JSImport("destroyNativeTextInput", "./browserHost.js")]
    internal static partial void DestroyNativeTextInput(int id);

    [JSImport("getNativeTextInputCaretIndex", "./browserHost.js")]
    internal static partial int GetNativeTextInputCaretIndex(int id);

    [JSImport("focusNativeTextInput", "./browserHost.js")]
    internal static partial void FocusNativeTextInput(int id, int caretIndex);

    [JSImport("blurNativeTextInput", "./browserHost.js")]
    internal static partial void BlurNativeTextInput(int id);

    [JSExport]
    internal static void SetApplicationActive(bool active)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is not ISingleViewApplicationLifetime { MainView: MobileShellView shell })
        {
            return;
        }

        if (active)
            shell.NotifyApplicationActivated();
        else
            shell.NotifyApplicationDeactivated();
    }

    [JSExport]
    internal static void SetViewportInsets(
        double top,
        double right,
        double bottom,
        double left,
        double keyboardInset,
        double viewportHeight)
    {
        if (Avalonia.Application.Current?.ApplicationLifetime
            is ISingleViewApplicationLifetime { MainView: MobileShellView shell })
        {
            shell.ApplyPlatformInsets(
                new Avalonia.Thickness(left, top, right, bottom),
                keyboardInset,
                viewportHeight);
        }
    }

    [JSExport]
    internal static void SetNativeTextInputText(int id, string text, int caretIndex) =>
        NativeTextInputOverlayPresenter?.SetTextFromBrowser(id, text, caretIndex);

    [JSExport]
    internal static bool HandleNativeTextInputKey(
        int id,
        string key,
        bool control,
        bool alt,
        bool shift,
        bool meta) =>
        NativeTextInputOverlayPresenter?.HandleKeyFromBrowser(
            id,
            key,
            control,
            alt,
            shift,
            meta) == true;

    [JSExport]
    internal static void SetNativeTextInputFocus(int id, bool focused) =>
        NativeTextInputOverlayPresenter?.SetFocusFromBrowser(id, focused);
}
