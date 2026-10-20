using System.Globalization;
using System.Text;

namespace Lumi.Services.Sharing;

/// <summary>An agent tool on this computer that reads Agent Skills from a personal skills folder.</summary>
public sealed record SkillToolTarget(string DisplayName, string SkillsDirectory);

public enum SkillTargetState
{
    /// <summary>Nothing of this skill's is there yet; installing creates a new folder.</summary>
    NotInstalled,

    /// <summary>The folder already holds this exact skill.</summary>
    UpToDate,

    /// <summary>The folder holds an earlier copy Lumi installed for this same skill; installing updates it.</summary>
    Different
}

/// <summary>Where a skill would land in a skills folder, and what is there now.</summary>
public sealed record SkillInstallPlan(string Slug, string FilePath, SkillTargetState State);

/// <summary>
/// "Send to Claude Code": installs a skill where another agent tool will pick it up, using the
/// canonical Agent Skills layout (<c>&lt;skills folder&gt;/&lt;name&gt;/SKILL.md</c>). A tool is offered
/// only when its home folder exists, which means it has been used on this computer.
///
/// <para>Installing never replaces someone else's skill: an existing folder is only updated when its
/// SKILL.md is a copy Lumi wrote for this same skill (its <c>metadata.lumi-name</c> matches). Any other
/// skill with the same folder name is left alone and this one is installed as <c>name-2</c>.</para>
///
/// <para>GitHub Copilot's personal folders (<c>~/.copilot/skills</c>, <c>~/.agents/skills</c>) are
/// deliberately not offered: Lumi's own Copilot runtime reads them, so the skill would appear in Lumi
/// twice under two names.</para>
/// </summary>
public static class SkillToolTargets
{
    private const int MaxAttempts = 100;

    private static readonly (string DisplayName, string Folder, string? HomeVariable)[] KnownTools =
    [
        ("Claude Code", ".claude", "CLAUDE_CONFIG_DIR"),
        ("Codex", ".codex", "CODEX_HOME"),
        ("Gemini CLI", ".gemini", null),
        ("Cursor", ".cursor", null)
    ];

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static IReadOnlyList<SkillToolTarget> Discover(
        string? homeDirectory = null,
        Func<string, string?>? readEnvironment = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        readEnvironment ??= Environment.GetEnvironmentVariable;
        var targets = new List<SkillToolTarget>();
        if (string.IsNullOrWhiteSpace(home))
            return targets;

        foreach (var (displayName, folder, homeVariable) in KnownTools)
        {
            var configured = homeVariable is null ? null : readEnvironment(homeVariable);
            var root = string.IsNullOrWhiteSpace(configured) ? Path.Combine(home, folder) : configured.Trim();
            try
            {
                if (Directory.Exists(root))
                    targets.Add(new SkillToolTarget(displayName, Path.Combine(root, "skills")));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // An unreadable tool folder is simply not offered.
            }
        }

        return targets;
    }

    /// <summary>
    /// Finds the folder this skill belongs in: its own earlier copy if there is one, otherwise the first
    /// free <c>name</c>, <c>name-2</c>, … that does not belong to a different skill.
    /// </summary>
    public static SkillInstallPlan PlanInstall(string skillsDirectory, SharedSkill skill)
    {
        var baseSlug = CapabilityPackWriter.SkillSlug(skill.Slug);
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var slug = attempt == 1 ? baseSlug : WithSuffix(baseSlug, attempt);
            var folder = Path.Combine(skillsDirectory, slug);
            var filePath = Path.Combine(folder, CapabilityPackWriter.SkillFileName);

            string? existing;
            try
            {
                // A folder is free when it does not exist yet or is empty (for example one just made
                // in the folder picker); a folder with other content belongs to something else.
                if (!File.Exists(filePath) && (!Directory.Exists(folder) || !Directory.EnumerateFileSystemEntries(folder).Any()))
                    return new SkillInstallPlan(slug, filePath, SkillTargetState.NotInstalled);

                existing = File.Exists(filePath) ? File.ReadAllText(filePath) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                existing = null;
            }

            if (existing is not null && IsCopyOf(existing, skill))
            {
                var markdown = CapabilityPackWriter.WriteSkillMarkdown(skill with { Slug = slug });
                return new SkillInstallPlan(
                    slug,
                    filePath,
                    PackText.SameText(existing, markdown) ? SkillTargetState.UpToDate : SkillTargetState.Different);
            }
        }

        throw new IOException("Too many skills with this name already exist in " + skillsDirectory + ".");
    }

    /// <summary>Writes the skill where <see cref="PlanInstall"/> puts it, atomically, and returns that plan.</summary>
    public static SkillInstallPlan Install(string skillsDirectory, SharedSkill skill)
    {
        var plan = PlanInstall(skillsDirectory, skill);
        var markdown = CapabilityPackWriter.WriteSkillMarkdown(skill with { Slug = plan.Slug });
        var directory = Path.GetDirectoryName(plan.FilePath)!;
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, "." + CapabilityPackWriter.SkillFileName + "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            File.WriteAllText(temporary, markdown, Utf8NoBom);
            File.Move(temporary, plan.FilePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }

        return plan with { State = SkillTargetState.UpToDate };
    }

    /// <summary>
    /// A SKILL.md Lumi wrote for this skill carries the skill's display name in its metadata. Another
    /// tool's file is read defensively: anything unreadable simply is not ours.
    /// </summary>
    private static bool IsCopyOf(string existing, SharedSkill skill)
    {
        try
        {
            return Frontmatter.TryParse(existing, out var frontmatter, out _, out _)
                   && frontmatter.GetMap("metadata")?.GetValueOrDefault("lumi-name") is { } lumiName
                   && string.Equals(PackText.SingleLine(lumiName), PackText.SingleLine(skill.Name), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }
    }

    private static string WithSuffix(string slug, int attempt)
    {
        var suffix = "-" + attempt.ToString(CultureInfo.InvariantCulture);
        var stem = slug.Length + suffix.Length > 64 ? slug[..(64 - suffix.Length)].TrimEnd('-') : slug;
        return stem + suffix;
    }
}
