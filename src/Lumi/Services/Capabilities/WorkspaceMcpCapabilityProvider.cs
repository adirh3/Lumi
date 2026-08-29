using System;
using System.Collections.Generic;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Reports MCP servers declared in a workspace's own configuration files.
/// </summary>
/// <remarks>
/// <para>
/// Everything else in this pipeline comes from the Copilot runtime, which is the point of the
/// refactor. MCP is the one exception: the CLI does not read <c>.vscode/mcp.json</c> or a repo's
/// root <c>.mcp.json</c>, so without this a workspace that declares its servers there would lose
/// them entirely. Skills and agents stay SDK-only — those the runtime does discover.
/// </para>
/// <para>
/// A server reported here has no owner in the runtime, so unlike a discovered one it cannot simply
/// be left to start itself: the descriptor carries the definition, and
/// <see cref="McpSessionPlanner"/> hands it to the session as configuration.
/// </para>
/// </remarks>
public sealed class WorkspaceMcpCapabilityProvider : ICapabilityProvider
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex VariablePattern = new(@"\$\{([A-Za-z0-9_]+(?::[^}]*)?)\}", RegexOptions.Compiled);

    /// <summary>Relative paths probed in each directory, in precedence order.</summary>
    private static readonly string[] ConfigPaths =
    [
        Path.Combine(".vscode", "mcp.json"),
        ".mcp.json",
    ];

    public string Id => "workspace-mcp";

    public Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);

        var files = new List<(string Path, string Directory)>();
        foreach (var directory in query.WorkingDirectories)
        {
            foreach (var relativePath in ConfigPaths)
            {
                var path = Path.Combine(directory, relativePath);
                if (File.Exists(path))
                    files.Add((path, directory));
            }
        }

        // Most workspaces declare nothing, so stay synchronous rather than paying for a thread hop
        // to discover there is no work.
        if (files.Count == 0)
            return Task.FromResult(CapabilityProviderResult.Empty);

        // Parsing is synchronous file IO and callers ask for a snapshot from the UI thread.
        return Task.Run(() =>
        {
            var servers = new Dictionary<string, CapabilityDescriptor>(NameComparer);
            foreach (var (path, directory) in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ReadConfigFile(path, directory, servers);
            }

            return new CapabilityProviderResult(servers.Values.ToArray());
        }, cancellationToken);
    }

    /// <summary>
    /// Reads one config file. A malformed or unreadable file is skipped rather than failing the
    /// load: the rest of the workspace's servers, and every other source, still stand.
    /// </summary>
    private static void ReadConfigFile(
        string path,
        string contextDirectory,
        Dictionary<string, CapabilityDescriptor> servers)
    {
        JsonElement root;
        try
        {
                        using var document = JsonDocument.Parse(
                File.ReadAllText(path),
                new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Debug.WriteLine($"[Capabilities] Workspace MCP config '{path}' skipped: {ex.Message}");
            return;
        }

        if (root.ValueKind != JsonValueKind.Object)
            return;

        // "servers" is the VS Code key; "mcpServers" is the convention used by root .mcp.json files.
        if (!TryGetObject(root, "servers", out var entries) && !TryGetObject(root, "mcpServers", out entries))
            return;

        foreach (var entry in entries.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(entry.Name) || entry.Value.ValueKind != JsonValueKind.Object)
                continue;

            if (ToServer(entry.Name, entry.Value, contextDirectory) is not { } server)
                continue;

            // First definition wins, matching the directory precedence of the query.
            servers.TryAdd(entry.Name, new CapabilityDescriptor
            {
                Kind = CapabilityKind.McpServer,
                Name = entry.Name,
                Origin = CapabilityOrigin.Workspace,
                Description = server.ServerType == "remote" ? server.Url : server.Command,
                SourcePath = contextDirectory,
                Glyph = CopilotSdkCapabilityProvider.McpGlyph,
                McpDefinition = server,
            });
        }
    }

    private static McpServer? ToServer(string name, JsonElement element, string contextDirectory)
    {
        var declaredType = GetString(element, "type")?.Trim().ToLowerInvariant();
        var url = Expand(GetString(element, "url"), contextDirectory);
        var command = Expand(GetString(element, "command"), contextDirectory);

        // Treat anything carrying a URL as remote: VS Code spells the type "http" or "sse".
        var isRemote = !string.IsNullOrWhiteSpace(url)
                       || declaredType is "http" or "sse" or "remote";

        if (isRemote)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            return new McpServer
            {
                Name = name,
                ServerType = "remote",
                Url = url,
                Headers = ReadStringMap(element, "headers", contextDirectory),
                IsEnabled = true,
            };
        }

        if (string.IsNullOrWhiteSpace(command))
            return null;

        return new McpServer
        {
            Name = name,
            ServerType = "local",
            Command = command,
            Args = ReadStringArray(element, "args", contextDirectory),
            Env = ReadStringMap(element, "env", contextDirectory),
            IsEnabled = true,
        };
    }

    private static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
        => parent.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement parent, string name)
        => parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<string> ReadStringArray(JsonElement parent, string name, string contextDirectory)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(item => Expand(item.GetString(), contextDirectory))
            .Where(static item => item is not null)
            .Select(static item => item!)
            .ToList();
    }

    private static Dictionary<string, string> ReadStringMap(JsonElement parent, string name, string contextDirectory)
    {
        var result = new Dictionary<string, string>(NameComparer);
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return result;

        foreach (var property in value.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.String)
                continue;

            if (Expand(property.Value.GetString(), contextDirectory) is { Length: > 0 } expanded)
                result[property.Name] = expanded;
        }

        return result;
    }

    /// <summary>
    /// Substitutes the workspace placeholders a checked-in config realistically uses, including the
    /// <c>${env:NAME}</c> form these files rely on for credentials. Anything else — VS Code's
    /// <c>${input:...}</c> prompts, for instance — is left verbatim rather than guessed at, so a
    /// server Lumi cannot faithfully reproduce fails visibly instead of silently wrongly.
    /// </summary>
    private static string? Expand(string? value, string contextDirectory)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return VariablePattern.Replace(value, match =>
        {
            var variable = match.Groups[1].Value;

            const string envPrefix = "env:";
            if (variable.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
                return Environment.GetEnvironmentVariable(variable[envPrefix.Length..]) ?? string.Empty;

            return variable switch
            {
                var v when v.Equals("workspaceFolder", StringComparison.OrdinalIgnoreCase)
                           || v.Equals("cwd", StringComparison.OrdinalIgnoreCase) => contextDirectory,
                var v when v.Equals("workspaceFolderBasename", StringComparison.OrdinalIgnoreCase)
                    => new DirectoryInfo(contextDirectory).Name,
                var v when v.Equals("userHome", StringComparison.OrdinalIgnoreCase)
                    => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                var v when v.Equals("pathSeparator", StringComparison.OrdinalIgnoreCase)
                    => Path.DirectorySeparatorChar.ToString(),
                _ => match.Value,
            };
        });
    }
}
