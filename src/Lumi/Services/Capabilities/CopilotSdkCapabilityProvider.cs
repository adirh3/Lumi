using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Contributes every capability the GitHub Copilot runtime can see — project skills and agents,
/// the signed-in user's profile (<c>~/.copilot</c>), plugin-installed capabilities, workspace and
/// user MCP configuration, and built-ins — by asking the SDK's server-level discovery RPCs.
/// </summary>
/// <remarks>
/// This provider is the reason Lumi no longer enumerates the file system looking for skills,
/// agents or MCP servers: whatever mechanism Copilot supports today or adds later is discovered
/// by the runtime and reported here, complete with the source it came from.
/// </remarks>
public sealed class CopilotSdkCapabilityProvider : ICapabilityProvider
{
    /// <summary>Glyph used for skills the Copilot runtime discovered.</summary>
    public const string SkillGlyph = "\u26A1";

    /// <summary>Glyph used for agents the Copilot runtime discovered.</summary>
    public const string AgentGlyph = "\U0001F916";

    /// <summary>Glyph used for MCP servers the Copilot runtime discovered.</summary>
    public const string McpGlyph = "\U0001F50C";

    /// <summary>
    /// Discovery feeds a picker, never a blocking dependency, so a wedged runtime is abandoned
    /// rather than allowed to stall a caller indefinitely.
    /// </summary>
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromSeconds(20);

    private readonly Func<CopilotClient?> _clientAccessor;

    public CopilotSdkCapabilityProvider(Func<CopilotClient?> clientAccessor)
    {
        ArgumentNullException.ThrowIfNull(clientAccessor);
        _clientAccessor = clientAccessor;
    }

    public string Id => "copilot-sdk";

    public async Task<CapabilityProviderResult> LoadAsync(CapabilityQuery query, CancellationToken cancellationToken)
    {
        if (_clientAccessor() is not { } client)
            return CapabilityProviderResult.Unavailable;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);
        var token = timeout.Token;

        var projectPaths = query.WorkingDirectories.ToList();

        var skills = LoadSkillsAsync(client, projectPaths, token);
        var agents = LoadAgentsAsync(client, projectPaths, token);
        var mcpServers = LoadMcpServersAsync(client, projectPaths, token);
        var skillRoots = LoadSessionSkillRootsAsync(client, projectPaths, token);

        try
        {
            await Task.WhenAll(skills, agents, mcpServers, skillRoots).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Debug.WriteLine("[Capabilities] Discovery timed out.");
            return CapabilityProviderResult.Unavailable;
        }
        catch (Exception ex)
        {
            // A discovery call that failed did not report "nothing here" — it reported nothing at
            // all. Treating the gap as an answer would let consumers prune a saved selection or
            // omit a server the user deselected from the session's disabled list, so the whole
            // load is declared unavailable and retried instead.
            Debug.WriteLine($"[Capabilities] Discovery failed: {ex.Message}");
            return CapabilityProviderResult.Unavailable;
        }

        var capabilities = new List<CapabilityDescriptor>();
        capabilities.AddRange(await skills.ConfigureAwait(false));
        capabilities.AddRange(await agents.ConfigureAwait(false));
        capabilities.AddRange(await mcpServers.ConfigureAwait(false));

        return new CapabilityProviderResult(
            capabilities,
            SessionSkillRoots: await skillRoots.ConfigureAwait(false));
    }

    /// <summary>
    /// Returns an agent's persona body.
    /// </summary>
    /// <remarks>
    /// Agent discovery reports where an agent lives but not its prompt, and Lumi needs the body to
    /// apply a Copilot agent as the active persona and to register it as a delegatable subagent.
    /// This reads the single file the runtime named — it is not discovery, and an agent with no
    /// reachable file (remote, plugin-hosted) simply has no body.
    /// </remarks>
    private static string? ResolveAgentPrompt(GitHub.Copilot.Rpc.AgentInfo agent)
    {
        if (!string.IsNullOrWhiteSpace(agent.Prompt))
            return agent.Prompt;

        var descriptor = new CapabilityDescriptor
        {
            Kind = CapabilityKind.Agent,
            Name = agent.Name,
            Origin = CapabilityOrigin.Other,
            SourcePath = agent.Path,
        };

        return CapabilityContent.TryReadBody(descriptor, out var body) ? body : null;
    }

    /// <summary>
    /// Asks the runtime where it looks for skills across every directory in the query.
    /// </summary>
    /// <remarks>
    /// A session has one working directory and resolves its "personal" scope from its config
    /// directory, which Lumi points at its own folder to stay isolated from the CLI's. So two kinds
    /// of root are discovered here but unreachable by the session on its own: the user's
    /// <c>~/.copilot</c> skills, and skills in a project's additional context folders. Both are
    /// handed to the session explicitly, which adds to its discovery rather than replacing it.
    /// Plugin and built-in scopes are runtime-internal and need no path.
    /// </remarks>
    private async Task<IReadOnlyList<string>> LoadSessionSkillRootsAsync(
        CopilotClient client,
        List<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Skills
            .GetDiscoveryPathsAsync(projectPaths, excludeHostSkills: null, cancellationToken)
            .ConfigureAwait(false);

        if (result?.Paths is not { Count: > 0 } paths)
            return [];

        return paths
            .Where(static path => path.Scope.Value.StartsWith("personal", StringComparison.OrdinalIgnoreCase)
                                  || path.Scope.Value.StartsWith("project", StringComparison.OrdinalIgnoreCase))
            .Select(static path => path.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Distinct(CapabilityQuery.DirectoryComparer)
            .ToArray();
    }

    private async Task<IReadOnlyList<CapabilityDescriptor>> LoadSkillsAsync(
        CopilotClient client,
        List<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Skills
            .DiscoverAsync(projectPaths, skillDirectories: null, excludeHostSkills: null, cancellationToken)
            .ConfigureAwait(false);

        // Per-skill parse errors describe individual entries the runtime rejected, not a source it
        // failed to consult, so the rest of the discovery still stands.
        if (result?.Errors is { Count: > 0 } errors)
        {
            foreach (var error in errors)
                Debug.WriteLine($"[Capabilities] Skill discovery: {error}");
        }

        if (result?.Skills is not { Count: > 0 } discovered)
            return [];

        return discovered
            .Where(static skill => !string.IsNullOrWhiteSpace(skill.Name))
            .Select(skill => new CapabilityDescriptor
            {
                Kind = CapabilityKind.Skill,
                Name = skill.Name,
                Origin = CapabilityOrigin.FromSdkSource(skill.Source.Value),
                Description = skill.Description,
                SourcePath = skill.Path,
                IsEnabled = skill.Enabled,
                IsUserInvocable = skill.UserInvocable,
                Glyph = SkillGlyph,
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<CapabilityDescriptor>> LoadAgentsAsync(
        CopilotClient client,
        List<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Agents
            .DiscoverAsync(projectPaths, excludeHostAgents: null, cancellationToken)
            .ConfigureAwait(false);

        if (result?.Agents is not { Count: > 0 } discovered)
            return [];

        return discovered
            .Where(static agent => !string.IsNullOrWhiteSpace(agent.Name))
            .Select(agent => new CapabilityDescriptor
            {
                Kind = CapabilityKind.Agent,
                Name = agent.Name,
                DisplayName = agent.DisplayName,
                Origin = CapabilityOrigin.FromSdkSource(agent.Source?.Value),
                Description = agent.Description,
                Content = ResolveAgentPrompt(agent),
                SourcePath = agent.Path,
                IsUserInvocable = agent.UserInvocable ?? true,
                Glyph = AgentGlyph,
                Behavior = new AgentBehavior(
                    agent.Tools?.ToArray(),
                    agent.Model,
                    agent.Skills?.ToArray()),
            })
            .ToArray();
    }

    /// <summary>
    /// MCP discovery is scoped to a single working directory, so each project context folder is
    /// probed and the results are merged with the first definition of a name winning — the same
    /// precedence Lumi applied when it parsed workspace MCP config itself.
    /// </summary>
    private async Task<IReadOnlyList<CapabilityDescriptor>> LoadMcpServersAsync(
        CopilotClient client,
        List<string> projectPaths,
        CancellationToken cancellationToken)
    {
        var directories = projectPaths.Count > 0 ? projectPaths : [string.Empty];
        var servers = new Dictionary<string, CapabilityDescriptor>(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            var result = await client.Rpc.Mcp
                .DiscoverAsync(directory, cancellationToken)
                .ConfigureAwait(false);

            if (result?.Servers is not { Count: > 0 } discovered)
                continue;

            foreach (var server in discovered)
            {
                if (string.IsNullOrWhiteSpace(server.Name))
                    continue;

                servers.TryAdd(server.Name, new CapabilityDescriptor
                {
                    Kind = CapabilityKind.McpServer,
                    Name = server.Name,
                    Origin = CapabilityOrigin.FromSdkSource(server.Source.Value, server.SourcePlugin),
                    IsEnabled = server.Enabled,
                    SourcePath = directory.Length == 0 ? null : directory,
                    Glyph = McpGlyph,
                });
            }
        }

        return servers.Values.ToArray();
    }
}
