using System;
using System.Collections.Generic;
using System.Linq;
using Lumi.Models;

namespace Lumi.ViewModels;

/// <summary>
/// Keeping, retuning and hiding the new chat's setups in the profile, for the launchpad and the phone alike. A setup is
/// found by its entry in the row the user saw, so a kept one is the very pin that row shows. Keeping anything in a place
/// stops Lumi learning setups there, so the learned ones the row showed in that place are kept with it: keeping never
/// takes a setup off the row.
/// </summary>
internal static class LaunchpadSetupKeeper
{
    /// <summary>
    /// Keeps a setup on new chats. One the row learned is kept after the user's others, with the learned ones of its
    /// place; one the row doesn't hold becomes the first of them (made in the composer) or, pinned from the Other list
    /// (<paramref name="joinsTheEnd"/>), joins the end of the row, with the learned ones of its place after it. A place
    /// the user dismissed comes back.
    /// </summary>
    internal static void Keep(
        AppData data,
        IReadOnlyList<LaunchpadSetupChoice> row,
        LaunchpadSetupSpec spec,
        bool joinsTheEnd = false)
    {
        var shown = row.FirstOrDefault(choice => choice.Spec == spec);
        if (shown?.Pin is not null)
            return;

        if (shown is not null)
        {
            data.PinnedSetups.AddRange(LearnedAt(row, spec.Context));
        }
        else
        {
            data.PinnedSetups.RemoveAll(pin => IsSameSetup(pin, spec));
            data.PinnedSetups.InsertRange(joinsTheEnd ? data.PinnedSetups.Count : 0,
                [ToPin(spec), .. LearnedAt(row, spec.Context)]);
        }

        data.HiddenSetups.RemoveAll(hidden => PlaceOf(hidden) == spec.Context);
    }

    /// <summary>Lets a kept setup go back to being suggested by use.</summary>
    internal static void StopKeeping(AppData data, IReadOnlyList<LaunchpadSetupChoice> row, LaunchpadSetupSpec spec)
    {
        if (row.FirstOrDefault(choice => choice.Spec == spec)?.Pin is { } pin)
            data.PinnedSetups.Remove(pin);
        else
            data.PinnedSetups.RemoveAll(kept => IsSameSetup(kept, spec));
    }

    /// <summary>
    /// Takes a setup off the new chat. Once nothing is kept in its place, the place isn't suggested by use either, so the
    /// setup doesn't come straight back as a learned one.
    /// </summary>
    internal static void Hide(AppData data, LaunchpadSetupPin? pin, LaunchpadSetupSpec spec)
    {
        if (pin is not null)
            data.PinnedSetups.Remove(pin);
        if (!data.PinnedSetups.Exists(kept => PlaceOf(kept) == spec.Context)
            && !data.HiddenSetups.Exists(hidden => PlaceOf(hidden) == spec.Context))
        {
            data.HiddenSetups.Add(ToPin(spec with { ModelId = null, Effort = null }));
        }
    }

    internal static void Hide(AppData data, IReadOnlyList<LaunchpadSetupChoice> row, LaunchpadSetupSpec spec)
        => Hide(data, row.FirstOrDefault(choice => choice.Spec == spec)?.Pin, spec);

    /// <summary>
    /// Keeps the changed setup in place of the one the user adjusted (a learned one is kept from now on). Its other kept
    /// variants stay, and so do the learned ones the row shows where it now starts; one the change made identical to it
    /// goes, since a setup is kept once.
    /// </summary>
    internal static void Retune(
        AppData data,
        IReadOnlyList<LaunchpadSetupChoice> row,
        LaunchpadSetupPin? pin,
        LaunchpadSetupSpec original,
        LaunchpadSetupSpec changed)
    {
        var pins = data.PinnedSetups;
        var replacement = ToPin(changed);
        var index = pin is not null ? pins.IndexOf(pin) : -1;
        if (index >= 0)
            pins[index] = replacement;
        else
            pins.Add(replacement);
        pins.RemoveAll(other => !ReferenceEquals(other, replacement) && IsSameSetup(other, changed));
        pins.AddRange(LearnedAt(row, changed.Context, except: original).Where(shown => !IsSameSetup(shown, changed)));
        data.HiddenSetups.RemoveAll(hidden => PlaceOf(hidden) == changed.Context);
    }

    internal static void Retune(
        AppData data,
        IReadOnlyList<LaunchpadSetupChoice> row,
        LaunchpadSetupSpec original,
        LaunchpadSetupSpec changed)
        => Retune(data, row, row.FirstOrDefault(choice => choice.Spec == original)?.Pin, original, changed);

    /// <summary>The learned setups the row shows in a place, as kept setups in row order.</summary>
    private static List<LaunchpadSetupPin> LearnedAt(
        IReadOnlyList<LaunchpadSetupChoice> row,
        (Guid? ProjectId, Guid? AgentId, bool UseWorktree) place,
        LaunchpadSetupSpec? except = null)
        => row.Where(choice => choice.Pin is null && choice.Spec.Context == place && choice.Spec != except)
            .Select(static choice => ToPin(choice.Spec))
            .ToList();

    internal static LaunchpadSetupPin ToPin(LaunchpadSetupSpec spec) => new()
    {
        ProjectId = spec.ProjectId,
        AgentId = spec.AgentId,
        UseWorktree = spec.UseWorktree,
        ModelId = spec.ModelId,
        Effort = spec.Effort,
    };

    internal static (Guid? ProjectId, Guid? AgentId, bool UseWorktree) PlaceOf(LaunchpadSetupPin pin)
        => (pin.ProjectId, pin.AgentId, pin.UseWorktree);

    /// <summary>The kept setup is this one: the same place, model and effort.</summary>
    internal static bool IsSameSetup(LaunchpadSetupPin pin, LaunchpadSetupSpec spec)
        => PlaceOf(pin) == spec.Context
           && string.Equals(pin.ModelId, spec.ModelId, StringComparison.Ordinal)
           && string.Equals(pin.Effort, spec.Effort, StringComparison.Ordinal);
}
