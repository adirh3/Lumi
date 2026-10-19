using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// The welcome step that says plainly what Lumi can do on its own and lets the user choose. Full
/// access, how Lumi has always worked, is preselected; a replay preselects the current default.
/// </summary>
public partial class OnboardingViewModel
{
    private ApprovalMode? _autonomy;
    private List<ApprovalModeCard>? _autonomyCards;

    /// <summary>Raised when the user picks a mode, so Settings and live chats take it on at once.</summary>
    public event Action<ApprovalMode>? ApprovalModeChosen;

    public ApprovalMode Autonomy => _autonomy ??= _dataStore.Data.Settings.DefaultApprovalMode;

    public IReadOnlyList<ApprovalModeCard> AutonomyCards
        => _autonomyCards ??= ApprovalModeCard.CreateAll(ApprovalModeOption.CreateAll(), Autonomy, ChooseAutonomy);

    public bool IsFullAccessDemo => Autonomy == ApprovalMode.FullAccess;
    public bool IsAutoReviewDemo => Autonomy == ApprovalMode.AutoReview;
    public bool IsAskFirstDemo => Autonomy == ApprovalMode.AskFirst;

    public string AutonomyDemoCaption => Loc.Get(Autonomy switch
    {
        ApprovalMode.AskFirst => "Onboarding_AutonomyDemoAskFirst",
        ApprovalMode.AutoReview => "Onboarding_AutonomyDemoAutoReview",
        _ => "Onboarding_AutonomyDemoFullAccess"
    });

    public string ReadyApprovalText
        => Loc.Get("Onboarding_ReadyApproval", ApprovalModeOption.CreateAll().First(option => option.Mode == Autonomy).Title);

    private void ChooseAutonomy(ApprovalMode mode)
    {
        _autonomy = mode;
        foreach (var card in AutonomyCards)
            card.IsSelected = card.Mode == mode;
        _dataStore.Data.Settings.DefaultApprovalMode = mode;
        ApprovalModeChosen?.Invoke(mode);
        OnPropertyChanged(nameof(Autonomy));
        OnPropertyChanged(nameof(IsFullAccessDemo));
        OnPropertyChanged(nameof(IsAutoReviewDemo));
        OnPropertyChanged(nameof(IsAskFirstDemo));
        OnPropertyChanged(nameof(AutonomyDemoCaption));
        OnPropertyChanged(nameof(ReadyApprovalText));
    }
}
