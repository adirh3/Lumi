namespace Lumi.Services.Sharing;

/// <summary>What a share is about. Drives the card, the suggested file name and where an import lands.</summary>
public enum SharedCapabilityKind
{
    Skill,
    Lumi,
    McpServer
}

/// <summary>The text format a pack was read from, or is written as.</summary>
public enum CapabilityPackFormat
{
    /// <summary>A plain Agent Skills <c>SKILL.md</c>: Lumi's, Claude Code's, Codex's, anyone's.</summary>
    SkillMarkdown,

    /// <summary>A Lumi capability pack (<c>*.lumi.md</c>): one Lumi or MCP server plus everything it needs.</summary>
    LumiPack,

    /// <summary>An MCP client config (<c>mcpServers</c> / <c>servers</c> JSON) from another app or a README.</summary>
    McpConfig
}

/// <summary>
/// A skill as it travels. <see cref="Slug"/> is its portable Agent Skills identity (the SKILL.md
/// <c>name</c>), which is also how a shared Lumi refers to it; <see cref="Name"/> is the display name.
/// </summary>
public sealed record SharedSkill(string Slug, string Name, string Description, string Content, string IconGlyph)
{
    /// <summary>Optional <c>license</c> from a foreign SKILL.md, shown on the import receipt.</summary>
    public string? License { get; init; }

    /// <summary>
    /// Files that sat next to an imported SKILL.md (scripts/, references/, …). Lumi skills are a single
    /// document, so these are reported on the receipt instead of being silently dropped.
    /// </summary>
    public IReadOnlyList<string> CompanionFiles { get; init; } = [];
}

/// <summary>A Lumi (custom agent) as it travels. Skills and MCP servers are referenced by their pack identity.</summary>
public sealed record SharedLumi(
    string Name,
    string Description,
    string SystemPrompt,
    string IconGlyph,
    IReadOnlyList<string>? ToolNames,
    IReadOnlyList<string> SkillSlugs,
    IReadOnlyList<string> McpServerNames)
{
    /// <summary>Null means the Lumi may use every Lumi tool; an empty list means none.</summary>
    public bool RestrictsTools => ToolNames is not null;
}

/// <summary>
/// An MCP server as it travels. There is deliberately nowhere to put an environment variable or header
/// VALUE: packs carry the names a recipient must fill in, never the secrets themselves.
/// </summary>
public sealed record SharedMcpServer(
    string Name,
    string Description,
    bool IsRemote,
    string Command,
    IReadOnlyList<string> Args,
    string Url,
    IReadOnlyList<string> EnvKeys,
    IReadOnlyList<string> HeaderKeys,
    IReadOnlyList<string> Tools,
    int? Timeout)
{
    /// <summary>Names of every value the recipient has to provide before the server can work.</summary>
    public IReadOnlyList<string> RequiredKeys => IsRemote ? HeaderKeys : EnvKeys;

    /// <summary>True when an argument or the URL still holds a value that was removed before sharing.</summary>
    public bool HasRedactedValues
        => Url.Contains(SecretRedactor.Placeholder, StringComparison.Ordinal)
           || Args.Any(static arg => arg.Contains(SecretRedactor.Placeholder, StringComparison.Ordinal));

    /// <summary>The command line exactly as it would run (or the URL it connects to), for the receipt.</summary>
    public string CommandLine => IsRemote
        ? Url
        : Args.Count == 0 ? Command : Command + " " + string.Join(' ', Args.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string arg)
        => arg.Length == 0 || arg.Any(char.IsWhiteSpace) ? "\"" + arg + "\"" : arg;
}

/// <summary>
/// Everything a share contains, independent of the text format it came from. The primary item
/// (<see cref="Kind"/>) is what the recipient sees on the card; the rest is what it needs to work.
/// </summary>
public sealed class CapabilityPack
{
    public required SharedCapabilityKind Kind { get; init; }
    public required CapabilityPackFormat Format { get; init; }
    public required string Name { get; init; }
    public string Description { get; init; } = "";
    public string IconGlyph { get; init; } = "";
    public IReadOnlyList<SharedSkill> Skills { get; init; } = [];
    public SharedLumi? Lumi { get; init; }
    public IReadOnlyList<SharedMcpServer> McpServers { get; init; } = [];

    /// <summary>Reader observations worth telling the user (dangling references, skipped entries).</summary>
    public IReadOnlyList<PackNote> Notes { get; init; } = [];
}

public enum PackNoteKind
{
    /// <summary>A shared Lumi referred to a skill or MCP server the pack did not include.</summary>
    MissingReference,

    /// <summary>An MCP config entry could not be understood (no command or URL) and was skipped.</summary>
    SkippedServer
}

public sealed record PackNote(PackNoteKind Kind, string Detail);

/// <summary>Why something about a share deserves the sharer's attention before it leaves the machine.</summary>
public enum ShareFindingKind
{
    /// <summary>An environment variable or header value was left out; only its name is shared.</summary>
    SecretValueRemoved,

    /// <summary>A credential embedded in an argument (connection string, --token=…) was replaced.</summary>
    CredentialRemovedFromArgument,

    /// <summary>A credential embedded in a URL (password, ?api_key=, secret path segment) was replaced.</summary>
    CredentialRemovedFromUrl,

    /// <summary>A command or argument points at a path on this computer; the recipient will need their own.</summary>
    LocalPath,

    /// <summary>Instructions contain text that looks like a real token. Shared as written, so the sharer is warned.</summary>
    TextLooksLikeSecret
}

public sealed record ShareFinding(ShareFindingKind Kind, string ItemName, string Detail);

/// <summary>A ready-to-share pack: the portable text plus what was removed or flagged on the way out.</summary>
public sealed record CapabilityShare(
    CapabilityPack Pack,
    string Text,
    string FileName,
    IReadOnlyList<ShareFinding> Findings);
