using System;
using System.Collections.Generic;
using Lumi.Models;

namespace Lumi.Services.Capabilities;

/// <summary>The kind of capability a <see cref="CapabilityDescriptor"/> describes.</summary>
public enum CapabilityKind
{
    Skill,
    Agent,
    McpServer,
}

/// <summary>
/// Where a capability was loaded from. Origins are data, not code branches: a new capability
/// source only has to produce descriptors carrying a new origin and the whole pipeline
/// (merging, ordering, composer source hints) keeps working unchanged.
/// </summary>
/// <param name="Id">Stable machine id, for example <c>lumi</c>, <c>project</c> or <c>plugin</c>.</param>
/// <param name="Label">Short human label shown next to the capability in the composer.</param>
/// <param name="Rank">
/// Merge precedence and display order. Lower wins when two providers report the same
/// capability name, so Lumi's first-party definition always shadows an identically named
/// Copilot one — matching the behaviour Lumi had before the unified pipeline.
/// </param>
public sealed record CapabilityOrigin(string Id, string Label, int Rank)
{
    public const string LumiId = "lumi";
    public const string ProjectId = "project";
    public const string WorkspaceId = "workspace";
    public const string PersonalId = "personal";
    public const string PluginId = "plugin";
    public const string RemoteId = "remote";
    public const string BuiltInId = "builtin";
    public const string UnknownId = "other";

    /// <summary>Lumi's own store (skills, Lumis and MCP servers managed inside the app).</summary>
    public static readonly CapabilityOrigin Lumi = new(LumiId, "Lumi", 0);

    /// <summary>Discovered from the current project/workspace folders.</summary>
    public static readonly CapabilityOrigin Project = new(ProjectId, "Project", 10);

    /// <summary>Discovered from workspace-level configuration (for example a workspace MCP config).</summary>
    public static readonly CapabilityOrigin Workspace = new(WorkspaceId, "Workspace", 20);

    /// <summary>Discovered from the signed-in user's Copilot profile (<c>~/.copilot</c>).</summary>
    public static readonly CapabilityOrigin Personal = new(PersonalId, "Personal", 30);

    /// <summary>Provided by a remote Copilot configuration.</summary>
    public static readonly CapabilityOrigin Remote = new(RemoteId, "Remote", 40);

    /// <summary>Shipped by the Copilot runtime itself.</summary>
    public static readonly CapabilityOrigin BuiltIn = new(BuiltInId, "Built-in", 60);

    /// <summary>Reported by the SDK with a source Lumi does not have a friendlier name for.</summary>
    public static readonly CapabilityOrigin Other = new(UnknownId, "Copilot", 70);

    /// <summary>Installed by a Copilot plugin. The plugin name is carried in the label.</summary>
    public static CapabilityOrigin ForPlugin(string? pluginName)
        => string.IsNullOrWhiteSpace(pluginName)
            ? new CapabilityOrigin(PluginId, "Plugin", 50)
            : new CapabilityOrigin(PluginId, $"Plugin · {pluginName.Trim()}", 50);

    /// <summary>True for capabilities Lumi itself owns and can edit.</summary>
    public bool IsLumi => string.Equals(Id, LumiId, StringComparison.Ordinal);

    /// <summary>
    /// Maps a raw SDK source value onto a Lumi origin. Unknown values degrade to
    /// <see cref="Other"/> instead of throwing, so a newer CLI that introduces another source
    /// still surfaces its capabilities.
    /// </summary>
    public static CapabilityOrigin FromSdkSource(string? source, string? pluginName = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            return Other;

        return source.Trim().ToLowerInvariant() switch
        {
            "project" or "inherited" => Project,
            "workspace" => Workspace,
            "user" or "personal" or "personal-copilot" or "personal-agents" or "custom" => Personal,
            "plugin" => ForPlugin(pluginName),
            "remote" => Remote,
            "builtin" or "built-in" => BuiltIn,
            _ => Other,
        };
    }
}

/// <summary>
/// One capability — a skill, an agent (Lumi) or an MCP server — normalised across every source.
/// This is the single currency of the capability pipeline: providers emit descriptors, the
/// catalog merges descriptors, and every consumer (composer, session config, system prompt)
/// reads descriptors instead of touching a source-specific representation.
/// </summary>
public sealed record CapabilityDescriptor
{
    public required CapabilityKind Kind { get; init; }

    /// <summary>Canonical identity used for selection, persistence and de-duplication.</summary>
    public required string Name { get; init; }

    public required CapabilityOrigin Origin { get; init; }

    /// <summary>Label preferred for display; falls back to <see cref="Name"/>.</summary>
    public string? DisplayName { get; init; }

    public string? Description { get; init; }

    /// <summary>
    /// Capability body when the source can supply it without touching the file system:
    /// Lumi's store for first-party items, and the SDK's agent prompt for Copilot agents.
    /// </summary>
    public string? Content { get; init; }

    /// <summary>Path the SDK reported for this capability. Informational; never enumerated.</summary>
    public string? SourcePath { get; init; }

    /// <summary>Identifier of the owning Lumi entity when <see cref="Origin"/> is Lumi.</summary>
    public Guid? LumiId { get; init; }

    /// <summary>False when the source reports the capability as switched off.</summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>False for capabilities the model may use but a user may not pick directly.</summary>
    public bool IsUserInvocable { get; init; } = true;

    /// <summary>Icon shown in the composer and management surfaces.</summary>
    public string? Glyph { get; init; }

    /// <summary>
    /// How an agent was authored to behave. Carried verbatim so registering a discovered agent with
    /// the runtime cannot silently widen it — an agent authored with a restricted tool list must not
    /// end up with every tool because Lumi only forwarded its prompt.
    /// </summary>
    public AgentBehavior? Behavior { get; init; }

    /// <summary>
    /// The server definition for an MCP capability the runtime does not own, and therefore will not
    /// start on its own. Lumi must hand it to the session as configuration. Null for servers the
    /// runtime discovered itself, which it starts unless they are explicitly disabled.
    /// </summary>
    public McpServer? McpDefinition { get; init; }

    /// <summary>Label shown as the source hint in the composer.</summary>
    public string SourceLabel => Origin.Label;

    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Name : DisplayName!;
}

/// <summary>
/// The behavioural contract an agent was authored with, as the runtime reports it.
/// </summary>
/// <param name="Tools">
/// Tool allowlist. Null means the author set no restriction; an empty list means the author
/// allowed no tools at all. The two are not interchangeable — the runtime reads a null allowlist
/// on a registered agent as "every tool".
/// </param>
/// <param name="Model">Model the agent should run on, when it pins one.</param>
/// <param name="Skills">Skills the agent preloads.</param>
/// <remarks>
/// An agent's own MCP declarations are deliberately not carried: discovery reports them as raw
/// JSON rather than server configuration, and a session's MCP set is planned from the user's
/// selection anyway.
/// </remarks>
public sealed record AgentBehavior(
    IReadOnlyList<string>? Tools,
    string? Model,
    IReadOnlyList<string>? Skills)
{
    public bool IsEmpty
        => Tools is null
           && string.IsNullOrWhiteSpace(Model)
           && Skills is null or { Count: 0 };
}
