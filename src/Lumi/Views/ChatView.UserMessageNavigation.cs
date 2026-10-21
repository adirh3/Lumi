using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>
/// Ctrl+↑/↓ (⌥⌘↑/↓ on macOS) jumps the transcript to the previous/next user message, landing it just
/// below the header and briefly glowing it. It applies when nothing is focused, from the transcript,
/// or from an empty composer (matching plain ↑/↓ scrolling the transcript from an empty composer);
/// other focused controls and typing keep the keys.
/// </summary>
public partial class ChatView
{
    private const double UserMessageLandingInset = 64;

    private TopLevel? _userNavKeySource;
    private TranscriptTurn? _userNavTarget;
    private double _userNavLandingOffset = double.NaN;
    private bool _userNavInFlight;
    private int _userNavVersion;

    /// <summary>Returns -1/+1 for the previous/next user message shortcut, or 0 when the key is not one.</summary>
    internal static int GetUserMessageNavigationDirection(Key key, KeyModifiers modifiers, bool isMac)
    {
        var expected = isMac ? KeyModifiers.Meta | KeyModifiers.Alt : KeyModifiers.Control;
        if (modifiers != expected)
            return 0;

        return key switch
        {
            Key.Up => -1,
            Key.Down => 1,
            _ => 0,
        };
    }

    /// <summary>
    /// Picks the user turn to land on. <paramref name="anchorIndex"/> is the turn at the landing line;
    /// when its top is already above that line the reader is partway through it, so "previous" first
    /// returns to that turn's own start before stepping further back. Returns -1 when there is none.
    /// </summary>
    internal static int FindUserMessageTarget(
        IReadOnlyList<bool> isUserTurn,
        int anchorIndex,
        bool anchorStartsAboveLandingLine,
        int direction)
    {
        if (isUserTurn.Count == 0 || direction == 0)
            return -1;

        anchorIndex = Math.Clamp(anchorIndex, 0, isUserTurn.Count - 1);
        if (direction < 0)
        {
            for (var i = anchorStartsAboveLandingLine ? anchorIndex : anchorIndex - 1; i >= 0; i--)
            {
                if (isUserTurn[i])
                    return i;
            }

            return -1;
        }

        for (var i = anchorIndex + 1; i < isUserTurn.Count; i++)
        {
            if (isUserTurn[i])
                return i;
        }

        return -1;
    }

    private static bool IsUserTurn(TranscriptTurn turn) => turn.Items.Any(static item => item is UserMessageItem);

    private void ResetUserMessageNavigation()
    {
        _userNavVersion++;
        _userNavTarget = null;
        _userNavLandingOffset = double.NaN;
        _userNavInFlight = false;
    }

    private void AttachUserMessageNavigation(TopLevel? topLevel)
    {
        if (ReferenceEquals(_userNavKeySource, topLevel))
            return;

        _userNavKeySource?.RemoveHandler(KeyDownEvent, OnUserMessageNavigationKeyDown);
        _userNavKeySource = topLevel;
        // On the window, not this view: when nothing is focused the key is raised on the window
        // itself and would never reach the chat view.
        _userNavKeySource?.AddHandler(KeyDownEvent, OnUserMessageNavigationKeyDown, RoutingStrategies.Tunnel);
    }

    private void OnUserMessageNavigationKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || !IsEffectivelyVisible)
            return;

        var direction = GetUserMessageNavigationDirection(e.Key, e.KeyModifiers, OperatingSystem.IsMacOS());
        if (direction == 0 || !CanNavigateUserMessagesFrom(e.Source))
            return;

        e.Handled = true;
        _ = NavigateUserMessageAsync(direction);
    }

    /// <summary>
    /// The shortcut only applies where the reader is plainly in this chat: a window with nothing
    /// focused, a non-text control inside this transcript, or this view's empty composer. Any other
    /// focused control (sidebar, header, workspace panel, typing in a text box) keeps its own
    /// Ctrl+↑/↓ handling, since this handler tunnels from the window and would otherwise run first.
    /// </summary>
    private bool CanNavigateUserMessagesFrom(object? source)
    {
        if (source is null || ReferenceEquals(source, _userNavKeySource))
            return true;

        if (source is not Visual visual)
            return false;

        var textBox = visual as TextBox ?? visual.FindAncestorOfType<TextBox>();
        if (textBox is not null)
        {
            return textBox.Name == "PART_Input"
                && ReferenceEquals(textBox.FindAncestorOfType<StrataChatComposer>(), _composer)
                && string.IsNullOrEmpty(textBox.Text);
        }

        return _transcriptScrollViewer is { } transcript
            && (ReferenceEquals(transcript, visual) || transcript.IsVisualAncestorOf(visual));
    }

    private async Task NavigateUserMessageAsync(int direction)
    {
        if (_subscribedVm is not { } vm || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var turns = vm.TranscriptTurns;
        if (!TryResolveUserNavigationAnchor(turns, out var anchorIndex, out var anchorStartsAbove))
            return;

        var isUserTurn = turns.Select(IsUserTurn).ToArray();
        var targetIndex = FindUserMessageTarget(isUserTurn, anchorIndex, anchorStartsAbove, direction);
        var version = ++_userNavVersion;
        if (targetIndex < 0)
        {
            _userNavTarget = null;
            _userNavInFlight = false;
            if (direction > 0 && !_chatShell.IsFollowingTail)
                JumpToLatest(focusComposer: false);
            return;
        }

        var turn = turns[targetIndex];
        _userNavTarget = turn;
        _userNavInFlight = true;
        try
        {
            var control = await EnsureTurnRealizedAsync(turn, settleContent: false);
            if (version != _userNavVersion || control is null || _chatShell is null || _transcriptScrollViewer is null)
                return;

            var message = control.GetVisualDescendants().OfType<StrataChatMessage>().FirstOrDefault();
            Control landing = message is not null ? message : control;
            // Glow in the same frame as the first scroll, not after the corrections below.
            if (message is not null)
                FlashMessage(message);

            // Realizing the target and its neighbours re-measures them, shifting the target after the
            // first scroll; re-measure and correct until it settles on the landing line.
            _chatShell.PreserveViewport();
            for (var attempt = 0; attempt < 4; attempt++)
            {
                if (landing.TranslatePoint(default, _transcriptScrollViewer) is not { } point)
                    return;

                var before = _chatShell.VerticalOffset;
                var desired = Math.Max(0, before + point.Y - UserMessageLandingInset);
                if (Math.Abs(desired - before) < 1)
                    break;

                _chatShell.ScrollToVerticalOffset(desired);
                if (Math.Abs(_chatShell.VerticalOffset - before) < 1)
                    break; // clamped at an edge

                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => { }, Avalonia.Threading.DispatcherPriority.Loaded);
                if (version != _userNavVersion)
                    return;
            }

            _userNavLandingOffset = _chatShell.VerticalOffset;
        }
        finally
        {
            if (version == _userNavVersion)
                _userNavInFlight = false;
        }
    }

    /// <summary>
    /// Finds where the reader is. Repeated presses continue from the last target while the viewport
    /// is still where that jump left it (or the jump is still realizing), so landings clamped at the
    /// top or bottom of the transcript never trap the reader on the same message.
    /// </summary>
    private bool TryResolveUserNavigationAnchor(IList<TranscriptTurn> turns, out int anchorIndex, out bool anchorStartsAbove)
    {
        anchorIndex = -1;
        anchorStartsAbove = false;
        if (_chatShell is null || _transcriptScrollViewer is null || turns.Count == 0)
            return false;

        if (_userNavTarget is { } last
            && (_userNavInFlight || Math.Abs(_chatShell.VerticalOffset - _userNavLandingOffset) < 1))
        {
            var lastIndex = turns.IndexOf(last);
            if (lastIndex >= 0)
            {
                anchorIndex = lastIndex;
                return true;
            }
        }

        // The realized panel's child order is not guaranteed to be index order, so take the
        // top-most turn still crossing the landing line.
        TranscriptTurnControl? anchor = null;
        var anchorTop = double.PositiveInfinity;
        foreach (var control in EnumerateRealizedTurnControls())
        {
            if (control.Turn is null || control.TranslatePoint(default, _transcriptScrollViewer) is not { } point)
                continue;

            if (point.Y + control.Bounds.Height <= UserMessageLandingInset || point.Y >= anchorTop)
                continue;

            anchor = control;
            anchorTop = point.Y;
        }

        if (anchor?.Turn is null)
            return false;

        anchorIndex = turns.IndexOf(anchor.Turn);
        anchorStartsAbove = anchorTop < UserMessageLandingInset - 1;
        return anchorIndex >= 0;
    }
}
