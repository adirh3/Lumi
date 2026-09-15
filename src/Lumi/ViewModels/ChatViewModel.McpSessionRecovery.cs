using System.Diagnostics;
using Avalonia.Threading;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Models;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private const int McpSessionNotFoundCode = -32_001;
    private const string McpSessionNotFoundMessage = "Session not found";
    private static readonly TimeSpan McpCatalogReconciliationBudget = TimeSpan.FromSeconds(30);

    private readonly object _mcpCatalogRecoveryLock = new();
    private readonly Dictionary<Guid, McpCatalogRecovery> _mcpCatalogRecoveries = [];
    private readonly Dictionary<Guid, HashSet<string>> _visibleMcpProviderBaselines = [];
    private readonly Dictionary<Guid, HashSet<string>> _degradedMcpProviders = [];
    private readonly HashSet<Guid> _postSendMcpCatalogObservations = [];
    private readonly HashSet<Guid> _mcpCatalogRecoveryReplayPending = [];

    internal enum McpCatalogRecoverySignal
    {
        SessionResumed,
        ToolsListChanged,
        ExactSessionLoss,
        ProviderDegradedBeforeSend
    }

    internal sealed record McpCatalogRecoveryOperations(
        Func<IReadOnlySet<string>> GetSelectedProviders,
        Func<CancellationToken, Task<IReadOnlySet<string>?>> ReadVisibleProviders,
        Func<CancellationToken, Task> ReconcileCatalog,
        Func<CancellationToken, Task> ReplaceSession);

    internal sealed record McpCatalogEvaluation(
        IReadOnlySet<string> UpdatedBaseline,
        IReadOnlySet<string> MissingProviders);

    private sealed class McpCatalogRecovery(
        CopilotSession session,
        CancellationTokenSource cancellation,
        Task completion)
    {
        public CopilotSession Session { get; } = session;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public Task Completion { get; } = completion;
        public bool RerunRequested { get; set; }
        public CopilotSession? RerunSession { get; set; }
        public McpCatalogRecoverySignal? RerunSignal { get; set; }
        public McpCatalogRecoveryOperations? RerunOperations { get; set; }
    }

    internal static bool IsExactMcpSessionLoss(
        int? statusCode,
        string? errorCode,
        string? message)
    {
        var hasExpectedCode = statusCode == McpSessionNotFoundCode
            || (int.TryParse(errorCode, out var parsedCode) && parsedCode == McpSessionNotFoundCode);
        return hasExpectedCode
            && string.Equals(
                message?.Trim(),
                McpSessionNotFoundMessage,
                StringComparison.OrdinalIgnoreCase);
    }

    internal static McpCatalogEvaluation EvaluateMcpCatalog(
        IEnumerable<string> baselineProviders,
        IEnumerable<string> selectedProviders,
        IEnumerable<string> visibleProviders)
    {
        var selected = selectedProviders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visible = visibleProviders.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var updatedBaseline = baselineProviders
            .Where(selected.Contains)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        updatedBaseline.UnionWith(visible.Where(selected.Contains));

        var missing = baselineProviders
            .Where(selected.Contains)
            .Where(provider => !visible.Contains(provider))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return new McpCatalogEvaluation(updatedBaseline, missing);
    }

    internal bool TryScheduleMcpCatalogReconciliation(
        Chat chat,
        CopilotSession session,
        McpCatalogRecoverySignal signal,
        McpCatalogRecoveryOperations? operationsOverride = null)
        => TryScheduleMcpCatalogReconciliation(
            chat,
            session,
            signal,
            out _,
            operationsOverride);

    internal bool TryScheduleMcpCatalogReconciliation(
        Chat chat,
        CopilotSession session,
        McpCatalogRecoverySignal signal,
        out Task? recoveryTask,
        McpCatalogRecoveryOperations? operationsOverride = null)
    {
        if (_isDisposed
            || !_sessionCache.TryGetValue(chat.Id, out var currentSession)
            || !ReferenceEquals(currentSession, session))
        {
            recoveryTask = null;
            return false;
        }

        lock (_mcpCatalogRecoveryLock)
        {
            if (_mcpCatalogRecoveries.TryGetValue(chat.Id, out var currentRecovery))
            {
                currentRecovery.RerunRequested = true;
                if (!ReferenceEquals(currentRecovery.Session, session))
                {
                    currentRecovery.RerunSession = session;
                    currentRecovery.RerunSignal = signal;
                    currentRecovery.RerunOperations = operationsOverride;
                }
                recoveryTask = currentRecovery.Completion;
                return false;
            }

            var cancellation = new CancellationTokenSource();
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var recovery = new McpCatalogRecovery(session, cancellation, completion.Task);
            _mcpCatalogRecoveries[chat.Id] = recovery;
            recoveryTask = completion.Task;

            _ = completion.Task.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            var operations = operationsOverride ?? CreateMcpCatalogRecoveryOperations(chat, session);
            _ = RunMcpCatalogReconciliationAsync(chat, signal, recovery, operations, completion);
            return true;
        }
    }

    private void ObserveMcpCatalogAfterSuccessfulSend(Chat chat, CopilotSession session)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            if (!_postSendMcpCatalogObservations.Remove(chat.Id))
                return;
        }

        _ = ObserveMcpCatalogAfterSessionCreationAsync(
            chat,
            session,
            CancellationToken.None,
            retryWhenUninitialized: false);
    }

    private McpCatalogRecoveryOperations CreateMcpCatalogRecoveryOperations(
        Chat chat,
        CopilotSession session)
        => new(
            () => GetSelectedMcpProviders(chat.Id),
            ct => ReadVisibleMcpProvidersAsync(session, ct),
            ct => ReconcileMcpCatalogAsync(session, ct),
            ct => ReplaceMcpBackendSessionAsync(chat, session, ct));

    private IReadOnlySet<string> GetSelectedMcpProviders(Guid chatId)
    {
        if (!_sessionMcpPlans.TryGetValue(chatId, out var plan))
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var selectedAtCreation = plan.GetSelectedRuntimeServerNames();
        var chat = _dataStore.Data.Chats.FirstOrDefault(candidate => candidate.Id == chatId);
        if (chat is null
            || (!chat.HasExplicitMcpServerSelection && chat.ActiveMcpServerNames.Count == 0))
        {
            return selectedAtCreation;
        }

        var currentlySelected = chat.ActiveMcpServerNames
            .Select(plan.ResolveRuntimeKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mappedRuntimeNames = plan.RuntimeKeysByName?.Values
            .ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var selected = selectedAtCreation
            .Where(provider =>
                currentlySelected.Contains(provider)
                || (plan.Servers.ContainsKey(provider) && !mappedRuntimeNames.Contains(provider)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var agentRestrictsMcp = chat.AgentId is { } agentId
            && _dataStore.Data.Agents.FirstOrDefault(agent => agent.Id == agentId) is
                { McpServerIds.Count: > 0 };
        if (!agentRestrictsMcp)
            selected.UnionWith(currentlySelected);
        return selected;
    }

    private static async Task<IReadOnlySet<string>?> ReadVisibleMcpProvidersAsync(
        CopilotSession session,
        CancellationToken cancellationToken)
    {
        var metadata = await session.Rpc.Tools
            .GetCurrentMetadataAsync(cancellationToken)
            .ConfigureAwait(true);
        if (metadata.Tools is null)
            return null;

        return ExtractVisibleMcpProviders(metadata.Tools);
    }

    internal static IReadOnlySet<string> ExtractVisibleMcpProviders(
        IEnumerable<CurrentToolMetadata> tools)
        => tools
            .Select(tool => tool.McpServerName)
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Select(static name => name!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static async Task ReconcileMcpCatalogAsync(
        CopilotSession session,
        CancellationToken cancellationToken)
    {
        await session.Rpc.Tools
            .InitializeAndValidateAsync(cancellationToken)
            .ConfigureAwait(true);
    }

    private async Task RunMcpCatalogReconciliationAsync(
        Chat chat,
        McpCatalogRecoverySignal signal,
        McpCatalogRecovery recovery,
        McpCatalogRecoveryOperations operations,
        TaskCompletionSource completion)
    {
        await Task.Yield();
        IReadOnlySet<string> selectedProviders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> providersToReport = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        IReadOnlySet<string> providersAtRisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var rpcCts = CancellationTokenSource.CreateLinkedTokenSource(recovery.Cancellation.Token);
            rpcCts.CancelAfter(McpCatalogReconciliationBudget);

            while (true)
            {
                selectedProviders = operations.GetSelectedProviders();
                providersToReport = GetExpectedMcpProviders(chat.Id, selectedProviders);
                providersAtRisk = providersToReport;
                var visibleProviders = await operations.ReadVisibleProviders(rpcCts.Token);
                var evaluation = EvaluateAndRecordMcpCatalog(
                    chat.Id,
                    selectedProviders,
                    visibleProviders);
                providersToReport = evaluation.MissingProviders;
                if (evaluation.MissingProviders.Count > 0)
                    RecordMcpProviderDegradation(chat.Id, evaluation.MissingProviders);

                // Every accepted signal owns one bounded catalog refresh. Provider presence only
                // decides whether replacement is necessary after that refresh; it must not bypass
                // reconciliation because same-provider tools and schemas may have changed.
                await operations.ReconcileCatalog(rpcCts.Token);
                visibleProviders = await operations.ReadVisibleProviders(rpcCts.Token);
                evaluation = EvaluateAndRecordMcpCatalog(
                    chat.Id,
                    operations.GetSelectedProviders(),
                    visibleProviders);
                providersToReport = evaluation.MissingProviders;
                signal = McpCatalogRecoverySignal.ToolsListChanged;

                if (evaluation.MissingProviders.Count == 0)
                {
                    ClearMcpCatalogDegradation(chat.Id);
                    if (TryCompleteMcpCatalogRecovery(chat.Id, recovery, completion))
                        continue;
                    return;
                }

                recovery.Cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentSession(chat.Id, recovery.Session))
                {
                    completion.TrySetResult();
                    return;
                }

                using var replacementCts =
                    CancellationTokenSource.CreateLinkedTokenSource(recovery.Cancellation.Token);
                replacementCts.CancelAfter(McpSessionSetupTimeout);
                var retiredProviders = RetireMissingMcpProviderBaselines(
                    chat.Id,
                    evaluation.MissingProviders);
                try
                {
                    await operations.ReplaceSession(replacementCts.Token);
                }
                catch
                {
                    RestoreMissingMcpProviderBaselines(chat.Id, retiredProviders);
                    throw;
                }
                CompleteMcpCatalogRecoveryAfterReplacement(chat, recovery, completion);
                return;
            }
        }
        catch (OperationCanceledException ex) when (recovery.Cancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(ex.CancellationToken);
        }
        catch (OperationCanceledException ex)
        {
            var timeout = new TimeoutException(
                "MCP tool catalog recovery timed out.",
                ex);
            var affectedProviders =
                providersToReport.Count > 0 ? providersToReport : providersAtRisk;
            RecordMcpProviderDegradation(chat.Id, affectedProviders);
            ReportMcpCatalogRecoveryFailure(chat.Id, affectedProviders, timeout);
            completion.TrySetException(timeout);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(
                $"[MCP] Catalog reconciliation failed for chat {chat.Id} ({signal}): {ex.Message}");
            var affectedProviders =
                providersToReport.Count > 0 ? providersToReport : providersAtRisk;
            RecordMcpProviderDegradation(chat.Id, affectedProviders);
            ReportMcpCatalogRecoveryFailure(chat.Id, affectedProviders, ex);
            completion.TrySetException(ex);
        }

        finally
        {
            lock (_mcpCatalogRecoveryLock)
            {
                if (_mcpCatalogRecoveries.TryGetValue(chat.Id, out var current)
                    && ReferenceEquals(current, recovery))
                {
                    _mcpCatalogRecoveries.Remove(chat.Id);
                }
            }

            recovery.Cancellation.Dispose();
        }
    }

    private bool TryCompleteMcpCatalogRecovery(
        Guid chatId,
        McpCatalogRecovery recovery,
        TaskCompletionSource completion)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            if (recovery.RerunRequested)
            {
                recovery.RerunRequested = false;
                return true;
            }

            if (_mcpCatalogRecoveries.TryGetValue(chatId, out var current)
                && ReferenceEquals(current, recovery))
            {
                _mcpCatalogRecoveries.Remove(chatId);
            }
            completion.TrySetResult();
            return false;
        }
    }

    private void CompleteMcpCatalogRecoveryAfterReplacement(
        Chat chat,
        McpCatalogRecovery recovery,
        TaskCompletionSource completion)
    {
        McpCatalogRecovery? rerunRecovery = null;
        McpCatalogRecoveryOperations? rerunOperations = null;
        TaskCompletionSource? rerunCompletion = null;
        var rerunSignal = McpCatalogRecoverySignal.ToolsListChanged;

        lock (_mcpCatalogRecoveryLock)
        {
            if (!_mcpCatalogRecoveries.TryGetValue(chat.Id, out var current)
                || !ReferenceEquals(current, recovery))
            {
                completion.TrySetResult();
                return;
            }

            if (recovery.RerunRequested
                && recovery.RerunSession is { } rerunSession
                && recovery.RerunSignal is { } requestedSignal
                && !ReferenceEquals(rerunSession, recovery.Session)
                && IsCurrentSession(chat.Id, rerunSession))
            {
                rerunCompletion =
                    new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                rerunRecovery = new McpCatalogRecovery(
                    rerunSession,
                    new CancellationTokenSource(),
                    rerunCompletion.Task);
                rerunOperations = recovery.RerunOperations
                    ?? CreateMcpCatalogRecoveryOperations(chat, rerunSession);
                rerunSignal = requestedSignal;
                _mcpCatalogRecoveries[chat.Id] = rerunRecovery;

                _ = rerunCompletion.Task.ContinueWith(
                    static task => _ = task.Exception,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }
            else
            {
                _mcpCatalogRecoveries.Remove(chat.Id);
            }

            completion.TrySetResult();
        }

        if (rerunRecovery is not null && rerunOperations is not null && rerunCompletion is not null)
        {
            _ = RunMcpCatalogReconciliationAsync(
                chat,
                rerunSignal,
                rerunRecovery,
                rerunOperations,
                rerunCompletion);
        }
    }

    private IReadOnlySet<string> GetExpectedMcpProviders(
        Guid chatId,
        IReadOnlySet<string> selectedProviders)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            return _visibleMcpProviderBaselines.TryGetValue(chatId, out var baseline)
                ? baseline.Where(selectedProviders.Contains)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private McpCatalogEvaluation EvaluateAndRecordMcpCatalog(
        Guid chatId,
        IReadOnlySet<string> selectedProviders,
        IReadOnlySet<string>? visibleProviders)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            var baseline = _visibleMcpProviderBaselines.TryGetValue(chatId, out var current)
                ? current
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // A null SDK result means tools have not been initialized. It is not evidence that a
            // provider disappeared, but an existing baseline still needs the reconciliation step.
            var evaluation = visibleProviders is null
                ? new McpCatalogEvaluation(
                    baseline.Where(selectedProviders.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase),
                    baseline.Where(selectedProviders.Contains)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase))
                : EvaluateMcpCatalog(baseline, selectedProviders, visibleProviders);

            _visibleMcpProviderBaselines[chatId] =
                evaluation.UpdatedBaseline.ToHashSet(StringComparer.OrdinalIgnoreCase);
            return evaluation;
        }
    }

    private async Task ObserveMcpCatalogAfterSessionCreationAsync(
        Chat chat,
        CopilotSession session,
        CancellationToken cancellationToken,
        bool retryWhenUninitialized = true)
    {
        if (GetSelectedMcpProviders(chat.Id).Count == 0)
            return;

        using var rpcCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        rpcCts.CancelAfter(McpCatalogReconciliationBudget);
        try
        {
            var visible = await ReadVisibleMcpProvidersAsync(session, rpcCts.Token);
            if (!IsCurrentSession(chat.Id, session))
                return;
            if (visible is null)
            {
                if (retryWhenUninitialized)
                {
                    lock (_mcpCatalogRecoveryLock)
                        _postSendMcpCatalogObservations.Add(chat.Id);
                }
                return;
            }

            var evaluation = EvaluateAndRecordMcpCatalog(
                chat.Id,
                GetSelectedMcpProviders(chat.Id),
                visible);
            if (evaluation.MissingProviders.Count > 0)
                RecordMcpProviderDegradation(chat.Id, evaluation.MissingProviders);
            else
                ClearMcpCatalogDegradation(chat.Id);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Debug.WriteLine($"[MCP] Initial catalog observation timed out for chat {chat.Id}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (IsMcpCatalogMetadataFailure(ex))
        {
            Debug.WriteLine($"[MCP] Initial catalog observation failed for chat {chat.Id}: {ex.Message}");
        }
    }

    private static bool IsMcpCatalogMetadataFailure(Exception ex)
        => ex is IOException or HttpRequestException or TimeoutException
           || string.Equals(
               ex.GetType().FullName,
               "GitHub.Copilot.RemoteRpcException",
               StringComparison.Ordinal);

    private void RecordMcpProviderStatus(Guid chatId, string serverName, McpServerStatus status)
    {
        if (status == McpServerStatus.Connected || string.IsNullOrWhiteSpace(serverName))
            return;

        lock (_mcpCatalogRecoveryLock)
        {
            if (!_visibleMcpProviderBaselines.TryGetValue(chatId, out var baseline)
                || !baseline.Contains(serverName)
                || !GetSelectedMcpProviders(chatId).Contains(serverName))
            {
                return;
            }

            if (!_degradedMcpProviders.TryGetValue(chatId, out var degraded))
            {
                degraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _degradedMcpProviders[chatId] = degraded;
            }
            degraded.Add(serverName);
        }
    }

    private void RecordMcpProviderDegradation(Guid chatId, IEnumerable<string> providers)
    {
        var missingProviders = providers.ToArray();
        if (missingProviders.Length == 0)
            return;

        lock (_mcpCatalogRecoveryLock)
        {
            if (!_degradedMcpProviders.TryGetValue(chatId, out var degraded))
            {
                degraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _degradedMcpProviders[chatId] = degraded;
            }
            degraded.UnionWith(missingProviders);
        }
    }

    private void ClearMcpCatalogDegradation(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            _degradedMcpProviders.Remove(chatId);
    }

    private IReadOnlySet<string> RetireMissingMcpProviderBaselines(
        Guid chatId,
        IReadOnlySet<string> missingProviders)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            var retired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_visibleMcpProviderBaselines.TryGetValue(chatId, out var baseline))
            {
                foreach (var provider in missingProviders)
                {
                    if (baseline.Remove(provider))
                        retired.Add(provider);
                }
            }

            if (_degradedMcpProviders.TryGetValue(chatId, out var degraded))
            {
                degraded.ExceptWith(missingProviders);
                if (degraded.Count == 0)
                    _degradedMcpProviders.Remove(chatId);
            }

            return retired;
        }
    }

    private void RestoreMissingMcpProviderBaselines(
        Guid chatId,
        IReadOnlySet<string> retiredProviders)
    {
        if (retiredProviders.Count == 0)
            return;

        lock (_mcpCatalogRecoveryLock)
        {
            if (!_visibleMcpProviderBaselines.TryGetValue(chatId, out var baseline))
            {
                baseline = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _visibleMcpProviderBaselines[chatId] = baseline;
            }
            baseline.UnionWith(retiredProviders);

            if (!_degradedMcpProviders.TryGetValue(chatId, out var degraded))
            {
                degraded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _degradedMcpProviders[chatId] = degraded;
            }
            degraded.UnionWith(retiredProviders);
        }
    }

    internal void PruneMcpCatalogBaselineToSelection(Guid chatId)
    {
        var selected = GetSelectedMcpProviders(chatId);
        lock (_mcpCatalogRecoveryLock)
        {
            if (_visibleMcpProviderBaselines.TryGetValue(chatId, out var baseline))
                baseline.IntersectWith(selected);
            if (_degradedMcpProviders.TryGetValue(chatId, out var degraded))
            {
                degraded.IntersectWith(selected);
                if (degraded.Count == 0)
                    _degradedMcpProviders.Remove(chatId);
            }
        }
    }

    internal bool HasMcpCatalogDegradation(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            return _degradedMcpProviders.TryGetValue(chatId, out var providers) && providers.Count > 0;
    }

    private async Task AwaitMcpCatalogRecoveryBeforeSendAsync(
        Chat chat,
        CancellationToken cancellationToken)
    {
        Task? scheduledRecovery = null;
        if (HasMcpCatalogDegradation(chat.Id)
            && _sessionCache.TryGetValue(chat.Id, out var degradedSession))
        {
            TryScheduleMcpCatalogReconciliation(
                chat,
                degradedSession,
                McpCatalogRecoverySignal.ProviderDegradedBeforeSend,
                out scheduledRecovery);
        }

        if (scheduledRecovery is not null)
            await scheduledRecovery.WaitAsync(cancellationToken);
        await AwaitMcpCatalogRecoveryAsync(chat.Id, cancellationToken);
    }

    internal async Task AwaitMcpCatalogRecoveryAsync(
        Guid chatId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            Task? recoveryTask;
            lock (_mcpCatalogRecoveryLock)
                recoveryTask = _mcpCatalogRecoveries.GetValueOrDefault(chatId)?.Completion;

            if (recoveryTask is null)
                return;
            await recoveryTask.WaitAsync(cancellationToken);

            lock (_mcpCatalogRecoveryLock)
            {
                if (!_mcpCatalogRecoveries.TryGetValue(chatId, out var current)
                    || ReferenceEquals(current.Completion, recoveryTask))
                {
                    return;
                }
            }
        }
    }

    private async Task ReplaceMcpBackendSessionAsync(
        Chat chat,
        CopilotSession failedSession,
        CancellationToken cancellationToken)
    {
        if (_isDisposed || !IsCurrentSession(chat.Id, failedSession))
            return;

        // Never replay the failed tools/call. Retire the whole Copilot boundary and let normal
        // session creation rebuild MCP schemas from the current capability snapshot.
        var runtime = GetOrCreateRuntimeState(chat.Id);
        ClearPendingTurnTracking(chat.Id);
        ResolvePendingSteersAsFailed(chat.Id);
        MarkRuntimeTerminal(runtime);
        if (CurrentChat?.Id == chat.Id)
        {
            ApplyDisplayedRuntimeState(runtime);
            _transcriptBuilder.HideTypingIndicator();
            _transcriptBuilder.CloseCurrentToolGroup();
            _transcriptBuilder.CollapseCompletedBlocksInCurrentTurn();
        }

        DetachPersistedSession(chat, failedSession.SessionId);
        MarkMcpCatalogRecoveryReplayRequired(chat.Id);
        await AwaitPendingSessionReleaseAsync(chat.Id, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var created = await EnsureSessionAsync(chat, cancellationToken, allowCreateFallback: true);
        if (!created)
            throw new InvalidOperationException(Localization.Loc.Status_OriginalSessionUnavailable);

        cancellationToken.ThrowIfCancellationRequested();
        _pendingSessionInvalidations.Remove(chat.Id);
        _pendingSessionReconfigurations.Remove(chat.Id);
        QueueSaveChat(chat, saveIndex: true);
        ScheduleQueuedBusySendDrain(chat.Id);
    }

    private bool IsCurrentSession(Guid chatId, CopilotSession session)
        => _sessionCache.TryGetValue(chatId, out var current)
           && ReferenceEquals(current, session);

    internal bool HasPendingMcpCatalogRecoveryReplay(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            return _mcpCatalogRecoveryReplayPending.Contains(chatId);
    }

    private void MarkMcpCatalogRecoveryReplayRequired(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            _mcpCatalogRecoveryReplayPending.Add(chatId);
    }

    private void CompleteMcpCatalogRecoveryReplay(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            _mcpCatalogRecoveryReplayPending.Remove(chatId);
    }

    internal bool HasPendingMcpCatalogRecovery(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
            return _mcpCatalogRecoveries.ContainsKey(chatId);
    }

    internal void CancelMcpCatalogRecovery(Guid chatId)
    {
        McpCatalogRecovery? recovery;
        lock (_mcpCatalogRecoveryLock)
        {
            _degradedMcpProviders.Remove(chatId);
            _postSendMcpCatalogObservations.Remove(chatId);
            _mcpCatalogRecoveries.Remove(chatId, out recovery);
        }

        try
        {
            recovery?.Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void ForgetMcpCatalogState(Guid chatId)
    {
        lock (_mcpCatalogRecoveryLock)
        {
            _visibleMcpProviderBaselines.Remove(chatId);
            _degradedMcpProviders.Remove(chatId);
            _postSendMcpCatalogObservations.Remove(chatId);
            _mcpCatalogRecoveryReplayPending.Remove(chatId);
        }
    }

    private void CancelAllMcpCatalogRecoveries()
    {
        Guid[] chatIds;
        lock (_mcpCatalogRecoveryLock)
            chatIds = _mcpCatalogRecoveries.Keys.ToArray();
        foreach (var chatId in chatIds)
            CancelMcpCatalogRecovery(chatId);
        // A reconnect invalidates runtime sessions, not the fact that a replacement session has no
        // transcript. Keep replay-required markers until a send succeeds or the chat is cleaned up.
    }

    private void ReportMcpCatalogRecoveryFailure(
        Guid chatId,
        IEnumerable<string> selectedProviders,
        Exception error)
    {
        var providers = selectedProviders.ToArray();
        Dispatcher.UIThread.Post(() =>
        {
            if (providers.Length == 0)
            {
                if (CurrentChat?.Id == chatId)
                    StatusText = $"MCP tool catalog recovery failed: {error.Message}";
                return;
            }

            foreach (var provider in providers)
            {
                var displayName = ResolveMcpDisplayName(chatId, provider);
                SetMcpChipError(
                    chatId,
                    provider,
                    $"MCP server '{displayName}' lost its model-facing tools and Lumi could not restore them ({error.Message}).");
            }
        });
    }
}
