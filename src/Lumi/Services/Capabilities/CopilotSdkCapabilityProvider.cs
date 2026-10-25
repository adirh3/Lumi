using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Services.Discover;

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
    private readonly Func<string> _runtimeHome;
    private readonly Func<IReadOnlyList<string>> _personalSkillDirectories;

    public CopilotSdkCapabilityProvider(
        Func<CopilotClient?> clientAccessor,
        Func<string>? runtimeHome = null,
        Func<IReadOnlyList<string>>? personalSkillDirectories = null)
    {
        ArgumentNullException.ThrowIfNull(clientAccessor);
        _clientAccessor = clientAccessor;
        _runtimeHome = runtimeHome ?? (static () => CopilotService.RuntimeHomeDirectory);
        _personalSkillDirectories = personalSkillDirectories ?? (static () => []);
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
        var configuredFolders = _personalSkillDirectories().ToArray();
        var personalSkillRoots = Task.Run(
            () => PersonalSkillFolders.GetExistingDirectories(configuredFolders), token);

        var skills = LoadSkillsAsync(client, projectPaths, personalSkillRoots, token);
        var agents = LoadAgentsAsync(client, projectPaths, token);
        var mcpServers = LoadMcpServersAsync(client, projectPaths, token);
        var skillRoots = LoadSessionSkillRootsAsync(client, projectPaths, personalSkillRoots, token);
        var pluginRoots = LoadSessionPluginRootsAsync(client, token);

        try
        {
            await Task.WhenAll(skills, agents, mcpServers, skillRoots, pluginRoots).ConfigureAwait(false);
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
            SessionSkillRoots: await skillRoots.ConfigureAwait(false),
            SessionPluginRoots: await pluginRoots.ConfigureAwait(false));
    }

    /// <summary>
    /// The folders of every enabled installed plugin.
    /// </summary>
    /// <remarks>
    /// The runtime installs plugins into its home (<c>~/.copilot</c>) and loads them for sessions
    /// configured from that home. Lumi's sessions use Lumi's own config folder, so without these
    /// paths a plugin's skills, agents and MCP servers are listed but never reach a chat.
    /// </remarks>
    private async Task<IReadOnlyList<string>> LoadSessionPluginRootsAsync(CopilotClient client, CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Plugins.ListAsync(cancellationToken).ConfigureAwait(false);
        if (result?.Plugins is not { Count: > 0 } plugins)
            return [];

        var home = _runtimeHome();
        var locations = PluginInstallLocator.Read(home);
        return plugins
            .Where(static plugin => plugin.Enabled && plugin.Installed != false)
            .Select(plugin => PluginInstallLocator.Resolve(locations, home, plugin.Name, plugin.Marketplace))
            .OfType<string>()
            .Distinct(CapabilityQuery.DirectoryComparer)
            .ToArray();
    }

    /// <summary>
    /// The runtime prefixes what a plugin installs with the plugin's name (<c>azure:deploy</c>);
    /// failing that, the plugin is the folder under <c>installed-plugins/&lt;marketplace&gt;/</c>.
    /// </summary>
    internal static string? ResolvePluginName(string? source, string? qualifiedName, string? path)
    {
        if (!string.Equals(source, CapabilityOrigin.PluginId, StringComparison.OrdinalIgnoreCase))
            return null;

        if (qualifiedName is { Length: > 0 } name && name.IndexOf(':') is > 0 and var colon)
            return name[..colon];

        if (string.IsNullOrWhiteSpace(path))
            return null;

        var segments = path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var index = Array.FindIndex(segments, static segment => segment.Equals("installed-plugins", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 2 < segments.Length ? segments[index + 2] : null;
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
        Task<IReadOnlyList<string>> personalSkillRoots,
        CancellationToken cancellationToken)
    {
        var result = await client.Rpc.Skills
            .GetDiscoveryPathsAsync(projectPaths, excludeHostSkills: null, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        var nativeRoots = result?.Paths?
            .Where(static path => path.Scope.Value.StartsWith("personal", StringComparison.OrdinalIgnoreCase)
                                  || path.Scope.Value.StartsWith("project", StringComparison.OrdinalIgnoreCase))
            .Select(static path => path.Path)
            .Where(static path => !string.IsNullOrWhiteSpace(path)) ?? [];

        return nativeRoots
            .Concat(await personalSkillRoots.WaitAsync(cancellationToken).ConfigureAwait(false))
            .Distinct(CapabilityQuery.DirectoryComparer)
            .ToArray();
    }

    private async Task<IReadOnlyList<CapabilityDescriptor>> LoadSkillsAsync(
        CopilotClient client,
        List<string> projectPaths,
        Task<IReadOnlyList<string>> personalSkillRoots,
        CancellationToken cancellationToken)
    {
        var directories = await personalSkillRoots.WaitAsync(cancellationToken).ConfigureAwait(false);
        var result = await client.Rpc.Skills
            .DiscoverAsync(projectPaths,
                skillDirectories: directories.Count > 0 ? directories.ToList() : null,
                excludeHostSkills: null, cancellationToken: cancellationToken)
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
            .Select(skill =>
            {
                var pluginName = ResolvePluginName(skill.Source.Value, skill.CommandName, skill.Path);
                return new CapabilityDescriptor
                {
                    Kind = CapabilityKind.Skill,
                    Name = skill.Name,
                    SkillInvocationName = skill.CommandName,
                    Origin = CapabilityOrigin.FromSdkSource(skill.Source.Value, pluginName),
                    Description = skill.Description,
                    SourcePath = skill.Path,
                    IsEnabled = skill.Enabled,
                    IsUserInvocable = skill.UserInvocable,
                    Glyph = SkillGlyph,
                    PluginName = pluginName,
                };
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
            .Select(agent =>
            {
                var pluginName = ResolvePluginName(agent.Source?.Value, agent.Name, agent.Path);
                return new CapabilityDescriptor
                {
                    Kind = CapabilityKind.Agent,
                    Name = agent.Name,
                    DisplayName = agent.DisplayName,
                    Origin = CapabilityOrigin.FromSdkSource(agent.Source?.Value, pluginName),
                    Description = agent.Description,
                    Content = ResolveAgentPrompt(agent),
                    SourcePath = agent.Path,
                    IsUserInvocable = agent.UserInvocable ?? true,
                    Glyph = AgentGlyph,
                    Behavior = new AgentBehavior(
                        agent.Tools?.ToArray(),
                        agent.Model,
                        agent.Skills?.ToArray()),
                    PluginName = pluginName,
                };
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
                .DiscoverAsync(directory, includeEffectiveSource: true, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            if (result?.Servers is not { Count: > 0 } discovered)
                continue;

            foreach (var server in discovered)
            {
                if (string.IsNullOrWhiteSpace(server.Name))
                    continue;

                var pluginName = string.IsNullOrWhiteSpace(server.SourcePlugin) ? null : server.SourcePlugin;
                var file = server.EffectiveSource?.File;
                servers.TryAdd(server.Name, new CapabilityDescriptor
                {
                    Kind = CapabilityKind.McpServer,
                    Name = server.Name,
                    Origin = CapabilityOrigin.FromSdkSource(server.Source.Value, pluginName),
                    IsEnabled = server.Enabled,
                    SourcePath = directory.Length == 0 ? null : directory,
                    Glyph = McpGlyph,
                    PluginName = pluginName,
                    ConfigFilePath = ToLocalPath(file?.Uri),
                    ConfigPointer = file?.JsonPointer,
                    McpTransport = server.Type?.Value,
                });
            }
        }

        return servers.Values.ToArray();
    }

    private static string? ToLocalPath(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.IsFile ? parsed.LocalPath : null;
}
