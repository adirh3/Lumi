using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Lumi.Services.Capabilities;

/// <summary>
/// The merged, de-duplicated view of every capability available for one
/// <see cref="CapabilityQuery"/>. This is what the composer, the session builder and the
/// system prompt all read; nothing downstream knows which provider supplied an entry.
/// </summary>
public sealed class CapabilitySnapshot
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static CapabilitySnapshot Empty { get; } = new(CapabilityQuery.Empty, [], isComplete: false);

    public CapabilitySnapshot(
        CapabilityQuery query,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        bool isComplete,
        IReadOnlyList<string>? sessionSkillRoots = null)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(capabilities);

        Query = query;
        IsComplete = isComplete;
        SessionSkillRoots = sessionSkillRoots ?? [];

        Skills = Order(capabilities.Where(static c => c.Kind == CapabilityKind.Skill));
        Agents = Order(capabilities.Where(static c => c.Kind == CapabilityKind.Agent));
        McpServers = Order(capabilities.Where(static c => c.Kind == CapabilityKind.McpServer));
    }

    public CapabilityQuery Query { get; }

    /// <summary>
    /// Skill roots that must be handed to a session explicitly because its own configuration would
    /// not reach them — in practice the user's Copilot profile, since Lumi points a session's
    /// config directory at its own folder. Reported by the runtime, never discovered by Lumi.
    /// </summary>
    public IReadOnlyList<string> SessionSkillRoots { get; }

    public IReadOnlyList<CapabilityDescriptor> Skills { get; }

    public IReadOnlyList<CapabilityDescriptor> Agents { get; }

    public IReadOnlyList<CapabilityDescriptor> McpServers { get; }

    /// <summary>
    /// False while only synchronous providers have reported. The composer paints an incomplete
    /// snapshot immediately and repaints once the Copilot runtime finishes discovery.
    /// </summary>
    public bool IsComplete { get; }

    public CapabilityDescriptor? FindSkill(string? name) => Find(name, Skills, matchSlug: true);

    /// <summary>
    /// Resolves an agent the same way as a skill. An agent's file name and its authored name often
    /// differ only by separators and case (<c>lumi-e2e-agent.md</c> vs <c>Lumi E2E Agent</c>), and a
    /// caller outside the picker — a remote client, a saved selection — may hold either form.
    /// </summary>
    public CapabilityDescriptor? FindAgent(string? name) => Find(name, Agents, matchSlug: true);

    public CapabilityDescriptor? FindMcpServer(string? name)
        => Find(name, McpServers, matchSlug: false);

    /// <summary>
    /// Matches on the canonical name first, then on a separator/case-insensitive slug. The native
    /// Copilot tools report slugified ids (e.g. <c>Publish-New-Version</c>) while the catalog is
    /// keyed by the authored name (<c>Publish New Version</c>); both must resolve to one entry.
    /// </summary>
    private static CapabilityDescriptor? Find(
        string? name,
        IReadOnlyList<CapabilityDescriptor> capabilities,
        bool matchSlug)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var match = capabilities.FirstOrDefault(
            capability => NameComparer.Equals(capability.Name, name));
        if (match is not null || !matchSlug)
            return match;

        var slug = Slugify(name);
        return slug is null
            ? null
            : capabilities.FirstOrDefault(
                capability => string.Equals(Slugify(capability.Name), slug, StringComparison.Ordinal));
    }

    /// <summary>Capabilities a user may pick directly in the composer.</summary>
    public IEnumerable<CapabilityDescriptor> UserInvocable(CapabilityKind kind)
        => Get(kind).Where(static c => c.IsUserInvocable && c.IsEnabled);

    public IReadOnlyList<CapabilityDescriptor> Get(CapabilityKind kind) => kind switch
    {
        CapabilityKind.Skill => Skills,
        CapabilityKind.Agent => Agents,
        CapabilityKind.McpServer => McpServers,
        _ => [],
    };

    /// <summary>
    /// Orders by origin rank then name so Lumi's own capabilities lead each picker and
    /// Copilot-supplied ones follow grouped by where they came from.
    /// </summary>
    private static IReadOnlyList<CapabilityDescriptor> Order(IEnumerable<CapabilityDescriptor> capabilities)
        => capabilities
            .OrderBy(static c => c.Origin.Rank)
            .ThenBy(static c => c.Label, NameComparer)
            .ToArray();

    /// <summary>
    /// Reduces a name to a canonical slug (lowercase alphanumerics separated by single hyphens)
    /// so the native skill tool's id and the catalog's front-matter name compare equal.
    /// </summary>
    internal static string? Slugify(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        var builder = new StringBuilder(name.Length);
        var pendingSeparator = false;
        foreach (var ch in name)
        {
            if (char.IsLetterOrDigit(ch))
            {
                if (pendingSeparator && builder.Length > 0)
                    builder.Append('-');
                pendingSeparator = false;
                builder.Append(char.ToLowerInvariant(ch));
            }
            else
            {
                pendingSeparator = true;
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }
}
