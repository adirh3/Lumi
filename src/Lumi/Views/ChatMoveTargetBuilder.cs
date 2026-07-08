using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Views;

/// <summary>Which kind of destination a "Move to Project" entry represents.</summary>
internal enum ChatMoveTargetKind
{
    /// <summary>Moves the chat out of any project (the unfiled pool shown under "All projects").</summary>
    AllProjects,

    /// <summary>Moves the chat into a specific project.</summary>
    Project,
}

/// <summary>A single destination in a chat's "Move to Project" submenu.</summary>
/// <param name="Kind">Whether this entry is the "All projects" pool or a concrete project.</param>
/// <param name="Header">Display text for the menu item.</param>
/// <param name="ProjectId">The target project id, or <c>null</c> for the "All projects" pool.</param>
/// <param name="IsCurrent">True when this is the chat's current home (rendered as a disabled checkmark).</param>
internal sealed record ChatMoveTarget(
    ChatMoveTargetKind Kind,
    string Header,
    Guid? ProjectId,
    bool IsCurrent);

/// <summary>
/// Pure logic for a chat's "Move to Project" submenu. Kept UI-free so it can be unit tested:
/// the code-behind materializes these descriptors into <c>MenuItem</c>s.
/// </summary>
internal static class ChatMoveTargetBuilder
{
    /// <summary>
    /// Builds the ordered move destinations for a chat: an "All projects" entry first (moves the
    /// chat out of any project), followed by every project in the supplied order. The destination
    /// matching the chat's current home is flagged <see cref="ChatMoveTarget.IsCurrent"/> so the
    /// caller can render it as a disabled checkmark instead of an actionable item.
    /// </summary>
    public static IReadOnlyList<ChatMoveTarget> Build(
        IReadOnlyList<Project> projects, Chat chat, string allProjectsHeader)
    {
        ArgumentNullException.ThrowIfNull(projects);
        ArgumentNullException.ThrowIfNull(chat);

        var targets = new List<ChatMoveTarget>(projects.Count + 1)
        {
            new(ChatMoveTargetKind.AllProjects, allProjectsHeader, null, chat.ProjectId is null),
        };

        foreach (var project in projects)
        {
            targets.Add(new ChatMoveTarget(
                ChatMoveTargetKind.Project,
                project.Name,
                project.Id,
                chat.ProjectId == project.Id));
        }

        return targets;
    }
}
