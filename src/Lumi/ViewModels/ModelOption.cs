using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services;
using StrataTheme.Controls;

namespace Lumi.ViewModels;

/// <summary>
/// The badge-worthy facts about one model, distilled from the SDK's <see cref="ModelInfo"/>. Only
/// fields the SDK actually reports are represented — there is no speed or intelligence score to
/// derive, so the picker never claims one.
/// </summary>
public sealed record ModelOptionMetadata(
    long ContextTokens,
    double? CostMultiplier,
    bool SupportsVision,
    bool SupportsReasoning,
    bool RequiresOptIn,
    string? DisplayName = null);

/// <summary>
/// One row in the composer's model picker: the model id plus the metadata the row renders as badges,
/// and whether the user pinned it. <see cref="ChatViewModel.SelectedModel"/> stays a plain id string —
/// the picker resolves this option back to <see cref="Id"/> through <see cref="IStrataModelOption"/>.
/// </summary>
public sealed partial class ModelOption : ObservableObject, IStrataModelOption
{
    public ModelOption(string id)
    {
        Id = id;
        _displayName = ChatViewModel.FormatModelDisplay(id) ?? id;
        _providerName = ByokConfigHelper.IsByokModel(id) ? null : "GitHub Copilot";
    }

    /// <summary>Catalog selection token, including an explicit native Auto tier when selected.</summary>
    public string Id { get; }

    string IStrataModelOption.ModelId => Id;

    public IReadOnlyList<string> SelectionAliases { get; private set; } = [];

    public int SortPriority => SelectionAliases.Count > 0 ? 1 : 0;

    internal void UpdateSelectionAliases(IReadOnlyList<string> aliases)
    {
        if (SelectionAliases.SequenceEqual(aliases, StringComparer.Ordinal))
            return;
        SelectionAliases = aliases;
        OnPropertyChanged(nameof(SelectionAliases));
    }

    [ObservableProperty] private string _displayName;
    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip), nameof(PinnedProviderSuffix))]
    [ObservableProperty] private string? _providerName;

    /// <summary>Max context window in tokens, or 0 when the catalog does not report one.</summary>
    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip))]
    [ObservableProperty] private long _contextTokens;

    /// <summary>Copilot premium-request multiplier (e.g. 1, 0.33), or null when not reported.</summary>
    [ObservableProperty] private double? _costMultiplier;

    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip))]
    [ObservableProperty] private bool _supportsVision;

    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip))]
    [ObservableProperty] private bool _supportsReasoning;

    /// <summary>True when the model's policy still has to be accepted before it can be used.</summary>
    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip))]
    [ObservableProperty] private bool _requiresOptIn;

    [NotifyPropertyChangedFor(nameof(DetailsText), nameof(HasDetails), nameof(DetailsToolTip), nameof(PinnedProviderSuffix))]
    [ObservableProperty] private bool _isPinned;

    /// <summary>"200K context" style badge, or null when the catalog reports no context limit.</summary>
    public string? ContextBadge => ModelOptionCatalog.FormatContextTokens(ContextTokens) is { } tokens
        ? string.Format(CultureInfo.CurrentCulture, Localization.Loc.ModelPicker_ContextBadge, tokens)
        : null;

    /// <summary>Bare context size for the row's metadata column ("200K"), or null when unknown.</summary>
    public string? ContextShort => ModelOptionCatalog.FormatContextTokens(ContextTokens);

    /// <summary>"1×" / "0.33×" / "Included" cost badge, or null when the catalog reports no multiplier.</summary>
    public string? CostBadge => ModelOptionCatalog.FormatCostMultiplier(CostMultiplier);

    public bool HasContextBadge => ContextBadge is not null;

    public bool HasCostBadge => CostBadge is not null;

    /// <summary>
    /// Connected-account name shown after a pinned BYOK model's name: the Pinned group hides the
    /// account heading, and Copilot models need no qualifier.
    /// </summary>
    public string? PinnedProviderSuffix => IsPinned && ByokConfigHelper.IsByokModel(Id) && !string.IsNullOrWhiteSpace(ProviderName)
        ? "  " + ProviderName
        : null;

    /// <summary>Every known fact on one line: the row's tooltip, where the row itself shows only
    /// context size and cost.</summary>
    public string DetailsText
    {
        get
        {
            var details = new List<string>(5);
            if (IsPinned && !string.IsNullOrWhiteSpace(ProviderName))
                details.Add(ProviderName);
            if (RequiresOptIn)
                details.Add(Localization.Loc.ModelPicker_NeedsEnabling);
            if (ContextBadge is { } context)
                details.Add(context);
            if (SupportsReasoning)
                details.Add(Localization.Loc.ModelPicker_Reasoning);
            if (SupportsVision)
                details.Add(Localization.Loc.ModelPicker_Vision);
            return string.Join(" \u00b7 ", details);
        }
    }

    public bool HasDetails => DetailsText.Length > 0;

    /// <summary>Row tooltip; null rather than empty so a model without facts shows no tooltip.</summary>
    public string? DetailsToolTip => HasDetails ? DetailsText : null;

    partial void OnContextTokensChanged(long value)
    {
        OnPropertyChanged(nameof(ContextBadge));
        OnPropertyChanged(nameof(ContextShort));
        OnPropertyChanged(nameof(HasContextBadge));
    }

    partial void OnCostMultiplierChanged(double? value)
    {
        OnPropertyChanged(nameof(CostBadge));
        OnPropertyChanged(nameof(HasCostBadge));
    }

    /// <summary>Copies freshly fetched catalog metadata onto an option that is already on screen, so a
    /// live catalog refresh updates badges without replacing (and re-rendering) the row.</summary>
    public void Apply(ModelOptionMetadata metadata, UserSettings? settings = null)
    {
        DisplayName = SelectionAliases.Count > 0 ? Localization.Loc.Get("ModelPicker_Fusion")
            : CopilotModelCatalog.FormatAutoTierName(Id, metadata.DisplayName)
            ?? ChatViewModel.FormatModelDisplay(Id) ?? Id;
        if (settings is not null && ByokConfigHelper.IsByokModel(Id))
        {
            var model = settings.ByokModels.FirstOrDefault(model => ByokConfigHelper.BuildModelToken(model) == Id);
            var endpoint = settings.ByokEndpoints.FirstOrDefault(endpoint => endpoint.Id == model?.EndpointId);
            ProviderName = endpoint?.Name;
            if (model is not null)
                DisplayName = ModelOptionCatalog.GetByokDisplayName(model, endpoint);
        }
        ContextTokens = metadata.ContextTokens;
        CostMultiplier = metadata.CostMultiplier;
        SupportsVision = metadata.SupportsVision;
        SupportsReasoning = metadata.SupportsReasoning;
        RequiresOptIn = metadata.RequiresOptIn;
    }

    public override string ToString() => Id;
}

/// <summary>
/// Helpers that turn model catalogs, connected accounts and favorites into picker rows.
/// Presentation and ordering stay shared between the composer and Settings.
/// </summary>
public static class ModelOptionCatalog
{
    private static readonly ModelOptionMetadata UnknownMetadata = new(0, null, false, false, false);

    public static ModelOptionMetadata Describe(ModelInfo model)
    {
        ArgumentNullException.ThrowIfNull(model);

        var limits = model.Capabilities?.Limits;
        var supports = model.Capabilities?.Supports;
        var contextTokens = limits?.MaxContextWindowTokens is > 0 and var maxContext
            ? maxContext
            : 0L;

        // The SDK exposes reasoning either as an explicit capability flag or as a non-empty effort
        // list; a model that offers efforts but leaves the flag unset still reasons.
        var supportsReasoning = supports?.ReasoningEffort == true
            || model.SupportedReasoningEfforts is { Count: > 0 };

        var requiresOptIn = !Services.CopilotService.IsModelEnabled(model);

        return new ModelOptionMetadata(
            contextTokens,
            model.Billing?.Multiplier,
            supports?.Vision == true,
            supportsReasoning,
            requiresOptIn,
            CopilotModelCatalog.IsAutoTierSelection(model.Id) ? model.Name : null);
    }

    public static Dictionary<string, ModelOptionMetadata> BuildMetadata(IEnumerable<ModelInfo> models)
    {
        var metadata = new Dictionary<string, ModelOptionMetadata>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                continue;

            metadata[model.Id] = Describe(model);
        }

        return metadata;
    }

    public static string GetByokDisplayName(ByokModel model, ByokEndpoint? endpoint)
    {
        var name = string.IsNullOrWhiteSpace(model.DisplayName) ? model.ModelId : model.DisplayName;
        var prefix = endpoint?.Name + " · ";
        if (endpoint is not null && name.StartsWith(prefix, StringComparison.Ordinal))
            return name[prefix.Length..];
        var originalName = Services.Byok.ProviderCatalog.Find(endpoint?.ConnectionProviderId)?.Name;
        var originalPrefix = originalName + " · ";
        return originalName is not null && name.StartsWith(originalPrefix, StringComparison.Ordinal)
            ? name[originalPrefix.Length..] : name;
    }

    /// <summary>Updates provider labels, badges and pin order without replacing existing row instances.</summary>
    public static void SyncOptions(
        ObservableCollection<ModelOption> target, IEnumerable<string> modelIds, UserSettings settings,
        IReadOnlyDictionary<string, ModelOptionMetadata> metadata,
        bool backgroundChoices = false)
    {
        var ids = modelIds.Distinct(StringComparer.Ordinal).ToList();
        var tiers = CopilotModelCatalog.GetAutoTierSelections(ids.Where(metadata.ContainsKey));
        var fusion = CopilotModelCatalog.DefaultAutoTierSelection(tiers);
        var aliases = fusion is null ? [] : tiers.Concat(backgroundChoices ? [] : new[] { "auto" }).ToArray();
        var displayedIds = fusion is null ? ids
            : ids.Where(id => !tiers.Contains(id, StringComparer.Ordinal)
                && (backgroundChoices || id != "auto")).Prepend(fusion).ToList();
        var favorites = settings.FavoriteModelIds?.Select(id =>
            aliases.Contains(id, StringComparer.Ordinal) ? fusion! : id);
        var ordered = OrderPinnedFirst(displayedIds, favorites);
        var pinned = new HashSet<string>(favorites ?? [], StringComparer.Ordinal);
        var existing = target.ToDictionary(static option => option.Id, StringComparer.Ordinal);
        for (var index = target.Count - 1; index >= 0; index--)
            if (!ordered.Contains(target[index].Id, StringComparer.Ordinal))
                target.RemoveAt(index);

        for (var index = 0; index < ordered.Count; index++)
        {
            var id = ordered[index];
            var option = existing.TryGetValue(id, out var current) ? current : new ModelOption(id);
            option.UpdateSelectionAliases(id == fusion ? aliases : []);
            option.Apply(metadata.TryGetValue(id, out var facts) ? facts : UnknownMetadata, settings);
            if (backgroundChoices && id == UserSettings.AutomaticBackgroundModel)
            {
                option.DisplayName = Localization.Loc.Setting_BackgroundModelAuto;
                option.ProviderName = null;
            }
            else if (backgroundChoices && id == SettingsViewModel.DefaultAiFeatureModel)
            {
                option.DisplayName = Localization.Loc.Get("AiFeatures_DefaultModelChoice");
                option.ProviderName = null;
            }
            option.IsPinned = pinned.Contains(id);
            var currentIndex = target.IndexOf(option);
            if (currentIndex < 0)
                target.Insert(index, option);
            else if (currentIndex != index)
                target.Move(currentIndex, index);
        }
    }

    internal static void SetPinned(UserSettings settings, ModelOption option, bool pinned)
    {
        settings.FavoriteModelIds ??= [];
        if (pinned)
        {
            if (!settings.FavoriteModelIds.Any(id => id == option.Id
                    || option.SelectionAliases.Contains(id, StringComparer.Ordinal)))
                settings.FavoriteModelIds.Add(option.Id);
        }
        else
            settings.FavoriteModelIds.RemoveAll(id => id == option.Id
                || option.SelectionAliases.Contains(id, StringComparer.Ordinal));
    }

    /// <summary>
    /// Orders model ids pinned-first: favorites in the order the user pinned them, then everything
    /// else in catalog order. Favorites that are not in the catalog (a retired or BYOK-only model)
    /// are skipped rather than invented.
    /// </summary>
    public static List<string> OrderPinnedFirst(IEnumerable<string> modelIds, IEnumerable<string>? favoriteIds)
    {
        var ordered = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var available = new List<string>();

        foreach (var id in modelIds)
        {
            if (!string.IsNullOrWhiteSpace(id) && seen.Add(id))
                available.Add(id);
        }

        if (favoriteIds is not null)
        {
            var pinned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var favorite in favoriteIds)
            {
                if (string.IsNullOrWhiteSpace(favorite) || !pinned.Add(favorite))
                    continue;

                var match = available.FirstOrDefault(id => string.Equals(id, favorite, StringComparison.Ordinal));
                if (match is not null)
                    ordered.Add(match);
            }
        }

        var alreadyPinned = new HashSet<string>(ordered, StringComparer.Ordinal);
        ordered.AddRange(available.Where(id => !alreadyPinned.Contains(id)));
        return ordered;
    }

    /// <summary>Renders a token count as a compact context badge ("200K", "1M"). Null when unknown.</summary>
    public static string? FormatContextTokens(long tokens)
    {
        if (tokens <= 0)
            return null;

        if (tokens >= 1_000_000)
        {
            var millions = tokens / 1_000_000d;
            return Localization.Loc.Get(nameof(Localization.Loc.ModelPicker_CompactMillions), millions.ToString("0.#", CultureInfo.CurrentCulture));
        }

        if (tokens >= 1_000)
        {
            var thousands = tokens / 1_000d;
            return Localization.Loc.Get(nameof(Localization.Loc.ModelPicker_CompactThousands), thousands.ToString("0.#", CultureInfo.CurrentCulture));
        }

        return tokens.ToString(CultureInfo.CurrentCulture);
    }

    /// <summary>Renders the premium-request multiplier. A zero multiplier means the model does not
    /// consume premium requests at all, which reads better as "Included" than as "0×".</summary>
    public static string? FormatCostMultiplier(double? multiplier)
    {
        if (multiplier is not { } value || value < 0)
            return null;

        if (value == 0)
            return Localization.Loc.ModelPicker_CostIncluded;

        return string.Create(CultureInfo.CurrentCulture, $"{value:0.##}\u00d7");
    }
}
