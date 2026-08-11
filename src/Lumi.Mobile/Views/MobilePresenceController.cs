using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Mobile.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Mobile.Views;

/// <summary>
/// Drives the ambient <see cref="StrataPresence"/> field behind the conversation, so Lumi feels
/// present rather than merely responsive.
///
/// <para>Deliberately much smaller than the desktop's <c>PresenceController</c>: that one
/// choreographs a field across a multi-column window with islands, seams and split animations. A
/// phone has one column, so all this needs to do is map chat state onto an ambient state, aim the
/// focus at whatever the user is looking at, and fire a pulse when something notable happens.</para>
///
/// <para>The vertical travel is the part that carries the feeling, and it mirrors the desktop
/// exactly: the field rests low at the composer, <b>rises</b> into the conversation when a turn
/// starts and tracks the answer as it grows, then <b>pours back down</b> to the composer when the
/// turn lands. A field pinned at one height reads as a static gradient, not a presence.</para>
/// </summary>
internal sealed class MobilePresenceController : IDisposable
{
    /// <summary>Focus sits low, near the composer — that is where the user's attention lives.</summary>
    private static readonly Point ComposerFocus = new(0.5, 0.82);

    /// <summary>On the empty launch screen the greeting is the hero, so the field rises to meet it.</summary>
    private static readonly Point GreetingFocus = new(0.5, 0.44);

    /// <summary>Where the field sits while working, before any turn has been realized to aim at.</summary>
    private static readonly Point WorkingFocus = new(0.5, 0.40);

    /// <summary>
    /// Below this the field counts as resting "low" (at the composer) and above it as "high" (in
    /// the conversation). The lift/descend impulses are gated on it so each fires only when the
    /// field is actually where the move starts, never as a kick in place.
    /// </summary>
    private const double LowRestThreshold = 0.58;

    private readonly StrataPresence _presence;
    private readonly Panel _host;
    private MobileShellViewModel? _shell;
    private bool _wasStreaming;
    private bool _wasBusy;
    private readonly HashSet<TranscriptTurnViewModel> _observedTurns =
        new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<TranscriptItemViewModel> _observedItems =
        new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// Re-aims the focus while a turn runs so the glow tracks the answer as it grows. A single
    /// property write on send is not enough: the turn Lumi is writing gets taller every frame, so
    /// without a follow the field aims at where the answer *started* and is left behind by it.
    /// </summary>
    private DispatcherTimer? _followTimer;

    /// <summary>Keeps the follow alive briefly after work ends so the descent lands on real layout.</summary>
    private DateTime _followUntil;

    public MobilePresenceController(Panel host)
    {
        _host = host;
        _presence = new StrataPresence
        {
            Name = "Presence",
            IsHitTestVisible = false,
            // A phone screen is small and mostly dark; the field has to read as atmosphere behind
            // the text, never as a wash over it.
            Intensity = 0.95,
            Halo = true,
            FocusPoint = GreetingFocus
        };

        host.Children.Insert(0, _presence);

        // On white the same field reads as a heavy purple wash bleeding over the cards, because
        // the aurora is additive: it has a dark canvas to build on and none to spend itself
        // against. Ambience has to stay ambient, so light mode gets roughly a third of the strength.
        _presence.ActualThemeVariantChanged += (_, _) => ApplyThemeIntensity();
        ApplyThemeIntensity();
    }

    private void ApplyThemeIntensity() =>
        _presence.Intensity = _presence.ActualThemeVariant == ThemeVariant.Light ? 0.34 : 0.95;

    public StrataPresence Visual => _presence;

    public void Attach(MobileShellViewModel shell)
    {
        Detach();

        _shell = shell;
        shell.PropertyChanged += OnShellChanged;
        shell.Chat.PropertyChanged += OnChatChanged;
        shell.Chat.Turns.CollectionChanged += OnTurnsChanged;
        SynchronizeObservers(shell.Chat, reactToNewItems: false);
        Sync();
    }

    public void Detach()
    {
        if (_shell is null)
            return;

        _shell.PropertyChanged -= OnShellChanged;
        _shell.Chat.PropertyChanged -= OnChatChanged;
        _shell.Chat.Turns.CollectionChanged -= OnTurnsChanged;

        ClearObservers();

        _followTimer?.Stop();
        _attentionPending = false;
        _wasStreaming = false;
        _wasBusy = false;
        _shell = null;
    }

    private void SynchronizeObservers(MobileChatViewModel chat, bool reactToNewItems)
    {
        var activeTurns = new HashSet<TranscriptTurnViewModel>(
            chat.Turns,
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

        var activeItems = new HashSet<TranscriptItemViewModel>(
            activeTurns.SelectMany(turn => turn.Items),
            ReferenceEqualityComparer.Instance);

        foreach (var item in _observedItems.Where(item => !activeItems.Contains(item)).ToArray())
        {
            if (item is QuestionItemViewModel)
                item.PropertyChanged -= OnTranscriptItemChanged;
            _observedItems.Remove(item);
        }

        foreach (var item in activeItems)
        {
            if (!_observedItems.Add(item))
                continue;
            if (item is QuestionItemViewModel)
                item.PropertyChanged += OnTranscriptItemChanged;
            if (reactToNewItems)
                ReactToNewItem(item);
        }
    }

    private void ClearObservers()
    {
        foreach (var turn in _observedTurns)
            turn.Items.CollectionChanged -= OnItemsChanged;
        _observedTurns.Clear();
        foreach (var item in _observedItems)
        {
            if (item is QuestionItemViewModel)
                item.PropertyChanged -= OnTranscriptItemChanged;
        }
        _observedItems.Clear();
    }

    private void OnTurnsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell is { } shell)
            SynchronizeObservers(shell.Chat, reactToNewItems: true);
    }

    /// <summary>
    /// The desktop pulses the field as Lumi *does* things — a file appears, an edit lands, a source
    /// is read, a question is asked. Those are exactly the moments that make presence feel like it
    /// is watching the work rather than just the connection, so the phone mirrors them: every new
    /// transcript row is an event worth a breath.
    /// </summary>
    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_shell is { } shell)
            SynchronizeObservers(shell.Chat, reactToNewItems: true);
    }

    private void ReactToNewItem(TranscriptItemViewModel item)
    {
        switch (item)
        {
            case QuestionItemViewModel:
                // A pending question outranks everything: it is the one state where Lumi is
                // waiting on the user rather than the other way round.
                Sync();
                break;
            case TerminalItemViewModel:
            case ToolGroupItemViewModel:
                Pulse(PresencePulse.Edit);
                break;
            case ErrorItemViewModel:
                Pulse(PresencePulse.Alert);
                break;
        }
    }

    private bool _attentionPending;

    private void OnTranscriptItemChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is QuestionItemViewModel && e.PropertyName == nameof(QuestionItemViewModel.IsAnswered))
            Sync();
    }

    private void OnShellChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileShellViewModel.IsLive)
            or nameof(MobileShellViewModel.Page))
        {
            Sync();
        }
    }

    private void OnChatChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MobileChatViewModel.IsBusy)
            or nameof(MobileChatViewModel.IsStreaming)
            or nameof(MobileChatViewModel.HasChat)
            or nameof(MobileChatViewModel.ErrorText))
        {
            Sync();
        }
    }

    private void Sync()
    {
        if (_shell is not { } shell)
            return;

        var chat = shell.Chat;
        _attentionPending = chat.HasChat
                            && !chat.IsStreaming
                            && chat.Turns
                                .SelectMany(turn => turn.Items)
                                .OfType<QuestionItemViewModel>()
                                .Any(question => !question.IsAnswered);

        var state = !shell.IsLive ? PresenceState.Dormant
            : _attentionPending ? PresenceState.Attention
            : chat.IsStreaming ? PresenceState.Streaming
            : chat.IsBusy ? PresenceState.Thinking
            : chat.HasChat ? PresenceState.Idle
            : PresenceState.Dormant;

        if (_presence.State != state)
            _presence.State = state;

        // Fire the felt vertical gesture BEFORE re-aiming, so its gate reads the field's
        // pre-move resting height rather than the height it is about to travel to.
        UpdateWorkEdge(chat);
        UpdateFocusTarget(chat);

        // A finished answer earns a quiet bloom: the moment work completes is exactly when a
        // little life makes the app feel like it is with you.
        if (_wasStreaming && !chat.IsStreaming && !chat.IsBusy)
            Pulse(PresencePulse.Bloom);
        else if (!_wasBusy && chat.IsBusy)
            Pulse(PresencePulse.Ripple);

        if (!string.IsNullOrEmpty(chat.ErrorText))
            Pulse(PresencePulse.Alert);

        _wasStreaming = chat.IsStreaming;
        _wasBusy = chat.IsBusy;
    }

    /// <summary>
    /// The felt kick on each work transition, mirroring the desktop: sending <b>lifts</b> the field
    /// up off the composer, finishing <b>pours</b> it back down. Each is gated on the field actually
    /// resting where the move begins, so neither fires on the welcome canvas (whose luminance already
    /// sits high on the greeting) nor as a twitch in place. Edge-tracked via <see cref="_wasBusy"/>
    /// so a transition fires exactly once.
    /// </summary>
    private void UpdateWorkEdge(MobileChatViewModel chat)
    {
        if (!chat.HasChat)
            return;

        var working = chat.IsBusy || chat.IsStreaming;
        var wasWorking = _wasBusy || _wasStreaming;

        if (working && !wasWorking && _presence.FocusPoint.Y >= LowRestThreshold)
            _presence.Lift();
        else if (!working && wasWorking && _presence.FocusPoint.Y < LowRestThreshold)
            _presence.Descend();
    }

    /// <summary>
    /// Aims the focus at whatever currently deserves attention: the greeting on an empty canvas,
    /// the live answer while Lumi works (tracked as it grows), and the composer once idle. The
    /// busy/idle swing is what makes the light read as rising into the conversation and settling
    /// back home rather than sitting at a fixed height.
    /// </summary>
    private void UpdateFocusTarget(MobileChatViewModel chat)
    {
        var working = chat.IsBusy || chat.IsStreaming || _attentionPending;

        Point target;
        if (!chat.HasChat)
        {
            target = GreetingFocus;
        }
        else if (working)
        {
            // Clamped clearly above the composer's resting band so sending visibly LIFTS the field
            // off the composer, while still tracking which part of the answer is live.
            target = TryGetLiveTurnFocus() ?? WorkingFocus;
            if (chat.IsBusy || chat.IsStreaming)
                EnsureFollow();
        }
        else
        {
            target = TryGetControlFocus("Composer", 0.28, 0.16, 0.86) ?? ComposerFocus;
        }

        // Dedup micro-moves so the follow timer never restarts the glide in place; the threshold is
        // tighter while working so the gaze visibly tracks the answer as it grows.
        var cur = _presence.FocusPoint;
        var dedup = working ? 0.006 : 0.014;
        if (Math.Abs(target.X - cur.X) + Math.Abs(target.Y - cur.Y) < dedup)
            return;

        _presence.FocusPoint = target;
    }

    /// <summary>
    /// The bottom-most realized transcript row's upper third, normalized into the field's own
    /// space — i.e. wherever the answer is currently being written.
    /// </summary>
    private Point? TryGetLiveTurnFocus()
    {
        var transcript = ResolveNamed("Transcript");
        if (transcript is null)
            return null;

        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1)
            return null;

        Point? best = null;
        foreach (var ctrl in transcript.GetVisualChildren().OfType<Control>()
                     .SelectMany(c => c.GetVisualChildren().OfType<Control>()))
        {
            if (ctrl.Bounds.Width <= 0 || ctrl.Bounds.Height <= 0)
                continue;
            var anchor = new Point(ctrl.Bounds.Width / 2, ctrl.Bounds.Height * 0.32);
            if (ctrl.TranslatePoint(anchor, _presence) is not { } p)
                continue;
            if (best is null || p.Y > best.Value.Y)
                best = p;
        }

        if (best is not { } hit)
            return null;

        return new Point(
            Math.Clamp(hit.X / pw, 0.18, 0.82),
            Math.Clamp(hit.Y / ph, 0.24, 0.52));
    }

    /// <summary>Normalizes a named control's anchor point into the field's own 0..1 space.</summary>
    private Point? TryGetControlFocus(string name, double anchorYFraction, double minY, double maxY)
    {
        var ctrl = ResolveNamed(name);
        if (ctrl is null)
            return null;

        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1 || ctrl.Bounds.Width <= 0 || ctrl.Bounds.Height <= 0)
            return null;

        var anchor = new Point(ctrl.Bounds.Width / 2, ctrl.Bounds.Height * anchorYFraction);
        if (ctrl.TranslatePoint(anchor, _presence) is not { } mapped)
            return null;

        return new Point(
            Math.Clamp(mapped.X / pw, 0.0, 1.0),
            Math.Clamp(mapped.Y / ph, minY, maxY));
    }

    private readonly Dictionary<string, Control> _namedControls = [];

    private Control? ResolveNamed(string name)
    {
        if (_namedControls.TryGetValue(name, out var cached) && cached.IsAttachedToVisualTree())
            return cached;

        var found = _host.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Name == name);
        if (found is not null)
            _namedControls[name] = found;
        return found;
    }

    /// <summary>
    /// Keeps re-aiming while a turn runs. Stops once work has ended and the settle window has
    /// passed, so an idle phone is not paying for a timer that has nothing left to track.
    /// </summary>
    private void EnsureFollow()
    {
        _followUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(1400);

        _followTimer ??= CreateFollowTimer();
        if (!_followTimer.IsEnabled)
            _followTimer.Start();
    }

    private DispatcherTimer CreateFollowTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(140)
        };

        timer.Tick += (_, _) =>
        {
            if (_shell is not { } shell)
            {
                _followTimer?.Stop();
                return;
            }

            var chat = shell.Chat;
            var working = chat.IsBusy || chat.IsStreaming;
            UpdateFocusTarget(chat);

            if (!working && DateTime.UtcNow >= _followUntil)
                _followTimer?.Stop();
        };

        return timer;
    }

    private void Pulse(PresencePulse pulse)
    {
        // Pulses are visual flourishes; a failure to render one must never surface as a crash.
        try
        {
            _presence.Pulse(pulse);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public void Dispose() => Detach();
}
