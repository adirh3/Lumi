using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Lumi.Services.Discover;

/// <summary>
/// People-facing names for plugin slugs ("ai-ready" → "AI Ready") and version comparison for
/// update hints. Plugin names are identifiers; Discover shows them the way their makers write them.
/// </summary>
public static class DisplayNames
{
    private static readonly HashSet<string> Acronyms = new(StringComparer.OrdinalIgnoreCase)
    {
        "ai", "api", "apis", "aws", "bd", "cd", "ci", "cli", "css", "csv", "db", "gcp", "gh", "html", "http",
        "iac", "id", "ios", "json", "k8s", "llm", "mcp", "pdf", "pr", "qa", "rag", "sdk", "seo", "sql",
        "sre", "ts", "twg", "ui", "url", "ux", "vm", "xml", "yaml",
    };

    private static readonly Dictionary<string, string> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["github"] = "GitHub",
        ["devops"] = "DevOps",
        ["dotnet"] = ".NET",
        ["openapi"] = "OpenAPI",
        ["nodejs"] = "Node.js",
        ["nestjs"] = "NestJS",
        ["javascript"] = "JavaScript",
        ["typescript"] = "TypeScript",
        ["powershell"] = "PowerShell",
        ["sharepoint"] = "SharePoint",
        ["onedrive"] = "OneDrive",
        ["postgresql"] = "PostgreSQL",
        ["mysql"] = "MySQL",
        ["mongodb"] = "MongoDB",
        ["graphql"] = "GraphQL",
        ["fastapi"] = "FastAPI",
        ["workiq"] = "Work IQ",
        ["vscode"] = "VS Code",
        ["macos"] = "macOS",
        ["ios"] = "iOS",
        ["winui"] = "WinUI",
        ["wpf"] = "WPF",
        ["agentrc"] = "AgentRC",
    };

    /// <summary>Title-cases a slug, keeping acronyms and brand spellings; names already written for people stay as they are.</summary>
    public static string Humanize(string? name)
    {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0 || trimmed.Any(char.IsUpper) || trimmed.Any(char.IsWhiteSpace))
            return trimmed;

        var words = trimmed.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0)
            return trimmed;

        return string.Join(' ', words.Select(static word =>
            Spellings.TryGetValue(word, out var spelling) ? spelling
            : Acronyms.Contains(word) ? word.ToUpperInvariant()
            : char.ToUpperInvariant(word[0]) + word[1..]));
    }

    /// <summary>
    /// True only when <paramref name="candidate"/> is a strictly newer version. A catalog can lag
    /// behind the plugin it lists, so anything not provably newer is not offered as an update.
    /// </summary>
    public static bool IsNewerVersion(string? candidate, string? current)
    {
        if (!TryParse(candidate, out var next, out var nextPre) || !TryParse(current, out var installed, out var installedPre))
            return false;

        for (var i = 0; i < Math.Max(next.Length, installed.Length); i++)
        {
            var left = i < next.Length ? next[i] : 0;
            var right = i < installed.Length ? installed[i] : 0;
            if (left != right)
                return left > right;
        }

        // 1.2.0 is newer than 1.2.0-beta; two pre-releases of one version are not compared.
        return nextPre is null && installedPre is not null;
    }

    private static bool TryParse(string? version, out int[] parts, out string? prerelease)
    {
        parts = [];
        prerelease = null;
        if (string.IsNullOrWhiteSpace(version))
            return false;

        var text = version.Trim().TrimStart('v', 'V');
        var dash = text.IndexOfAny(['-', '+']);
        if (dash >= 0)
        {
            prerelease = text[dash..];
            text = text[..dash];
        }

        var segments = text.Split('.');
        var numbers = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            if (!int.TryParse(segments[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]))
                return false;
        }

        parts = numbers;
        return numbers.Length > 0;
    }
}
