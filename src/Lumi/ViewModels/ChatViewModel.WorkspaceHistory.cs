using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lumi.Models;
using Lumi.Remote.Protocol;
using Lumi.Services;
using Lumi.Services.Storage;

namespace Lumi.ViewModels;

/// <summary>
/// What the Workspace panel and the Agents index list from a chat's older exchanges, which are only
/// in storage. Read once when a chat with history opens — its conversation and the few kinds of steps
/// the panel lists, through indexes, with SQLite reducing what it can (file edits to what they changed,
/// answers to those that can hold a link) — and reduced off the UI thread to the rows themselves, so no
/// transcript stays in memory for them. Rows that jump to the transcript build their exchange first.
/// </summary>
public partial class ChatViewModel
{
    /// <summary>Jump targets of rows from older exchanges: <c>stored:{messageId:N}:{seed}</c>.</summary>
    private const string StoredJumpPrefix = "stored:";

    /// <summary>The steps the panel lists besides file edits, which come as what they changed.</summary>
    internal static readonly string[] WorkspaceHistoryKinds =
    [
        ChatMessageKinds.Agent, ChatMessageKinds.Deliverable, ChatMessageKinds.Skill,
        ChatMessageKinds.Question, ChatMessageKinds.Intent, ChatMessageKinds.Web, ChatMessageKinds.ChangedFile,
    ];

    private WorkspaceHistoryItems? _workspaceHistory;
    private int _workspaceHistoryGeneration;
    private bool _workspaceHistoryReleased;

    /// <summary>The open chat's older rows, or null while there are none (or they are still loading).</summary>
    private WorkspaceHistoryItems? CurrentWorkspaceHistory
        => _workspaceHistory is { } history && ReferenceEquals(history.Chat, CurrentChat) ? history : null;

    /// <summary>Sub-agent runs of older exchanges, oldest first.</summary>
    private IReadOnlyList<SubagentToolCallItem> HistorySubagentRuns
        => CurrentWorkspaceHistory?.Runs ?? (IReadOnlyList<SubagentToolCallItem>)[];

    private void ResetWorkspaceHistory()
    {
        _workspaceHistoryGeneration++;
        _workspaceHistory = null;
        _workspaceHistoryReleased = false;
    }

    /// <summary>
    /// Reads back the Workspace's rows of older exchanges after an idle cached surface released them
    /// (see <see cref="ReleaseRealizedTranscriptControls"/>), when the surface is shown again.
    /// </summary>
    internal void RestoreReleasedHistorySnapshots()
    {
        if (_workspaceHistoryReleased && CurrentChat is { } chat)
            StartWorkspaceHistoryLoad(chat);
    }

    /// <summary>Reads the rows of the chat's older exchanges in the background, then shows them.</summary>
    private void StartWorkspaceHistoryLoad(Chat chat)
    {
        ResetWorkspaceHistory();
        if (!HasTranscriptHistory)
            return;
        _ = LoadWorkspaceHistoryAsync(chat, _workspaceHistoryGeneration);
    }

    private async Task LoadWorkspaceHistoryAsync(Chat chat, int generation)
    {
        try
        {
            var rows = await ReadWorkspaceHistoryRowsAsync(chat, _dataStore.GetTranscriptWindow(chat).TailStartSeq).ConfigureAwait(false);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (generation != _workspaceHistoryGeneration || !ReferenceEquals(CurrentChat, chat))
                    return;

                _workspaceHistory = CreateWorkspaceHistoryItems(chat, rows);
                RebuildWorkspacePanel();
                RefreshSubagentRunState();
                RefreshHeaderDetails();
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("[Workspace] Could not read the chat's older exchanges: {0}", ex);
        }
    }

#if DEBUG
    /// <summary>
    /// For the history harness: drops the Workspace's rows of older exchanges and, with
    /// <paramref name="load"/>, reads and shows them again, returning how long reading and showing took.
    /// </summary>
    internal async Task<(double ReadMs, double ShowMs)> ReloadWorkspaceHistoryAsync(bool load)
    {
        ResetWorkspaceHistory();
        RebuildWorkspacePanel();
        RefreshSubagentRunState();
        if (!load || CurrentChat is not { } chat || !HasTranscriptHistory)
            return default;

        var read = Stopwatch.StartNew();
        var rows = await ReadWorkspaceHistoryRowsAsync(chat, _dataStore.GetTranscriptWindow(chat).TailStartSeq).ConfigureAwait(true);
        var readMs = read.Elapsed.TotalMilliseconds;
        var show = Stopwatch.StartNew();
        _workspaceHistory = CreateWorkspaceHistoryItems(chat, rows);
        RebuildWorkspacePanel();
        RefreshSubagentRunState();
        RefreshHeaderDetails();
        return (readMs, show.Elapsed.TotalMilliseconds);
    }
#endif

    /// <summary>
    /// Reads the rows of the chat's history before <paramref name="beforeSeq"/>: the conversation and
    /// the kinds of steps the panel lists (runs as summaries), what each file edit changed, plus the
    /// steps each run's index row summarizes. Runs off the UI thread.
    /// </summary>
    private async Task<WorkspaceHistoryRows> ReadWorkspaceHistoryRowsAsync(Chat chat, long beforeSeq)
    {
        var overview = await _dataStore.ReadHistoryOverviewAsync(chat, WorkspaceHistoryKinds, beforeSeq).ConfigureAwait(false);
        var rows = DeriveWorkspaceHistory(overview.Messages, overview.FileEdits);
        // Each run's index row reads like its card: read just the steps the card summarizes.
        var runIds = rows.AgentMessages
            .Select(static message => message.ToolCallId)
            .OfType<string>()
            .Where(static id => id.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        rows.AgentSteps.AddRange(await _dataStore
            .ReadHistoryRunGlanceStepsAsync(chat, runIds, TranscriptBuilder.ToolsWithoutStepCards)
            .ConfigureAwait(false));
        return rows;
    }

    /// <summary>
    /// Reduces older messages (the conversation plus the steps the panel lists, oldest first) and what
    /// their file edits changed to rows, the way <see cref="RebuildWorkspacePanel"/> reads the live
    /// messages. Pure; runs on any thread.
    /// </summary>
    internal static WorkspaceHistoryRows DeriveWorkspaceHistory(IReadOnlyList<ChatMessage> messages, IReadOnlyList<StoredFileEdit> fileEdits)
    {
        var rows = new WorkspaceHistoryRows();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The transcript shows a skill once, where it first appears, and the panel lists what it shows.
        var seenSkills = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var latestEdits = new Dictionary<string, WorkspaceHistoryFileChange>(StringComparer.OrdinalIgnoreCase);
        var exchange = 0L;
        var touched = 0;
        var nextEdit = 0;

        void AddActivity(WorkspaceActivityKind kind, string? title, ChatMessage message)
        {
            if (string.IsNullOrWhiteSpace(title))
                return;
            if (rows.Activities.Count > 0
                && rows.Activities[^1].Kind == kind
                && string.Equals(rows.Activities[^1].Title, title, StringComparison.Ordinal))
            {
                return;
            }

            // Questions render under their question id, tool steps under their call id.
            var seed = !string.IsNullOrEmpty(message.QuestionId) ? message.QuestionId
                : !string.IsNullOrEmpty(message.ToolCallId) ? message.ToolCallId
                : message.Id.ToString();
            rows.Activities.Add(new WorkspaceHistoryActivity(kind, title, StoredJumpTarget(message.Id, seed)));
        }

        // File edits take their place among the messages: each belongs to the exchange its position is in.
        void AddFileEditsBefore(long seq)
        {
            for (; nextEdit < fileEdits.Count && fileEdits[nextEdit].Seq < seq; nextEdit++)
            {
                var edit = fileEdits[nextEdit];
                // Like a turn's summary, a file shows the edits of the latest exchange that touched it,
                // and its edits replace a change the exchange only reported.
                if (!latestEdits.TryGetValue(edit.FilePath, out var change) || change.Exchange != exchange || !change.HasEdits)
                {
                    change = new WorkspaceHistoryFileChange(edit.FilePath, edit.IsCreate, exchange);
                    latestEdits[edit.FilePath] = change;
                }

                change.AddEdit(edit.Seq, edit.LinesAdded, edit.LinesRemoved);
                change.Touched = ++touched;
            }
        }

        // A file the session reported changed without an edit shows with the exchange's edits unless one
        // of them edited it, as created when any report says so.
        void AddReportedChange(string filePath, bool isCreate)
        {
            if (latestEdits.TryGetValue(filePath, out var change) && change.Exchange == exchange)
            {
                if (!change.HasEdits)
                    change.IsCreate |= isCreate;
                return;
            }

            change = new WorkspaceHistoryFileChange(filePath, isCreate, exchange);
            latestEdits[filePath] = change;
            change.Touched = ++touched;
        }

        foreach (var message in messages)
        {
            AddFileEditsBefore(message.StorageSeq);
            switch (message.Role)
            {
                case "user":
                    exchange = message.StorageSeq;
                    if (!RemoteJobWake.IsJobWake(message.Author, message.Content))
                        rows.UserMessages.Add(message);
                    // A job's wake or a form's receipt shows as itself, without the skills it carries.
                    if (TranscriptBuilder.ShowsAsUserMessage(message.Role, message.Author, message.Content, message.InteractionReceipt is not null))
                    {
                        foreach (var skill in message.ActiveSkills)
                        {
                            if (seenSkills.Add(skill.Name))
                                rows.Skills.Add(new WorkspaceHistorySkill(skill, skill.Name));
                        }
                    }
                    break;
                case "assistant":
                    foreach (var link in ExtractAssistantLinks(message.Content))
                        rows.Links.Add((link.Title, link.Url));
                    break;
                case "error":
                    AddActivity(WorkspaceActivityKind.Error, FirstMeaningfulLine(message.Content), message);
                    break;
            }

            rows.Sources.AddRange(message.Sources);
            if (message.Role != "tool" || message.ToolName is not { Length: > 0 } tool)
                continue;

            if (string.Equals(tool, "announce_file", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = ToolDisplayHelper.ExtractJsonField(message.Content, "filePath");
                if (!string.IsNullOrWhiteSpace(filePath) && File.Exists(filePath) && seenFiles.Add(filePath))
                    rows.Deliverables.Add(filePath);
            }
            else if (string.Equals(tool, "report_intent", StringComparison.OrdinalIgnoreCase))
            {
                AddActivity(WorkspaceActivityKind.Intent, ToolDisplayHelper.ExtractJsonField(message.Content, "intent"), message);
            }
            else if (string.Equals(tool, "web_search", StringComparison.OrdinalIgnoreCase))
            {
                AddActivity(WorkspaceActivityKind.Search, ToolDisplayHelper.ExtractJsonField(message.Content, "query"), message);
            }
            else if (string.Equals(tool, "ask_question", StringComparison.OrdinalIgnoreCase)
                     || string.Equals(tool, "ask_input", StringComparison.OrdinalIgnoreCase))
            {
                var question = !string.IsNullOrWhiteSpace(message.QuestionText)
                    ? message.QuestionText
                    : ToolDisplayHelper.ExtractJsonField(message.Content, "question");
                AddActivity(WorkspaceActivityKind.Question, question, message);
            }
            else if (tool == "fetch_skill")
            {
                if (ToolDisplayHelper.ExtractJsonField(message.Content, "name") is { Length: > 0 } name && seenSkills.Add(name))
                    rows.Skills.Add(new WorkspaceHistorySkill(null, name));
            }
            else if (tool == ToolDisplayHelper.WorkspaceFileChangedToolName)
            {
                var reported = ToolDisplayHelper.ExtractJsonField(message.Content, "filePath")
                    ?? ToolDisplayHelper.ExtractJsonField(message.Content, "path");
                if (message.ToolStatus is not ("Failed" or "Stopped") && !string.IsNullOrWhiteSpace(reported))
                {
                    AddReportedChange(
                        reported.Trim(),
                        string.Equals(ToolDisplayHelper.ExtractJsonField(message.Content, "operation"), "Create", StringComparison.OrdinalIgnoreCase));
                }
            }

            if (string.Equals(tool, "task", StringComparison.OrdinalIgnoreCase) || tool.StartsWith("agent:", StringComparison.Ordinal))
            {
                var name = ToolDisplayHelper.GetSubagentDisplayName(tool, message.Content, message.Author);
                var task = ToolDisplayHelper.GetSubagentTaskDescription(tool, message.Content);
                AddActivity(WorkspaceActivityKind.Subagent, !string.IsNullOrWhiteSpace(task) ? task : name, message);
            }

            if (ToolDisplayHelper.IsSubagentTool(tool))
                rows.AgentMessages.Add(message);
        }

        AddFileEditsBefore(long.MaxValue);
        rows.FileChanges.AddRange(latestEdits.Values.OrderBy(static change => change.Touched));
        // A file whose creation carried no content is counted from disk, as its row reads it; a
        // reported creation shows the whole file as added text.
        foreach (var change in rows.FileChanges)
        {
            if (change.IsCreate && change.LinesAdded == 0)
            {
                change.LinesAdded = change.HasEdits
                    ? FileChangeItem.CountFileLines(change.FilePath)
                    : FileChangeItem.CountLines(FileChangeItem.ReadFileText(change.FilePath));
            }
        }

        return rows;
    }

    private static string StoredJumpTarget(Guid messageId, string seed) => $"{StoredJumpPrefix}{messageId:N}:{seed}";

    /// <summary>The panel's items for older rows, created once on the UI thread.</summary>
    private WorkspaceHistoryItems CreateWorkspaceHistoryItems(Chat chat, WorkspaceHistoryRows rows)
    {
        var items = new WorkspaceHistoryItems(chat);
        foreach (var path in rows.Deliverables)
            items.Files.Add(new FileAttachmentItem(path, isPreviewable: true, resolveIconLater: true));

        var seenLinks = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (title, url) in rows.Links)
        {
            if (seenLinks.Add(url))
                items.Links.Add(new SourceItem(title, url));
        }

        var seenSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in rows.Sources)
        {
            if (!string.IsNullOrWhiteSpace(source.Url) && seenSources.Add(source.Url))
                items.Sources.Add(new SourceItem(source));
        }

        foreach (var message in rows.UserMessages)
        {
            items.Messages.Add(new WorkspaceUserMessageItem(
                items.Messages.Count + 1,
                message.Content,
                message.Timestamp.ToString("HH:mm"),
                message.Attachments.Count,
                message.ActiveSkills.Count,
                StoredJumpTarget(message.Id, message.Id.ToString()),
                RaiseWorkspaceJump));
        }

        foreach (var activity in rows.Activities)
            items.Activities.Add(new WorkspaceActivityItem(activity.Kind, activity.Title, activity.Target, RaiseWorkspaceJump));

        foreach (var change in rows.FileChanges)
        {
            items.Edits.Add(new FileChangeItem(
                change.FilePath,
                change.IsCreate,
                change.LinesAdded,
                change.LinesRemoved,
                () => ReadStoredEdits(chat, change),
                edit => DiffShowRequested?.Invoke(edit)));
        }

        foreach (var skill in rows.Skills)
        {
            if ((skill.Reference ?? FindSkillReferenceByName(skill.Name)) is { } reference)
                items.Skills.Add(new SkillChipItem(reference, () => OpenSkillPreview(reference)));
        }

        // Runs are built exactly as the transcript builds them, from their own messages and the
        // steps their cards summarize. Like the runs of older exchanges in the transcript, each keeps
        // only what its row shows: its page reads the rest of its steps, with their count, and what
        // only the page shows (the task, the agent's log) when it opens. Work a crash cut off shows as
        // stopped, as everywhere else.
        if (rows.AgentMessages.Count > 0)
        {
            var runMessages = rows.AgentMessages
                .Concat(rows.AgentSteps)
                .OrderBy(static message => message.StorageSeq)
                .ToList();
            InteractionHistory.SettleInterrupted(runMessages);
            var builder = CreateTranscriptBuilder(subagentRunsChanged: null);
            builder.KeepsRunDetailInStorage = true;
            builder.BeginRebuild(forkOrigin: null);
            foreach (var message in runMessages)
                builder.ProcessMessageToTranscript(new ChatMessageViewModel(message));
            builder.EndRebuild();
            items.Runs.AddRange(builder.SubagentRuns);
        }

        return items;
    }

    /// <summary>
    /// What a file change of an older exchange shows in its diff, read back when the diff opens (off the
    /// UI thread): the edits its messages made to the file, in order, as <see cref="DeriveWorkspaceHistory"/>
    /// counted them.
    /// </summary>
    private IReadOnlyList<(string? OldText, string? NewText)> ReadStoredEdits(Chat chat, WorkspaceHistoryFileChange change)
    {
        // A change the session only reported shows the file as it is now: whole, when it was created.
        if (!change.HasEdits)
            return change.IsCreate ? [(null, FileChangeItem.ReadFileText(change.FilePath))] : [];

        var edits = new List<(string? OldText, string? NewText)>();
        try
        {
            foreach (var message in _dataStore.ReadStoredMessagesNow(chat, change.Seqs))
            {
                foreach (var diff in ToolDisplayHelper.ExtractAllDiffs(message.ToolName ?? "", message.Content))
                {
                    if (string.Equals(diff.FilePath, change.FilePath, StringComparison.OrdinalIgnoreCase))
                        edits.Add((diff.OldText, diff.NewText));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Trace.TraceWarning("[Workspace] Could not read the edits of {0}: {1}", change.FilePath, ex.Message);
        }

        return edits;
    }

    /// <summary>
    /// Resolves a Workspace jump to a row of an older exchange: builds that exchange (and the folded
    /// steps of its collapsed rows) and returns the turn that shows the row's step, or the exchange's
    /// first turn when the step has no row of its own.
    /// </summary>
    private async Task<TranscriptTurn?> ResolveStoredJumpAsync(string target)
    {
        var parts = target[StoredJumpPrefix.Length..].Split(':', 2);
        if (!Guid.TryParseExact(parts[0], "N", out var messageId)
            || !await EnsureMessageInTranscriptAsync(messageId, includeFoldedSteps: true).ConfigureAwait(true))
        {
            return null;
        }

        BuildActivitySeedIndex();
        string[] seeds = parts.Length > 1 ? [parts[1], messageId.ToString()] : [messageId.ToString()];
        foreach (var seed in seeds)
        {
            if (_activitySeedToTurnId.TryGetValue(seed, out var turnId)
                && TranscriptTurns.FirstOrDefault(turn => turn.StableId == turnId) is { } turn)
            {
                return turn;
            }
        }

        return _history.FindHydratedSegment(messageId)?.Turns?.FirstOrDefault();
    }
}

/// <summary>Rows of a chat's older exchanges for the Workspace, before they become items.</summary>
internal sealed class WorkspaceHistoryRows
{
    public List<string> Deliverables { get; } = [];
    public List<(string Title, string Url)> Links { get; } = [];
    public List<SearchSource> Sources { get; } = [];
    public List<ChatMessage> UserMessages { get; } = [];
    public List<WorkspaceHistoryActivity> Activities { get; } = [];
    public List<WorkspaceHistoryFileChange> FileChanges { get; } = [];
    public List<WorkspaceHistorySkill> Skills { get; } = [];
    public List<ChatMessage> AgentMessages { get; } = [];

    /// <summary>The steps each run in <see cref="AgentMessages"/> summarizes on its card.</summary>
    public List<ChatMessage> AgentSteps { get; } = [];
}

internal sealed record WorkspaceHistoryActivity(WorkspaceActivityKind Kind, string Title, string Target);

internal sealed record WorkspaceHistorySkill(SkillReference? Reference, string Name);

internal sealed class WorkspaceHistoryFileChange(string filePath, bool isCreate, long exchange)
{
    public string FilePath { get; } = filePath;
    public bool IsCreate { get; set; } = isCreate;
    public long Exchange { get; } = exchange;

    /// <summary>The positions of the messages whose edits the row shows; their text stays in storage.</summary>
    public List<long> Seqs { get; } = [];

    /// <summary>Whether edits show what changed; otherwise the session only reported the change.</summary>
    public bool HasEdits => Seqs.Count > 0;

    public int LinesAdded { get; set; }
    public int LinesRemoved { get; private set; }
    public int Touched { get; set; }

    public void AddEdit(long seq, int linesAdded, int linesRemoved)
    {
        if (Seqs.Count == 0 || Seqs[^1] != seq)
            Seqs.Add(seq);
        LinesAdded += linesAdded;
        LinesRemoved += linesRemoved;
    }
}

/// <summary>The Workspace's items for a chat's older exchanges, oldest first.</summary>
internal sealed class WorkspaceHistoryItems(Chat chat)
{
    public Chat Chat { get; } = chat;
    public List<FileAttachmentItem> Files { get; } = [];
    public List<SourceItem> Links { get; } = [];
    public List<SourceItem> Sources { get; } = [];
    public List<WorkspaceUserMessageItem> Messages { get; } = [];
    public List<WorkspaceActivityItem> Activities { get; } = [];
    public List<FileChangeItem> Edits { get; } = [];
    public List<SkillChipItem> Skills { get; } = [];
    public List<SubagentToolCallItem> Runs { get; } = [];
}
