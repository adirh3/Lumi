using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.Services.WhatsNew;

/// <summary>What one presentation of What's new shows: a spotlight and the other items, newest release first.</summary>
public sealed record WhatsNewSelection(Version? Version, WhatsNewItem? Spotlight, IReadOnlyList<WhatsNewItem> Highlights)
{
    public bool IsEmpty => Spotlight is null && Highlights.Count == 0;
}

/// <summary>
/// When What's new shows and what it shows. The profile remembers the newest version whose What's new the user saw
/// (<see cref="UserSettings.LastSeenWhatsNewVersion"/>), so an update shows what came out since then, however many
/// versions it skipped.
/// </summary>
public static class WhatsNewSchedule
{
    /// <summary>
    /// Where profiles from before What's new count from: they updated from 1.0.0 or 1.0.1, which recorded no
    /// version, so they see everything since 1.0.
    /// </summary>
    public static readonly Version Baseline = new(1, 0, 0);

    /// <summary>How many items besides the spotlight one presentation shows; older ones stay in the release notes.</summary>
    public const int MaxHighlights = 8;

    /// <summary>The version of this build of Lumi, as major.minor.patch.</summary>
    public static Version CurrentVersion { get; } = Normalize(typeof(WhatsNewSchedule).Assembly.GetName().Version);

    public static WhatsNewPlatforms CurrentPlatform =>
        OperatingSystem.IsWindows() ? WhatsNewPlatforms.Windows
        : OperatingSystem.IsMacOS() ? WhatsNewPlatforms.MacOS
        : WhatsNewPlatforms.Linux;

    /// <summary>
    /// Decides as Lumi starts whether this version shows What's new: the version it counts from, or null. A profile
    /// still in the welcome starts at <paramref name="current"/>, as the welcome is its introduction to Lumi, and an
    /// update that brings nothing to show counts as seen, so the next one counts from it. Neither is saved here.
    /// </summary>
    public static Version? PendingSince(
        UserSettings settings,
        Version current,
        IReadOnlyList<WhatsNewRelease> releases,
        WhatsNewPlatforms platform)
    {
        if (!settings.IsOnboarded)
        {
            settings.LastSeenWhatsNewVersion = Format(current);
            return null;
        }

        var since = Parse(settings.LastSeenWhatsNewVersion) ?? Baseline;
        if (since >= current)
            return null;

        if (Select(releases, since, current, platform).IsEmpty)
        {
            settings.LastSeenWhatsNewVersion = Format(current);
            return null;
        }

        return since;
    }

    /// <summary>Remembers that the user has seen What's new up to <paramref name="version"/>; it never moves back.</summary>
    public static void MarkSeen(UserSettings settings, Version version)
    {
        if (Parse(settings.LastSeenWhatsNewVersion) is { } seen && seen >= version)
            return;
        settings.LastSeenWhatsNewVersion = Format(version);
    }

    /// <summary>
    /// What an update from <paramref name="since"/> to <paramref name="upTo"/> shows on <paramref name="platform"/>:
    /// the newest release's spotlight (an older one shows as an ordinary item) and the rest, newest release first, up
    /// to <see cref="MaxHighlights"/>. <see cref="WhatsNewSelection.Version"/> is the newest release with something.
    /// </summary>
    public static WhatsNewSelection Select(
        IReadOnlyList<WhatsNewRelease> releases,
        Version since,
        Version upTo,
        WhatsNewPlatforms platform)
    {
        WhatsNewItem? spotlight = null;
        Version? newest = null;
        var highlights = new List<WhatsNewItem>();
        foreach (var release in releases
                     .Where(release => release.Version > since && release.Version <= upTo)
                     .OrderByDescending(release => release.Version))
        {
            foreach (var item in release.Items)
            {
                if ((item.Platforms & platform) == 0)
                    continue;

                newest ??= release.Version;
                if (item.IsSpotlight && spotlight is null)
                    spotlight = item;
                else if (highlights.Count < MaxHighlights)
                    highlights.Add(item);
            }
        }

        return new WhatsNewSelection(newest, spotlight, highlights);
    }

    /// <summary>The newest version the catalog describes.</summary>
    public static Version Newest(IReadOnlyList<WhatsNewRelease> releases)
        => releases.Count == 0 ? Baseline : releases.Max(release => release.Version)!;

    public static Version? Parse(string? text)
        => Version.TryParse(text?.Trim(), out var version) ? Normalize(version) : null;

    public static string Format(Version version) => Normalize(version).ToString(3);

    /// <summary>How a version reads to the user: "1.0" for a minor version's first release, else "1.0.2".</summary>
    public static string Display(Version version)
    {
        var normalized = Normalize(version);
        return normalized.Build == 0 ? normalized.ToString(2) : normalized.ToString(3);
    }

    private static Version Normalize(Version? version)
        => version is null
            ? new Version(0, 0, 0)
            : new Version(Math.Max(0, version.Major), Math.Max(0, version.Minor), Math.Max(0, version.Build));
}
