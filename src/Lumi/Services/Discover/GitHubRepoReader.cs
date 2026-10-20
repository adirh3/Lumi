using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services.Discover;

/// <summary>A folder inside a GitHub repository at an optional ref.</summary>
public sealed record GitHubRepoRef(string Owner, string Repo, string? Ref = null, string? Path = null)
{
    private static readonly Regex OwnerRepo = new(
        @"^(?:https?://github\.com/|git@github\.com:|github:\s*)?(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/(?<repo>[A-Za-z0-9._-]+?)(?:\.git)?/?(?:#(?<ref>[^\s]+))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>
    /// Reads <c>owner/repo</c>, <c>owner/repo#ref</c>, a github.com URL or the runtime's
    /// "GitHub: owner/repo" description. Anything else is not a GitHub repository.
    /// </summary>
    public static GitHubRepoRef? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var match = OwnerRepo.Match(text.Trim());
        if (!match.Success)
            return null;

        var reference = match.Groups["ref"].Success ? match.Groups["ref"].Value : null;
        return new GitHubRepoRef(match.Groups["owner"].Value, match.Groups["repo"].Value, reference);
    }

    public string FullName => $"{Owner}/{Repo}";

    public string WebUrl => Path is { Length: > 0 }
        ? $"https://github.com/{Owner}/{Repo}/tree/{Ref ?? "HEAD"}/{Path}"
        : $"https://github.com/{Owner}/{Repo}";

    /// <summary>The same repository, at a folder relative to this one.</summary>
    public GitHubRepoRef Under(string? relativePath)
        => this with { Path = Combine(Path, relativePath) };

    internal static string? Combine(string? basePath, string? relativePath)
    {
        var segments = new List<string>();
        foreach (var part in $"{basePath}/{relativePath}".Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".")
                continue;
            if (part == "..")
            {
                if (segments.Count > 0)
                    segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(part);
        }

        return segments.Count == 0 ? null : string.Join('/', segments);
    }
}

/// <summary>An entry of a repository folder listing.</summary>
public sealed record GitHubEntry(string Name, bool IsDirectory);

/// <summary>
/// Reads files from public GitHub repositories for Discover's previews. Only GitHub's own hosts
/// ever see the user's token, which lifts the anonymous API rate limit when it is available.
/// </summary>
public sealed class GitHubRepoReader
{
    private readonly HttpClient _http;
    private readonly Func<string?> _tokenProvider;

    public GitHubRepoReader(HttpClient? http = null, Func<string?>? tokenProvider = null)
    {
        _http = http ?? DiscoverHttp.Client;
        _tokenProvider = tokenProvider ?? CopilotService.TryGetGitHubTokenForMcp;
    }

    /// <summary>A file's text, or null when it does not exist.</summary>
    public async Task<string?> TryGetTextAsync(GitHubRepoRef repo, string relativePath, CancellationToken cancellationToken)
    {
        var path = GitHubRepoRef.Combine(repo.Path, relativePath);
        if (path is null)
            return null;

        var url = $"https://raw.githubusercontent.com/{repo.Owner}/{repo.Repo}/{EscapePath(repo.Ref ?? "HEAD")}/{EscapePath(path)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        AddToken(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A folder's entries, or null when the folder does not exist.</summary>
    public async Task<IReadOnlyList<GitHubEntry>?> TryListAsync(GitHubRepoRef repo, string? relativePath, CancellationToken cancellationToken)
    {
        var path = GitHubRepoRef.Combine(repo.Path, relativePath) ?? "";
        var url = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/contents/{EscapePath(path)}";
        if (repo.Ref is { Length: > 0 } reference)
            url += $"?ref={Uri.EscapeDataString(reference)}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        AddToken(request);
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return null;

        return document.RootElement.EnumerateArray()
            .Select(static item => new GitHubEntry(
                item.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
                item.TryGetProperty("type", out var type) && type.GetString() == "dir"))
            .Where(static entry => entry.Name.Length > 0)
            .ToArray();
    }

    private void AddToken(HttpRequestMessage request)
    {
        if (_tokenProvider() is { Length: > 0 } token)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    private static string EscapePath(string path)
        => string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
}

/// <summary>The HTTP client Discover shares for GitHub and MCP registry requests.</summary>
internal static class DiscoverHttp
{
    internal static readonly HttpClient Client = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Lumi-Discover/1.0");
        return client;
    }
}
