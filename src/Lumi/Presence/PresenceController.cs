using System;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.Models;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Presence;

/// <summary>
/// Owns and drives Lumi's ambient "presence" glow as a single, self-contained layer that lives
/// <b>behind</b> the chat + workspace surfaces. The controller <i>observes</i> already-public
/// <see cref="ChatViewModel"/> state (a strictly one-way dependency) and renders the field; the
/// production chat/workspace views reference nothing presence-specific and never push to the glow.
///
/// The glow's entire life — its visual, its placement, and every behaviour — lives here, so the
/// presence can be extended or retuned without touching any production view. The host only lends a
/// container to inject into and the view-model to read.
/// </summary>
public sealed class PresenceController : IDisposable
{
    private readonly Grid _host;
    private readonly StrataPresence _presence;
    private bool _isEnabled;

    private ChatViewModel? _vm;
    private Chat? _lastObservedChat;

    // Edge-trackers so events fire once on a real transition, not on every notification.
    private bool _attentionPending;
    private bool _wasBusy;
    private bool _wasWorking;
    private bool _lastSplitOpen;
    private bool _lastHadErrors;

    // Baseline workspace counts so a rebuild can tell what *newly* arrived (a produced file, an
    // edit, a referenced source) and answer with a single matching coloured breath.
    private int _deliverableCount;
    private int _changeCount;
    private int _sourceCount;
    // Suppresses the one workspace rebuild that follows a chat switch, so a chat's pre-existing
    // content isn't mistaken for live activity.
    private bool _suppressNextRebuild;

    // While Lumi works (and for a short settle window after) a low-priority timer re-aims the
    // field at the live message so the gaze visibly tracks the answer.
    private DispatcherTimer? _focusTimer;
    private DateTime _focusSettleUntil;
    private IDisposable? _sameStateFocusSnapRegistration;
    private int _sameStateFocusSnapGeneration;
    private DateTime _sameStateFocusHoldUntil;
    private static readonly TimeSpan SameStateFocusPeakRemaining = TimeSpan.FromMilliseconds(870);

    // One-shot deferral for the welcome -> existing-chat hand-off. Opening a chat with history
    // triggers a heavy transcript rebuild that briefly STALLS the UI thread. The focus glide is a
    // render-thread spring whose live state is read off a wall-clock; if it is armed into that stall
    // the render commit is delayed while the clock advances, so the follow timer's re-aims evaluate
    // the spring as already settled and seed a teleport -> the descent snaps instead of pouring down.
    // (Other chat-opens move well because they have no comparable focus travel.) So we hold the bright
    // welcome Halo lit at the hero and DEFER the hand-off until a Background tick arrives on-schedule
    // (UI thread idle again) with the composer realized -> the glide is then armed in the clear.
    private DispatcherTimer? _armTimer;
    private bool _armPending;
    private DateTime _armDeadline;
    private DateTime _armLastTick;
    private int _armReadyTicks;

    // Focus-target controls, found lazily in the host's visual tree (no production hooks needed).
    private Border? _welcomeOrb;
    private ItemsControl? _transcript;
    private readonly System.Collections.Generic.Dictionary<string, Control> _namedControls = new();

    // The companion pool's current normalized island anchor (null when merged into the chat).
    private Point? _companionPoint;

    // Coalesces the bursty SizeChanged stream from a window resize/maximize into a single deferred
    // focus/companion resync (at most one Background post in flight at a time).
    private bool _resyncQueued;

    public PresenceController(Grid host, bool isEnabled = true)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _isEnabled = isEnabled;

        _presence = new StrataPresence
        {
            Name = "Presence",
            IsHitTestVisible = false,
            ZIndex = 0,
            // Brightness is carried by StrataPresence's intrinsic Gain; Intensity is the consumer-facing
            // fine multiplier on top of it. The islands the field sits behind were firmed up (less
            // transparent — see App.axaml's Surface1Glass override) for readability over bright backdrops,
            // so we lift Intensity above neutral to keep the glow blooming clearly through the thicker glass.
            Intensity = 1.35,
        };

        // One continuous field spanning every column: behind the chat island, the seam, the
        // preview island and the workspace rail at once. As columns open/close the same field is
        // already there to "split" across — no second control, no hand-off illusion.
        Grid.SetColumn(_presence, 0);
        Grid.SetColumnSpan(_presence, Math.Max(1, host.ColumnDefinitions.Count));

        // Inject as the bottom-most layer so the translucent islands composite over it.
        if (_isEnabled)
            _host.Children.Insert(0, _presence);

        // A window resize/maximize re-lays out every surface, so the normalized focus point and the
        // companion island anchor (both derived from live control bounds) go stale. Re-derive them when
        // the field's size changes so the glow re-centres for the new window size instead of clinging to
        // its old-proportion placement.
        _presence.SizeChanged += OnPresenceSizeChanged;
    }

    /// <summary>The glow visual the controller owns (exposed for diagnostics/automation only).</summary>
    public StrataPresence Visual => _presence;

    public void SetEnabled(bool enabled)
    {
        if (_isEnabled == enabled)
            return;

        _isEnabled = enabled;
        if (!enabled)
        {
            _focusTimer?.Stop();
            CancelSameStateHandoff();
            CancelDeferredArm();
            _host.Children.Remove(_presence);
            return;
        }

        if (!_host.Children.Contains(_presence))
            _host.Children.Insert(0, _presence);

        UpdatePresence();
        if (_vm is { } vm && AnyIslandOpen(vm))
        {
            _companionPoint = null;
            ReaimCompanion(vm);
            ArmCompanionSettle();
        }
    }

    /// <summary>Begin observing a view-model and driving the field from its state.</summary>
    public void Attach(ChatViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (ReferenceEquals(_vm, vm))
            return;

        Detach();

        _vm = vm;
        _lastObservedChat = vm.CurrentChat;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.QuestionAsked += OnQuestionAsked;
        vm.WorkspaceContentChanged += OnWorkspaceContentChanged;

        _attentionPending = false;
        _wasBusy = vm.IsBusy;
        _wasWorking = vm.IsBusy || vm.IsStreaming;
        _lastSplitOpen = AnyIslandOpen(vm);
        _lastHadErrors = vm.HasErrorActivities;
        SeedWorkspaceBaseline(vm);

        // Sync the companion pool to the initial island state (clear it if no island is open).
        _companionPoint = null;
        if (_lastSplitOpen)
        {
            ReaimCompanion(vm);
            ArmCompanionSettle();
        }
        else
        {
            _presence.Merge();
        }

        UpdatePresence();
    }

    /// <summary>
    /// Re-point the SAME persistent field at a different chat surface. The app swaps the entire
    /// <see cref="ChatViewModel"/> instance on a chat open (each chat gets its own surface), so the
    /// host view re-creates its preview panel — but the glow must NOT be torn down with it. Unlike
    /// <see cref="Attach"/> this preserves <see cref="_lastObservedChat"/>, so the welcome(null) ->
    /// existing transition is still recognised across the surface swap and the one continuous field
    /// GLIDES from the hero down to the composer, instead of the welcome glow being destroyed and a
    /// fresh one reborn already at the composer (which reads as "it doesn't move").
    /// </summary>
    public void Repoint(ChatViewModel vm)
    {
        ArgumentNullException.ThrowIfNull(vm);
        if (ReferenceEquals(_vm, vm))
            return;

        // Swap observers WITHOUT going through Detach() — Detach nulls _lastObservedChat, which would
        // erase the "came from welcome" signal the transition below depends on.
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.QuestionAsked -= OnQuestionAsked;
            _vm.WorkspaceContentChanged -= OnWorkspaceContentChanged;
        }
        CancelDeferredArm();

        _vm = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        vm.QuestionAsked += OnQuestionAsked;
        vm.WorkspaceContentChanged += OnWorkspaceContentChanged;

        // Re-seed the edge-trackers from the new surface so subsequent send/finish/split/error
        // gestures fire correctly — but deliberately leave _lastObservedChat pointing at what the
        // field was last showing, so EvaluateChatChange can read the real delta.
        _wasBusy = vm.IsBusy;
        _wasWorking = vm.IsBusy || vm.IsStreaming;
        _lastHadErrors = vm.HasErrorActivities;

        _companionPoint = null;
        _lastSplitOpen = AnyIslandOpen(vm);
        if (_lastSplitOpen)
        {
            ReaimCompanion(vm);
            ArmCompanionSettle();
        }
        else
        {
            _presence.Merge();
        }

        EvaluateChatChange(vm.CurrentChat);
    }

    /// <summary>
    /// Drive the field from a change of displayed chat. Shared by the in-surface property change and
    /// the cross-surface <see cref="Repoint"/> so both treat a welcome -> existing hand-off identically.
    /// </summary>
    private void EvaluateChatChange(Chat? current)
    {
        if (_vm is not { } vm)
            return;
        if (ReferenceEquals(current, _lastObservedChat))
            return;

        var previous = _lastObservedChat;
        var previousState = _presence.State;
        var fromWelcome = previous is null;
        _lastObservedChat = current;

        // Any in-flight deferred arm is stale the moment the chat changes again.
        CancelDeferredArm();
        CancelSameStateFocusSnap();

        _attentionPending = false;
        var handoffSameState =
            _isEnabled &&
            previous is not null &&
            current is { Messages.Count: > 0 } &&
            previous.Id != current.Id &&
            previousState == ResolvePresenceState(vm);
        if (!handoffSameState)
            _presence.CancelHandoff();
        if (current is { Messages.Count: > 0 })
        {
            // Existing chat: a load rebuild follows and replaces the workspace collections,
            // so re-baseline from current state and consume that one rebuild.
            SeedWorkspaceBaseline(vm);
            _suppressNextRebuild = true;

            if (fromWelcome)
            {
                // welcome -> existing is the ONE chat-open that pairs a big intended focus move
                // (hero -> composer) with that heavy rebuild. Keep the welcome Halo lit and DEFER
                // the hand-off until the load drains; then the bright pool RIDES the focus glide
                // DOWN in the clear (see StrataPresence.UpdateHalo) instead of snapping mid-stall.
                BeginDeferredArm();
                return;
            }
            // existing -> existing rests at the composer in both chats (no big move), so even
            // though a rebuild follows there is nothing to desync — render immediately.
        }
        else
        {
            // New/empty chat (or welcome): no load rebuild follows, so baseline at zero and
            // don't suppress — that keeps the very first produced file/source/edit's breath.
            _deliverableCount = 0;
            _changeCount = 0;
            _sourceCount = 0;
            _suppressNextRebuild = false;
            if (current is { Messages.Count: 0 })
                _presence.Pulse(PresencePulse.Awaken);
        }
        var handoffActive = false;
        if (handoffSameState)
        {
            _presence.Handoff();
            var remaining = _presence.HandoffRemaining;
            handoffActive = remaining > TimeSpan.Zero;
            if (handoffActive)
                BeginSameStateFocusSnap(remaining);
        }
        UpdatePresence(preserveFocus: handoffActive);
    }

    /// <summary>Stop observing the current view-model (the field stays, at rest).</summary>
    public void Detach()
    {
        if (_vm is null)
            return;

        _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm.QuestionAsked -= OnQuestionAsked;
        _vm.WorkspaceContentChanged -= OnWorkspaceContentChanged;
        _vm = null;
        _lastObservedChat = null;
        _companionPoint = null;
        _focusTimer?.Stop();
        CancelSameStateHandoff();
        CancelDeferredArm();
    }

    public void Dispose()
    {
        Detach();
        _presence.SizeChanged -= OnPresenceSizeChanged;
        _focusTimer?.Stop();
        _focusTimer = null;
        _armTimer?.Stop();
        _armTimer = null;
        _host.Children.Remove(_presence);
        _presence.Dispose();
    }

    /// <summary>
    /// A window resize/maximize re-lays out every surface, so the normalized <see cref="StrataPresence.FocusPoint"/>
    /// and the companion island anchor — both derived from live control bounds against the field's own bounds —
    /// no longer describe the same on-screen spot (a fixed-width rail column makes the composer's normalized X/Y
    /// drift as the window grows). Re-derive both once the layout pass settles. The recompute is deferred to
    /// <see cref="DispatcherPriority.Background"/> so the sibling controls (composer, islands) have taken their
    /// NEW bounds before we read them, and coalesced via <see cref="_resyncQueued"/> so a drag-resize's burst of
    /// size changes collapses into one resync. The re-derived focus is applied as a rigid SNAP
    /// (<see cref="StrataPresence.ResyncFocus"/>), not a spring, so the FocusPoint refinement adds no jitter on
    /// top of the drag. This is belt-and-braces: the field's structural placement on resize is handled inside
    /// <see cref="StrataPresence"/> (its <c>ArrangeOverride</c> arranges each lobe host AT its focal target, so
    /// the resize arrange writes the focal Offset and the field can never collapse to centre even if this
    /// Background recompute is starved by a continuous drag). This recompute only refines the normalized target
    /// to track the re-proportioned composer, applied the next time Background gets a slice.
    /// </summary>
    private void OnPresenceSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (_resyncQueued)
            return;
        _resyncQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            // try/finally + clear-last: an exception can't strand the flag (which would permanently disable
            // resize re-aim), and were the recompute to provoke another SizeChanged mid-flight the still-set
            // flag suppresses a re-entrant post. (UpdateFocusTarget/ReaimCompanion only read control bounds
            // and write FocusPoint/companion offsets — none change the presence's own size — so this is
            // defensive, not a real re-entrancy path.)
            try
            {
                if (_vm is { } vm)
                {
                    UpdateFocusTarget(snap: true);
                    ReaimCompanion(vm);
                }
            }
            finally
            {
                _resyncQueued = false;
            }
        }, DispatcherPriority.Background);
    }

    private static bool AnyIslandOpen(ChatViewModel vm)
        => vm.IsWorkspacePanelOpen || vm.IsBrowserOpen || vm.IsDiffOpen || vm.IsPlanOpen;

    // ── View-model observation (one-way) ─────────────────────────────────────────────

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is not { } vm)
            return;

        switch (e.PropertyName)
        {
            case nameof(ChatViewModel.CurrentChat):
                EvaluateChatChange(vm.CurrentChat);
                break;

            case nameof(ChatViewModel.IsBusy):
            {
                CancelSameStateHandoff();
                var busy = vm.IsBusy;
                if (!busy && _wasBusy)
                {
                    // Celebrate a finished turn with a soft completion bloom.
                    _presence.Pulse(PresencePulse.Bloom);
                    _attentionPending = false;
                }
                _wasBusy = busy;
                // Fire the felt vertical edge gesture BEFORE UpdatePresence re-aims the focus: the gate
                // reads the field's resting position to tell a send/finish-in-existing-chat from welcome.
                UpdateWorkEdge();
                UpdatePresence();
                break;
            }

            case nameof(ChatViewModel.IsStreaming):
            {
                CancelSameStateHandoff();
                // Streaming resuming means a pending question was answered.
                if (vm.IsStreaming)
                    _attentionPending = false;
                UpdateWorkEdge();
                UpdatePresence();
                break;
            }

            case nameof(ChatViewModel.AnimatePresenceWhileWorking):
                UpdatePresence();
                break;

            case nameof(ChatViewModel.IsWorkspacePanelOpen):
            case nameof(ChatViewModel.IsBrowserOpen):
            case nameof(ChatViewModel.IsDiffOpen):
            case nameof(ChatViewModel.IsPlanOpen):
                HandleSplitChanged(vm);
                break;

            case nameof(ChatViewModel.HasErrorActivities):
            {
                var hasErrors = vm.HasErrorActivities;
                if (hasErrors && !_lastHadErrors)
                    _presence.Pulse(PresencePulse.Alert);
                _lastHadErrors = hasErrors;
                break;
            }
        }
    }

    private void OnQuestionAsked(string questionId, string question, string optionsJson, bool allowFreeText)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => OnQuestionAsked(questionId, question, optionsJson, allowFreeText));
            return;
        }

        _attentionPending = true;
        CancelSameStateHandoff();
        UpdatePresence();
        _presence.Pulse(PresencePulse.Ripple);
    }

    /// <summary>
    /// Drives the felt vertical "move" at the edges of a turn in an <i>existing</i> chat:
    /// <list type="bullet">
    /// <item>On the <b>rising</b> edge (work begins) — where the field rests low at the composer — lift
    /// the whole presence UP off the composer so the send reads as the glow rising into the conversation.</item>
    /// <item>On the <b>falling</b> edge (turn finishes) — where the field sits high at the active answer —
    /// pour it back DOWN to settle at the composer (the mirror of the lift), so completing reads as the
    /// light descending home.</item>
    /// </list>
    /// Each gesture is gated on the field actually being where the move starts (low ≈ composer for the
    /// lift, high ≈ answer for the descent), so neither fires on the welcome canvas (whose luminance is
    /// centred high on the Lumi mark) nor when the field is already at rest. The sustained new height is
    /// carried by <see cref="UpdateFocusTarget"/>; this is the felt kick on top of it. Edge-tracked via
    /// <see cref="_wasWorking"/> so each transition fires exactly once, and called BEFORE
    /// <see cref="UpdatePresence"/> so the gate reads the field's pre-move resting position.
    /// </summary>
    private void UpdateWorkEdge()
    {
        if (_vm is not { } vm)
            return;
        var working = vm.IsBusy || vm.IsStreaming;
        if (vm.CurrentChat is not null)
        {
            if (working && !_wasWorking && _presence.FocusPoint.Y >= 0.58)
                _presence.Lift();
            else if (!working && _wasWorking && _presence.FocusPoint.Y < 0.58)
                _presence.Descend();
        }
        _wasWorking = working;
    }

    /// <summary>
    /// The glow physically splits in two when a companion island opens: the whole field first
    /// surges toward the seam (<see cref="StrataPresence.Emit"/>), then a second pool separates and
    /// travels into the island and parks there (<see cref="StrataPresence.SplitToIsland"/>) while the
    /// main field flows back to the chat. On close the companion retracts and merges back into one.
    /// </summary>
    private void HandleSplitChanged(ChatViewModel vm)
    {
        var open = AnyIslandOpen(vm);
        if (open && !_lastSplitOpen)
        {
            // Opening: surge toward the seam now; the companion blooms + travels in once the island
            // has taken its space (the settle window below keeps re-aiming until it lays out).
            _presence.Emit(PresenceEdge.Right);
            _companionPoint = null;
            ReaimCompanion(vm);
            ArmCompanionSettle();
        }
        else if (!open && _lastSplitOpen)
        {
            // Closing: the companion detaches from the island and glides ALL the way home, retracting
            // back into the chat field in one continuous travel (Merge now homes it onto the field's
            // own centre); a soft reunion breath then marks the two pools becoming one again. No full
            // field sweep — the field never left the chat, so the motion belongs to the returning pool.
            _presence.Merge();
            _presence.Pulse(PresencePulse.Settle);
            _companionPoint = null;
        }
        else if (open)
        {
            // Still open, but a different island took focus (e.g. browser → diff) — re-aim the pool.
            ReaimCompanion(vm);
            ArmCompanionSettle();
        }

        _lastSplitOpen = open;
        UpdatePresence();
    }

    /// <summary>
    /// Aims the companion pool at the currently open island. Resolved lazily from layout, so until
    /// the island has actually taken its space this is a no-op (the settle timer retries); deduped so
    /// a settled pool never restarts its glide in place.
    /// </summary>
    private void ReaimCompanion(ChatViewModel vm)
    {
        if (!AnyIslandOpen(vm))
            return;
        if (TryGetIslandFocus(vm) is not { } pt)
            return;
        if (_companionPoint is { } prev && Math.Abs(pt.X - prev.X) + Math.Abs(pt.Y - prev.Y) < 0.012)
            return;
        // Commit the dedup anchor only once the split actually applied. While the presence is not yet
        // composition-ready (e.g. an island is already open on first attach) SplitToIsland no-ops;
        // caching the point regardless would dedup every settle retry and the companion never appears.
        if (_presence.SplitToIsland(pt))
            _companionPoint = pt;
    }

    /// <summary>Keep the focus/companion follow alive briefly so the pool lands once the island lays out.</summary>
    private void ArmCompanionSettle()
    {
        _focusSettleUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(1600);
        EnsureFocusFollow();
    }

    /// <summary>
    /// The workspace index rebuilt — answer any net-new content with a single, subtle coloured
    /// "breath" matching what arrived (a produced file, a workspace edit, a referenced source).
    /// One field, one breath; the focus lean (toward an open island) lands it where the user looks.
    /// </summary>
    private void OnWorkspaceContentChanged()
    {
        if (_vm is not { } vm)
            return;

        var deliverables = vm.WorkspaceDeliverables.Count;
        var changes = vm.WorkspaceChanges.Count;
        var sources = vm.WorkspaceSources.Count;

        var newDeliverable = deliverables > _deliverableCount;
        var newChange = changes > _changeCount;
        var newSource = sources > _sourceCount;

        _deliverableCount = deliverables;
        _changeCount = changes;
        _sourceCount = sources;

        // The first rebuild after a chat switch only re-establishes the baseline.
        if (_suppressNextRebuild)
        {
            _suppressNextRebuild = false;
            return;
        }

        if (!newDeliverable && !newChange && !newSource)
            return;

        // One breath, by salience: a produced file reads strongest, then an edit, then a source.
        var kind = newDeliverable ? PresencePulse.Create
            : newChange ? PresencePulse.Edit
            : PresencePulse.Browse;
        _presence.Pulse(kind);
    }

    private void SeedWorkspaceBaseline(ChatViewModel vm)
    {
        _deliverableCount = vm.WorkspaceDeliverables.Count;
        _changeCount = vm.WorkspaceChanges.Count;
        _sourceCount = vm.WorkspaceSources.Count;
    }

    // ── Rendering the field from observed state ──────────────────────────────────────

    /// <summary>Maps the current view-model state onto the ambient presence field.</summary>
    private void UpdatePresence(bool preserveFocus = false)
    {
        if (!_isEnabled)
        {
            CancelDeferredArm();
            return;
        }

        if (_vm is not { } vm)
            return;

        // Any state we render here supersedes a pending welcome -> existing deferral (this is also the
        // path the deferred arm itself takes once the load drains), so clear it and proceed.
        CancelDeferredArm();
        _presence.State = ResolvePresenceState(vm);
        // A soft luminance haloes the Lumi mark on the welcome screen ("new chat"); it clears the
        // instant a real canvas takes over.
        _presence.Halo = vm.CurrentChat is null;
        _presence.AnimateWhileWorking = vm.AnimatePresenceWhileWorking;
        var holdFocus = preserveFocus || _sameStateFocusSnapRegistration is not null;
        if (!holdFocus)
            UpdateFocusTarget();

        // Follow the live message while working; keep a brief settle window afterward so the glow
        // can find the final turn and then glide down to rest at the composer.
        var working = vm.IsBusy || vm.IsStreaming;
        if (!working)
            _focusSettleUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(1600);
        if (!holdFocus)
            EnsureFocusFollow();
    }

    private PresenceState ResolvePresenceState(ChatViewModel vm)
    {
        if (vm.CurrentChat is null)
            return PresenceState.Dormant;
        if (_attentionPending)
            return PresenceState.Attention;
        if (vm.IsStreaming)
            return PresenceState.Streaming;
        return vm.IsBusy ? PresenceState.Thinking : PresenceState.Idle;
    }

    /// <summary>
    /// Aims <see cref="StrataPresence.FocusPoint"/> at whatever currently deserves attention: on a new
    /// chat the middle of the hero, near the suggestion chips; while Lumi works, the live/last message
    /// as it grows; and when idle in an existing chat, the composer — illuminating up from where the
    /// user types. The new→existing and busy→idle changes therefore read as the light gliding *down*
    /// into the conversation. (The companion pool, not this focal point, leans into an open island.)
    /// </summary>
    private bool UpdateFocusTarget(bool snap = false, bool allowFallbackSnap = false)
    {
        if (_vm is not { } vm)
            return false;

        // While a welcome -> existing hand-off is deferred, keep the welcome luminance pooled at the
        // hero; the focus glide is armed (once) when the load drains, never re-aimed into the stall.
        if (_armPending)
            return false;

        var working = vm.IsBusy || vm.IsStreaming || _attentionPending;

        double x = 0.5, y = 0.5;
        // Whether a REAL control anchor (composer / live turn / welcome mark) backed this target, vs a
        // hardcoded centre fallback used only before anything is realized. On a resize re-aim we refuse
        // to snap to the fallback: a transiently-unresolved composer (read mid-relayout) must not yank an
        // established focus to screen-centre — that was the "resize jumps the orb to the middle" glitch.
        var anchored = false;
        if (vm.CurrentChat is null)
        {
            // New chat: pool in the middle of the hero, gathered toward the suggestion chips (where
            // the eye and the next action live) while still washing up over the Lumi mark above them.
            var orb = TryGetWelcomeOrbFocus();
            var suggestions = TryGetControlFocus("WelcomeSuggestions", 0.5, 0.0, 1.0);
            if (orb is { } o && suggestions is { } s)
            {
                x = o.X * 0.4 + s.X * 0.6;
                y = o.Y * 0.4 + s.Y * 0.6;
                anchored = true;
            }
            else if (suggestions is { } s2) { x = s2.X; y = s2.Y; anchored = true; }
            else if (orb is { } o2) { x = o2.X; y = o2.Y; anchored = true; }
            else { x = 0.5; y = 0.5; }
            y = Math.Clamp(y, 0.30, 0.74);
        }
        else if (working && TryGetActiveTurnFocus() is { } active)
        {
            x = active.X;
            y = active.Y;
            anchored = true;
        }
        else if (!working && TryGetControlFocus("ComposerContainer", 0.28, 0.16, 0.86) is { } composer)
        {
            // Idle in an existing chat: settle low, at the composer, illuminating upward.
            x = composer.X;
            y = composer.Y;
            anchored = true;
        }
        else
        {
            // No realized anchor yet — sit up-centre while working (lifted toward the conversation),
            // and low near the composer when idle.
            x = 0.5;
            y = working ? 0.44 : 0.80;
        }

        // A resize re-aim that found no live anchor leaves the established focus untouched (see above):
        // better to hold the last good spot for a frame than to flash the field to centre.
        if (snap && !anchored && !allowFallbackSnap)
            return false;

        var target = new Point(Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));

        if (snap)
        {
            // Resize: re-pin rigidly (no spring) so the field tracks the re-laid-out canvas without lag
            // or the spring's deferred-completion revert-to-centre. No dedup — the underlying pixels moved
            // even when the normalized point barely did, and the resize arrange clobbered the base Offset.
            _presence.ResyncFocus(target);
            return true;
        }

        var cur = _presence.FocusPoint;
        // Dedup micro-moves so the follow timer never restarts the glide in place. While working we
        // use a tighter threshold so the gaze visibly tracks the live answer as it grows.
        var dedup = working ? 0.006 : 0.014;
        if (Math.Abs(target.X - cur.X) + Math.Abs(target.Y - cur.Y) < dedup)
            return true;
        _presence.FocusPoint = target;
        return true;
    }

    /// <summary>
    /// A same-state chat switch should show only the focal glow, never a second whole-field spring.
    /// Pause the old follow loop while the replacement transcript realizes, then snap once to the new
    /// anchor at Background priority. A few short retries cover frame-budgeted transcript mounting;
    /// the final attempt uses the normal state fallback so focus can never remain tied to the old chat.
    /// </summary>
    private void BeginSameStateFocusSnap(TimeSpan handoffRemaining)
    {
        CancelSameStateFocusSnap();
        _focusTimer?.Stop();
        _sameStateFocusHoldUntil = DateTime.UtcNow + handoffRemaining;
        var generation = ++_sameStateFocusSnapGeneration;
        var initialDelay = handoffRemaining - SameStateFocusPeakRemaining;
        if (initialDelay < TimeSpan.Zero)
            initialDelay = TimeSpan.Zero;
        ScheduleSameStateFocusSnap(generation, attempt: 0, initialDelay);
    }

    private void ScheduleSameStateFocusSnap(int generation, int attempt, TimeSpan delay)
    {
        _sameStateFocusSnapRegistration = DispatcherTimer.RunOnce(
            () =>
            {
                if (generation != _sameStateFocusSnapGeneration)
                    return;

                _sameStateFocusSnapRegistration = null;
                if (!_isEnabled || _vm is null)
                    return;

                var snapped = UpdateFocusTarget(
                    snap: true,
                    allowFallbackSnap: attempt >= 4);
                if (!snapped && attempt < 4)
                {
                    var retryDelay = _sameStateFocusHoldUntil - DateTime.UtcNow;
                    if (retryDelay > TimeSpan.FromMilliseconds(80))
                        retryDelay = TimeSpan.FromMilliseconds(80);
                    if (retryDelay < TimeSpan.Zero)
                        retryDelay = TimeSpan.Zero;
                    ScheduleSameStateFocusSnap(generation, attempt + 1, retryDelay);
                    return;
                }

                var remaining = _sameStateFocusHoldUntil - DateTime.UtcNow;
                if (remaining > TimeSpan.Zero)
                {
                    _sameStateFocusSnapRegistration = DispatcherTimer.RunOnce(
                        () => ResumeFocusAfterSameStateHandoff(generation),
                        remaining,
                        DispatcherPriority.Background);
                    return;
                }

                ResumeFocusAfterSameStateHandoff(generation);
            },
            delay,
            DispatcherPriority.Background);
    }

    private void ResumeFocusAfterSameStateHandoff(int generation)
    {
        if (generation != _sameStateFocusSnapGeneration)
            return;

        _sameStateFocusSnapRegistration = null;
        _sameStateFocusHoldUntil = default;
        UpdateFocusTarget(snap: true, allowFallbackSnap: true);
        _focusSettleUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(1600);
        EnsureFocusFollow();
    }

    private void CancelSameStateFocusSnap()
    {
        _sameStateFocusSnapGeneration++;
        _sameStateFocusSnapRegistration?.Dispose();
        _sameStateFocusSnapRegistration = null;
        _sameStateFocusHoldUntil = default;
    }

    private void CancelSameStateHandoff()
    {
        CancelSameStateFocusSnap();
        _presence.CancelHandoff();
    }

    /// <summary>
    /// Returns the Lumi welcome mark's centre in the field's own normalized (0..1) space so the
    /// glow can pool into a soft luminance around the icon. Null until the orb (and the field)
    /// have a real layout.
    /// </summary>
    private Point? TryGetWelcomeOrbFocus()
    {
        var orb = ResolveWelcomeOrb();
        if (orb is null)
            return null;

        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1)
            return null;
        if (orb.Bounds.Width <= 0 || orb.Bounds.Height <= 0)
            return null;

        var centre = new Point(orb.Bounds.Width / 2, orb.Bounds.Height / 2);
        if (orb.TranslatePoint(centre, _presence) is not { } mapped)
            return null;

        return new Point(
            Math.Clamp(mapped.X / pw, 0.0, 1.0),
            Math.Clamp(mapped.Y / ph, 0.0, 1.0));
    }

    /// <summary>
    /// Returns the normalized (0..1) anchor — near the bottom, where new tokens land — of the
    /// lowest realized transcript turn, expressed in the field's own space.
    /// </summary>
    private Point? TryGetActiveTurnFocus()
    {
        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1)
            return null;

        TranscriptTurnControl? active = null;
        double bestY = double.MinValue;
        Point bestAnchor = default;
        foreach (var ctrl in EnumerateRealizedTurnControls())
        {
            if (ctrl.Bounds.Width <= 0 || ctrl.Bounds.Height <= 0)
                continue;
            var anchor = new Point(ctrl.Bounds.Width / 2, ctrl.Bounds.Height * 0.32);
            if (ctrl.TranslatePoint(anchor, _presence) is not { } p)
                continue;
            if (p.Y > bestY)
            {
                bestY = p.Y;
                bestAnchor = p;
                active = ctrl;
            }
        }

        if (active is null)
            return null;

        // Sit UP in the conversation while Lumi works — clamped clearly above the composer's resting
        // band so that sending an existing chat visibly LIFTS the field off the composer (rather than
        // landing right back beside it), while still tracking which turn is live.
        return new Point(
            Math.Clamp(bestAnchor.X / pw, 0.18, 0.82),
            Math.Clamp(bestAnchor.Y / ph, 0.24, 0.52));
    }

    private void EnsureFocusFollow()
    {
        if (!_isEnabled)
            return;

        _focusTimer ??= CreateFocusFollowTimer();
        if (!_focusTimer.IsEnabled)
            _focusTimer.Start();
    }

    private DispatcherTimer CreateFocusFollowTimer()
    {
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(110),
        };
        timer.Tick += (_, _) =>
        {
            UpdateFocusTarget();
            var vm = _vm;
            if (vm is not null)
                ReaimCompanion(vm);
            var working = vm is not null && (vm.IsBusy || vm.IsStreaming);
            if (!working && DateTime.UtcNow >= _focusSettleUntil)
                _focusTimer?.Stop();
        };
        return timer;
    }

    // ── Deferred welcome -> existing-chat arm (glide in the clear, after the load drains) ─────

    /// <summary>
    /// Hold the welcome luminance and arm the new -> existing descent only once the chat-open's heavy
    /// transcript rebuild has drained off the UI thread, so the render-thread focus spring is armed in
    /// the clear (its wall-clock baseline matches the render commit) and pours down smoothly.
    /// </summary>
    private void BeginDeferredArm()
    {
        if (!_isEnabled)
            return;

        _armPending = true;
        _armReadyTicks = 0;
        _armLastTick = DateTime.UtcNow;
        // Safety cap: never let the hand-off get stuck behind a pathologically long rebuild.
        _armDeadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(1200);

        _armTimer ??= CreateArmTimer();
        if (!_armTimer.IsEnabled)
            _armTimer.Start();
    }

    private void CancelDeferredArm()
    {
        _armPending = false;
        _armReadyTicks = 0;
        _armTimer?.Stop();
    }

    private DispatcherTimer CreateArmTimer()
    {
        // Background priority: a tick only fires when nothing higher-priority is queued, so a run of
        // on-schedule ticks is direct evidence the UI thread has idle headroom again (rebuild drained).
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        timer.Tick += (_, _) =>
        {
            if (!_armPending)
            {
                _armTimer?.Stop();
                return;
            }

            var now = DateTime.UtcNow;
            var gap = (now - _armLastTick).TotalMilliseconds;
            _armLastTick = now;

            // The composer realizes as part of the chat view's layout — its presence (with real bounds)
            // confirms the canvas the descent aims at actually exists yet.
            var composerReady = _vm is not null
                && TryGetControlFocus("ComposerContainer", 0.28, 0.16, 0.86) is not null;

            // Two consecutive on-schedule ticks (~100ms of calm) with the composer laid out means the
            // load has genuinely drained — a single lucky gap mid-rebuild won't trip it.
            if (gap <= 130 && composerReady)
                _armReadyTicks++;
            else
                _armReadyTicks = 0;

            if (_armReadyTicks >= 2 || now >= _armDeadline)
                CompleteDeferredArm();
        };
        return timer;
    }

    /// <summary>
    /// Fires the welcome -> existing descent once the load has drained: a whole-field <see cref="StrataPresence.Descend"/>
    /// impulse (the felt downward "pour" — the SAME punchy gesture the send/finish edges use, so this reads as
    /// a real position-to-position move, not just the slower partial focus glide) followed by
    /// <see cref="UpdatePresence"/> which re-aims the focus to the composer and rides the Halo down. The
    /// impulse is a fixed render-thread keyframe animation, so unlike the velocity-preserving focus spring it
    /// is immune to any residual UI-thread stall — the motion shows even if the rebuild over-ran the deadline.
    /// </summary>
    private void CompleteDeferredArm()
    {
        if (!_isEnabled)
        {
            CancelDeferredArm();
            return;
        }

        // Fire the felt impulse BEFORE UpdatePresence re-aims the focus (matching the work-edge gesture order),
        // so the punch reads off the field's resting welcome position.
        _presence.Descend();
        // Renders Idle + extinguishes the Halo (ride-down) + aims the focus at the composer, and
        // CancelDeferredArm (called inside) stops the arm timer — the descent is now armed in the clear.
        UpdatePresence();
    }

    private Border? ResolveWelcomeOrb()
    {
        if (_welcomeOrb is { } cached && cached.IsAttachedToVisualTree())
            return cached;

        _welcomeOrb = _host.GetVisualDescendants()
            .OfType<Border>()
            .FirstOrDefault(b => b.Name == "WelcomeOrb");
        return _welcomeOrb;
    }

    /// <summary>
    /// Maps a named control's anchor point (its horizontal centre and <paramref name="anchorYFraction"/>
    /// of its height) into the field's own normalized (0..1) space, clamping Y to
    /// [<paramref name="minY"/>, <paramref name="maxY"/>]. Null until both the control and the field
    /// have a real layout.
    /// </summary>
    private Point? TryGetControlFocus(string name, double anchorYFraction, double minY, double maxY)
    {
        var ctrl = ResolveNamed(name);
        if (ctrl is null)
            return null;

        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1)
            return null;
        if (ctrl.Bounds.Width <= 0 || ctrl.Bounds.Height <= 0)
            return null;

        var anchor = new Point(ctrl.Bounds.Width / 2, ctrl.Bounds.Height * anchorYFraction);
        if (ctrl.TranslatePoint(anchor, _presence) is not { } mapped)
            return null;

        return new Point(
            Math.Clamp(mapped.X / pw, 0.0, 1.0),
            Math.Clamp(mapped.Y / ph, minY, maxY));
    }

    private Control? ResolveNamed(string name)
    {
        if (_namedControls.TryGetValue(name, out var cached) && cached.IsAttachedToVisualTree())
            return cached;

        var found = _host.GetVisualDescendants()
            .OfType<Control>()
            .FirstOrDefault(c => c.Name == name);
        if (found is not null)
            _namedControls[name] = found;
        return found;
    }

    /// <summary>
    /// Returns the normalized (0..1) centre of the currently open island — preferring the specific
    /// open panel, falling back to the empty region to the right of the chat. Null until the island
    /// has actually taken its space (so the companion never blooms at a stale spot).
    /// </summary>
    private Point? TryGetIslandFocus(ChatViewModel vm)
    {
        var name = vm.IsBrowserOpen ? "BrowserIsland"
            : vm.IsDiffOpen ? "DiffIsland"
            : vm.IsPlanOpen ? "PlanIsland"
            : vm.IsWorkspacePanelOpen ? "WorkspaceRail"
            : null;

        if (name is not null && TryGetControlFocus(name, 0.5, 0.1, 0.9) is { } centre)
            return centre;

        return TryGetChatRightRegion();
    }

    /// <summary>
    /// The centre of the region to the right of the (now-shrunken) chat island — used as the island
    /// anchor while the specific panel is still laying out. Null while the chat is still full-width.
    /// </summary>
    private Point? TryGetChatRightRegion()
    {
        if (ResolveNamed("ChatIsland") is not { } chat)
            return null;

        var pw = _presence.Bounds.Width;
        var ph = _presence.Bounds.Height;
        if (pw <= 1 || ph <= 1)
            return null;
        if (chat.Bounds.Width <= 0 || chat.Bounds.Height <= 0)
            return null;

        var rightMid = new Point(chat.Bounds.Width, chat.Bounds.Height * 0.5);
        if (chat.TranslatePoint(rightMid, _presence) is not { } mapped)
            return null;

        var chatRight = Math.Clamp(mapped.X / pw, 0.0, 1.0);
        // The chat hasn't yielded any space yet — no island region to aim at.
        if (chatRight >= 0.985)
            return null;

        return new Point(
            Math.Clamp((chatRight + 1.0) / 2.0, 0.0, 1.0),
            Math.Clamp(mapped.Y / ph, 0.2, 0.8));
    }

    private System.Collections.Generic.IEnumerable<TranscriptTurnControl> EnumerateRealizedTurnControls()
    {
        if (_transcript is null || !_transcript.IsAttachedToVisualTree())
        {
            _transcript = _host.GetVisualDescendants()
                .OfType<ItemsControl>()
                .FirstOrDefault(c => c.Name == "Transcript");
        }

        var host = _transcript?.ItemsPanelRoot;
        return host is null
            ? Enumerable.Empty<TranscriptTurnControl>()
            : host.GetVisualDescendants().OfType<TranscriptTurnControl>();
    }
}
