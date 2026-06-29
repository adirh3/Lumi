using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;
using Lumi.Services;

namespace Lumi.ViewModels;

public partial class ChatViewModel
{
    private const string ExternalSkillGlyph = "\u26A1";
    private const string ExternalAgentGlyph = "🤖";

    private ProjectContextCatalogSnapshot GetProjectContextCatalog()
        => ProjectContextCatalog.Discover(GetEffectiveWorkingDirectory(), GetCurrentProject());

    /// <summary>
    /// Discovers context for a standalone directory. This intentionally does not
    /// include the currently selected project's additional folders.
    /// </summary>
    private ProjectContextCatalogSnapshot GetProjectContextCatalog(string effectiveWorkingDirectory)
        => ProjectContextCatalog.Discover(effectiveWorkingDirectory, project: null);

    private ProjectContextCatalogSnapshot GetProjectContextCatalog(Chat chat, string? effectiveWorkingDirectory = null)
    {
        var project = chat.ProjectId.HasValue
            ? _dataStore.Data.Projects.FirstOrDefault(p => p.Id == chat.ProjectId.Value)
            : null;

        return ProjectContextCatalog.Discover(effectiveWorkingDirectory ?? GetEffectiveWorkingDirectory(chat), project);
    }

    private CopilotAgentDefinition? FindExternalAgentByName(string name)
        => GetProjectContextCatalog().FindAgent(name);

    private static CopilotAgentDefinition? FindExternalAgentByName(ProjectContextCatalogSnapshot catalog, string? name)
        => catalog.FindAgent(name);

    internal static string? GetSessionSdkAgentName(Chat chat, Chat? currentChat, string? selectedSdkAgentName)
    {
        if (!string.IsNullOrWhiteSpace(chat.SdkAgentName))
            return chat.SdkAgentName;

        return currentChat?.Id == chat.Id ? selectedSdkAgentName : null;
    }

    internal static string? ResolveSessionAgentName(
        LumiAgent? activeAgent,
        CopilotAgentDefinition? externalAgent,
        string? sdkAgentName,
        bool allowSdkAgentRouting)
    {
        if (!string.IsNullOrWhiteSpace(activeAgent?.Name))
            return activeAgent.Name;

        if (externalAgent is not null)
            return null;

        return allowSdkAgentRouting && !string.IsNullOrWhiteSpace(sdkAgentName)
            ? sdkAgentName
            : null;
    }

    private bool CanRouteSdkAgentByName(Chat chat, CopilotAgentDefinition? externalAgent, string? sdkAgentName)
    {
        if (externalAgent is not null || CurrentChat?.Id != chat.Id || string.IsNullOrWhiteSpace(sdkAgentName))
            return false;

        return AvailableAgentChips.Any(chip =>
            chip.Glyph == ExternalAgentGlyph
            && string.Equals(chip.Name, sdkAgentName, StringComparison.OrdinalIgnoreCase));
    }

    private static SkillReference CreateExternalSkillReference(CopilotSkillDefinition skill)
    {
        return new SkillReference
        {
            Name = skill.Name,
            Glyph = ExternalSkillGlyph,
            Description = skill.Description
        };
    }
}
