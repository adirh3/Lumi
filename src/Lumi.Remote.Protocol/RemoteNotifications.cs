namespace Lumi.Remote.Protocol;

/// <summary>
/// One phone notification: a finished reply, or something Lumi needs from the user. The desktop sends
/// it only while the user is away from the PC, and takes it down again once it has been dealt with.
/// </summary>
public sealed class RemoteNotification
{
    /// <summary>Stable identity. A later notification with the same id replaces the earlier one.</summary>
    public string Id { get; set; } = "";

    /// <summary>One of <see cref="RemoteProtocol.NotificationKinds"/>.</summary>
    public string Kind { get; set; } = RemoteProtocol.NotificationKinds.Reply;

    public Guid ChatId { get; set; }

    public string ChatTitle { get; set; } = "";

    /// <summary>Who speaks in the notification: the chat's agent, otherwise Lumi.</summary>
    public string? AgentName { get; set; }

    /// <summary>Plain text: the reply's opening, the question, the form's title or what needs approval.</summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// For a finished reply, what the user will most likely send next (Lumi's follow-up suggestions).
    /// The phone offers them in the notification and sends the one picked to the chat.
    /// </summary>
    public List<string> Suggestions { get; set; } = [];

    /// <summary>
    /// When the PC raised it. A chat's next reply keeps the id of the one before, so this tells a new
    /// notification from the same one sent again.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Every notification still waiting for the user. Sent first on each notification stream.</summary>
public sealed class RemoteNotificationSync
{
    /// <summary>
    /// The desktop run that holds these notifications. A phone takes down notifications this run
    /// no longer lists; notifications from an earlier run, which the desktop forgot when it restarted,
    /// stay until the user deals with them.
    /// </summary>
    public string Epoch { get; set; } = "";

    public List<RemoteNotification> Notifications { get; set; } = [];
}

/// <summary>Notifications the user dealt with somewhere else, which the phone takes down.</summary>
public sealed class RemoteNotificationDismissal
{
    public List<string> Ids { get; set; } = [];
}
