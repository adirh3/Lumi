using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services.Discover;

/// <summary>One MCP server from an MCP registry (the official <c>server.json</c> format).</summary>
public sealed record McpRegistryServer(
    string Name,
    string Title,
    string? Description,
    string? Version,
    string? Publisher,
    string? AvatarUrl,
    int? Stars,
    string? License,
    string? RepositoryUrl,
    string? WebsiteUrl,
    string? Readme,
    IReadOnlyList<McpRegistryPackage> Packages,
    IReadOnlyList<McpRegistryRemote> Remotes)
{
    public bool HasRemote => Remotes.Count > 0;
}

public sealed record McpRegistryPackage(
    string RegistryType,
    string Identifier,
    string? Version,
    string Transport,
    IReadOnlyList<McpRegistryArgument> RuntimeArguments,
    IReadOnlyList<McpRegistryArgument> PackageArguments,
    IReadOnlyList<McpRegistryInput> EnvironmentVariables);

public sealed record McpRegistryRemote(string Type, string Url, IReadOnlyList<McpRegistryInput> Headers);

/// <summary>An environment variable or header the server reads; <see cref="Default"/> may hold <c>{variable}</c> placeholders.</summary>
public sealed record McpRegistryInput(string Name, string? Description, bool IsRequired, bool IsSecret, string? Default)
{
    /// <summary>The placeholders <see cref="Default"/> uses. Registries often mark these, not the input, as required.</summary>
    public IReadOnlyList<McpRegistryInput> Variables { get; init; } = [];
}

/// <summary>A command-line argument; <see cref="Value"/> may hold <c>{variable}</c> placeholders.</summary>
public sealed record McpRegistryArgument(
    string Type,
    string? Name,
    string? Value,
    string? Default,
    bool IsRequired,
    string? Description,
    IReadOnlyList<McpRegistryInput> Variables);

public sealed record McpRegistryPage(IReadOnlyList<McpRegistryServer> Servers, string? NextCursor, int? Total);

/// <summary>
/// Reads an MCP registry through the official registry API (<c>GET /v0.1/servers</c>), the same
/// API GitHub's MCP Registry and the community registry serve.
/// </summary>
public sealed class McpRegistryClient
{
    private const string GitHubMetaKey = "io.modelcontextprotocol.registry/publisher-provided";
    private const string OfficialMetaKey = "io.modelcontextprotocol.registry/official";

    private readonly HttpClient _http;

    public McpRegistryClient(HttpClient? http = null) => _http = http ?? DiscoverHttp.Client;

    public async Task<McpRegistryPage> ListAsync(
        string registryUrl,
        string? search,
        string? cursor,
        int limit,
        CancellationToken cancellationToken)
    {
        var url = $"{registryUrl.TrimEnd('/')}/v0.1/servers?limit={limit.ToString(CultureInfo.InvariantCulture)}&version=latest";
        if (!string.IsNullOrWhiteSpace(search))
            url += $"&search={Uri.EscapeDataString(search.Trim())}";
        if (!string.IsNullOrWhiteSpace(cursor))
            url += $"&cursor={Uri.EscapeDataString(cursor)}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ParsePage(json);
    }

    public static McpRegistryPage ParsePage(string json)
    {
        using var document = JsonDocument.Parse(json, JsonOptions.Lenient);
        var root = document.RootElement;
        var servers = new List<McpRegistryServer>();
        if (root.TryGetProperty("servers", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in items.EnumerateArray())
            {
                if (ParseEntry(item) is { } server)
                    servers.Add(server);
            }
        }

        string? next = null;
        int? total = null;
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            next = JsonText.String(metadata, "nextCursor") ?? JsonText.String(metadata, "next_cursor");
            if (metadata.TryGetProperty("total", out var totalValue) && totalValue.TryGetInt32(out var parsed))
                total = parsed;
        }

        // Without a version filter the registry can list a server's older releases too.
        var latest = servers
            .GroupBy(static server => server.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();
        return new McpRegistryPage(latest, next, total);
    }

    private static McpRegistryServer? ParseEntry(JsonElement item)
    {
        // Registry v0.1 wraps the server.json in { server, _meta }; older payloads were flat.
        var server = item.TryGetProperty("server", out var wrapped) && wrapped.ValueKind == JsonValueKind.Object ? wrapped : item;
        if (JsonText.String(server, "name") is not { } name)
            return null;

        if (item.TryGetProperty("_meta", out var entryMeta)
            && entryMeta.TryGetProperty(OfficialMetaKey, out var official)
            && JsonText.String(official, "status") is { } status
            && !status.Equals("active", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var github = server.TryGetProperty("_meta", out var meta)
                     && meta.TryGetProperty(GitHubMetaKey, out var provided)
                     && provided.TryGetProperty("github", out var githubMeta)
            ? githubMeta
            : default;

        var repositoryUrl = server.TryGetProperty("repository", out var repository) ? JsonText.String(repository, "url") : null;
        int? stars = github.ValueKind == JsonValueKind.Object
                     && github.TryGetProperty("stargazerCount", out var starValue)
                     && starValue.TryGetInt32(out var starCount)
            ? starCount
            : null;

        return new McpRegistryServer(
            name,
            JsonText.String(server, "title")
                ?? (github.ValueKind == JsonValueKind.Object ? JsonText.String(github, "displayName") : null)
                ?? FriendlyName(name),
            JsonText.String(server, "description"),
            JsonText.String(server, "version"),
            Publisher(name, repositoryUrl),
            github.ValueKind == JsonValueKind.Object ? JsonText.String(github, "ownerAvatarUrl") : null,
            stars,
            github.ValueKind == JsonValueKind.Object ? JsonText.String(github, "license") : null,
            repositoryUrl,
            JsonText.String(server, "websiteUrl"),
            github.ValueKind == JsonValueKind.Object ? JsonText.String(github, "readme") : null,
            ReadPackages(server),
            ReadRemotes(server));
    }

    private static IReadOnlyList<McpRegistryPackage> ReadPackages(JsonElement server)
    {
        if (!server.TryGetProperty("packages", out var packages) || packages.ValueKind != JsonValueKind.Array)
            return [];

        return packages.EnumerateArray()
            .Where(static package => package.ValueKind == JsonValueKind.Object)
            .Select(static package => new McpRegistryPackage(
                (JsonText.String(package, "registryType") ?? JsonText.String(package, "registry_name") ?? "").ToLowerInvariant(),
                JsonText.String(package, "identifier") ?? JsonText.String(package, "name") ?? "",
                JsonText.String(package, "version"),
                package.TryGetProperty("transport", out var transport) ? JsonText.String(transport, "type") ?? "stdio" : "stdio",
                ReadArguments(package, "runtimeArguments"),
                ReadArguments(package, "packageArguments"),
                ReadInputs(package, "environmentVariables")))
            .Where(static package => package.Identifier.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<McpRegistryRemote> ReadRemotes(JsonElement server)
    {
        if (!server.TryGetProperty("remotes", out var remotes) || remotes.ValueKind != JsonValueKind.Array)
            return [];

        return remotes.EnumerateArray()
            .Where(static remote => remote.ValueKind == JsonValueKind.Object)
            .Select(static remote => new McpRegistryRemote(
                (JsonText.String(remote, "type") ?? "streamable-http").ToLowerInvariant(),
                JsonText.String(remote, "url") ?? "",
                ReadInputs(remote, "headers")))
            .Where(static remote => remote.Url.Length > 0)
            .ToArray();
    }

    private static IReadOnlyList<McpRegistryArgument> ReadArguments(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var arguments) || arguments.ValueKind != JsonValueKind.Array)
            return [];

        return arguments.EnumerateArray()
            .Where(static argument => argument.ValueKind == JsonValueKind.Object)
            .Select(static argument => new McpRegistryArgument(
                JsonText.String(argument, "type") ?? "positional",
                JsonText.String(argument, "name"),
                JsonText.String(argument, "value"),
                JsonText.String(argument, "default"),
                argument.TryGetProperty("isRequired", out var required) && required.ValueKind == JsonValueKind.True,
                JsonText.String(argument, "description"),
                ReadVariables(argument)))
            .ToArray();
    }

    private static IReadOnlyList<McpRegistryInput> ReadVariables(JsonElement owner)
    {
        if (!owner.TryGetProperty("variables", out var variables) || variables.ValueKind != JsonValueKind.Object)
            return [];

        return variables.EnumerateObject()
            .Select(static variable => ReadInput(variable.Name, variable.Value))
            .ToArray();
    }

    private static IReadOnlyList<McpRegistryInput> ReadInputs(JsonElement owner, string property)
    {
        if (!owner.TryGetProperty(property, out var inputs) || inputs.ValueKind != JsonValueKind.Array)
            return [];

        return inputs.EnumerateArray()
            .Where(static input => input.ValueKind == JsonValueKind.Object && JsonText.String(input, "name") is not null)
            .Select(static input => ReadInput(JsonText.String(input, "name")!, input))
            .ToArray();
    }

    private static McpRegistryInput ReadInput(string name, JsonElement input)
        => new(
            name,
            JsonText.String(input, "description"),
            input.TryGetProperty("isRequired", out var required) && required.ValueKind == JsonValueKind.True,
            input.TryGetProperty("isSecret", out var secret) && secret.ValueKind == JsonValueKind.True,
            JsonText.String(input, "default") ?? JsonText.String(input, "value"))
        {
            Variables = ReadVariables(input),
        };

    /// <summary>"io.github.owner/my-server" → "My Server".</summary>
    internal static string FriendlyName(string name)
    {
        var last = name.Contains('/') ? name[(name.LastIndexOf('/') + 1)..] : name;
        // "owner/mcp" or "owner/server" says nothing; the owner is the better name.
        if (last.ToLowerInvariant() is "mcp" or "server" or "mcp-server" or "mcpserver" && name.Contains('/'))
        {
            var space = name[..name.LastIndexOf('/')];
            last = space.Contains('.') ? space[(space.LastIndexOf('.') + 1)..] : space;
        }

        last = Regex.Replace(last, "(?i)[-_.]?mcp[-_.]?server$|^mcp[-_.]server[-_.]?|[-_.]mcp$|^mcp[-_.]", "");
        var words = last.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return name;

        return string.Join(' ', words.Select(static word => word.ToLowerInvariant() is "ai" or "api" or "aws" or "gcp" or "sql" or "mcp" or "ui"
            ? word.ToUpperInvariant()
            : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>The owner from a GitHub repository, else the namespace of the registry name.</summary>
    private static string? Publisher(string name, string? repositoryUrl)
    {
        if (GitHubRepoRef.TryParse(repositoryUrl) is { } repo)
            return repo.Owner;

        var slash = name.IndexOf('/');
        if (slash <= 0)
            return null;

        var space = name[..slash];
        foreach (var prefix in new[] { "io.github.", "com.github." })
        {
            if (space.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return space[prefix.Length..];
        }

        return space;
    }
}

/// <summary>How Lumi will run a registry server, decided before anything is added.</summary>
public sealed record McpInstallPlan(
    McpServer Server,
    string Runtime,
    string? RuntimeCommand,
    IReadOnlyList<McpRegistryInput> RequiredInputs,
    bool NeedsSetup);

/// <summary>
/// Turns a registry entry into a Lumi MCP server the way the registry specification says a
/// client should run it: a hosted endpoint when there is one, otherwise the published package
/// through its package runner.
/// </summary>
public static class McpRegistryMapper
{
    private static readonly Regex Variable = new(@"\{([A-Za-z0-9_.-]+)\}", RegexOptions.CultureInvariant);

    /// <summary>Package types Lumi knows how to launch, in order of preference.</summary>
    private static readonly string[] PackagePreference = ["npm", "pypi", "nuget", "oci"];

    public static McpInstallPlan? Plan(McpRegistryServer entry, Func<string, bool>? isCommandAvailable = null)
    {
        isCommandAvailable ??= CommandLocator.IsAvailable;

        var streamable = entry.Remotes.FirstOrDefault(static remote => remote.Type is "streamable-http" or "http");
        if (streamable is not null)
            return PlanRemote(entry, streamable);

        var packages = entry.Packages
            .Where(static package => package.Transport == "stdio" && PackagePreference.Contains(package.RegistryType))
            .OrderBy(package => Array.IndexOf(PackagePreference, package.RegistryType))
            .ToArray();
        var package = packages.FirstOrDefault(candidate => isCommandAvailable(RuntimeFor(candidate)))
                      ?? packages.FirstOrDefault();
        if (package is not null)
            return PlanPackage(entry, package);

        return entry.Remotes.FirstOrDefault() is { } remote ? PlanRemote(entry, remote) : null;
    }

    private static McpInstallPlan PlanRemote(McpRegistryServer entry, McpRegistryRemote remote)
    {
        var server = NewServer(entry);
        server.ServerType = "remote";
        server.Url = remote.Url;
        var required = new List<McpRegistryInput>();
        foreach (var header in remote.Headers)
        {
            if (ResolveInput(header, required) is { } value)
                server.Headers[header.Name] = value;
        }

        return new McpInstallPlan(server, "remote", null, required, required.Count > 0 || Variable.IsMatch(remote.Url));
    }

    private static McpInstallPlan PlanPackage(McpRegistryServer entry, McpRegistryPackage package)
    {
        var server = NewServer(entry);
        server.ServerType = "local";
        var runtime = RuntimeFor(package);
        var required = new List<McpRegistryInput>();
        var args = new List<string>();
        var versioned = package.Version is { Length: > 0 } version && !version.Equals("latest", StringComparison.OrdinalIgnoreCase)
            ? version
            : null;

        switch (package.RegistryType)
        {
            case "npm":
                server.Command = OperatingSystem.IsWindows() ? "npx.cmd" : "npx";
                AppendArguments(args, package.RuntimeArguments, required);
                if (!args.Contains("-y") && !args.Contains("--yes"))
                    args.Insert(0, "-y");
                args.Add(versioned is null ? package.Identifier : $"{package.Identifier}@{versioned}");
                break;

            case "pypi":
                server.Command = "uvx";
                AppendArguments(args, package.RuntimeArguments, required);
                var hasFrom = package.RuntimeArguments.Any(static argument => argument.Name == "--from");
                args.Add(versioned is null || hasFrom ? package.Identifier : $"{package.Identifier}@{versioned}");
                break;

            case "nuget":
                server.Command = "dnx";
                args.Add(versioned is null ? package.Identifier : $"{package.Identifier}@{versioned}");
                AppendArguments(args, package.RuntimeArguments, required);
                if (!args.Contains("--yes"))
                    args.Add("--yes");
                args.Add("--");
                break;

            case "oci":
                server.Command = "docker";
                args.AddRange(["run", "-i", "--rm"]);
                AppendArguments(args, package.RuntimeArguments, required);
                foreach (var variable in package.EnvironmentVariables)
                    args.AddRange(["-e", variable.Name]);
                args.Add(versioned is null || package.Identifier.Contains(':') ? package.Identifier : $"{package.Identifier}:{versioned}");
                break;
        }

        AppendArguments(args, package.PackageArguments, required);
        server.Args = args;

        foreach (var variable in package.EnvironmentVariables)
        {
            if (ResolveInput(variable, required) is { } value)
                server.Env[variable.Name] = value;
        }

        var needsSetup = required.Count > 0 || args.Any(static argument => Variable.IsMatch(argument));
        return new McpInstallPlan(server, package.RegistryType, runtime, required, needsSetup);
    }

    private static void AppendArguments(List<string> args, IReadOnlyList<McpRegistryArgument> arguments, List<McpRegistryInput> required)
    {
        foreach (var argument in arguments)
        {
            var value = argument.Value ?? argument.Default;
            var resolved = value is null ? null : Resolve(value, argument.Variables, argument.IsRequired, variable => AddRequired(required, variable));

            if (argument.Type == "named" && argument.Name is { Length: > 0 } name)
            {
                if (resolved is null)
                {
                    args.Add(name);
                }
                else if (resolved.Length > 0)
                {
                    args.Add(name);
                    args.Add(resolved);
                }

                continue;
            }

            if (!string.IsNullOrEmpty(resolved))
                args.Add(resolved);
        }
    }

    /// <summary>
    /// The value to save for an environment variable or header, or null to leave it out. One that
    /// needs the user's value keeps its placeholder (or stays empty) and is listed as required, so
    /// the connector opens in the editor instead of starting with a placeholder for a key.
    /// </summary>
    private static string? ResolveInput(McpRegistryInput input, List<McpRegistryInput> required)
    {
        var needsValue = input.IsRequired && string.IsNullOrEmpty(input.Default);
        var isSecret = input.IsSecret;
        var blanked = false;
        var value = Resolve(
            input.Default ?? "",
            input.Variables,
            input.IsRequired,
            variable =>
            {
                needsValue = true;
                isSecret |= variable.IsSecret;
            },
            () => blanked = true);

        if (needsValue || (input.IsRequired && blanked))
        {
            AddRequired(required, isSecret == input.IsSecret ? input : input with { IsSecret = isSecret });
            return value;
        }

        // An optional value whose placeholder nobody filled would be sent half-written.
        return value.Length == 0 || blanked ? null : value;
    }

    /// <summary>
    /// Fills <c>{variable}</c> placeholders from their defaults. One the user must fill stays as
    /// written, so the editor shows exactly what to replace, and is passed to <paramref name="needed"/>;
    /// an optional one without a default is left out and reported through <paramref name="blanked"/>.
    /// </summary>
    private static string Resolve(
        string template,
        IReadOnlyList<McpRegistryInput> variables,
        bool isRequired,
        Action<McpRegistryInput> needed,
        Action? blanked = null)
        => Variable.Replace(template, match =>
        {
            var variable = variables.FirstOrDefault(candidate => candidate.Name == match.Groups[1].Value);
            if (variable?.Default is { Length: > 0 } fallback)
                return fallback;

            if (variable is not null && (variable.IsRequired || isRequired))
            {
                needed(variable);
                return match.Value;
            }

            blanked?.Invoke();
            return "";
        });

    private static void AddRequired(List<McpRegistryInput> required, McpRegistryInput input)
    {
        if (!required.Any(existing => existing.Name == input.Name))
            required.Add(input);
    }

    private static McpServer NewServer(McpRegistryServer entry)
        => new()
        {
            Name = entry.Title,
            Description = entry.Description ?? "",
            CatalogId = entry.Name,
            IsEnabled = true,
        };

    /// <summary>The command a package needs on the PATH.</summary>
    public static string RuntimeFor(McpRegistryPackage package) => package.RegistryType switch
    {
        "npm" => "npx",
        "pypi" => "uvx",
        "nuget" => "dnx",
        "oci" => "docker",
        _ => package.RegistryType,
    };
}

/// <summary>Finds commands on the PATH a launched MCP server gets: on macOS and Linux, the user's shell PATH as well.</summary>
public static class CommandLocator
{
    public static bool IsAvailable(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var extensions = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [""];
        foreach (var directory in UnixShellPath.Augment(Environment.GetEnvironmentVariable("PATH")).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions.Prepend(""))
            {
                try
                {
                    if (File.Exists(Path.Combine(directory.Trim('"'), command + extension)))
                        return true;
                }
                catch (ArgumentException)
                {
                    break;
                }
            }
        }

        return false;
    }
}
