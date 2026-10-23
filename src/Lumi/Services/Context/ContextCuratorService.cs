using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;

namespace Lumi.Services.Context;

/// <summary>
/// Keeps Lumi's cross-chat context current. Once a day (05:00, or at the first chance after it when Lumi wasn't
/// running) it reviews the chats since its last run: one curator session per project with activity, then one across
/// all projects. Each session updates its brief and curates memories; projects without activity are skipped.
/// </summary>
/// <remarks>
/// A missed day is caught up a day at a time (at most a week back), so each session sees one day like the nightly
/// run would. A scope that fails keeps its place and is retried on its own a few hours later. Update now reviews every
/// scope up to the moment.
/// </remarks>
public sealed class ContextCuratorService : IAsyncDisposable
{
    internal const int DayStartHour = 5;
    internal static readonly TimeSpan FirstRunLookback = TimeSpan.FromDays(3);
    internal static readonly TimeSpan MaxLookback = TimeSpan.FromDays(7);
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromHours(3);
    private static readonly TimeSpan SessionTimeout = TimeSpan.FromMinutes(15);
    private const int MaxParallelProjects = 2;

    private readonly DataStore _dataStore;
    private readonly CopilotService? _copilot;
    private readonly ChatHistoryService _history;
    private readonly Func<DateTimeOffset> _now;
    private readonly Func<bool> _isReady;
    private readonly Func<ContextCuratorScope, CancellationToken, Task> _runScopeAsync;
    private readonly TimeSpan _checkInterval;
    private readonly TimeSpan _startupDelay;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _runGate = new(1, 1);

    // Global memories project curators proposed and project briefs they changed wait here until an all-projects
    // review takes them in, so a failed one leaves them for its retry.
    private readonly List<CuratorProposal> _proposals = [];
    private readonly HashSet<Guid> _updatedProjects = [];
    private Task? _loopTask;
    private int _started;
    private int _disposed;
    private int _running;
    private DateTimeOffset? _lastFailureAt;
    private DateTimeOffset? _lastRunAt;

    public ContextCuratorService(DataStore dataStore, CopilotService copilot, GlobalSearchService? searchService)
        : this(
            dataStore,
            copilot,
            new ChatHistoryService(dataStore, searchService),
            static () => DateTimeOffset.Now,
            () => copilot.IsConnected,
            runScopeAsync: null,
            checkInterval: TimeSpan.FromMinutes(30),
            startupDelay: TimeSpan.FromMinutes(1))
    {
    }

    internal ContextCuratorService(
        DataStore dataStore,
        CopilotService? copilot,
        ChatHistoryService history,
        Func<DateTimeOffset> now,
        Func<bool> isReady,
        Func<ContextCuratorScope, CancellationToken, Task>? runScopeAsync,
        TimeSpan checkInterval,
        TimeSpan startupDelay)
    {
        _dataStore = dataStore;
        _copilot = copilot;
        _history = history;
        _now = now;
        _isReady = isReady;
        _runScopeAsync = runScopeAsync ?? RunSessionAsync;
        _checkInterval = checkInterval;
        _startupDelay = startupDelay;
    }

    /// <summary>Raised on any thread when a run starts or ends, or a brief or memory changed.</summary>
    public event Action? Changed;

    public bool IsRunning => Volatile.Read(ref _running) != 0;

    /// <summary>The last run that failed and hasn't been followed by a successful one.</summary>
    public bool LastRunFailed => _lastFailureAt is not null;

    /// <summary>When the chats were last reviewed: when this session's last successful run finished, or before one, how
    /// far the all-projects review (which always runs last) got.</summary>
    public DateTimeOffset? LastRunAt
        => _lastRunAt ?? _dataStore.Data.Briefs.ToArray().FirstOrDefault(brief => brief.ProjectId is null)?.CuratedThrough;

    private bool HasPendingProposals
    {
        get
        {
            lock (_proposals)
                return _proposals.Count > 0;
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;

        var cancellationToken = _shutdown.Token;
        _loopTask = Task.Run(() => RunLoopAsync(cancellationToken));
    }

    /// <summary>Asks the loop to check now, e.g. after the feature was turned on.</summary>
    public void RequestCheck()
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;
        try
        {
            _wakeSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // A check is already queued.
        }
    }

    /// <summary>Reviews the chats up to now, whatever the schedule says. False when a run is already going, the
    /// feature is off, Lumi isn't connected yet, or a scope failed.</summary>
    public async Task<bool> RunNowAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _disposed) != 0 || !_dataStore.Data.Settings.EnableDailyBriefs || !_isReady())
            return false;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
        try
        {
            return await RunAsync(everything: true, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    // ───────────── schedule ─────────────

    /// <summary>The most recent start of a day: 05:00 today, or yesterday before 05:00.</summary>
    internal static DateTimeOffset LatestDayStart(DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, DayStartHour, 0, 0, now.Offset);
        return now < start ? start.AddDays(-1) : start;
    }

    /// <summary>Splits a window at each day start, so a catch-up reviews one day per step.</summary>
    internal static List<(DateTimeOffset From, DateTimeOffset To)> SplitIntoSteps(DateTimeOffset from, DateTimeOffset to)
    {
        var steps = new List<(DateTimeOffset, DateTimeOffset)>();
        var cursor = from;
        for (var boundary = LatestDayStart(from).AddDays(1); boundary < to; boundary = boundary.AddDays(1))
        {
            steps.Add((cursor, boundary));
            cursor = boundary;
        }

        if (cursor < to)
            steps.Add((cursor, to));
        return steps;
    }

    /// <summary>Due once a day: when a scope hasn't been reviewed since the day started. After a failed run that is the
    /// scopes that failed, or proposals still waiting for an all-projects review, retried a few hours later.</summary>
    internal bool IsDue(DateTimeOffset now)
    {
        if (!_dataStore.Data.Settings.EnableDailyBriefs)
            return false;
        if (_lastFailureAt is { } failedAt && now - failedAt < RetryAfterFailure)
            return false;
        var briefs = _dataStore.Data.Briefs.ToArray();
        return briefs.Length == 0 || briefs.Any(brief => NeedsReview(brief, LatestDayStart(now))) || HasPendingProposals;
    }

    private static bool NeedsReview(ContextBrief brief, DateTimeOffset dayStart)
        => brief.CuratedThrough is not { } through || through < dayStart;

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(_startupDelay, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_isReady() && IsDue(_now()))
                    await RunAsync(everything: false, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Trace.TraceWarning($"[Context] Daily review failed: {ex}");
            }

            using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(_checkInterval, waitCts.Token);
            var wakeTask = _wakeSignal.WaitAsync(waitCts.Token);
            var completed = await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
            waitCts.Cancel();
            try
            {
                await completed.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    // ───────────── a run ─────────────

    /// <param name="everything">Review every scope up to now (Update now); otherwise only the scopes not reviewed
    /// since the day started.</param>
    internal async Task<bool> RunAsync(bool everything, CancellationToken cancellationToken)
    {
        if (!await _runGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            return false;

        Interlocked.Exchange(ref _running, 1);
        RaiseChanged();
        var succeeded = false;
        try
        {
            succeeded = await RunStepsAsync(everything, cancellationToken).ConfigureAwait(false);
            return succeeded;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Context] Daily review failed: {ex}");
            return false;
        }
        finally
        {
            _lastFailureAt = succeeded ? null : _now();
            if (succeeded)
                _lastRunAt = _now();
            Interlocked.Exchange(ref _running, 0);
            _runGate.Release();
            RaiseChanged();
        }
    }

    private async Task<bool> RunStepsAsync(bool everything, CancellationToken cancellationToken)
    {
        var now = _now();
        var settings = _dataStore.Data.Settings;
        var owner = string.IsNullOrWhiteSpace(settings.UserName) ? "the user" : settings.UserName.Trim();
        var canWriteMemories = settings.EnableMemoryAutoSave;
        var projects = _dataStore.Data.Projects.ToArray();
        var briefs = PrepareBriefs(projects);
        var dayStart = LatestDayStart(now);
        var due = briefs
            .Where(pair => everything || NeedsReview(pair.Value, dayStart))
            .Select(pair => pair.Key)
            .ToHashSet();
        if (due.Count == 0 && !HasPendingProposals)
            return true;

        // The schedule reviews whole days (what came after shows in new chats as a live list); Update now goes up to
        // the moment. With nothing due, only the proposals at the end are left to decide.
        var to = everything ? now : dayStart;
        var firstRunStart = dayStart - FirstRunLookback;
        var earliest = due.Count == 0 ? to : due.Min(id => briefs[id].CuratedThrough ?? firstRunStart);
        if (earliest < dayStart - MaxLookback)
            earliest = dayStart - MaxLookback;

        // Scopes that failed keep their place: later steps skip them, so their next run starts where this one did.
        var failed = new HashSet<Guid?>();
        foreach (var (stepFrom, stepTo) in SplitIntoSteps(earliest, to))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var projectScopes = new List<ContextCuratorScope>();
            foreach (var project in projects)
            {
                var brief = briefs[project.Id];
                var from = WindowStart(brief, firstRunStart, stepFrom);
                if (!due.Contains(project.Id) || failed.Contains(project.Id) || from >= stepTo)
                    continue;
                var chats = await LoadActivityAsync(project.Id, projects, from, stepTo, cancellationToken).ConfigureAwait(false);
                if (chats.Count == 0)
                {
                    brief.CuratedThrough = stepTo;
                    continue;
                }

                projectScopes.Add(new ContextCuratorScope(_dataStore, _history, project, brief, from, stepTo, chats,
                    _proposals, _updatedProjects, owner, canWriteMemories));
            }

            await RunProjectScopesAsync(projectScopes, failed, cancellationToken).ConfigureAwait(false);

            var globalBrief = briefs[Guid.Empty];
            var globalFrom = WindowStart(globalBrief, firstRunStart, stepFrom);
            var pending = _updatedProjects.Count > 0 || HasPendingProposals;
            if (!failed.Contains(null) && globalFrom < stepTo && (due.Contains(Guid.Empty) || pending))
            {
                var outside = await LoadActivityAsync(null, projects, globalFrom, stepTo, cancellationToken).ConfigureAwait(false);
                if (outside.Count > 0 || pending)
                {
                    var scope = new ContextCuratorScope(_dataStore, _history, null, globalBrief, globalFrom, stepTo, outside,
                        _proposals, _updatedProjects, owner, canWriteMemories);
                    if (await RunGlobalScopeAsync(scope, cancellationToken).ConfigureAwait(false))
                        globalBrief.CuratedThrough = stepTo;
                    else
                        failed.Add(null);
                }
                else
                {
                    globalBrief.CuratedThrough = stepTo;
                }
            }

            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
            RaiseChanged();
        }

        // A project retried after the all-projects curator already reviewed these days left proposals: they're decided
        // now, in a review without new chats of its own.
        if (HasPendingProposals && !failed.Contains(null))
        {
            var globalBrief = briefs[Guid.Empty];
            var through = globalBrief.CuratedThrough ?? to;
            var scope = new ContextCuratorScope(_dataStore, _history, null, globalBrief, through, through, [],
                _proposals, _updatedProjects, owner, canWriteMemories);
            if (!await RunGlobalScopeAsync(scope, cancellationToken).ConfigureAwait(false))
                failed.Add(null);
            await _dataStore.SaveAsync(cancellationToken).ConfigureAwait(false);
            RaiseChanged();
        }

        return failed.Count == 0;
    }

    /// <summary>Runs the all-projects scope; when it succeeds, it has taken in the proposals and project changes.</summary>
    private async Task<bool> RunGlobalScopeAsync(ContextCuratorScope scope, CancellationToken cancellationToken)
    {
        if (!await RunScopeAsync(scope, cancellationToken).ConfigureAwait(false))
            return false;

        lock (_proposals)
            _proposals.Clear();
        _updatedProjects.Clear();
        return true;
    }

    /// <summary>Where a scope's window starts in a step: where it left off, or the step's start.</summary>
    private static DateTimeOffset WindowStart(ContextBrief brief, DateTimeOffset firstRunStart, DateTimeOffset stepFrom)
    {
        var curatedThrough = brief.CuratedThrough ?? firstRunStart;
        return curatedThrough > stepFrom ? curatedThrough : stepFrom;
    }

    private async Task RunProjectScopesAsync(
        List<ContextCuratorScope> scopes,
        HashSet<Guid?> failed,
        CancellationToken cancellationToken)
    {
        using var gate = new SemaphoreSlim(MaxParallelProjects, MaxParallelProjects);
        await Task.WhenAll(scopes.Select(async scope =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var succeeded = await RunScopeAsync(scope, cancellationToken).ConfigureAwait(false);
                lock (failed)
                {
                    if (succeeded)
                    {
                        scope.Brief.CuratedThrough = scope.To;
                        if (scope.BriefChanged)
                            _updatedProjects.Add(scope.Project!.Id);
                    }
                    else
                    {
                        failed.Add(scope.Project!.Id);
                    }
                }
            }
            finally
            {
                gate.Release();
            }
        })).ConfigureAwait(false);
    }

    private async Task<bool> RunScopeAsync(ContextCuratorScope scope, CancellationToken cancellationToken)
    {
        try
        {
            await _runScopeAsync(scope, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Context] {scope.Name}: the review failed: {ex.Message}");
            return false;
        }
        finally
        {
            if (scope.BriefChanged || scope.MemoryChanges > 0)
                RaiseChanged();
        }
    }

    /// <summary>One brief per project and one across projects (keyed by <see cref="Guid.Empty"/> here), created as
    /// needed; briefs of deleted projects are dropped.</summary>
    private Dictionary<Guid, ContextBrief> PrepareBriefs(IReadOnlyList<Project> projects)
    {
        var list = _dataStore.Data.Briefs;
        var projectIds = projects.Select(project => project.Id).ToHashSet();
        list.RemoveAll(brief => brief.ProjectId is { } id && !projectIds.Contains(id));
        var result = new Dictionary<Guid, ContextBrief>();
        foreach (var id in projectIds.Select(id => (Guid?)id).Append(null))
        {
            var brief = list.FirstOrDefault(item => item.ProjectId == id);
            if (brief is null)
            {
                brief = new ContextBrief { ProjectId = id };
                list.Add(brief);
            }

            result[id ?? Guid.Empty] = brief;
        }

        return result;
    }

    /// <summary>Chats of a scope (a project, or outside projects when <paramref name="projectId"/> is null) with
    /// messages in (from, to], busiest first.</summary>
    private async Task<List<CuratorChat>> LoadActivityAsync(
        Guid? projectId,
        IReadOnlyList<Project> projects,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var projectIds = projects.Select(project => project.Id).ToHashSet();
        var candidates = _dataStore.Data.Chats.ToArray()
            .Where(chat => projectId is { } id
                ? chat.ProjectId == id
                : chat.ProjectId is not { } chatProject || !projectIds.Contains(chatProject))
            .Where(chat => chat.UpdatedAt > from.AddMinutes(-1))
            .ToList();

        var result = new List<CuratorChat>();
        foreach (var chat in candidates)
        {
            var messages = (await _dataStore.ReadChatMessagesAsync(chat, cancellationToken).ConfigureAwait(false))
                .Where(message => message.ParentToolCallId is null)
                .OrderBy(message => message.Timestamp)
                .ToList();
            var window = messages.Where(message => message.Timestamp > from && message.Timestamp <= to).ToList();
            if (window.Count == 0)
                continue;
            result.Add(new CuratorChat(chat, window, messages.Where(message => message.Timestamp <= from).ToList()));
        }

        return result
            .OrderByDescending(chat => chat.OwnerMessages)
            .ThenByDescending(chat => chat.Window.Count)
            .ToList();
    }

    private async Task RunSessionAsync(ContextCuratorScope scope, CancellationToken cancellationToken)
    {
        var copilot = _copilot ?? throw new InvalidOperationException("The context curator needs Copilot.");
        var stopwatch = Stopwatch.StartNew();
        long nanoAiu = 0;
        await copilot.UseLightweightSessionAsync(
            new LightweightSessionOptions
            {
                SystemPrompt = scope.SystemPrompt,
                Feature = AiFeatureIds.DailyBriefs,
                Streaming = true,
                Tools = scope.BuildTools(),
            },
            async (session, innerCt) =>
            {
                using var subscription = session.On<SessionEvent>(evt =>
                {
                    if (evt is AssistantUsageEvent usage)
                        Interlocked.Add(ref nanoAiu, (long)(usage.Data.CopilotUsage?.TotalNanoAiu ?? 0));
                });
                await session.SendAndWaitAsync(new MessageOptions { Prompt = scope.BuildPrompt() }, SessionTimeout, innerCt)
                    .ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);

        Debug.WriteLine($"[Context] {scope.Name} {scope.From:MMM d HH:mm}–{scope.To:MMM d HH:mm}: {scope.Chats.Count} chats, "
                        + $"{scope.ToolCalls} reads, {scope.MemoryChanges} memory changes, brief {(scope.BriefChanged ? "updated" : "kept")}, "
                        + $"{nanoAiu / 1e9:F2} AIU, {stopwatch.Elapsed.TotalSeconds:F0}s");
    }

    private void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"[Context] A change handler failed: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _shutdown.Cancel();
        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown.
            }
        }

        _shutdown.Dispose();
        _wakeSignal.Dispose();
        _runGate.Dispose();
    }
}
