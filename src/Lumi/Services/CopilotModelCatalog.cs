using System.Text.Json;
using System.Text.Json.Serialization;
using GitHub.Copilot;
using Lumi.Localization;

namespace Lumi.Services;

internal static class CopilotModelCatalog
{
    // A picker selection, not a wire model ID. Persisting the tier in the existing model field
    // keeps desktop, phone, saved chats and AI-feature selections on the same native route.
    internal const string AutoTierPrefix = "auto:";

    internal static bool IsAutoTierSelection(string? selection)
        => selection?.StartsWith(AutoTierPrefix, StringComparison.Ordinal) == true;

    internal static bool IsAutoSelection(string? selection)
        => selection == "auto" || IsAutoTierSelection(selection);

    internal static string[] GetAutoTierSelections(IEnumerable<string> models)
        => models.Where(IsAutoTierSelection).Distinct(StringComparer.Ordinal).ToArray();

    // The family row's representative, not the provider's runtime default.
    internal static string? DefaultAutoTierSelection(IReadOnlyList<string> selections)
        => selections.Contains("auto:balance", StringComparer.Ordinal) ? "auto:balance" : selections.FirstOrDefault();

    internal static string? FormatAutoTierLevel(string? selection)
        => selection switch
        {
            "auto:balance" => Loc.Get("Quality_Standard"),
            "auto:hydrafusion" => Loc.Quality_High,
            "auto:hydrafusion-max" => Loc.Quality_Max,
            _ when IsAutoTierSelection(selection) => selection![AutoTierPrefix.Length..],
            _ => null
        };

    internal static string[]? GetAutoTierQualityLevels(
        string? selection, IEnumerable<string> models, bool includeDefaultAuto = true)
    {
        if (!IsAutoTierSelection(selection) && !(includeDefaultAuto && selection == "auto"))
            return null;
        var choices = GetAutoTierSelections(models);
        return choices.Length == 0 ? null : choices.Select(id => FormatAutoTierLevel(id)!).ToArray();
    }

    internal static string? GetAutoTierQuality(
        string? selection, IEnumerable<string> models, bool includeDefaultAuto = true,
        string? defaultAutoTier = null)
    {
        if (GetAutoTierQualityLevels(selection, models, includeDefaultAuto) is null)
            return null;
        return FormatAutoTierLevel(selection == "auto"
            ? string.IsNullOrWhiteSpace(defaultAutoTier) ? null : AutoTierPrefix + defaultAutoTier
            : selection);
    }

    internal static string? ResolveAutoTierQuality(
        string? selection, string? quality, IEnumerable<string> models)
    {
        if (!IsAutoSelection(selection) || string.IsNullOrWhiteSpace(quality))
            return null;
        var canonical = quality.Trim().ToLowerInvariant() switch
        {
            "standard" => "auto:balance",
            "high" => "auto:hydrafusion",
            "max" => "auto:hydrafusion-max",
            _ => null
        };
        return GetAutoTierSelections(models).FirstOrDefault(id =>
            id == canonical || string.Equals(FormatAutoTierLevel(id), quality, StringComparison.OrdinalIgnoreCase));
    }

    internal static AutoTier? GetAutoTier(string? selection)
        => IsAutoTierSelection(selection)
            ? new AutoTier(selection![AutoTierPrefix.Length..])
            : null;

    internal static string? GetWireModelId(string? selection, ProviderConfig? provider = null)
        => provider is null && IsAutoTierSelection(selection) ? "auto" : selection;

    internal static string? GetSessionModelSelection(string? model, AutoTier? autoTier)
        => IsAutoSelection(model) && autoTier is { } tier ? AutoTierPrefix + tier.Value : model;

    internal static CapiSessionOptions? CreateCapiOptions(string? selection, ProviderConfig? provider = null)
        => provider is null && GetAutoTier(selection) is { } tier
            ? new CapiSessionOptions { AutoTier = tier }
            : null;

#pragma warning disable GHCP001 // Read the native account-bound catalog, including its advertised Auto tiers.
    internal static ModelCatalogSnapshot BuildSnapshot(GitHub.Copilot.Rpc.ModelList catalog)
    {
        var longContextModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var limits = new Dictionary<string, ModelContextWindowLimits>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in catalog.Models)
        {
            if (string.IsNullOrWhiteSpace(model.Id))
                continue;

            var tokenPrices = model.Billing?.TokenPrices;
            var defaultContextMax = NormalizeContextMax(tokenPrices?.ContextMax);
            var longContextMax = NormalizeContextMax(tokenPrices?.LongContext?.ContextMax);
            if (longContextMax > 0)
                longContextModelIds.Add(model.Id);
            if (defaultContextMax > 0 || longContextMax > 0)
                limits[model.Id] = new(defaultContextMax, longContextMax > 0 ? longContextMax : null);
        }

        return new(BuildModels(catalog), new(longContextModelIds, limits), catalog.Auto?.DefaultTier);
    }

    internal static List<ModelInfo> BuildModels(GitHub.Copilot.Rpc.ModelList catalog)
    {
        // The SDK's flat and native model types share the wire schema. Preserve all flat metadata
        // without calling ListModelsAsync, whose internal cache survives a picker refresh.
        var models = JsonSerializer.SerializeToElement(catalog.Models, CopilotCatalogJsonContext.Default.NativeModels)
            .Deserialize(CopilotPickerJsonContext.Default.PickerModels)
            ?? throw new JsonException("The Copilot runtime returned an invalid model catalog.");
        var tiers = catalog.Auto?.Tiers
            .Where(tier => tier.Status.Enabled && tier.Status.PickerVisible != false
                && !string.IsNullOrWhiteSpace(tier.Id))
            .DistinctBy(tier => tier.Id, StringComparer.Ordinal)
            .Select(tier => new ModelInfo
            {
                Id = AutoTierPrefix + tier.Id,
                Name = tier.FusionTier is not null
                    ? $"Copilot Fusion {tier.DisplayName}"
                    : $"Auto {tier.DisplayName}"
            })
            .ToList();
        if (tiers is { Count: > 0 })
        {
            var autoIndex = models.FindIndex(model => model.Id == "auto");
            models.InsertRange(autoIndex < 0 ? 0 : autoIndex + 1, tiers);
        }

        return models;
    }
#pragma warning restore GHCP001

    private static long NormalizeContextMax(double? tokens)
        => tokens is null || double.IsNaN(tokens.Value) || double.IsInfinity(tokens.Value) || tokens.Value <= 0
            ? 0
            : (long)Math.Round(tokens.Value);

    internal static string? FormatAutoTierName(string? selection, string? advertisedName = null)
    {
        if (!IsAutoTierSelection(selection))
            return null;

        return selection![AutoTierPrefix.Length..] switch
        {
            "balance" => Loc.Get("ModelPicker_FusionStandard"),
            "hydrafusion" => Loc.Get("ModelPicker_FusionHigh"),
            "hydrafusion-max" => Loc.Get("ModelPicker_FusionMax"),
            _ => advertisedName ?? string.Format(Loc.Get("ModelPicker_AutoTier"), selection[AutoTierPrefix.Length..])
        };
    }
}

#pragma warning disable GHCP001
[JsonSerializable(typeof(IList<GitHub.Copilot.Rpc.Model>), TypeInfoPropertyName = "NativeModels")]
internal partial class CopilotCatalogJsonContext : JsonSerializerContext;
#pragma warning restore GHCP001

[JsonSerializable(typeof(List<ModelInfo>), TypeInfoPropertyName = "PickerModels")]
internal partial class CopilotPickerJsonContext : JsonSerializerContext;
