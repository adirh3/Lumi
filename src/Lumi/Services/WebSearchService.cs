using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web;

namespace Lumi.Services;

/// <summary>
/// Searches the web using DuckDuckGo's server-rendered HTML endpoint.
/// No API key required — parses the HTML results page directly.
/// </summary>
public static partial class WebSearchService
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36" },
            { "Accept", "text/html" },
            { "Accept-Language", "en-US,en;q=0.9" },
        }
    };

    public static async Task<(string Text, List<SearchResult> Results)> SearchWithResultsAsync(string query, int count = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ("Error: No search query provided.", []);

        try
        {
            var encoded = HttpUtility.UrlEncode(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await Http.GetStringAsync(url);
            var results = ParseResults(html, count);

            if (results.Count == 0)
                return ($"No results found for: {query}", []);

            var output = $"Search results for: {query}\n\n";
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                output += $"{i + 1}. {r.Title}\n   {r.Snippet}\n   {r.Url}\n\n";
            }
            return (output.TrimEnd(), results);
        }
        catch (TaskCanceledException)
        {
            return ("Search timed out. Try a simpler query.", []);
        }
        catch (Exception ex)
        {
            return ($"Search failed: {ex.Message}", []);
        }
    }

    public static async Task<string> SearchAsync(string query, int count = 5)
    {
        if (string.IsNullOrWhiteSpace(query))
            return "Error: No search query provided.";

        try
        {
            var encoded = HttpUtility.UrlEncode(query);
            var url = $"https://html.duckduckgo.com/html/?q={encoded}";
            var html = await Http.GetStringAsync(url);
            var results = ParseResults(html, count);

            if (results.Count == 0)
                return $"No results found for: {query}";

            var output = $"Search results for: {query}\n\n";
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                output += $"{i + 1}. {r.Title}\n   {r.Snippet}\n   {r.Url}\n\n";
            }
            return output.TrimEnd();
        }
        catch (TaskCanceledException)
        {
            return "Search timed out. Try a simpler query.";
        }
        catch (Exception ex)
        {
            return $"Search failed: {ex.Message}";
        }
    }

    private static List<SearchResult> ParseResults(string html, int maxResults)
    {
        var results = new List<SearchResult>();

        // DuckDuckGo HTML results have links wrapped in redirect URLs:
        // href="//duckduckgo.com/l/?uddg=https%3A%2F%2Factual-url.com&rut=..."
        // Title is the link text, snippet is in <a class="result__snippet">
        var resultBlocks = ResultBlockRegex().Matches(html);

        foreach (Match block in resultBlocks)
        {
            if (results.Count >= maxResults) break;

            var blockHtml = block.Groups[1].Value;

            // Extract URL from the result title link
            var linkMatch = TitleLinkRegex().Match(blockHtml);
            if (!linkMatch.Success) continue;

            var rawUrl = linkMatch.Groups[1].Value;
            var title = StripHtml(linkMatch.Groups[2].Value).Trim();

            // Resolve the actual URL from DuckDuckGo's redirect
            var actualUrl = ExtractActualUrl(rawUrl);
            if (actualUrl is null || title.Length == 0) continue;

            // Extract snippet
            var snippetMatch = SnippetRegex().Match(blockHtml);
            var snippet = snippetMatch.Success
                ? StripHtml(snippetMatch.Groups[1].Value).Trim()
                : "";

            results.Add(new SearchResult(title, snippet, actualUrl));
        }

        return results;
    }

    private static string? ExtractActualUrl(string href)
    {
        // DuckDuckGo wraps URLs: //duckduckgo.com/l/?uddg=<encoded_url>&rut=...
        if (href.Contains("uddg="))
        {
            var uddgMatch = UddgRegex().Match(href);
            if (uddgMatch.Success)
                return HttpUtility.UrlDecode(uddgMatch.Groups[1].Value);
        }

        // Direct URL (sometimes DuckDuckGo uses direct links)
        if (href.StartsWith("http"))
            return href;

        return null;
    }

    private static string StripHtml(string html)
    {
        var text = HtmlTagRegex().Replace(html, "");
        return HttpUtility.HtmlDecode(text);
    }

    [GeneratedRegex(@"<div[^>]*class=""[^""]*result[^""]*""[^>]*>(.*?)</div>\s*</div>", RegexOptions.Singleline)]
    private static partial Regex ResultBlockRegex();

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__a[^""]*""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex TitleLinkRegex();

    [GeneratedRegex(@"<a[^>]*class=""[^""]*result__snippet[^""]*""[^>]*>(.*?)</a>", RegexOptions.Singleline)]
    private static partial Regex SnippetRegex();

    [GeneratedRegex(@"uddg=([^&]+)")]
    private static partial Regex UddgRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    public sealed record SearchResult(string Title, string Snippet, string Url);
}
