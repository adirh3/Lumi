using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;
using Lumi.Services;
using Lumi.Services.Capabilities;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private const string ExternalSkillGlyph = CopilotSdkCapabilityProvider.SkillGlyph;
    private const string ExternalAgentGlyph = CopilotSdkCapabilityProvider.AgentGlyph;

    /// <summary>
    /// Builds the capability query for the current chat: the effective working directory plus the
    /// active project's additional context folders.
    /// </summary>
    private CapabilityQuery BuildCapabilityQuery()
        => BuildCapabilityQuery(GetEffectiveWorkingDirectory(), GetCurrentProject());

    private static CapabilityQuery BuildCapabilityQuery(string? workingDirectory, Project? project)
        => new(ProjectContextDirectoryHelper.GetExistingContextDirectories(workingDirectory ?? "", project));

    /// <summary>
    /// The merged capability snapshot for the current chat. Returns immediately with everything
    /// known so far and schedules a background refresh when Copilot discovery has not landed yet.
    /// </summary>
    private CapabilitySnapshot GetCapabilities()
        => _capabilityCatalog.GetSnapshot(BuildCapabilityQuery());

    /// <summary>
    /// Capabilities for a standalone directory. This intentionally does not include the currently
    /// selected project's additional folders.
    /// </summary>
    private CapabilitySnapshot GetCapabilities(string effectiveWorkingDirectory)
        => _capabilityCatalog.GetSnapshot(BuildCapabilityQuery(effectiveWorkingDirectory, project: null));

    private CapabilitySnapshot GetCapabilities(Chat chat, string? effectiveWorkingDirectory = null)
        => _capabilityCatalog.GetSnapshot(BuildCapabilityQuery(chat, effectiveWorkingDirectory));

    /// <summary>Capability query for a chat, honouring its project's additional context folders.</summary>
    private CapabilityQuery BuildCapabilityQuery(Chat chat, string? effectiveWorkingDirectory = null)
    {
        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId.Value)
            : null;

        return BuildCapabilityQuery(
            effectiveWorkingDirectory ?? GetEffectiveWorkingDirectory(chat),
            project);
    }

    internal static string? GetSessionSdkAgentName(Chat chat, Chat? currentChat, string? selectedSdkAgentName)
    {
        if (!string.IsNullOrWhiteSpace(chat.SdkAgentName))
            return chat.SdkAgentName;

        return currentChat?.Id == chat.Id ? selectedSdkAgentName : null;
    }

    /// <summary>
    /// Chooses the agent name handed to <c>config.Agent</c>. A Lumi agent always wins — it is
    /// Lumi's own persona and is applied through the system prompt. Everything else is a Copilot
    /// agent the runtime resolves by name.
    /// </summary>
    /// <param name="routedAgentName">
    /// The canonical name of the resolved Copilot agent, or null when none applies. It must be the
    /// catalog's spelling, not the caller's: the runtime matches <c>config.Agent</c> against a
    /// registered agent's name exactly, while a saved selection may hold an older or slugged form.
    /// </param>
    internal static string? ResolveSessionAgentName(LumiAgent? activeAgent, string? routedAgentName)
    {
        if (!string.IsNullOrWhiteSpace(activeAgent?.Name))
            return activeAgent.Name;

        return string.IsNullOrWhiteSpace(routedAgentName) ? null : routedAgentName;
    }

    /// <summary>
    /// Resolves the Copilot agent a session should activate, returning the catalog's canonical name.
    /// Membership in the snapshot the session is built from — not in the composer's chips — is what
    /// decides this, so a background surface with no UI routes just as well. An agent the user may
    /// not pick directly is delegable only and never becomes the session's persona.
    /// </summary>
    private static string? ResolveRoutedAgentName(CapabilitySnapshot capabilities, string? sdkAgentName)
        => capabilities.FindAgent(sdkAgentName)
            is { Origin.IsLumi: false, IsEnabled: true, IsUserInvocable: true } agent
            ? agent.Name
            : null;

    private static SkillReference CreateExternalSkillReference(CapabilityDescriptor skill)
    {
        return new SkillReference
        {
            Name = skill.Name,
            Glyph = string.IsNullOrWhiteSpace(skill.Glyph) ? ExternalSkillGlyph : skill.Glyph,
            Description = skill.Description ?? string.Empty
        };
    }
}
