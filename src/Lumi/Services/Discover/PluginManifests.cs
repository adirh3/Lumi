using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Lumi.Services.Discover;

/// <summary>One plugin as a marketplace's <c>marketplace.json</c> describes it.</summary>
public sealed record MarketplaceListing(
    string Name,
    string? Description,
    string? Version,
    string? Author,
    string? AuthorUrl,
    string? Homepage,
    string? Repository,
    string? License,
    IReadOnlyList<string> Keywords,
    PluginSource? Source);

/// <summary>
/// Where a plugin's files live on GitHub: a folder of the marketplace repository, or another
/// repository. A plugin elsewhere (any other git URL) installs through the runtime but has no
/// source Lumi can preview.
/// </summary>
public sealed record PluginSource(string? RelativePath, GitHubRepoRef? Repository)
{
    /// <summary>The plugin's folder on GitHub, when it can be read for a preview.</summary>
    public GitHubRepoRef? Resolve(GitHubRepoRef? marketplaceRepository)
    {
        if (Repository is not null)
            return Repository;

        return RelativePath is not null && marketplaceRepository is not null
            ? marketplaceRepository.Under(RelativePath)
            : null;
    }
}

/// <summary>
/// Reads the de-facto plugin marketplace format shared by GitHub Copilot, VS Code and Claude Code:
/// a <c>marketplace.json</c> in a Git repository listing each plugin and where its files live.
/// </summary>
public static class MarketplaceManifest
{
    /// <summary>Where a marketplace repository keeps its catalog, in the order the runtimes look.</summary>
    public static readonly string[] Locations =
    [
        ".github/plugin/marketplace.json",
        ".claude-plugin/marketplace.json",
    ];

    public static IReadOnlyList<MarketplaceListing> Parse(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions.Lenient);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("plugins", out var plugins)
            || plugins.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var pluginRoot = root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object
            ? JsonText.String(metadata, "pluginRoot")
            : null;

        var listings = new List<MarketplaceListing>();
        foreach (var plugin in plugins.EnumerateArray())
        {
            if (plugin.ValueKind != JsonValueKind.Object || JsonText.String(plugin, "name") is not { Length: > 0 } name)
                continue;

            var (author, authorUrl) = JsonText.Person(plugin, "author");
            var keywords = JsonText.Strings(plugin, "keywords")
                .Concat(JsonText.Strings(plugin, "tags"))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            listings.Add(new MarketplaceListing(
                name,
                JsonText.String(plugin, "description"),
                JsonText.String(plugin, "version"),
                author,
                authorUrl,
                JsonText.String(plugin, "homepage"),
                JsonText.Url(plugin, "repository"),
                JsonText.String(plugin, "license"),
                keywords,
                ReadSource(plugin, pluginRoot)));
        }

        return listings;
    }

    private static PluginSource? ReadSource(JsonElement plugin, string? pluginRoot)
    {
        if (!plugin.TryGetProperty("source", out var source))
            return null;

        if (source.ValueKind == JsonValueKind.String)
        {
            var path = source.GetString();
            if (string.IsNullOrWhiteSpace(path))
                return null;

            if (GitHubRepoRef.TryParse(path) is { } repo && path.Contains("github.com", StringComparison.OrdinalIgnoreCase))
                return new PluginSource(null, repo);

            var relative = path.StartsWith("./", StringComparison.Ordinal) || pluginRoot is null
                ? path
                : $"{pluginRoot}/{path}";
            return new PluginSource(GitHubRepoRef.Combine(null, relative) ?? "", null);
        }

        if (source.ValueKind != JsonValueKind.Object)
            return null;

        var kind = JsonText.String(source, "source")?.ToLowerInvariant();
        var reference = JsonText.String(source, "ref") ?? JsonText.String(source, "sha");
        var subPath = JsonText.String(source, "path");
        if (kind == "github" && GitHubRepoRef.TryParse(JsonText.String(source, "repo")) is { } github)
            return new PluginSource(null, github with { Ref = reference ?? github.Ref, Path = GitHubRepoRef.Combine(null, subPath) });

        return GitHubRepoRef.TryParse(JsonText.String(source, "url")) is { } fromUrl
            ? new PluginSource(null, fromUrl with { Ref = reference ?? fromUrl.Ref, Path = GitHubRepoRef.Combine(null, subPath) })
            : null;
    }
}

/// <summary>What a plugin brings, read before it is installed.</summary>
public sealed record PluginContents(
    IReadOnlyList<string> Skills,
    IReadOnlyList<string> Agents,
    IReadOnlyList<PluginMcpServerInfo> McpServers,
    bool HasHooks,
    string? LogoUrl,
    string? Author = null)
{
    public static readonly PluginContents Empty = new([], [], [], false, null);
}

/// <summary>An MCP server a plugin declares, summarized for people: its URL, or the command that starts it.</summary>
public sealed record PluginMcpServerInfo(string Name, string Summary);

/// <summary>
/// Previews a plugin straight from its repository, following the Agent Plugins manifest and the
/// conventions the Copilot runtime installs from: an explicit list in <c>plugin.json</c> when the
/// author gave one, the conventional folders otherwise.
/// </summary>
public sealed class PluginInspector
{
    /// <summary>Manifest locations, in the order the runtimes resolve them.</summary>
    public static readonly string[] ManifestLocations =
    [
        "plugin.json",
        ".plugin/plugin.json",
        ".github/plugin/plugin.json",
        ".claude-plugin/plugin.json",
    ];

    private const string AwesomeCopilotExtension = "com.github.awesome-copilot";
    private const string CopilotExtension = "com.github.copilot";

    private readonly GitHubRepoReader _reader;

    public PluginInspector(GitHubRepoReader reader) => _reader = reader;

    public async Task<PluginContents> InspectAsync(GitHubRepoRef root, CancellationToken cancellationToken)
    {
        JsonElement manifest = default;
        var hasManifest = false;
        foreach (var location in ManifestLocations)
        {
            if (await _reader.TryGetTextAsync(root, location, cancellationToken).ConfigureAwait(false) is not { } text)
                continue;

            try
            {
                using var document = JsonDocument.Parse(text, JsonOptions.Lenient);
                manifest = document.RootElement.Clone();
                hasManifest = manifest.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                hasManifest = false;
            }

            break;
        }

        var awesome = hasManifest ? Extension(manifest, AwesomeCopilotExtension) : default;
        var copilot = hasManifest ? Extension(manifest, CopilotExtension) : default;

        var skills = await ReadNamesAsync(root, hasManifest ? manifest : default, awesome, "skills", "skills", directories: true, cancellationToken).ConfigureAwait(false);
        var agents = await ReadNamesAsync(root, hasManifest ? manifest : default, awesome, "agents", "agents", directories: false, cancellationToken).ConfigureAwait(false);
        var servers = await ReadMcpServersAsync(root, hasManifest ? manifest : default, cancellationToken).ConfigureAwait(false);

        var hasHooks = hasManifest && manifest.TryGetProperty("hooks", out var hooks) && hooks.ValueKind is not JsonValueKind.Null;
        if (!hasHooks)
            hasHooks = await _reader.TryGetTextAsync(root, "hooks/hooks.json", cancellationToken).ConfigureAwait(false) is not null;

        var logo = (copilot.ValueKind == JsonValueKind.Object ? JsonText.String(copilot, "logo") : null)
                   ?? (hasManifest ? JsonText.String(manifest, "logo") : null);

        return new PluginContents(
            skills,
            agents,
            servers,
            hasHooks,
            ToRawUrl(root, logo),
            hasManifest ? JsonText.Person(manifest, "author").Name : null);
    }

    private static JsonElement Extension(JsonElement manifest, string name)
        => manifest.TryGetProperty("extensions", out var extensions)
           && extensions.ValueKind == JsonValueKind.Object
           && extensions.TryGetProperty(name, out var extension)
           && extension.ValueKind == JsonValueKind.Object
            ? extension
            : default;

    /// <summary>
    /// Skill folders or agent files: an explicit list names them, a folder path is listed, and
    /// without either the conventional folder is.
    /// </summary>
    private async Task<IReadOnlyList<string>> ReadNamesAsync(
        GitHubRepoRef root,
        JsonElement manifest,
        JsonElement awesome,
        string property,
        string conventionalFolder,
        bool directories,
        CancellationToken cancellationToken)
    {
        var declared = new List<string>();
        var isList = false;
        if (manifest.ValueKind == JsonValueKind.Object)
        {
            declared.AddRange(JsonText.Strings(manifest, property));
            isList = manifest.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Array;
        }

        if (awesome.ValueKind == JsonValueKind.Object && JsonText.Strings(awesome, property).ToArray() is { Length: > 0 } listed)
        {
            declared.AddRange(listed);
            isList = true;
        }

        var names = new List<string>();
        var folders = new List<string>();
        foreach (var path in declared)
        {
            var trimmed = path.Trim().TrimEnd('/', '\\');
            if (trimmed.Length == 0 || trimmed == ".")
                continue;

            // A listed skill is its own folder, unless the list just names the conventional folder;
            // a single path is a folder of them. Agents are files, so a folder is anything else.
            var isItem = directories
                ? isList && !string.Equals(Path.GetFileName(trimmed), conventionalFolder, StringComparison.OrdinalIgnoreCase)
                : trimmed.EndsWith(".md", StringComparison.OrdinalIgnoreCase);
            if (isItem)
                names.Add(ItemName(trimmed));
            else
                folders.Add(trimmed);
        }

        if (declared.Count == 0)
            folders.Add(conventionalFolder);

        foreach (var folder in folders)
        {
            if (await _reader.TryListAsync(root, folder, cancellationToken).ConfigureAwait(false) is not { } entries)
                continue;

            names.AddRange(entries
                .Where(entry => directories
                    ? entry.IsDirectory
                    : !entry.IsDirectory && entry.Name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                      && !entry.Name.Equals("README.md", StringComparison.OrdinalIgnoreCase))
                .Select(entry => ItemName(entry.Name)));
        }

        return names.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task<IReadOnlyList<PluginMcpServerInfo>> ReadMcpServersAsync(
        GitHubRepoRef root,
        JsonElement manifest,
        CancellationToken cancellationToken)
    {
        if (manifest.ValueKind == JsonValueKind.Object && manifest.TryGetProperty("mcpServers", out var declared))
        {
            if (declared.ValueKind == JsonValueKind.Object)
                return ReadServers(declared);

            if (declared.ValueKind == JsonValueKind.String && declared.GetString() is { Length: > 0 } path)
                return await ReadServerFileAsync(root, path, cancellationToken).ConfigureAwait(false);
        }

        return await ReadServerFileAsync(root, ".mcp.json", cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<PluginMcpServerInfo>> ReadServerFileAsync(GitHubRepoRef root, string path, CancellationToken cancellationToken)
    {
        if (await _reader.TryGetTextAsync(root, path, cancellationToken).ConfigureAwait(false) is not { } text)
            return [];

        try
        {
            using var document = JsonDocument.Parse(text, JsonOptions.Lenient);
            return ReadServers(document.RootElement);
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>Accepts a file with an <c>mcpServers</c> or <c>servers</c> map, or the map itself.</summary>
    internal static IReadOnlyList<PluginMcpServerInfo> ReadServers(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return [];

        if (element.TryGetProperty("mcpServers", out var nested) && nested.ValueKind == JsonValueKind.Object)
            element = nested;
        else if (element.TryGetProperty("servers", out nested) && nested.ValueKind == JsonValueKind.Object)
            element = nested;

        var servers = new List<PluginMcpServerInfo>();
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (JsonText.String(entry.Value, "url") is { Length: > 0 } url)
            {
                servers.Add(new PluginMcpServerInfo(entry.Name, url));
                continue;
            }

            var command = JsonText.String(entry.Value, "command");
            if (string.IsNullOrWhiteSpace(command))
                continue;

            var summary = string.Join(' ', new[] { command }.Concat(JsonText.Strings(entry.Value, "args")));
            servers.Add(new PluginMcpServerInfo(entry.Name, summary));
        }

        return servers;
    }

    private static string ItemName(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/').TrimEnd('/'));
        foreach (var suffix in new[] { ".agent.md", ".md" })
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return name[..^suffix.Length];
        }

        return name;
    }

    private static string? ToRawUrl(GitHubRepoRef root, string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        if (Uri.TryCreate(relativePath, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https")
            return absolute.ToString();

        var path = GitHubRepoRef.Combine(root.Path, relativePath);
        return path is null
            ? null
            : $"https://raw.githubusercontent.com/{root.Owner}/{root.Repo}/{root.Ref ?? "HEAD"}/{path}";
    }
}

internal static class JsonOptions
{
    internal static readonly JsonDocumentOptions Lenient = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };
}

/// <summary>Tolerant readers for loosely specified JSON manifests.</summary>
internal static class JsonText
{
    public static string? String(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var value)
           && value.ValueKind == JsonValueKind.String
           && value.GetString() is { Length: > 0 } text
            ? text.Trim()
            : null;

    /// <summary>A string, or every string of an array.</summary>
    public static IEnumerable<string> Strings(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return [];

        return value.ValueKind switch
        {
            JsonValueKind.String when value.GetString() is { Length: > 0 } single => [single],
            JsonValueKind.Array => value.EnumerateArray()
                .Where(static item => item.ValueKind == JsonValueKind.String)
                .Select(static item => item.GetString() ?? "")
                .Where(static item => item.Length > 0)
                .ToArray(),
            _ => [],
        };
    }

    /// <summary>A person given as a name or as <c>{ name, url }</c>.</summary>
    public static (string? Name, string? Url) Person(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return (null, null);

        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString(), null),
            JsonValueKind.Object => (String(value, "name"), String(value, "url")),
            _ => (null, null),
        };
    }

    /// <summary>A URL given as a string or as <c>{ url }</c>.</summary>
    public static string? Url(JsonElement element, string property)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(property, out var value))
            return null;

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object => String(value, "url"),
            _ => null,
        };
    }
}
