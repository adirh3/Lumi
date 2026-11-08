using System;
using System.Collections.Generic;
using Lumi.Localization;
using StrataTheme.Controls;

namespace Lumi.Services.WhatsNew;

/// <summary>
/// Where a What's new item's action takes the user. A new destination also gets its case in
/// <c>MainViewModel.OpenWhatsNewDestination</c>; a test fails for one that leads nowhere.
/// </summary>
public enum WhatsNewDestination
{
    None,

    /// <summary>Settings → Mobile, where a phone is connected and paired.</summary>
    PhoneSettings,

    /// <summary>Settings → AI &amp; Models, at Image generation.</summary>
    ImageSettings,

    /// <summary>Settings → AI &amp; Models, at Response detail.</summary>
    ResponseDetailSettings,

    /// <summary>The Jobs page.</summary>
    Jobs,

    /// <summary>Settings → Permissions, at Sandbox.</summary>
    SandboxSettings,
}

/// <summary>The platforms an item is shown on, for what only some platforms have.</summary>
[Flags]
public enum WhatsNewPlatforms
{
    Windows = 1,
    MacOS = 2,
    Linux = 4,
    All = Windows | MacOS | Linux,
}

/// <summary>One line of a spotlight: an icon (a <see cref="StrataIcons"/> name) and a short sentence (a <see cref="Loc"/> key).</summary>
public sealed record WhatsNewPoint(string Icon, string TextKey);

/// <summary>
/// One thing a release brings, written for the user. Text is given as <see cref="Loc"/> keys, written with
/// <c>nameof(Loc.…)</c> so a string missing from en.json fails the build, and the icon as the name of one of Strata's
/// line icons, written with <c>nameof(StrataIcons.…)</c>.
/// A spotlight is the release's headline: shown large above the rest, with an eyebrow, points and a note.
/// </summary>
public sealed record WhatsNewItem(string Icon, string TitleKey, string BodyKey)
{
    /// <summary>The label of the action that takes the user to <see cref="Destination"/>.</summary>
    public string? ActionKey { get; init; }

    public WhatsNewDestination Destination { get; init; }

    public bool IsSpotlight { get; init; }

    /// <summary>A spotlight's small label above its title.</summary>
    public string? EyebrowKey { get; init; }

    /// <summary>A spotlight's lines under its body.</summary>
    public IReadOnlyList<WhatsNewPoint> Points { get; init; } = [];

    /// <summary>A spotlight's quiet line under its action.</summary>
    public string? NoteKey { get; init; }

    public WhatsNewPlatforms Platforms { get; init; } = WhatsNewPlatforms.All;
}

/// <summary>A released version of Lumi (major.minor.patch) and what it brings, most important first.</summary>
public sealed record WhatsNewRelease(Version Version, IReadOnlyList<WhatsNewItem> Items)
{
    public WhatsNewRelease(string version, IReadOnlyList<WhatsNewItem> items)
        : this(WhatsNewSchedule.Parse(version) ?? throw new FormatException($"'{version}' is not a version."), items)
    {
    }
}

/// <summary>
/// What each version of Lumi shows after an update, newest first. Every release Lumi ships has an entry here, made
/// before the version is bumped (an empty one when the release brings nothing worth showing). The Update Whats New
/// skill (.github/skills) walks through writing one, its strings in all ten languages included; the entries below show
/// each kind of item: a spotlight, items with an action and plain ones.
/// </summary>
public static class WhatsNewCatalog
{
    public static IReadOnlyList<WhatsNewRelease> Releases { get; } =
    [
        new("1.0.2",
        [
            // Lumi on your phone gathers what the phone gained since 1.0 (notifications in 1.0.1, suggestions in 1.0.2).
            new(nameof(StrataIcons.Phone), nameof(Loc.WhatsNew_Phone_Title), nameof(Loc.WhatsNew_Phone_Body))
            {
                IsSpotlight = true,
                EyebrowKey = nameof(Loc.WhatsNew_Phone_Eyebrow),
                ActionKey = nameof(Loc.WhatsNew_Phone_Connect),
                Destination = WhatsNewDestination.PhoneSettings,
                NoteKey = nameof(Loc.WhatsNew_Phone_Private),
                Points =
                [
                    new(nameof(StrataIcons.Bell), nameof(Loc.WhatsNew_Phone_Notify)),
                    new(nameof(StrataIcons.Reply), nameof(Loc.WhatsNew_Phone_Reply)),
                    new(nameof(StrataIcons.Files), nameof(Loc.WhatsNew_Phone_Files)),
                ],
            },
            new(nameof(StrataIcons.Image), nameof(Loc.WhatsNew_Images_Title), nameof(Loc.WhatsNew_Images_Body))
            {
                ActionKey = nameof(Loc.WhatsNew_Images_Action),
                Destination = WhatsNewDestination.ImageSettings,
            },
            new(nameof(StrataIcons.Retry), nameof(Loc.WhatsNew_Rewind_Title), nameof(Loc.WhatsNew_Rewind_Body)),
            new(nameof(StrataIcons.Cube), nameof(Loc.WhatsNew_Sandbox_Title), nameof(Loc.WhatsNew_Sandbox_Body))
            {
                ActionKey = nameof(Loc.WhatsNew_Sandbox_Action),
                Destination = WhatsNewDestination.SandboxSettings,
            },
            new(nameof(StrataIcons.Queue), nameof(Loc.WhatsNew_Queue_Title), nameof(Loc.WhatsNew_Queue_Body)),
            new(nameof(StrataIcons.Question), nameof(Loc.WhatsNew_Rail_Title), nameof(Loc.WhatsNew_Rail_Body)),
        ]),
        new("1.0.1",
        [
            new(nameof(StrataIcons.Overlap), nameof(Loc.WhatsNew_Fusion_Title), nameof(Loc.WhatsNew_Fusion_Body)),
            new(nameof(StrataIcons.Lightbulb), nameof(Loc.WhatsNew_FollowUps_Title), nameof(Loc.WhatsNew_FollowUps_Body)),
            new(nameof(StrataIcons.Sliders), nameof(Loc.WhatsNew_Detail_Title), nameof(Loc.WhatsNew_Detail_Body))
            {
                ActionKey = nameof(Loc.WhatsNew_Detail_Action),
                Destination = WhatsNewDestination.ResponseDetailSettings,
            },
            new(nameof(StrataIcons.Clock), nameof(Loc.WhatsNew_Jobs_Title), nameof(Loc.WhatsNew_Jobs_Body))
            {
                ActionKey = nameof(Loc.WhatsNew_Jobs_Action),
                Destination = WhatsNewDestination.Jobs,
            },
        ]),
    ];
}
