using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>
/// The transcript rail (<see cref="StrataTranscriptRail"/>) in place of the scrollbar: hovered, it maps the
/// user's messages and receipts, and the cards that asked them for something (questions, forms, approvals),
/// as marks at their places in the transcript, previews the nearest one, and lands the one picked as
/// Ctrl+↑/↓ would, building an older exchange that is only in storage first. Marks are measured only while
/// the rail is open.
/// </summary>
public partial class ChatView
{
    private void AttachTranscriptRail()
    {
        if (_chatShell is null)
            return;

        _chatShell.TranscriptMarksRequested -= OnTranscriptMarksRequested;
        _chatShell.TranscriptMarksRequested += OnTranscriptMarksRequested;
        _chatShell.TranscriptMarkInvoked -= OnTranscriptMarkInvoked;
        _chatShell.TranscriptMarkInvoked += OnTranscriptMarkInvoked;
    }

    private void DetachTranscriptRail()
    {
        if (_chatShell is null)
            return;

        _chatShell.TranscriptMarksRequested -= OnTranscriptMarksRequested;
        _chatShell.TranscriptMarkInvoked -= OnTranscriptMarkInvoked;
    }

    private void InvalidateTranscriptRailMarks() => _chatShell?.InvalidateTranscriptMarks();

    private void OnTranscriptMarksRequested(object? sender, StrataTranscriptMarksRequestedEventArgs e)
    {
        if (_subscribedVm is not { } vm
            || _transcript?.ItemsPanelRoot is not { } panel
            || _transcriptScrollViewer is not { } viewer
            || panel.TranslatePoint(default, viewer) is not { } panelOrigin)
        {
            return;
        }

        // Every turn has a container laid out in the items panel (membership is stable), placeholders of
        // older exchanges included, so a turn's place is its container's top below the panel's.
        var panelTop = panelOrigin.Y + viewer.Offset.Y;
        var now = DateTimeOffset.Now;
        var turns = vm.MountedTranscriptTurns;
        for (var index = 0; index < turns.Count; index++)
        {
            var turn = turns[index];
            if (_transcript.ContainerFromIndex(index) is not Control container || !container.IsVisible)
                continue;

            var top = panelTop + container.Bounds.Y;
            if (TranscriptRailMarks.Describe(turn) is { } message)
            {
                e.Marks.Add(new StrataTranscriptMark(
                    top,
                    message.Text,
                    TranscriptRailMarks.FormatSentAt(message.SentAt, now),
                    new TranscriptRailTarget(turn, null)));
            }

            if (TranscriptHistory.IsPlaceholder(turn))
                AddStoredInputMarks(e.Marks, turn, top, container.Bounds.Height, now);
            else
                AddInputMarks(e.Marks, turn, container, top, now);
        }
    }

    /// <summary>
    /// The inputs of an exchange only its placeholder shows: their places are not known until it is built,
    /// so each is put as far into the placeholder as it is into the exchange's stored messages.
    /// </summary>
    private static void AddStoredInputMarks(
        IList<StrataTranscriptMark> marks, TranscriptTurn placeholder, double top, double height, DateTimeOffset now)
    {
        if (placeholder.HistorySegment is not TranscriptHistorySegment { RailInputs: { Count: > 0 } inputs } segment)
            return;

        var startsWithChat = segment.StartSeq == TranscriptHistory.ChatStartSeq;
        double span = Math.Max(1, segment.EndSeq - segment.StartSeq);
        for (var i = 0; i < inputs.Count; i++)
        {
            var input = inputs[i];
            var share = startsWithChat ? (i + 1d) / (inputs.Count + 1) : (input.Seq - segment.StartSeq) / span;
            marks.Add(CreateInputMark(input.Mark, top + height * Math.Clamp(share, 0, 0.95), new TranscriptRailTarget(placeholder, input.ItemId), now));
        }
    }

    /// <summary>
    /// The inputs of a built turn, among its items and the steps its "worked for" rows fold, where they show:
    /// exactly while the turn is realized, otherwise as far into the turn as they are among its items. A
    /// folded one is put at the row that folds it while the row is closed.
    /// </summary>
    private static void AddInputMarks(
        IList<StrataTranscriptMark> marks, TranscriptTurn turn, Control container, double top, DateTimeOffset now)
    {
        var items = turn.Items;
        var host = container is ContentPresenter { Child: TranscriptTurnControl { Content: StackPanel shown } }
                   && ReferenceEquals(shown, turn.RealizedItemsHost)
            ? shown
            : null;
        var height = container.Bounds.Height;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (!item.IsItemVisible)
                continue;

            Control? view = null;
            var offset = height * i / items.Count;
            if (host is not null && i < host.Children.Count
                && ReferenceEquals(TranscriptTurnControl.GetHostedItem(host.Children[i]), item))
            {
                view = host.Children[i];
                offset = view.TranslatePoint(default, container)?.Y ?? offset;
            }

            var target = new TranscriptRailTarget(turn, item.StableId);
            if (TranscriptRailMarks.Describe(item) is { } input)
            {
                marks.Add(CreateInputMark(input, top + offset, target, now));
                continue;
            }

            if (item is not TurnSummaryItem summary)
                continue;

            if (summary.HasDeferredItems)
            {
                foreach (var (itemId, mark) in summary.DeferredRailInputs)
                    marks.Add(CreateInputMark(mark, top + offset, new TranscriptRailTarget(turn, itemId), now));
                continue;
            }

            Dictionary<TranscriptItem, Control>? innerViews = null;
            foreach (var inner in summary.InnerItems)
            {
                if (TranscriptRailMarks.Describe(inner) is not { } folded)
                    continue;

                var innerOffset = offset;
                if (summary.IsExpanded && view is not null)
                {
                    innerViews ??= MapItemViews(view);
                    // The open row scrolls its steps in a bounded area: a step scrolled out of it stays at its edge.
                    if (innerViews.TryGetValue(inner, out var innerView) && innerView.TranslatePoint(default, container) is { } point)
                        innerOffset = Math.Clamp(point.Y, offset, offset + view.Bounds.Height);
                }

                marks.Add(CreateInputMark(folded, top + innerOffset, new TranscriptRailTarget(turn, inner.StableId), now));
            }
        }
    }

    private static StrataTranscriptMark CreateInputMark(
        TranscriptRailMarks.InputMark input, double position, TranscriptRailTarget target, DateTimeOffset now)
        => new(position, input.Text, input.Detail(now), target, input.Kind, input.Label);

    private static Dictionary<TranscriptItem, Control> MapItemViews(Visual root)
    {
        var views = new Dictionary<TranscriptItem, Control>();
        foreach (var visual in root.GetVisualDescendants())
        {
            if (visual is not Control control)
                continue;
            var item = TranscriptTurnControl.GetHostedItem(control)
                ?? (control as ContentPresenter)?.Content as TranscriptItem;
            if (item is not null)
                views.TryAdd(item, control);
        }

        return views;
    }

    private void OnTranscriptMarkInvoked(object? sender, StrataTranscriptMarkInvokedEventArgs e)
    {
        if (e.Mark.Tag is not TranscriptRailTarget target)
            return;

        e.Handled = true;
        _ = target.ItemId is { } itemId ? JumpToRailInputAsync(target.Turn, itemId) : JumpToUserTurnAsync(target.Turn);
    }

    /// <summary>
    /// Lands a question, form, approval or receipt the reader picked on the rail as a user message lands:
    /// its exchange built from storage, and the "worked for" row that folds it opened, first.
    /// </summary>
    private async Task JumpToRailInputAsync(TranscriptTurn turn, string itemId)
    {
        if (_subscribedVm is not { } vm || _chatShell is null || _transcriptScrollViewer is null)
            return;

        var version = ++_userNavVersion;
        _userNavTarget = turn;
        _userNavInFlight = true;
        try
        {
            // An older exchange's closed rows keep their steps unbuilt: build the exchange with them first.
            IReadOnlyList<TranscriptTurn> turns = [turn];
            if (turn.HistorySegment is TranscriptHistorySegment segment)
            {
                if (await vm.BuildExchangeWithFoldedStepsAsync(segment) is not { } built || version != _userNavVersion)
                    return;
                turns = built;
            }

            if (FindRailInput(turns, itemId) is not { } found)
                return;

            var (owner, item) = found;
            _userNavTarget = owner;
            var revealed = owner.RevealFoldedItem(item);
            var control = await EnsureTurnRealizedAsync(owner);
            if (version != _userNavVersion || control is null)
                return;
            if (revealed)
            {
                await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                await RealizeFoldedItemAsync(control, item);
            }

            if (FindTranscriptItemView(control, item) is not { } view)
                return;
            await RevealWithinFoldAsync(view);
            if (version != _userNavVersion)
                return;

            FlashMessage(FlashTargetOf(view));
            await LandOnReadingLineAsync(vm, version, () =>
                FindRealizedTurnControl(owner.StableId) is { } realized
                && FindTranscriptItemView(realized, item) is { } current
                && _transcriptScrollViewer is { } scroller
                && current.TranslatePoint(default, scroller) is { } point
                    ? point.Y
                    : null);
        }
        finally
        {
            if (version == _userNavVersion)
                _userNavInFlight = false;
        }
    }

    /// <summary>The turn and item showing a rail input, or null when it is not among <paramref name="turns"/>.</summary>
    private static (TranscriptTurn Turn, TranscriptItem Item)? FindRailInput(IEnumerable<TranscriptTurn> turns, string itemId)
    {
        foreach (var turn in turns)
        {
            foreach (var item in turn.EnumerateItems())
            {
                if (item.StableId == itemId)
                    return (turn, item);
            }
        }

        return null;
    }

    /// <summary>
    /// The card an input's view shows, which glows when it is landed: the view itself, or what the presenter
    /// of a folded step shows.
    /// </summary>
    private static Control FlashTargetOf(Control view) => view is ContentPresenter { Child: { } card } ? card : view;
}
