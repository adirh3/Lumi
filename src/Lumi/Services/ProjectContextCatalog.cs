using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Text.Json;
using GitHub.Copilot;
using Lumi.Models;

namespace Lumi.Services;

/// <summary>
/// Effective Copilot context for a chat/project.
/// Skills and agents include project folders plus user/built-in Copilot definitions;
/// MCP servers include only project-context .vscode/mcp.json definitions.
/// </summary>
public sealed class ProjectContextCatalogSnapshot
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IReadOnlyDictionary<string, CopilotSkillDefinition> _skillsByName;
    private readonly IReadOnlyDictionary<string, CopilotAgentDefinition> _agentsByName;

    public ProjectContextCatalogSnapshot(
        IReadOnlyList<CopilotSkillDefinition> skills,
        IReadOnlyList<CopilotAgentDefinition> agents,
        IReadOnlyList<ProjectContextMcpServerDefinition> mcpServers,
        IReadOnlyList<string>? skillDirectories = null,
        IReadOnlyList<ProjectContextCatalogDiagnostic>? diagnostics = null)
    {
        Skills = skills.ToArray();
        Agents = agents.ToArray();
        McpServers = mcpServers.ToArray();
        SkillDirectories = skillDirectories?.ToArray() ?? [];
        Diagnostics = diagnostics?.ToArray() ?? [];
        _skillsByName = BuildFirstByName(Skills, static skill => skill.Name);
        _agentsByName = BuildFirstByName(Agents, static agent => agent.Name);
    }

    public IReadOnlyList<CopilotSkillDefinition> Skills { get; }

    public IReadOnlyList<CopilotAgentDefinition> Agents { get; }

    public IReadOnlyList<ProjectContextMcpServerDefinition> McpServers { get; }

    /// <summary>
    /// Workspace <c>.github/skills</c> root directories discovered from the effective working
    /// directory (including monorepo subfolders). Passed to <c>config.SkillDirectories</c> so the
    /// native Copilot skill tool can load these skills directly.
    /// </summary>
    public IReadOnlyList<string> SkillDirectories { get; }

    public IReadOnlyList<ProjectContextCatalogDiagnostic> Diagnostics { get; }

    public CopilotSkillDefinition? FindSkill(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _skillsByName.TryGetValue(name, out var skill) ? skill : null;
    }

    public CopilotAgentDefinition? FindAgent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _agentsByName.TryGetValue(name, out var agent) ? agent : null;
    }

    private static IReadOnlyDictionary<string, TDefinition> BuildFirstByName<TDefinition>(
        IEnumerable<TDefinition> definitions,
        Func<TDefinition, string> getName)
    {
        var result = new Dictionary<string, TDefinition>(NameComparer);
        foreach (var definition in definitions)
        {
            var name = getName(definition);
            if (!string.IsNullOrWhiteSpace(name))
                result.TryAdd(name, definition);
        }

        return result;
    }
}

public sealed record ProjectContextMcpServerDefinition(
    string Name,
    McpServerConfig Config,
    string SourcePath,
    string SourceDirectory);

public sealed record ProjectContextCatalogDiagnostic(string SourcePath, string Message);

/// <summary>
/// Loads all project-sensitive Copilot context from one place so consumers work with
/// discovered skills, agents, and MCP servers instead of source folders.
/// </summary>
public static class ProjectContextCatalog
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly Regex VariablePattern = new(@"\$\{([^}]+)\}", RegexOptions.Compiled);

    public static ProjectContextCatalogSnapshot Discover(
        string effectiveWorkingDirectory,
        Project? project,
        string? copilotRootOverride = null)
    {
        var contextDirectories = ProjectContextDirectoryHelper.GetExistingContextDirectories(effectiveWorkingDirectory, project);
        var copilotCatalog = CopilotConfigCatalog.Discover(contextDirectories, copilotRootOverride);
        var skillDirectories = CopilotConfigCatalog.GetWorkspaceSkillDirectories(contextDirectories);
        var mcpCatalog = DiscoverMcpServers(contextDirectories);
        return new ProjectContextCatalogSnapshot(
            copilotCatalog.Skills,
            copilotCatalog.Agents,
            mcpCatalog.Servers,
            skillDirectories,
            mcpCatalog.Diagnostics);
    }

    private static (
        IReadOnlyList<ProjectContextMcpServerDefinition> Servers,
        IReadOnlyList<ProjectContextCatalogDiagnostic> Diagnostics) DiscoverMcpServers(IReadOnlyList<string> contextDirectories)
    {
        var servers = new Dictionary<string, ProjectContextMcpServerDefinition>(NameComparer);
        var diagnostics = new List<ProjectContextCatalogDiagnostic>();
        foreach (var contextDirectory in contextDirectories)
        {
            var mcpJsonPath = Path.Combine(contextDirectory, ".vscode", "mcp.json");
            if (!File.Exists(mcpJsonPath))
                continue;

            try
            {
                using var stream = File.OpenRead(mcpJsonPath);
                using var doc = JsonDocument.Parse(stream);
                if (!TryGetMcpServerEntries(doc.RootElement, out var serverEntries, out var rootWarning))
                    continue;

                if (!string.IsNullOrWhiteSpace(rootWarning))
                {
                    diagnostics.Add(new ProjectContextCatalogDiagnostic(
                        mcpJsonPath,
                        rootWarning));
                }

                foreach (var serverEntry in serverEntries.EnumerateObject())
                {
                    if (!TryCreateMcpServerConfig(serverEntry.Value, contextDirectory, out var config, out var error))
                    {
                        diagnostics.Add(new ProjectContextCatalogDiagnostic(
                            mcpJsonPath,
                            $"Skipped MCP server '{serverEntry.Name}': {error}"));
                        continue;
                    }

                    var definition = new ProjectContextMcpServerDefinition(
                        serverEntry.Name,
                        config,
                        mcpJsonPath,
                        contextDirectory);
                    if (!servers.TryAdd(serverEntry.Name, definition))
                    {
                        diagnostics.Add(new ProjectContextCatalogDiagnostic(
                            mcpJsonPath,
                            $"Ignored duplicate MCP server '{serverEntry.Name}' because an earlier project context folder already defines it."));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                diagnostics.Add(new ProjectContextCatalogDiagnostic(
                    mcpJsonPath,
                    $"Failed to load MCP config: {ex.Message}"));
            }
        }

        return (servers.Values.ToList(), diagnostics);
    }

    private static bool TryCreateMcpServerConfig(
        JsonElement server,
        string contextDirectory,
        out McpServerConfig config,
        out string? error)
    {
        config = null!;
        var type = GetOptionalStringProperty(server, "type")?.Trim().ToLowerInvariant() ?? "stdio";

        if (type is "sse" or "http" or "streamable-http")
        {
            var url = ExpandMcpVariables(GetOptionalStringProperty(server, "url"), contextDirectory)?.Trim();
            if (!IsValidRemoteMcpUrl(url))
            {
                error = "remote MCP server requires an absolute http/https URL";
                return false;
            }

            var remote = new McpHttpServerConfig
            {
                Url = url!,
                Tools = ["*"]
            };

            if (!TryReadStringMap(server, "headers", contextDirectory, out var headers, out error))
                return false;

            if (headers.Count > 0)
                remote.Headers = headers;

            config = remote;
            error = null;
            return true;
        }

        if (type is not "stdio")
        {
            error = $"unsupported MCP server type '{type}'";
            return false;
        }

        var command = ExpandMcpVariables(GetOptionalStringProperty(server, "command"), contextDirectory)?.Trim();
        if (string.IsNullOrWhiteSpace(command))
        {
            error = "stdio MCP server requires a non-empty command";
            return false;
        }

        if (!TryReadStringArray(server, "args", contextDirectory, out var args, out error))
            return false;

        var local = new McpStdioServerConfig
        {
            Command = command.Trim(),
            Args = args,
            WorkingDirectory = contextDirectory,
            Tools = ["*"]
        };

        if (!TryReadStringMap(server, "env", contextDirectory, out var env, out error))
            return false;

        if (env.Count > 0)
            local.Env = env;

        config = local;
        error = null;
        return true;
    }

    private static bool TryGetMcpServerEntries(JsonElement root, out JsonElement serverEntries, out string? warning)
    {
        warning = null;
        var hasServers = root.TryGetProperty("servers", out var servers)
            && servers.ValueKind == JsonValueKind.Object;
        var hasMcpServers = root.TryGetProperty("mcpServers", out var mcpServers)
            && mcpServers.ValueKind == JsonValueKind.Object;

        if (hasServers)
        {
            serverEntries = servers;
            if (hasMcpServers)
                warning = "Both 'servers' and 'mcpServers' are defined in MCP config; using 'servers'.";
            return true;
        }

        if (hasMcpServers)
        {
            serverEntries = mcpServers;
            return true;
        }

        serverEntries = default;
        return false;
    }

    private static string? GetOptionalStringProperty(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool IsValidRemoteMcpUrl(string? url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
    }

    private static bool TryReadStringArray(
        JsonElement element,
        string propertyName,
        string contextDirectory,
        out List<string> values,
        out string? error)
    {
        values = [];
        error = null;

        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (property.ValueKind != JsonValueKind.Array)
        {
            error = $"'{propertyName}' must be an array";
            return false;
        }

        foreach (var item in property.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                error = $"'{propertyName}' entries must be strings";
                return false;
            }

            values.Add(ExpandMcpVariables(item.GetString() ?? "", contextDirectory) ?? "");
        }

        return true;
    }

    private static bool TryReadStringMap(
        JsonElement element,
        string propertyName,
        string contextDirectory,
        out Dictionary<string, string> values,
        out string? error)
    {
        values = [];
        error = null;

        if (!element.TryGetProperty(propertyName, out var property)
            || property.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (property.ValueKind != JsonValueKind.Object)
        {
            error = $"'{propertyName}' must be an object";
            return false;
        }

        foreach (var entry in property.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                error = $"'{propertyName}' values must be strings";
                return false;
            }

            values[entry.Name] = ExpandMcpVariables(entry.Value.GetString() ?? "", contextDirectory) ?? "";
        }

        return true;
    }

    private static string? ExpandMcpVariables(string? value, string contextDirectory)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        return VariablePattern.Replace(value, match =>
        {
            var variable = match.Groups[1].Value;
            if (variable.Equals("workspaceFolder", StringComparison.OrdinalIgnoreCase)
                || variable.Equals("cwd", StringComparison.OrdinalIgnoreCase))
            {
                return contextDirectory;
            }

            if (variable.Equals("workspaceFolderBasename", StringComparison.OrdinalIgnoreCase))
                return GetDirectoryName(contextDirectory);

            if (variable.Equals("userHome", StringComparison.OrdinalIgnoreCase))
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            if (variable.Equals("pathSeparator", StringComparison.OrdinalIgnoreCase))
                return Path.PathSeparator.ToString();

            const string envPrefix = "env:";
            if (variable.StartsWith(envPrefix, StringComparison.OrdinalIgnoreCase))
            {
                var name = variable[envPrefix.Length..];
                return Environment.GetEnvironmentVariable(name) ?? "";
            }

            return match.Value;
        });
    }

    private static string GetDirectoryName(string directory)
    {
        var trimmed = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? directory : name;
    }
}
