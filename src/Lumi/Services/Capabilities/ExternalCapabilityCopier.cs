using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Lumi.Models;
using Lumi.Services.Sharing;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Turns a read-only skill, agent or MCP server — one a plugin installed or that lives in the
/// user's Copilot folder — into a Lumi item the user owns and can edit.
/// </summary>
public static class ExternalCapabilityCopier
{
    /// <summary>A new Lumi skill with the skill's instructions and every file it ships with.</summary>
    public static Skill? CopySkill(DataStore dataStore, CapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        if (capability.Kind != CapabilityKind.Skill || ReadFile(capability.SourcePath) is not { } text)
            return null;

        var name = capability.Label;
        var description = capability.Description ?? "";
        var body = text;
        if (Frontmatter.TryParse(text, out var frontmatter, out var parsedBody, out _))
        {
            name = frontmatter.GetString("name") ?? name;
            description = frontmatter.GetString("description") ?? description;
            body = parsedBody;
        }

        var skill = new Skill
        {
            Name = UniqueName(name, dataStore.Data.Skills.Select(static existing => existing.Name)),
            Description = description.Trim(),
            Content = body.Trim(),
            IconGlyph = "⚡",
        };

        var bundled = CopySupportingFiles(capability.SourcePath!, dataStore.GetSkillBundleDirectory(skill.Id));
        if (bundled > 0)
        {
            skill.Content += $"""


                ---
                Supporting files for this skill (scripts, references, templates) are in `{dataStore.GetSkillBundleDirectory(skill.Id)}`. Resolve relative paths in these instructions against that folder.
                """;
        }

        dataStore.Data.Skills.Add(skill);
        return skill;
    }

    /// <summary>A new Lumi with the agent's persona. Copilot tool names do not map to Lumi's, so tools start unrestricted.</summary>
    public static LumiAgent? CopyAgent(DataStore dataStore, CapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        if (capability.Kind != CapabilityKind.Agent)
            return null;

        var prompt = capability.Content;
        if (string.IsNullOrWhiteSpace(prompt) && CapabilityContent.TryReadBody(capability, out var body))
            prompt = body;
        if (string.IsNullOrWhiteSpace(prompt))
            return null;

        var agent = new LumiAgent
        {
            Name = UniqueName(capability.Label, dataStore.Data.Agents.Select(static existing => existing.Name)),
            Description = capability.Description?.Trim() ?? "",
            SystemPrompt = prompt.Trim(),
            IconGlyph = "✦",
        };

        dataStore.Data.Agents.Add(agent);
        return agent;
    }

    /// <summary>
    /// A new Lumi MCP server with the same definition. It keeps the original's name, so in chats it
    /// takes the place of the one the plugin or Copilot folder provides. Null when the server cannot
    /// be read, or has its own sign-in that a copy could not keep.
    /// </summary>
    public static McpServer? CopyMcpServer(DataStore dataStore, CapabilityDescriptor capability)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        if (ReadMcpServer(capability, out var signsIn) is not { } server || signsIn)
            return null;

        server.Name = dataStore.Data.McpServers.Any(existing => existing.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase))
            ? UniqueName(server.Name, dataStore.Data.McpServers.Select(static existing => existing.Name))
            : server.Name;
        dataStore.Data.McpServers.Add(server);
        return server;
    }

    /// <inheritdoc cref="ReadMcpServer(CapabilityDescriptor, out bool)"/>
    public static McpServer? ReadMcpServer(CapabilityDescriptor capability) => ReadMcpServer(capability, out _);

    /// <summary>
    /// Reads a discovered MCP server's definition from the configuration file the runtime named,
    /// at the JSON pointer it named. Null when the server has no readable declaration.
    /// </summary>
    /// <param name="signsIn">
    /// The declaration has its own sign-in (an OAuth client or auth settings). A Lumi server cannot
    /// hold those, so a copy of it could not sign in.
    /// </param>
    public static McpServer? ReadMcpServer(CapabilityDescriptor capability, out bool signsIn)
    {
        signsIn = false;
        if (capability.Kind != CapabilityKind.McpServer)
            return null;

        if (capability.McpDefinition is { } definition)
            return Clone(definition);

        if (ReadFile(capability.ConfigFilePath) is not { } text)
            return null;

        try
        {
            using var document = JsonDocument.Parse(text, PackText.LenientJson);
            var element = Resolve(document.RootElement, capability.ConfigPointer, capability.Name);
            if (element is not { ValueKind: JsonValueKind.Object } entry)
                return null;

            var root = PluginRoot(capability.ConfigFilePath!) ?? Path.GetDirectoryName(capability.ConfigFilePath!) ?? "";
            var server = WorkspaceMcpCapabilityProvider.ToServer(capability.Name, entry, root);
            if (server is null)
                return null;

            signsIn = entry.EnumerateObject().Any(static property =>
                property.Name.StartsWith("oauth", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("auth", StringComparison.OrdinalIgnoreCase));
            if (entry.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
            {
                server.Tools = [.. tools.EnumerateArray()
                    .Where(static tool => tool.ValueKind == JsonValueKind.String)
                    .Select(static tool => tool.GetString()!)];
            }

            server.Description = capability.Description ?? "";
            if (string.Equals(server.Command, "npx", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows())
                server.Command = "npx.cmd";
            return server;
        }
        catch (JsonException ex)
        {
            Debug.WriteLine($"[Capabilities] Could not read MCP server '{capability.Name}': {ex.Message}");
            return null;
        }
    }

    /// <summary>Lumi's naming for a copy: the original name when free, else "Name (copy)".</summary>
    internal static string UniqueName(string name, IEnumerable<string> taken)
    {
        var existing = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var trimmed = string.IsNullOrWhiteSpace(name) ? "Copy" : name.Trim();
        if (!existing.Contains(trimmed))
            return trimmed;

        var candidate = $"{trimmed} (copy)";
        for (var i = 2; existing.Contains(candidate); i++)
            candidate = $"{trimmed} (copy {i})";
        return candidate;
    }

    /// <summary>Copies everything next to <c>SKILL.md</c>. Returns how many files were copied.</summary>
    private static int CopySupportingFiles(string skillFile, string destination)
    {
        var folder = Path.GetDirectoryName(skillFile);
        if (folder is null || !Directory.Exists(folder))
            return 0;

        // A skill file that sits directly in a skills root (rather than in its own folder) shares
        // that folder with other skills, so there is nothing that belongs to it alone.
        if (!Path.GetFileName(skillFile).Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
            return 0;

        var copied = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                if (Path.GetFullPath(file).Equals(Path.GetFullPath(skillFile), StringComparison.OrdinalIgnoreCase))
                    continue;

                var target = Path.Combine(destination, Path.GetRelativePath(folder, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
                copied++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"[Capabilities] Copying skill files failed: {ex.Message}");
        }

        return copied;
    }

    private static JsonElement? Resolve(JsonElement root, string? pointer, string name)
    {
        if (!string.IsNullOrWhiteSpace(pointer))
        {
            var current = root;
            foreach (var raw in pointer.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var segment = raw.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
                    return null;
            }

            return current;
        }

        foreach (var container in new[] { "mcpServers", "servers" })
        {
            if (root.TryGetProperty(container, out var servers)
                && servers.ValueKind == JsonValueKind.Object
                && servers.TryGetProperty(name, out var server))
            {
                return server;
            }
        }

        return null;
    }

    /// <summary>The installed plugin folder that holds a file, so <c>${PLUGIN_ROOT}</c> expands correctly.</summary>
    internal static string? PluginRoot(string path)
    {
        var full = Path.GetFullPath(path);
        var segments = full.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var index = Array.FindIndex(segments, static segment => segment.Equals("installed-plugins", StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 2 >= segments.Length)
            return null;

        var root = string.Join(Path.DirectorySeparatorChar, segments[..(index + 3)]);
        return OperatingSystem.IsWindows() || root.StartsWith(Path.DirectorySeparatorChar) ? root : Path.DirectorySeparatorChar + root;
    }

    private static string? ReadFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            return null;

        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static McpServer Clone(McpServer server) => new()
    {
        Name = server.Name,
        Description = server.Description,
        ServerType = server.ServerType,
        Command = server.Command,
        Args = [.. server.Args],
        Env = new Dictionary<string, string>(server.Env),
        Url = server.Url,
        Headers = new Dictionary<string, string>(server.Headers),
        Tools = [.. server.Tools],
        Timeout = server.Timeout,
        IsEnabled = true,
    };
}
