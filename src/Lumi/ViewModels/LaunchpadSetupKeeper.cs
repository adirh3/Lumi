using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// Keeping, adding, editing and dismissing the new chat's starters in the profile, for the launchpad and the phone alike.
/// A kept starter is edited as itself (its pin), whatever it starts with; one named by its recipe is the kept starter the
/// row shows for it. Keeping a recipe that is already kept keeps it once, a starter made in the new starter card is added
/// beside any other with its recipe, and a kept starter that is exactly a learned place stands in for it on the row.
/// </summary>
internal static class LaunchpadSetupKeeper
{
    /// <summary>
    /// Keeps a recipe on new chats, as a learned starter's Keep or the draft's pin does: the kept starter that has it
    /// already (the one the row shows for it), else a new one after the user's others, without a name or icon of its own.
    /// </summary>
    internal static LaunchpadSetupPin Keep(AppData data, IReadOnlyList<LaunchpadSetupChoice> row, LaunchpadSetupSpec spec)
    {
        if (FindPin(data, row, spec) is { } kept)
            return kept;

        var pin = ToPin(spec);
        data.PinnedSetups.Add(pin);
        return pin;
    }

    /// <summary>
    /// Adds a starter made in the new starter card after the user's others, under its name and icon (null or empty for
    /// its place's). It never renames or replaces another kept starter, even one with its recipe; only one kept exactly
    /// so (its recipe, name and icon) is returned instead, so asking twice adds it once.
    /// </summary>
    internal static LaunchpadSetupPin Add(AppData data, LaunchpadSetupSpec spec, string? name, string? icon)
    {
        var pin = ToPin(spec);
        Rename(data, pin, spec, name ?? "", icon ?? "");
        if (data.PinnedSetups.FirstOrDefault(other => IsSameSetup(other, spec)
                                                      && string.Equals(other.Name, pin.Name, StringComparison.Ordinal)
                                                      && string.Equals(other.Icon, pin.Icon, StringComparison.Ordinal)) is { } same)
        {
            return same;
        }

        data.PinnedSetups.Add(pin);
        return pin;
    }

    /// <summary>Lets a kept starter go; its place is suggested again if the user still starts there.</summary>
    internal static bool StopKeeping(AppData data, LaunchpadSetupPin pin) => data.PinnedSetups.Remove(pin);

    /// <summary>Lets go of the kept starter the row shows for a recipe, else one kept with exactly that recipe.</summary>
    internal static bool StopKeeping(AppData data, IReadOnlyList<LaunchpadSetupChoice> row, LaunchpadSetupSpec spec)
        => FindPin(data, row, spec) is { } pin && data.PinnedSetups.Remove(pin);

    /// <summary>Stops suggesting a place. Just chat is always offered, so it can't be dismissed.</summary>
    internal static bool Hide(AppData data, LaunchpadSetupSpec spec)
    {
        if (spec.IsJustChat || data.HiddenSetups.Exists(hidden => PlaceOf(hidden) == spec.Context))
            return false;

        data.HiddenSetups.Add(ToPin(spec.Place));
        return true;
    }

    /// <summary>
    /// Changes what a kept starter (<paramref name="pin"/>) starts with, or its name or icon (null leaves either as it is;
    /// empty clears it), from now on. A learned starter (no pin) is kept with the change, so it holds. Two kept starters
    /// the change makes look the same (one recipe, neither with a name or icon of its own) become one; a starter the user
    /// tells apart from it by a name or icon stays, as the new starter card adds those beside their recipe's others.
    /// Returns the kept starter, or null when <paramref name="pin"/> was let go meanwhile.
    /// </summary>
    internal static LaunchpadSetupPin? Edit(
        AppData data,
        IReadOnlyList<LaunchpadSetupChoice> row,
        LaunchpadSetupPin? pin,
        LaunchpadSetupSpec changed,
        string? name = null,
        string? icon = null)
    {
        if (pin is null)
        {
            pin = FindPin(data, row, changed);
            if (pin is null)
            {
                pin = ToPin(changed);
                data.PinnedSetups.Add(pin);
            }
        }
        else if (data.PinnedSetups.Contains(pin))
        {
            pin.UseWorktree = changed.UseWorktree;
            pin.ModelId = changed.ModelId;
            pin.Effort = changed.Effort;
        }
        else
        {
            return null;
        }

        Rename(data, pin, changed, name, icon);
        if (pin.Name is null && pin.Icon is null)
        {
            data.PinnedSetups.RemoveAll(other => !ReferenceEquals(other, pin)
                                                 && other.Name is null
                                                 && other.Icon is null
                                                 && IsSameSetup(other, changed));
        }

        return pin;
    }

    /// <summary>The kept starter the row shows for a recipe, else one kept with exactly that recipe.</summary>
    internal static LaunchpadSetupPin? FindPin(AppData data, IReadOnlyList<LaunchpadSetupChoice> row, LaunchpadSetupSpec spec)
        => row.FirstOrDefault(choice => choice.Pin is not null && choice.Spec == spec)?.Pin is { } shown
           && data.PinnedSetups.Contains(shown)
            ? shown
            : data.PinnedSetups.FirstOrDefault(pin => IsSameSetup(pin, spec));

    private static void Rename(AppData data, LaunchpadSetupPin pin, LaunchpadSetupSpec spec, string? name, string? icon)
    {
        if (name is not null)
        {
            var project = spec.ProjectId is { } projectId ? data.Projects.ToArray().FirstOrDefault(p => p.Id == projectId) : null;
            var agent = spec.AgentId is { } agentId ? data.Agents.ToArray().FirstOrDefault(a => a.Id == agentId) : null;
            pin.Name = NormalizeName(name, LaunchpadViewModel.PlaceTitle(project, agent));
        }

        if (icon is not null)
            pin.Icon = LaunchpadPlanner.StarterIcons.Contains(icon) ? icon : null;
    }

    /// <summary>A name as the starter keeps it: trimmed and bounded; none when it is empty or just its place's name.</summary>
    internal static string? NormalizeName(string name, string? placeTitle)
    {
        var trimmed = name.Trim();
        if (trimmed.Length > LaunchpadPlanner.MaxStarterNameLength)
            trimmed = trimmed[..LaunchpadPlanner.MaxStarterNameLength].TrimEnd();
        return trimmed.Length == 0 || string.Equals(trimmed, placeTitle, StringComparison.Ordinal) ? null : trimmed;
    }

    internal static LaunchpadSetupPin ToPin(LaunchpadSetupSpec spec) => new()
    {
        ProjectId = spec.ProjectId,
        AgentId = spec.AgentId,
        UseWorktree = spec.UseWorktree,
        ModelId = spec.ModelId,
        Effort = spec.Effort,
    };

    /// <summary>
    /// The recipe a kept starter holds, whatever this computer offers now: a model it no longer offers stays, so a change
    /// to anything else (its name, icon or checkout) keeps it for when the model is back.
    /// </summary>
    internal static LaunchpadSetupSpec SpecOf(LaunchpadSetupPin pin) => new(
        pin.ProjectId,
        pin.AgentId,
        pin.UseWorktree && pin.ProjectId is not null,
        pin.ModelId,
        pin.ModelId is null ? null : pin.Effort);

    internal static (Guid? ProjectId, Guid? AgentId, bool UseWorktree) PlaceOf(LaunchpadSetupPin pin)
        => (pin.ProjectId, pin.AgentId, pin.UseWorktree);

    /// <summary>The kept starter has this recipe: the same place, model and effort.</summary>
    internal static bool IsSameSetup(LaunchpadSetupPin pin, LaunchpadSetupSpec spec)
        => PlaceOf(pin) == spec.Context
           && string.Equals(pin.ModelId, spec.ModelId, StringComparison.Ordinal)
           && string.Equals(pin.Effort, spec.Effort, StringComparison.Ordinal);
}
