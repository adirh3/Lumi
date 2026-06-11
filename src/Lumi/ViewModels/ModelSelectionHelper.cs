using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using GitHub.Copilot;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.ViewModels;

internal static class ModelSelectionHelper
{
    public static void ApplyModelCapabilities(
        IEnumerable<ModelInfo> models,
        IDictionary<string, List<string>> reasoningEfforts,
        IDictionary<string, string> defaultEfforts,
        IDictionary<string, long>? contextTokenLimits = null)
    {
        reasoningEfforts.Clear();
        defaultEfforts.Clear();
        contextTokenLimits?.Clear();

        foreach (var model in models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                continue;

            if (model.SupportedReasoningEfforts is { Count: > 0 })
                reasoningEfforts[model.Id] = model.SupportedReasoningEfforts
                    .Where(static effort => !string.IsNullOrWhiteSpace(effort))
                    .ToList();

            if (!string.IsNullOrWhiteSpace(model.DefaultReasoningEffort))
                defaultEfforts[model.Id] = model.DefaultReasoningEffort!;

            if (model.Capabilities?.Limits?.MaxContextWindowTokens is > 0 and var contextTokenLimit)
                contextTokenLimits?[model.Id] = contextTokenLimit;
        }
    }

    public static string[]? GetQualityLevels(
        string? modelId,
        IReadOnlyDictionary<string, List<string>> reasoningEfforts)
    {
        if (string.IsNullOrWhiteSpace(modelId)
            || !reasoningEfforts.TryGetValue(modelId, out var efforts)
            || efforts.Count == 0)
        {
            return null;
        }

        return efforts.Select(EffortToDisplay).ToArray();
    }

    public static string? ResolveSelectedQualityDisplay(
        string? effort,
        string? modelId,
        IReadOnlyDictionary<string, List<string>> reasoningEfforts,
        IReadOnlyDictionary<string, string> defaultEfforts)
    {
        var normalizedEffort = NormalizeEffort(effort, modelId, reasoningEfforts, defaultEfforts);
        return normalizedEffort is null ? null : EffortToDisplay(normalizedEffort);
    }

    public static string? NormalizeEffort(
        string? effort,
        string? modelId,
        IReadOnlyDictionary<string, List<string>> reasoningEfforts,
        IReadOnlyDictionary<string, string> defaultEfforts)
    {
        if (string.IsNullOrWhiteSpace(modelId)
            || !reasoningEfforts.TryGetValue(modelId, out var supportedEfforts)
            || supportedEfforts.Count == 0)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(effort))
        {
            var explicitMatch = supportedEfforts.FirstOrDefault(candidate =>
                string.Equals(candidate, effort, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(explicitMatch))
                return explicitMatch;
        }

        var preferredHighMatch = supportedEfforts.FirstOrDefault(candidate =>
            string.Equals(candidate, "high", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredHighMatch))
            return preferredHighMatch;

        var preferredMediumMatch = supportedEfforts.FirstOrDefault(candidate =>
            string.Equals(candidate, "medium", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(preferredMediumMatch))
            return preferredMediumMatch;

        if (defaultEfforts.TryGetValue(modelId, out var defaultEffort))
        {
            var defaultMatch = supportedEfforts.FirstOrDefault(candidate =>
                string.Equals(candidate, defaultEffort, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(defaultMatch))
                return defaultMatch;
        }

        return supportedEfforts.Count == 1
            ? supportedEfforts[0]
            : supportedEfforts[supportedEfforts.Count / 2];
    }

    public static string EffortToDisplay(string effort) => effort.ToLowerInvariant() switch
    {
        "low" => Loc.Quality_Low,
        "medium" => Loc.Quality_Medium,
        "high" => Loc.Quality_High,
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(effort)
    };

    public static string? DisplayToEffort(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return null;

        if (display == Loc.Quality_Low)
            return "low";
        if (display == Loc.Quality_Medium)
            return "medium";
        if (display == Loc.Quality_High)
            return "high";
        return display.ToLowerInvariant();
    }

    public static string[]? GetContextWindowTiers(
        string? modelId,
        IReadOnlySet<string> longContextModelIds)
    {
        if (!SupportsContextWindowTiers(modelId, longContextModelIds))
            return null;

        return
        [
            ContextWindowTierToDisplay(ModelContextWindowTiers.Default),
            ContextWindowTierToDisplay(ModelContextWindowTiers.LongContext)
        ];
    }

    public static string? ResolveSelectedContextWindowTierDisplay(
        string? tier,
        string? modelId,
        IReadOnlySet<string> longContextModelIds)
    {
        var normalizedTier = NormalizeContextWindowTier(tier, modelId, longContextModelIds);
        return normalizedTier is null ? null : ContextWindowTierToDisplay(normalizedTier);
    }

    public static string? NormalizeContextWindowTier(
        string? tier,
        string? modelId,
        IReadOnlySet<string> longContextModelIds)
    {
        if (!SupportsContextWindowTiers(modelId, longContextModelIds))
            return null;

        var normalizedTier = DisplayToContextWindowTier(tier);
        return string.Equals(normalizedTier, ModelContextWindowTiers.LongContext, StringComparison.OrdinalIgnoreCase)
            ? ModelContextWindowTiers.LongContext
            : ModelContextWindowTiers.Default;
    }

    public static string ContextWindowTierToDisplay(string tier) => tier.ToLowerInvariant() switch
    {
        ModelContextWindowTiers.Default => Loc.ContextWindow_Default,
        ModelContextWindowTiers.LongContext => Loc.ContextWindow_Long,
        _ => CultureInfo.CurrentCulture.TextInfo.ToTitleCase(tier.Replace('_', ' '))
    };

    public static string? DisplayToContextWindowTier(string? display)
    {
        if (string.IsNullOrWhiteSpace(display))
            return null;

        if (display == Loc.ContextWindow_Default)
            return ModelContextWindowTiers.Default;
        if (display == Loc.ContextWindow_Long)
            return ModelContextWindowTiers.LongContext;

        return display.Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_') switch
        {
            "standard" or "default" => ModelContextWindowTiers.Default,
            "long" or "longcontext" or "long_context" => ModelContextWindowTiers.LongContext,
            var tier => tier
        };
    }

    private static bool SupportsContextWindowTiers(
        string? modelId,
        IReadOnlySet<string> longContextModelIds)
    {
        if (string.IsNullOrWhiteSpace(modelId))
            return false;

        var normalizedModel = modelId.Trim();
        return longContextModelIds.Contains(normalizedModel)
            || longContextModelIds.Any(candidate => string.Equals(candidate, normalizedModel, StringComparison.OrdinalIgnoreCase));
    }
}
