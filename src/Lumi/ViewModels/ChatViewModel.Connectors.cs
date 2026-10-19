using System.Globalization;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// A new chat that asks Lumi to fix a connector: the visible request, plus the report attached to it
/// with the complete error and the connector's settings (credentials removed).
/// </summary>
public sealed record McpRepairRequest(
    Chat Chat,
    Guid? SourceChatId,
    string ConnectorName,
    string Message,
    string ReportFileName,
    string ReportContent);

/// <summary>Opens the place to find connectors (with what the user searched for) or to manage them.</summary>
public sealed record ConnectorsPageRequest(bool Discover, string? Query);

/// <summary>
/// Connectors in a chat: what the session reported about each one, how the composer shows it, the "Fix
/// with Lumi" hand-off, and the ways to find and manage connectors. A connector's issue lasts as long as
/// the session that reported it, so rebuilding the composer's chips for an unrelated reason never makes
/// a broken connector look healthy.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Issues reported by each chat's current session, keyed by connector name. UI thread only.</summary>
    private readonly Dictionary<Guid, Dictionary<string, McpConnectorIssue>> _mcpIssues = new();
    /// <summary>Raised when the user asks Lumi to fix a connector. The window that shows this chat opens the repair chat.</summary>
    public event Func<McpRepairRequest, Task>? McpRepairRequested;
    /// <summary>Raised to find new connectors in Discover or to manage them in Customize.</summary>
    public event Action<ConnectorsPageRequest>? ConnectorsPageRequested;

    /// <summary>True while connectors from plugins and Copilot are still being discovered for this chat.</summary>
    [ObservableProperty] private bool _isConnectorCatalogLoading;

    /// <summary>
    /// Tooltip and accessible description of the composer's connector button: how many connectors are on,
    /// how many need attention, or that there are none yet.
    /// </summary>
    public string ConnectorsButtonToolTip
    {
        get
        {
            var active = ActiveMcpChips.OfType<StrataComposerChip>().ToList();
            var problems = active.Count(chip => chip.HasError);
            if (problems > 0)
            {
                return problems == 1
                    ? Loc.Chat_ConnectorsNeedAttentionOne
                    : string.Format(CultureInfo.CurrentCulture, Loc.Chat_ConnectorsNeedAttentionMany, problems);
            }

            if (AvailableMcpChips.Count == 0)
                return Loc.Chat_ConnectorsTooltipEmpty;

            var on = AvailableMcpChips.Count(available => active.Any(chip => NameComparer.Equals(chip.Name, available.Name)));
            return string.Format(CultureInfo.CurrentCulture, Loc.Chat_ConnectorsTooltip, on, AvailableMcpChips.Count);
        }
    }

    /// <summary>Finds new connectors in Discover, searching for <paramref name="query"/> when there is one.</summary>
    [RelayCommand]
    private void FindConnectors(string? query)
        => ConnectorsPageRequested?.Invoke(new ConnectorsPageRequest(
            Discover: true,
            Query: string.IsNullOrWhiteSpace(query) ? null : query.Trim()));

    [RelayCommand]
    private void ManageConnectors() => ConnectorsPageRequested?.Invoke(new ConnectorsPageRequest(Discover: false, Query: null));

    /// <summary>Puts a connector's chip into its reported state: a problem, or a neutral status.</summary>
    private void SetMcpChipError(Guid chatId, string serverName, McpConnectorIssue issue)
        => RecordMcpIssue(chatId, ResolveMcpDisplayName(chatId, serverName), issue);

    /// <summary>The connector is working: it drops whatever it reported before.</summary>
    private void ClearMcpChipError(Guid chatId, string serverName)
        => RecordMcpIssue(chatId, ResolveMcpDisplayName(chatId, serverName), null);

    internal void RecordMcpIssue(Guid chatId, string connectorName, McpConnectorIssue? issue)
    {
        if (issue is null)
        {
            if (_mcpIssues.TryGetValue(chatId, out var existing))
                existing.Remove(connectorName);
        }
        else
        {
            if (!_mcpIssues.TryGetValue(chatId, out var issues))
                _mcpIssues[chatId] = issues = new Dictionary<string, McpConnectorIssue>(NameComparer);
            issues[connectorName] = issue;
        }

        if (CurrentChat?.Id == chatId)
            ApplyMcpIssuesToChips();
    }

    /// <summary>A new session reports every connector afresh.</summary>
    private void ResetMcpIssues(Guid chatId)
    {
        if (_mcpIssues.Remove(chatId) && CurrentChat?.Id == chatId)
            ApplyMcpIssuesToChips();
    }

    /// <summary>
    /// The chat's session is being rebuilt, so what its connectors reported no longer stands: each one is
    /// tried again with the next message. Returns whether anything changed.
    /// </summary>
    private bool MarkMcpIssuesRetrying(Guid chatId, string? connectorName = null)
    {
        if (!_mcpIssues.TryGetValue(chatId, out var issues))
            return false;

        var changed = false;
        foreach (var (name, issue) in issues.ToList())
        {
            if (issue.Kind == McpConnectorIssueKind.Retrying
                || (connectorName is not null && !NameComparer.Equals(name, connectorName)))
                continue;

            issues[name] = McpConnectorIssues.Retrying(issue);
            changed = true;
        }

        if (changed && CurrentChat?.Id == chatId)
            ApplyMcpIssuesToChips();
        return changed;
    }

    /// <summary>
    /// Called when a "Fix with Lumi" chat for this connector finishes a turn. The fix may have changed its
    /// settings or a file Lumi does not watch, so the next message rebuilds the session to pick it up.
    /// </summary>
    internal void RetryMcpConnectorAfterRepair(Guid chatId, string connectorName)
    {
        if (CurrentChat is not { } chat || chat.Id != chatId)
            return;

        if (MarkMcpIssuesRetrying(chatId, connectorName))
            InvalidateSessionConfiguration(chat);
    }

    /// <summary>The chip as the current chat's session last reported it.</summary>
    private StrataComposerChip WithMcpIssue(StrataComposerChip chip)
    {
        McpConnectorIssue? issue = null;
        if (CurrentChat is { } chat && _mcpIssues.TryGetValue(chat.Id, out var issues))
            issues.TryGetValue(chip.Name, out issue);

        return issue is null
            ? chip with { ErrorMessage = null, ErrorDetails = null, StatusMessage = null }
            : issue.IsProblem
                ? chip with
                {
                    ErrorMessage = issue.Summary,
                    ErrorDetails = string.IsNullOrWhiteSpace(issue.Details) ? null : issue.Details,
                    StatusMessage = null,
                }
                : chip with { ErrorMessage = null, ErrorDetails = null, StatusMessage = issue.Summary };
    }

    private void ApplyMcpIssuesToChips()
    {
        for (var i = 0; i < ActiveMcpChips.Count; i++)
        {
            if (ActiveMcpChips[i] is not StrataComposerChip chip)
                continue;

            var updated = WithMcpIssue(chip);
            if (updated != chip)
                ActiveMcpChips[i] = updated;
        }
    }

    /// <summary>A connector the user turns off stops counting, and starts afresh if turned back on.</summary>
    private void ForgetMcpIssues(IEnumerable<string>? connectorNames)
    {
        if (CurrentChat is not { } chat || !_mcpIssues.TryGetValue(chat.Id, out var issues))
            return;

        if (connectorNames is null)
        {
            issues.Clear();
            return;
        }

        foreach (var name in connectorNames)
            issues.Remove(name);
    }

    private void OnMcpIssuesChanged()
    {
        OnPropertyChanged(nameof(ConnectorsButtonToolTip));
        FixMcpConnectorCommand.NotifyCanExecuteChanged();
    }

    private bool CanFixMcpConnector(StrataComposerChip? chip)
        => McpRepairRequested is not null && FindCurrentMcpProblem(chip) is not null;

    /// <summary>Opens a new chat that asks Lumi to fix the connector. This chat, its draft and its work are left alone.</summary>
    [RelayCommand(CanExecute = nameof(CanFixMcpConnector))]
    private async Task FixMcpConnector(StrataComposerChip? chip)
    {
        var problem = FindCurrentMcpProblem(chip);
        var handlers = McpRepairRequested?.GetInvocationList().OfType<Func<McpRepairRequest, Task>>().ToArray() ?? [];
        var activeWindowContext = Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.FirstOrDefault(window => window.IsActive)?.DataContext
            : null;
        var handler = SelectMcpRepairHandler(handlers, activeWindowContext);
        if (problem is null || handler is null)
            return;

        try
        {
            await handler(BuildMcpRepairRequest(problem));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            System.Diagnostics.Trace.TraceError("Could not open a connector repair chat: {0}", ex);
            StatusText = Loc.Connector_RepairStartFailed;
        }
    }

    /// <summary>
    /// The live problem for a connector chip. A chip captured by an open list may be stale; only a
    /// connector that still reports a problem can be fixed.
    /// </summary>
    private StrataComposerChip? FindCurrentMcpProblem(StrataComposerChip? chip)
        => chip is null
            ? null
            : ActiveMcpChips.OfType<StrataComposerChip>()
                .FirstOrDefault(active => active.HasError && NameComparer.Equals(active.Name, chip.Name));

    /// <summary>
    /// A chat shown in two windows has a repair handler from each; start the repair once, in the window
    /// the user is working in.
    /// </summary>
    internal static Func<McpRepairRequest, Task>? SelectMcpRepairHandler(
        IReadOnlyList<Func<McpRepairRequest, Task>> handlers,
        object? activeWindowContext)
        => (activeWindowContext is null
                ? null
                : handlers.FirstOrDefault(handler => ReferenceEquals(handler.Target, activeWindowContext)))
           ?? handlers.LastOrDefault();

    internal McpRepairRequest BuildMcpRepairRequest(StrataComposerChip problem)
    {
        var name = problem.Name;
        var capability = GetCapabilities().FindMcpServer(name);
        var definition = _dataStore.Data.McpServers.LastOrDefault(server => NameComparer.Equals(server.Name, name))
                         ?? capability?.McpDefinition;

        McpServerConfig? config = null;
        if (CurrentChat is { } source)
        {
            var runtimeKey = _sessionMcpPlans.TryGetValue(source.Id, out var plan) ? plan.ResolveRuntimeKey(name) : name;
            config = ResolveMcpConfig(source.Id, runtimeKey);
        }

        // Connectors with a problem stay off in the repair chat, so they cannot hold it up; every other
        // selection carries over, together with the chat's own project, checkout and model (never the
        // sidebar's project filter: the connector ran where this chat runs).
        var failing = ActiveMcpChips.OfType<StrataComposerChip>()
            .Where(chip => chip.HasError)
            .Select(chip => chip.Name)
            .ToHashSet(NameComparer);
        var chat = new Chat
        {
            Title = string.Format(CultureInfo.CurrentCulture, Loc.Connector_RepairChatTitle, name),
            ProjectId = CurrentChat?.ProjectId,
            WorktreePath = CurrentChat?.WorktreePath,
            LastModelUsed = SelectedModel,
            LastReasoningEffortUsed = GetPersistedReasoningEffortPreference(),
            LastContextWindowTierUsed = GetSelectedContextWindowTier(),
            ActiveMcpServerNames = ActiveMcpServerNames.Where(selected => !failing.Contains(selected)).ToList(),
            HasExplicitMcpServerSelection = true,
            // The repair chat runs where this chat runs, so it acts with the same freedom.
            ApprovalMode = CurrentChat?.ApprovalMode ?? _draftApprovalMode,
        };

        var summary = problem.ErrorMessage ?? "";
        return new McpRepairRequest(
            chat,
            CurrentChat?.Id,
            name,
            string.Format(CultureInfo.CurrentCulture, Loc.Connector_RepairMessage, name, summary),
            McpConnectorIssues.ReportFileName(name) + ".md",
            McpConnectorIssues.BuildRepairReport(
                name,
                summary,
                problem.ErrorDetails,
                definition,
                config,
                capability,
                CurrentChat is { } current ? GetEffectiveWorkingDirectory(current) : GetEffectiveWorkingDirectory()));
    }
}
