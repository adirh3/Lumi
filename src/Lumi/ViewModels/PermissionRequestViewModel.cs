using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services.Permissions;

namespace Lumi.ViewModels;

public enum ApprovalChoice
{
    AllowOnce,
    AllowForChat,
    AllowForProject,
    Deny
}

public enum ApprovalOutcome
{
    Pending,
    Allowed,
    AllowedForChat,
    AllowedForProject,
    Denied,
    ReviewerApproved,
    Skipped,
    Expired
}

/// <summary>One of the three modes, as the mode picker, Settings and the welcome step show it.</summary>
public sealed record ApprovalModeOption(ApprovalMode Mode, string Title, string Summary, string Detail, string Badge)
{
    public bool HasBadge => Badge.Length > 0;

    public override string ToString() => Title;

    public static IReadOnlyList<ApprovalModeOption> CreateAll() =>
    [
        new(ApprovalMode.FullAccess, Loc.Get("ApprovalMode_FullAccess"), Loc.Get("ApprovalMode_FullAccess_Summary"),
            Loc.Get("ApprovalMode_FullAccess_Detail"), Loc.Get("ApprovalMode_DefaultBadge")),
        new(ApprovalMode.AutoReview, Loc.Get("ApprovalMode_AutoReview"), Loc.Get("ApprovalMode_AutoReview_Summary"),
            Loc.Get("ApprovalMode_AutoReview_Detail"), Loc.Get("ApprovalMode_PreviewBadge")),
        new(ApprovalMode.AskFirst, Loc.Get("ApprovalMode_AskFirst"), Loc.Get("ApprovalMode_AskFirst_Summary"),
            Loc.Get("ApprovalMode_AskFirst_Detail"), ""),
    ];
}

/// <summary>
/// A native Copilot permission request waiting in one chat, shown as an approval card. The runtime
/// holds the tool call until the card is answered through <c>HandlePendingPermissionRequest</c>.
/// </summary>
public partial class PermissionRequestViewModel : ObservableObject
{
    private Func<PermissionRequestViewModel, ApprovalChoice, string?, CancellationToken, Task<bool>>? _respond;
    private readonly CancellationTokenSource _cancellation = new();

    public Guid ChatId { get; }
    public string SessionId { get; }
    public string RequestId { get; }
    public ApprovalRequestInfo Info { get; }
    public string? ProjectName { get; }

    public string Title => Info.Title;
    public string IconKey => Info.IconKey;
    public string? Subject => Info.Subject;
    public string? Intention => Info.Intention;
    public string? Detail => Info.Detail;
    public string? Diff => Info.Diff;
    public string? Warning => Info.Warning;
    public string? ReviewerReason => Info.ReviewerReason;
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
    public bool HasDiff => !string.IsNullOrWhiteSpace(Diff);
    public bool HasWarning => !string.IsNullOrWhiteSpace(Warning);
    public bool IsCommand => Info.Kind == ApprovalCardKind.Command;
    public bool IsEscalation => Info.IsEscalation;
    public bool IsAlwaysAsk => Info.IsAlwaysAsk;
    public bool CanAllow => Info.IsRecognized;
    public bool CanRemember => Info.CanRemember;
    public bool CanRememberForProject => CanRemember && ProjectName is not null;
    public string AllowForProjectShortLabel => Loc.Get("Approval_AlwaysInProject", ProjectName ?? "");
    public string? AlwaysAskNote => IsAlwaysAsk ? Loc.Get("Approval_AlwaysAskNote") : null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending))]
    [NotifyPropertyChangedFor(nameof(IsDecided))]
    [NotifyPropertyChangedFor(nameof(ReceiptText))]
    [NotifyPropertyChangedFor(nameof(CardStatus))]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendFeedbackCommand))]
    private ApprovalOutcome _outcome;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AllowOnceCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForChatCommand))]
    [NotifyCanExecuteChangedFor(nameof(AllowForProjectCommand))]
    [NotifyCanExecuteChangedFor(nameof(DenyCommand))]
    [NotifyCanExecuteChangedFor(nameof(SendFeedbackCommand))]
    private bool _isResponding;

    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _isFeedbackOpen;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendFeedbackCommand))]
    private string _feedback = "";

    public bool IsPending => Outcome == ApprovalOutcome.Pending;
    public bool IsDecided => !IsPending;

    public StrataTheme.Controls.StrataApprovalStatus CardStatus => Outcome switch
    {
        ApprovalOutcome.Pending => StrataTheme.Controls.StrataApprovalStatus.Pending,
        ApprovalOutcome.Denied => StrataTheme.Controls.StrataApprovalStatus.Denied,
        ApprovalOutcome.Skipped => StrataTheme.Controls.StrataApprovalStatus.Skipped,
        ApprovalOutcome.Expired => StrataTheme.Controls.StrataApprovalStatus.Expired,
        _ => StrataTheme.Controls.StrataApprovalStatus.Approved
    };

    /// <summary>The pill while it waits: an Always-ask rule says so, everything else just needs the user.</summary>
    public string StatusText => Loc.Get(IsAlwaysAsk ? "Approval_StatusAlwaysAsk" : "Approval_StatusNeedsYou");

    /// <summary>A sandbox warning wins; an Always-ask request explains why Lumi asked even in Full access.</summary>
    public string? CardWarning => HasWarning ? Warning : AlwaysAskNote;

    public bool HasAnyDetail => HasDiff || HasDetail;

    /// <summary>What remembering covers, stated plainly under the choices.</summary>
    public string? RememberHint => !CanRemember ? null
        : Loc.Get(Info.Kind == ApprovalCardKind.Command ? "Approval_RememberHintCommand" : "Approval_RememberHint",
            Info.RememberScope ?? "");
    public bool HasRememberHint => RememberHint is not null;

    public string? ReceiptText => Outcome switch
    {
        ApprovalOutcome.Allowed => Loc.Get("Approval_Receipt_Allowed"),
        ApprovalOutcome.AllowedForChat => Loc.Get("Approval_Receipt_AllowedForChat"),
        ApprovalOutcome.AllowedForProject => Loc.Get("Approval_Receipt_AllowedForProject", ProjectName ?? ""),
        ApprovalOutcome.Denied => Loc.Get("Approval_Receipt_Denied"),
        ApprovalOutcome.ReviewerApproved => Loc.Get("Approval_Receipt_Reviewer"),
        ApprovalOutcome.Skipped => Loc.Get("Approval_Receipt_Skipped"),
        ApprovalOutcome.Expired => Loc.Get("Approval_Receipt_Expired"),
        _ => null
    };

    internal PermissionRequestViewModel(
        Guid chatId, string sessionId, string requestId, ApprovalRequestInfo info, string? projectName,
        Func<PermissionRequestViewModel, ApprovalChoice, string?, CancellationToken, Task<bool>> respond)
    {
        ChatId = chatId;
        SessionId = sessionId;
        RequestId = requestId;
        Info = info;
        ProjectName = projectName;
        _respond = respond;
    }

    private bool CanAllowNow() => IsPending && !IsResponding && CanAllow;
    private bool CanRememberNow() => CanAllowNow() && CanRemember;
    private bool CanRememberForProjectNow() => CanAllowNow() && CanRememberForProject;
    private bool CanDenyNow() => IsPending && !IsResponding;
    private bool CanSendFeedback() => CanDenyNow() && !string.IsNullOrWhiteSpace(Feedback);

    [RelayCommand(CanExecute = nameof(CanAllowNow))]
    private Task AllowOnce() => RespondAsync(ApprovalChoice.AllowOnce, null);

    [RelayCommand(CanExecute = nameof(CanRememberNow))]
    private Task AllowForChat() => RespondAsync(ApprovalChoice.AllowForChat, null);

    [RelayCommand(CanExecute = nameof(CanRememberForProjectNow))]
    private Task AllowForProject() => RespondAsync(ApprovalChoice.AllowForProject, null);

    [RelayCommand(CanExecute = nameof(CanDenyNow))]
    private Task Deny() => RespondAsync(ApprovalChoice.Deny, null);

    [RelayCommand]
    private void OpenFeedback() => IsFeedbackOpen = IsPending;

    [RelayCommand]
    private void CancelFeedback()
    {
        IsFeedbackOpen = false;
        Feedback = "";
    }

    [RelayCommand(CanExecute = nameof(CanSendFeedback))]
    private Task SendFeedback() => RespondAsync(ApprovalChoice.Deny, Feedback.Trim());

    internal async Task RespondAsync(ApprovalChoice choice, string? feedback)
    {
        if (!IsPending || IsResponding || _respond is not { } respond)
            return;
        if (choice != ApprovalChoice.Deny && !CanAllow)
            return;
        IsResponding = true;
        Error = null;
        try
        {
            var applied = await respond(this, choice, feedback, _cancellation.Token);
            if (!applied)
                Trace.TraceInformation("[Approvals] Request {0} in session {1} was already settled.", RequestId, SessionId);
            Settle(applied ? choice switch
            {
                ApprovalChoice.AllowForChat => ApprovalOutcome.AllowedForChat,
                ApprovalChoice.AllowForProject => ApprovalOutcome.AllowedForProject,
                ApprovalChoice.Deny => ApprovalOutcome.Denied,
                _ => ApprovalOutcome.Allowed
            } : ApprovalOutcome.Expired);
        }
        catch (OperationCanceledException) when (!IsPending)
        {
        }
        catch (Exception error)
        {
            Trace.TraceWarning("[Approvals] Answering {0} in {1} failed: {2}", RequestId, SessionId, error);
            if (IsPending)
                Error = Loc.Get("Approval_ResponseFailed", error.Message);
        }
        finally
        {
            IsResponding = false;
        }
    }

    /// <summary>Ends the request; the card stays as a receipt of what happened.</summary>
    internal void Settle(ApprovalOutcome outcome)
    {
        if (!IsPending)
            return;
        IsFeedbackOpen = false;
        Outcome = outcome == ApprovalOutcome.Pending ? ApprovalOutcome.Expired : outcome;
        _respond = null;
        _cancellation.Cancel();
        _cancellation.Dispose();
    }
}
