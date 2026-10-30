using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>Where a feature is listed on Settings → AI Features.</summary>
public enum AiFeatureGroup
{
    WhileYouChat,
    MemoryAndContext,
    WhenAsked
}

/// <summary>
/// One of Lumi's AI features on Settings → AI Features: what it does, when it runs, what it reads, whether it is on
/// and which model it uses. Its state lives in <see cref="SettingsViewModel"/>, so the switch and the model stay in
/// step with everything else that changes them (reset, the debug bridge).
/// </summary>
public sealed partial class AiFeatureViewModel : ObservableObject
{
    private readonly SettingsViewModel _owner;
    private readonly Func<bool>? _isOn;
    private readonly Action<bool>? _turnOn;

    internal AiFeatureViewModel(
        SettingsViewModel owner,
        string id,
        string textKey,
        AiFeatureGroup group,
        bool isFirstInGroup,
        Func<bool>? isOn = null,
        Action<bool>? turnOn = null)
    {
        _owner = owner;
        _isOn = isOn;
        _turnOn = turnOn;
        Id = id;
        Group = group;
        ShowSeparator = !isFirstInGroup;
        Title = Loc.Get($"AiFeature_{textKey}");
        Description = Loc.Get($"AiFeature_{textKey}Desc");
        WhenText = Loc.Get($"AiFeature_{textKey}When");
        ReadsText = Loc.Get($"AiFeature_{textKey}Reads");
        SearchText = Loc.Get($"AiFeature_{textKey}Keywords");
        ModelPickerName = Loc.Get("AiFeatures_ModelFor", Title);
    }

    /// <summary>The <see cref="AiFeatureIds"/> id, which also picks the feature's glyph.</summary>
    public string Id { get; }

    public AiFeatureGroup Group { get; }
    public string Title { get; }
    public string Description { get; }
    public string WhenText { get; }
    public string ReadsText { get; }

    /// <summary>Other words that find the feature in Settings search, such as the setting's older name.</summary>
    public string SearchText { get; }

    /// <summary>What a screen reader calls the feature's model picker ("Model for Chat Titles").</summary>
    public string ModelPickerName { get; }

    /// <summary>Rows after the first in a card draw a divider above them, as a card's own rows do.</summary>
    public bool ShowSeparator { get; }

    /// <summary>Features that run on their own can be turned off; the rest run only when someone asks for them.</summary>
    public bool CanTurnOff => _isOn is not null;

    public bool IsOn
    {
        get => _isOn?.Invoke() ?? true;
        set
        {
            if (_turnOn is not null && value != IsOn)
                _turnOn(value);
        }
    }

    /// <summary><see cref="SettingsViewModel.DefaultAiFeatureModel"/> while the feature follows the default model,
    /// otherwise the model chosen for it.</summary>
    public string ModelChoice
    {
        get => _owner.GetAiFeatureModelChoice(Id);
        set => _owner.SetAiFeatureModelChoice(Id, value);
    }

    public ObservableCollection<string> ModelChoices => _owner.AiFeatureModelChoices;
    public ObservableCollection<ModelOption> ModelOptions => _owner.AiFeatureModelOptions;
    public string[]? QualityLevels => _owner.GetFusionQualityLevels(ModelChoice);
    public string? SelectedQuality
    {
        get => _owner.GetFusionQuality(ModelChoice);
        set
        {
            if (_owner.ResolveFusionQuality(ModelChoice, value) is { } selection)
                ModelChoice = selection;
        }
    }

    /// <summary>Opening a feature's model picker refreshes the catalog, as the default model's picker does.</summary>
    public IAsyncRelayCommand RefreshModelsCommand => _owner.RefreshModelCatalogCommand;

    /// <summary>What the model picker shows: "Default · GPT 5.6 Luna", or the model chosen for the feature.</summary>
    public string ModelLabel => Services.CopilotModelCatalog.IsAutoTierSelection(ModelChoice)
        ? Loc.Get("ModelPicker_Fusion") : _owner.DescribeAiFeatureModel(Id);

    /// <summary>With "Use only your own accounts" on, a feature whose model isn't from those accounts can't run.</summary>
    public bool IsBlocked => _owner.IsAiFeatureBlocked(Id);

    public bool IsModified => (CanTurnOff && !IsOn) || ModelChoice != SettingsViewModel.DefaultAiFeatureModel;

    [RelayCommand]
    private void Revert()
    {
        IsOn = true;
        ModelChoice = SettingsViewModel.DefaultAiFeatureModel;
    }

    internal void Refresh()
    {
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(ModelChoice));
        OnPropertyChanged(nameof(ModelLabel));
        OnPropertyChanged(nameof(QualityLevels));
        OnPropertyChanged(nameof(SelectedQuality));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(IsModified));
    }
}
