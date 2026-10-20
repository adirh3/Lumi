using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>A mode shown as a choice card, in Settings and in the welcome step.</summary>
public sealed partial class ApprovalModeCard : ObservableObject
{
    public ApprovalModeCard(ApprovalModeOption option, Action<ApprovalMode> select)
    {
        Option = option;
        SelectCommand = new RelayCommand(() => select(option.Mode));
    }

    public ApprovalModeOption Option { get; }
    public ApprovalMode Mode => Option.Mode;
    public IRelayCommand SelectCommand { get; }

    [ObservableProperty] private bool _isSelected;

    public static List<ApprovalModeCard> CreateAll(IEnumerable<ApprovalModeOption> options, ApprovalMode selected, Action<ApprovalMode> select)
        => options.Select(option => new ApprovalModeCard(option, select) { IsSelected = option.Mode == selected }).ToList();
}
