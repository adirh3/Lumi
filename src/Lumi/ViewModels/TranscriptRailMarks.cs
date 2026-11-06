using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Storage;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// What a transcript rail mark lands: a turn's user message (no item), or the item that shows a receipt,
/// question, form or approval. While its exchange is only in storage, the turn is the exchange's placeholder.
/// </summary>
internal sealed record TranscriptRailTarget(TranscriptTurn Turn, string? ItemId);

/// <summary>
/// What the transcript rail shows: the user's messages and receipts of the forms they submitted, previewed
/// with how they begin and when they were sent, and the cards that asked the user for something — Lumi's
/// questions, its forms and choices, and approvals — previewed with what they asked and how the user answered.
/// </summary>
internal static class TranscriptRailMarks
{
    /// <summary>The longest preview: about the three lines the rail's card shows.</summary>
    internal const int PreviewLength = 160;

    /// <summary>The longest answer quoted under a card's preview.</summary>
    internal const int AnswerLength = 60;

    internal readonly record struct UserMessageMark(string Text, DateTimeOffset? SentAt);

    /// <summary>
    /// A place on the rail besides a user's message: a receipt, a question, a form or an approval.
    /// <paramref name="Status"/> is how the user answered; a receipt says when it was sent instead.
    /// </summary>
    internal sealed record InputMark(
        StrataTranscriptMarkKind Kind,
        string? Label,
        string Text,
        string? Status,
        DateTimeOffset? SentAt = null)
    {
        /// <summary>The preview's quieter line: how it was answered, or when it was sent.</summary>
        public string? Detail(DateTimeOffset now) => Status ?? FormatSentAt(SentAt, now);
    }

    /// <summary>An input mark of an older exchange that is only in storage: where it is, and the item that shows it once built.</summary>
    internal sealed record StoredInputMark(long Seq, string ItemId, InputMark Mark);

    /// <summary>The user's message a turn shows or stands in for, or null for any other turn.</summary>
    internal static UserMessageMark? Describe(TranscriptTurn turn)
    {
        foreach (var item in turn.Items)
        {
            if (item is UserMessageItem user)
            {
                var files = string.IsNullOrWhiteSpace(user.Content)
                    ? string.Join(", ", user.Attachments.Select(static attachment => attachment.FileName))
                    : null;
                return new UserMessageMark(Preview(user.Content, files), user.Message.Timestamp);
            }
        }

        return ChatViewModel.StandsInForUserMessage(turn) && turn.HistorySegment is TranscriptHistorySegment segment
            ? new UserMessageMark(Preview(segment.FirstMessageStart), segment.FirstMessageAt)
            : null;
    }

    /// <summary>The input mark of a transcript item, or null for an item that is not one.</summary>
    internal static InputMark? Describe(TranscriptItem item) => item switch
    {
        InteractionReceiptItem receipt => Receipt(receipt.Receipt.Title, receipt.Receipt.Summary, receipt.SentAt),
        QuestionItem question => Question(question.Question,
            question.IsAnswered ? question.SelectedAnswer : null, question.IsExpired),
        InputRequestItem input => Input(input.Definition, input.InputStatus),
        ApprovalItem approval => new InputMark(
            StrataTranscriptMarkKind.Card,
            Loc.TranscriptRail_Approval,
            Preview(string.IsNullOrWhiteSpace(approval.Request.Subject)
                ? approval.Request.Title
                : $"{approval.Request.Title} · {approval.Request.Subject}"),
            approval.Request.ReceiptText ?? (approval.Request.IsPending ? Loc.TranscriptRail_Waiting : null)),
        _ => null,
    };

    /// <summary>
    /// The input mark a stored message shows as once built (see <see cref="TranscriptBuilder"/>), with the
    /// id of the item that shows it, or null when it shows as none.
    /// </summary>
    internal static StoredInputMark? Describe(ChatMessage message)
    {
        if (message.Role == "user" && message.InteractionReceipt is { } receipt)
        {
            return new StoredInputMark(message.StorageSeq, InteractionReceiptItem.StableIdFor(message.Id),
                Receipt(receipt.Title, receipt.Summary, message.Timestamp));
        }

        if (message.ToolName == "ask_question")
        {
            var question = TranscriptBuilder.ReadStoredQuestion(message);
            return new StoredInputMark(message.StorageSeq, QuestionItem.StableIdFor(question.Id),
                Question(question.Question, question.IsAnswered ? question.Answer : null, question.IsExpired));
        }

        if (message.ToolName == "ask_input" && message.InputKind is { } kind && message.InputDefinition is { } json)
        {
            InteractiveDefinition definition;
            try
            {
                definition = InteractiveDefinition.Parse(kind, json);
            }
            catch (Exception ex) when (ex is FormatException or System.Text.Json.JsonException)
            {
                return null;
            }

            return new StoredInputMark(message.StorageSeq, InputRequestItem.StableIdFor(message.Id),
                Input(definition, InputRequestItem.StatusOf(message)));
        }

        return null;
    }

    /// <summary>The input mark of a stored exchange that starts with a form's receipt, read from its outline.</summary>
    internal static StoredInputMark? Describe(ChatOutlineEntry entry)
        => entry.IsReceipt
            ? new StoredInputMark(entry.Seq, InteractionReceiptItem.StableIdFor(entry.Id),
                Receipt(entry.ReceiptTitle ?? "", entry.ReceiptSummary ?? "", entry.Timestamp))
            : null;

    // A receipt is the user's own: it reads as their bubble, captioned with what they did.
    private static InputMark Receipt(string title, string summary, DateTimeOffset? sentAt)
        => string.IsNullOrWhiteSpace(summary)
            ? new InputMark(StrataTranscriptMarkKind.Message, null, Preview(title), null, sentAt)
            : new InputMark(StrataTranscriptMarkKind.Message, title, Preview(summary), null, sentAt);

    private static InputMark Question(string question, string? answer, bool expired)
        => new(StrataTranscriptMarkKind.Card, Loc.TranscriptRail_Question, Preview(question),
            !string.IsNullOrWhiteSpace(answer) ? Loc.Get("TranscriptRail_Answered", Preview(answer, length: AnswerLength))
            : expired ? Loc.TranscriptRail_NotAnswered
            : Loc.TranscriptRail_Waiting);

    private static InputMark Input(InteractiveDefinition definition, string status)
        => new(StrataTranscriptMarkKind.Card,
            definition is InteractiveResults ? Loc.TranscriptRail_Choice : Loc.TranscriptRail_Form,
            Preview(definition.Title),
            status switch
            {
                "Submitted" => Loc.TranscriptRail_Submitted,
                "Canceled" => Loc.TranscriptRail_Canceled,
                "Waiting" => Loc.TranscriptRail_Waiting,
                _ => Loc.TranscriptRail_NotAnswered,
            });

    /// <summary>
    /// A message's text on one flowing line (the card wraps it), cut at <paramref name="length"/>
    /// (<see cref="PreviewLength"/>). A message sent without text reads as its files, or as files when
    /// only the stand-in for it is known.
    /// </summary>
    internal static string Preview(string? text, string? files = null, int length = PreviewLength)
    {
        var preview = new StringBuilder(Math.Min(text?.Length ?? 0, length + 1));
        var pendingSpace = false;
        foreach (var character in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = preview.Length > 0;
                continue;
            }

            if (pendingSpace)
                preview.Append(' ');
            pendingSpace = false;
            preview.Append(character);
            if (preview.Length > length)
                break;
        }

        if (preview.Length > length)
        {
            var cut = char.IsHighSurrogate(preview[length - 1]) ? length - 1 : length;
            return preview.ToString(0, cut).TrimEnd() + "…";
        }

        if (preview.Length > 0)
            return preview.ToString();

        return string.IsNullOrWhiteSpace(files) ? Loc.TranscriptRail_Attachments : files;
    }

    /// <summary>When a message was sent, as briefly as its distance from now allows.</summary>
    internal static string? FormatSentAt(DateTimeOffset? sentAt, DateTimeOffset now, CultureInfo? culture = null)
    {
        if (sentAt is not { } value)
            return null;

        culture ??= CultureInfo.CurrentCulture;
        var local = value.ToLocalTime();
        var today = now.ToLocalTime().Date;
        var time = local.ToString("t", culture);
        var day = local.Date;
        if (day == today)
            return time;
        if (day == today.AddDays(-1))
            return $"{Loc.ChatGroup_Yesterday} {time}";
        if (day > today.AddDays(-7) && day < today)
            return $"{local.ToString("dddd", culture)} {time}";
        if (day.Year == today.Year)
            return $"{local.ToString(culture.DateTimeFormat.MonthDayPattern, culture)}, {time}";
        return $"{local.ToString("d", culture)}, {time}";
    }
}
