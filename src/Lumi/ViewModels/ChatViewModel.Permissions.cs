using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.Input;
using GitHub.Copilot;
using GitHub.Copilot.Rpc;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Permissions;
using Chat = Lumi.Models.Chat;

namespace Lumi.ViewModels;

/// <summary>Sends a decision on one pending request to the runtime; false when it no longer applies there.</summary>
internal delegate Task<bool> NativeApprovalDecider(
    PermissionDecision decision, PermissionDecisionContext context, CancellationToken cancellationToken);

/// <summary>
/// Tool approvals. Copilot enforces every decision through its native permission modes, rules and
/// pending requests; this partial chooses them for each chat, re-applies them whenever a session is
/// created or resumed (the runtime keeps neither rules nor session approvals across a resume), and
/// shows the requests that need a person as approval cards in the chat that owns them.
/// </summary>
public partial class ChatViewModel
{
    private readonly Dictionary<(Guid ChatId, string SessionId, string RequestId), PermissionRequestViewModel> _permissionRequests = [];
    private readonly HashSet<(Guid ChatId, string SessionId, string RequestId)> _retiredPermissionRequests = [];
    private readonly Dictionary<Guid, string> _sessionAskLayers = [];
    private readonly HashSet<Guid> _unattendedTurns = [];
    private ApprovalMode? _draftApprovalMode;

    public IReadOnlyList<ApprovalModeOption> ApprovalModeOptions { get; } = ApprovalModeOption.CreateAll();
    public ObservableCollection<PermissionRequestViewModel> PendingPermissionRequests { get; } = [];
    public bool HasPendingPermissionRequests => PendingPermissionRequests.Count > 0;
    public bool HasSeveralPendingPermissionRequests => PendingPermissionRequests.Count > 1;

    public string ApprovalsWaitingText => PendingPermissionRequests.Count > 1
        ? Loc.Get("Approval_WaitingMany", PendingPermissionRequests.Count)
        : Loc.Get("Approval_WaitingOne");

    public string AllowAllApprovalsLabel => Loc.Get("Approval_AllowAll", PendingPermissionRequests.Count);

    /// <summary>The cards wait at the end of the chat.</summary>
    [RelayCommand]
    private void ReviewPendingApprovals() => ScrollToEndRequested?.Invoke();

    private Project? ApprovalProject(Chat? chat)
    {
        var projectId = chat is null ? _pendingProjectId ?? ActiveProjectFilterId : chat.ProjectId;
        return projectId is { } id ? _dataStore.Data.Projects.FirstOrDefault(project => project.Id == id) : null;
    }

    private (ApprovalMode Mode, ApprovalModeSource Source) ResolveApprovalMode(Chat? chat)
    {
        if (chat is null && _draftApprovalMode is { } draft)
            return (draft, ApprovalModeSource.Chat);
        return ApprovalPolicy.ResolveMode(chat, ApprovalProject(chat), _dataStore.Data.Settings);
    }

    public ApprovalMode CurrentApprovalMode => ResolveApprovalMode(CurrentChat).Mode;
    public ApprovalModeOption CurrentApprovalModeOption
        => ApprovalModeOptions.First(option => option.Mode == CurrentApprovalMode);
    public bool IsFullAccessMode => CurrentApprovalMode == ApprovalMode.FullAccess;

    public StrataTheme.Controls.StrataModeTone ApprovalModeTone => CurrentApprovalMode switch
    {
        ApprovalMode.AskFirst => StrataTheme.Controls.StrataModeTone.Caution,
        ApprovalMode.AutoReview => StrataTheme.Controls.StrataModeTone.Accent,
        _ => StrataTheme.Controls.StrataModeTone.Neutral
    };

    public string ApprovalModeToolTip => Loc.Get("ApprovalMode_ToolTip", CurrentApprovalModeOption.Title);

    /// <summary>Where the chat's mode comes from, as the mode picker explains it.</summary>
    public string ApprovalModeOrigin
    {
        get
        {
            var (_, source) = ResolveApprovalMode(CurrentChat);
            return source switch
            {
                ApprovalModeSource.Chat => Loc.Get("ApprovalMode_Origin_Chat"),
                ApprovalModeSource.Project => Loc.Get("ApprovalMode_Origin_Project", ApprovalProject(CurrentChat)?.Name ?? ""),
                _ => Loc.Get("ApprovalMode_Origin_Default")
            };
        }
    }

    public bool HasOwnApprovalMode => ResolveApprovalMode(CurrentChat).Source == ApprovalModeSource.Chat;

    public int ApprovalRuleCount
        => ApprovalPolicy.EffectiveRules(_dataStore.Data.Settings, ApprovalProject(CurrentChat), CurrentChat).Count;

    public string ApprovalRulesSummary => ApprovalRuleCount switch
    {
        0 => Loc.Get("ApprovalMode_NoRules"),
        1 => Loc.Get("ApprovalMode_OneRule"),
        var count => Loc.Get("ApprovalMode_Rules", count)
    };

    /// <summary>Raised when the mode picker asks to open Settings → Permissions.</summary>
    public event Action? ManageApprovalsRequested;

    /// <summary>A card remembered an approval: every open chat and the Settings and Projects lists take it on.</summary>
    public event Action? ApprovalsRemembered;

    [RelayCommand]
    private void ManageApprovals() => ManageApprovalsRequested?.Invoke();

    [RelayCommand]
    private void SelectApprovalMode(ApprovalModeOption? option)
    {
        if (option is null)
            return;
        if (CurrentChat is { } chat)
        {
            chat.ApprovalMode = option.Mode;
            QueueSaveChatIndex(chat);
            _ = ApplyApprovalPolicyAsync(chat);
        }
        else
            _draftApprovalMode = option.Mode;
        NotifyPermissionPropertiesChanged();
    }

    /// <summary>Lets the chat follow its project or the default again.</summary>
    [RelayCommand]
    private void FollowDefaultApprovalMode()
    {
        if (CurrentChat is { } chat)
        {
            chat.ApprovalMode = null;
            QueueSaveChatIndex(chat);
            _ = ApplyApprovalPolicyAsync(chat);
        }
        else
            _draftApprovalMode = null;
        NotifyPermissionPropertiesChanged();
    }

    internal void NotifyPermissionPropertiesChanged()
    {
        OnPropertyChanged(nameof(CurrentApprovalMode));
        OnPropertyChanged(nameof(CurrentApprovalModeOption));
        OnPropertyChanged(nameof(IsFullAccessMode));
        OnPropertyChanged(nameof(ApprovalModeTone));
        OnPropertyChanged(nameof(ApprovalModeToolTip));
        OnPropertyChanged(nameof(ApprovalModeOrigin));
        OnPropertyChanged(nameof(HasOwnApprovalMode));
        OnPropertyChanged(nameof(ApprovalRuleCount));
        OnPropertyChanged(nameof(ApprovalRulesSummary));
    }

    /// <summary>Settings or a project's approvals changed: every live session takes them on.</summary>
    internal void OnApprovalPolicyChanged()
    {
        NotifyPermissionPropertiesChanged();
        foreach (var chatId in _sessionCache.Keys.ToList())
            if (_dataStore.Data.Chats.Find(chat => chat.Id == chatId) is { } chat)
                _ = ApplyApprovalPolicyAsync(chat);
    }

    // ── Native application ──────────────────────────────

    private Func<string, string> ConnectorKeys(Guid chatId)
        => _sessionMcpPlans.TryGetValue(chatId, out var plan) ? plan.ResolveRuntimeKey : static name => name;

    private Func<string, string> ConnectorNames(Guid chatId)
        => _activeMcpDisplayNames.TryGetValue(chatId, out var names)
            ? key => names.TryGetValue(key, out var name) ? name : key
            : static key => key;

    private List<ApprovalRule> EffectiveApprovalRules(Chat chat)
        => ApprovalPolicy.EffectiveRules(_dataStore.Data.Settings, ApprovalProject(chat), chat);

    /// <summary>The native Ask layer for a session about to start. It is fixed for the session's life.</summary>
    private ManagedSettings? BuildApprovalAskLayer(Chat chat, McpSessionPlan plan)
        => ApprovalPolicy.BuildAskLayer(EffectiveApprovalRules(chat), plan.ResolveRuntimeKey);

    private Func<PermissionRequest, PermissionInvocation, Task<PermissionDecision>> BuildApprovalHandler(Chat chat)
        => SessionConfigBuilder.BuildPermissionHandler(() => ResolveApprovalMode(chat).Mode);

    /// <summary>
    /// Installs the chat's mode and rules on a session. A stricter mode that cannot be installed
    /// fails the session rather than let it act with more freedom than the user chose.
    /// </summary>
    internal async Task PrepareSessionPermissionsAsync(CopilotSession session, Chat chat, ManagedSettings? askLayer, CancellationToken ct)
    {
        try
        {
            var bridge = await session.Rpc.Permissions.SetRequiredAsync(true, ct);
            if (!bridge.Success)
                throw new InvalidOperationException(Loc.Get("Approval_PolicyNotApplied"));
            await ApplyNativeApprovalPolicyAsync(session, chat, ct);
            _sessionAskLayers[chat.Id] = ApprovalPolicy.AskLayerSignature(askLayer);
        }
        catch
        {
            // Never cache a session whose requested policy could not be installed.
            _ = TrackSessionRelease(chat.Id, session);
            throw;
        }
    }

    private async Task ApplyNativeApprovalPolicyAsync(CopilotSession session, Chat chat, CancellationToken ct)
    {
        var mode = ResolveApprovalMode(chat).Mode;
        var native = ApprovalPolicy.ToNativeMode(mode, _dataStore.Data.Settings.UseBYOKOnly);
        var result = await session.Rpc.Permissions.SetModeAsync(native, cancellationToken: ct);
        if (!result.Success || result.Mode != native)
        {
            // Full access keeps working through the approving handler, exactly as before modes existed.
            // Auto-review without its reviewer (disabled by policy, or a runtime without it) asks instead.
            var acceptable = mode == ApprovalMode.FullAccess
                || (mode == ApprovalMode.AutoReview && result.Mode == PermissionMode.Manual);
            if (!acceptable)
                throw new InvalidOperationException(Loc.Get("Approval_PolicyNotApplied"));
            Trace.TraceInformation("[Approvals] {0} ran as {1} in chat {2}.", native.Value, result.Mode.Value, chat.Id);
        }

        // "Allow for this chat" answers live apart from the rules. Each is also saved as a rule and
        // installed below, so clearing them first is what makes a forgotten one stop applying.
        var reset = await session.Rpc.Permissions.ResetSessionApprovalsAsync(includeLocation: false, ct);
        if (!reset.Success)
            throw new InvalidOperationException(Loc.Get("Approval_PolicyNotApplied"));

        var rules = ApprovalPolicy.BuildSessionRules(EffectiveApprovalRules(chat), ConnectorKeys(chat.Id));
        var configured = await session.Rpc.Permissions.ConfigureAsync(
            approveAllToolPermissionRequests: mode == ApprovalMode.FullAccess ? null : false,
            // Copilot's own apps read files without asking in every mode; Lumi matches them.
            approveAllReadPermissionRequests: true,
            rules: rules,
            cancellationToken: ct);
        if (!configured.Success)
            throw new InvalidOperationException(Loc.Get("Approval_PolicyNotApplied"));
    }

    /// <summary>Applies a changed mode or rule to the chat's live session, if it has one.</summary>
    internal async Task ApplyApprovalPolicyAsync(Chat chat)
    {
        NotifyPermissionPropertiesChanged();
        if (!_sessionCache.TryGetValue(chat.Id, out var session))
            return;
        try
        {
            await ApplyNativeApprovalPolicyAsync(session, chat, CancellationToken.None);
        }
        catch (Exception error) when (!_isDisposed)
        {
            Trace.TraceWarning("[Approvals] Live update failed for chat {0}: {1}", chat.Id, error.Message);
            // The next turn rebuilds the session with the policy instead.
            ReleaseSessionResources(chat.Id, cancelActiveRequest: false);
            return;
        }

        // Always-ask rules are part of the session's startup layer, so a change rebuilds it.
        var askLayer = _sessionMcpPlans.TryGetValue(chat.Id, out var plan) ? BuildApprovalAskLayer(chat, plan) : null;
        if (_sessionAskLayers.TryGetValue(chat.Id, out var current)
            && !string.Equals(current, ApprovalPolicy.AskLayerSignature(askLayer), StringComparison.Ordinal))
            InvalidateSessionConfiguration(chat);
    }

    // ── Requests ────────────────────────────────────────

    private bool HasPendingPermissionRequest(Guid chatId)
        => _permissionRequests.Values.Any(request => request.ChatId == chatId && request.IsPending);

    private void RefreshPermissionRequests(bool showInTranscript = true)
    {
        var displayed = _permissionRequests.Values
            .Where(request => request.ChatId == CurrentChat?.Id && request.IsPending).ToList();
        for (var index = PendingPermissionRequests.Count - 1; index >= 0; index--)
            if (!displayed.Contains(PendingPermissionRequests[index]))
                PendingPermissionRequests.RemoveAt(index);
        foreach (var request in displayed)
            if (!PendingPermissionRequests.Contains(request))
            {
                PendingPermissionRequests.Add(request);
                if (showInTranscript)
                    _transcriptBuilder.AddApprovalToTranscript(request);
            }
        OnPropertyChanged(nameof(HasPendingPermissionRequests));
        OnPropertyChanged(nameof(HasSeveralPendingPermissionRequests));
        OnPropertyChanged(nameof(ApprovalsWaitingText));
        OnPropertyChanged(nameof(AllowAllApprovalsLabel));
    }

    /// <summary>Puts the chat's waiting requests back into its transcript after the transcript is rebuilt.</summary>
    private void RestoreApprovalCardsToTranscript()
    {
        RefreshPermissionRequests(showInTranscript: false);
        foreach (var request in PendingPermissionRequests)
            _transcriptBuilder.AddApprovalToTranscript(request);
    }

    private bool IsPermissionSessionCurrent(Guid chatId, CopilotSession session)
        => !_isDisposed && _sessionCache.TryGetValue(chatId, out var current) && ReferenceEquals(current, session);

    private static PermissionDecisionContext DecisionContext(
        PermissionDecisionSource source, PermissionDecisionOutcome outcome, PermissionResponseCapability capability) => new()
    {
        Source = source,
        Outcome = outcome,
        ResponseCapability = capability,
        Surface = PermissionDecisionSurface.Sdk
    };

    /// <summary>A native <c>permission.requested</c> event for the chat's live session.</summary>
    private void PresentNativePermissionRequest(Chat chat, CopilotSession session, PermissionRequestedData data)
    {
        if (data.ResolvedByHook == true || !IsPermissionSessionCurrent(chat.Id, session))
            return;
        var decide = NativeDecider(chat, session, data.RequestId);
        var managed = data.PermissionRequest?.ManagedApprovalRequired == true;
        if (data.PermissionRequest is { } request && !SessionConfigBuilder.NeedsPerson(ResolveApprovalMode(chat).Mode, request))
        {
            // The handler approved it. If the mode loosened after the handler declined, this settles it.
            _ = decide(PermissionDecision.ApproveOnce(),
                DecisionContext(PermissionDecisionSource.HostPolicy, PermissionDecisionOutcome.AutoApproved, PermissionResponseCapability.Interactive),
                CancellationToken.None);
            return;
        }

        if (data.PromptRequest is { } prompt)
            PresentPermissionRequest(chat, session.SessionId, data.RequestId, prompt, managed, decide);
    }

    /// <summary>Answers one pending request in its own session: false when that session moved on or the runtime no longer has it.</summary>
    private NativeApprovalDecider NativeDecider(Chat chat, CopilotSession session, string requestId)
        => async (decision, context, ct) => IsPermissionSessionCurrent(chat.Id, session)
            && (await session.Rpc.Permissions.HandlePendingPermissionRequestAsync(requestId, decision, context, ct)).Success;

    /// <summary>
    /// A request that needs a decision, in the chat that owns it. Auto-review settles what the reviewer
    /// approved, an unattended turn may skip it, and anything else becomes a card the user answers.
    /// </summary>
    internal PermissionRequestViewModel? PresentPermissionRequest(
        Chat chat, string sessionId, string requestId, PermissionPromptRequest prompt, bool managed, NativeApprovalDecider decide)
    {
        var key = (chat.Id, sessionId, requestId);
        if (_isDisposed || _retiredPermissionRequests.Contains(key) || _permissionRequests.ContainsKey(key))
            return null;

        var info = ApprovalRequestReader.Read(prompt, managed, ConnectorNames(chat.Id));
        var mode = ResolveApprovalMode(chat).Mode;
        if (mode == ApprovalMode.AutoReview && info is { IsAlwaysAsk: false, IsEscalation: false, IsRecognized: true }
            && info.Recommendation == AssistedApprovalRecommendation.Approve)
        {
            _retiredPermissionRequests.Add(key);
            _ = decide(PermissionDecision.ApproveOnce(),
                DecisionContext(PermissionDecisionSource.AssistedApproval, PermissionDecisionOutcome.AutoApproved, PermissionResponseCapability.Interactive),
                CancellationToken.None);
            AddApprovalReceipt(chat, sessionId, requestId, info, ApprovalOutcome.ReviewerApproved);
            return null;
        }

        if (_unattendedTurns.Contains(chat.Id) && _dataStore.Data.Settings.AwayApprovalBehavior == AwayApprovalBehavior.Skip)
        {
            _retiredPermissionRequests.Add(key);
            _ = decide(PermissionDecision.UserNotAvailable(),
                DecisionContext(PermissionDecisionSource.UnattendedFallback, PermissionDecisionOutcome.AutopilotDenied, PermissionResponseCapability.Headless),
                CancellationToken.None);
            AddApprovalReceipt(chat, sessionId, requestId, info, ApprovalOutcome.Skipped);
            return null;
        }

        var card = new PermissionRequestViewModel(chat.Id, sessionId, requestId, info, ApprovalProject(chat)?.Name,
            (vm, choice, feedback, ct) => RespondToApprovalAsync(chat, decide, vm, choice, feedback, ct));
        _permissionRequests.Add(key, card);
        RefreshInputWaitingState(chat);
        RefreshPermissionRequests();
        NotifyQuestionAsked(chat.Id, info.Title + (info.Subject is { Length: > 0 } subject ? " · " + subject : ""));
        ChatUpdated?.Invoke();
        return card;
    }

    /// <summary>Shows a decision Lumi made without the user as a settled card, so nothing happens unseen.</summary>
    private void AddApprovalReceipt(Chat chat, string sessionId, string requestId, ApprovalRequestInfo info, ApprovalOutcome outcome)
    {
        if (CurrentChat?.Id != chat.Id)
            return;
        var receipt = new PermissionRequestViewModel(chat.Id, sessionId, requestId, info, null,
            static (_, _, _, _) => Task.FromResult(false));
        receipt.Settle(outcome);
        _transcriptBuilder.AddApprovalToTranscript(receipt);
    }

    private async Task<bool> RespondToApprovalAsync(
        Chat chat, NativeApprovalDecider decide, PermissionRequestViewModel card, ApprovalChoice choice, string? feedback, CancellationToken ct)
    {
        if (!_permissionRequests.ContainsKey((chat.Id, card.SessionId, card.RequestId)))
            return false;

        _unattendedTurns.Remove(chat.Id);
        var info = card.Info;
        var remember = choice is ApprovalChoice.AllowForChat or ApprovalChoice.AllowForProject && info.CanRemember;
        PermissionDecision decision = choice switch
        {
            ApprovalChoice.Deny => PermissionDecision.Reject(string.IsNullOrWhiteSpace(feedback)
                ? "The user denied this permission request." : feedback),
            _ when remember => new PermissionDecisionApproveForSession { Approval = info.SessionApproval },
            _ => new PermissionDecisionApproveOnce { ApprovedInteractively = true }
        };

        var applied = await decide(decision,
            DecisionContext(PermissionDecisionSource.HumanResponse, PermissionDecisionOutcome.PromptedUser, PermissionResponseCapability.Interactive),
            ct);
        // Saved only once the runtime took the answer, so a failed or late one never leaves a standing rule.
        if (applied && remember)
            Remember(chat, info.RememberRules, choice == ApprovalChoice.AllowForProject);
        CompletePermissionRequest(chat.Id, card.SessionId, card.RequestId, !applied ? ApprovalOutcome.Expired : choice switch
        {
            ApprovalChoice.Deny => ApprovalOutcome.Denied,
            ApprovalChoice.AllowForChat => ApprovalOutcome.AllowedForChat,
            ApprovalChoice.AllowForProject => ApprovalOutcome.AllowedForProject,
            _ => ApprovalOutcome.Allowed
        });
        if (applied && remember)
            ApprovalsRemembered?.Invoke();
        return applied;
    }

    /// <summary>Saves the rules so the approval outlives this session: the runtime forgets it on resume.</summary>
    private void Remember(Chat chat, IReadOnlyList<ApprovalRule> rules, bool forProject)
    {
        var project = forProject ? ApprovalProject(chat) : null;
        var target = project?.ApprovalRules ?? chat.ApprovalRules;
        foreach (var rule in rules)
            if (!target.Any(existing => ApprovalPolicy.IsSameRule(existing, rule)))
                target.Add(rule);
        if (project is not null)
            _ = _dataStore.SaveAsync();
        else
            QueueSaveChatIndex(chat);
    }

    [RelayCommand]
    private async Task AllowAllPendingApprovals()
    {
        foreach (var card in PendingPermissionRequests.Where(card => card.CanAllow).ToList())
            await card.RespondAsync(ApprovalChoice.AllowOnce, null);
    }

    internal void CompletePermissionRequest(Guid chatId, string sessionId, string requestId, ApprovalOutcome outcome = ApprovalOutcome.Expired)
    {
        var key = (chatId, sessionId, requestId);
        _retiredPermissionRequests.Add(key);
        if (_permissionRequests.Remove(key, out var request))
            request.Settle(outcome);
        if ((_dataStore.Data.Chats.Find(chat => chat.Id == chatId)
             ?? (CurrentChat?.Id == chatId ? CurrentChat : null)) is { } chat)
            RefreshInputWaitingState(chat);
        RefreshPermissionRequests();
        ScheduleQueuedBusySendDrain(chatId);
        OnPropertyChanged(nameof(IsSessionActive));
    }

    /// <summary>The runtime settled a request: answered here, by another client, or cancelled by a stop.</summary>
    private void OnNativePermissionCompleted(Chat chat, CopilotSession session, PermissionCompletedData data)
    {
        if (!IsPermissionSessionCurrent(chat.Id, session)
            || !_permissionRequests.TryGetValue((chat.Id, session.SessionId, data.RequestId), out var card)
            || card.IsResponding)
            return;
        CompletePermissionRequest(chat.Id, session.SessionId, data.RequestId, data.Result switch
        {
            PermissionResultApproved or PermissionResultApprovedForLocation or PermissionResultApprovedReadOnlyForSession => ApprovalOutcome.Allowed,
            PermissionResultApprovedForSession => ApprovalOutcome.AllowedForChat,
            PermissionResultDeniedInteractivelyByUser or PermissionResultDeniedByRules
                or PermissionResultDeniedByPermissionRequestHook or PermissionResultDeniedByContentExclusionPolicy => ApprovalOutcome.Denied,
            PermissionResultDeniedNoApprovalRuleAndCouldNotRequestFromUser => ApprovalOutcome.Skipped,
            _ => ApprovalOutcome.Expired
        });
    }

    internal void CancelPermissionRequests(Guid chatId)
    {
        foreach (var request in _permissionRequests.Values.Where(request => request.ChatId == chatId).ToList())
            CompletePermissionRequest(chatId, request.SessionId, request.RequestId);
    }

    /// <summary>Requests a resumed session was still waiting on, rebuilt from its own event history.</summary>
    private async Task RestorePendingPermissionsAsync(CopilotSession session, Chat chat, CancellationToken ct)
    {
        try
        {
            var pending = await session.Rpc.Permissions.PendingRequestsAsync(ct);
            if (!IsPermissionSessionCurrent(chat.Id, session))
                return;
            foreach (var request in pending.Items)
                PresentPermissionRequest(chat, session.SessionId, request.RequestId, request.Request, managed: false,
                    NativeDecider(chat, session, request.RequestId));
        }
        catch
        {
            ReleaseSessionResources(chat.Id, cancelActiveRequest: true);
            throw;
        }
    }

    /// <summary>A job or another chat is driving this turn, so nobody may be there to answer.</summary>
    internal void MarkTurnUnattended(Guid chatId) => _unattendedTurns.Add(chatId);

    internal void MarkTurnAttended(Guid chatId) => _unattendedTurns.Remove(chatId);
}
