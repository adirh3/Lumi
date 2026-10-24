using System;
using Avalonia;
using Avalonia.Controls;
using Lumi.ViewModels;
using StrataTheme.Controls;

namespace Lumi.Views;

/// <summary>
/// The transcript rail (<see cref="StrataTranscriptRail"/>) in place of the scrollbar: hovered, it maps the
/// user's messages as marks at their turns' places in the transcript, previews the nearest one (how it
/// begins and when it was sent), and lands the one picked as Ctrl+↑/↓ would, building an older exchange
/// that is only in storage first. Marks are measured only while the rail is open.
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
            if (TranscriptRailMarks.Describe(turn) is not { } message
                || _transcript.ContainerFromIndex(index) is not Control container
                || !container.IsVisible)
            {
                continue;
            }

            e.Marks.Add(new StrataTranscriptMark(
                panelTop + container.Bounds.Y,
                message.Text,
                TranscriptRailMarks.FormatSentAt(message.SentAt, now),
                turn));
        }
    }

    private void OnTranscriptMarkInvoked(object? sender, StrataTranscriptMarkInvokedEventArgs e)
    {
        if (e.Mark.Tag is not TranscriptTurn turn)
            return;

        e.Handled = true;
        _ = JumpToUserTurnAsync(turn);
    }
}
