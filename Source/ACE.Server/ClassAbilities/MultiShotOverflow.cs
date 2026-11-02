using System;
using System.Collections.Generic;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure planning for a missile volley's extra arrows (weapon multi-shot AND class-ability arrows such as
    /// Multishot): which extra arrow goes to which acquired secondary target, and what happens to an arrow
    /// that has no secondary target left to go to.
    ///
    /// OWNER RULING: an arrow with no secondary target is not dropped. It OVERFLOWS onto the PRIMARY target
    /// at <see cref="OverflowFraction"/> (one third) of that arrow's own multishot damage multiplier, as its
    /// own hit (its own hit/miss roll, its own procs), exactly like a real extra. Example: one target and
    /// three extra arrows -> the primary takes its normal hit plus three hits at perShot/3. Two targets and
    /// three extras -> one extra to the second target and two overflow hits on the primary. The one exception:
    /// when the primary is out of line of sight the overflow arrows ARE dropped (see Plan's primaryInSight).
    ///
    /// Kept as a static class with no WorldObject dependency so the plan is unit testable; the launch path in
    /// Player.LaunchMissile executes it.
    /// </summary>
    public static class MultiShotOverflow
    {
        /// <summary>The share of an arrow's own multishot damage multiplier that an overflow hit on the primary deals.</summary>
        public const float OverflowFraction = 1.0f / 3.0f;

        /// <summary>
        /// One planned extra arrow. <see cref="ExtraTargetIndex"/> indexes the acquired secondary-target list,
        /// or is -1 for an overflow arrow aimed at the primary target.
        /// </summary>
        public readonly struct PlannedShot
        {
            public int ExtraTargetIndex { get; }
            public float DamageMultiplier { get; }

            public bool IsOverflow => ExtraTargetIndex < 0;

            public PlannedShot(int extraTargetIndex, float damageMultiplier)
            {
                ExtraTargetIndex = extraTargetIndex;
                DamageMultiplier = damageMultiplier;
            }
        }

        /// <summary>
        /// One entry per extra arrow, in shot order. Arrow i goes to secondary target i while one exists;
        /// every arrow beyond the acquired targets overflows onto the primary at multiplier / 3. An empty or
        /// null multiplier list plans nothing. A negative acquired count is treated as zero.
        ///
        /// <paramref name="primaryInSight"/> false drops every overflow arrow: an overflow hit is resolved
        /// cleave-style (its cosmetic projectile never collides), so without this gate it would land on a
        /// primary target behind a wall that the real arrow cannot reach. Secondary targets carry their own
        /// line-of-sight check in GetMultiShotTargets.
        /// </summary>
        public static List<PlannedShot> Plan(IReadOnlyList<float> shotMultipliers, int acquiredTargets, bool primaryInSight = true)
        {
            var plan = new List<PlannedShot>();

            if (shotMultipliers == null)
                return plan;

            acquiredTargets = Math.Max(0, acquiredTargets);

            for (var i = 0; i < shotMultipliers.Count; i++)
            {
                if (i < acquiredTargets)
                    plan.Add(new PlannedShot(i, shotMultipliers[i]));
                else if (primaryInSight)
                    plan.Add(new PlannedShot(-1, shotMultipliers[i] * OverflowFraction));
            }

            return plan;
        }
    }
}
