using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Lumi.Localization;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

/// <summary>
/// Settings → AI Features: every way Lumi uses AI on its own, in one place. Each feature says what it does, when it
/// runs and what it reads; the ones that run by themselves can be turned off, and each can run on a model of its
/// own instead of the default model (<see cref="BackgroundModel"/>).
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>AI Features (listed after AI &amp; Models). The next free index, as Permissions took 9.</summary>
    public const int AiFeaturesPageIndex = 10;

    /// <summary>A feature's model choice while it follows the default model.</summary>
    public const string DefaultAiFeatureModel = "default";

    // What can change a feature's switch, model or warning, or the page's summary.
    private static readonly HashSet<string> AiFeatureSources =
    [
        nameof(AutoGenerateTitles), nameof(SuggestFollowUps), nameof(EnableMemoryAutoSave), nameof(EnableDailyBriefs),
        nameof(BackgroundModelStatus), nameof(DefaultApprovalMode)
    ];

    private AiFeatureViewModel[]? _aiFeatures;
    private IReadOnlyList<string> _aiFeatureCatalogModels = [];

    public IReadOnlyList<AiFeatureViewModel> AiFeatures => _aiFeatures ??= CreateAiFeatures();
    public IReadOnlyList<AiFeatureViewModel> ChatAiFeatures => InGroup(AiFeatureGroup.WhileYouChat);
    public IReadOnlyList<AiFeatureViewModel> ContextAiFeatures => InGroup(AiFeatureGroup.MemoryAndContext);
    public IReadOnlyList<AiFeatureViewModel> RequestedAiFeatures => InGroup(AiFeatureGroup.WhenAsked);

    /// <summary>What a feature's model picker offers: the default model, then every model the default can be.</summary>
    public ObservableCollection<string> AiFeatureModelChoices { get; } = [];
    public ObservableCollection<ModelOption> AiFeatureModelOptions { get; } = [];

    public string AiFeaturesSummary
    {
        get
        {
            var (on, all) = CountAiFeaturesOn();
            return on == all ? Loc.Get("AiFeatures_SummaryAllOn", all)
                : on == 0 ? Loc.Get("AiFeatures_SummaryAllOff")
                : Loc.Get("AiFeatures_SummarySome", on, all);
        }
    }

    public string AiFeaturesSummaryDetail => AreAllAiFeaturesOff
        ? Loc.Get("AiFeatures_SummaryDetailOff")
        : Loc.Get("AiFeatures_SummaryDetail", DescribeHelperModel(BackgroundModel));

    public bool AreAllAiFeaturesOff => CountAiFeaturesOn().On == 0;

    public string ToggleAllAiFeaturesLabel => Loc.Get(AreAllAiFeaturesOff ? "AiFeatures_TurnAllOn" : "AiFeatures_TurnAllOff");

    /// <summary>The approval mode chats use unless a project or chat picks another; Auto-review is the one that uses AI.</summary>
    public string AutoReviewStatus => Loc.Get("AiFeatures_AutoReviewStatus",
        ApprovalModeOptions.FirstOrDefault(option => option.Mode == DefaultApprovalMode)?.Title ?? "");

    public bool IsAutoReviewDefault => DefaultApprovalMode == ApprovalMode.AutoReview;

    public bool IsDictationAvailable => VoiceInputService.IsSupportedOnThisComputer;

    [RelayCommand]
    private void ToggleAllAiFeatures()
    {
        var turnOn = AreAllAiFeaturesOff;
        foreach (var feature in AiFeatures.Where(static feature => feature.CanTurnOff))
            feature.IsOn = turnOn;
    }

    [RelayCommand]
    private void OpenAiFeatures() => SelectedPageIndex = AiFeaturesPageIndex;

    [RelayCommand]
    private void OpenPermissionsPage() => SelectedPageIndex = PermissionsPageIndex;

    internal string GetAiFeatureModelChoice(string feature)
        => _dataStore.Data.Settings.GetAiFeatureModel(feature) ?? DefaultAiFeatureModel;

    internal void SetAiFeatureModelChoice(string feature, string? choice)
    {
        if (choice is null)
            return;

        var settings = _dataStore.Data.Settings;
        var model = string.IsNullOrWhiteSpace(choice) || choice == DefaultAiFeatureModel ? null : choice.Trim();
        if (settings.GetAiFeatureModel(feature) == model)
            return;

        // Helper sessions read the choices off the UI thread, so a change swaps in a new dictionary.
        var models = new Dictionary<string, string>(settings.AiFeatureModels);
        if (model is null)
            models.Remove(feature);
        else
            models[feature] = model;
        settings.AiFeatureModels = models;
        SyncAiFeatureModelChoices();
        Save();
        RefreshAiFeatures();
    }

    private void ResetAiFeatureModels()
    {
        if (_dataStore.Data.Settings.AiFeatureModels.Count == 0)
            return;

        _dataStore.Data.Settings.AiFeatureModels = [];
        SyncAiFeatureModelChoices();
        Save();
        RefreshAiFeatures();
    }

    /// <summary>"Default · GPT 5.6 Luna" while the feature follows the default model, else the model chosen for it.</summary>
    internal string DescribeAiFeatureModel(string feature)
        => _dataStore.Data.Settings.GetAiFeatureModel(feature) is { } model
            ? DescribeHelperModel(model)
            : Loc.Get("AiFeatures_DefaultModelLabel", DescribeHelperModel(BackgroundModel));

    /// <summary>
    /// With "Use only your own accounts" on, nothing may reach GitHub Copilot: a feature runs only on a model from the
    /// user's accounts. Auto then means the default chat model (see <see cref="CopilotService.ResolveConfiguredBackgroundModel"/>).
    /// </summary>
    internal bool IsAiFeatureBlocked(string feature)
    {
        if (!UseBYOKOnly)
            return false;

        var selection = CopilotService.NormalizeBackgroundModel(_dataStore.Data.Settings.GetAiFeatureModel(feature) ?? BackgroundModel);
        return !ByokConfigHelper.IsByokModel(selection == UserSettings.AutomaticBackgroundModel ? PreferredModel : selection);
    }

    // Names the model a selection runs on; Auto names the model it picks today, as the default model's status does.
    private string DescribeHelperModel(string selection)
    {
        var model = CopilotService.NormalizeBackgroundModel(selection);
        if (model == UserSettings.AutomaticBackgroundModel)
        {
            model = UseBYOKOnly || _copilotService.UsesExternalModel
                ? ByokConfigHelper.IsByokModel(PreferredModel) ? PreferredModel : model
                : _automaticBackgroundModel?.Id ?? model;
        }

        return model == UserSettings.AutomaticBackgroundModel
            ? Loc.Setting_BackgroundModelAuto
            : ChatViewModel.FormatModelDisplay(model) ?? model;
    }

    private void SyncAiFeatureModelChoices(IReadOnlyList<string>? catalog = null)
    {
        if (catalog is not null)
        {
            _aiFeatureCatalogModels = catalog
                .Where(static model => !string.Equals(model, UserSettings.AutomaticBackgroundModel, StringComparison.OrdinalIgnoreCase))
                .ToArray();
        }

        // A model chosen for a feature stays listed after it leaves the catalog, as the default model's does.
        ModelSelectionHelper.SyncAvailableModels(
            AiFeatureModelChoices,
            [DefaultAiFeatureModel, .. _aiFeatureCatalogModels, .. _dataStore.Data.Settings.AiFeatureModels.Values],
            pinnedModel: null);
        ModelOptionCatalog.SyncOptions(AiFeatureModelOptions, AiFeatureModelChoices,
            _dataStore.Data.Settings, _modelOptionMetadata, backgroundChoices: true);
    }

    private AiFeatureViewModel[] CreateAiFeatures()
    {
        PropertyChanged += OnAiFeatureSourceChanged;
        return
        [
            new(this, AiFeatureIds.ChatTitles, "ChatTitles", AiFeatureGroup.WhileYouChat, isFirstInGroup: true,
                () => AutoGenerateTitles, on => AutoGenerateTitles = on),
            new(this, AiFeatureIds.FollowUps, "FollowUps", AiFeatureGroup.WhileYouChat, isFirstInGroup: false,
                () => SuggestFollowUps, on => SuggestFollowUps = on),
            new(this, AiFeatureIds.Memories, "Memories", AiFeatureGroup.MemoryAndContext, isFirstInGroup: true,
                () => EnableMemoryAutoSave, on => EnableMemoryAutoSave = on),
            new(this, AiFeatureIds.DailyBriefs, "DailyBriefs", AiFeatureGroup.MemoryAndContext, isFirstInGroup: false,
                () => EnableDailyBriefs, on => EnableDailyBriefs = on),
            new(this, AiFeatureIds.SharedPrompts, "SharedPrompts", AiFeatureGroup.WhenAsked, isFirstInGroup: true),
            new(this, AiFeatureIds.Welcome, "Welcome", AiFeatureGroup.WhenAsked, isFirstInGroup: false),
        ];
    }

    private IReadOnlyList<AiFeatureViewModel> InGroup(AiFeatureGroup group)
        => AiFeatures.Where(feature => feature.Group == group).ToArray();

    private (int On, int All) CountAiFeaturesOn()
    {
        var switchable = AiFeatures.Where(static feature => feature.CanTurnOff).ToList();
        return (switchable.Count(static feature => feature.IsOn), switchable.Count);
    }

    private void OnAiFeatureSourceChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is { } name && AiFeatureSources.Contains(name))
            RefreshAiFeatures();
    }

    private void RefreshAiFeatures()
    {
        if (_aiFeatures is null)
            return;

        foreach (var feature in _aiFeatures)
            feature.Refresh();
        OnPropertyChanged(nameof(AiFeaturesSummary));
        OnPropertyChanged(nameof(AiFeaturesSummaryDetail));
        OnPropertyChanged(nameof(AreAllAiFeaturesOff));
        OnPropertyChanged(nameof(ToggleAllAiFeaturesLabel));
        OnPropertyChanged(nameof(AutoReviewStatus));
        OnPropertyChanged(nameof(IsAutoReviewDefault));
    }
}
