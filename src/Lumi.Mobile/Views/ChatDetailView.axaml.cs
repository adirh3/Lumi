using System;
using System.Buffers;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.VisualTree;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Behaviors;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

public partial class ChatDetailView : UserControl
{
    private MobilePresenceController? _presence;
    private MobileShellViewModel? _shell;
    private StrataChatComposer? _composer;
    private NativeComposerEditorHost? _nativeComposerEditor;
    private TextBox? _sharedComposerInput;

    /// <summary>Every collection and item we have hooked, so detach is exact and nothing leaks.</summary>
    private readonly HashSet<TranscriptTurnViewModel> _observedTurns =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TranscriptItemViewModel> _observedItems =
        new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _composerChipChildren;
    private bool _composerChipUpdateQueued;
    /// <summary>
    /// Coalesces streaming follow-ups into one per frame.
    ///
    /// <para>Streaming raises a property change per token. Posting a dispatcher job for each — which
    /// is what the first version did — queues hundreds of jobs deep during a long answer, and each
    /// one lands after the layout it was meant to react to. The result was a transcript that lurched,
    /// fought the finger, and drifted behind the text. Strata's own scroll queue is already
    /// coalesced, so the fix is to stop re-queuing on top of it and simply not call it more than
    /// once per frame.</para>
    /// </summary>
    private bool _followQueued;
    private bool _transcriptContentChanged;
    private MobileEntrance? _transcriptEntrance;
    private MobileEntrance? _welcomeEntrance;
    private int _chatEntranceVersion;
    private bool _awaitingChatEntrance;
    private IDisposable? _loadingDelay;
    private bool _transcriptWidthHeld;

    public ChatDetailView()
    {
        InitializeComponent();
        AddHandler(PointerPressedEvent, OnSurfacePointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        // The ambient field is created in code rather than XAML because the controller owns it: it
        // has to insert it as the bottom-most child and keep it out of the hit-test path.
        if (this.FindControl<Panel>("ChatRoot") is { } root)
            _presence = new MobilePresenceController(root);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(AttachComposerChipObserver, DispatcherPriority.Loaded);
        Dispatcher.UIThread.Post(AttachNativeComposerEditor, DispatcherPriority.Loaded);
        if (_shell?.Chat.IsInitialLoading == true)
            OnChatSurfaceReset();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        SetTranscriptMotionWidth(false);
        DetachNativeComposerEditor();
        DetachComposerChipObserver();
        _loadingDelay?.Dispose();
        _loadingDelay = null;
        _chatEntranceVersion++;
        _transcriptEntrance?.Dispose();
        _transcriptEntrance = null;
        _welcomeEntrance?.Dispose();
        _welcomeEntrance = null;
        _awaitingChatEntrance = false;
        _transcriptContentChanged = false;
        if (this.FindControl<Border>("ChatTranscriptSideInset") is { } transcript)
            transcript.IsHitTestVisible = true;
        base.OnDetachedFromVisualTree(e);
    }

    private void AttachNativeComposerEditor()
    {
        DetachNativeComposerEditor();
        if (!this.IsAttachedToVisualTree()
            || this.FindControl<StrataChatComposer>("Composer") is not { } composer)
        {
            return;
        }

        _composer = composer;
        composer.PropertyChanged += OnComposerPropertyChanged;
        composer.GotFocus += OnComposerGotFocus;
        composer.LostFocus += OnComposerLostFocus;
        composer.SendRequested += OnComposerActionRequested;
        composer.StopRequested += OnComposerActionRequested;
        composer.StopAndSendRequested += OnComposerActionRequested;
        composer.LayoutUpdated += OnComposerLayoutUpdated;
        if (!MobilePlatformServices.NativeComposerEditorFactory.IsAvailable)
        {
            _sharedComposerInput = composer.GetVisualDescendants().OfType<TextBox>()
                .FirstOrDefault(input => input.Name == "PART_Input");
            if (_sharedComposerInput is not null)
                _sharedComposerInput.Classes.CollectionChanged += OnSharedEditorClassesChanged;
            UpdateComposerFocus();
            return;
        }

        var editor = new NativeComposerEditorHost
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Placeholder = composer.Placeholder,
            Text = composer.PromptText ?? ""
        };
        _nativeComposerEditor = editor;
        editor.PropertyChanged += OnNativeComposerEditorPropertyChanged;
        editor.InputFocusChanged += OnNativeInputFocusChanged;
        composer.EditorContent = editor;
        UpdateNativeComposerVisibility();
        UpdateComposerFocus();
    }

    private void DetachNativeComposerEditor()
    {
        if (_composer is not null)
        {
            _composer.PropertyChanged -= OnComposerPropertyChanged;
            _composer.GotFocus -= OnComposerGotFocus;
            _composer.LostFocus -= OnComposerLostFocus;
            _composer.SendRequested -= OnComposerActionRequested;
            _composer.StopRequested -= OnComposerActionRequested;
            _composer.StopAndSendRequested -= OnComposerActionRequested;
            _composer.LayoutUpdated -= OnComposerLayoutUpdated;
        }
        if (_sharedComposerInput is not null)
            _sharedComposerInput.Classes.CollectionChanged -= OnSharedEditorClassesChanged;
        if (_nativeComposerEditor is not null)
        {
            _nativeComposerEditor.PropertyChanged -= OnNativeComposerEditorPropertyChanged;
            _nativeComposerEditor.InputFocusChanged -= OnNativeInputFocusChanged;
        }
        if (_composer is not null
            && ReferenceEquals(_composer.EditorContent, _nativeComposerEditor))
        {
            _composer.EditorContent = null;
            _composer.IsEditorContentVisible = true;
        }

        _nativeComposerEditor = null;
        _sharedComposerInput = null;
        _composer = null;
    }

    private void OnComposerPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == StrataChatComposer.PromptTextProperty)
        {
            _nativeComposerEditor?.SetCurrentValue(
                NativeComposerEditorHost.TextProperty,
                e.GetNewValue<string?>() ?? "");
            UpdateComposerFocus();
        }
    }

    private void OnComposerGotFocus(object? sender, RoutedEventArgs e)
    {
        if (e.Source is TextBox || _composer?.IsCompact == false)
            UpdateComposerFocus();
    }

    private void OnComposerLostFocus(object? sender, RoutedEventArgs e) =>
        Dispatcher.UIThread.Post(UpdateComposerFocus, DispatcherPriority.Input);

    private void OnSharedEditorClassesChanged(object? sender, NotifyCollectionChangedEventArgs e) =>
        UpdateComposerFocus();

    private void OnNativeInputFocusChanged(bool focused)
    {
        // Unhandled native key-up events must not activate the last focused Avalonia button.
        if (focused)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        UpdateComposerFocus();
    }

    private void UpdateComposerFocus()
    {
        if (_composer is null)
            return;

        var focused = _sharedComposerInput?.IsFocused == true
                      || _nativeComposerEditor?.IsInputFocused == true
                      || _sharedComposerInput?.Classes.Contains("native-input-focused") == true
                      || _composer is { IsCompact: false, IsKeyboardFocusWithin: true };
        _composer.IsCompact = !focused && string.IsNullOrEmpty(_composer.PromptText)
                              && _shell?.Chat.HasAttachments != true;
    }

    private void OnComposerActionRequested(object? sender, RoutedEventArgs e) => ReleaseComposerFocus();

    private void OnSurfacePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_composer is null || e.Source is not Visual source
            || ReferenceEquals(source, _composer) || source.GetVisualAncestors().Contains(_composer))
            return;
        ReleaseComposerFocus();
    }

    private void ReleaseComposerFocus()
    {
        if (_composer?.IsKeyboardFocusWithin == true)
            TopLevel.GetTopLevel(this)?.FocusManager?.Focus(null);
        if (_sharedComposerInput is not null)
            NativeTextInputOverlay.Blur(_sharedComposerInput);
        _nativeComposerEditor?.Blur();
        UpdateComposerFocus();
    }

    private void OnComposerLayoutUpdated(object? sender, EventArgs e)
    {
        if (_shell?.IsWelcomeVisible == true
            && _composer?.TranslatePoint(default, this) is { } origin
            && this.FindControl<Border>("WelcomeSideInset") is { } welcome)
        {
            welcome.Margin = new Thickness(0, 0, 0, Math.Max(0, Bounds.Height - origin.Y + 8));
        }
    }

    private void OnNativeComposerEditorPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == NativeComposerEditorHost.TextProperty
            && _composer is { } composer)
        {
            composer.SetCurrentValue(
                StrataChatComposer.PromptTextProperty,
                e.GetNewValue<string?>() ?? "");
        }
    }

    private void UpdateNativeComposerVisibility()
    {
        var visible = ShouldShowNativeComposerEditor(_shell);
        if (!visible)
            ReleaseComposerFocus();
        if (_nativeComposerEditor is not null)
        {
            if (visible && _composer is not null)
                _composer.IsEditorContentVisible = true;
            _nativeComposerEditor.IsVisible = visible;
            if (!visible && _composer is not null)
                _composer.IsEditorContentVisible = false;
        }
    }

    internal static bool ShouldShowNativeComposerEditor(MobileShellViewModel? shell) =>
        shell is
        {
            IsChatPage: true,
            IsDrawerOverlay: false,
            IsNavigationCoveringContent: false,
            IsModalSheetPresented: false,
            IsChatActionsOpen: false,
            HasPageOverlay: false
        }
        && !shell.Chat.HasOpenSheet;

    private void AttachComposerChipObserver()
    {
        DetachComposerChipObserver();
        var chipRow = this.FindControl<StrataChatComposer>("Composer")
            ?.GetVisualDescendants()
            .OfType<WrapPanel>()
            .FirstOrDefault(panel => panel.Name == "PART_ChipsRow");
        if (chipRow?.Children is not INotifyCollectionChanged children)
            return;

        _composerChipChildren = children;
        children.CollectionChanged += OnComposerChipChildrenChanged;
        ApplyProgrammaticChipSize();
    }

    private void DetachComposerChipObserver()
    {
        if (_composerChipChildren is not null)
            _composerChipChildren.CollectionChanged -= OnComposerChipChildrenChanged;
        _composerChipChildren = null;
        _composerChipUpdateQueued = false;
    }

    private void OnComposerChipChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_composerChipUpdateQueued)
            return;

        _composerChipUpdateQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _composerChipUpdateQueued = false;
            if (this.IsAttachedToVisualTree())
                ApplyProgrammaticChipSize();
        }, DispatcherPriority.Loaded);
    }

    private void ApplyProgrammaticChipSize()
    {
        if (this.FindControl<StrataChatComposer>("Composer") is not { } composer)
            return;

        foreach (var button in composer.GetVisualDescendants()
                     .OfType<Button>()
                     .Where(candidate => candidate.Classes.Contains("chip-remove")))
        {
            button.Width = 48;
            button.Height = 48;
            button.MinWidth = 48;
            button.MinHeight = 48;
            button.CornerRadius = new CornerRadius(24);
        }
    }

    /// <summary>
    /// Picking a file needs a <see cref="TopLevel"/>, which is a view concern — so the command lives
    /// here and hands the bytes to the view model, which owns the upload.
    ///
    /// <para>This is an <c>async void</c> event handler, so it is a process boundary: anything that
    /// escapes it is rethrown on the synchronization context and takes the app down. On Android the
    /// picker alone can raise cancellation, a Java <see cref="SystemException"/> for a revoked URI
    /// permission, or a marshalled Java throwable — none of which a narrow filter anticipates. It
    /// therefore catches everything and reports, rather than listing exception types.</para>
    /// </summary>
    private async void OnPickAttachment()
    {
        if (_shell is not { } shell || TopLevel.GetTopLevel(this) is not { StorageProvider: { } storage })
            return;

        ReleaseComposerFocus();
        try
        {
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Attach to Lumi",
                AllowMultiple = true
            });

            foreach (var file in files)
            {
                // Size is checked BEFORE reading. The upload encodes as base64, so a file costs
                // roughly 2.4x its size in managed memory before it ever leaves the phone — reading
                // a video first and validating afterwards is how you turn "too big" into an
                // OutOfMemoryException that no catch block can usefully recover from.
                var size = await TryGetSizeAsync(file);
                if (size > RemoteProtocol.MaxUploadBytes)
                {
                    shell.Chat.ErrorText = FileTooLargeMessage(file.Name, size);
                    continue;
                }

                await using var stream = await file.OpenReadAsync();
                var read = await ReadBoundedAsync(
                    stream,
                    checked((int)RemoteProtocol.MaxUploadBytes));
                using var buffer = read.Buffer;
                if (read.IsTooLarge)
                {
                    shell.Chat.ErrorText = FileTooLargeMessage(file.Name, size: null);
                    continue;
                }

                // GetBuffer avoids a second full-size copy that ToArray would make.
                await shell.Chat.AttachFileAsync(
                    file.Name,
                    buffer.GetBuffer().AsMemory(0, (int)buffer.Length));
            }
        }
        catch (Exception ex)
        {
            shell.Chat.ErrorText = ex is OutOfMemoryException
                ? "That file is too large for this phone to send."
                : "That file could not be attached.";
            Trace.TraceWarning($"[Mobile] Attach failed: {ex}");
        }
    }

    /// <summary>
    /// Reads at most <paramref name="maxBytes"/> plus one sentinel byte. Some Android document
    /// providers cannot report a size, so trusting metadata alone made a video-sized stream grow a
    /// MemoryStream until the process ran out of memory. Capacity grows geometrically but is capped
    /// at the same sentinel limit, and the successful buffer is passed directly to the uploader.
    /// </summary>
    internal static async Task<(MemoryStream Buffer, bool IsTooLarge)> ReadBoundedAsync(
        Stream source,
        int maxBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentOutOfRangeException.ThrowIfNegative(maxBytes);
        if (maxBytes == int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxBytes),
                "The upload limit must leave room for one sentinel byte.");
        }

        var limit = maxBytes + 1;
        var initialCapacity = Math.Min(64 * 1024, limit);
        var output = new MemoryStream(initialCapacity);
        var rented = ArrayPool<byte>.Shared.Rent(Math.Max(1, initialCapacity));

        try
        {
            while (output.Length < limit)
            {
                var remaining = limit - (int)output.Length;
                var read = await source.ReadAsync(
                    rented.AsMemory(0, Math.Min(rented.Length, remaining)),
                    cancellationToken);
                if (read == 0)
                {
                    output.Position = 0;
                    return (output, false);
                }

                EnsureCapacity(output, checked((int)output.Length + read), limit);
                output.Write(rented, 0, read);
            }

            output.Position = 0;
            return (output, true);
        }
        catch
        {
            output.Dispose();
            throw;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented, clearArray: true);
        }
    }

    private static void EnsureCapacity(MemoryStream stream, int required, int limit)
    {
        if (stream.Capacity >= required)
            return;

        if (required > limit)
            throw new InvalidOperationException("Required capacity exceeds the bounded read limit.");

        var current = Math.Max(1, stream.Capacity);
        stream.Capacity = (int)Math.Min(
            limit,
            Math.Max(required, (long)current * 2));
    }

    private static string FileTooLargeMessage(string fileName, long? size) =>
        size is { } knownSize
            ? $"{fileName} is too large to send ({knownSize / (1024 * 1024)} MB). The limit is "
              + $"{RemoteProtocol.MaxUploadBytes / (1024 * 1024)} MB."
            : $"{fileName} is too large to send. The limit is "
              + $"{RemoteProtocol.MaxUploadBytes / (1024 * 1024)} MB.";

    /// <summary>Best-effort size probe; a provider that cannot answer returns 0 so the read proceeds.</summary>
    private static async Task<long> TryGetSizeAsync(IStorageFile file)
    {
        try
        {
            var properties = await file.GetBasicPropertiesAsync();
            return (long?)properties.Size ?? 0;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Mobile] Could not read file size: {ex.Message}");
            return 0;
        }
    }

    /// <summary>
    /// Scrolls the model sheet to whichever row is currently selected.
    ///
    /// <para>Posted rather than run inline: the sheet's rows are realized as it opens, so at the
    /// moment the flag flips there is nothing laid out to scroll to yet.</para>
    /// </summary>
    private void ScrollSelectedModelIntoView() =>
        Dispatcher.UIThread.Post(
            () =>
            {
                if (this.FindControl<ItemsControl>("ModelSheetList") is not { } list)
                    return;

                var selected = list.GetVisualDescendants()
                    .OfType<Button>()
                    .FirstOrDefault(button => button.DataContext is PickerOption { IsSelected: true });

                if (selected is not null)
                {
                    selected.BringIntoView();
                    selected.Focus();
                }
            },
            DispatcherPriority.Loaded);

    private void FocusEffortSlider() =>
        Dispatcher.UIThread.Post(
            () => this.FindControl<Slider>("EffortSlider")?.Focus(),
            DispatcherPriority.Loaded);

    private void FocusNamedControl(string name) =>
        Dispatcher.UIThread.Post(
            () => this.FindControl<Control>(name)?.Focus(),
            DispatcherPriority.Loaded);

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private StrataChatShell? Shell => this.FindControl<StrataChatShell>("ChatShell");

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        Detach();

        if (DataContext is not MobileShellViewModel shell)
        {
            _presence?.Detach();
            return;
        }

        _presence?.Attach(shell);

        _shell = shell;
        shell.PropertyChanged += OnShellPropertyChanged;
        shell.Chat.Turns.CollectionChanged += OnTurnsChanged;
        shell.Chat.PropertyChanged += OnChatPropertyChanged;
        shell.Chat.ChatActivitySubmitted += OnChatActivitySubmitted;
        shell.Chat.ChatSurfaceReset += OnChatSurfaceReset;
        shell.Chat.TranscriptApplied += OnTranscriptApplied;
        shell.Chat.AttachmentPickRequested += OnPickAttachment;
        SynchronizeObservers();
        UpdateNativeComposerVisibility();
    }

    private void Detach()
    {
        SetTranscriptMotionWidth(false);
        if (_shell is null)
            return;

        _shell.Chat.Turns.CollectionChanged -= OnTurnsChanged;
        _shell.Chat.PropertyChanged -= OnChatPropertyChanged;
        _shell.Chat.ChatActivitySubmitted -= OnChatActivitySubmitted;
        _shell.Chat.ChatSurfaceReset -= OnChatSurfaceReset;
        _shell.Chat.TranscriptApplied -= OnTranscriptApplied;
        _shell.Chat.AttachmentPickRequested -= OnPickAttachment;
        _shell.PropertyChanged -= OnShellPropertyChanged;

        ClearObservers();
        _loadingDelay?.Dispose();
        _loadingDelay = null;
        _chatEntranceVersion++;
        _awaitingChatEntrance = false;
        _transcriptContentChanged = false;
        _transcriptEntrance?.Dispose();
        _transcriptEntrance = null;
        _welcomeEntrance?.Dispose();
        _welcomeEntrance = null;

        _shell = null;
        UpdateNativeComposerVisibility();
    }

    private void OnShellPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileShellViewModel.IsDrawerMoving)
            or nameof(MobileShellViewModel.CanDockDrawer)
            or nameof(MobileShellViewModel.HasHingeGap))
        {
            SetTranscriptMotionWidth(_shell is { IsDrawerMoving: true, CanDockDrawer: true, HasHingeGap: false });
        }

        if (e.PropertyName == nameof(MobileShellViewModel.IsKeyboardOpen))
        {
            if (_shell?.IsKeyboardOpen == true)
                UpdateComposerFocus();
            else
                ReleaseComposerFocus();
        }

        if (e.PropertyName is nameof(MobileShellViewModel.IsChatPage)
            or nameof(MobileShellViewModel.IsDrawerOverlay)
            or nameof(MobileShellViewModel.IsNavigationCoveringContent)
            or nameof(MobileShellViewModel.IsModalSheetPresented)
            or nameof(MobileShellViewModel.IsChatActionsOpen)
            or nameof(MobileShellViewModel.HasPageOverlay))
        {
            UpdateNativeComposerVisibility();
        }
    }

    private void SetTranscriptMotionWidth(bool hold)
    {
        if (_transcriptWidthHeld == hold
            || this.FindControl<Border>("ChatTranscriptSideInset") is not { } transcript)
            return;

        if (hold)
        {
            if (transcript.Bounds.Width <= 0)
                return;
            // Only the surrounding canvas resizes during motion; long markdown reflows once at rest.
            transcript.Width = transcript.Bounds.Width;
            transcript.HorizontalAlignment = FlowDirection == Avalonia.Media.FlowDirection.RightToLeft
                ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        }
        else
        {
            transcript.ClearValue(WidthProperty);
            transcript.ClearValue(HorizontalAlignmentProperty);
        }
        _transcriptWidthHeld = hold;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell?.Chat.IsApplyingTranscript == true)
        {
            _transcriptContentChanged = true;
            return;
        }

        SynchronizeObservers();

        // Replacing an older bounded page is navigation, not new tail content. Let the reader keep
        // their place instead of applying the latest-window auto-follow policy to this segment.
        if (_shell?.Chat.IsLatestWindow != true)
            return;

        // Server reconciliation can append turns repeatedly while Lumi is working. Treat those as
        // new content and honour a reader who scrolled away; explicit local sends have their own
        // ChatActivitySubmitted signal below and are the only additions that force the tail.
        RequestFollow(newContent: true);
    }

    private void OnChatActivitySubmitted(Guid chatId, string _)
    {
        if (_shell?.Chat.ChatId == chatId)
            Shell?.JumpToLatest();
    }

    private void OnChatSurfaceReset()
    {
        ReleaseComposerFocus();
        var version = ++_chatEntranceVersion;
        _awaitingChatEntrance = true;
        _loadingDelay?.Dispose();
        if (this.FindControl<Border>("ChatTranscriptSideInset") is { } transcript)
        {
            _transcriptEntrance ??= new MobileEntrance(transcript);
            _transcriptEntrance.Prepare();
            transcript.IsHitTestVisible = false;
        }
        Shell?.RequestInitialBottom();
        if (this.FindControl<Border>("ChatLoadingOverlay") is { } loading)
        {
            loading.IsVisible = false;
            _loadingDelay = DispatcherTimer.RunOnce(() =>
            {
                if (version == _chatEntranceVersion && _shell?.Chat.IsInitialLoading == true)
                    loading.IsVisible = true;
            }, TimeSpan.FromMilliseconds(100));
        }
        QueueChatEntrance();
    }

    private void OnTranscriptApplied()
    {
        var contentChanged = _transcriptContentChanged;
        _transcriptContentChanged = false;
        SynchronizeObservers();
        if (_awaitingChatEntrance)
        {
            Shell?.RequestInitialBottom();
            QueueChatEntrance();
        }
        else if (_shell?.Chat.IsLatestWindow == true)
        {
            RequestFollow(newContent: contentChanged);
        }
    }

    private void QueueChatEntrance()
    {
        if (!_awaitingChatEntrance)
            return;

        var version = _chatEntranceVersion;
        Dispatcher.UIThread.Post(() =>
        {
            if (!_awaitingChatEntrance || version != _chatEntranceVersion || _shell?.Chat.IsLoading != false)
                return;

            // The shell lands the initial viewport at Render/Loaded priority. Reveal after that
            // layout, rather than showing the top of the transcript and jumping to its bottom.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_awaitingChatEntrance || version != _chatEntranceVersion || _shell?.Chat.IsLoading != false)
                    return;
                _awaitingChatEntrance = false;
                _loadingDelay?.Dispose();
                _loadingDelay = null;
                if (this.FindControl<Border>("ChatLoadingOverlay") is { } loading)
                    loading.IsVisible = false;
                if (this.FindControl<Border>("ChatTranscriptSideInset") is { } transcript)
                    transcript.IsHitTestVisible = true;
                _transcriptEntrance?.Reveal();
                if (_shell.IsWelcomeVisible && this.FindControl<StackPanel>("NoChatPlaceholder") is { } welcome)
                {
                    _welcomeEntrance ??= new MobileEntrance(welcome);
                    _welcomeEntrance.Play();
                }
            }, DispatcherPriority.Loaded);
        }, DispatcherPriority.Loaded);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell?.Chat.IsApplyingTranscript == true)
        {
            _transcriptContentChanged = true;
            return;
        }

        SynchronizeObservers();
        if (_shell?.Chat.IsLatestWindow != true)
            return;

        RequestFollow(newContent: true);
    }

    private void OnItemPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shell?.Chat.IsLatestWindow != true)
            return;

        // Streaming grows an existing row rather than adding one. That is layout growth, not new
        // content: it must not re-arm the "unseen content" badge on every token.
        if (e.PropertyName is nameof(AssistantItemViewModel.Text)
            or nameof(ReasoningItemViewModel.Text)
            or nameof(ActivitySummaryItemViewModel.SummaryText)
            or nameof(ActivitySummaryItemViewModel.HasFileChanges))
            RequestFollow(newContent: false);
    }

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MobileChatViewModel.HasAttachments))
            UpdateComposerFocus();

        if (e.PropertyName == nameof(MobileChatViewModel.IsLoading) && _awaitingChatEntrance)
            QueueChatEntrance();

        if (e.PropertyName == nameof(MobileChatViewModel.HasOpenSheet))
        {
            UpdateNativeComposerVisibility();
            return;
        }

        // A picker sheet that opens at the top of a long catalog hides the very thing the user came
        // to check — which model is active. Bring it into view as the sheet opens.
        if (e.PropertyName == nameof(MobileChatViewModel.IsModelSheetOpen)
            && _shell?.Chat.IsModelSheetOpen == true)
        {
            ScrollSelectedModelIntoView();
            return;
        }

        if (e.PropertyName == nameof(MobileChatViewModel.IsEffortSheetOpen)
            && _shell?.Chat.IsEffortSheetOpen == true)
        {
            FocusEffortSlider();
            return;
        }

        if (e.PropertyName == nameof(MobileChatViewModel.IsRunSettingsSheetOpen)
            && _shell?.Chat.IsRunSettingsSheetOpen == true)
        {
            FocusNamedControl("RunSettingsModelButton");
            return;
        }

        if (e.PropertyName == nameof(MobileChatViewModel.IsContextSheetOpen)
            && _shell?.Chat.IsContextSheetOpen == true)
        {
            Dispatcher.UIThread.Post(
                () =>
                {
                    var selected = this.FindControl<StrataBottomSheet>("ContextSheet")
                        ?.GetVisualDescendants()
                        .OfType<Button>()
                        .FirstOrDefault(button => button.DataContext is PickerOption { IsSelected: true });
                    selected?.Focus();
                },
                DispatcherPriority.Loaded);
            return;
        }

        if (e.PropertyName is not (nameof(MobileChatViewModel.IsStreaming) or nameof(MobileChatViewModel.IsBusy)))
            return;

        // Status can originate from desktop or another mobile surface. It may update the progress
        // affordance, but it must not override a reader who deliberately scrolled away. Only the
        // explicit local ChatActivitySubmitted signal above forces the viewport to the tail.
        RequestFollow(newContent: false);
    }

    /// <summary>
    /// Asks the shell to follow the tail at most once per frame. Honours a reader who has
    /// deliberately scrolled up — that check lives in Strata's scroll policy.
    /// </summary>
    private void RequestFollow(bool newContent)
    {
        if (_shell?.Chat.IsApplyingTranscript == true || _awaitingChatEntrance)
            return;

        if (_followQueued)
            return;

        _followQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _followQueued = false;

            if (newContent)
                Shell?.NotifyTranscriptContentChanged();
            else
                Shell?.NotifyTranscriptLayoutChanged();
        }, DispatcherPriority.Background);
    }

    private void SynchronizeObservers()
    {
        if (_shell is not { } shell)
            return;

        var activeTurns = new HashSet<TranscriptTurnViewModel>(
            shell.Chat.Turns,
            ReferenceEqualityComparer.Instance);
        foreach (var turn in _observedTurns.Where(turn => !activeTurns.Contains(turn)).ToArray())
        {
            turn.Items.CollectionChanged -= OnItemsChanged;
            _observedTurns.Remove(turn);
        }

        foreach (var turn in activeTurns)
        {
            if (_observedTurns.Add(turn))
                turn.Items.CollectionChanged += OnItemsChanged;
        }

        // Items are also REPLACED in place when a row changes kind, so derive the complete active
        // set after every collection mutation. That both hooks the replacement and releases the old
        // row immediately instead of retaining every streamed message until the view is detached.
        var activeItems = new HashSet<TranscriptItemViewModel>(
            activeTurns
                .SelectMany(turn => turn.Items)
                .Where(item => item is AssistantItemViewModel
                    or ReasoningItemViewModel
                    or ActivitySummaryItemViewModel),
            ReferenceEqualityComparer.Instance);

        foreach (var item in _observedItems.Where(item => !activeItems.Contains(item)).ToArray())
        {
            item.PropertyChanged -= OnItemPropertyChanged;
            _observedItems.Remove(item);
        }

        foreach (var item in activeItems)
        {
            if (_observedItems.Add(item))
                item.PropertyChanged += OnItemPropertyChanged;
        }
    }

    private void ClearObservers()
    {
        foreach (var turn in _observedTurns)
            turn.Items.CollectionChanged -= OnItemsChanged;
        _observedTurns.Clear();

        foreach (var item in _observedItems)
            item.PropertyChanged -= OnItemPropertyChanged;
        _observedItems.Clear();
    }

    private void OnQuestionAnswered(object? sender, string answer)
    {
        if (sender is not StrataQuestionCard { DataContext: QuestionItemViewModel question })
            return;

        if (DataContext is MobileShellViewModel shell)
            _ = shell.Chat.AnswerQuestionAsync(question.QuestionId, answer);
    }

    private async void OnSourceClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: RemoteSource { Url: { Length: > 0 } url } }
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")
            || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher)
        {
            return;
        }

        try
        {
            if (!await launcher.LaunchUriAsync(uri) && _shell is { } shell)
                shell.Chat.ErrorText = "That source could not be opened.";
        }
        catch (Exception ex)
        {
            if (_shell is { } shell)
                shell.Chat.ErrorText = "That source could not be opened.";
            Trace.TraceWarning($"[Mobile] Source launch failed: {ex}");
        }
    }

    private async void OnProducedFileOpenRequested(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control
            {
                DataContext: RemoteAttachment
                {
                    MessageId: { } messageId,
                    FileName: { Length: > 0 } fileName
                }
            }
            || _shell is not { } shell
            || shell.Chat.ChatId == Guid.Empty
            || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        string? downloadedPath = null;
        try
        {
            downloadedPath = await shell.DownloadProducedFileAsync(shell.Chat.ChatId, messageId, fileName);
            if (downloadedPath is null)
            {
                shell.Chat.ErrorText = shell.ConnectionMessage ?? "That file could not be downloaded.";
                return;
            }

            if (await MobilePlatformServices.ProducedFileOpener.TryOpenAsync(
                    downloadedPath,
                    fileName,
                    CancellationToken.None))
            {
                return;
            }

            var destination = await topLevel.StorageProvider.SaveFilePickerAsync(
                new FilePickerSaveOptions
                {
                    Title = "Save Lumi file",
                    SuggestedFileName = fileName
                });
            if (destination is null)
                return;

            await ProducedFileExport.CopyAndVerifyAsync(
                downloadedPath,
                destination.OpenWriteAsync,
                destination.OpenReadAsync,
                CancellationToken.None);

            if (!await topLevel.Launcher.LaunchFileAsync(destination))
                shell.Chat.ErrorText = "The file was downloaded, but this phone could not open it.";
        }
        catch (Exception ex)
        {
            shell.Chat.ErrorText = "That file could not be opened.";
            Trace.TraceWarning($"[Mobile] Produced file launch failed: {ex}");
        }
        finally
        {
            if (downloadedPath is not null && File.Exists(downloadedPath))
                File.Delete(downloadedPath);
        }
    }
}
