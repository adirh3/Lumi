using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// A chat's identity in the floating header: its agent's glyph, or its project's initial, on the
/// project's hue. Value equality keeps refreshes that change nothing from repainting the mark.
/// </summary>
public sealed record ChatHeaderMark(string Glyph, Color Hue)
{
    private static readonly Color AgentOnlyHue = Color.Parse("#9CA3AF");

    public IBrush Background => new SolidColorBrush(Color.FromArgb(0x2E, Hue.R, Hue.G, Hue.B)).ToImmutable();

    public IBrush Foreground => new SolidColorBrush(Hue).ToImmutable();

    /// <summary>Null when the chat has neither a project nor an agent: the title stands on its own.</summary>
    internal static ChatHeaderMark? Create(Project? project, string? agentName, string? agentGlyph)
    {
        var hasAgent = !string.IsNullOrWhiteSpace(agentName);
        if (project is null && !hasAgent)
            return null;

        var glyph = hasAgent && !string.IsNullOrWhiteSpace(agentGlyph)
            ? agentGlyph!
            : LaunchpadProjectBadge.InitialOf(project?.Name ?? agentName!);
        var hue = project is null ? AgentOnlyHue : LaunchpadProjectBadge.HueFor(project.Id);
        return new ChatHeaderMark(glyph, hue);
    }
}

/// <summary>
/// The floating chat header: who the chat is, what Lumi is doing right now and which agents are
/// running. Everything here is derived from state the chat already owns.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>The chat's identity mark, or null when it has no project or agent.</summary>
    [ObservableProperty] private ChatHeaderMark? _headerMark;

    /// <summary>Up to three running sub-agents, for the header's avatar stack.</summary>
    [ObservableProperty] private IReadOnlyList<SubagentToolCallItem> _headerRunningAgents = [];

    /// <summary>
    /// What Lumi is doing right now, for the header pill that keeps the live step in view while the
    /// transcript is scrolled away from it. Falls back to the turn's status, then to "Working…".
    /// </summary>
    public string HeaderActivityText
    {
        get
        {
            var live = _transcriptBuilder.LiveActivityLabel;
            if (!string.IsNullOrWhiteSpace(live))
                return live;

            return string.IsNullOrWhiteSpace(StatusText) ? Loc.ToolGroup_Working : StatusText;
        }
    }

    /// <summary>"2 agents working": the header's summary of running sub-agents.</summary>
    public string HeaderAgentsText => RunningSubagentCount == 1
        ? Loc.ChatHeader_AgentWorking
        : string.Format(Loc.Culture, Loc.ChatHeader_AgentsWorking, RunningSubagentCount);

    /// <summary>The model chip in the title menu.</summary>
    public string? HeaderModelText => FormatModelDisplay(SelectedModel);

    /// <summary>
    /// When the chat started, how long it is and what it produced: the title menu's quiet last line.
    /// The menu asks again each time it opens, so "today" stays true.
    /// </summary>
    public string HeaderDetailsText
    {
        get
        {
            if (CurrentChat is not { } chat)
                return "";

            var parts = new List<string>(3) { FormatHeaderStarted(chat.CreatedAt, DateTimeOffset.Now) };
            var messages = chat.Messages.Count(static message =>
                message.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(message.Content));
            if (messages > 0)
                parts.Add(messages == 1
                    ? Loc.ChatHeader_OneMessage
                    : string.Format(Loc.Culture, Loc.ChatHeader_Messages, messages));

            var files = WorkspaceFiles.TotalCount;
            if (files > 0)
                parts.Add(files == 1
                    ? Loc.ChatHeader_OneFile
                    : string.Format(Loc.Culture, Loc.ChatHeader_Files, files));

            return string.Join(" · ", parts);
        }
    }

    /// <summary>
    /// The header lights the one tool whose page the Workspace shows: the Browser and the Desktop have
    /// buttons of their own, and every other page belongs to the Workspace's.
    /// </summary>
    public bool IsHeaderWorkspaceActive => IsWorkspacePanelOpen
        && WorkspacePage is not (WorkspacePage.Browser or WorkspacePage.Desktop);

    /// <inheritdoc cref="IsHeaderWorkspaceActive"/>
    public bool IsHeaderDesktopActive => IsWorkspacePanelOpen && WorkspacePage == WorkspacePage.Desktop;

    /// <summary>"Started today 10:58", "Started yesterday 18:20", then just the day.</summary>
    internal static string FormatHeaderStarted(DateTimeOffset startedAt, DateTimeOffset now)
    {
        var local = startedAt.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today)
            return string.Format(Loc.Culture, Loc.ChatHeader_StartedToday, local.ToString("HH:mm", Loc.Culture));
        if (local.Date == today.AddDays(-1))
            return string.Format(Loc.Culture, Loc.ChatHeader_StartedYesterday, local.ToString("HH:mm", Loc.Culture));

        return string.Format(Loc.Culture, Loc.ChatHeader_StartedOn,
            local.ToString(local.Year == today.Year ? "MMM d" : "MMM d, yyyy", Loc.Culture));
    }

    internal void RefreshHeaderDetails() => OnPropertyChanged(nameof(HeaderDetailsText));

    private void RefreshHeaderMark()
    {
        var agentGlyph = SelectedSdkAgentName is not null ? ExternalAgentGlyph : ActiveAgent?.IconGlyph;
        HeaderMark = ChatHeaderMark.Create(GetCurrentProject(), AgentBadgeText, agentGlyph);
    }

    private void RefreshHeaderRunningAgents(IReadOnlyList<SubagentToolCallItem> ordered)
    {
        var running = new List<SubagentToolCallItem>(3);
        foreach (var run in ordered)
        {
            if (!run.IsInProgress)
                continue;

            running.Add(run);
            if (running.Count == 3)
                break;
        }

        if (!running.SequenceEqual(HeaderRunningAgents))
            HeaderRunningAgents = running;
    }

    private void OnTranscriptLiveActivityChanged() => OnPropertyChanged(nameof(HeaderActivityText));
}
