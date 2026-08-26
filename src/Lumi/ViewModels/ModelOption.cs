using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using GitHub.Copilot;
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
    bool RequiresOptIn);

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
    }

    /// <summary>Model id exactly as the catalog reports it (also the value written back on select).</summary>
    public string Id { get; }

    string IStrataModelOption.ModelId => Id;

    [ObservableProperty] private string _displayName;

    /// <summary>Max context window in tokens, or 0 when the catalog does not report one.</summary>
    [ObservableProperty] private long _contextTokens;

    /// <summary>Copilot premium-request multiplier (e.g. 1, 0.33), or null when not reported.</summary>
    [ObservableProperty] private double? _costMultiplier;

    [ObservableProperty] private bool _supportsVision;

    [ObservableProperty] private bool _supportsReasoning;

    /// <summary>True when the model's policy still has to be accepted before it can be used.</summary>
    [ObservableProperty] private bool _requiresOptIn;

    [ObservableProperty] private bool _isPinned;

    /// <summary>"200K context" style badge, or null when the catalog reports no context limit.</summary>
    public string? ContextBadge => ModelOptionCatalog.FormatContextTokens(ContextTokens) is { } tokens
        ? string.Format(CultureInfo.CurrentCulture, Localization.Loc.ModelPicker_ContextBadge, tokens)
        : null;

    /// <summary>"1×" / "0.33×" / "Included" cost badge, or null when the catalog reports no multiplier.</summary>
    public string? CostBadge => ModelOptionCatalog.FormatCostMultiplier(CostMultiplier);

    public bool HasContextBadge => ContextBadge is not null;

    public bool HasCostBadge => CostBadge is not null;

    partial void OnContextTokensChanged(long value)
    {
        OnPropertyChanged(nameof(ContextBadge));
        OnPropertyChanged(nameof(HasContextBadge));
    }

    partial void OnCostMultiplierChanged(double? value)
    {
        OnPropertyChanged(nameof(CostBadge));
        OnPropertyChanged(nameof(HasCostBadge));
    }

    /// <summary>Copies freshly fetched catalog metadata onto an option that is already on screen, so a
    /// live catalog refresh updates badges without replacing (and re-rendering) the row.</summary>
    public void Apply(ModelOptionMetadata metadata)
    {
        DisplayName = ChatViewModel.FormatModelDisplay(Id) ?? Id;
        ContextTokens = metadata.ContextTokens;
        CostMultiplier = metadata.CostMultiplier;
        SupportsVision = metadata.SupportsVision;
        SupportsReasoning = metadata.SupportsReasoning;
        RequiresOptIn = metadata.RequiresOptIn;
    }

    public override string ToString() => Id;
}

/// <summary>
/// Pure helpers that turn the SDK model catalog and the user's favorites into picker rows. Kept free
/// of view-model state so ordering and badge formatting are directly testable.
/// </summary>
public static class ModelOptionCatalog
{
    /// <summary>Policy states that mean the model is already usable; anything else needs enabling.</summary>
    private static readonly string[] EnabledPolicyStates = ["enabled", "ready", "unset", ""];

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

        var policyState = model.Policy?.State?.Trim().ToLowerInvariant() ?? string.Empty;
        var requiresOptIn = !EnabledPolicyStates.Contains(policyState, StringComparer.Ordinal);

        return new ModelOptionMetadata(
            contextTokens,
            model.Billing?.Multiplier,
            supports?.Vision == true,
            supportsReasoning,
            requiresOptIn);
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
            return string.Create(CultureInfo.InvariantCulture, $"{millions:0.#}M");
        }

        if (tokens >= 1_000)
        {
            var thousands = tokens / 1_000d;
            return string.Create(CultureInfo.InvariantCulture, $"{thousands:0.#}K");
        }

        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>Renders the premium-request multiplier. A zero multiplier means the model does not
    /// consume premium requests at all, which reads better as "Included" than as "0×".</summary>
    public static string? FormatCostMultiplier(double? multiplier)
    {
        if (multiplier is not { } value || value < 0)
            return null;

        if (value == 0)
            return Localization.Loc.ModelPicker_CostIncluded;

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##}\u00d7");
    }
}
