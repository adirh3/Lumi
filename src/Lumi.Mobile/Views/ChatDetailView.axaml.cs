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
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Lumi.Mobile.Services;
using Lumi.Mobile.ViewModels;
using Lumi.Remote.Protocol;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

public partial class ChatDetailView : UserControl
{
    private MobilePresenceController? _presence;
    private MobileShellViewModel? _shell;

    /// <summary>Every collection and item we have hooked, so detach is exact and nothing leaks.</summary>
    private readonly HashSet<TranscriptTurnViewModel> _observedTurns =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TranscriptItemViewModel> _observedItems =
        new(ReferenceEqualityComparer.Instance);
    private INotifyCollectionChanged? _composerChipChildren;
    private bool _composerChipTargetUpdateQueued;

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

    public ChatDetailView()
    {
        InitializeComponent();

        // The ambient field is created in code rather than XAML because the controller owns it: it
        // has to insert it as the bottom-most child and keep it out of the hit-test path.
        if (this.FindControl<Panel>("ChatRoot") is { } root)
            _presence = new MobilePresenceController(root);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(AttachComposerChipObserver, DispatcherPriority.Loaded);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        DetachComposerChipObserver();
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Strata creates skill-chip remove buttons in code with desktop-local 16dp dimensions, so an
    /// ordinary mobile style cannot outrank those values. Observe the chip row and apply the mobile
    /// touch token whenever Strata rebuilds it; commands and chip behavior remain entirely owned by
    /// the composer.
    /// </summary>
    private void AttachComposerChipObserver()
    {
        DetachComposerChipObserver();
        if (!this.IsAttachedToVisualTree())
            return;

        var chipRow = this.FindControl<StrataChatComposer>("Composer")
            ?.GetVisualDescendants()
            .OfType<WrapPanel>()
            .FirstOrDefault(panel => panel.Name == "PART_ChipsRow");
        if (chipRow?.Children is not INotifyCollectionChanged children)
            return;

        _composerChipChildren = children;
        children.CollectionChanged += OnComposerChipChildrenChanged;
        ApplyComposerChipTouchTargets();
    }

    private void DetachComposerChipObserver()
    {
        if (_composerChipChildren is not null)
            _composerChipChildren.CollectionChanged -= OnComposerChipChildrenChanged;

        _composerChipChildren = null;
        _composerChipTargetUpdateQueued = false;
    }

    private void OnComposerChipChildrenChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_composerChipTargetUpdateQueued)
            return;

        _composerChipTargetUpdateQueued = true;
        Dispatcher.UIThread.Post(
            () =>
            {
                _composerChipTargetUpdateQueued = false;
                if (this.IsAttachedToVisualTree())
                    ApplyComposerChipTouchTargets();
            },
            DispatcherPriority.Loaded);
    }

    private void ApplyComposerChipTouchTargets()
    {
        if (this.FindControl<StrataChatComposer>("Composer") is not { } composer)
            return;

        var target = composer.TryFindResource(
            "Touch.MinTarget",
            composer.ActualThemeVariant,
            out var resource) && resource is double value
                ? value
                : 48;

        foreach (var button in composer.GetVisualDescendants()
                     .OfType<Button>()
                     .Where(candidate => candidate.Classes.Contains("chip-remove")))
        {
            button.Width = target;
            button.Height = target;
            button.MinWidth = target;
            button.MinHeight = target;
            button.CornerRadius = new CornerRadius(target / 2);
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
        shell.Chat.Turns.CollectionChanged += OnTurnsChanged;
        shell.Chat.PropertyChanged += OnChatPropertyChanged;
        shell.Chat.AttachmentPickRequested += OnPickAttachment;
        SynchronizeObservers();
    }

    private void Detach()
    {
        if (_shell is null)
            return;

        _shell.Chat.Turns.CollectionChanged -= OnTurnsChanged;
        _shell.Chat.PropertyChanged -= OnChatPropertyChanged;
        _shell.Chat.AttachmentPickRequested -= OnPickAttachment;

        ClearObservers();

        _wasBusy = false;
        _shell = null;
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        SynchronizeObservers();

        // Replacing an older bounded page is navigation, not new tail content. Let the reader keep
        // their place instead of applying the latest-window auto-follow policy to this segment.
        if (_shell?.Chat.IsLatestWindow != true)
            return;

        // A new turn is the user's own message or the start of a reply. They just acted, so jump
        // even if they had scrolled away — this is the one case where overriding them is right.
        if (e.Action == NotifyCollectionChangedAction.Add)
            Shell?.JumpToLatest();
        else
            RequestFollow(newContent: true);
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
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

    private bool _wasBusy;

    private void OnChatPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
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

        // Rising edge of busy == the user just sent something. That is explicit intent to be at the
        // tail, so snap there NOW rather than asking politely.
        //
        // The gentle notify below honours a reader who has scrolled up and is posted at Background
        // priority, so after sending from anywhere but the very bottom the echoed bubble and the
        // thinking row both landed off-screen: the app looked like it had ignored the tap until the
        // answer eventually pushed the view down. JumpToLatest overrides the scroll-away policy and
        // runs inline, so the feedback is on screen in the same frame the tap is handled.
        var busy = _shell?.Chat is { } chat && (chat.IsBusy || chat.IsStreaming);
        if (busy && !_wasBusy)
        {
            _wasBusy = true;
            Shell?.JumpToLatest();
            return;
        }

        _wasBusy = busy;
        RequestFollow(newContent: false);
    }

    /// <summary>
    /// Asks the shell to follow the tail at most once per frame. Honours a reader who has
    /// deliberately scrolled up — that check lives in Strata's scroll policy.
    /// </summary>
    private void RequestFollow(bool newContent)
    {
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
