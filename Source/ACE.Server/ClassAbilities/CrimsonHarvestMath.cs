using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure math for CRIMSON HARVEST (Blood Mage T2): a Drain spell strikes up to 4 creatures within 8m of
    /// the primary target instead of one. Each secondary strike then resolves INDEPENDENTLY through the
    /// normal transfer path - its own transfer_Cap, its own resistance roll, its own heal back to the
    /// caster - so the only thing that needs to be arithmetic rather than world state is WHICH neighbours
    /// are picked when more than the cap are in range.
    ///
    /// Kept as a static so that selection rule is unit-testable without a landblock. The live half -
    /// visible-object enumeration, the hostile/PvE filters, and re-entering the transfer path - lives at
    /// the call site in WorldObject_Magic.HandleCastSpell_Transfer, because it needs world state.
    /// </summary>
    public static class CrimsonHarvestMath
    {
        /// <summary>
        /// Picks the secondary targets: everything strictly inside <paramref name="radius"/>, nearest
        /// first, capped at <paramref name="maxTargets"/>. Returns indices into
        /// <paramref name="distances"/>, so the caller keeps its own candidate list.
        ///
        /// NEAREST-FIRST IS THE TIE-BREAK, NOT A DAMAGE MODEL. Every selected target takes the same
        /// independent drain; distance only decides who makes the cut when a pack is denser than the cap.
        /// Ordering is stable for equal distances (the caller's enumeration order wins), so a cast into a
        /// stack of identically-placed creatures is deterministic rather than arbitrary.
        ///
        /// The distance measured is from the PRIMARY TARGET, not the caster - the harvest spreads outward
        /// from whatever was drained, the same shape Spell AOE uses for its radiated blasts.
        /// </summary>
        public static int[] SelectSecondaryTargets(IReadOnlyList<double> distances, double radius, int maxTargets)
        {
            if (distances == null || distances.Count == 0 || maxTargets <= 0 || radius <= 0.0)
                return Array.Empty<int>();

            var inRange = new List<int>();

            for (var i = 0; i < distances.Count; i++)
            {
                if (distances[i] <= radius)
                    inRange.Add(i);
            }

            if (inRange.Count == 0)
                return Array.Empty<int>();

            // stable sort by distance: List.Sort is unstable, so break ties on the original index
            inRange.Sort((a, b) =>
            {
                var cmp = distances[a].CompareTo(distances[b]);
                return cmp != 0 ? cmp : a.CompareTo(b);
            });

            if (inRange.Count > maxTargets)
                inRange.RemoveRange(maxTargets, inRange.Count - maxTargets);

            return inRange.ToArray();
        }
    }
}
