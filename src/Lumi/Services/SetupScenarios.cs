using Lumi.Localization;

namespace Lumi.Services;

/// <summary>The official plugin that sets a scenario up, and other names it is published under.</summary>
public sealed record SetupPlugin(string Marketplace, string Name, IReadOnlyList<string> Aliases)
{
    public bool Matches(string? pluginName)
        => !string.IsNullOrWhiteSpace(pluginName)
           && (pluginName.Equals(Name, StringComparison.OrdinalIgnoreCase)
               || Aliases.Contains(pluginName, StringComparer.OrdinalIgnoreCase));
}

/// <summary>One shape of a scenario's mark, in its own colour.</summary>
public sealed record SetupMarkShape(string Geometry, string Color);

/// <summary>
/// A well-known way of working that Lumi recognises on this computer and sets up in one step with an
/// official plugin. Text is looked up by key, so it follows the interface language.
/// </summary>
/// <param name="AppGroups">Matches when every group names an app the user has.</param>
/// <param name="Sites">Matches a visited host; an entry that starts with a dot matches its subdomains.</param>
public sealed record SetupScenario(
    string Id,
    string NameKey,
    string DescriptionKey,
    string PromptKey,
    SetupPlugin Plugin,
    IReadOnlyList<IReadOnlyList<string>> AppGroups,
    IReadOnlyList<string> Sites,
    string SearchTerms,
    IReadOnlyList<SetupMarkShape> Mark)
{
    public bool Matches(IEnumerable<string> apps, IEnumerable<string> sites)
    {
        if (sites.Any(IsScenarioSite))
            return true;

        var known = apps.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return AppGroups.Count > 0 && AppGroups.All(group => group.Any(known.Contains));
    }

    private bool IsScenarioSite(string site)
    {
        if (!Uri.TryCreate(site.Contains("://", StringComparison.Ordinal) ? site : "https://" + site,
                UriKind.Absolute, out var uri))
            return false;

        var host = uri.IdnHost;
        return Sites.Any(candidate => candidate.StartsWith('.')
            ? host.EndsWith(candidate, StringComparison.OrdinalIgnoreCase)
            : host.Equals(candidate, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>The setups Lumi can suggest. Adding a scenario here is all a new suggestion needs.</summary>
public static class SetupScenarios
{
    public static IReadOnlyList<SetupScenario> All { get; } =
    [
        // Microsoft's official Work IQ plugin: a hosted Microsoft 365 connector and Microsoft-maintained skills.
        new(
            Id: "microsoft-365",
            NameKey: nameof(Loc.Setup_Microsoft365_Name),
            DescriptionKey: nameof(Loc.Setup_Microsoft365_Description),
            PromptKey: nameof(Loc.Setup_Microsoft365_Prompt),
            Plugin: new SetupPlugin("microsoft/work-iq", "workiq", ["workiq-preview"]),
            AppGroups:
            [
                ["Microsoft 365", "Microsoft Office", "Word", "Excel", "PowerPoint", "Outlook", "OneNote"],
                ["Teams"],
            ],
            Sites: [".sharepoint.com", "outlook.office.com", "outlook.office365.com", "m365.cloud.microsoft"],
            SearchTerms: "M365 Microsoft365 Office 365 Outlook Teams SharePoint OneDrive Work IQ WorkIQ calendar email meetings",
            Mark:
            [
                new("M0 0h10v10H0z", "#F25022"),
                new("M11 0h10v10H11z", "#7FBA00"),
                new("M0 11h10v10H0z", "#00A4EF"),
                new("M11 11h10v10H11z", "#FFB900"),
            ]),
    ];
}
