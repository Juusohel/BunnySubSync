using System.Collections.Generic;

namespace BunnySubSync.Game;

/// <summary>
/// Picks the site FC a workshop's sector unlocks are sent to.
///
/// The Mapping tab maps a game FC either to one site FC or to "(account)"
/// (site FC id 0), which matches its subs against every sub on the account.
/// Unlocks belong to one real site FC, so for "(account)" the FC comes from
/// the workshop's linked subs: when every one of them is in the same site FC,
/// that's the FC. No links, links spread over several FCs, or a linked sub the
/// site's list doesn't show in an FC: nothing is sent, and the user picks the
/// FC in the Mapping tab. FC 0 is never a target.
///
/// Pure (no game or network state), so it can be checked on its own.
/// </summary>
public static class AccountFcResolver
{
    public enum Outcome
    {
        /// <summary>Send the unlocks to <see cref="Target.FcId"/>.</summary>
        Send,

        /// <summary>"(account)" with links, but the site's sub list isn't loaded yet.</summary>
        NeedInventory,

        /// <summary>No single site FC; send nothing.</summary>
        Ambiguous,
    }

    public readonly record struct Target(Outcome Outcome, int FcId);

    private static readonly Target AmbiguousTarget = new(Outcome.Ambiguous, 0);

    /// <param name="mappedFcId">The mapping's site FC id (0 = "(account)").</param>
    /// <param name="linkedSiteSubIds">The site sub ids of this workshop's enabled links.</param>
    /// <param name="siteSubFcs">Site sub id → its site FC id, from the account's
    /// sub list; null when it hasn't been loaded.</param>
    public static Target Resolve(
        int mappedFcId,
        IReadOnlyCollection<int> linkedSiteSubIds,
        IReadOnlyDictionary<int, int?>? siteSubFcs)
    {
        if (mappedFcId > 0)
            return new Target(Outcome.Send, mappedFcId);
        if (mappedFcId < 0 || linkedSiteSubIds.Count == 0)
            return AmbiguousTarget;
        if (siteSubFcs == null)
            return new Target(Outcome.NeedInventory, 0);

        int? fcId = null;
        foreach (var subId in linkedSiteSubIds)
        {
            if (!siteSubFcs.TryGetValue(subId, out var subFc) || subFc is not > 0)
                return AmbiguousTarget;
            if (fcId == null)
                fcId = subFc;
            else if (fcId != subFc)
                return AmbiguousTarget;
        }

        return new Target(Outcome.Send, fcId!.Value);
    }
}
