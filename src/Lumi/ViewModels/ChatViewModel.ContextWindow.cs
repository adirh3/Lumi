using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

public sealed record ContextTokenBreakdownItem(
    string Key,
    string Label,
    long Tokens,
    string TokensDisplay,
    int SharePercent,
    string ShareDisplay);

internal readonly record struct ContextWindowMetrics(
    int UsagePercent,
    int ProgressPercent,
    long RemainingTokens,
    bool HasCompactionThreshold,
    bool CompactionThresholdReached,
    long TokensUntilCompaction);

public partial class ChatViewModel
{
    private static readonly TimeSpan ContextDetailsFreshness = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan ContextDetailsTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan ContextCompactionTimeout = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ContextCompactionAbortTimeout = TimeSpan.FromSeconds(15);

    private CancellationTokenSource? _contextDetailsCts;
    private long _contextDetailsGeneration;
    private CancellationTokenSource? _contextCompactionCts;
    private TaskCompletionSource<bool>? _contextCompactionCompletion;
    private DateTimeOffset? _contextDetailsUpdatedAt;
    private Guid? _contextCompactionChatId;
    private Guid? _stoppedContextCompactionChatId;
    private bool _manualContextCompactionStopRequested;
    private bool _usesSyntheticContextDetails;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContextPanelBusy))]
    [NotifyPropertyChangedFor(nameof(CanRefreshContextDetails))]
    [NotifyPropertyChangedFor(nameof(CanCompactContext))]
    private bool _isContextDetailsLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContextPanelBusy))]
    [NotifyPropertyChangedFor(nameof(IsContextCompactingForCurrentChat))]
    [NotifyPropertyChangedFor(nameof(CanRefreshContextDetails))]
    [NotifyPropertyChangedFor(nameof(CanCompactContext))]
    private bool _isContextCompacting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsContextPanelBusy))]
    [NotifyPropertyChangedFor(nameof(CanRefreshContextDetails))]
    [NotifyPropertyChangedFor(nameof(CanCompactContext))]
    private bool _isContextOperationRunning;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextDetailsError))]
    private string? _contextDetailsError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextActionStatus))]
    private string? _contextActionStatus;

    [ObservableProperty]
    private string? _contextDetailsModelId;

    [ObservableProperty]
    private string _contextDetailsTierDisplay = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextCompactionThreshold))]
    [NotifyPropertyChangedFor(nameof(ContextCompactionThresholdDisplay))]
    [NotifyPropertyChangedFor(nameof(ContextCompactionHeadroomDisplay))]
    [NotifyPropertyChangedFor(nameof(ContextHealthDisplay))]
    private long _contextCompactionThreshold;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextLastUpdatedDisplay))]
    private string? _contextLastUpdatedDisplay;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasContextLastCompactionDisplay))]
    private string? _contextLastCompactionDisplay;

    public ObservableCollection<ContextTokenBreakdownItem> ContextBreakdownItems { get; } = [];

    public bool HasContextDetailsError => !string.IsNullOrWhiteSpace(ContextDetailsError);
    public bool HasContextActionStatus => !string.IsNullOrWhiteSpace(ContextActionStatus);
    public bool HasContextLastUpdatedDisplay => !string.IsNullOrWhiteSpace(ContextLastUpdatedDisplay);
    public bool HasContextLastCompactionDisplay => !string.IsNullOrWhiteSpace(ContextLastCompactionDisplay);
    public bool HasContextBreakdown => ContextBreakdownItems.Count > 0;
    public bool IsContextPanelBusy => IsContextDetailsLoading || IsContextOperationRunning || IsContextCompactingForCurrentChat;
    public bool IsContextCompactingForCurrentChat => IsContextCompacting && _contextCompactionChatId == CurrentChat?.Id;

    public long ContextRemainingTokens
        => CalculateContextWindowMetrics(ContextCurrentTokens, ContextTokenLimit, ContextCompactionThreshold).RemainingTokens;

    public string ContextRemainingDisplay => HasContextUsage
        ? string.Format(Loc.Get("Chat_ContextWindow_RemainingFormat"), FormatTokenCount(ContextRemainingTokens))
        : "";

    public string ContextUsageDetailDisplay => HasContextUsage
        ? string.Format(
            Loc.Get("Chat_ContextWindow_UsedFormat"),
            FormatTokenCount(ContextCurrentTokens),
            FormatTokenCount(ContextTokenLimit))
        : "";

    public int ContextUsageProgress
        => CalculateContextWindowMetrics(ContextCurrentTokens, ContextTokenLimit, ContextCompactionThreshold).ProgressPercent;

    public string ContextHealthDisplay
    {
        get
        {
            if (!HasContextUsage)
                return "";

            var metrics = CalculateContextWindowMetrics(
                ContextCurrentTokens,
                ContextTokenLimit,
                ContextCompactionThreshold);

            if (metrics.CompactionThresholdReached)
                return Loc.Get("Chat_ContextWindow_HealthThreshold");

            if (metrics.HasCompactionThreshold
                && metrics.TokensUntilCompaction <= Math.Max(ContextCompactionThreshold / 10, 1))
            {
                return Loc.Get("Chat_ContextWindow_HealthCompactionSoon");
            }

            return metrics.UsagePercent switch
            {
                < 60 => Loc.Get("Chat_ContextWindow_HealthPlenty"),
                < 80 => Loc.Get("Chat_ContextWindow_HealthHealthy"),
                _ => Loc.Get("Chat_ContextWindow_HealthFilling")
            };
        }
    }

    public bool HasContextCompactionThreshold => ContextCompactionThreshold > 0;
    public string ContextCompactionThresholdDisplay => HasContextCompactionThreshold
        ? FormatTokenCount(ContextCompactionThreshold)
        : "";

    public string ContextCompactionHeadroomDisplay
    {
        get
        {
            var metrics = CalculateContextWindowMetrics(
                ContextCurrentTokens,
                ContextTokenLimit,
                ContextCompactionThreshold);

            if (!metrics.HasCompactionThreshold)
                return "";

            return metrics.CompactionThresholdReached
                ? Loc.Get("Chat_ContextWindow_AutoCompactionReached")
                : string.Format(
                    Loc.Get("Chat_ContextWindow_UntilAutoCompaction"),
                    FormatTokenCount(metrics.TokensUntilCompaction));
        }
    }

    public bool CanRefreshContextDetails
        => CurrentChat is { } chat
           && !string.IsNullOrWhiteSpace(chat.CopilotSessionId)
           && !_pendingSessionInvalidations.Contains(CurrentChat.Id)
           && !IsBusy
           && !IsContextDetailsLoading
           && !IsContextOperationRunning
           && !IsContextCompactingForCurrentChat;

    public bool CanCompactContext
        => CurrentChat is { } chat
           && !string.IsNullOrWhiteSpace(chat.CopilotSessionId)
           && HasContextUsage
           && !IsBusy
           && !IsContextDetailsLoading
           && !IsContextOperationRunning
           && !IsContextCompactingForCurrentChat;

    [RelayCommand]
    private Task OpenContextDetailsAsync()
        => RefreshContextDetailsCoreAsync(force: false);

    [RelayCommand]
    private Task RefreshContextDetailsAsync()
        => RefreshContextDetailsCoreAsync(force: true);

    [RelayCommand]
    private async Task CompactContextAsync()
    {
        if (!CanCompactContext || CurrentChat is not { } chat)
            return;
        if (_contextCompactionCts is not null)
            return;

        _contextDetailsCts?.Cancel();
        using var timeoutCts = new CancellationTokenSource(ContextCompactionTimeout);
        using var operationCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token);
        _contextCompactionCts = operationCts;
        _contextCompactionCompletion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _stoppedContextCompactionChatId = null;
        _manualContextCompactionStopRequested = false;
        var chatId = chat.Id;
        var runtime = GetOrCreateRuntimeState(chatId);
        CopilotSession? session = null;
        var compactionRequested = false;
        var terminationConfirmed = false;

        IsContextOperationRunning = true;
        IsContextCompacting = true;
        _contextCompactionChatId = chatId;
        NotifyContextActionAvailabilityChanged();
        ContextDetailsError = null;
        ContextActionStatus = Loc.Get("Chat_ContextWindow_Compacting");
        MarkRuntimeCompacting(runtime);
        ApplyDisplayedRuntimeState(runtime);

        try
        {
            session = await GetContextMaintenanceSessionAsync(chat, operationCts.Token);
            if (session is null)
                throw new InvalidOperationException(Loc.Get("Chat_ContextWindow_SessionUnavailable"));

            MarkRuntimeCompacting(runtime);
            if (CurrentChat?.Id == chatId)
                ApplyDisplayedRuntimeState(runtime);

            compactionRequested = true;
#pragma warning disable GHCP001
            var result = await session.Rpc.History.CompactAsync(
                new SessionHistoryCompactRequest
                {
                    Trigger = SessionHistoryCompactRequestTrigger.Manual
                },
                cancellationToken: operationCts.Token);
#pragma warning restore GHCP001

            terminationConfirmed = true;
            if (CurrentChat?.Id == chatId)
                ApplyHistoryCompactionResult(chat, result);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            terminationConfirmed = !compactionRequested
                || await TryAbortManualCompactionAsync(session);
            if (CurrentChat?.Id == chatId)
            {
                ContextActionStatus = null;
                ContextDetailsError = terminationConfirmed
                    ? Loc.Get("Chat_ContextWindow_CompactionTimedOut")
                    : Loc.Get("Chat_ContextWindow_CompactionStopPending");
            }
        }
        catch (OperationCanceledException)
        {
            terminationConfirmed = !compactionRequested
                || await TryAbortManualCompactionAsync(session);
            if (CurrentChat?.Id == chatId)
            {
                if (terminationConfirmed)
                {
                    ContextLastCompactionDisplay = FormatCompactionOutcome(
                        success: false,
                        tokensRemoved: null,
                        messagesRemoved: null,
                        error: null,
                        stoppedByUser: true);
                    ContextActionStatus = ContextLastCompactionDisplay;
                    ContextDetailsError = null;
                }
                else
                {
                    ContextActionStatus = null;
                    ContextDetailsError = Loc.Get("Chat_ContextWindow_CompactionStopPending");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Context compaction failed: {ex}");
            terminationConfirmed = !compactionRequested
                || await TryAbortManualCompactionAsync(session);
            if (CurrentChat?.Id == chatId)
            {
                ContextActionStatus = null;
                if (_manualContextCompactionStopRequested && terminationConfirmed)
                {
                    ContextLastCompactionDisplay = FormatCompactionOutcome(
                        success: false,
                        tokensRemoved: null,
                        messagesRemoved: null,
                        error: ex.Message,
                        stoppedByUser: true);
                    ContextActionStatus = ContextLastCompactionDisplay;
                    ContextDetailsError = null;
                }
                else
                {
                    ContextDetailsError = terminationConfirmed
                        ? string.Format(Loc.Get("Chat_ContextWindow_CompactionFailed"), ex.Message)
                        : Loc.Get("Chat_ContextWindow_CompactionStopPending");
                }
            }
        }
        finally
        {
            // The SDK completion event may have arrived while an abort request was in flight.
            terminationConfirmed |= !runtime.HasActiveWork;
            if (terminationConfirmed)
            {
                CompleteContextCompactionLifecycle(
                    chat,
                    runtime,
                    updateDisplayed: CurrentChat?.Id == chatId);
                CompleteManualContextCompactionTracking(chatId);
            }
            else
            {
                runtime.StatusText = Loc.Status_Compacting;
                if (CurrentChat?.Id == chatId)
                    ApplyDisplayedRuntimeState(runtime);
            }

            if (ReferenceEquals(_contextCompactionCts, operationCts))
                _contextCompactionCts = null;

            IsContextOperationRunning = false;
            if (!terminationConfirmed)
                IsContextCompacting = true;
            _manualContextCompactionStopRequested = false;
            NotifyContextActionAvailabilityChanged();
        }
    }

    private async Task<bool> TryStopManualContextCompactionAsync(Chat chat)
    {
        if (_contextCompactionChatId != chat.Id
            || _contextCompactionCompletion is null
            || !IsContextCompacting)
            return false;

        ContextDetailsError = null;
        ContextActionStatus = Loc.Get("Chat_ContextWindow_StoppingCompaction");
        _manualContextCompactionStopRequested = true;
        _stoppedContextCompactionChatId = chat.Id;
        var completion = _contextCompactionCompletion;

        if (_contextCompactionCts is { } operationCts)
        {
            operationCts.Cancel();
        }
        else
        {
            var terminationConfirmed = _sessionCache.TryGetValue(chat.Id, out var session)
                && await TryAbortManualCompactionAsync(session);
            if (terminationConfirmed)
            {
                var runtime = GetOrCreateRuntimeState(chat.Id);
                CompleteContextCompactionLifecycle(
                    chat,
                    runtime,
                    updateDisplayed: CurrentChat?.Id == chat.Id);
                ContextLastCompactionDisplay = FormatCompactionOutcome(
                    success: false,
                    tokensRemoved: null,
                    messagesRemoved: null,
                    error: null,
                    stoppedByUser: true);
                ContextActionStatus = ContextLastCompactionDisplay;
                CompleteManualContextCompactionTracking(chat.Id);
                _manualContextCompactionStopRequested = false;
            }
            else
            {
                ContextActionStatus = null;
                ContextDetailsError = Loc.Get("Chat_ContextWindow_CompactionStopPending");
            }
        }

        if (completion is not null)
            await completion.Task;
        return true;
    }

    private static async Task<bool> TryAbortManualCompactionAsync(CopilotSession? session)
    {
        if (session is null)
            return false;

        using var abortCts = new CancellationTokenSource(ContextCompactionAbortTimeout);
        try
        {
#pragma warning disable GHCP001
            await session.Rpc.History.AbortManualCompactionAsync(abortCts.Token);
#pragma warning restore GHCP001
            // A successful no-op also confirms that no manual compaction remains in flight.
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to abort manual context compaction: {ex}");
            return false;
        }
    }

    private void CompleteManualContextCompactionTracking(Guid chatId)
    {
        if (_contextCompactionChatId != chatId)
            return;

        _contextCompactionChatId = null;
        IsContextCompacting = false;
        var completion = _contextCompactionCompletion;
        _contextCompactionCompletion = null;
        completion?.TrySetResult(true);
        NotifyContextActionAvailabilityChanged();
    }

    private async Task RefreshContextDetailsCoreAsync(bool force)
    {
        if (CurrentChat is not { } chat)
            return;

        SeedContextIdentity(chat);
        if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
            return;
        if (_pendingSessionInvalidations.Contains(chat.Id))
            return;

        if (_usesSyntheticContextDetails)
        {
            ContextDetailsError = null;
            ContextActionStatus = Loc.Get("Chat_ContextWindow_DebugSnapshot");
            return;
        }

        if (!force
            && _contextDetailsUpdatedAt is { } updatedAt
            && DateTimeOffset.UtcNow - updatedAt <= ContextDetailsFreshness
            && HasContextBreakdown)
        {
            return;
        }

        if (IsBusy)
        {
            ContextDetailsError = null;
            ContextActionStatus = Loc.Get("Chat_ContextWindow_Busy");
            return;
        }

        if (IsContextOperationRunning || IsContextCompactingForCurrentChat)
            return;

        _contextDetailsCts?.Cancel();
        _contextDetailsCts = new CancellationTokenSource(ContextDetailsTimeout);
        var refreshCts = _contextDetailsCts;
        var chatId = chat.Id;
        var detailsGeneration = _contextDetailsGeneration;

        IsContextDetailsLoading = true;
        ContextDetailsError = null;
        ContextActionStatus = Loc.Get("Chat_ContextWindow_Loading");

        try
        {
            var session = await GetContextMaintenanceSessionAsync(chat, refreshCts.Token);
            if (session is null)
                throw new InvalidOperationException(Loc.Get("Chat_ContextWindow_SessionUnavailable"));

            await LoadContextDetailsFromSessionAsync(
                chat,
                session,
                refreshCts.Token,
                preserveActionStatus: false,
                detailsGeneration);
        }
        catch (OperationCanceledException) when (
            ReferenceEquals(_contextDetailsCts, refreshCts)
            && detailsGeneration == _contextDetailsGeneration)
        {
            if (CurrentChat?.Id == chatId)
            {
                ContextActionStatus = null;
                ContextDetailsError = Loc.Get("Chat_ContextWindow_RefreshTimedOut");
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Context details refresh failed: {ex}");
            if (CurrentChat?.Id == chatId)
            {
                ContextActionStatus = null;
                ContextDetailsError = string.Format(
                    Loc.Get("Chat_ContextWindow_RefreshFailed"),
                    ex.Message);
            }
        }
        finally
        {
            if (ReferenceEquals(_contextDetailsCts, refreshCts))
            {
                _contextDetailsCts = null;
                refreshCts.Dispose();
                IsContextDetailsLoading = false;
            }
        }
    }

    private async Task<CopilotSession?> GetContextMaintenanceSessionAsync(Chat chat, CancellationToken cancellationToken)
    {
        if (!_copilotService.IsConnected)
            await _copilotService.ConnectAsync(cancellationToken);

        var cachedSession = await TryGetReusableCachedSessionAsync(chat, cancellationToken);
        if (cachedSession is not null)
            return cachedSession;

        if (string.IsNullOrWhiteSpace(chat.CopilotSessionId))
            return null;

        var runtime = GetOrCreateRuntimeState(chat.Id);
        var previousStatus = runtime.StatusText;

        try
        {
            var ready = await EnsureSessionAsync(chat, cancellationToken, allowCreateFallback: false);
            return ready && _sessionCache.TryGetValue(chat.Id, out var resumedSession)
                ? resumedSession
                : null;
        }
        finally
        {
            if (!runtime.IsBusy)
            {
                runtime.StatusText = previousStatus;
                if (CurrentChat?.Id == chat.Id)
                    StatusText = previousStatus;
            }
        }
    }

    private async Task LoadContextDetailsFromSessionAsync(
        Chat chat,
        CopilotSession session,
        CancellationToken cancellationToken,
        bool preserveActionStatus,
        long detailsGeneration = -1)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        var modelId = runtime.ActiveModelId;
        if (string.IsNullOrWhiteSpace(modelId))
            modelId = ResolveSelectedModelForChat(chat);
        var contextTier = runtime.ActiveContextWindowTier
            ?? ResolveSelectedContextWindowTierForChat(chat, modelId);
        if (detailsGeneration < 0)
            detailsGeneration = _contextDetailsGeneration;
        var requestedPromptTokenLimit = ResolveKnownContextTokenLimitForIdentity(
            runtime,
            modelId,
            contextTier,
            reportedTokenLimit: 0);

#pragma warning disable GHCP001
        MetadataRecomputeContextTokensResult? recomputed = null;
        if (!string.IsNullOrWhiteSpace(modelId))
            recomputed = await session.Rpc.Metadata.RecomputeContextTokensAsync(modelId, cancellationToken);

        var contextInfoResult = await session.Rpc.Metadata.ContextInfoAsync(
            promptTokenLimit: requestedPromptTokenLimit,
            outputTokenLimit: 0,
            selectedModel: modelId,
            cancellationToken: cancellationToken);
#pragma warning restore GHCP001

        var currentModelId = runtime.ActiveModelId;
        if (string.IsNullOrWhiteSpace(currentModelId))
            currentModelId = ResolveSelectedModelForChat(chat);
        var currentContextTier = runtime.ActiveContextWindowTier
            ?? ResolveSelectedContextWindowTierForChat(chat, currentModelId);
        if (CurrentChat?.Id != chat.Id
            || detailsGeneration != _contextDetailsGeneration
            || !string.Equals(modelId, currentModelId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(contextTier, currentContextTier, StringComparison.OrdinalIgnoreCase))
            return;

        var info = contextInfoResult.ContextInfo
            ?? throw new InvalidOperationException(Loc.Get("Chat_ContextWindow_DetailsUnavailable"));
        ApplyContextDetails(
            chat,
            runtime,
            info,
            recomputed,
            requestedPromptTokenLimit,
            modelId,
            contextTier);

        _contextDetailsUpdatedAt = DateTimeOffset.UtcNow;
        ContextLastUpdatedDisplay = Loc.Get("Chat_ContextWindow_UpdatedNow");
        ContextDetailsError = null;
        if (!preserveActionStatus)
            ContextActionStatus = ContextLastUpdatedDisplay;
    }

    private void ApplyContextDetails(
        Chat chat,
        ChatRuntimeState runtime,
        MetadataContextInfoResultContextInfo info,
        MetadataRecomputeContextTokensResult? recomputed,
        long requestedPromptTokenLimit,
        string? modelId,
        string? contextTier)
    {
        var systemTokens = info.SystemTokens > 0
            ? info.SystemTokens
            : recomputed?.SystemTokenCount ?? 0;
        var conversationTokens = info.ConversationTokens > 0
            ? info.ConversationTokens
            : recomputed?.MessagesTokenCount ?? 0;
        var totalTokens = info.TotalTokens > 0
            ? info.TotalTokens
            : recomputed?.TotalTokens ?? systemTokens + conversationTokens + info.ToolDefinitionsTokens;
        var promptTokenLimit = ResolveContextInfoPromptTokenLimit(
            requestedPromptTokenLimit,
            info.PromptTokenLimit);

        ApplyContextUsage(
            chat,
            runtime,
            totalTokens > 0 ? totalTokens : null,
            promptTokenLimit > 0 ? promptTokenLimit : null,
            ContextTokenLimitSource.Session,
            updateDisplayed: true,
            currentTokensAreExact: true,
            tokenLimitModelId: modelId,
            tokenLimitTier: contextTier);

        ContextDetailsModelId = string.IsNullOrWhiteSpace(info.ModelName)
            ? runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat)
            : info.ModelName;
        ContextDetailsTierDisplay = FormatContextTier(runtime.ActiveContextWindowTier ?? chat.LastContextWindowTierUsed);
        ContextCompactionThreshold = info.CompactionThreshold;

        ReplaceItems(
            ContextBreakdownItems,
            CreateContextBreakdownItems(
                systemTokens,
                conversationTokens,
                info.ToolDefinitionsTokens > 0 ? info.ToolDefinitionsTokens : info.McpToolsTokens));
        OnPropertyChanged(nameof(HasContextBreakdown));

        QueueSaveChat(chat, saveIndex: false);
    }

    private void ApplyHistoryCompactionResult(Chat chat, HistoryCompactResult result)
    {
        if (result.ContextWindow is { } contextWindow)
        {
            var runtime = GetOrCreateRuntimeState(chat.Id);
            var tokenLimit = ResolveKnownContextTokenLimitForIdentity(
                runtime,
                runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat),
                runtime.ActiveContextWindowTier
                    ?? ResolveSelectedContextWindowTierForChat(
                        chat,
                        runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat)),
                contextWindow.TokenLimit);
            ApplyContextUsage(
                chat,
                runtime,
                contextWindow.CurrentTokens > 0 ? contextWindow.CurrentTokens : null,
                tokenLimit > 0 ? tokenLimit : null,
                ContextTokenLimitSource.Session,
                updateDisplayed: true,
                currentTokensAreExact: true,
                tokenLimitModelId: runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat),
                tokenLimitTier: runtime.ActiveContextWindowTier
                    ?? ResolveSelectedContextWindowTierForChat(
                        chat,
                        runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat)));

            ReplaceItems(
                ContextBreakdownItems,
                CreateContextBreakdownItems(
                    contextWindow.SystemTokens ?? 0,
                    contextWindow.ConversationTokens ?? 0,
                    contextWindow.ToolDefinitionsTokens ?? 0));
            OnPropertyChanged(nameof(HasContextBreakdown));
        }

        ContextLastCompactionDisplay = FormatCompactionOutcome(
            result.Success == true,
            result.TokensRemoved,
            result.MessagesRemoved,
            error: null,
            stoppedByUser: _stoppedContextCompactionChatId == chat.Id && result.Success != true);
        ContextActionStatus = ContextLastCompactionDisplay;
        QueueSaveChat(chat, saveIndex: false);
    }

    private void HandleContextCompactionStarted(
        Chat chat,
        ChatRuntimeState runtime,
        SessionCompactionStartData data,
        bool updateDisplayed)
    {
        var currentTokens = (long)(data.CurrentTokens ?? 0);
        var modelId = runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat);
        var contextTier = runtime.ActiveContextWindowTier
            ?? ResolveSelectedContextWindowTierForChat(chat, modelId);
        var tokenLimit = ResolveKnownContextTokenLimitForIdentity(
            runtime,
            modelId,
            contextTier,
            (long)(data.TokenLimit ?? 0));
        ApplyContextUsage(
            chat,
            runtime,
            currentTokens > 0 ? currentTokens : null,
            tokenLimit > 0 ? tokenLimit : null,
            ContextTokenLimitSource.Session,
            updateDisplayed,
            currentTokensAreExact: true,
            tokenLimitModelId: modelId,
            tokenLimitTier: contextTier);

        if (!updateDisplayed)
            return;

        _contextCompactionChatId = chat.Id;
        IsContextCompacting = true;
        ContextDetailsError = null;
        ContextActionStatus = Loc.Get("Chat_ContextWindow_Compacting");
        NotifyContextActionAvailabilityChanged();

        ReplaceItems(
            ContextBreakdownItems,
            CreateContextBreakdownItems(
                (long)(data.SystemTokens ?? 0),
                (long)(data.ConversationTokens ?? 0),
                (long)(data.ToolDefinitionsTokens ?? 0)));
        OnPropertyChanged(nameof(HasContextBreakdown));
    }

    private void HandleContextCompactionCompleted(
        Chat chat,
        ChatRuntimeState runtime,
        SessionCompactionCompleteData data,
        bool updateDisplayed)
    {
        var stoppedByUser = _stoppedContextCompactionChatId == chat.Id && data.Success != true;
        var currentTokens = (long)(data.PostCompactionTokens ?? 0);
        if (currentTokens <= 0)
        {
            currentTokens = (long)(data.SystemTokens ?? 0)
                + (long)(data.ConversationTokens ?? 0)
                + (long)(data.ToolDefinitionsTokens ?? 0);
        }

        var modelId = runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat);
        var contextTier = runtime.ActiveContextWindowTier
            ?? ResolveSelectedContextWindowTierForChat(chat, modelId);
        var tokenLimit = ResolveKnownContextTokenLimitForIdentity(
            runtime,
            modelId,
            contextTier,
            (long)(data.TokenLimit ?? 0));
        ApplyContextUsage(
            chat,
            runtime,
            currentTokens > 0 ? currentTokens : null,
            tokenLimit > 0 ? tokenLimit : null,
            ContextTokenLimitSource.Session,
            updateDisplayed,
            currentTokensAreExact: true,
            tokenLimitModelId: modelId,
            tokenLimitTier: contextTier);
        QueueSaveChat(chat, saveIndex: false, releaseIfInactive: CurrentChat?.Id != chat.Id);

        if (_contextCompactionChatId == chat.Id)
            CompleteManualContextCompactionTracking(chat.Id);

        if (!updateDisplayed)
            return;

        ContextLastCompactionDisplay = FormatCompactionOutcome(
            data.Success == true,
            (long?)(data.TokensRemoved ?? 0),
            (long?)(data.MessagesRemoved ?? 0),
            data.Error,
            stoppedByUser);
        ContextActionStatus = ContextLastCompactionDisplay;
        if (_stoppedContextCompactionChatId == chat.Id)
            _stoppedContextCompactionChatId = null;

        ReplaceItems(
            ContextBreakdownItems,
            CreateContextBreakdownItems(
                (long)(data.SystemTokens ?? 0),
                (long)(data.ConversationTokens ?? 0),
                (long)(data.ToolDefinitionsTokens ?? 0)));
        OnPropertyChanged(nameof(HasContextBreakdown));
    }

    internal static ContextWindowMetrics CalculateContextWindowMetrics(
        long currentTokens,
        long tokenLimit,
        long compactionThreshold)
    {
        var usagePercent = CalculateBoundedContextUsagePercent(currentTokens, tokenLimit);
        var progressPercent = usagePercent;
        var remainingTokens = tokenLimit > 0
            ? Math.Max(tokenLimit - Math.Max(currentTokens, 0), 0)
            : 0;
        var hasCompactionThreshold = compactionThreshold > 0;
        var compactionThresholdReached = hasCompactionThreshold && currentTokens >= compactionThreshold;
        var tokensUntilCompaction = hasCompactionThreshold
            ? Math.Max(compactionThreshold - Math.Max(currentTokens, 0), 0)
            : 0;

        return new ContextWindowMetrics(
            usagePercent,
            progressPercent,
            remainingTokens,
            hasCompactionThreshold,
            compactionThresholdReached,
            tokensUntilCompaction);
    }

    internal static long NormalizeRemovedContextTokens(long? tokensRemoved)
        => Math.Max(tokensRemoved ?? 0, 0);

    internal static long ResolveContextInfoPromptTokenLimit(long knownTokenLimit, long reportedTokenLimit)
        => knownTokenLimit > 0 ? knownTokenLimit : Math.Max(reportedTokenLimit, 0);

    private long ResolveKnownContextTokenLimitForIdentity(
        ChatRuntimeState runtime,
        string? modelId,
        string? contextTier,
        long reportedTokenLimit)
    {
        if (runtime.ContextTokenLimit > 0
            && string.Equals(runtime.ContextTokenLimitModelId, modelId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(runtime.ContextTokenLimitTier, contextTier, StringComparison.OrdinalIgnoreCase))
        {
            return runtime.ContextTokenLimit;
        }

        var catalogTokenLimit = ResolveKnownContextTokenLimit(modelId, contextTier);
        return catalogTokenLimit > 0 ? catalogTokenLimit : Math.Max(reportedTokenLimit, 0);
    }

    private void ApplySelectedContextTokenLimit(
        Chat chat,
        ChatRuntimeState runtime,
        string? modelId,
        string? contextTier,
        bool updateDisplayed)
    {
        var tokenLimit = ResolveKnownContextTokenLimit(modelId, contextTier);
        ApplyContextUsage(
            chat,
            runtime,
            currentTokens: null,
            tokenLimit: tokenLimit > 0 ? tokenLimit : null,
            tokenLimitSource: ContextTokenLimitSource.Catalog,
            updateDisplayed: updateDisplayed,
            tokenLimitModelId: modelId,
            tokenLimitTier: contextTier);
    }

    internal static int[] CalculateContextSharePercents(params long[] tokenCounts)
    {
        var normalized = tokenCounts.Select(tokens => Math.Max(tokens, 0)).ToArray();
        var total = normalized.Sum();
        if (total <= 0)
            return new int[normalized.Length];

        var exactShares = normalized.Select(tokens => 100.0 * tokens / total).ToArray();
        var shares = exactShares.Select(share => (int)Math.Floor(share)).ToArray();
        var remaining = 100 - shares.Sum();

        foreach (var index in exactShares
                     .Select((share, index) => new { index, remainder = share - Math.Floor(share), tokens = normalized[index] })
                     .OrderByDescending(item => item.remainder)
                     .ThenByDescending(item => item.tokens)
                     .ThenBy(item => item.index)
                     .Take(remaining)
                     .Select(item => item.index))
        {
            shares[index]++;
        }

        return shares;
    }

    private static IReadOnlyList<ContextTokenBreakdownItem> CreateContextBreakdownItems(
        long systemTokens,
        long conversationTokens,
        long toolDefinitionTokens)
    {
        var items = new List<ContextTokenBreakdownItem>();
        var shares = CalculateContextSharePercents(
            conversationTokens,
            systemTokens,
            toolDefinitionTokens);

        AddContextBreakdownItem(
            items,
            "conversation",
            Loc.Get("Chat_ContextWindow_Conversation"),
            conversationTokens,
            shares[0]);
        AddContextBreakdownItem(
            items,
            "instructions",
            Loc.Get("Chat_ContextWindow_System"),
            systemTokens,
            shares[1]);
        AddContextBreakdownItem(
            items,
            "tools",
            Loc.Get("Chat_ContextWindow_Tools"),
            toolDefinitionTokens,
            shares[2]);

        return items;
    }

    private static void AddContextBreakdownItem(
        ICollection<ContextTokenBreakdownItem> items,
        string key,
        string label,
        long tokens,
        int sharePercent)
    {
        var normalizedTokens = Math.Max(tokens, 0);

        items.Add(new ContextTokenBreakdownItem(
            key,
            label,
            normalizedTokens,
            FormatTokenCount(normalizedTokens),
            sharePercent,
            $"{sharePercent}%"));
    }

    private static void ReplaceItems<T>(ObservableCollection<T> target, IEnumerable<T> source)
    {
        target.Clear();
        foreach (var item in source)
            target.Add(item);
    }

    private static string FormatContextTier(string? tier)
    {
        if (string.Equals(tier, ModelContextWindowTiers.LongContext, StringComparison.OrdinalIgnoreCase))
            return Loc.Get("ContextWindow_Long");

        return Loc.Get("ContextWindow_Default");
    }

    internal static string FormatCompactionOutcome(
        bool success,
        long? tokensRemoved,
        long? messagesRemoved,
        string? error,
        bool stoppedByUser = false)
    {
        if (stoppedByUser && !success)
            return Loc.Get("Chat_ContextWindow_CompactionStopped");

        if (!success)
        {
            return string.IsNullOrWhiteSpace(error)
                ? Loc.Get("Chat_ContextWindow_CompactionNoResult")
                : string.Format(Loc.Get("Chat_ContextWindow_CompactionFailed"), error);
        }

        var normalizedTokensRemoved = NormalizeRemovedContextTokens(tokensRemoved);
        var normalizedMessagesRemoved = Math.Max(messagesRemoved ?? 0, 0);
        if (normalizedTokensRemoved > 0 && normalizedMessagesRemoved > 0)
        {
            return string.Format(
                Loc.Get("Chat_ContextWindow_CompactionCompleteDetailed"),
                FormatTokenCount(normalizedTokensRemoved),
                normalizedMessagesRemoved.ToString("N0"));
        }

        if (normalizedTokensRemoved > 0)
        {
            return string.Format(
                Loc.Get("Chat_ContextWindow_CompactionCompleteTokens"),
                FormatTokenCount(normalizedTokensRemoved));
        }

        return Loc.Get("Chat_ContextWindow_CompactionComplete");
    }

    private void SeedContextIdentity(Chat chat)
    {
        var runtime = GetOrCreateRuntimeState(chat.Id);
        ContextDetailsModelId = runtime.ActiveModelId ?? ResolveSelectedModelForChat(chat);
        ContextDetailsTierDisplay = FormatContextTier(runtime.ActiveContextWindowTier ?? chat.LastContextWindowTierUsed);
    }

    private void InvalidateContextForSelectionChange(
        Chat chat,
        string? modelId,
        string? contextTier)
    {
        _contextDetailsGeneration++;
        _contextDetailsCts?.Cancel();
        _contextDetailsUpdatedAt = null;
        _usesSyntheticContextDetails = false;

        var runtime = GetOrCreateRuntimeState(chat.Id);
        runtime.ContextCurrentTokens = 0;
        runtime.HasExactContextUsage = false;
        runtime.ContextTokenLimit = 0;
        runtime.ContextTokenLimitSource = ContextTokenLimitSource.Unknown;
        runtime.ContextTokenLimitModelId = null;
        runtime.ContextTokenLimitTier = null;
        chat.ContextCurrentTokens = 0;
        chat.HasExactContextUsage = false;
        chat.ContextTokenLimit = 0;
        _dataStore.MarkChatChanged(chat);

        if (CurrentChat?.Id == chat.Id)
        {
            ContextCurrentTokens = 0;
            ContextTokenLimit = 0;
            ContextDetailsModelId = modelId;
            ContextDetailsTierDisplay = FormatContextTier(contextTier);
            ContextCompactionThreshold = 0;
            ContextDetailsError = null;
            ContextActionStatus = null;
            ContextLastUpdatedDisplay = null;
            ContextBreakdownItems.Clear();
            OnPropertyChanged(nameof(HasContextBreakdown));
        }
    }

    private void InvalidateContextDetailsForSessionModelChange(
        Chat chat,
        string? modelId,
        string? contextTier)
    {
        if (CurrentChat?.Id != chat.Id)
            return;

        _contextDetailsGeneration++;
        _contextDetailsCts?.Cancel();
        _contextDetailsUpdatedAt = null;
        _usesSyntheticContextDetails = false;

        ContextDetailsModelId = modelId;
        ContextDetailsTierDisplay = FormatContextTier(contextTier);
        ContextCompactionThreshold = 0;
        ContextDetailsError = null;
        ContextActionStatus = null;
        ContextLastUpdatedDisplay = null;
        ContextBreakdownItems.Clear();
        OnPropertyChanged(nameof(HasContextBreakdown));
    }

    private void ResetContextForSessionInvalidation(Chat chat)
    {
        if (_runtimeStates.TryGetValue(chat.Id, out var runtime))
        {
            runtime.ContextCurrentTokens = 0;
            runtime.HasExactContextUsage = false;
            runtime.ContextTokenLimit = 0;
            runtime.ContextTokenLimitSource = ContextTokenLimitSource.Unknown;
            runtime.ContextTokenLimitModelId = null;
            runtime.ContextTokenLimitTier = null;
        }

        chat.ContextCurrentTokens = 0;
        chat.HasExactContextUsage = false;
        chat.ContextTokenLimit = 0;

        if (CurrentChat?.Id != chat.Id)
            return;

        _contextDetailsGeneration++;
        _contextDetailsCts?.Cancel();
        _contextDetailsUpdatedAt = null;
        _usesSyntheticContextDetails = false;
        ContextCurrentTokens = 0;
        ContextTokenLimit = 0;
        ContextCompactionThreshold = 0;
        ContextDetailsError = null;
        ContextActionStatus = null;
        ContextLastUpdatedDisplay = null;
        ContextLastCompactionDisplay = null;
        ContextBreakdownItems.Clear();
        OnPropertyChanged(nameof(HasContextBreakdown));

        var selectedModel = ResolveSelectedModelForChat(chat);
        var selectedTier = ResolveSelectedContextWindowTierForChat(chat, selectedModel);
        ContextDetailsModelId = selectedModel;
        ContextDetailsTierDisplay = FormatContextTier(selectedTier);
        if (runtime is not null)
            ApplySelectedContextTokenLimit(chat, runtime, selectedModel, selectedTier, updateDisplayed: true);
        NotifyTokenPropertiesChanged();
        NotifyContextActionAvailabilityChanged();
    }

    private void ResetContextDetailsForChatChange(Chat? chat)
    {
        _contextDetailsGeneration++;
        _contextDetailsCts?.Cancel();
        _contextDetailsUpdatedAt = null;
        _usesSyntheticContextDetails = false;
        IsContextDetailsLoading = false;
        ContextDetailsError = null;
        ContextActionStatus = null;
        ContextLastUpdatedDisplay = null;
        ContextLastCompactionDisplay = null;
        ContextCompactionThreshold = 0;
        ContextBreakdownItems.Clear();
        OnPropertyChanged(nameof(HasContextBreakdown));

        if (chat is null)
        {
            ContextDetailsModelId = null;
            ContextDetailsTierDisplay = "";
        }
        else
        {
            SeedContextIdentity(chat);
        }

        NotifyContextActionAvailabilityChanged();
    }

    private void NotifyContextUsageDerivedPropertiesChanged()
    {
        OnPropertyChanged(nameof(ContextRemainingTokens));
        OnPropertyChanged(nameof(ContextRemainingDisplay));
        OnPropertyChanged(nameof(ContextUsageDetailDisplay));
        OnPropertyChanged(nameof(ContextUsageProgress));
        OnPropertyChanged(nameof(ContextCompactionHeadroomDisplay));
        OnPropertyChanged(nameof(ContextHealthDisplay));
        OnPropertyChanged(nameof(CanCompactContext));
    }

    private void NotifyContextActionAvailabilityChanged()
    {
        OnPropertyChanged(nameof(IsContextPanelBusy));
        OnPropertyChanged(nameof(IsContextCompactingForCurrentChat));
        OnPropertyChanged(nameof(CanRefreshContextDetails));
        OnPropertyChanged(nameof(CanCompactContext));
    }

    private void CancelContextWindowOperations()
    {
        _contextDetailsCts?.Cancel();
        _contextDetailsCts?.Dispose();
        _contextDetailsCts = null;
        _contextCompactionCts?.Cancel();
        _contextCompactionCts?.Dispose();
        _contextCompactionCts = null;
        _contextCompactionCompletion?.TrySetCanceled();
        _contextCompactionCompletion = null;
        _contextCompactionChatId = null;
        _stoppedContextCompactionChatId = null;
        _manualContextCompactionStopRequested = false;
    }

#if DEBUG
    private void LoadDebugContextWindowDetails()
    {
        if (CurrentChat is not { } chat)
            return;

        SeedContextIdentity(chat);
        ContextCompactionThreshold = 110_000;

        ReplaceItems(
            ContextBreakdownItems,
            CreateContextBreakdownItems(
                systemTokens: 3_200,
                conversationTokens: 10_500,
                toolDefinitionTokens: 5_434));

        OnPropertyChanged(nameof(HasContextBreakdown));
        _contextDetailsUpdatedAt = DateTimeOffset.UtcNow;
        _usesSyntheticContextDetails = true;
        ContextLastUpdatedDisplay = Loc.Get("Chat_ContextWindow_UpdatedNow");
        ContextActionStatus = Loc.Get("Chat_ContextWindow_DebugSnapshot");
    }
#endif
}
