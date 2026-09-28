using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace Lumi.Services.Sharing;

public enum PackReadError
{
    None,
    Empty,
    TooLarge,
    NotRecognized,
    MalformedFrontmatter,
    SkillWithoutName,
    SkillWithoutInstructions,
    InvalidJson,
    NoMcpServers,
    NewerVersion,
    EmptyPack
}

public sealed record PackReadResult(CapabilityPack? Pack, PackReadError Error, string? Detail = null)
{
    public bool Success => Pack is not null;

    public static PackReadResult Ok(CapabilityPack pack) => new(pack, PackReadError.None);
    public static PackReadResult Fail(PackReadError error, string? detail = null) => new(null, error, detail);
}

/// <summary>
/// Reads anything a person is likely to hand Lumi: a Lumi capability pack, any Agent Skills
/// <c>SKILL.md</c> (Lumi's or another tool's), or an MCP client config — <c>mcpServers</c> (Claude
/// Desktop, Cursor, Claude Code, Copilot CLI), <c>servers</c> (VS Code), a bare server map or a single
/// server object — with or without the code fence it was copied out of. Reading never touches the
/// data store; it only describes what a pack contains.
/// </summary>
public static class CapabilityPackReader
{
    /// <summary>Anything larger is not a capability someone wrote by hand; refuse it before parsing.</summary>
    public const int MaxTextLength = 2 * 1024 * 1024;

    private const int MaxCompanionFiles = 200;

    public static PackReadResult Read(string? text, string? sourcePath = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return PackReadResult.Fail(PackReadError.Empty);
        if (text.Length > MaxTextLength)
            return PackReadResult.Fail(PackReadError.TooLarge);

        // Input comes from files, drops and the clipboard: malformed text of any kind must surface
        // as a readable error, never as an exception on the UI thread.
        try
        {
            return ReadCore(text, sourcePath);
        }
        catch (JsonException ex)
        {
            return PackReadResult.Fail(PackReadError.InvalidJson, ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException or DecoderFallbackException)
        {
            return PackReadResult.Fail(PackReadError.NotRecognized, ex.Message);
        }
    }

    private static PackReadResult ReadCore(string text, string? sourcePath)
    {
        var normalized = PackText.NormalizeNewlines(text).Trim().TrimStart('\uFEFF').Trim();
        if (PackText.TryUnwrapSingleFence(normalized, out var inner))
            normalized = inner.Trim();

        if (normalized.StartsWith('{'))
            return ReadMcpConfig(normalized);

        if (!normalized.StartsWith("---", StringComparison.Ordinal))
            return PackReadResult.Fail(PackReadError.NotRecognized);

        if (!Frontmatter.TryParse(normalized, out var frontmatter, out var body, out var error))
            return PackReadResult.Fail(PackReadError.MalformedFrontmatter, error);

        return frontmatter.Has("lumi-pack")
            ? ReadLumiPack(frontmatter, body)
            : ReadSkill(frontmatter, body, sourcePath);
    }

    private static PackReadResult ReadSkill(Frontmatter frontmatter, string body, string? sourcePath)
    {
        var skill = ReadSkillDocument(frontmatter, body, out var error);
        if (skill is null)
            return PackReadResult.Fail(error);

        skill = skill with { CompanionFiles = FindCompanionFiles(sourcePath) };
        return PackReadResult.Ok(new CapabilityPack
        {
            Kind = SharedCapabilityKind.Skill,
            Format = CapabilityPackFormat.SkillMarkdown,
            Name = skill.Name,
            Description = skill.Description,
            IconGlyph = skill.IconGlyph,
            Skills = [skill]
        });
    }

    private static SharedSkill? ReadSkillDocument(Frontmatter frontmatter, string body, out PackReadError error)
    {
        error = PackReadError.None;
        var slug = PackText.SingleLine(frontmatter.GetString("name"));
        if (slug.Length == 0)
        {
            error = PackReadError.SkillWithoutName;
            return null;
        }

        var content = PackText.NormalizeNewlines(body).Trim();
        if (content.Length == 0)
        {
            error = PackReadError.SkillWithoutInstructions;
            return null;
        }

        var metadata = frontmatter.GetMap("metadata");
        var displayName = PackText.SingleLine(metadata?.GetValueOrDefault("lumi-name"));
        if (displayName.Length == 0)
            displayName = TitleFromHeading(content, slug) ?? PackText.HumanizeName(slug);
        var icon = PackText.SingleLine(metadata?.GetValueOrDefault("lumi-icon"));
        var license = PackText.SingleLine(frontmatter.GetString("license"));
        return new SharedSkill(
            slug,
            displayName,
            PackText.SingleLine(frontmatter.GetString("description")),
            content,
            icon.Length > 0 ? icon : "⚡")
        {
            License = license.Length > 0 ? license : null
        };
    }

    /// <summary>
    /// Foreign skills often open with a title that is the readable form of their slug
    /// (<c>name: pdf-processing</c> + <c># PDF Processing</c>); that title is the better display name.
    /// </summary>
    private static string? TitleFromHeading(string content, string slug)
    {
        var firstLine = content.Split('\n', 2)[0].Trim();
        if (!firstLine.StartsWith("# ", StringComparison.Ordinal))
            return null;

        var title = PackText.SingleLine(firstLine[2..].Trim().TrimEnd('#').Trim(), 120);
        return title.Length > 0 && CapabilityPackWriter.SkillSlug(title) == CapabilityPackWriter.SkillSlug(slug)
            ? title
            : null;
    }

    private static PackReadResult ReadLumiPack(Frontmatter frontmatter, string body)
    {
        var versionText = frontmatter.GetString("lumi-pack")?.Trim();
        if (!int.TryParse(versionText, out var version) || version < 1)
            return PackReadResult.Fail(PackReadError.MalformedFrontmatter, "lumi-pack must be a version number");
        if (version > CapabilityPackWriter.FormatVersion)
            return PackReadResult.Fail(PackReadError.NewerVersion);

        var notes = new List<PackNote>();
        var skills = new List<SharedSkill>();
        var servers = new List<SharedMcpServer>();
        SharedLumi? lumi = null;

        foreach (var block in PackText.FindFencedBlocks(body))
        {
            switch (block.Tag)
            {
                case CapabilityPackWriter.SkillBlockTag:
                    if (Frontmatter.TryParse(block.Content, out var skillFrontmatter, out var skillBody, out _)
                        && ReadSkillDocument(skillFrontmatter, skillBody, out _) is { } skill
                        && !skills.Any(existing => existing.Slug.Equals(skill.Slug, StringComparison.OrdinalIgnoreCase)))
                    {
                        skills.Add(skill);
                    }

                    break;

                case CapabilityPackWriter.McpBlockTag:
                    foreach (var server in ParseMcpServers(block.Content, notes, out _, out _))
                    {
                        if (!servers.Any(existing => existing.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
                            servers.Add(server);
                    }

                    break;

                case CapabilityPackWriter.LumiBlockTag:
                    lumi ??= ReadLumiDocument(block.Content);
                    break;
            }
        }

        if (lumi is null && skills.Count == 0 && servers.Count == 0)
            return PackReadResult.Fail(PackReadError.EmptyPack);

        if (lumi is not null)
        {
            var skillSlugs = skills.Select(static skill => skill.Slug).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var serverNames = servers.Select(static server => server.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var missing in lumi.SkillSlugs.Where(slug => !skillSlugs.Contains(slug))
                         .Concat(lumi.McpServerNames.Where(name => !serverNames.Contains(name))))
            {
                notes.Add(new PackNote(PackNoteKind.MissingReference, missing));
            }

            lumi = lumi with
            {
                SkillSlugs = lumi.SkillSlugs.Where(skillSlugs.Contains).ToList(),
                McpServerNames = lumi.McpServerNames.Where(serverNames.Contains).ToList()
            };
        }

        var kind = lumi is not null
            ? SharedCapabilityKind.Lumi
            : servers.Count > 0 && skills.Count == 0 ? SharedCapabilityKind.McpServer : SharedCapabilityKind.Skill;
        var primaryName = lumi?.Name ?? (kind == SharedCapabilityKind.McpServer ? servers[0].Name : skills[0].Name);
        var name = PackText.SingleLine(frontmatter.GetString("name"));
        var icon = PackText.SingleLine(frontmatter.GetString("icon"));

        return PackReadResult.Ok(new CapabilityPack
        {
            Kind = kind,
            Format = CapabilityPackFormat.LumiPack,
            Name = name.Length > 0 ? name : primaryName,
            Description = PackText.SingleLine(frontmatter.GetString("description")),
            IconGlyph = icon.Length > 0
                ? icon
                : lumi?.IconGlyph ?? (kind == SharedCapabilityKind.McpServer ? "🔌" : skills[0].IconGlyph),
            Skills = skills,
            Lumi = lumi,
            McpServers = servers,
            Notes = notes
        });
    }

    private static SharedLumi? ReadLumiDocument(string content)
    {
        if (!Frontmatter.TryParse(content, out var frontmatter, out var prompt, out _))
            return null;

        var name = PackText.SingleLine(frontmatter.GetString("name"));
        if (name.Length == 0)
            return null;

        // "tools: all" (or no tools key) means unrestricted; a list, even an empty one, restricts.
        var tools = frontmatter.GetList("tools");
        var icon = PackText.SingleLine(frontmatter.GetString("icon"));
        return new SharedLumi(
            name,
            PackText.SingleLine(frontmatter.GetString("description")),
            PackText.NormalizeNewlines(prompt).Trim(),
            icon.Length > 0 ? icon : "✦",
            tools?.Select(static tool => tool.Trim()).Where(static tool => tool.Length > 0).Distinct(StringComparer.Ordinal).ToList(),
            (frontmatter.GetList("skills") ?? []).Select(static slug => slug.Trim()).Where(static slug => slug.Length > 0).ToList(),
            (frontmatter.GetList("mcp-servers") ?? []).Select(static server => server.Trim()).Where(static server => server.Length > 0).ToList());
    }

    private static PackReadResult ReadMcpConfig(string json)
    {
        var notes = new List<PackNote>();
        var servers = ParseMcpServers(json, notes, out var error, out var detail);
        if (error != PackReadError.None)
            return PackReadResult.Fail(error, detail);
        if (servers.Count == 0)
            return PackReadResult.Fail(PackReadError.NoMcpServers);

        return PackReadResult.Ok(new CapabilityPack
        {
            Kind = SharedCapabilityKind.McpServer,
            Format = CapabilityPackFormat.McpConfig,
            Name = servers.Count == 1
                ? servers[0].Name
                : string.Join(", ", servers.Take(3).Select(static server => server.Name))
                  + (servers.Count > 3 ? " +" + (servers.Count - 3).ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
            Description = servers.Count == 1 ? servers[0].Description : "",
            IconGlyph = "🔌",
            McpServers = servers,
            Notes = notes
        });
    }

    internal static List<SharedMcpServer> ParseMcpServers(
        string json,
        List<PackNote> notes,
        out PackReadError error,
        out string? errorDetail)
    {
        error = PackReadError.None;
        errorDetail = null;
        var servers = new List<SharedMcpServer>();
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, PackText.LenientJson);
        }
        catch (JsonException ex)
        {
            error = PackReadError.InvalidJson;
            errorDetail = ex.Message;
            return servers;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                error = PackReadError.NoMcpServers;
                return servers;
            }

            if (TryGetObject(root, "mcpServers", out var map)
                || TryGetObject(root, "servers", out map)
                || (TryGetObject(root, "mcp", out var mcp) && TryGetObject(mcp, "servers", out map)))
            {
                AddServers(map, servers, notes);
            }
            else if (LooksLikeServer(root))
            {
                var name = GetString(root, "name") ?? DeriveServerName(root);
                if (ReadServer(name, root, notes) is { } server)
                    servers.Add(server);
            }
            else if (root.EnumerateObject().Any()
                     && root.EnumerateObject().All(static property => property.Value.ValueKind == JsonValueKind.Object && LooksLikeServer(property.Value)))
            {
                AddServers(root, servers, notes);
            }
            else
            {
                error = PackReadError.NoMcpServers;
            }
        }

        return servers;
    }

    private static void AddServers(JsonElement map, List<SharedMcpServer> servers, List<PackNote> notes)
    {
        foreach (var property in map.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
                continue;
            if (ReadServer(property.Name, property.Value, notes) is { } server
                && !servers.Any(existing => existing.Name.Equals(server.Name, StringComparison.OrdinalIgnoreCase)))
            {
                servers.Add(server);
            }
        }
    }

    private static SharedMcpServer? ReadServer(string name, JsonElement element, List<PackNote> notes)
    {
        var displayName = PackText.SingleLine(name, 120);
        if (displayName.Length == 0)
            displayName = DeriveServerName(element);

        var type = (GetString(element, "type") ?? GetString(element, "transport") ?? "").Trim().ToLowerInvariant();
        var command = GetString(element, "command")?.Trim() ?? "";
        var url = (GetString(element, "url") ?? GetString(element, "serverUrl") ?? "").Trim();
        var isRemote = type switch
        {
            "http" or "sse" or "remote" or "streamable-http" or "streamablehttp" or "streamable_http" or "ws" or "websocket" => true,
            "stdio" or "local" => false,
            _ => command.Length == 0 && url.Length > 0
        };

        if ((isRemote && url.Length == 0) || (!isRemote && command.Length == 0))
        {
            notes.Add(new PackNote(PackNoteKind.SkippedServer, displayName));
            return null;
        }

        var tools = GetStringArray(element, "tools");
        if (tools.Count == 1 && tools[0] == "*")
            tools = [];

        int? timeout = element.TryGetProperty("timeout", out var timeoutElement)
                       && timeoutElement.ValueKind == JsonValueKind.Number
                       && timeoutElement.TryGetInt32(out var parsedTimeout)
                       && parsedTimeout > 0
            ? parsedTimeout
            : null;

        return new SharedMcpServer(
            displayName,
            PackText.SingleLine(GetString(element, "description")),
            isRemote,
            isRemote ? "" : command,
            isRemote ? [] : GetStringArray(element, "args"),
            isRemote ? url : "",
            isRemote ? [] : GetObjectKeys(element, "env"),
            isRemote ? GetObjectKeys(element, "headers") : [],
            tools,
            timeout);
    }

    private static bool LooksLikeServer(JsonElement element)
        => element.ValueKind == JsonValueKind.Object
           && (element.TryGetProperty("command", out _) || element.TryGetProperty("url", out _) || element.TryGetProperty("serverUrl", out _));

    private static string DeriveServerName(JsonElement element)
    {
        foreach (var arg in GetStringArray(element, "args"))
        {
            if (arg.StartsWith('-'))
                continue;

            var name = arg;
            var at = name.LastIndexOf('@');
            if (at > 0)
                name = name[..at];
            name = name[(name.LastIndexOf('/') + 1)..];
            foreach (var noise in new[] { "server-", "-server", "mcp-", "-mcp" })
                name = name.Replace(noise, "", StringComparison.OrdinalIgnoreCase);
            if (name.Trim('-').Length > 0)
                return PackText.HumanizeName(name.Trim('-'));
        }

        var url = GetString(element, "url") ?? GetString(element, "serverUrl");
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Host.Length > 0)
            return uri.Host;

        return GetString(element, "command") is { Length: > 0 } command ? PackText.HumanizeName(Path.GetFileNameWithoutExtension(command)) : "MCP server";
    }

    private static bool TryGetObject(JsonElement element, string name, out JsonElement value)
        => element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.Object;

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> GetStringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            return [];

        return value.EnumerateArray()
            .Select(static item => item.ValueKind switch
            {
                JsonValueKind.String => item.GetString() ?? "",
                JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => item.GetRawText(),
                _ => null
            })
            .OfType<string>()
            .ToList();
    }

    private static List<string> GetObjectKeys(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object)
            return [];

        return value.EnumerateObject()
            .Select(static property => property.Name.Trim())
            .Where(static key => key.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Agent Skills are folders; a Lumi skill is one document. When the source is a <c>SKILL.md</c>
    /// inside a skill folder, name what else lives there so the receipt can say it was left behind.
    /// </summary>
    private static IReadOnlyList<string> FindCompanionFiles(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)
            || !string.Equals(Path.GetFileName(sourcePath), CapabilityPackWriter.SkillFileName, StringComparison.OrdinalIgnoreCase))
            return [];

        try
        {
            var directory = Path.GetDirectoryName(Path.GetFullPath(sourcePath));
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
                return [];

            return Directory.EnumerateFileSystemEntries(directory)
                .Where(entry => !string.Equals(Path.GetFileName(entry), CapabilityPackWriter.SkillFileName, StringComparison.OrdinalIgnoreCase)
                                && !Path.GetFileName(entry).StartsWith('.'))
                .Take(MaxCompanionFiles)
                .OrderBy(static entry => entry, StringComparer.OrdinalIgnoreCase)
                .Select(static entry => Directory.Exists(entry) ? Path.GetFileName(entry) + "/" : Path.GetFileName(entry))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }
}
