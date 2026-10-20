using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Lumi.Localization;
using Lumi.Services.Sharing;

namespace Lumi.ViewModels;

public partial class PromptInputValueViewModel : ObservableObject
{
    public PromptInputValueViewModel(PromptInput input) => Input = input;

    public PromptInput Input { get; }
    public string ControlName => "PromptInput_" + Input.Key;
    public string BrowseControlName => "PromptBrowse_" + Input.Key;
    public bool CanBrowse => Input.Kind is PromptInputKind.File or PromptInputKind.Folder;
    public string Placeholder => Input.Kind == PromptInputKind.Text ? Loc.Prompt_YourValue : Loc.Prompt_YourPath;

    [ObservableProperty] private string _value = "";
}

public sealed record PromptUseRequest(string Name, string Text, IReadOnlyList<string> Attachments);

public partial class ImportSheetViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DialogTitle))]
    private bool _isPrompt;
    [ObservableProperty] private string _promptPreviewText = "";
    [ObservableProperty] private string _promptSetupNotes = "";
    [ObservableProperty] private string? _promptUseError;

    public string DialogTitle => IsPrompt ? Loc.Prompt_UseTitle : Loc.Import_Title;
    public ObservableCollection<PromptInputValueViewModel> PromptInputs { get; } = [];
    public event Action<PromptUseRequest>? PromptUseRequested;

    private void ResetPrompt()
    {
        foreach (var input in PromptInputs)
            input.PropertyChanged -= OnPromptInputChanged;
        PromptInputs.Clear();
        IsPrompt = false;
        PromptPreviewText = "";
        PromptSetupNotes = "";
        PromptUseError = null;
    }

    private void ReviewPrompt(CapabilityPack pack, string footer)
    {
        ResetPrompt();
        _pack = pack;
        var prompt = pack.Prompt!;
        IsPrompt = true;
        Card = CapabilityCardViewModel.ForPack(pack, footer);
        ErrorMessage = null;
        Items.Clear();
        Notes.Clear();
        Risk = ImportRisk.InstructionsOnly;
        RiskGlyph = "\uD83D\uDEE1";
        RiskTitle = Loc.Prompt_TrustTitle;
        RiskText = Loc.Prompt_TrustText;
        foreach (var definition in prompt.Inputs)
        {
            var input = new PromptInputValueViewModel(definition);
            input.PropertyChanged += OnPromptInputChanged;
            PromptInputs.Add(input);
        }
        PromptSetupNotes = prompt.Requirements.Count == 0
            ? Loc.Prompt_NoExtraRequirements
            : string.Join("\n", prompt.Requirements.Select(static requirement => "- " + requirement));
        ConfirmLabel = Loc.Prompt_Use;
        UpdatePersonalizedPreview();
        Stage = ImportSheetStage.Review;
    }

    private void OnPromptInputChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PromptInputValueViewModel.Value))
            UpdatePersonalizedPreview();
    }

    private void UpdatePersonalizedPreview()
    {
        if (_pack?.Prompt is not { } prompt)
            return;
        CanConfirm = PromptInputs.All(static input => !string.IsNullOrWhiteSpace(input.Value));
        PromptUseError = null;
        ReviewSummary = CanConfirm ? Loc.Prompt_NewChatDraft : Loc.Prompt_FillInputs;
        PromptPreviewText = CanConfirm
            ? PromptSharing.Personalize(prompt, PromptValues())
            : PromptSharing.ComposeText(prompt);
    }

    private Dictionary<string, string> PromptValues()
        => PromptInputs.ToDictionary(static input => input.Input.Key, static input => input.Value, StringComparer.Ordinal);

    private void UsePrompt()
    {
        if (_pack?.Prompt is not { } prompt || !CanConfirm)
            return;
        var files = new List<string>();
        foreach (var input in PromptInputs.Where(static input => input.Input.Kind == PromptInputKind.File))
        {
            var path = input.Value.Trim();
            if (!File.Exists(path))
            {
                PromptUseError = string.Format(CultureInfo.CurrentCulture, Loc.Prompt_FileUnavailable, input.Input.Label);
                return;
            }
            files.Add(path);
        }

        var request = new PromptUseRequest(prompt.Name, PromptSharing.Personalize(prompt, PromptValues()),
            files.Distinct(StringComparer.Ordinal).ToList());
        PromptUseRequested?.Invoke(request);
        IsOpen = false;
    }
}
