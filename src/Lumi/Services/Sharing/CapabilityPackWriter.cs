using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Lumi.Models;

namespace Lumi.Services.Sharing;

/// <summary>
/// Turns Lumi capabilities into portable, human-readable text.
///
/// <para><b>Skills</b> become a standard Agent Skills <c>SKILL.md</c> (agentskills.io): a slug
/// <c>name</c>, a <c>description</c>, and Lumi's display name and icon tucked into the spec's
/// <c>metadata</c> map, so the same file drops straight into Claude Code, Codex, Cursor or Gemini
/// CLI and still round-trips into Lumi exactly.</para>
///
/// <para><b>Lumis and MCP servers</b> become a <em>capability pack</em> (<c>*.lumi.md</c>): a markdown
/// document that is its own receipt. Front matter identifies it (<c>lumi-pack: 1</c>); the prose reads
/// as a summary anyone can review in a text editor or on GitHub; and each part lives in a tagged
/// fenced block that Lumi parses: <c>```markdown lumi:agent</c> (front matter + system prompt),
/// <c>```markdown lumi:skill</c> (a complete SKILL.md) and <c>```json lumi:mcp</c> (a standard
/// <c>mcpServers</c> config, pasteable into Claude Desktop, Cursor or VS Code). Fences are sized to
/// whatever the content contains, so no instruction text can break out of its block.</para>
///
/// <para>Secrets never leave: environment variable and header values are dropped structurally (the pack
/// model has nowhere to hold them) and credentials inlined into arguments or URLs are replaced with
/// <see cref="SecretRedactor.Placeholder"/>. Everything removed or worth a second look is reported as a
/// <see cref="ShareFinding"/> so the share sheet can say so before anything is copied.</para>
/// </summary>
public static class CapabilityPackWriter
{
    public const int FormatVersion = 1;
    public const string PackExtension = ".lumi.md";
    public const string SkillFileName = "SKILL.md";

    internal const string LumiBlockTag = "lumi:agent";
    internal const string SkillBlockTag = "lumi:skill";
    internal const string McpBlockTag = "lumi:mcp";

    private const int MaxSpecDescriptionLength = 1024;
    private static readonly string[] NodeShims = ["npx", "npm", "pnpm", "pnpx", "yarn"];

    public static CapabilityShare ForSkill(Skill skill)
    {
        ArgumentNullException.ThrowIfNull(skill);

        var findings = new List<ShareFinding>();
        var shared = ToShared(skill, SkillSlug(skill.Name), findings);
        var pack = new CapabilityPack
        {
            Kind = SharedCapabilityKind.Skill,
            Format = CapabilityPackFormat.SkillMarkdown,
            Name = shared.Name,
            Description = shared.Description,
            IconGlyph = shared.IconGlyph,
            Skills = [shared]
        };

        return new CapabilityShare(pack, WriteSkillMarkdown(shared), shared.Slug + "/" + SkillFileName, findings);
    }

    /// <summary>
    /// Packs a Lumi with every skill and MCP server it references, so the recipient gets a Lumi that
    /// works rather than one with dangling references. References that no longer resolve are dropped.
    /// </summary>
    public static CapabilityShare ForLumi(LumiAgent agent, AppData data)
    {
        ArgumentNullException.ThrowIfNull(agent);
        ArgumentNullException.ThrowIfNull(data);

        var findings = new List<ShareFinding>();
        var usedSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var skills = new List<SharedSkill>();
        foreach (var id in agent.SkillIds.Distinct())
        {
            if (data.Skills.FirstOrDefault(skill => skill.Id == id) is not { } skill)
                continue;

            var slug = SkillSlug(skill.Name);
            var unique = slug;
            for (var suffix = 2; !usedSlugs.Add(unique); suffix++)
            {
                // Keep the suffixed name within the spec's 64 characters so it cannot collide again.
                var suffixText = "-" + suffix.ToString(CultureInfo.InvariantCulture);
                var stem = slug.Length + suffixText.Length > 64 ? slug[..(64 - suffixText.Length)].TrimEnd('-') : slug;
                unique = stem + suffixText;
            }

            skills.Add(ToShared(skill, unique, findings));
        }

        var servers = new List<SharedMcpServer>();
        var usedServerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in agent.McpServerIds.Distinct())
        {
            if (data.McpServers.FirstOrDefault(server => server.Id == id) is not { } server
                || !usedServerNames.Add(server.Name.Trim()))
                continue;
            servers.Add(ToShared(server, findings));
        }

        var name = agent.Name.Trim();
        AddTextFindings(name, agent.SystemPrompt, findings);
        var lumi = new SharedLumi(
            name,
            agent.Description.Trim(),
            agent.SystemPrompt.Trim(),
            Glyph(agent.IconGlyph, "✦"),
            agent.HasToolRestrictions
                ? agent.ToolNames.Where(static tool => !string.IsNullOrWhiteSpace(tool)).Select(static tool => tool.Trim()).Distinct(StringComparer.Ordinal).ToList()
                : null,
            skills.Select(static skill => skill.Slug).ToList(),
            servers.Select(static server => server.Name).ToList());

        var pack = new CapabilityPack
        {
            Kind = SharedCapabilityKind.Lumi,
            Format = CapabilityPackFormat.LumiPack,
            Name = lumi.Name,
            Description = lumi.Description,
            IconGlyph = lumi.IconGlyph,
            Skills = skills,
            Lumi = lumi,
            McpServers = servers
        };

        return new CapabilityShare(pack, WritePack(pack), PackFileName(pack.Name, "lumi"), findings);
    }

    public static CapabilityShare ForMcpServer(McpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);

        var findings = new List<ShareFinding>();
        var shared = ToShared(server, findings);
        var pack = new CapabilityPack
        {
            Kind = SharedCapabilityKind.McpServer,
            Format = CapabilityPackFormat.LumiPack,
            Name = shared.Name,
            Description = shared.Description,
            IconGlyph = "🔌",
            McpServers = [shared]
        };

        return new CapabilityShare(pack, WritePack(pack), PackFileName(pack.Name, "mcp-server"), findings);
    }

    /// <summary>A standard Agent Skills <c>SKILL.md</c>. Lumi's display name and icon ride in <c>metadata</c>.</summary>
    public static string WriteSkillMarkdown(SharedSkill skill)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(SkillSlug(skill.Slug)).Append('\n');
        builder.Append("description: ").Append(PackText.Quote(SpecDescription(skill))).Append('\n');
        builder.Append("metadata:\n");
        builder.Append("  lumi-name: ").Append(PackText.Quote(skill.Name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(skill.IconGlyph))
            builder.Append("  lumi-icon: ").Append(PackText.Quote(skill.IconGlyph)).Append('\n');
        builder.Append("---\n\n");
        builder.Append(PackText.NormalizeNewlines(skill.Content).Trim()).Append('\n');
        return builder.ToString();
    }

    /// <summary>
    /// The server list as a standard <c>mcpServers</c> config. Secret-bearing entries are written with
    /// empty values, so the JSON pastes into another MCP client and shows exactly what to fill in.
    /// </summary>
    public static string WriteMcpConfig(IEnumerable<SharedMcpServer> servers)
    {
        var builder = new StringBuilder();
        builder.Append("{\n  \"mcpServers\": {");
        var first = true;
        foreach (var server in servers)
        {
            builder.Append(first ? "\n" : ",\n");
            first = false;

            var fields = new List<string>();
            if (server.IsRemote)
            {
                fields.Add("\"type\": \"http\"");
                fields.Add("\"url\": " + PackText.Quote(server.Url));
                if (server.HeaderKeys.Count > 0)
                    fields.Add("\"headers\": " + EmptyValueObject(server.HeaderKeys, "      "));
            }
            else
            {
                fields.Add("\"type\": \"stdio\"");
                fields.Add("\"command\": " + PackText.Quote(server.Command));
                if (server.Args.Count > 0)
                    fields.Add("\"args\": " + PackText.QuoteList(server.Args));
                if (server.EnvKeys.Count > 0)
                    fields.Add("\"env\": " + EmptyValueObject(server.EnvKeys, "      "));
            }

            if (!string.IsNullOrWhiteSpace(server.Description))
                fields.Add("\"description\": " + PackText.Quote(server.Description));
            if (server.Tools.Count > 0)
                fields.Add("\"tools\": " + PackText.QuoteList(server.Tools));
            if (server.Timeout is { } timeout)
                fields.Add("\"timeout\": " + timeout.ToString(CultureInfo.InvariantCulture));

            builder.Append("    ").Append(PackText.Quote(server.Name)).Append(": {\n");
            builder.Append(string.Join(",\n", fields.Select(static field => "      " + field)));
            builder.Append("\n    }");
        }

        builder.Append(first ? "}\n}\n" : "\n  }\n}\n");
        return builder.ToString();
    }

    private static string EmptyValueObject(IReadOnlyList<string> keys, string indent)
        => "{\n" + string.Join(",\n", keys.Select(key => indent + "  " + PackText.Quote(key) + ": \"\"")) + "\n" + indent + "}";

    /// <summary>
    /// The smallest text that still reads back to the same pack: a skill's SKILL.md, or a Lumi pack
    /// without the human-readable prose (front matter and tagged blocks only). This is what a chat
    /// code carries; the readers never needed the prose.
    /// </summary>
    public static string WriteCompact(CapabilityShare share)
        => share.Pack.Format == CapabilityPackFormat.SkillMarkdown ? share.Text : WritePack(share.Pack, compact: true);

    private static string WritePack(CapabilityPack pack, bool compact = false)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("lumi-pack: ").Append(FormatVersion.ToString(CultureInfo.InvariantCulture)).Append('\n');
        builder.Append("kind: ").Append(pack.Kind == SharedCapabilityKind.Lumi ? "lumi" : "mcp-server").Append('\n');
        builder.Append("name: ").Append(PackText.Quote(pack.Name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(pack.Description))
            builder.Append("description: ").Append(PackText.Quote(pack.Description)).Append('\n');
        if (!string.IsNullOrWhiteSpace(pack.IconGlyph))
            builder.Append("icon: ").Append(PackText.Quote(pack.IconGlyph)).Append('\n');
        builder.Append("---\n\n");

        if (compact)
            return AppendBlocks(builder, pack, prose: false);

        builder.Append("# ").Append(pack.IconGlyph).Append(' ').Append(PackText.SingleLine(pack.Name)).Append("\n\n");
        if (!string.IsNullOrWhiteSpace(pack.Description))
            builder.Append(PackText.SingleLine(pack.Description)).Append("\n\n");

        builder.Append("> **Lumi capability pack** · ").Append(DescribeContents(pack)).Append('\n');
        builder.Append(">\n");
        builder.Append("> To add it, open Lumi and choose **Import**, or drop this file onto Lumi. You will see everything inside before anything is added, and nothing runs until you turn it on.\n");
        var keys = pack.McpServers.SelectMany(static server => server.RequiredKeys).Distinct(StringComparer.Ordinal).ToList();
        if (keys.Count > 0)
        {
            builder.Append(">\n");
            builder.Append("> 🔒 Secrets were removed before sharing. You will add your own: ")
                .Append(string.Join(", ", keys.Select(PackText.InlineCode))).Append('\n');
        }

        builder.Append('\n');
        return AppendBlocks(builder, pack, prose: true);
    }

    private static string AppendBlocks(StringBuilder builder, CapabilityPack pack, bool prose)
    {
        if (pack.Lumi is { } lumi)
        {
            if (prose)
                builder.Append("## ").Append(lumi.IconGlyph).Append(' ').Append(PackText.SingleLine(lumi.Name)).Append(" · Lumi\n\n");
            PackText.AppendFence(builder, "markdown " + LumiBlockTag, WriteLumiDocument(lumi));
        }

        foreach (var skill in pack.Skills)
        {
            if (prose)
                builder.Append("## ").Append(skill.IconGlyph).Append(' ').Append(PackText.SingleLine(skill.Name)).Append(" · Skill\n\n");
            PackText.AppendFence(builder, "markdown " + SkillBlockTag, WriteSkillMarkdown(skill));
        }

        foreach (var server in pack.McpServers)
        {
            if (!prose)
            {
                PackText.AppendFence(builder, "json " + McpBlockTag, WriteMcpConfig([server]));
                continue;
            }

            builder.Append("## 🔌 ").Append(PackText.SingleLine(server.Name)).Append(" · MCP server\n\n");
            builder.Append(server.IsRemote ? "Connects to: " : "Runs on your computer: ")
                .Append(PackText.InlineCode(server.CommandLine)).Append("\n\n");
            if (server.RequiredKeys.Count > 0)
            {
                builder.Append("Needs: ").Append(string.Join(", ", server.RequiredKeys.Select(PackText.InlineCode)))
                    .Append(" (not included; add your own)\n\n");
            }

            PackText.AppendFence(builder, "json " + McpBlockTag, WriteMcpConfig([server]));
        }

        return builder.ToString().TrimEnd('\n') + "\n";
    }

    private static string WriteLumiDocument(SharedLumi lumi)
    {
        var builder = new StringBuilder();
        builder.Append("---\n");
        builder.Append("name: ").Append(PackText.Quote(lumi.Name)).Append('\n');
        if (!string.IsNullOrWhiteSpace(lumi.Description))
            builder.Append("description: ").Append(PackText.Quote(lumi.Description)).Append('\n');
        builder.Append("icon: ").Append(PackText.Quote(lumi.IconGlyph)).Append('\n');
        builder.Append("tools: ").Append(lumi.ToolNames is null ? "all" : PackText.QuoteList(lumi.ToolNames)).Append('\n');
        builder.Append("skills: ").Append(PackText.QuoteList(lumi.SkillSlugs)).Append('\n');
        builder.Append("mcp-servers: ").Append(PackText.QuoteList(lumi.McpServerNames)).Append('\n');
        builder.Append("---\n\n");
        builder.Append(PackText.NormalizeNewlines(lumi.SystemPrompt).Trim()).Append('\n');
        return builder.ToString();
    }

    private static string DescribeContents(CapabilityPack pack)
    {
        var parts = new List<string>();
        if (pack.Lumi is not null)
            parts.Add("1 Lumi");
        if (pack.Skills.Count > 0)
            parts.Add(pack.Skills.Count == 1 ? "1 skill" : pack.Skills.Count.ToString(CultureInfo.InvariantCulture) + " skills");
        if (pack.McpServers.Count > 0)
            parts.Add(pack.McpServers.Count == 1 ? "1 MCP server" : pack.McpServers.Count.ToString(CultureInfo.InvariantCulture) + " MCP servers");
        return string.Join(" · ", parts);
    }

    private static SharedSkill ToShared(Skill skill, string slug, List<ShareFinding> findings)
    {
        var name = PackText.SingleLine(skill.Name);
        AddTextFindings(name, skill.Content, findings);
        AddTextFindings(name, skill.Description, findings);
        return new SharedSkill(
            slug,
            name,
            PackText.SingleLine(skill.Description),
            PackText.NormalizeNewlines(skill.Content).Trim(),
            Glyph(skill.IconGlyph, "⚡"));
    }

    private static SharedMcpServer ToShared(McpServer server, List<ShareFinding> findings)
    {
        var name = PackText.SingleLine(server.Name);
        var isRemote = string.Equals(server.ServerType, "remote", StringComparison.OrdinalIgnoreCase);
        var envKeys = new List<string>();
        var headerKeys = new List<string>();
        var args = new List<string>();
        var command = "";
        var url = "";

        if (isRemote)
        {
            url = SecretRedactor.RedactUrl(server.Url?.Trim(), out var urlChanged);
            if (urlChanged)
                findings.Add(new ShareFinding(ShareFindingKind.CredentialRemovedFromUrl, name, url));

            headerKeys = CollectKeys(server.Headers, name, findings);
        }
        else
        {
            command = PortableCommand(server.Command?.Trim() ?? "");
            args = SecretRedactor.RedactArguments(server.Args.Select(static arg => arg ?? "").ToList(), out var redacted);
            foreach (var label in redacted)
                findings.Add(new ShareFinding(ShareFindingKind.CredentialRemovedFromArgument, name, label));

            envKeys = CollectKeys(server.Env, name, findings);

            foreach (var value in args.Prepend(command).Where(SecretRedactor.LooksLikeLocalPath).Distinct(StringComparer.Ordinal))
                findings.Add(new ShareFinding(ShareFindingKind.LocalPath, name, value));
        }

        return new SharedMcpServer(
            name,
            PackText.SingleLine(server.Description),
            isRemote,
            command,
            args,
            url,
            envKeys,
            headerKeys,
            server.Tools.Where(static tool => !string.IsNullOrWhiteSpace(tool)).Select(static tool => tool.Trim()).ToList(),
            server.Timeout);
    }

    private static List<string> CollectKeys(Dictionary<string, string> values, string serverName, List<ShareFinding> findings)
    {
        var keys = new List<string>();
        foreach (var (key, value) in values)
        {
            var trimmed = key?.Trim();
            if (string.IsNullOrEmpty(trimmed) || keys.Contains(trimmed, StringComparer.OrdinalIgnoreCase))
                continue;

            keys.Add(trimmed);
            if (!string.IsNullOrEmpty(value))
                findings.Add(new ShareFinding(ShareFindingKind.SecretValueRemoved, serverName, trimmed));
        }

        return keys;
    }

    private static void AddTextFindings(string itemName, string? text, List<ShareFinding> findings)
    {
        foreach (var sample in SecretRedactor.FindTokensInText(text).Distinct(StringComparer.Ordinal))
            findings.Add(new ShareFinding(ShareFindingKind.TextLooksLikeSecret, itemName, sample));
    }

    /// <summary>
    /// An Agent Skills-compliant name: lowercase ASCII letters and digits joined by single hyphens,
    /// at most 64 characters. Accents are folded (é → e). A name with no Latin letters or digits (for
    /// example Hebrew) becomes <c>skill-</c> plus a short hash of the name, so two such skills never
    /// share a folder name.
    /// </summary>
    public static string SkillSlug(string? name)
    {
        var builder = new StringBuilder();
        var pendingHyphen = false;
        foreach (var ch in (name ?? "").Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (pendingHyphen && builder.Length > 0)
                    builder.Append('-');
                pendingHyphen = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingHyphen = true;
            }
        }

        var slug = builder.ToString();
        if (slug.Length > 64)
            slug = slug[..64].TrimEnd('-');
        if (slug.Length > 0)
            return slug;

        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
            return "skill";

        var hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.Normalize(NormalizationForm.FormC)));
        return "skill-" + Convert.ToHexString(hash, 0, 3).ToLowerInvariant();
    }

    private static string PackFileName(string name, string fallback)
    {
        var slug = SkillSlug(name);
        if (slug == "skill")
            slug = fallback;
        else if (slug.StartsWith("skill-", StringComparison.Ordinal) && !name.Any(char.IsAsciiLetterOrDigit))
            slug = fallback + slug["skill".Length..];
        return slug + PackExtension;
    }

    /// <summary>The spec requires a non-empty description; fall back to the first line of the instructions.</summary>
    private static string SpecDescription(SharedSkill skill)
    {
        var description = PackText.SingleLine(skill.Description);
        if (description.Length == 0)
        {
            description = PackText.NormalizeNewlines(skill.Content)
                .Split('\n')
                .Select(static line => line.Trim().TrimStart('#', '>', '-', '*', ' ').Trim())
                .FirstOrDefault(static line => line.Length > 0 && !line.StartsWith("```", StringComparison.Ordinal))
                ?? "";
            description = PackText.SingleLine(description, 200);
        }

        if (description.Length == 0)
            description = skill.Name;
        return description.Length > MaxSpecDescriptionLength ? description[..MaxSpecDescriptionLength].TrimEnd() : description;
    }

    private static string Glyph(string? glyph, string fallback)
    {
        var trimmed = glyph?.Trim();
        return string.IsNullOrEmpty(trimmed) || trimmed.Length > 16 ? fallback : trimmed;
    }

    /// <summary>
    /// Windows needs <c>npx.cmd</c> where every other OS runs <c>npx</c>. Packs always carry the
    /// portable name; <see cref="PlatformCommand"/> puts the suffix back when importing on Windows.
    /// </summary>
    public static string PortableCommand(string command)
    {
        foreach (var shim in NodeShims)
        {
            if (command.Equals(shim + ".cmd", StringComparison.OrdinalIgnoreCase))
                return shim;
        }

        return command;
    }

    public static string PlatformCommand(string command, bool isWindows)
    {
        var portable = PortableCommand(command);
        return isWindows && NodeShims.Contains(portable, StringComparer.OrdinalIgnoreCase)
            ? portable + ".cmd"
            : portable;
    }
}
