using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services.Byok;

namespace Lumi.Services;

public sealed record SessionModelRoute(
    string? SelectionToken,
    string? WireModelId,
    ByokModel? ByokModel,
    ProviderConfig? Provider,
    bool IsInvalidByok)
{
    public bool IsByok => Provider is not null;
}

/// <summary>
/// Helper for Lumi's BYOK2 (Endpoint + Models) configuration.
///
/// Resolution chain for a selected model token:
///   selectedModel (e.g. "byok:abc123") → ByokModel → ByokEndpoint → ProviderConfig
///
/// The BYOK2 picker token is canonical: <c>byok:{ByokModel.Id}</c>. <see cref="ByokModel.Id"/>
/// is the stable identity — rename of DisplayName/ModelId never changes it, and the token
/// continues to resolve across restarts.
///
/// Failures (stale id, missing endpoint, invalid config) return <c>null</c> rather than
/// silently falling back to Copilot. Callers must check the return value and surface a clear
/// BYOK error to the user instead of silently switching providers.
/// </summary>
public static class ByokConfigHelper
{
    /// <summary>Prefix for all BYOK picker tokens.</summary>
    public const string TokenPrefix = "byok:";

    /// <summary>Fallback environment variable when an endpoint's <see cref="ByokEndpoint.ApiKeyEnvVar"/> is blank.</summary>
    public const string FallbackApiKeyEnvVar = "LUMI_BYOK_API_KEY";

    /// <summary>
    /// Header value placeholder that resolves to the endpoint's configured API key at runtime.
    /// Use in <see cref="ByokEndpoint.Headers"/> values (for example: <c>api-key = ${apiKey}</c>).
    /// </summary>
    public const string ApiKeyHeaderToken = "${apiKey}";

    /// <summary>
    /// Legacy alias for <see cref="ApiKeyHeaderToken"/>. Kept for backward compatibility with
    /// manually-authored config files.
    /// </summary>
    public const string ApiKeyHeaderTokenLegacy = "${byok.apiKey}";

    /// <summary>True when <paramref name="modelId"/> is a BYOK picker token.</summary>
    public static bool IsByokModel(string? modelId)
        => !string.IsNullOrWhiteSpace(modelId)
           && modelId.StartsWith(TokenPrefix, StringComparison.Ordinal);

    /// <summary>Builds the canonical token for a model entry.</summary>
    public static string BuildModelToken(ByokModel model) => $"{TokenPrefix}{model.Id}";

    /// <summary>
    /// Parses a BYOK picker token. Returns <c>true</c> for any <c>byok:{id}</c> form;
    /// <paramref name="modelEntryId"/> is the id segment (non-empty when the input is a
    /// well-formed token). Returns <c>false</c> for non-BYOK input or <c>null</c>.
    /// </summary>
    public static bool TryParseModelToken(string? modelId, out string? modelEntryId)
    {
        if (!IsByokModel(modelId))
        {
            modelEntryId = null;
            return false;
        }

        var id = modelId!.Substring(TokenPrefix.Length);
        if (string.IsNullOrWhiteSpace(id))
        {
            modelEntryId = null;
            return false;
        }

        modelEntryId = id;
        return true;
    }

    /// <summary>
    /// Normalizes the WireApi to a canonical SDK value. The Copilot SDK's
    /// <c>wireApi</c> is the request format — <c>"completions"</c> (Chat
    /// Completions, <c>/v1/chat/completions</c>) or <c>"responses"</c> (Responses
    /// API, <c>/v1/responses</c>). It applies only to openai/azure providers;
    /// anthropic omits it. Legacy Lumi values are migrated: <c>"openai"</c> and
    /// <c>"anthropic"</c> map to <c>"completions"</c> (the SDK default).
    /// Unknown/empty → <c>"completions"</c>.
    /// </summary>
    public static string NormalizeWireApi(string? wireApi)
    {
        var v = (wireApi ?? string.Empty).Trim().ToLowerInvariant();
        return v == "responses" ? "responses" : "completions";
    }

    public static void NormalizeEndpoint(ByokEndpoint endpoint)
    {
        endpoint.Name = (endpoint.Name ?? string.Empty).Trim();
        endpoint.ProviderType = (endpoint.ProviderType ?? string.Empty).Trim().ToLowerInvariant();
        endpoint.BaseUrl = (endpoint.BaseUrl ?? string.Empty).Trim();
        endpoint.WireApi = NormalizeWireApi(endpoint.WireApi);
        endpoint.AzureApiVersion = string.IsNullOrWhiteSpace(endpoint.AzureApiVersion)
            ? null
            : endpoint.AzureApiVersion.Trim();
        endpoint.ApiKeyEnvVar = string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvVar)
            ? null
            : endpoint.ApiKeyEnvVar.Trim();
        // ApiKey is never trimmed — the key is opaque.
    }

    /// <summary>Normalizes a model entry by trimming fields in place.</summary>
    public static void NormalizeModel(ByokModel model)
    {
        model.EndpointId = (model.EndpointId ?? string.Empty).Trim();
        model.ModelId = (model.ModelId ?? string.Empty).Trim();
        model.DisplayName = (model.DisplayName ?? string.Empty).Trim();
        // Advanced token/rate limits: negative or zero values are meaningless (0 RPM means
        // "unlimited", not "never allow"), so normalize them to null ("inherit default").
        // This keeps persisted JSON tidy and lets the rate limiter treat null as a pure
        // passthrough — there is no separate "disabled" state to track.
        model.MaxOutputTokens = NormalizePositiveTokenLimit(model.MaxOutputTokens);
        model.MaxPromptTokens = NormalizePositiveTokenLimit(model.MaxPromptTokens);
        model.MaxRequestsPerMinute = NormalizePositiveTokenLimit(model.MaxRequestsPerMinute);
    }

    /// <summary>
    /// Coerces a token/rate limit into a clean positive value or <c>null</c>. Values &lt;= 0
    /// become <c>null</c> ("inherit the default / unlimited"); values are left intact otherwise.
    /// </summary>
    private static int? NormalizePositiveTokenLimit(int? value)
        => value is <= 0 ? null : value;

    /// <summary>
    /// Validates that <paramref name="url"/> is an absolute http/https URL. Returns <c>null</c>
    /// on success, otherwise a human-readable error message.
    /// </summary>
    public static string? ValidateBaseUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Base URL is required.";

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri))
            return "Base URL must be an absolute URL (e.g. https://api.example.com).";

        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
            return "Base URL must use http or https.";

        return null;
    }

    /// <summary>
    /// Detects Azure AI Foundry and Azure OpenAI host patterns.
    /// This helper is used for diagnostics/migrations only; BYOK2 does not auto-inject
    /// auth headers based on URL and instead relies on explicit endpoint headers.
    /// </summary>
    public static bool IsAzureAiFoundryUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return false;
        return url.Contains("services.ai.azure.com", StringComparison.OrdinalIgnoreCase)
               || url.Contains(".openai.azure.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Validates an endpoint after <see cref="NormalizeEndpoint"/> has been called. Returns
    /// <c>null</c> on success, otherwise a human-readable error message.
    /// </summary>
    public static string? ValidateEndpoint(ByokEndpoint endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint.Name))
            return "Endpoint name is required.";

        if (string.IsNullOrWhiteSpace(endpoint.ProviderType))
            return "Provider type is required.";

        var urlError = ValidateBaseUrl(endpoint.BaseUrl);
        if (urlError is not null)
            return urlError;

        // wireApi applies only to openai/azure providers; anthropic omits it entirely.
        if (endpoint.ProviderType != "anthropic" && string.IsNullOrWhiteSpace(endpoint.WireApi))
            return "API format is required (completions or responses).";

        if (endpoint.ProviderType == "azure" && string.IsNullOrWhiteSpace(endpoint.AzureApiVersion))
            return "Azure provider requires an API version.";

        // Note: an empty ApiKeyEnvVar in EnvVar mode is intentionally allowed — ResolveApiKey
        // falls back to LUMI_BYOK_API_KEY in that case, so there's nothing to validate here.

        return null;
    }

    /// <summary>
    /// Validates a model entry after <see cref="NormalizeModel"/> has been called.
    /// The endpoint (when provided) must also pass <see cref="ValidateEndpoint"/>.
    /// </summary>
    public static string? ValidateModel(ByokModel model, ByokEndpoint? endpoint)
    {
        if (string.IsNullOrWhiteSpace(model.DisplayName))
            return "Display name is required.";

        if (string.IsNullOrWhiteSpace(model.ModelId))
            return "Model ID is required.";

        if (string.IsNullOrWhiteSpace(model.EndpointId))
            return "Endpoint is required.";

        if (endpoint is null)
            return "Endpoint is missing.";

        if (!string.Equals(endpoint.Id, model.EndpointId, StringComparison.Ordinal))
            return "Endpoint mismatch.";

        // Advanced token/rate limits: NormalizeModel already converted <=0 to null, so a
        // non-null value here that is still negative means the JSON contained a value the
        // normalizer deliberately kept (i.e. a positive int). Nothing else to validate for v1.
        return null;
    }

    /// <summary>
    /// Resolves the API key for an endpoint using automatic source fallback.
    ///
    /// Resolution by <see cref="ByokApiKeyMode"/>:
    /// - <see cref="ByokApiKeyMode.CredentialStore"/> — read from the OS credential store
    ///   (key <c>Endpoint.{endpoint.Id}.ApiKey</c>). When <paramref name="keyStore"/> is null
    ///   or unsupported, or the entry is absent, falls through to environment variables.
    /// - <see cref="ByokApiKeyMode.Stored"/> — the plaintext <see cref="ByokEndpoint.ApiKey"/>
    ///   field, then env-var fallback.
    /// - <see cref="ByokApiKeyMode.EnvVar"/> — endpoint env var, then
    ///   <see cref="FallbackApiKeyEnvVar"/>.
    /// - <see cref="ByokApiKeyMode.None"/> — always <c>null</c> (no key sent).
    ///
    /// Plaintext is only read in <see cref="ByokApiKeyMode.Stored"/> mode. All authenticated
    /// modes may fall back to the endpoint-specific and global environment variables.
    ///
    /// <see cref="ByokApiKeyMode.None"/> is the only true opt-out: it short-circuits and
    /// always returns <c>null</c>, so local/no-auth endpoints (e.g. Ollama) can disable key
    /// injection intentionally regardless of any configured key or env var.
    /// </summary>
    /// <param name="endpoint">The endpoint to resolve the key for.</param>
    /// <param name="keyStore">
    /// Optional OS credential store. Required for <see cref="ByokApiKeyMode.CredentialStore"/>
    /// to actually return its key; when <c>null</c> that mode degrades to environment variables.
    /// </param>
    public static string? ResolveApiKey(ByokEndpoint endpoint, ISecureKeyStore? keyStore = null)
    {
        if (endpoint.ApiKeyMode == ByokApiKeyMode.None)
            return null;

        if (endpoint.ApiKeyMode == ByokApiKeyMode.Stored
            && !string.IsNullOrWhiteSpace(endpoint.ApiKey))
        {
            return endpoint.ApiKey;
        }

        if (endpoint.ApiKeyMode == ByokApiKeyMode.CredentialStore
            && keyStore is { IsSupported: true })
        {
            var storedKey = Task.Run(() => keyStore.GetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id)))
                .GetAwaiter().GetResult();
            if (!string.IsNullOrEmpty(storedKey))
                return storedKey;
        }

        return ResolveApiKeyFromEnvironment(endpoint);
    }

    private static string? ResolveApiKeyFromEnvironment(ByokEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvVar))
        {
            var endpointEnv = Environment.GetEnvironmentVariable(endpoint.ApiKeyEnvVar.Trim());
            if (!string.IsNullOrEmpty(endpointEnv))
                return endpointEnv;
        }

        var fallback = Environment.GetEnvironmentVariable(FallbackApiKeyEnvVar);
        return string.IsNullOrEmpty(fallback) ? null : fallback;
    }

    /// <summary>Find an endpoint by id. Returns <c>null</c> when not found.</summary>
    public static ByokEndpoint? FindEndpoint(UserSettings settings, string endpointId)
        => settings.ByokEndpoints.FirstOrDefault(e => e.Id == endpointId);

    /// <summary>Find a model entry by id. Returns <c>null</c> when not found.</summary>
    public static ByokModel? FindModel(UserSettings settings, string modelEntryId)
        => settings.ByokModels.FirstOrDefault(m => m.Id == modelEntryId);

    /// <summary>
    /// All endpoints that are enabled and pass <see cref="ValidateEndpoint"/>.
    /// </summary>
    /// <remarks>
    /// Non-mutating read: endpoint normalization/validation is done on a temporary copy so this
    /// helper never rewrites persisted settings as a side effect of querying.
    /// </remarks>
    public static IReadOnlyList<ByokEndpoint> GetValidEndpoints(UserSettings settings)
    {
        var list = new List<ByokEndpoint>();
        foreach (var e in settings.ByokEndpoints)
        {
            if (!e.IsEnabled) continue;
            var candidate = CloneEndpoint(e);
            NormalizeEndpoint(candidate);
            if (ValidateEndpoint(candidate) is null) list.Add(e);
        }
        return list;
    }

    /// <summary>
    /// All models that are enabled and point to a valid enabled endpoint.
    /// </summary>
    /// <remarks>
    /// Non-mutating read: model/endpoint normalization and validation are performed on temporary
    /// copies so this helper does not change persisted settings.
    /// </remarks>
    public static IReadOnlyList<ByokModel> GetValidModels(UserSettings settings)
    {
        var validEndpoints = GetValidEndpoints(settings);
        var list = new List<ByokModel>();
        foreach (var m in settings.ByokModels)
        {
            if (!m.IsEnabled) continue;
            var candidate = CloneModel(m);
            NormalizeModel(candidate);
            var endpoint = validEndpoints.FirstOrDefault(e => e.Id == candidate.EndpointId);
            if (endpoint is null) continue;
            if (ValidateModel(candidate, endpoint) is not null) continue;
            list.Add(m);
        }
        return list;
    }

    /// <summary>All model entries (enabled or not) pointing to a specific endpoint id.</summary>
    public static IReadOnlyList<ByokModel> GetModelsForEndpoint(UserSettings settings, string endpointId)
        => settings.ByokModels
            .Where(m => string.Equals(m.EndpointId, endpointId, StringComparison.Ordinal))
            .ToList();

    /// <summary>
    /// Builds a <see cref="ProviderConfig"/> from an endpoint. <c>ModelId</c> and
    /// <c>WireModel</c> are intentionally NOT set — callers supply the actual model id via
    /// <see cref="TryBuildProviderConfig"/>, which routes it to
    /// <c>SessionConfig.Model</c> via SDK fallback.
    /// When <paramref name="model"/> is supplied, its advanced token limits
    /// (<see cref="ByokModel.MaxOutputTokens"/>, <see cref="ByokModel.MaxPromptTokens"/>) are
    /// copied onto the resulting <c>ProviderConfig</c> so the SDK forwards them to the provider.
    /// <c>null</c> limits are left unset, preserving the provider/SDK default behavior.
    /// </summary>
    public static ProviderConfig BuildProviderConfig(ByokEndpoint endpoint, ByokModel? model = null, ISecureKeyStore? keyStore = null)
    {
        NormalizeEndpoint(endpoint);
        var validationError = ValidateEndpoint(endpoint);
        if (validationError is not null)
            throw new InvalidOperationException($"Invalid BYOK endpoint configuration: {validationError}");

        // Resolve the API key ONCE and reuse it everywhere. This matters for correctness, not
        // just perf: a ${apiKey} header placeholder must resolve to the EXACT same value as
        // pc.ApiKey (the SDK sends the latter as 'Authorization: Bearer'). Resolving twice would
        // re-read the environment and could, in principle, race if the env var changed between
        // the two calls — leaving the Authorization header and the api-key header disagreeing.
        var resolvedKey = ResolveApiKey(endpoint, keyStore);

        // Coerce the model's advanced limits through the same normalizer so a stray <=0 value
        // (e.g. from a hand-edited data.json) never reaches the SDK as an invalid cap.
        var maxOutputTokens = model is null ? null : NormalizePositiveTokenLimit(model.MaxOutputTokens);
        var maxPromptTokens = model is null ? null : NormalizePositiveTokenLimit(model.MaxPromptTokens);

        var pc = new ProviderConfig
        {
            Type = string.IsNullOrWhiteSpace(endpoint.ProviderType) ? "openai" : endpoint.ProviderType,
            BaseUrl = endpoint.BaseUrl,
            ApiKey = resolvedKey,
            // Token limits: null = inherit provider/SDK default (no cap sent). Verified to reach
            // the provider via ProviderConfig.MaxPromptTokens / MaxOutputTokens (SDK 1.0.1).
            MaxOutputTokens = maxOutputTokens,
            MaxPromptTokens = maxPromptTokens,
        };

        // WireApi only applies to openai/azure providers (valid values: "completions", "responses").
        // Anthropic uses its native Messages API; setting WireApi would be ignored.
        // Default to "completions" when the user hasn't explicitly set a value.
        if (pc.Type != "anthropic")
        {
            var wire = string.IsNullOrWhiteSpace(endpoint.WireApi) ? "completions" : endpoint.WireApi;
            pc.WireApi = wire;
        }

        if (endpoint.ProviderType == "azure" && !string.IsNullOrWhiteSpace(endpoint.AzureApiVersion))
        {
            pc.Azure = new AzureOptions { ApiVersion = endpoint.AzureApiVersion };
        }

        // Copy the user-configured custom headers.
        //
        // Header values support dynamic placeholders:
        //   ${apiKey} / ${byok.apiKey}   -> endpoint API key resolved via ApiKeyMode
        //   ${env:MY_VAR_NAME}           -> process environment variable value
        //
        // Unresolved dynamic placeholders are skipped (instead of being sent literally).
        if (endpoint.Headers is { Count: > 0 })
        {
            var resolvedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, rawValue) in endpoint.Headers)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!TryResolveHeaderValue(rawValue, resolvedKey, out var resolvedValue))
                    continue;

                resolvedHeaders[name] = resolvedValue;
            }

            if (resolvedHeaders.Count > 0)
                pc.Headers = resolvedHeaders;
        }

        return pc;
    }

    /// <summary>
    /// Resolves a single header value, applying dynamic placeholders. <paramref name="resolvedKey"/>
    /// is the already-resolved endpoint API key (computed once by the caller) so a header using
    /// <c>${apiKey}</c> is guaranteed to match the <c>Authorization: Bearer</c> key the SDK sends.
    /// </summary>
    private static bool TryResolveHeaderValue(string rawValue, string? resolvedKey, out string resolvedValue)
    {
        var value = rawValue ?? string.Empty;
        var trimmed = value.Trim();

        if (trimmed.Equals(ApiKeyHeaderToken, StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals(ApiKeyHeaderTokenLegacy, StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(resolvedKey))
            {
                resolvedValue = string.Empty;
                return false;
            }

            resolvedValue = resolvedKey;
            return true;
        }

        if (trimmed.StartsWith("${env:", StringComparison.OrdinalIgnoreCase)
            && trimmed.EndsWith('}'))
        {
            var envName = trimmed[6..^1].Trim();
            if (string.IsNullOrWhiteSpace(envName))
            {
                resolvedValue = string.Empty;
                return false;
            }

            var envValue = Environment.GetEnvironmentVariable(envName);
            if (string.IsNullOrEmpty(envValue))
            {
                resolvedValue = string.Empty;
                return false;
            }

            resolvedValue = envValue;
            return true;
        }

        resolvedValue = value;
        return true;
    }

    private static ByokEndpoint CloneEndpoint(ByokEndpoint endpoint)
        => new()
        {
            Id = endpoint.Id,
            Name = endpoint.Name,
            ProviderType = endpoint.ProviderType,
            BaseUrl = endpoint.BaseUrl,
            WireApi = endpoint.WireApi,
            AzureApiVersion = endpoint.AzureApiVersion,
            IsEnabled = endpoint.IsEnabled,
            ApiKeyMode = endpoint.ApiKeyMode,
            ApiKeyEnvVar = endpoint.ApiKeyEnvVar,
            ApiKey = endpoint.ApiKey,
            Headers = endpoint.Headers is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(endpoint.Headers, StringComparer.OrdinalIgnoreCase),
        };

    private static ByokModel CloneModel(ByokModel model)
        => new()
        {
            Id = model.Id,
            EndpointId = model.EndpointId,
            ModelId = model.ModelId,
            DisplayName = model.DisplayName,
            IsEnabled = model.IsEnabled,
            MaxOutputTokens = model.MaxOutputTokens,
            MaxPromptTokens = model.MaxPromptTokens,
            MaxRequestsPerMinute = model.MaxRequestsPerMinute,
        };

    /// <summary>
    /// Computes a stable, comparable signature for a <see cref="ProviderConfig"/> that
    /// captures every field the SDK uses to route requests. Two provider configs are
    /// routing-equivalent when their signatures match; a mismatch means a request made
    /// against the old session would be sent to a different endpoint and must NOT happen.
    /// Returns <c>null</c> when <paramref name="provider"/> is <c>null</c> (no BYOK).
    /// The ApiKey/BearerToken values are hashed (not stored in cleartext) so the signature
    /// is safe to log for diagnostics.
    /// </summary>
    public static string? BuildProviderSignature(ProviderConfig? provider)
    {
        if (provider is null) return null;

        var apiKeyHash = HashOpaqueSecret(provider.ApiKey);
        var bearerTokenHash = HashOpaqueSecret(provider.BearerToken);
        var azureApiVersion = provider.Azure?.ApiVersion?.Trim() ?? "";
        var headers = provider.Headers is null
            ? ""
            : string.Join("&",
                provider.Headers
                    .OrderBy(kv => kv.Key, StringComparer.Ordinal)
                    .Select(kv => $"{kv.Key}={HashOpaqueSecret(kv.Value)}"));

        // Token limits are part of the routing contract: changing them must invalidate any
        // cached session so the next request is created/resumed with the new caps baked into
        // the baked-in ProviderConfig. Without this, raising MaxOutputTokens would silently keep
        // using the old (smaller) cap until the session was recreated for an unrelated reason.
        var maxOutputTokens = provider.MaxOutputTokens?.ToString() ?? "";
        var maxPromptTokens = provider.MaxPromptTokens?.ToString() ?? "";

        return string.Join(
            "|",
            provider.Type?.Trim() ?? "",
            provider.BaseUrl?.Trim() ?? "",
            provider.WireApi?.Trim() ?? "",
            apiKeyHash,
            bearerTokenHash,
            azureApiVersion,
            headers,
            $"out={maxOutputTokens}",
            $"in={maxPromptTokens}");
    }

    /// <summary>
    /// Resolves a selected model token to its model entry + endpoint + the wire model id
    /// to pass to the SDK. Returns <c>false</c> (and null outputs) when:
    /// <list type="bullet">
    ///   <item>the token is non-BYOK,</item>
    ///   <item>the token references a missing model,</item>
    ///   <item>the model is disabled,</item>
    ///   <item>the model points at a missing / disabled / invalid endpoint.</item>
    /// </list>
    /// Callers must NOT fall back to Copilot silently on stale tokens — surface a BYOK error.
    /// </summary>
    public static bool TryResolveModel(
        UserSettings settings,
        string? selectedModel,
        out ByokModel? model,
        out ByokEndpoint? endpoint,
        out string? actualModelId)
    {
        model = null;
        endpoint = null;
        actualModelId = null;

        if (!TryParseModelToken(selectedModel, out var modelEntryId) || modelEntryId is null)
            return false;

        var foundModel = FindModel(settings, modelEntryId);
        if (foundModel is null || !foundModel.IsEnabled)
            return false;

        NormalizeModel(foundModel);

        var foundEndpoint = FindEndpoint(settings, foundModel.EndpointId);
        if (foundEndpoint is null || !foundEndpoint.IsEnabled)
            return false;

        NormalizeEndpoint(foundEndpoint);
        if (ValidateEndpoint(foundEndpoint) is not null)
            return false;

        if (ValidateModel(foundModel, foundEndpoint) is not null)
            return false;

        model = foundModel;
        endpoint = foundEndpoint;
        actualModelId = foundModel.ModelId;
        return true;
    }

    /// <summary>
    /// Entry point for session construction: resolves the selected model to a
    /// <see cref="ProviderConfig"/> and the wire model id to pass to
    /// <c>SessionConfig.Model</c>. Returns <c>null</c> for non-BYOK tokens (caller should
    /// use the regular non-BYOK session build path) or stale BYOK tokens (caller must
    /// surface a BYOK error and not fall back silently).
    /// </summary>
    public static ProviderConfig? TryBuildProviderConfig(
        UserSettings settings,
        string? selectedModel,
        out string? actualModelId,
        ISecureKeyStore? keyStore = null)
    {
        actualModelId = null;

        if (!TryResolveModel(settings, selectedModel, out var model, out var endpoint, out actualModelId))
            return null;

        // TryResolveModel guarantees model != null and endpoint != null when it returns true.
        // Pass the model so its advanced token limits (MaxOutputTokens/MaxPromptTokens) are
        // copied onto the ProviderConfig and forwarded to the provider by the SDK.
        return BuildProviderConfig(endpoint!, model, keyStore);
    }

    public static SessionModelRoute ResolveSessionModelRoute(
        UserSettings settings,
        string? selectedModel,
        string? persistedSessionId = null,
        string? persistedProviderSignature = null,
        ISecureKeyStore? keyStore = null,
        bool allowLegacyByWireId = false)
    {
        if (IsByokModel(selectedModel))
        {
            if (!TryResolveModel(settings, selectedModel, out var model, out var endpoint, out var wireModelId))
                return new(selectedModel, null, null, null, IsInvalidByok: true);

            return new(
                BuildModelToken(model!),
                wireModelId,
                model,
                BuildProviderConfig(endpoint!, model, keyStore),
                IsInvalidByok: false);
        }

        var hasLegacyEvidence = allowLegacyByWireId
            && !string.IsNullOrWhiteSpace(persistedSessionId)
            && !string.IsNullOrWhiteSpace(persistedProviderSignature);
        if (!hasLegacyEvidence)
            return new(selectedModel, selectedModel, null, null, IsInvalidByok: false);

        var candidates = new List<(ByokModel Model, ProviderConfig Provider)>();
        foreach (var model in settings.ByokModels.Where(model =>
                     model.IsEnabled
                     && string.Equals(model.ModelId, selectedModel, StringComparison.Ordinal)))
        {
            var endpoint = FindEndpoint(settings, model.EndpointId);
            if (endpoint is null || !endpoint.IsEnabled)
                continue;

            NormalizeModel(model);
            NormalizeEndpoint(endpoint);
            if (ValidateEndpoint(endpoint) is not null || ValidateModel(model, endpoint) is not null)
                continue;

            candidates.Add((model, BuildProviderConfig(endpoint, model, keyStore)));
        }

        if (candidates.Count != 1
            || !string.Equals(
                BuildProviderSignature(candidates[0].Provider),
                persistedProviderSignature,
                StringComparison.Ordinal))
        {
            return new(selectedModel, null, null, null, IsInvalidByok: true);
        }

        var legacy = candidates[0];
        return new(
            BuildModelToken(legacy.Model),
            legacy.Model.ModelId,
            legacy.Model,
            legacy.Provider,
            IsInvalidByok: false);
    }

    /// <summary>
    /// True when <paramref name="provider"/> carries a usable BYOK endpoint — i.e. it is set
    /// AND has a non-empty <c>BaseUrl</c>. This is the authoritative "is this a BYOK request?"
    /// check used by the non-BYOK block, rather than relying on the <c>byok:</c> token prefix.
    /// </summary>
    public static bool HasValidByokUrl(GitHub.Copilot.ProviderConfig? provider)
        => provider is not null
           && !string.IsNullOrWhiteSpace(provider.BaseUrl);

    /// <summary>
    /// Lightweight local validation for the UI's "Test" button — does not perform an HTTP
    /// request (avoids token charges). Returns <c>null</c> when the endpoint passes local
    /// validation, otherwise a human-readable error message describing the first issue found.
    /// </summary>
    /// <param name="endpoint">The endpoint to test.</param>
    /// <param name="keyStore">
    /// Optional OS credential store. Required for <see cref="ByokApiKeyMode.CredentialStore"/>
    /// to be recognized as a valid key source; when <c>null</c> (or unsupported) that mode
    /// degrades to environment variables, mirroring <see cref="ResolveApiKey"/>.
    /// </param>
    public static string? TestEndpoint(ByokEndpoint endpoint, ISecureKeyStore? keyStore = null)
    {
        NormalizeEndpoint(endpoint);
        var baseError = ValidateEndpoint(endpoint);
        if (baseError is not null) return baseError;

        // (Azure AI Foundry detection intentionally has no special test branch here: the SDK
        // already sends 'Authorization: Bearer', and Lumi does NOT auto-inject an 'api-key'
        // header. Foundry auth is configured explicitly via endpoint Headers with ${apiKey}.)

        switch (endpoint.ApiKeyMode)
        {
            case ByokApiKeyMode.None:
                // No key to verify — assume OK.
                return null;

            case ByokApiKeyMode.CredentialStore:
                if (keyStore is { IsSupported: true })
                {
                    var storedKey = Task.Run(() => keyStore.GetAsync(SecureKeyStoreFactory.EndpointKey(endpoint.Id)))
                        .GetAwaiter().GetResult();
                    if (!string.IsNullOrEmpty(storedKey))
                        return null;
                }

                return ResolveEnvVarError(endpoint);

            case ByokApiKeyMode.Stored:
                if (!string.IsNullOrWhiteSpace(endpoint.ApiKey))
                    return null;

                return ResolveEnvVarError(endpoint);

            case ByokApiKeyMode.EnvVar:
            default:
                return ResolveEnvVarError(endpoint);
        }
    }

    /// <summary>
    /// Shared env-var resolution + error message for the tail of <see cref="TestEndpoint"/>.
    /// Returns <c>null</c> when an env var resolves, otherwise a human-readable message naming
    /// the configured env var (if any) and the global fallback.
    /// </summary>
    private static string? ResolveEnvVarError(ByokEndpoint endpoint)
    {
        if (!string.IsNullOrWhiteSpace(endpoint.ApiKeyEnvVar))
        {
            var endpointEnvName = endpoint.ApiKeyEnvVar.Trim();
            var endpointEnvValue = Environment.GetEnvironmentVariable(endpointEnvName);
            if (!string.IsNullOrEmpty(endpointEnvValue))
                return null;

            var fallbackValue = Environment.GetEnvironmentVariable(FallbackApiKeyEnvVar);
            if (!string.IsNullOrEmpty(fallbackValue))
                return null;

            return $"API key not found. Set a stored API key, environment variable '{endpointEnvName}', or fallback '{FallbackApiKeyEnvVar}'.";
        }

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(FallbackApiKeyEnvVar)))
            return $"API key not found. Set a stored API key or fallback environment variable '{FallbackApiKeyEnvVar}'.";

        return null;
    }

    /// <summary>
    /// Computes a short, non-reversible hash of an opaque secret (API key, bearer token,
    /// or header value). Used by <see cref="BuildProviderSignature"/> so the signature
    /// stays stable across key edits and is safe to log.
    /// </summary>
    private static string HashOpaqueSecret(string? secret)
    {
        if (string.IsNullOrEmpty(secret)) return "";
        // Static HashData avoids allocating/ disposing a SHA256 instance per call. This runs on
        // every model selection and chat load (BuildProviderSignature hashes ApiKey, BearerToken,
        // and every header value), so the cheaper path is worth it.
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(secret));
        // 8 hex chars (~32 bits) is plenty for a routing-equivalence check.
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }
}