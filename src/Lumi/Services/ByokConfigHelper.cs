using System;
using GitHub.Copilot.SDK;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Builds <see cref="ProviderConfig"/> from <see cref="UserSettings"/> BYOK fields.
/// Returns null when BYOK is not enabled or not fully configured.
/// </summary>
public static class ByokConfigHelper
{
    /// <summary>
    /// Model ID prefix used to identify BYOK models in the model picker.
    /// When the selected model starts with this prefix, BYOK provider config is applied.
    /// </summary>
    public const string ByokModelPrefix = "byok:";

    /// <summary>
    /// Returns true when BYOK is enabled and has enough configuration to be usable.
    /// Checks trimmed values so whitespace-only fields do not create false-positive readiness.
    /// </summary>
    public static bool IsByokConfigured(UserSettings settings)
    {
        return settings.IsByokEnabled
            && !string.IsNullOrWhiteSpace(settings.ByokProviderType?.Trim())
            && !string.IsNullOrWhiteSpace(settings.ByokBaseUrl?.Trim())
            && !string.IsNullOrWhiteSpace(settings.ByokModelId?.Trim());
    }

    /// <summary>
    /// Validates a BYOK base URL. Returns null if valid, or a validation message if not.
    /// Accepts absolute http and https URLs. Does not require https because local
    /// providers such as Ollama use http.
    /// </summary>
    public static string? ValidateBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Base URL is required.";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "Enter a valid absolute URL (e.g. https://api.openai.com/v1).";

        if (uri.Scheme is not ("http" or "https"))
            return "URL must use http or https scheme.";

        return null;
    }

    /// <summary>
    /// Returns the full BYOK model identifier used in the model picker.
    /// Format: "byok:{modelId}"
    /// </summary>
    public static string GetByokModelId(UserSettings settings)
    {
        return $"{ByokModelPrefix}{settings.ByokModelId}";
    }

    /// <summary>
    /// Returns the display name for the BYOK model shown in the picker.
    /// </summary>
    public static string GetByokModelDisplayName(UserSettings settings)
    {
        var provider = settings.ByokProviderType ?? "custom";
        var model = settings.ByokModelId ?? "unknown";
        return $"{model} ({provider.ToUpperInvariant()}) — BYOK";
    }

    /// <summary>
    /// Returns true if the given model ID refers to a BYOK model.
    /// </summary>
    public static bool IsByokModel(string? modelId)
    {
        return !string.IsNullOrEmpty(modelId) && modelId.StartsWith(ByokModelPrefix);
    }

    /// <summary>
    /// Extracts the underlying model ID from a BYOK-prefixed model identifier.
    /// Returns null if the model is not a BYOK model.
    /// </summary>
    public static string? StripByokPrefix(string? modelId)
    {
        if (!IsByokModel(modelId)) return null;
        return modelId![ByokModelPrefix.Length..];
    }

    /// <summary>
    /// Builds a <see cref="ProviderConfig"/> from the current BYOK settings.
    /// Returns null if BYOK is not enabled or missing required fields.
    /// Resolves the API key from settings first, then falls back to the
    /// LUMI_BYOK_API_KEY environment variable.
    /// </summary>
    public static ProviderConfig? BuildProviderConfig(UserSettings settings)
    {
        if (!IsByokConfigured(settings))
            return null;

        var providerConfig = new ProviderConfig
        {
            Type = settings.ByokProviderType!,
            BaseUrl = settings.ByokBaseUrl!,
        };

        // Wire API: default to "completions" if not specified
        if (!string.IsNullOrWhiteSpace(settings.ByokWireApi))
            providerConfig.WireApi = settings.ByokWireApi;

        // API key: settings first, environment variable fallback
        var apiKey = !string.IsNullOrWhiteSpace(settings.ByokApiKey)
            ? settings.ByokApiKey
            : Environment.GetEnvironmentVariable("LUMI_BYOK_API_KEY");

        if (!string.IsNullOrWhiteSpace(apiKey))
            providerConfig.ApiKey = apiKey;

        // Azure-specific options
        if (string.Equals(settings.ByokProviderType, "azure", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(settings.ByokAzureApiVersion))
        {
            providerConfig.Azure = new AzureOptions
            {
                ApiVersion = settings.ByokAzureApiVersion,
            };
        }

        return providerConfig;
    }

    /// <summary>
    /// Builds a <see cref="ProviderConfig"/> if the selected model is a BYOK model.
    /// Returns null otherwise. Strips the BYOK prefix from the model ID so the
    /// actual model ID is sent to the provider.
    /// </summary>
    /// <param name="settings">User settings with BYOK configuration.</param>
    /// <param name="selectedModel">The model selected for the session.</param>
    /// <param name="actualModelId">Receives the actual model ID (without BYOK prefix) to use in SessionConfig.Model.</param>
    /// <returns>A ProviderConfig if BYOK applies, null otherwise.</returns>
    public static ProviderConfig? TryBuildForSession(UserSettings settings, string? selectedModel, out string? actualModelId)
    {
        actualModelId = selectedModel;

        if (!IsByokModel(selectedModel))
            return null;

        if (!IsByokConfigured(settings))
            return null;

        // Strip the "byok:" prefix for the actual model ID sent to the provider
        actualModelId = StripByokPrefix(selectedModel);
        return BuildProviderConfig(settings);
    }
}
