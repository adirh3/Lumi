using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Lumi.Services;

/// <summary>
/// Searches the web using DuckDuckGo's server-rendered HTML endpoints.
/// No API key required — parses HTML results directly.
/// Uses retry logic with fallback to the lite endpoint for reliability.
/// </summary>
public static partial class WebSearchService
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        CookieContainer = new CookieContainer(),
        UseCookies = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
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

    public static async Task<(string Text, List<SearchResult> Results)> SearchWithResultsAsync(string query, int count = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ("Error: No search query provided.", []);

        // Try primary endpoint
        var (results, error) = await TryHtmlSearchAsync(query, count);

        // Retry once on failure after brief delay
        if (results is null || results.Count == 0)
        {
            await Task.Delay(1500);
            (results, error) = await TryHtmlSearchAsync(query, count);
        }

        // Fallback to lite endpoint (simpler HTML, more stable)
        if (results is null || results.Count == 0)
            (results, error) = await TryLiteSearchAsync(query, count);

        if (results is null || results.Count == 0)
            return (error ?? $"No results found for: {query}", []);

        return (FormatResults(query, results), results);
    }

    public static async Task<string> SearchAsync(string query, int count = 5)
    {
        var (text, _) = await SearchWithResultsAsync(query, count);
        return text;
    }

    private static string FormatResults(string query, List<SearchResult> results)
    {
        var output = $"Search results for: {query}\n\n";
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            output += $"{i + 1}. {r.Title}\n   {r.Snippet}\n   {r.Url}\n\n";
        }
        return output.TrimEnd();
    }

    // --- Primary endpoint: html.duckduckgo.com ---

    private static async Task<(List<SearchResult>? Results, string? Error)> TryHtmlSearchAsync(string query, int count)
    {
        try
        {
            var encoded = HttpUtility.UrlEncode(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await Http.GetStringAsync(url);
            var results = ParseHtmlResults(html, count);
            return (results, results.Count == 0 ? $"No results found for: {query}" : null);
        }
        catch (TaskCanceledException)
        {
            return (null, "Search timed out. Try a simpler query.");
        }
        catch (Exception ex)
        {
            return (null, $"Search failed: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseHtmlResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();
        var resultBlocks = ResultBlockRegex().Matches(html);

        foreach (Match block in resultBlocks)
        {
            if (results.Count >= maxResults) break;

            var blockHtml = block.Groups[1].Value;

            var linkMatch = TitleLinkRegex().Match(blockHtml);
            if (!linkMatch.Success) continue;

            var rawUrl = linkMatch.Groups[1].Value;
            var title = StripHtml(linkMatch.Groups[2].Value).Trim();

            var actualUrl = ExtractActualUrl(rawUrl);
            if (actualUrl is null || title.Length == 0) continue;
            if (actualUrl.Contains("duckduckgo.com")) continue;

            var snippetMatch = SnippetRegex().Match(blockHtml);
            if (!snippetMatch.Success)
                snippetMatch = SnippetAltRegex().Match(blockHtml);
            var snippet = snippetMatch.Success
                ? StripHtml(snippetMatch.Groups[1].Value).Trim()
                : "";

            results.Add(new SearchResult(title, snippet, actualUrl));
        }

        return results;
    }

    // --- Fallback endpoint: lite.duckduckgo.com ---

    private static async Task<(List<SearchResult>? Results, string? Error)> TryLiteSearchAsync(string query, int count)
    {
        try
        {
            var encoded = HttpUtility.UrlEncode(query);
            var url = $"https://lite.duckduckgo.com/lite/?q={encoded}";
            var html = await Http.GetStringAsync(url);
            var results = ParseLiteResults(html, count);
            return (results, results.Count == 0 ? $"No results found for: {query}" : null);
        }
        catch (TaskCanceledException)
        {
            return (null, "Search timed out. Try a simpler query.");
        }
        catch (Exception ex)
        {
            return (null, $"Search failed: {ex.Message}");
        }
    }

    private static List<SearchResult> ParseLiteResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        // Lite endpoint: links in <a rel="nofollow" href="...uddg=...">Title</a>
        // Snippets in <td class="result-snippet">...</td>
        var linkMatches = LiteLinkRegex().Matches(html);
        var snippetMatches = LiteSnippetRegex().Matches(html);

        for (int i = 0; i < linkMatches.Count && results.Count < maxResults; i++)
        {
            var rawUrl = linkMatches[i].Groups[1].Value;
            var title = StripHtml(linkMatches[i].Groups[2].Value).Trim();

            var actualUrl = ExtractActualUrl(rawUrl);
            if (actualUrl is null || title.Length == 0) continue;
            if (actualUrl.Contains("duckduckgo.com")) continue;

            var snippet = i < snippetMatches.Count
                ? StripHtml(snippetMatches[i].Groups[1].Value).Trim()
                : "";

            results.Add(new SearchResult(title, snippet, actualUrl));
        }

        return results;
    }

    // --- Shared utilities ---

    private static string? ExtractActualUrl(string href)
    {
        if (href.Contains("uddg="))
        {
            var uddgMatch = UddgRegex().Match(href);
            if (uddgMatch.Success)
                return HttpUtility.UrlDecode(uddgMatch.Groups[1].Value);
        }

        if (href.StartsWith("http"))
            return href;

        return null;
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, "");
        return HttpUtility.HtmlDecode(text);
    }

    // Primary endpoint regexes
    [GeneratedRegex(@"<div[^>]*class=""[^""]*result[^""]*""[^>]*>(.*?)</div>\s*</div>", RegexOptions.Singleline)]
    private static partial Regex ResultBlockRegex();

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex(@"<(?:span|div|td)[^>]*class=""[^""]*(?:result__body|snippet|result-snippet)[^""]*""[^>]*>(.*?)</(?:span|div|td)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex SnippetAltRegex();

    // Lite endpoint regexes
    [GeneratedRegex(@"<a[^>]*rel=""nofollow""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LiteLinkRegex();

    [GeneratedRegex(@"<td[^>]*class=""result-snippet""[^>]*>(.*?)</td>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex LiteSnippetRegex();

    // Shared regexes
    [GeneratedRegex(@"uddg=([^&]+)")]
    private static partial Regex UddgRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    public sealed record SearchResult(string Title, string Snippet, string Url);
}
