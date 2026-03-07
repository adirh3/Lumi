using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Lumi.Services;

/// <summary>
/// Fetches a webpage and returns cleaned text content.
/// Short pages are returned inline. Long pages are saved to a temp file with an
/// inline preview — the agent can read the full file with standard shell tools.
/// Uses retry logic for transient failures and smart content extraction.
/// </summary>
public static partial class WebFetchService
{
    private const int InlinePreviewLength = 8000;
    private static readonly string TempDir = Path.Combine(Path.GetTempPath(), "lumi-web");

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
    })
    {
        Timeout = TimeSpan.FromSeconds(20),
        DefaultRequestHeaders =
        {
            { "User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/133.0.0.0 Safari/537.36" },
            { "Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8" },
            { "Accept-Language", "en-US,en;q=0.9" },
        }
    };

    private static bool IsTransientStatusCode(int code) =>
        code is 408 or 429 or 500 or 502 or 503 or 504;

    public static async Task<string> FetchAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return "Error: No URL provided.";

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)
            || (uri.Scheme != "http" && uri.Scheme != "https"))
            return "Error: Invalid URL. Provide a full URL starting with http:// or https://";

        // Try once, retry automatically on transient failure
        var (result, isTransient) = await TryFetchCoreAsync(uri);
        if (isTransient)
        {
            await Task.Delay(2000);
            (result, _) = await TryFetchCoreAsync(uri);
        }

        return result;
    }

    private static async Task<(string Result, bool IsTransient)> TryFetchCoreAsync(Uri uri)
    {
        try
        {
            var response = await Http.GetAsync(uri);

            if (!response.IsSuccessStatusCode)
            {
                var code = (int)response.StatusCode;
                var msg = $"Failed to fetch {uri.Host}: HTTP {code} {response.ReasonCode()}. " +
                          "Try a different source URL — do NOT retry this one.";
                return (msg, IsTransientStatusCode(code));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            if (!contentType.Contains("html") && !contentType.Contains("text") && !contentType.Contains("xml") && !contentType.Contains("json"))
                return ($"The URL returned non-text content ({contentType}). Try a different URL.", false);

            var html = await response.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(html))
                return ("The page returned empty content. Try a different source.", false);

            var text = HtmlToText(html);
            if (string.IsNullOrWhiteSpace(text))
                return ("Could not extract readable text from the page. It may require JavaScript. Try a different source.", false);

            // Short content: return inline, no file needed
            if (text.Length <= InlinePreviewLength)
                return (text, false);

            // Long content: save full text to temp file, return preview + path
            var filePath = SaveToTempFile(text, uri);
            var preview = text[..InlinePreviewLength];

            // Smart truncation: scan remaining text for section headings
            var remaining = text[InlinePreviewLength..];
            var headings = HeadingScanRegex().Matches(remaining);
            var sectionList = string.Join(", ", headings.Cast<Match>().Take(10).Select(m => m.Groups[1].Value.Trim()));

            var output = preview + $"\n\n[Content truncated — {text.Length:N0} chars total. Full content saved to: {filePath}]";
            if (sectionList.Length > 0)
                output += $"\n[Remaining sections: {sectionList}]";
            output += "\n[Use Get-Content or Select-String to read specific sections from the file]";
            return (output, false);
        }
        catch (TaskCanceledException)
        {
            return ($"Timed out fetching {uri.Host}. Try a different source URL.", true);
        }
        catch (HttpRequestException ex)
        {
            return ($"Failed to connect to {uri.Host}: {ex.Message}. Try a different source URL.", true);
        }
        catch (Exception ex)
        {
            return ($"Error fetching URL: {ex.Message}. Try a different source URL.", false);
        }
    }

    private static string SaveToTempFile(string text, Uri uri)
    {
        Directory.CreateDirectory(TempDir);
        CleanupOldFiles();

        var safeName = SanitizeFileName($"{uri.Host}{uri.AbsolutePath}");
        var filePath = Path.Combine(TempDir, safeName + ".txt");
        File.WriteAllText(filePath, text);
        return filePath;
    }

    private static void CleanupOldFiles()
    {
        try
        {
            foreach (var file in Directory.GetFiles(TempDir, "*.txt"))
            {
                if (File.GetLastWriteTimeUtc(file) < DateTime.UtcNow.AddHours(-1))
                    File.Delete(file);
            }
        }
        catch { /* best effort cleanup */ }
    }

    private static string SanitizeFileName(string name)
    {
        var sanitized = InvalidFileCharsRegex().Replace(name, "_");
        if (sanitized.Length > 120) sanitized = sanitized[..120];
        return sanitized.Trim('_');
    }

    private static string HtmlToText(string html)
    {
        // Phase 1: Remove invisible elements
        var cleaned = ScriptStyleRegex().Replace(html, " ");
        cleaned = CommentRegex().Replace(cleaned, " ");

        // Phase 2: Extract content area BEFORE noise stripping
        // Article is most specific (usually just content body), main is broader
        var article = ArticleRegex().Match(cleaned);
        if (article.Success && article.Groups[1].Value.Length > 100)
        {
            cleaned = article.Groups[1].Value;
        }
        else
        {
            var main = MainRegex().Match(cleaned);
            if (main.Success && main.Groups[1].Value.Length > 100)
                cleaned = main.Groups[1].Value;
        }

        // Phase 3: Strip noise AFTER extraction (catches nav/form/sidebar within article/main)
        cleaned = NoiseElementRegex().Replace(cleaned, " ");
        cleaned = AriaNoiseRegex().Replace(cleaned, " ");
        cleaned = ClassIdNoiseRegex().Replace(cleaned, " ");

        // Phase 4: Convert headings to markdown format for structure preservation
        cleaned = HeadingRegex().Replace(cleaned, match =>
        {
            var level = match.Groups[1].Value[0] - '0';
            var text = HtmlTagRegex().Replace(match.Groups[2].Value, "");
            text = WebUtility.HtmlDecode(text).Trim();
            return text.Length > 0 ? $"\n\n{new string('#', level)} {text}\n" : "\n";
        });

        // Phase 5: Block structure → plain text
        cleaned = BlockTagRegex().Replace(cleaned, "\n");
        cleaned = BrRegex().Replace(cleaned, "\n");
        cleaned = ListItemRegex().Replace(cleaned, "\n• ");
        cleaned = HtmlTagRegex().Replace(cleaned, "");
        cleaned = WebUtility.HtmlDecode(cleaned);

        // Phase 6: Cleanup
        cleaned = MultiSpaceRegex().Replace(cleaned, " ");
        cleaned = MultiNewlineRegex().Replace(cleaned, "\n\n");
        cleaned = BoilerplateTextRegex().Replace(cleaned, "");
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

    [GeneratedRegex(@"<(nav|footer|aside|header|form)\b[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex NoiseElementRegex();

    [GeneratedRegex(@"<article[^>]*>(.*?)</article>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ArticleRegex();

    [GeneratedRegex(@"<main[^>]*>(.*?)</main>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex MainRegex();

    [GeneratedRegex(@"<\w+[^>]*\brole\s*=\s*""(?:navigation|banner|complementary|search|contentinfo|toolbar|menubar|menu)""[^>]*>.*?</\w+>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex AriaNoiseRegex();

    [GeneratedRegex(@"<(?:div|section|ul|ol)\b[^>]*\b(?:class|id)\s*=\s*""[^""]*\b(?:sidebar|toc|table-of-content|breadcrumb|share|social|cookie|banner|toolbar|feedback|pagination|related-(?:articles|posts)|comments?-section|doc-outline|content-header|action-bar|page-actions|site-header|site-footer|mega-menu|alert-bar)[^""]*""[^>]*>.*?</(?:div|section|ul|ol)>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ClassIdNoiseRegex();

    [GeneratedRegex(@"<h([1-6])[^>]*>(.*?)</h\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex HeadingRegex();

    [GeneratedRegex(@"</(p|div|li|tr|section|article|header|footer|nav|main|aside|blockquote|figcaption|details|summary)\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BlockTagRegex();

    [GeneratedRegex(@"<br\s*/?\s*>", RegexOptions.IgnoreCase)]
    private static partial Regex BrRegex();

    [GeneratedRegex(@"<li[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex ListItemRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex HtmlTagRegex();

    [GeneratedRegex(@"[^\S\n]+")]
    private static partial Regex MultiSpaceRegex();

    [GeneratedRegex(@"\n{3,}")]
    private static partial Regex MultiNewlineRegex();

    [GeneratedRegex(@"[^\w\-.]")]
    private static partial Regex InvalidFileCharsRegex();

    [GeneratedRegex(@"^#{1,3}\s+(.+)$", RegexOptions.Multiline)]
    private static partial Regex HeadingScanRegex();

    [GeneratedRegex(@"(?:Share (?:via|on) (?:Facebook|Twitter|LinkedIn|X|Email)|Was this (?:page|article) helpful\??|Table [Oo]f [Cc]ontents|Skip to (?:main |)content|In this article|(?:Send |Give |)Feedback|Cookie (?:policy|consent|preferences)|Accept (?:all |)cookies|Sign [Ii]n to your account|(?:Previous|Next) (?:article|page)|Breadcrumb|Theme\s*Light\s*Dark\s*High contrast|Focus mode|Ask Learn|Edit (?:this page|on GitHub)|Print this (?:page|article)|Download (?:as |)PDF|Manage cookies|Open in new (?:window|tab)|Additional resources|Recommended content|Related articles|More info about |Submit and view feedback for|This product|This page|All page feedback|Choose language|Select version|Read in English|Save to collection|Add to collections|View all page feedback)", RegexOptions.IgnoreCase)]
    private static partial Regex BoilerplateTextRegex();
}
