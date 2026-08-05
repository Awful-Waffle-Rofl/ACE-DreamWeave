using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>The result of the pooled Shield Block / Parry avoidance roll.</summary>
    public enum ClassAbilityAvoidanceOutcome
    {
        None,
        Block,
        Parry,
    }

    /// <summary>
    /// Pure math for the pooled Shield Block + Parry avoidance roll (SKILL-TABLES-PREVIEW: "the avoidance
    /// tier"). Block and Parry share ONE roll, additive, capped at a combined maximum; when the raw sum
    /// exceeds the cap both are scaled down proportionally so their ratio (and thus which synergy is more
    /// likely to fire) is preserved. Kept pure so it is unit-testable without a live combat frame; the
    /// caller supplies the two chances, the cap, and a [0,1) roll.
    ///
    /// This is deliberately resolved BEFORE the normal evade roll (Player/DamageEvent), because a blocked
    /// or parried hit must be able to proc Thorns / Shield Check / Riposte, whereas an evaded hit procs
    /// nothing - the Vanguard-vs-Rogue fork the whole design turns on.
    /// </summary>
    public static class ClassAbilityAvoidance
    {
        /// <summary>
        /// The two chances AFTER the pooled cap has been applied, with their ratio preserved. Returns
        /// <paramref name="capped"/> true when the cap actually reduced them.
        ///
        /// Extracted so the /abilities readout can report each ability's post-cap share by CALLING this
        /// rather than restating the scale-down. Parry and Shield Block share one cap, so neither can
        /// compute its own effective chance alone, and two hand-mirrored copies of the arithmetic would
        /// drift from this one the first time the pooling rule changed.
        /// </summary>
        public static (double Block, double Parry, bool Capped) Pooled(double blockChance, double parryChance, double cap)
        {
            blockChance = Math.Max(0.0, blockChance);
            parryChance = Math.Max(0.0, parryChance);

            var total = blockChance + parryChance;

            // scale both down proportionally if the pooled chance exceeds the cap
            if (cap > 0.0 && total > cap)
            {
                var scale = cap / total;
                return (blockChance * scale, parryChance * scale, true);
            }

            return (blockChance, parryChance, false);
        }

        public static ClassAbilityAvoidanceOutcome Resolve(double blockChance, double parryChance, double cap, double roll)
        {
            if (Math.Max(0.0, blockChance) + Math.Max(0.0, parryChance) <= 0.0)
                return ClassAbilityAvoidanceOutcome.None;

            (blockChance, parryChance, _) = Pooled(blockChance, parryChance, cap);

            if (roll < blockChance)
                return ClassAbilityAvoidanceOutcome.Block;

            if (roll < blockChance + parryChance)
                return ClassAbilityAvoidanceOutcome.Parry;

            return ClassAbilityAvoidanceOutcome.None;
        }
    }
}
