#if DEBUG || LUMI_MOBILE_SMOOTHNESS
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using Lumi.Mobile.Views;
using StrataTheme.Controls;

namespace Lumi.Mobile.Smoothness;

internal sealed record SmoothnessRequest(string Operation, string? Scenario = null, int StallMs = 0);
internal sealed record SmoothnessTarget(double X, double Y, double Width, double Height, double Scale);
internal sealed record SmoothnessFrame(double TimeMs, double Offset, double BlankFraction, double KeyboardInset = 0);
internal sealed record SmoothnessPresentation(double TimeMs, string TurnId, string[] Kinds, string Phase,
    double DurationMs, double ViewportDistance);

internal sealed class SmoothnessMeasurement
{
    public string Scenario { get; set; } = "";
    public bool Complete { get; set; }
    public string? Error { get; set; }
    public double? InputToReadyMs { get; set; }
    public double? InputToScrollMs { get; set; }
    public double? InputToFocusMs { get; set; }
    public double? InputToKeyboardRequestMs { get; set; }
    public double? InputToKeyboardReportedMs { get; set; }
    public double? KeyboardAnimationMs { get; set; }
    public bool SoftKeyboardExpected { get; set; }
    public double StartMs { get; set; }
    public double ActiveEndMs { get; set; }
    public double EndMs { get; set; }
    public double InputFinishedMs { get; set; }
    public double Travel { get; set; }
    public int GestureUpdates { get; set; }
    public int InjectedStallMs { get; set; }
    public double? InjectedStallAtMs { get; set; }
    public int Gen2Collections { get; set; }
    public List<SmoothnessFrame> Frames { get; set; } = [];
    public List<SmoothnessPresentation> Presentations { get; set; } = [];
}

internal sealed class SmoothnessState
{
#if DEBUG
    public string Configuration { get; set; } = "Debug";
#else
    public string Configuration { get; set; } = "Release";
#endif
    public string Fixture { get; set; } = MobileSmoothnessFixture.Version;
    public string[] Scenarios { get; set; } = MobileSmoothnessSession.Scenarios;
    public SmoothnessTarget? Target { get; set; }
    public SmoothnessMeasurement? Measurement { get; set; }
    public int ChatCount { get; set; } = MobileSmoothnessFixture.ChatCount;
    public int TotalMessageCount { get; set; } = MobileSmoothnessFixture.TurnCount * 2;
    public int LoadedTurns { get; set; }
    public double ViewWidth { get; set; }
    public double ViewHeight { get; set; }
    public double Scale { get; set; }
    public double ClockMs { get; set; }
    public double NativeClockMs { get; set; }
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SmoothnessRequest))]
[JsonSerializable(typeof(SmoothnessState))]
internal sealed partial class SmoothnessJsonContext : JsonSerializerContext;

/// <summary>
/// Observes real host-injected touches and semantic readiness. UI frame opportunities are deliberately
/// not called presented frames; the runner independently records canvas/surface output.
/// </summary>
internal sealed class MobileSmoothnessSession : IDisposable
{
    internal static readonly string[] Scenarios =
    [
        "scroll-plain", "scroll-rich", "scroll-code", "scroll-chats", "scroll-models",
        "switch-plain", "switch-rich", "switch-code",
        "popup-model", "popup-project", "popup-title",
        "nav-jobs", "nav-customize", "nav-settings", "nav-chats",
        "composer-empty", "composer-plain", "composer-rich", "composer-code",
        "scroll-reveal-cold", "scroll-reveal-retained"
    ];
    private readonly MobileShellView _view;
    private readonly MobileShellViewModel _shell;
    private readonly TopLevel _root;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Func<double> _nativeClock;
    private readonly bool _softKeyboardExpected;
    private readonly Func<NativeComposerEditorHost, Point, bool>? _nativeEditorHitTest;
    private StrataChatComposer? _composer;
    private TextBox? _composerInput;
    private SmoothnessMeasurement? _measurement;
    private SmoothnessTarget? _target;
    private ScrollViewer? _scroll;
    private TranscriptTurnView[] _turns = [];
    private Func<bool>? _ready;
    private double _previousOffset;
    private double _lastMovementMs;
    private double? _releasedMs;
    private int _gen2;
    private int _stallMs;
    private int _version;
    private bool _disposed;

    public MobileSmoothnessSession(
        MobileShellView view, Func<double>? nativeClock = null, bool softKeyboardExpected = false,
        Func<NativeComposerEditorHost, Point, bool>? nativeEditorHitTest = null)
    {
        _view = view;
        _shell = view.DataContext as MobileShellViewModel
            ?? throw new InvalidOperationException("The benchmark shell is not mounted.");
        _root = TopLevel.GetTopLevel(view)
            ?? throw new InvalidOperationException("A headed renderer is required.");
        _nativeClock = nativeClock ?? (() => Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
        _softKeyboardExpected = softKeyboardExpected;
        _nativeEditorHitTest = nativeEditorHitTest;
        view.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel, true);
        view.AddHandler(InputElement.ScrollGestureEvent, OnScrollGesture, RoutingStrategies.Bubble, true);
        view.AddHandler(InputElement.ScrollGestureEndedEvent, OnScrollEnded, RoutingStrategies.Bubble, true);
        _view.SmoothnessKeyboardReported += OnKeyboardReported;
        TranscriptTurnView.PresentationMeasured += OnPresentationMeasured;
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    public async Task<string> CommandAsync(string json)
    {
        try
        {
            var request = JsonSerializer.Deserialize(json, SmoothnessJsonContext.Default.SmoothnessRequest)
                ?? throw new InvalidDataException("Missing benchmark request.");
            switch (request.Operation)
            {
                case "prepare":
                    await PrepareAsync(request.Scenario ?? "");
                    break;
                case "arm":
                    if (_target is null || _ready is null || _measurement?.StartMs > 0 && !_measurement.Complete)
                        throw new InvalidOperationException("Prepare and finish an action before arming it.");
                    if (request.StallMs is < 0 or > 250)
                        throw new ArgumentOutOfRangeException(nameof(request.StallMs));
                    if (request.Scenario != _scenario)
                        throw new InvalidDataException("The armed scenario must match the prepared surface.");
                    _stallMs = request.StallMs;
                    _measurement = new SmoothnessMeasurement
                    {
                        Scenario = request.Scenario ?? _scenario,
                        SoftKeyboardExpected = _softKeyboardExpected
                            && _scenario.StartsWith("composer-", StringComparison.Ordinal)
                    };
                    _releasedMs = null;
                    _previousOffset = _scroll?.Offset.Y ?? 0;
                    _gen2 = GC.CollectionCount(2);
                    _version++;
                    break;
                case "finish-input":
                    if (_measurement is not { Complete: false, StartMs: > 0 } active)
                        throw new InvalidOperationException("Finish input only after the armed action received touch input.");
                    active.InputFinishedMs = Now;
                    break;
                case "read":
                    if (_measurement is { Complete: false, StartMs: > 0 } measurement && Now - measurement.StartMs > 15000)
                        Fail("The action did not reach visible, settled content within 15 seconds.");
                    break;
                default:
                    throw new InvalidDataException($"Unknown benchmark operation: {request.Operation}.");
            }
            return SerializeState();
        }
        catch (Exception error)
        {
            Trace.TraceError($"[Mobile] Smoothness: {error}");
            return JsonSerializer.Serialize(new SmoothnessState { Error = $"{_scenario}: {error}" },
                SmoothnessJsonContext.Default.SmoothnessState);
        }
    }

    private string _scenario = "";

    private async Task PrepareAsync(string scenario)
    {
        if (!Scenarios.Contains(scenario, StringComparer.Ordinal))
            throw new InvalidDataException($"Unknown mobile scenario: {scenario}.");
        _version++;
        _measurement = null;
        _target = null;
        _scenario = scenario;
        await WaitAsync(() => _shell.IsConnected && _shell.IsHostReady && _shell.ChatList.Groups.Count > 0
            && _shell.Chat.AvailableModels.Count == MobileSmoothnessFixture.ModelCount && _shell.Projects.Count == 20,
            "The in-memory PC did not bootstrap.");
        while (_shell.Chat.DismissTopmostSheet()) { }
        _shell.IsProjectPickerOpen = false;
        _shell.IsChatActionsOpen = false;
        await WaitAsync(() => !_shell.IsModalSheetPresented, "Previous sheet did not dismiss.");
        _shell.ActiveProjectId = null;
        _shell.Chat.PromptText = "";
        var reveal = scenario.StartsWith("scroll-reveal-", StringComparison.Ordinal);
        var index = reveal ? 3 : scenario.EndsWith("plain", StringComparison.Ordinal) ? 0
            : scenario.EndsWith("code", StringComparison.Ordinal) ? 2 : 1;
        var chatView = _view.FindControl<ChatDetailView>("ChatSurface")!;
        chatView.ResetSmoothnessComposerFocus();
        await WaitAsync(() => !_shell.IsKeyboardOpen, "The previous soft keyboard did not close.");
        _composer = chatView.FindControl<StrataChatComposer>("Composer");
        _composerInput = _composer?.GetVisualDescendants().OfType<TextBox>()
            .FirstOrDefault(input => input.Name == "PART_Input");
        _scroll = null;
        if (reveal && _shell.Chat.ChatId == MobileSmoothnessFixture.ChatId(index))
        {
            _shell.ChatList.NewChatCommand.Execute(null);
            await WaitAsync(() => _shell.Chat.ChatId == Guid.Empty && !_shell.IsDrawerMoving,
                "The cold transcript did not reset.");
        }

        if (scenario == "composer-empty")
        {
            _shell.ChatList.NewChatCommand.Execute(null);
            await WaitAsync(() => _shell.Chat.ChatId == Guid.Empty && !_shell.IsDrawerMoving
                && !_shell.IsDrawerPresented, "The empty composer did not appear.");
        }
        else if (scenario is not ("scroll-chats" or "popup-project") && !scenario.StartsWith("nav-", StringComparison.Ordinal))
        {
            var setupIndex = scenario.StartsWith("switch-", StringComparison.Ordinal) ? (index + 1) % 3 : index;
            var row = _shell.ChatList.Groups.SelectMany(group => group.Chats)
                .First(chat => chat.Id == MobileSmoothnessFixture.ChatId(setupIndex));
            _shell.ChatList.OpenChatCommand.Execute(row);
            await WaitAsync(() => _shell.Chat.ChatId == row.Id && !_shell.Chat.IsLoading
                && chatView.CanAnimateArrival && !_shell.IsDrawerMoving, "The setup chat did not appear.");
        }
        if (scenario.StartsWith("switch-", StringComparison.Ordinal) || scenario is "scroll-chats" or "popup-project"
            || scenario.StartsWith("nav-", StringComparison.Ordinal))
        {
            if (scenario == "nav-chats")
                _shell.SelectPlace(MobilePlace.Settings);
            else
                _shell.ShowHome();
            await WaitAsync(() => !_shell.IsDrawerMoving, "Navigation did not settle.");
            if (scenario.StartsWith("switch-", StringComparison.Ordinal))
                Find<ScrollViewer>("DrawerScroll").Offset = default;
        }
        if (scenario == "scroll-models")
        {
            _shell.Chat.OpenModelSheetCommand.Execute(null);
            await WaitAsync(() => SheetReady("ModelSheet"), "The model sheet did not appear.");
        }
        // Compact mobile chrome exposes the model while a draft is being composed.
        if (scenario == "popup-model")
            _shell.Chat.PromptText = "Benchmark draft";
        // Setup is excluded from measurement. Keep the exact same starting position for every repetition.
        _scroll = scenario switch
        {
            "scroll-chats" => Find<ScrollViewer>("DrawerScroll"),
            "scroll-models" => Find<ScrollViewer>("ModelSheetScroll"),
            _ when scenario.StartsWith("scroll-", StringComparison.Ordinal) =>
                chatView.GetVisualDescendants().OfType<ScrollViewer>()
                    .FirstOrDefault(scroll => scroll.Name == "PART_TranscriptScroll")
                    ?? throw new InvalidOperationException("The real transcript scroll presenter did not attach."),
            _ => null
        };
        if (_scroll is not null)
        {
            if (scenario == "scroll-reveal-retained")
            {
                var chatShell = chatView.FindControl<StrataChatShell>("ChatShell")!;
                chatShell.PreserveViewport();
                for (var turnIndex = _shell.Chat.Turns.Count - 1; turnIndex >= 0; turnIndex--)
                {
                    chatView.FindControl<ItemsControl>("Transcript")!.ScrollIntoView(turnIndex);
                    await WaitAsync(() => _root.IsMeasureValid && _root.IsArrangeValid,
                        "Retained transcript preparation did not settle.");
                    await Task.Delay(32);
                }
            }
            _scroll.Offset = new Vector(0, scenario is "scroll-chats" or "scroll-models" ? 0
                : Math.Max(0, _scroll.Extent.Height - _scroll.Viewport.Height));
        }
        await WaitAsync(() => _root.IsMeasureValid && _root.IsArrangeValid
            && (_scroll is null || _scroll.IsMeasureValid && _scroll.IsArrangeValid), "Setup layout did not settle.");
        // The setup's entrances must finish before recording a new, independent interaction.
        await Task.Delay(450);

        Control target;
        if (scenario.StartsWith("scroll-", StringComparison.Ordinal))
        {
            target = _scroll!;
            _ready = () => _scroll!.IsMeasureValid && _scroll.IsArrangeValid;
        }
        else if (scenario.StartsWith("switch-", StringComparison.Ordinal))
        {
            var id = MobileSmoothnessFixture.ChatId(index);
            target = _view.GetVisualDescendants().OfType<Button>().First(button =>
                button.IsEffectivelyVisible && button.DataContext is ChatListItemViewModel chat && chat.Id == id);
            _ready = () => _shell.Chat.ChatId == id && !_shell.Chat.IsLoading
                && chatView.CanAnimateArrival && !_shell.IsDrawerPresented;
        }
        else if (scenario.StartsWith("composer-", StringComparison.Ordinal))
        {
            target = _composer?.EditorContent as NativeComposerEditorHost
                ?? (Control?)_composerInput
                ?? throw new InvalidOperationException("The production composer input did not attach.");
            _ready = ComposerReady;
        }
        else
        {
            var name = scenario switch
            {
                "popup-model" => "ComposerModelButton",
                "popup-project" => "DrawerProjectButton",
                "popup-title" => "HeaderDetailsButton",
                "nav-jobs" => "PlacesJobsButton",
                "nav-customize" => "PlacesCustomizeButton",
                "nav-settings" => "PlacesSettingsButton",
                "nav-chats" => "PlacesChatsButton",
                _ => throw new InvalidDataException(scenario)
            };
            target = Find<Button>(name);
            _ready = scenario switch
            {
                "popup-model" => () => SheetReady("ModelSheet"),
                "popup-project" => () => SheetReady("ProjectPickerSheet"),
                "popup-title" => () => SheetReady("RunSettingsSheet"),
                "nav-jobs" => () => _shell.Place == MobilePlace.Jobs && _shell.IsLibraryPage,
                "nav-customize" => () => _shell.Place == MobilePlace.Customize && _shell.IsLibraryPage,
                "nav-settings" => () => _shell.Place == MobilePlace.Settings && _shell.IsSettingsPage,
                "nav-chats" => () => _shell.Place == MobilePlace.Chats && _shell.IsDrawerPresented,
                _ => throw new InvalidDataException(scenario)
            };
        }
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height * 0.5), _root)
            ?? throw new InvalidOperationException("The target has no rendered position.");
        var hit = _root.InputHitTest(point) as Visual;
        var nativeHit = target is NativeComposerEditorHost native
            && _nativeEditorHitTest?.Invoke(native, point) == true;
        if (!nativeHit && (hit is null || !ReferenceEquals(hit, target) && !target.IsVisualAncestorOf(hit)))
            throw new InvalidOperationException($"The rendered target is covered or not touch-reachable: {scenario}, " +
                $"{target.Name} at {point}; hit {hit?.GetType().Name} {(hit as Control)?.Name}.");
        _target = new(point.X, point.Y, target.Bounds.Width, target.Bounds.Height, _root.RenderScaling);
        _turns = chatView.GetVisualDescendants().OfType<TranscriptTurnView>().ToArray();
    }

    private void OnPresentationMeasured(TranscriptTurnView view, string phase, double duration)
    {
        if (_measurement is not { Complete: false, StartMs: > 0 } measurement
            || !_turns.Contains(view) || view.DataContext is not TranscriptTurnViewModel turn)
            return;
        if (measurement.Presentations.Count >= 2000)
        {
            Fail("The bounded presentation observer exceeded its event budget.");
            return;
        }
        measurement.Presentations.Add(new(Now, turn.Id, turn.DisplayItems.Select(item => item.Kind).ToArray(),
            phase, duration, view.ViewportDistance));
    }

    private T Find<T>(string name) where T : Control => _view.GetVisualDescendants().OfType<T>()
        .First(control => control.Name == name && control.IsEffectivelyVisible);

    private bool SheetReady(string name)
    {
        var sheet = _view.FindControl<StrataBottomSheet>(name);
        return sheet is { IsOpen: true, IsPresented: true, IsMeasureValid: true, IsArrangeValid: true }
            && sheet.GetVisualDescendants().OfType<Border>().Any(border =>
                border.Name == "PART_Sheet" && border.Bounds.Height > 0);
    }

    private async Task WaitAsync(Func<bool> predicate, string failure)
    {
        var deadline = Now + 15000;
        while (!predicate())
        {
            if (Now > deadline)
                throw new TimeoutException(failure);
            if (_shell.Chat.TranscriptErrorText is { Length: > 0 } error)
                throw new InvalidOperationException(error);
            await Task.Delay(16);
        }
    }

    private void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Pointer.Type != PointerType.Touch)
        {
            if (_measurement is { Complete: false, StartMs: 0 })
                Fail("The benchmark requires actual host touch input, not a command or mouse click.");
            return;
        }
        ObserveHostTouch(e.GetPosition(_root).X, e.GetPosition(_root).Y, pressed: true);
    }

    // Native EditText/DOM editors receive input outside Avalonia's routed pointer events.
    internal void ObserveHostTouch(double x, double y, bool pressed)
    {
        if (!pressed)
        {
            if (_measurement is { Complete: false, StartMs: > 0 })
                _releasedMs = Now;
            return;
        }
        if (_measurement is not { Complete: false } measurement || _target is not { } target)
            return;
        _releasedMs = null;
        if (measurement.StartMs > 0)
            return;
        var point = new Point(x, y);
        if (Math.Abs(point.X - target.X) > Math.Max(target.Width / 2, 30)
            || Math.Abs(point.Y - target.Y) > Math.Max(target.Height / 2, 30))
        {
            Fail("Input landed outside the verified target.");
            return;
        }
        measurement.StartMs = Now;
        _lastMovementMs = Now;
        var version = _version;
        _root.RequestAnimationFrame(_ => OnFrame(version));
    }

    internal void ObserveKeyboardRequest()
    {
        if (_measurement is { Complete: false, StartMs: > 0, SoftKeyboardExpected: true } measurement)
            measurement.InputToKeyboardRequestMs ??= Now - measurement.StartMs;
    }

    private void OnKeyboardReported(double target, double durationMs)
    {
        if (target > 0
            && _measurement is { Complete: false, StartMs: > 0, SoftKeyboardExpected: true } measurement)
        {
            measurement.InputToKeyboardReportedMs ??= Now - measurement.StartMs;
            measurement.KeyboardAnimationMs ??= durationMs;
        }
    }

    private bool ComposerFocused() =>
        _composer?.EditorContent is NativeComposerEditorHost native
            ? native.IsInputFocused
            : _composerInput is { } input && (input.IsFocused || input.Classes.Contains("native-input-focused"));

    private bool ComposerReady()
    {
        if (!ComposerFocused())
            return false;
        if (!_softKeyboardExpected)
            return true;
        return _shell.IsKeyboardOpen && _view.IsSmoothnessKeyboardSettled
            && _composer?.TranslatePoint(new Point(0, _composer.Bounds.Height), _root) is { } bottom
            && bottom.Y <= _root.ClientSize.Height - _view.SmoothnessKeyboardInset + 1;
    }

    private void OnScrollGesture(object? sender, ScrollGestureEventArgs e)
    {
        if (_measurement is not { Complete: false, StartMs: > 0 } measurement || _scroll is null)
            return;
        measurement.GestureUpdates++;
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_measurement is { Complete: false, StartMs: > 0 } && e.Pointer.Type == PointerType.Touch)
            _releasedMs = Now;
    }

    private void OnScrollEnded(object? sender, ScrollGestureEndedEventArgs e)
    {
        if (_measurement is { Complete: false, StartMs: > 0 } && _scroll is not null)
            _releasedMs = Now;
    }

    private void OnFrame(int version)
    {
        if (_disposed || version != _version || _measurement is not { Complete: false } measurement)
            return;
        var now = Now;
        if (_scenario.StartsWith("composer-", StringComparison.Ordinal) && ComposerFocused())
            measurement.InputToFocusMs ??= now - measurement.StartMs;
        var offset = _scroll?.Offset.Y ?? 0;
        if (Math.Abs(offset - _previousOffset) > 0.2)
        {
            measurement.InputToScrollMs ??= now - measurement.StartMs;
            measurement.Travel += Math.Abs(offset - _previousOffset);
            _lastMovementMs = now;
        }
        _previousOffset = offset;
        measurement.Frames.Add(new(now, offset, BlankFraction(),
            _scenario.StartsWith("composer-", StringComparison.Ordinal) ? _view.SmoothnessKeyboardInset : 0));
        if (_stallMs > 0 && measurement.InjectedStallMs == 0
            && measurement.GestureUpdates > 0 && measurement.Frames.Count >= 3)
        {
            measurement.InjectedStallMs = _stallMs;
            measurement.InjectedStallAtMs = Now;
            var until = Now + _stallMs;
            while (Now < until)
                Thread.SpinWait(64);
        }
        if (_ready?.Invoke() == true && _root.IsMeasureValid && _root.IsArrangeValid)
            measurement.InputToReadyMs ??= now - measurement.StartMs;
        if (now - measurement.StartMs > 15000 || measurement.Frames.Count > 3000)
        {
            Fail("The measured action did not finish with valid frame/readiness evidence.");
            return;
        }
        var scroll = _scroll is not null;
        if (measurement.InputFinishedMs > 0 && _releasedMs is not null && measurement.InputToReadyMs is not null
            && now - Math.Max(measurement.InputFinishedMs,
                scroll ? _lastMovementMs : measurement.StartMs + measurement.InputToReadyMs.Value) >= 450)
        {
            measurement.ActiveEndMs = scroll ? _lastMovementMs : now;
            measurement.EndMs = now;
            measurement.Gen2Collections = GC.CollectionCount(2) - _gen2;
            if (scroll && (measurement.Travel < 150 || measurement.GestureUpdates == 0))
                Fail("The gesture did not move enough real content (150 DIPs and a real scroll gesture required).");
            else
                measurement.Complete = true;
            return;
        }
        _root.RequestAnimationFrame(_ => OnFrame(version));
    }

    private double BlankFraction()
    {
        if (_scroll is null || !_scenario.StartsWith("scroll-", StringComparison.Ordinal)
            || _scenario is "scroll-chats" or "scroll-models")
            return 0;
        var height = _scroll.Viewport.Height;
        if (height <= 0)
            return 1;
        var blank = 0d;
        foreach (var turn in _turns)
        {
            if (turn.IsRealized || turn.TranslatePoint(default, _scroll) is not { } point)
                continue;
            blank += Math.Max(0, Math.Min(height, point.Y + turn.Bounds.Height) - Math.Max(0, point.Y));
        }
        return Math.Clamp(blank / height, 0, 1);
    }

    private void Fail(string error)
    {
        if (_measurement is not { } measurement)
            return;
        var chatView = _view.FindControl<ChatDetailView>("ChatSurface");
        var chatShell = chatView?.FindControl<StrataChatShell>("ChatShell");
        measurement.Error = error + $" Chat={_shell.Chat.ChatId}; page={_shell.Page}; " +
            $"loading={_shell.Chat.IsLoading}; drawer={_shell.IsDrawerPresented}/{_shell.IsDrawerMoving}; " +
            $"arrivalReady={chatView?.CanAnimateArrival}; tailGap={chatShell?.CurrentDistanceFromBottom}.";
        measurement.EndMs = Now;
        measurement.Complete = true;
    }

    private string SerializeState() => JsonSerializer.Serialize(new SmoothnessState
    {
        Target = _target,
        Measurement = _measurement is { Complete: true } ? _measurement : _measurement is null ? null
            : new SmoothnessMeasurement
            {
                Scenario = _measurement.Scenario, StartMs = _measurement.StartMs,
                InputToReadyMs = _measurement.InputToReadyMs, InputToScrollMs = _measurement.InputToScrollMs
            },
        LoadedTurns = _shell.Chat.Turns.Count,
        ViewWidth = _view.Bounds.Width, ViewHeight = _view.Bounds.Height,
        Scale = _root.RenderScaling, ClockMs = Now, NativeClockMs = _nativeClock()
    }, SmoothnessJsonContext.Default.SmoothnessState);

    public void Dispose()
    {
        _disposed = true;
        _version++;
        _view.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
        _view.RemoveHandler(InputElement.PointerReleasedEvent, OnReleased);
        _view.RemoveHandler(InputElement.ScrollGestureEvent, OnScrollGesture);
        _view.RemoveHandler(InputElement.ScrollGestureEndedEvent, OnScrollEnded);
        _view.SmoothnessKeyboardReported -= OnKeyboardReported;
        TranscriptTurnView.PresentationMeasured -= OnPresentationMeasured;
    }
}
#endif
