using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Lumi.Localization;

namespace Lumi.ViewModels;

/// <summary>
/// What the transcript rail shows for the user's messages: each user turn (or the stand-in for an older
/// exchange that starts with one) becomes a mark, previewed with how the message begins and when it was sent.
/// </summary>
internal static class TranscriptRailMarks
{
    /// <summary>The longest preview: about the three lines the rail's card shows.</summary>
    internal const int PreviewLength = 160;

    internal readonly record struct UserMessageMark(string Text, DateTimeOffset? SentAt);

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

    /// <summary>
    /// A message's text on one flowing line (the card wraps it), cut at <see cref="PreviewLength"/>. A message
    /// sent without text reads as its files, or as files when only the stand-in for it is known.
    /// </summary>
    internal static string Preview(string? text, string? files = null)
    {
        var preview = new StringBuilder(Math.Min(text?.Length ?? 0, PreviewLength + 1));
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
            if (preview.Length > PreviewLength)
                break;
        }

        if (preview.Length > PreviewLength)
        {
            var length = char.IsHighSurrogate(preview[PreviewLength - 1]) ? PreviewLength - 1 : PreviewLength;
            return preview.ToString(0, length).TrimEnd() + "…";
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
