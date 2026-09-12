using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GitHub.Copilot;
using Lumi.Models;
using Lumi.Services.Capabilities;

namespace Lumi.Services;

/// <summary>Supplies Lumi's Markdown to the native skill tool without a filesystem identity.</summary>
public sealed class LumiSkillProvider(
    Func<CancellationToken, Task<IReadOnlyList<Skill>>> getSkills,
    CapabilitySnapshot? capabilities = null) : SkillProvider
{
    private sealed record Binding(Guid Id, SkillProviderDescriptor Descriptor);
    private readonly Func<CancellationToken, Task<IReadOnlyList<Skill>>> _getSkills =
        getSkills ?? throw new ArgumentNullException(nameof(getSkills));

    private IReadOnlyDictionary<string, Binding> _catalog =
        new Dictionary<string, Binding>(StringComparer.OrdinalIgnoreCase);

    public override async Task<IReadOnlyList<SkillProviderDescriptor>> ListAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var skills = await _getSkills(cancellationToken).ConfigureAwait(false);
        var names = GetRuntimeNames(skills, capabilities);
        var catalog = skills.ToDictionary(
            skill => names[skill.Id],
            skill => new Binding(skill.Id, new SkillProviderDescriptor
            {
                Name = names[skill.Id],
                Description = GetDescription(skill),
            }),
            StringComparer.OrdinalIgnoreCase);
        Volatile.Write(ref _catalog, catalog);
        return catalog.Values.Select(binding => binding.Descriptor).ToArray();
    }

    public override async Task<string> ReadAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Volatile.Read(ref _catalog).TryGetValue(name, out var binding))
            throw new KeyNotFoundException($"Lumi skill '{name}' is not in this session's skill catalog.");

        var skills = await _getSkills(cancellationToken).ConfigureAwait(false);
        var skill = skills.FirstOrDefault(candidate => candidate.Id == binding.Id)
            ?? throw new KeyNotFoundException($"Lumi skill '{name}' was deleted.");

        // The header must match the advertised catalog, even if an edit happened before this read.
        return DataStore.BuildSkillMarkdown(
            skill,
            binding.Descriptor.Name,
            binding.Descriptor.Description);
    }

    internal static bool IsAllowedForAgent(LumiAgent? agent)
        => agent is not { HasToolRestrictions: true }
           || ToolDisplayHelper.ToRuntimeToolNames(agent.ToolNames).Contains("skill", StringComparer.Ordinal);

    internal string? GetInvocationName(Guid skillId)
        => Volatile.Read(ref _catalog).FirstOrDefault(pair => pair.Value.Id == skillId).Key;

    internal static IReadOnlyDictionary<Guid, string> GetRuntimeNames(
        IReadOnlyList<Skill> skills,
        CapabilitySnapshot? capabilities = null)
    {
        var candidates = skills.Select(skill => (Skill: skill, Name: GetBaseName(skill))).ToArray();
        var counts = candidates.GroupBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.OrdinalIgnoreCase);
        return candidates.ToDictionary(
            candidate => candidate.Skill.Id,
            candidate => counts[candidate.Name] == 1
                         && capabilities?.NativeSkillInvocationNames.Contains(candidate.Name) != true
                ? candidate.Name
                : $"{candidate.Name[..Math.Min(31, candidate.Name.Length)].TrimEnd('-')}-{candidate.Skill.Id:N}");
    }

    internal static Skill? FindSkill(
        IReadOnlyList<Skill> skills,
        string runtimeName,
        CapabilitySnapshot? capabilities = null)
    {
        var names = GetRuntimeNames(skills, capabilities);
        return skills.FirstOrDefault(skill =>
            string.Equals(names[skill.Id], runtimeName, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetBaseName(Skill skill)
    {
        var slug = CapabilitySnapshot.Slugify(skill.Name) ?? "";
        var name = new string(slug.Where(character =>
            char.IsAsciiLetterOrDigit(character) || character == '-').ToArray()).Trim('-');
        if (name.Length == 0)
            return $"lumi-{skill.Id:N}";
        return name[..Math.Min(64, name.Length)].TrimEnd('-');
    }

    private static string GetDescription(Skill skill)
    {
        var description = string.IsNullOrWhiteSpace(skill.Description)
            ? skill.Name
            : $"{skill.Name}: {skill.Description}";
        if (description.Length <= 1024)
            return description;
        var length = char.IsHighSurrogate(description[1023]) ? 1023 : 1024;
        return description[..length];
    }
}
