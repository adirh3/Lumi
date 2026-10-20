using System.Globalization;
using Lumi.Models;

namespace Lumi.Services.Sharing;

public enum ImportItemStatus
{
    /// <summary>Added under the name it was shared with.</summary>
    New,

    /// <summary>An identical item is already here; the import links to it instead of duplicating it.</summary>
    Reused,

    /// <summary>The name was taken by something different, so the import is added under a numbered name.</summary>
    Renamed
}

/// <summary>The most consequential thing an import would add, for the receipt's trust banner.</summary>
public enum ImportRisk
{
    InstructionsOnly,
    ConnectsToInternet,
    RunsOnThisComputer
}

public sealed record PlannedSkill(
    SharedSkill Source,
    string Name,
    string Description,
    string Content,
    ImportItemStatus Status,
    Guid? ExistingId);

public sealed record PlannedMcpServer(
    SharedMcpServer Source,
    string Name,
    string Description,
    string Command,
    IReadOnlyList<string> Args,
    string Url,
    ImportItemStatus Status,
    Guid? ExistingId);

public sealed record PlannedLumi(
    SharedLumi Source,
    string Name,
    string Description,
    string SystemPrompt,
    ImportItemStatus Status);

/// <summary>Exactly what an import would do, computed without touching the data store.</summary>
public sealed class ImportPlan
{
    public required CapabilityPack Pack { get; init; }
    public required IReadOnlyList<PlannedSkill> Skills { get; init; }
    public required IReadOnlyList<PlannedMcpServer> McpServers { get; init; }
    public PlannedLumi? Lumi { get; init; }

    /// <summary>Invisible characters stripped from names, instructions, prompts and commands.</summary>
    public int HiddenCharactersRemoved { get; init; }

    public ImportRisk Risk
        => McpServers.Any(static server => server.Status != ImportItemStatus.Reused && !server.Source.IsRemote)
            ? ImportRisk.RunsOnThisComputer
            : McpServers.Any(static server => server.Status != ImportItemStatus.Reused)
                ? ImportRisk.ConnectsToInternet
                : ImportRisk.InstructionsOnly;

    public int NewItemCount
        => Skills.Count(static skill => skill.Status != ImportItemStatus.Reused)
           + McpServers.Count(static server => server.Status != ImportItemStatus.Reused)
           + (Lumi is null ? 0 : 1);

    public int ReusedItemCount
        => Skills.Count(static skill => skill.Status == ImportItemStatus.Reused)
           + McpServers.Count(static server => server.Status == ImportItemStatus.Reused);
}

public sealed class ImportOutcome
{
    public List<Skill> AddedSkills { get; } = [];
    public List<McpServer> AddedMcpServers { get; } = [];
    public LumiAgent? AddedLumi { get; set; }
    public int ReusedCount { get; set; }

    /// <summary>The item the recipient most likely wants to open next (the Lumi, else the first skill or server).</summary>
    public SharedCapabilityKind PrimaryKind { get; set; }
    public Guid? PrimaryId { get; set; }
    public string PrimaryName { get; set; } = "";
}

/// <summary>
/// Plans and applies an import. Planning is pure: it resolves every name against the current library,
/// reuses identical skills and MCP servers instead of duplicating them, and numbers anything whose
/// name is taken by something different. Applying only ever adds: nothing existing is overwritten,
/// built-in flags are never imported, secrets are never imported (servers get their keys with empty
/// values) and every new MCP server arrives turned off, so an import cannot start a process.
/// </summary>
public static class CapabilityImporter
{
    private const int MaxNameLength = 120;
    private const int MaxDescriptionLength = 1000;

    public static ImportPlan Plan(CapabilityPack pack, DataStore store, bool? isWindows = null)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(store);

        var data = store.Data;
        var hidden = 0;
        string Clean(string? text)
        {
            var cleaned = HiddenText.Strip(text, out var removed);
            hidden += removed;
            return cleaned;
        }

        var skills = new List<PlannedSkill>();
        var plannedSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plannedSkillFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in pack.Skills)
        {
            var name = SafeName(Clean(source.Name), Clean(PackText.HumanizeName(source.Slug)));
            var description = SafeDescription(Clean(source.Description));
            var content = PackText.NormalizeNewlines(Clean(source.Content)).Trim();

            var existing = data.Skills.FirstOrDefault(skill =>
                skill.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && PackText.SameText(skill.Content, content));
            if (existing is not null)
            {
                skills.Add(new PlannedSkill(source, existing.Name, description, content, ImportItemStatus.Reused, existing.Id));
                continue;
            }

            var finalName = UniqueName(name, candidate =>
                data.Skills.Any(skill => skill.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                || store.SkillFileNameConflicts(candidate)
                || plannedSkillNames.Contains(candidate)
                || plannedSkillFiles.Contains(Path.GetFileName(store.GetSkillFilePath(candidate))));
            plannedSkillNames.Add(finalName);
            plannedSkillFiles.Add(Path.GetFileName(store.GetSkillFilePath(finalName)));
            skills.Add(new PlannedSkill(
                source,
                finalName,
                description,
                content,
                finalName.Equals(name, StringComparison.Ordinal) ? ImportItemStatus.New : ImportItemStatus.Renamed,
                null));
        }

        var windows = isWindows ?? OperatingSystem.IsWindows();
        var servers = new List<PlannedMcpServer>();
        var plannedServerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in pack.McpServers)
        {
            var name = SafeName(Clean(source.Name), "MCP server");
            var description = SafeDescription(Clean(source.Description));
            var command = source.IsRemote ? "" : CapabilityPackWriter.PlatformCommand(Clean(source.Command).Trim(), windows);
            var args = source.IsRemote ? [] : source.Args.Select(arg => Clean(arg)).ToList();
            var url = source.IsRemote ? Clean(source.Url).Trim() : "";

            var existing = data.McpServers.FirstOrDefault(server => SameDefinition(server, source.IsRemote, command, args, url));
            if (existing is not null)
            {
                servers.Add(new PlannedMcpServer(source, existing.Name, description, command, args, url, ImportItemStatus.Reused, existing.Id));
                continue;
            }

            var finalName = UniqueName(name, candidate =>
                data.McpServers.Any(server => server.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                || plannedServerNames.Contains(candidate));
            plannedServerNames.Add(finalName);
            servers.Add(new PlannedMcpServer(
                source,
                finalName,
                description,
                command,
                args,
                url,
                finalName.Equals(name, StringComparison.Ordinal) ? ImportItemStatus.New : ImportItemStatus.Renamed,
                null));
        }

        PlannedLumi? lumi = null;
        if (pack.Lumi is { } sharedLumi)
        {
            var name = SafeName(Clean(sharedLumi.Name), "Lumi");
            var finalName = UniqueName(name, candidate =>
                data.Agents.Any(agent => agent.Name.Equals(candidate, StringComparison.OrdinalIgnoreCase)));
            lumi = new PlannedLumi(
                sharedLumi,
                finalName,
                SafeDescription(Clean(sharedLumi.Description)),
                PackText.NormalizeNewlines(Clean(sharedLumi.SystemPrompt)).Trim(),
                finalName.Equals(name, StringComparison.Ordinal) ? ImportItemStatus.New : ImportItemStatus.Renamed);
        }

        return new ImportPlan
        {
            Pack = pack,
            Skills = skills,
            McpServers = servers,
            Lumi = lumi,
            HiddenCharactersRemoved = hidden
        };
    }

    /// <summary>
    /// Adds what <paramref name="plan"/> describes to the store's data. The caller persists and
    /// notifies; plan against the current data immediately before applying.
    /// </summary>
    public static ImportOutcome Apply(ImportPlan plan, DataStore store)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(store);

        var data = store.Data;
        var outcome = new ImportOutcome();
        var skillIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var skillNames = new Dictionary<Guid, string>();
        foreach (var planned in plan.Skills)
        {
            Guid id;
            if (planned.Status == ImportItemStatus.Reused && planned.ExistingId is { } existingId)
            {
                id = existingId;
                outcome.ReusedCount++;
            }
            else
            {
                var skill = new Skill
                {
                    Name = planned.Name,
                    Description = planned.Description,
                    Content = planned.Content,
                    IconGlyph = SafeGlyph(planned.Source.IconGlyph, "⚡")
                };
                data.Skills.Add(skill);
                outcome.AddedSkills.Add(skill);
                id = skill.Id;
            }

            skillIds.TryAdd(planned.Source.Slug, id);
            skillNames[id] = planned.Name;
        }

        var serverIds = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var serverNames = new Dictionary<Guid, string>();
        foreach (var planned in plan.McpServers)
        {
            Guid id;
            if (planned.Status == ImportItemStatus.Reused && planned.ExistingId is { } existingId)
            {
                id = existingId;
                outcome.ReusedCount++;
            }
            else
            {
                var source = planned.Source;
                var server = new McpServer
                {
                    Name = planned.Name,
                    Description = planned.Description,
                    ServerType = source.IsRemote ? "remote" : "local",
                    Command = planned.Command,
                    Args = planned.Args.ToList(),
                    Url = planned.Url,
                    Env = source.IsRemote ? [] : EmptyValues(source.EnvKeys),
                    Headers = source.IsRemote ? EmptyValues(source.HeaderKeys) : [],
                    Tools = source.Tools.Where(static tool => !string.IsNullOrWhiteSpace(tool)).ToList(),
                    Timeout = source.Timeout is > 0 ? source.Timeout : null,
                    IsEnabled = false
                };
                data.McpServers.Add(server);
                outcome.AddedMcpServers.Add(server);
                id = server.Id;
            }

            serverIds.TryAdd(planned.Source.Name, id);
            serverNames[id] = planned.Name;
        }

        if (plan.Lumi is { } plannedLumi)
        {
            var source = plannedLumi.Source;
            var agent = new LumiAgent
            {
                Name = plannedLumi.Name,
                Description = plannedLumi.Description,
                SystemPrompt = plannedLumi.SystemPrompt,
                IconGlyph = SafeGlyph(source.IconGlyph, "✦"),
                SkillIds = source.SkillSlugs
                    .Select(slug => skillIds.TryGetValue(slug, out var id) ? id : (Guid?)null)
                    .OfType<Guid>()
                    .Distinct()
                    .ToList(),
                McpServerIds = source.McpServerNames
                    .Select(name => serverIds.TryGetValue(name, out var id) ? id : (Guid?)null)
                    .OfType<Guid>()
                    .Distinct()
                    .ToList(),
                ToolNames = source.ToolNames?.ToList() ?? [],
                HasExplicitToolSelection = source.RestrictsTools
            };
            data.Agents.Add(agent);
            outcome.AddedLumi = agent;
            outcome.PrimaryKind = SharedCapabilityKind.Lumi;
            outcome.PrimaryId = agent.Id;
            outcome.PrimaryName = agent.Name;
        }
        else if (plan.Pack.Kind == SharedCapabilityKind.McpServer && serverNames.Count > 0)
        {
            var first = serverIds[plan.McpServers[0].Source.Name];
            outcome.PrimaryKind = SharedCapabilityKind.McpServer;
            outcome.PrimaryId = first;
            outcome.PrimaryName = serverNames[first];
        }
        else if (skillNames.Count > 0)
        {
            var first = skillIds[plan.Skills[0].Source.Slug];
            outcome.PrimaryKind = SharedCapabilityKind.Skill;
            outcome.PrimaryId = first;
            outcome.PrimaryName = skillNames[first];
        }

        return outcome;
    }

    private static bool SameDefinition(McpServer existing, bool isRemote, string command, IReadOnlyList<string> args, string url)
    {
        var existingIsRemote = string.Equals(existing.ServerType, "remote", StringComparison.OrdinalIgnoreCase);
        if (existingIsRemote != isRemote)
            return false;

        if (isRemote)
            return string.Equals(existing.Url.Trim().TrimEnd('/'), url.TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

        return string.Equals(
                   CapabilityPackWriter.PortableCommand(existing.Command.Trim()),
                   CapabilityPackWriter.PortableCommand(command),
                   StringComparison.OrdinalIgnoreCase)
               && existing.Args.SequenceEqual(args, StringComparer.Ordinal);
    }

    private static Dictionary<string, string> EmptyValues(IEnumerable<string> keys)
    {
        var values = new Dictionary<string, string>();
        foreach (var key in keys)
        {
            var cleaned = PackText.SingleLine(HiddenText.Strip(key, out _));
            if (cleaned.Length > 0 && !values.ContainsKey(cleaned))
                values[cleaned] = "";
        }

        return values;
    }

    /// <summary>A single-line, bounded name that cannot corrupt the skill mirror's front matter.</summary>
    private static string SafeName(string name, string fallback)
    {
        var single = PackText.SingleLine(name, MaxNameLength).TrimStart('-').Trim();
        if (single.Length > 0)
            return single;

        var fallbackName = PackText.SingleLine(fallback, MaxNameLength).TrimStart('-').Trim();
        return fallbackName.Length > 0 ? fallbackName : "Imported";
    }

    /// <summary>Single-line and never starting with a front matter delimiter, like every Lumi description.</summary>
    private static string SafeDescription(string description)
    {
        var single = PackText.SingleLine(description, MaxDescriptionLength);
        return single.StartsWith("---", StringComparison.Ordinal) ? single.TrimStart('-').Trim() : single;
    }

    private static string SafeGlyph(string? glyph, string fallback)
    {
        var cleaned = PackText.SingleLine(HiddenText.Strip(glyph, out _));
        return cleaned.Length is 0 or > 16 ? fallback : cleaned;
    }

    private static string UniqueName(string name, Func<string, bool> isTaken)
    {
        if (!isTaken(name))
            return name;

        for (var suffix = 2; ; suffix++)
        {
            var candidate = name + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
            if (!isTaken(candidate))
                return candidate;
        }
    }
}
