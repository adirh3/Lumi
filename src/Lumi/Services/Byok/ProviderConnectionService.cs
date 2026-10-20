using System.Net;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using Lumi.Localization;
using Lumi.Models;

namespace Lumi.Services.Byok;

public sealed record ProviderModel(
    string Id, string Name, int? ContextTokens = null, bool? SupportsTools = null, bool IsFree = false,
    IReadOnlyList<string>? SupportedReasoningEfforts = null, string? DefaultReasoningEffort = null);

public sealed class ProviderConnectionException(string message, HttpStatusCode? statusCode = null) : Exception(message)
{
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class ProviderConnectionService : IDisposable
{
    // The runtime omits the field for "none", which cannot disable an external provider's default.
    private static readonly string[] EffortOrder = ["minimal", "low", "medium", "high", "xhigh", "max"];
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public ProviderConnectionService(HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<IReadOnlyList<ProviderModel>> DiscoverAsync(
        ByokEndpoint endpoint, string? key, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        cancellationToken = timeout.Token;
        var uri = BuildModelsUri(endpoint);
        var isGemini = ProviderCatalog.FromUrl(endpoint.BaseUrl)?.Id == "gemini";
        if (isGemini)
            uri = new Uri("https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        if (!string.IsNullOrEmpty(key))
        {
            if (isGemini)
                request.Headers.Add("x-goog-api-key", key);
            else if (endpoint.ConnectionProviderId == "foundry")
                request.Headers.Add("api-key", key);
            else if (endpoint.ProviderType == "anthropic")
            {
                request.Headers.Add("x-api-key", key);
                request.Headers.Add("anthropic-version", "2023-06-01");
            }
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        foreach (var (name, value) in endpoint.Headers)
        {
            if (!ByokConfigHelper.TryResolveHeaderValue(value, key, out var resolved))
                continue;
            request.Headers.Remove(name);
            if (!request.Headers.TryAddWithoutValidation(name, resolved))
                throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidHeader"));
        }
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new ProviderConnectionException(ErrorForStatus(response.StatusCode), response.StatusCode);
        if (response.Content.Headers.ContentLength is > 4_194_304)
            throw new ProviderConnectionException(Loc.Get("EasyConnect_CatalogTooLarge"));
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) != 0)
        {
            if (buffer.Length + read > 4_194_304)
                throw new ProviderConnectionException(Loc.Get("EasyConnect_CatalogTooLarge"));
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        try
        {
            using var json = await JsonDocument.ParseAsync(buffer, cancellationToken: cancellationToken);
            if (isGemini)
                return ParseGeminiModels(json.RootElement);
            return ParseModels(json.RootElement, endpoint.ProviderType == "anthropic" ? "/v1/messages"
                : endpoint.WireApi == "responses" ? "/v1/responses" : "/v1/chat/completions");
        }
        catch (JsonException)
        {
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidCatalog"));
        }
    }

    internal static Uri BuildModelsUri(ByokEndpoint endpoint)
    {
        var uri = ValidateBaseUri(endpoint.BaseUrl);
        var path = endpoint.BaseUrl.TrimEnd('/');
        if (endpoint.ProviderType == "anthropic" && !uri.AbsolutePath.TrimEnd('/').EndsWith("/v1", StringComparison.Ordinal))
            path += "/v1";
        return new Uri(path + "/models");
    }

    internal static string NormalizeFoundryBaseUrl(string baseUrl)
    {
        var uri = ValidateBaseUri(baseUrl);
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0)
            return uri.GetLeftPart(UriPartial.Authority) + "/openai/v1";
        if (path != "/openai/v1")
            throw new ProviderConnectionException(Loc.Get("EasyConnect_FoundryInvalidEndpoint"));
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static Uri ValidateBaseUri(string baseUrl)
    {
        if (ByokConfigHelper.ValidateBaseUrl(baseUrl) is not null
            || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || (uri.Scheme != Uri.UriSchemeHttps && !uri.IsLoopback))
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidUrl"));
        return uri;
    }

    internal static IReadOnlyList<ProviderModel> ParseModels(JsonElement root, string? requiredEndpoint = null)
    {
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidCatalog"));
        var models = new List<ProviderModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;
            if (!item.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String)
                continue;
            var id = idValue.GetString();
            if (string.IsNullOrWhiteSpace(id) || !seen.Add(id) || !IsChatModel(id))
                continue;
            if (item.TryGetProperty("architecture", out var architecture) && architecture.ValueKind == JsonValueKind.Object
                && architecture.TryGetProperty("output_modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Array
                && !modalities.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "text"))
                continue;
            var hasEndpointList = item.TryGetProperty("supported_endpoints", out var endpoints)
                && endpoints.ValueKind == JsonValueKind.Array;
            if (requiredEndpoint is not null && hasEndpointList
                && !endpoints.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == requiredEndpoint))
                continue;
            var name = GetString(item, "name") ?? GetString(item, "display_name");
            if (name is null && item.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
                name = GetString(metadata, "display_name");
            name ??= id;
            int? context = null;
            foreach (var field in new[] { "context_length", "max_context_length", "max_model_len" })
                if (item.TryGetProperty(field, out var length) && length.ValueKind == JsonValueKind.Number
                    && length.TryGetInt32(out var tokens) && tokens > 0)
                    context = tokens;
            bool? tools = null;
            if (item.TryGetProperty("supported_parameters", out var parameters) && parameters.ValueKind == JsonValueKind.Array)
                tools = parameters.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "tools");
            else if (item.TryGetProperty("supported_features", out var features) && features.ValueKind == JsonValueKind.Array)
                tools = features.EnumerateArray().Any(p => p.ValueKind == JsonValueKind.String && p.GetString() == "tools");
            else if (item.TryGetProperty("capabilities", out var capabilities) && capabilities.ValueKind == JsonValueKind.Object)
            {
                if (capabilities.TryGetProperty("inference", out var inference) && inference.ValueKind == JsonValueKind.False)
                    continue;
                if (capabilities.TryGetProperty("chat_completion", out var chatCompletion) && chatCompletion.ValueKind == JsonValueKind.False
                    && !(requiredEndpoint == "/v1/responses" && hasEndpointList))
                    continue;
                if (capabilities.TryGetProperty("completion_chat", out var chat) && chat.ValueKind == JsonValueKind.False)
                    continue;
                if (capabilities.TryGetProperty("function_calling", out var calling)
                    && calling.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    tools = calling.GetBoolean();
            }
            if (tools == false)
                continue;
            var free = IsFree(id);
            if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
            {
                var prices = new List<decimal>();
                foreach (var field in new[] { "prompt", "completion", "request" })
                {
                    if (!pricing.TryGetProperty(field, out var price))
                        continue;
                    if (price.ValueKind == JsonValueKind.Number && price.TryGetDecimal(out var numeric))
                        prices.Add(numeric);
                    else if (price.ValueKind == JsonValueKind.String
                             && decimal.TryParse(price.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out numeric))
                        prices.Add(numeric);
                }
                free |= prices.Count > 0 && prices.All(price => price == 0);
            }
            var (efforts, defaultEffort) = ReadReasoningEfforts(item);
            models.Add(new(id, name, context, tools, free, efforts, defaultEffort));
        }
        return models
            .OrderByDescending(m => m.SupportsTools == true)
            .ThenByDescending(m => m.IsFree)
            .ThenByDescending(m => m.ContextTokens >= 65536)
            .ThenBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
            .Take(500).ToArray();
    }

    internal static bool IsFree(string id)
        => id.EndsWith(":free", StringComparison.OrdinalIgnoreCase) || id == "openrouter/free";

    internal static ProviderModel WithFoundryReasoning(ProviderModel model, string wireApi)
        => model.SupportedReasoningEfforts is null
            && ProviderCatalog.GetFoundryReasoningEfforts(model.Id, wireApi) is { } efforts
                ? model with { SupportedReasoningEfforts = efforts }
                : model;

    private static (IReadOnlyList<string>? Efforts, string? DefaultEffort) ReadReasoningEfforts(JsonElement model)
    {
        var metadata = model.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object
            ? reasoning : model;
        var hasEfforts = metadata.TryGetProperty("supported_efforts", out var values)
            || model.TryGetProperty("supported_reasoning_efforts", out values);
        if (!hasEfforts || values.ValueKind != JsonValueKind.Array)
            return (null, null);

        var reported = values.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var efforts = EffortOrder.Where(reported.Contains).ToArray();
        var defaultEffort = GetString(metadata, "default_effort") ?? GetString(model, "default_reasoning_effort");
        return (efforts, efforts.FirstOrDefault(effort => string.Equals(effort, defaultEffort, StringComparison.OrdinalIgnoreCase)));
    }

    internal static IReadOnlyList<ProviderModel> ParseGeminiModels(JsonElement root)
    {
        if (!root.TryGetProperty("models", out var models) || models.ValueKind != JsonValueKind.Array)
            throw new ProviderConnectionException(Loc.Get("EasyConnect_InvalidCatalog"));
        var result = new List<ProviderModel>();
        foreach (var item in models.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || GetString(item, "name") is not { } name
                || !item.TryGetProperty("supportedGenerationMethods", out var methods)
                || methods.ValueKind != JsonValueKind.Array
                || !methods.EnumerateArray().Any(method => method.ValueKind == JsonValueKind.String && method.GetString() == "generateContent"))
                continue;
            var id = name.StartsWith("models/", StringComparison.Ordinal) ? name[7..] : name;
            if (!IsChatModel(id) || id.Contains("-image", StringComparison.OrdinalIgnoreCase))
                continue;
            int? context = null;
            if (item.TryGetProperty("inputTokenLimit", out var limit) && limit.ValueKind == JsonValueKind.Number
                && limit.TryGetInt32(out var tokens) && tokens > 0)
                context = tokens;
            result.Add(new(id, GetString(item, "displayName") ?? id, context));
        }
        return result.OrderBy(model => model.Name, StringComparer.OrdinalIgnoreCase).Take(500).ToArray();
    }

    private static string? GetString(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private static bool IsChatModel(string id)
        => !new[] { "embedding", "whisper", "tts-", "dall-e", "image-", "moderation", "transcribe", "rerank" }
            .Any(part => id.Contains(part, StringComparison.OrdinalIgnoreCase));

    internal static string ErrorForStatus(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => Loc.Get("EasyConnect_AuthFailed"),
        HttpStatusCode.PaymentRequired => Loc.Get("EasyConnect_NoCredits"),
        HttpStatusCode.TooManyRequests => Loc.Get("EasyConnect_RateLimited"),
        HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => Loc.Get("EasyConnect_NoModelDiscovery"),
        _ => string.Format(Loc.Get("EasyConnect_ProviderFailed"), (int)status)
    };

    internal static string DescribeFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("rate_limit", StringComparison.OrdinalIgnoreCase)
                || message.Contains("TooManyRequests", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 429", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("EasyConnect_RateLimited");
            if (message.Contains("insufficient", StringComparison.OrdinalIgnoreCase)
                && (message.Contains("credit", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("balance", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("quota", StringComparison.OrdinalIgnoreCase))
                || message.Contains("HTTP 402", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("EasyConnect_NoCredits");
            if (message.Contains("unauthorized", StringComparison.OrdinalIgnoreCase)
                || message.Contains("invalid api key", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
                || message.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("EasyConnect_AuthFailed");
            if (message.Contains("function tools with reasoning_effort are not supported", StringComparison.OrdinalIgnoreCase)
                && message.Contains("use /v1/responses", StringComparison.OrdinalIgnoreCase))
                return Loc.Get("EasyConnect_RequiresResponses");
        }
        return Loc.Get("EasyConnect_ConnectFailed");
    }

    public void Dispose()
    {
        if (_ownsHttp)
            _http.Dispose();
    }
}
