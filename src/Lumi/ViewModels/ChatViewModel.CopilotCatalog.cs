using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
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

    private CopilotSkillDefinition? FindExternalSkillByName(string name)
        => GetProjectContextCatalog().FindSkill(name);

    private CopilotSkillDefinition? FindExternalSkillByName(string name, string workDir)
        => GetProjectContextCatalog(workDir).FindSkill(name);

    private CopilotAgentDefinition? FindExternalAgentByName(string name)
        => GetProjectContextCatalog().FindAgent(name);

    private static CopilotSkillDefinition? FindExternalSkillByName(ProjectContextCatalogSnapshot catalog, string? name)
        => catalog.FindSkill(name);

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

    private List<SkillReference> BuildSkillReferences(
        IReadOnlyCollection<Guid> skillIds,
        IReadOnlyCollection<string> externalSkillNames,
        string? workDir = null)
    {
        var catalog = workDir is { Length: > 0 } ? GetProjectContextCatalog(workDir) : GetProjectContextCatalog();
        return BuildSkillReferences(skillIds, externalSkillNames, catalog);
    }

    private List<SkillReference> BuildSkillReferences(
        IReadOnlyCollection<Guid> skillIds,
        IReadOnlyCollection<string> externalSkillNames,
        ProjectContextCatalogSnapshot catalog)
    {
        var references = BuildSkillReferences(skillIds);
        if (externalSkillNames.Count == 0)
            return references;

        foreach (var skill in ResolveExternalSkills(catalog, externalSkillNames))
        {
            references.Add(CreateExternalSkillReference(skill));
        }

        return references;
    }

    private static bool SkillNameListsEqual(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
            return false;

        for (var i = 0; i < left.Count; i++)
        {
            if (!left[i].Equals(right[i], StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
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

    // File-based Copilot skills are not registered as persistent SDK session skills,
    // so selected ones must be resolved and reapplied from the catalog when needed.
    private static List<CopilotSkillDefinition> ResolveExternalSkills(
        ProjectContextCatalogSnapshot externalCatalog,
        IReadOnlyCollection<string> externalSkillNames)
    {
        var skills = new List<CopilotSkillDefinition>(externalSkillNames.Count);
        foreach (var externalSkillName in externalSkillNames)
        {
            var skill = FindExternalSkillByName(externalCatalog, externalSkillName);
            if (skill is not null)
                skills.Add(skill);
        }

        return skills;
    }

    private string AppendAvailableExternalSkillsToPrompt(
        string? systemPrompt,
        IReadOnlyList<CopilotSkillDefinition> externalSkills,
        IReadOnlyCollection<string> activeExternalSkillNames)
    {
        if (externalSkills.Count == 0)
            return systemPrompt ?? string.Empty;

        var internalSkillNames = _dataStore.Data.Skills
            .Select(skill => skill.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var promptSkills = externalSkills
            .Where(skill => !internalSkillNames.Contains(skill.Name))
            .ToList();
        if (promptSkills.Count == 0)
            return systemPrompt ?? string.Empty;

        var activeSkillNames = activeExternalSkillNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var builder = new StringBuilder(systemPrompt ?? string.Empty);
        builder.Append("""


            --- Additional Available Skills ---
            These file-based Copilot skills are also available. Use `fetch_skill` to retrieve their full content when relevant.

            """);

        foreach (var skill in promptSkills)
        {
            var activeMarker = activeSkillNames.Contains(skill.Name) ? " ✓" : string.Empty;
            var description = string.IsNullOrWhiteSpace(skill.Description)
                ? "Available from Copilot config"
                : skill.Description;
            builder.Append("- **")
                .Append(skill.Name)
                .Append("**: ")
                .Append(description)
                .Append(activeMarker)
                .Append('\n');
        }

        return builder.ToString();
    }
}
