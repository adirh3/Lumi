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

    // Focus-target controls, found lazily in the host's visual tree (no production hooks needed).
    private Border? _welcomeOrb;
    private ItemsControl? _transcript;
    private readonly System.Collections.Generic.Dictionary<string, Control> _namedControls = new();

    // The companion pool's current normalized island anchor (null when merged into the chat).
    private Point? _companionPoint;

    public PresenceController(Grid host)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));

        _presence = new StrataPresence
        {
            Name = "Presence",
            IsHitTestVisible = false,
            ZIndex = 0,
            // Brightness is carried by StrataPresence's intrinsic Gain; Intensity stays neutral
            // here and is the fine ± lever (it now reads through the glassy chat surfaces).
            Intensity = 1.0,
        };

        // One continuous field spanning every column: behind the chat island, the seam, the
        // preview island and the workspace rail at once. As columns open/close the same field is
        // already there to "split" across — no second control, no hand-off illusion.
        Grid.SetColumn(_presence, 0);
        Grid.SetColumnSpan(_presence, Math.Max(1, host.ColumnDefinitions.Count));

        // Inject as the bottom-most layer so the translucent islands composite over it.
        _host.Children.Insert(0, _presence);
    }

    /// <summary>The glow visual the controller owns (exposed for diagnostics/automation only).</summary>
    public StrataPresence Visual => _presence;

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
    }

    public void Dispose()
    {
        Detach();
        _focusTimer?.Stop();
        _focusTimer = null;
        _host.Children.Remove(_presence);
        _presence.Dispose();
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
            {
                var current = vm.CurrentChat;
                if (ReferenceEquals(current, _lastObservedChat))
                    break;
                var fromWelcome = _lastObservedChat is null;
                _lastObservedChat = current;

                _attentionPending = false;
                if (current is { Messages.Count: > 0 })
                {
                    // Existing chat: a load rebuild follows and replaces the workspace collections,
                    // so re-baseline from current state and consume that one rebuild.
                    SeedWorkspaceBaseline(vm);
                    _suppressNextRebuild = true;
                    // Coming from the welcome canvas, hand the presence off into the conversation:
                    // a soft "arrival" swell rides the focus glide down from the Lumi mark, so the
                    // new-chat → existing-chat change reads as the light *travelling and settling*
                    // here, not an instant swap.
                    if (fromWelcome)
                        _presence.Pulse(PresencePulse.Settle);
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
                UpdatePresence();
                break;
            }

            case nameof(ChatViewModel.IsBusy):
            {
                var busy = vm.IsBusy;
                if (!busy && _wasBusy)
                {
                    // Celebrate a finished turn with a soft completion bloom.
                    _presence.Pulse(PresencePulse.Bloom);
                    _attentionPending = false;
                }
                _wasBusy = busy;
                // Lift BEFORE UpdatePresence re-aims the focus: the gate reads the field's resting
                // position (still at the composer) to tell a send-in-existing-chat from the welcome canvas.
                MaybeLiftOnWorkStart();
                UpdatePresence();
                break;
            }

            case nameof(ChatViewModel.IsStreaming):
            {
                // Streaming resuming means a pending question was answered.
                if (vm.IsStreaming)
                    _attentionPending = false;
                MaybeLiftOnWorkStart();
                UpdatePresence();
                break;
            }

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
        UpdatePresence();
        _presence.Pulse(PresencePulse.Ripple);
    }

    /// <summary>
    /// The instant Lumi takes a turn (work begins) in an <i>existing</i> chat — where the field is
    /// resting low at the composer — lift the whole presence up off the composer so the send reads as
    /// the glow rising into the conversation. Gated on the field actually sitting low (≈ the composer),
    /// so it never fires on the welcome canvas, whose luminance is centred high on the Lumi mark. The
    /// sustained new height is carried by <see cref="UpdateFocusTarget"/> retargeting to the active turn;
    /// this is the felt "lift-off" kick on top of it. Edge-tracked so a turn lifts exactly once.
    /// </summary>
    private void MaybeLiftOnWorkStart()
    {
        if (_vm is not { } vm)
            return;
        var working = vm.IsBusy || vm.IsStreaming;
        if (working && !_wasWorking && vm.CurrentChat is not null && _presence.FocusPoint.Y >= 0.58)
            _presence.Lift();
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
    private void UpdatePresence()
    {
        if (_vm is not { } vm)
            return;

        PresenceState state;
        if (vm.CurrentChat is null)
            state = PresenceState.Dormant;
        else if (_attentionPending)
            state = PresenceState.Attention;
        else if (vm.IsStreaming)
            state = PresenceState.Streaming;
        else if (vm.IsBusy)
            state = PresenceState.Thinking;
        else
            state = PresenceState.Idle;

        _presence.State = state;
        // A soft luminance haloes the Lumi mark on the welcome screen ("new chat"); it clears the
        // instant a real canvas takes over.
        _presence.Halo = vm.CurrentChat is null;
        UpdateFocusTarget();

        // Follow the live message while working; keep a brief settle window afterward so the glow
        // can find the final turn and then glide down to rest at the composer.
        var working = vm.IsBusy || vm.IsStreaming;
        if (!working)
            _focusSettleUntil = DateTime.UtcNow + TimeSpan.FromMilliseconds(1600);
        EnsureFocusFollow();
    }

    /// <summary>
    /// Aims <see cref="StrataPresence.FocusPoint"/> at whatever currently deserves attention: on a new
    /// chat the middle of the hero, near the suggestion chips; while Lumi works, the live/last message
    /// as it grows; and when idle in an existing chat, the composer — illuminating up from where the
    /// user types. The new→existing and busy→idle changes therefore read as the light gliding *down*
    /// into the conversation. (The companion pool, not this focal point, leans into an open island.)
    /// </summary>
    private void UpdateFocusTarget()
    {
        if (_vm is not { } vm)
            return;

        var working = vm.IsBusy || vm.IsStreaming || _attentionPending;

        double x = 0.5, y = 0.5;
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
            }
            else if (suggestions is { } s2) { x = s2.X; y = s2.Y; }
            else if (orb is { } o2) { x = o2.X; y = o2.Y; }
            else { x = 0.5; y = 0.5; }
            y = Math.Clamp(y, 0.30, 0.74);
        }
        else if (working && TryGetActiveTurnFocus() is { } active)
        {
            x = active.X;
            y = active.Y;
        }
        else if (!working && TryGetControlFocus("ComposerContainer", 0.28, 0.16, 0.86) is { } composer)
        {
            // Idle in an existing chat: settle low, at the composer, illuminating upward.
            x = composer.X;
            y = composer.Y;
        }
        else
        {
            // No realized anchor yet — sit up-centre while working (lifted toward the conversation),
            // and low near the composer when idle.
            x = 0.5;
            y = working ? 0.44 : 0.80;
        }

        var target = new Point(Math.Clamp(x, 0.0, 1.0), Math.Clamp(y, 0.0, 1.0));
        var cur = _presence.FocusPoint;
        // Dedup micro-moves so the follow timer never restarts the glide in place. While working we
        // use a tighter threshold so the gaze visibly tracks the live answer as it grows.
        var dedup = working ? 0.006 : 0.014;
        if (Math.Abs(target.X - cur.X) + Math.Abs(target.Y - cur.Y) < dedup)
            return;
        _presence.FocusPoint = target;
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

    // ── Focus-target lookups in the host visual tree (no production hooks) ────────────

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
