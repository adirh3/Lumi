using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lumi.Models;

namespace Lumi.Services.Capabilities;

/// <summary>
/// Lumi's own first-party capabilities — the skills, Lumis and MCP servers the user manages inside
/// the app. Read synchronously on the consumer's thread so in-app edits appear immediately and the
/// store is never enumerated while the UI mutates it.
/// </summary>
public sealed class LumiCapabilityProvider
{
    private readonly DataStore _dataStore;

    public LumiCapabilityProvider(DataStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        _dataStore = dataStore;
    }

    /// <summary>Glyph used for MCP capabilities Lumi manages itself.</summary>
    public const string LumiMcpGlyph = "\U0001F50C";

    public IReadOnlyList<CapabilityDescriptor> Load()
    {
        var data = _dataStore.Data;
        var capabilities = new List<CapabilityDescriptor>(
            data.Skills.Count + data.Agents.Count + data.McpServers.Count);

        foreach (var skill in data.Skills)
        {
            capabilities.Add(new CapabilityDescriptor
            {
                Kind = CapabilityKind.Skill,
                Name = skill.Name,
                Origin = CapabilityOrigin.Lumi,
                Description = skill.Description,
                Content = skill.Content,
                LumiId = skill.Id,
                Glyph = skill.IconGlyph,
            });
        }

        foreach (var agent in data.Agents)
        {
            capabilities.Add(new CapabilityDescriptor
            {
                Kind = CapabilityKind.Agent,
                Name = agent.Name,
                Origin = CapabilityOrigin.Lumi,
                Description = agent.Description,
                Content = agent.SystemPrompt,
                LumiId = agent.Id,
                Glyph = agent.IconGlyph,
            });
        }

        foreach (var server in data.McpServers)
        {
            capabilities.Add(new CapabilityDescriptor
            {
                Kind = CapabilityKind.McpServer,
                Name = server.Name,
                Origin = CapabilityOrigin.Lumi,
                Description = server.Description,
                LumiId = server.Id,
                IsEnabled = server.IsEnabled,
                Glyph = LumiMcpGlyph,
            });
        }

        return capabilities;
    }
}
