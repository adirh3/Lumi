using System.Diagnostics;

namespace Lumi.Services.Capabilities;

internal static class PersonalSkillFolders
{
    public static List<string> GetDefaults(
        string? homeDirectory = null,
        Func<string, string?>? readEnvironment = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        readEnvironment ??= Environment.GetEnvironmentVariable;

        string Root(string variable, string fallback)
            => readEnvironment(variable) is { } value && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : fallback;

        var configHome = Root("XDG_CONFIG_HOME", Path.Combine(home, ".config"));
        List<string> folders =
        [
            Path.Combine(Root("CLAUDE_CONFIG_DIR", Path.Combine(home, ".claude")), "skills"),
            Path.Combine(Root("CODEX_HOME", Path.Combine(home, ".codex")), "skills"),
            Path.Combine(home, ".cursor", "skills"),
            Path.Combine(Root("OPENCODE_CONFIG_DIR", Path.Combine(configHome, "opencode")), "skills"),
        ];
        return folders.Distinct(CapabilityQuery.DirectoryComparer).ToList();
    }

    public static bool TryNormalize(string? path, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path.Trim()))
            return false;

        try
        {
            normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public static IReadOnlyList<string> GetExistingDirectories(IEnumerable<string> folders)
    {
        var result = new HashSet<string>(CapabilityQuery.DirectoryComparer);
        foreach (var folder in folders)
        {
            if (!TryNormalize(folder, out var normalized))
            {
                Debug.WriteLine($"[Skills] Ignoring invalid personal skill folder: {folder}");
                continue;
            }

            if (Directory.Exists(normalized))
                result.Add(normalized);
        }
        return result.ToArray();
    }
}
