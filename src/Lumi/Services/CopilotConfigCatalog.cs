using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Lumi.Services;

public sealed record CopilotSkillDefinition(
    string Name,
    string Description,
    string Content,
    string FilePath);

public sealed record CopilotAgentDefinition(
    string Name,
    string Description,
    string Content,
    string FilePath);

public sealed class CopilotCatalogSnapshot
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;
    private readonly IReadOnlyDictionary<string, CopilotSkillDefinition> _skillsByName;
    private readonly IReadOnlyDictionary<string, CopilotAgentDefinition> _agentsByName;

    public CopilotCatalogSnapshot(
        IReadOnlyList<CopilotSkillDefinition> skills,
        IReadOnlyList<CopilotAgentDefinition> agents)
    {
        Skills = skills;
        Agents = agents;
        _skillsByName = skills.ToDictionary(static skill => skill.Name, NameComparer);
        _agentsByName = agents.ToDictionary(static agent => agent.Name, NameComparer);
    }

    public IReadOnlyList<CopilotSkillDefinition> Skills { get; }

    public IReadOnlyList<CopilotAgentDefinition> Agents { get; }

    public CopilotSkillDefinition? FindSkill(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _skillsByName.TryGetValue(name, out var skill) ? skill : null;
    }

    public CopilotAgentDefinition? FindAgent(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return null;

        return _agentsByName.TryGetValue(name, out var agent) ? agent : null;
    }
}

public static class CopilotConfigCatalog
{
    private static readonly StringComparer NameComparer = StringComparer.OrdinalIgnoreCase;

    public static CopilotCatalogSnapshot Discover(string workDir, string? copilotRootOverride = null)
        => Discover([workDir], copilotRootOverride);

    public static CopilotCatalogSnapshot Discover(IReadOnlyList<string> workDirs, string? copilotRootOverride = null)
    {
        var sources = GetCatalogSources(workDirs, copilotRootOverride ?? GetCopilotRoot());
        var skills = DiscoverDefinitions(
            sources,
            static source => source.SkillDirectories,
            "SKILL.md",
            false,
            LoadSkillDefinition,
            static skill => skill.Name);
        var agents = DiscoverDefinitions(
            sources,
            static source => source.AgentDirectories,
            "AGENT.md",
            true,
            LoadAgentDefinition,
            static agent => agent.Name);

        return new CopilotCatalogSnapshot(skills, agents);
    }

    public static IReadOnlyList<CopilotSkillDefinition> DiscoverSkills(string workDir, string? copilotRootOverride = null)
        => Discover(workDir, copilotRootOverride).Skills;

    public static IReadOnlyList<CopilotSkillDefinition> DiscoverSkills(IReadOnlyList<string> workDirs, string? copilotRootOverride = null)
        => Discover(workDirs, copilotRootOverride).Skills;

    public static IReadOnlyList<CopilotAgentDefinition> DiscoverAgents(string workDir, string? copilotRootOverride = null)
        => Discover(workDir, copilotRootOverride).Agents;

    public static IReadOnlyList<CopilotAgentDefinition> DiscoverAgents(IReadOnlyList<string> workDirs, string? copilotRootOverride = null)
        => Discover(workDirs, copilotRootOverride).Agents;

    public static CopilotSkillDefinition? FindSkill(string workDir, string name, string? copilotRootOverride = null)
        => Discover(workDir, copilotRootOverride).FindSkill(name);

    public static CopilotAgentDefinition? FindAgent(string workDir, string name, string? copilotRootOverride = null)
        => Discover(workDir, copilotRootOverride).FindAgent(name);

    /// <summary>
    /// Returns the workspace-level skill root directories (<c>&lt;workDir&gt;/.github/skills</c>)
    /// that exist for the given working directories. These roots each contain
    /// <c>&lt;name&gt;/SKILL.md</c> skill folders and are exactly what the native Copilot skill
    /// discovery accepts as additional skill directories. The CLI's built-in discovery anchors
    /// skill lookup at the project/git root, so skills living under a subfolder <c>.github</c>
    /// (e.g. a monorepo app) are invisible to it; surfacing these roots through
    /// <c>config.SkillDirectories</c> lets such skills load through the always-present native
    /// skill tool.
    /// </summary>
    public static IReadOnlyList<string> GetWorkspaceSkillDirectories(IReadOnlyList<string> workDirs)
    {
        var directories = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var workDir in NormalizeWorkDirectories(workDirs))
        {
            var githubDir = GetGitHubDirectory(workDir);
            if (githubDir is null)
                continue;

            foreach (var skillDir in GetExistingDirectories(githubDir, "skills"))
            {
                if (seen.Add(skillDir))
                    directories.Add(skillDir);
            }
        }

        return directories;
    }

    private static IReadOnlyList<TDefinition> DiscoverDefinitions<TDefinition>(
        IReadOnlyList<CopilotCatalogSource> sources,
        Func<CopilotCatalogSource, IReadOnlyList<string>> selectDirectories,
        string nestedFileName,
        bool includeLooseMarkdownFiles,
        Func<string, string, TDefinition?> loadDefinition,
        Func<TDefinition, string> getName)
        where TDefinition : class
    {
        var definitions = new Dictionary<string, TDefinition>(NameComparer);
        foreach (var source in sources)
        {
            foreach (var directory in selectDirectories(source))
                AddMarkdownDirectory(directory, nestedFileName, includeLooseMarkdownFiles, loadDefinition, getName, definitions);
        }

        return definitions.Values
            .OrderBy(getName, NameComparer)
            .ToList();
    }

    private static IReadOnlyList<CopilotCatalogSource> GetCatalogSources(IReadOnlyList<string> workDirs, string? copilotRoot)
    {
        var sources = new List<CopilotCatalogSource>();

        foreach (var workDir in NormalizeWorkDirectories(workDirs))
        {
            var githubDir = GetGitHubDirectory(workDir);
            if (githubDir is not null)
            {
                AddCatalogSource(
                    sources,
                    GetExistingDirectories(githubDir, "skills"),
                    GetExistingDirectories(githubDir, "agents"));
            }
        }

        if (string.IsNullOrWhiteSpace(copilotRoot) || !Directory.Exists(copilotRoot))
            return sources;

        AddCatalogSource(
            sources,
            GetExistingDirectories(copilotRoot, "skills"),
            GetExistingDirectories(copilotRoot, "agents"));

        var latestPackageDir = GetLatestUniversalPackageDir(copilotRoot);
        if (latestPackageDir is null)
            return sources;

        AddCatalogSource(
            sources,
            GetExistingDirectories(latestPackageDir, "builtin-skills"),
            GetExistingDirectories(latestPackageDir, "builtin-agents", "agents"));
        return sources;
    }

    private static IReadOnlyList<string> NormalizeWorkDirectories(IReadOnlyList<string> workDirs)
    {
        var directories = new List<string>();
        foreach (var workDir in workDirs)
        {
            if (string.IsNullOrWhiteSpace(workDir) || !Directory.Exists(workDir))
                continue;

            if (directories.Any(existing => string.Equals(existing, workDir, StringComparison.OrdinalIgnoreCase)))
                continue;

            directories.Add(workDir);
        }

        return directories;
    }

    private static void AddCatalogSource(
        List<CopilotCatalogSource> sources,
        IReadOnlyList<string> skillDirectories,
        IReadOnlyList<string> agentDirectories)
    {
        if (skillDirectories.Count == 0 && agentDirectories.Count == 0)
            return;

        sources.Add(new CopilotCatalogSource(skillDirectories, agentDirectories));
    }

    private static IReadOnlyList<string> GetExistingDirectories(string parentDir, params string[] names)
    {
        var directories = new List<string>(names.Length);
        foreach (var name in names)
        {
            var directory = FindSubdirectory(parentDir, name);
            if (string.IsNullOrWhiteSpace(directory))
                continue;

            if (directories.Any(existing => string.Equals(existing, directory, StringComparison.OrdinalIgnoreCase)))
                continue;

            directories.Add(directory);
        }

        return directories;
    }

    private static void AddMarkdownDirectory<TDefinition>(
        string? directory,
        string nestedFileName,
        bool includeLooseMarkdownFiles,
        Func<string, string, TDefinition?> loadDefinition,
        Func<TDefinition, string> getName,
        Dictionary<string, TDefinition> definitions)
        where TDefinition : class
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return;

        if (includeLooseMarkdownFiles)
        {
            foreach (var file in Directory.GetFiles(directory, "*.md"))
                AddMarkdownFile(file, Path.GetFileNameWithoutExtension(file), loadDefinition, getName, definitions);
        }

        foreach (var nestedDirectory in Directory.GetDirectories(directory))
        {
            var nestedFile = FindFile(nestedDirectory, nestedFileName);
            if (nestedFile is not null)
                AddMarkdownFile(nestedFile, Path.GetFileName(nestedDirectory), loadDefinition, getName, definitions);
        }
    }

    private static void AddMarkdownFile<TDefinition>(
        string filePath,
        string fallbackName,
        Func<string, string, TDefinition?> loadDefinition,
        Func<TDefinition, string> getName,
        Dictionary<string, TDefinition> definitions)
        where TDefinition : class
    {
        var definition = loadDefinition(filePath, fallbackName);
        if (definition is null)
            return;

        var name = getName(definition);
        if (definitions.ContainsKey(name))
            return;

        definitions[name] = definition;
    }

    private static CopilotSkillDefinition? LoadSkillDefinition(string filePath, string fallbackName)
    {
        var asset = LoadMarkdownAsset(filePath, fallbackName);
        return asset is null
            ? null
            : new CopilotSkillDefinition(asset.Name, asset.Description, asset.Content, filePath);
    }

    private static CopilotAgentDefinition? LoadAgentDefinition(string filePath, string fallbackName)
    {
        var asset = LoadMarkdownAsset(filePath, fallbackName);
        return asset is null
            ? null
            : new CopilotAgentDefinition(asset.Name, asset.Description, asset.Content, filePath);
    }

    private static ParsedMarkdownAsset? LoadMarkdownAsset(string filePath, string fallbackName)
    {
        try
        {
            var content = File.ReadAllText(filePath);
            return ParseMarkdownAsset(content, fallbackName);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ParsedMarkdownAsset ParseMarkdownAsset(string content, string fallbackName)
    {
        var normalized = content.Replace("\r\n", "\n");
        if (!normalized.StartsWith("---\n", StringComparison.Ordinal))
            return new ParsedMarkdownAsset(fallbackName, string.Empty, content.Trim());

        var endOfFrontMatter = normalized.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        if (endOfFrontMatter < 0)
            return new ParsedMarkdownAsset(fallbackName, string.Empty, content.Trim());

        var frontMatterLines = normalized.Substring(4, endOfFrontMatter - 4).Split('\n');
        var body = normalized[(endOfFrontMatter + 5)..].Trim();
        var name = fallbackName;
        var description = string.Empty;

        for (var i = 0; i < frontMatterLines.Length; i++)
        {
            var rawLine = frontMatterLines[i];
            if (string.IsNullOrWhiteSpace(rawLine) || char.IsWhiteSpace(rawLine[0]))
                continue;

            var colonIndex = rawLine.IndexOf(':');
            if (colonIndex <= 0)
                continue;

            var key = rawLine[..colonIndex].Trim();
            var value = rawLine[(colonIndex + 1)..].Trim();

            if (key.Equals("name", StringComparison.OrdinalIgnoreCase))
            {
                name = ParseScalar(value) ?? fallbackName;
                continue;
            }

            if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
                description = ParseFrontMatterValue(frontMatterLines, ref i, value);
        }

        return new ParsedMarkdownAsset(name, description, body);
    }

    private static string ParseFrontMatterValue(string[] lines, ref int index, string value)
    {
        if (value is ">" or ">-" or "|" or "|-")
        {
            var literal = value.StartsWith('|');
            var buffer = new List<string>();
            while (index + 1 < lines.Length && IsIndented(lines[index + 1]))
                buffer.Add(lines[++index].Trim());

            return literal
                ? string.Join(Environment.NewLine, buffer)
                : string.Join(" ", buffer.Where(static line => !string.IsNullOrWhiteSpace(line)));
        }

        return ParseScalar(value) ?? string.Empty;
    }

    private static bool IsIndented(string line)
        => !string.IsNullOrEmpty(line) && char.IsWhiteSpace(line[0]);

    private static string? ParseScalar(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if ((trimmed.StartsWith('"') && trimmed.EndsWith('"'))
            || (trimmed.StartsWith('\'') && trimmed.EndsWith('\'')))
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed;
    }

    private static string? GetGitHubDirectory(string workDir)
    {
        if (string.IsNullOrWhiteSpace(workDir))
            return null;

        var githubDir = Path.Combine(workDir, ".github");
        return Directory.Exists(githubDir) ? githubDir : null;
    }

    private static string GetCopilotRoot()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".copilot");

    private static string? GetLatestUniversalPackageDir(string copilotRoot)
    {
        var universalDir = Path.Combine(copilotRoot, "pkg", "universal");
        if (!Directory.Exists(universalDir))
            return null;

        return Directory.GetDirectories(universalDir)
            .OrderByDescending(static dir => ParseVersionOrDefault(Path.GetFileName(dir)))
            .ThenByDescending(static dir => Path.GetFileName(dir), NameComparer)
            .FirstOrDefault();
    }

    private static Version ParseVersionOrDefault(string? value)
    {
        if (Version.TryParse(value, out var version))
            return version;

        if (string.IsNullOrWhiteSpace(value))
            return new Version(0, 0);

        var separatorIndex = value.IndexOf('-');
        if (separatorIndex < 0)
            return new Version(0, 0);

        // Copilot package folders may append a numeric revision suffix, for example 1.0.35-6.
        var baseVersionText = value[..separatorIndex];
        var revisionText = value[(separatorIndex + 1)..];
        if (!Version.TryParse(baseVersionText, out var baseVersion)
            || !int.TryParse(revisionText, out var revision))
        {
            return new Version(0, 0);
        }

        var build = baseVersion.Build >= 0 ? baseVersion.Build : 0;
        return new Version(baseVersion.Major, baseVersion.Minor, build, revision);
    }

    private static string? FindSubdirectory(string parentDir, string name)
    {
        var exact = Path.Combine(parentDir, name);
        if (Directory.Exists(exact))
            return exact;

        foreach (var directory in Directory.GetDirectories(parentDir))
        {
            if (string.Equals(Path.GetFileName(directory), name, StringComparison.OrdinalIgnoreCase))
                return directory;
        }

        return null;
    }

    private static string? FindFile(string parentDir, string name)
    {
        var exact = Path.Combine(parentDir, name);
        if (File.Exists(exact))
            return exact;

        foreach (var file in Directory.GetFiles(parentDir))
        {
            if (string.Equals(Path.GetFileName(file), name, StringComparison.OrdinalIgnoreCase))
                return file;
        }

        return null;
    }

    private sealed record CopilotCatalogSource(
        IReadOnlyList<string> SkillDirectories,
        IReadOnlyList<string> AgentDirectories);

    private sealed record ParsedMarkdownAsset(string Name, string Description, string Content);
}
