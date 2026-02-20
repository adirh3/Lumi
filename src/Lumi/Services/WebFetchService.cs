using System;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>
/// Fetches a webpage and returns cleaned text content.
/// Uses browser-like headers for better compatibility than the SDK's web_fetch.
/// </summary>
public static partial class WebFetchService
{
    private const int MaxContentLength = 8000;

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
            { "Accept-Language", "en-US,en;q=0.9" },
        }
    };

    public static async Task<string> FetchAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: No URL provided.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "Error: Invalid URL. Provide a full URL starting with http:// or https://";

        try
        {
            var response = await Http.GetAsync(uri);

            if (!response.IsSuccessStatusCode)
            {
                return $"Failed to fetch {uri.Host}: HTTP {(int)response.StatusCode} {response.ReasonCode()}. " +
                       "Try a different source URL — do NOT retry this one.";
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html") && !contentType.Contains("text") && !contentType.Contains("xml") && !contentType.Contains("json"))
            {
                return $"The URL returned non-text content ({contentType}). Try a different URL.";
            }

            var html = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(html))
                return "The page returned empty content. Try a different source.";

            var text = HtmlToText(html);
            if (string.IsNullOrWhiteSpace(text))
                return "Could not extract readable text from the page. It may require JavaScript. Try a different source.";

            if (text.Length > MaxContentLength)
                text = text[..MaxContentLength] + "\n\n[Content truncated — showing first ~8000 characters]";

            return text;
        }
        catch (TaskCanceledException)
        {
            return $"Timed out fetching {uri.Host}. Try a different source URL.";
        }
        catch (HttpRequestException ex)
        {
            return $"Failed to connect to {uri.Host}: {ex.Message}. Try a different source URL.";
        }
        catch (Exception ex)
        {
            return $"Error fetching URL: {ex.Message}. Try a different source URL.";
        }
    }

    private static string HtmlToText(string html)
    {
        // Remove script and style blocks entirely
        var cleaned = ScriptStyleRegex().Replace(html, " ");

        // Remove HTML comments
        cleaned = CommentRegex().Replace(cleaned, " ");

        // Replace block-level elements with newlines for readability
        cleaned = BlockTagRegex().Replace(cleaned, "\n");

        // Replace <br> variants with newlines
        cleaned = BrRegex().Replace(cleaned, "\n");

        // Remove all remaining HTML tags
        cleaned = HtmlTagRegex().Replace(cleaned, "");

        // Decode HTML entities
        cleaned = WebUtility.HtmlDecode(cleaned);

        // Collapse multiple whitespace/blank lines
        cleaned = MultiSpaceRegex().Replace(cleaned, " ");
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n\n");

        return cleaned.Trim();
    }

    private static string ReasonCode(this HttpResponseMessage response)
    {
        return response.StatusCode switch
        {
            HttpStatusCode.Forbidden => "Forbidden — this site blocks automated access",
            HttpStatusCode.NotFound => "Not Found — the page doesn't exist",
            HttpStatusCode.Unauthorized => "Unauthorized — login required",
            HttpStatusCode.TooManyRequests => "Too Many Requests — rate limited",
            HttpStatusCode.ServiceUnavailable => "Service Unavailable",
            HttpStatusCode.InternalServerError => "Internal Server Error",
            _ => response.ReasonPhrase ?? "Unknown error"
        };
    }

    [GeneratedRegex(@"<(script|style|noscript)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ScriptStyleRegex();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"</(p|div|h[1-6]|li|tr|section|article|header|footer|nav|main|aside|blockquote|figcaption|details|summary)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();
}
