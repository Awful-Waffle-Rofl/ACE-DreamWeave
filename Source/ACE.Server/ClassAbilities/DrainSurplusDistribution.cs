using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// FORK ADDITION - the allocation math for Drain Health's surplus cascade.
    ///
    /// Lives under ClassAbilities, not Entity: an ability's pure math belongs beside its handler, and this
    /// was one of only two files that did not follow that rule.
    ///
    /// Retail Drain caps its destination transfer at the caster's OWN missing health, so a caster at full
    /// health scales the whole spell to zero and Drain does no damage at all. The fork widens the receiving
    /// capacity to the caster plus their nearby RECIPIENTS - fellowship members and the caster's own summons
    /// (user, 2026-08-03) - and routes the overflow to them; the spell's TransferCap still clamps the total
    /// afterwards, so the per-cast ceiling is unchanged - the drain just reaches that ceiling more often.
    ///
    /// This class is the pure arithmetic half of that, kept as a static so it is unit-testable without a live
    /// Player (Player's static initializer cannot run under the test host). It is entirely blind to WHAT the
    /// recipients are: it sees a list of missing-health figures and nothing else, which is exactly why adding
    /// summons to the recipient list changed no arithmetic here. The eligibility half - fellowship membership
    /// or summon ownership, same landblock, alive, within range - lives at the call site in
    /// WorldObject.GetDrainSurplusFellows, because it needs live world state.
    ///
    /// Model:
    ///  - the surplus is split in proportion to how much health each recipient is MISSING, so the most hurt
    ///    recipient receives the most,
    ///  - no recipient ever receives more than they are missing,
    ///  - if the surplus exceeds the total missing across all recipients, everyone is topped up and the
    ///    remainder is simply lost (it is NOT returned to the caster and does NOT raise the drain).
    /// </summary>
    public static class DrainSurplusDistribution
    {
        /// <summary>
        /// What fraction of the surplus actually reaches the fellows, from the caster's Transfusion rank
        /// plus the Healing rider.
        ///
        /// rankFraction is 40/70/100% at ranks 1/2/3. healingRider is the dual-ratio Healing scaling from
        /// Player.GetClassAbilityScaling, already converted from percentage points to a fraction, and it is
        /// ADDITIVE on top of the rank fraction - so a skilled healer at rank 3 delivers MORE than the drain
        /// produced (about 108% Trained / 112% Specialized at 400 Healing).
        ///
        /// OVER 100% IS INTENDED AND THERE IS DELIBERATELY NO CLAMP AT 1.0. It looks like a bug and is not.
        /// <see cref="Distribute"/> caps every recipient at their own missing health, so a share above 100%
        /// cannot overheal anyone - the extra points simply reach MORE of the party before the "everyone is
        /// topped up, the rest is lost" branch takes over. The ceiling on a cast is still TransferCap, which
        /// has already been applied upstream and is not touched by any of this: raising the share changes
        /// only how much of the caster's UNUSED surplus is passed on, never how hard the drain hits.
        ///
        /// Rank is clamped rather than rejected out of range, matching the eligibility gate, which treats
        /// any rank >= 1 as owning the ability: a persisted rank above MaxRank behaves as max rank, never as
        /// unlearned. Rank 0 returns 0 - though the caller never gets that far, because rank 0 fails the
        /// eligibility gate and takes the retail path instead.
        ///
        /// <paramref name="gear"/> is the TRANSFUSION equipment mod (EquipmentModId.Transfusion), a THIRD
        /// ADDITIVE SUMMAND on the same axis as the rank fraction and the Healing rider - never a factor
        /// (DESIGN.md 3.3, the axis rule). It defaults to 0 and adding 0.0 is exact, so an unmodded caster
        /// reproduces the previous fraction bit-for-bit and TransfusionShareTests is unaffected.
        ///
        /// THE rank &lt;= 0 EARLY RETURN IS ALSO THE MACHINERY GATE. Transfusion's mod is machinery: it is
        /// inert without the ability. Because the rank test runs BEFORE the gear term is read, a caster who
        /// does not own Transfusion gets 0 no matter what they are wearing, whatever the call site did.
        /// </summary>
        public static double ShareFraction(int rank, double shareR1, double shareR2, double shareR3, double healingRider, double gear = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankFraction = rank switch
            {
                1 => shareR1,
                2 => shareR2,
                _ => shareR3,
            };

            return Math.Max(0.0, rankFraction + healingRider + gear);
        }

        /// <summary>
        /// Applies a share fraction to a surplus, in whole points. Saturates rather than wrapping, because
        /// the fraction is uncapped by design and the surplus is a uint.
        /// </summary>
        public static uint ApplyShare(uint surplus, double shareFraction)
        {
            if (surplus == 0 || shareFraction <= 0.0)
                return 0;

            var scaled = Math.Round(surplus * shareFraction);

            if (scaled >= uint.MaxValue)
                return uint.MaxValue;

            return (uint)scaled;
        }

        /// <summary>
        /// Splits <paramref name="surplus"/> points of healing across recipients, weighted by how much each
        /// is missing. Returns one allocation per entry of <paramref name="missing"/>, in the same order.
        ///
        /// The returned allocations always sum to exactly min(surplus, sum(missing)), and allocation[i] is
        /// never greater than missing[i]. Whole points only - fractional shares are handed out by largest
        /// remainder, so no point is lost to rounding while capacity remains.
        /// </summary>
        public static uint[] Distribute(uint surplus, IReadOnlyList<uint> missing)
        {
            if (missing == null || missing.Count == 0)
                return Array.Empty<uint>();

            var allocations = new uint[missing.Count];

            if (surplus == 0)
                return allocations;

            ulong totalMissing = 0;

            for (var i = 0; i < missing.Count; i++)
                totalMissing += missing[i];

            if (totalMissing == 0)
                return allocations;

            // more surplus than anyone can absorb - top everyone up, the rest is lost
            if (surplus >= totalMissing)
            {
                for (var i = 0; i < missing.Count; i++)
                    allocations[i] = missing[i];

                return allocations;
            }

            // proportional floor pass. surplus < totalMissing here, so every floor is <= missing[i].
            var remainders = new double[missing.Count];
            ulong allocated = 0;

            for (var i = 0; i < missing.Count; i++)
            {
                var exact = (ulong)surplus * missing[i];
                var share = exact / totalMissing;

                allocations[i] = (uint)share;
                remainders[i] = (double)(exact % totalMissing) / totalMissing;

                allocated += share;
            }

            // largest-remainder pass for the points lost to flooring. Total capacity strictly exceeds the
            // surplus, so this always terminates with the full surplus handed out.
            var leftover = surplus - allocated;

            while (leftover > 0)
            {
                var best = -1;
                var bestRemainder = -1.0;

                for (var i = 0; i < missing.Count; i++)
                {
                    if (allocations[i] >= missing[i])
                        continue;

                    if (remainders[i] > bestRemainder)
                    {
                        bestRemainder = remainders[i];
                        best = i;
                    }
                }

                if (best < 0)
                    break;

                allocations[best]++;
                remainders[best] = -1.0;
                leftover--;
            }

            return allocations;
        }
    }
}
