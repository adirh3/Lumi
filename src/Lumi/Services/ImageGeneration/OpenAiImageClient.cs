using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services.Byok;

namespace Lumi.Services.ImageGeneration;

public sealed class ImageAccountException(string message) : Exception(message);

/// <summary>An account's address and credentials, resolved once per request.</summary>
internal sealed record ImageAccountConnection(string BaseUrl, string? ApiKey, bool UsesApiKeyHeader, IReadOnlyDictionary<string, string> Headers)
{
    /// <summary>Microsoft Foundry: its model list is the whole catalog, so image models come from its deployments.</summary>
    public bool IsFoundry => UsesApiKeyHeader;

    public static ImageAccountConnection From(ByokEndpoint endpoint, ISecureKeyStore? keyStore)
        => From(endpoint, ByokConfigHelper.ResolveApiKey(endpoint, keyStore));

    /// <summary>A connection with a key the caller already holds, such as one being entered in the connect dialog.</summary>
    public static ImageAccountConnection From(ByokEndpoint endpoint, string? key)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in endpoint.Headers)
        {
            // Foundry's key travels in the api-key header this connection sets itself.
            if (!string.IsNullOrWhiteSpace(name) && ByokConfigHelper.TryResolveHeaderValue(value, key, out var resolved))
                headers[name] = resolved;
        }

        return new(endpoint.BaseUrl.TrimEnd('/'), key, endpoint.ConnectionProviderId == "foundry", headers);
    }
}

/// <summary>
/// Creates and edits images through the OpenAI Images API (<c>/images/generations</c>, <c>/images/edits</c>) that
/// OpenAI, Microsoft Foundry, Gemini, xAI and other OpenAI-compatible accounts serve.
/// </summary>
internal sealed class OpenAiImageClient(HttpClient http)
{
    private const long MaxImageBytes = 64L * 1024 * 1024;

    public static OpenAiImageClient Shared { get; } = new(new HttpClient { Timeout = TimeSpan.FromMinutes(5) });

    /// <summary>Creates one image, or edits <paramref name="inputImages"/> into one, and returns its bytes.</summary>
    public async Task<byte[]> CreateAsync(
        ImageAccountConnection account,
        string model,
        string prompt,
        string? size,
        string? quality,
        IReadOnlyList<string> inputImages,
        CancellationToken cancellationToken)
    {
        using var request = inputImages.Count == 0
            ? new HttpRequestMessage(HttpMethod.Post, account.BaseUrl + "/images/generations")
            {
                Content = new StringContent(BuildGenerationBody(model, prompt, size, quality), Encoding.UTF8, "application/json")
            }
            : new HttpRequestMessage(HttpMethod.Post, account.BaseUrl + "/images/edits")
            {
                Content = BuildEditBody(model, prompt, size, quality, inputImages)
            };
        Authorize(request, account);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageAccountException(DescribeError((int)response.StatusCode, body));

        return await ReadImageAsync(body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The image models an account offers, newest first by <see cref="OrderNewestFirst"/>: the image models it
    /// lists, or on Microsoft Foundry the names of its image deployments (what a request names as its model).
    /// </summary>
    public async Task<IReadOnlyList<string>> ListImageModelsAsync(ImageAccountConnection account, CancellationToken cancellationToken)
    {
        if (account.IsFoundry)
            return await ListFoundryImageDeploymentsAsync(account, cancellationToken).ConfigureAwait(false);

        using var request = new HttpRequestMessage(HttpMethod.Get, account.BaseUrl + "/models");
        Authorize(request, account);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageAccountException(DescribeError((int)response.StatusCode, body));

        var ids = new List<string>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } value
                        && IsImageModel(value))
                        ids.Add(value.StartsWith("models/", StringComparison.Ordinal) ? value[7..] : value);
                }
            }
        }
        catch (JsonException)
        {
            throw new ImageAccountException("The account's model list couldn't be read.");
        }

        return OrderNewestFirst(ids.Distinct(StringComparer.Ordinal)).ToList();
    }

    /// <summary>
    /// A Foundry resource's image deployments, read from its deployment list (the one data-plane API that lists
    /// deployments with the resource key) and named as requests name them.
    /// </summary>
    private async Task<IReadOnlyList<string>> ListFoundryImageDeploymentsAsync(ImageAccountConnection account, CancellationToken cancellationToken)
    {
        var resource = FoundryResourceUrl(account.BaseUrl);
        using var request = new HttpRequestMessage(HttpMethod.Get, resource + "/openai/deployments?api-version=2022-12-01");
        Authorize(request, account);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new ImageAccountException(DescribeError((int)response.StatusCode, body));

        var deployments = new List<(string Name, string Model)>();
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Object
                        && item.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } name)
                    {
                        var model = item.TryGetProperty("model", out var modelValue) ? modelValue.GetString() ?? name : name;
                        if (IsImageModel(model) || IsImageModel(name))
                            deployments.Add((name, model));
                    }
                }
            }
        }
        catch (JsonException)
        {
            throw new ImageAccountException("The resource's deployment list couldn't be read.");
        }

        // Ordered by the model each deploys, named by the deployment.
        string ModelOf((string Name, string Model) entry) => IsImageModel(entry.Model) ? entry.Model : entry.Name;
        var order = OrderNewestFirst(deployments.Select(ModelOf).Distinct(StringComparer.Ordinal)).ToList();
        return deployments
            .OrderBy(entry => order.IndexOf(ModelOf(entry)))
            .Select(static entry => entry.Name)
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A Foundry address (<c>https://x.services.ai.azure.com/openai/v1</c>) without its API path.</summary>
    internal static string FoundryResourceUrl(string baseUrl)
    {
        var trimmed = baseUrl.TrimEnd('/');
        var api = trimmed.IndexOf("/openai", StringComparison.OrdinalIgnoreCase);
        return api > 0 ? trimmed[..api] : trimmed;
    }

    /// <summary>Image models by name: GPT Image, DALL·E, Imagen, Grok image and FLUX families.</summary>
    internal static bool IsImageModel(string id)
    {
        var name = id.StartsWith("models/", StringComparison.Ordinal) ? id[7..] : id;
        return name.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("imagen", StringComparison.OrdinalIgnoreCase)
               || name.Contains("-image", StringComparison.OrdinalIgnoreCase) && name.StartsWith("grok", StringComparison.OrdinalIgnoreCase)
               || name.Contains("grok-imagine", StringComparison.OrdinalIgnoreCase)
               || name.Contains("flux", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Image models, strongest family first and newest first within it: the first is what "automatic" uses.</summary>
    internal static IEnumerable<string> OrderNewestFirst(IEnumerable<string> models)
        => models
            .Where(IsImageModel)
            .OrderBy(FamilyRank)
            .ThenByDescending(Version)
            // A dated snapshot and a lighter variant ("-mini") rank after the model they derive from.
            .ThenBy(static id => id.Contains("mini", StringComparison.OrdinalIgnoreCase) ? 1 : 0)
            .ThenBy(static id => id.Length)
            .ThenBy(static id => id, StringComparer.Ordinal);

    private static int FamilyRank(string id)
        => id.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase) ? 0
            : id.StartsWith("imagen", StringComparison.OrdinalIgnoreCase) ? 1
            : id.StartsWith("grok", StringComparison.OrdinalIgnoreCase) ? 2
            : id.Contains("flux", StringComparison.OrdinalIgnoreCase) ? 3
            : 4;

    // The first number in a model id ("gpt-image-2.5-flare" → 2.5), or 0.
    private static double Version(string id)
    {
        var start = -1;
        for (var i = 0; i < id.Length; i++)
        {
            if (char.IsDigit(id[i]))
            {
                start = i;
                break;
            }
        }
        if (start < 0)
            return 0;

        var end = start;
        while (end < id.Length && (char.IsDigit(id[end]) || id[end] == '.' && end + 1 < id.Length && char.IsDigit(id[end + 1])))
            end++;
        return double.TryParse(id[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var version) ? version : 0;
    }

    /// <summary>
    /// GPT Image's options (quality, sizes including auto, base64 always) for every model not known to be another
    /// family: a Foundry request names its deployment, whose name needn't say which model it deploys.
    /// </summary>
    private static bool UsesGptImageOptions(string model)
        => !(model.StartsWith("dall-e", StringComparison.OrdinalIgnoreCase)
             || model.StartsWith("imagen", StringComparison.OrdinalIgnoreCase)
             || model.StartsWith("grok", StringComparison.OrdinalIgnoreCase)
             || model.Contains("flux", StringComparison.OrdinalIgnoreCase));

    internal static string BuildGenerationBody(string model, string prompt, string? size, string? quality)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("model", model);
            json.WriteString("prompt", prompt);
            json.WriteNumber("n", 1);
            WriteOptions(json, model, size, quality);
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void WriteOptions(Utf8JsonWriter json, string model, string? size, string? quality)
    {
        if (UsesGptImageOptions(model))
        {
            // GPT Image models always answer in base64 and reject response_format.
            if (!string.IsNullOrWhiteSpace(size))
                json.WriteString("size", size);
            if (!string.IsNullOrWhiteSpace(quality))
                json.WriteString("quality", quality);
            return;
        }

        if (!string.IsNullOrWhiteSpace(size) && size != "auto")
            json.WriteString("size", size);
        json.WriteString("response_format", "b64_json");
    }

    private static MultipartFormDataContent BuildEditBody(
        string model, string prompt, string? size, string? quality, IReadOnlyList<string> inputImages)
    {
        var form = new MultipartFormDataContent
        {
            { new StringContent(model), "model" },
            { new StringContent(prompt), "prompt" },
            { new StringContent("1"), "n" },
        };
        if (!string.IsNullOrWhiteSpace(size) && (UsesGptImageOptions(model) || size != "auto"))
            form.Add(new StringContent(size), "size");
        if (UsesGptImageOptions(model) && !string.IsNullOrWhiteSpace(quality))
            form.Add(new StringContent(quality), "quality");
        if (!UsesGptImageOptions(model))
            form.Add(new StringContent("b64_json"), "response_format");

        var field = inputImages.Count > 1 ? "image[]" : "image";
        foreach (var path in inputImages)
        {
            var file = new ByteArrayContent(File.ReadAllBytes(path));
            file.Headers.ContentType = new MediaTypeHeaderValue(MimeTypeOf(path));
            form.Add(file, field, Path.GetFileName(path));
        }

        return form;
    }

    internal static string MimeTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".webp" => "image/webp",
        ".gif" => "image/gif",
        _ => "image/png",
    };

    private static void Authorize(HttpRequestMessage request, ImageAccountConnection account)
    {
        if (!string.IsNullOrEmpty(account.ApiKey))
        {
            if (account.UsesApiKeyHeader)
                request.Headers.Add("api-key", account.ApiKey);
            else
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", account.ApiKey);
        }

        foreach (var (name, value) in account.Headers)
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private async Task<byte[]> ReadImageAsync(string body, CancellationToken cancellationToken)
    {
        string? base64 = null;
        string? url = null;
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array
                && data.GetArrayLength() > 0)
            {
                var first = data[0];
                if (first.TryGetProperty("b64_json", out var b64) && b64.ValueKind == JsonValueKind.String)
                    base64 = b64.GetString();
                else if (first.TryGetProperty("url", out var link) && link.ValueKind == JsonValueKind.String)
                    url = link.GetString();
            }
        }
        catch (JsonException)
        {
            throw new ImageAccountException("The account answered with something other than an image.");
        }

        if (!string.IsNullOrEmpty(base64))
        {
            try
            {
                return Convert.FromBase64String(base64);
            }
            catch (FormatException)
            {
                throw new ImageAccountException("The account answered with an unreadable image.");
            }
        }

        if (url is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps)
        {
            using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is > MaxImageBytes)
                throw new ImageAccountException("The image couldn't be downloaded from the account.");
            return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        throw new ImageAccountException("The account didn't return an image.");
    }

    /// <summary>The provider's own error message when it sent one, else the status.</summary>
    internal static string DescribeError(int status, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Array && root.GetArrayLength() > 0)
                root = root[0];
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out var error))
            {
                var message = error.ValueKind == JsonValueKind.String
                    ? error.GetString()
                    : error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var text) ? text.GetString() : null;
                if (!string.IsNullOrWhiteSpace(message))
                    return $"The image account answered {status}: {message.Trim()}";
            }
        }
        catch (JsonException)
        {
        }

        return status switch
        {
            401 or 403 => $"The image account refused the request ({status}). Check its key in Settings → AI & Models.",
            404 => "The image account doesn't offer this image model (404). Pick another in Settings → AI & Models → Image generation.",
            429 => "The image account is rate limited or out of credit (429).",
            _ => $"The image account answered {status}.",
        };
    }
}
