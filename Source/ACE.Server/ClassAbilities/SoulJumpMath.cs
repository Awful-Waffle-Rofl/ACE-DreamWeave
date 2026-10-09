using System;

namespace ACE.Server.ClassAbilities
{
    /// <summary>
    /// Pure arithmetic for the Void/Summon T3 entry Soul Jump: when a blow would kill the summoner and a
    /// combat pet is alive, the pet dies instead and the summoner ends the hit at 10% of maximum health,
    /// once every 6/4/2 minutes by rank.
    ///
    /// Kept as a pure static because the decision itself - "would this blow kill me, and may the save fire" -
    /// is the whole ability, and Player's static initializer cannot run under the unit test host.
    ///
    /// THE SAVE IS EXPRESSED AS DAMAGE, NOT AS A "SURVIVE THIS" FLAG. Every one of the five damage sites
    /// writes the pre-write hook's damage figure to the vital and only then checks for death, so a handler
    /// that leaves less damage than the defender has Health has already saved them; a boolean would have to
    /// be honoured by five separate death checks in four files. See IPreWriteDamageAbility's own doc comment.
    /// </summary>
    public static class SoulJumpMath
    {
        /// <summary>
        /// The seconds between saves at a given rank: base + (rank-1)*step, which is 360/240/120 (6/4/2
        /// minutes) at ranks 1-3 because the registered STEP IS NEGATIVE - for this one magnitude, smaller
        /// is stronger.
        ///
        /// THE COOLDOWN CARRIES NO AFFINITY TERM AT ALL. Jump multiplies the RESTORED HEALTH instead (see
        /// <see cref="RestoreFraction"/>), which is what the signed-off design says and is also what keeps
        /// the standard affinity shape honest: the primitive returns a factor at or above 1.0, so it can
        /// only be applied to a quantity where bigger is better. Do not add a rider here later, and in
        /// particular do not multiply this by an affinity factor - that would LENGTHEN the cooldown and make
        /// the skill a penalty.
        ///
        /// <paramref name="minimumSeconds"/> floors the result. That guard is not about affinity (there is
        /// none on this number); it protects against a mis-tuned base/step pair, since a step steeper than
        /// the base drives the rank 3 cooldown negative and a negative cooldown reads as "always ready".
        ///
        /// Returns 0 for rank 0 / unlearned, which no caller uses as a cooldown - the ability cannot fire at
        /// all without a rank.
        /// </summary>
        public static double CooldownSeconds(int rank, double baseSeconds, double stepSeconds, double minimumSeconds)
        {
            if (rank <= 0)
                return 0.0;

            var rankCooldown = Math.Max(0.0, baseSeconds + (rank - 1) * stepSeconds);

            return Math.Max(Math.Max(0.0, minimumSeconds), rankCooldown);
        }

        /// <summary>
        /// The fraction of maximum health the summoner comes back with: the flat base fraction (10%),
        /// multiplied by the Jump affinity factor.
        ///
        /// THIS IS THE ABILITY'S ONE AFFINITY-BEARING MAGNITUDE. Jump multiplies the health you come back
        /// with at Soul Jump's own non-standard rate pair (+5% of it per 100 points, +7% per 100 when
        /// Specialized, against the shared 0.12/0.17), so 200 points of Specialized Jump turns a 10.0%
        /// restore into 11.4%. At zero effective Jump the factor is exactly 1.0 and the restore is
        /// bit-identical to the bare tunable.
        ///
        /// RANK-INVARIANT ON PURPOSE: rank buys cooldown here, not restore size, so there is no rank term to
        /// scale and the factor multiplies the flat fraction directly. A factor below 1.0 - which the
        /// primitive never returns - is treated as 1.0 rather than shrinking the restore.
        /// </summary>
        public static double RestoreFraction(double baseFraction, double affinityMultiplier)
        {
            if (baseFraction <= 0.0)
                return 0.0;

            return baseFraction * (affinityMultiplier < 1.0 ? 1.0 : affinityMultiplier);
        }

        /// <summary>
        /// Whether the save fires for this hit. All four conditions are load-bearing:
        ///
        ///  - <paramref name="hasLivePet"/> - WITH NO PET ALIVE THERE IS NO SAVE. That is the entry's whole
        ///    cost structure, not a precondition to be relaxed later: it spends a pet and leaves the
        ///    summoner at 10% health with nothing out.
        ///  - <paramref name="cooldownReady"/> - one save per 6/4/2 minutes by rank.
        ///  - <paramref name="currentHealth"/> above zero - a defender already at zero is not being saved by
        ///    anything; the blow that took them there was the lethal one.
        ///  - the blow must actually be lethal (damage at or above current health). A save that fired on a
        ///    survivable hit would spend the pet and the cooldown for nothing, which is exactly what an
        ///    incorrectly ordered death save does - see the ordering note on DamageMitigationOrder.DeathSave.
        /// </summary>
        public static bool SavesFromDeath(uint incomingDamage, uint currentHealth, bool hasLivePet, bool cooldownReady)
        {
            return hasLivePet && cooldownReady && currentHealth > 0 && incomingDamage >= currentHealth;
        }

        /// <summary>
        /// The health the summoner is left at: <paramref name="restoreFraction"/> of maximum, rounded, and
        /// never zero while they have any maximum health at all (a save that left the player on 0 health
        /// would be no save).
        /// </summary>
        public static uint RestoreAmount(uint maxHealth, double restoreFraction)
        {
            if (maxHealth == 0 || restoreFraction <= 0.0)
                return 0;

            var scaled = Math.Round(maxHealth * restoreFraction, MidpointRounding.AwayFromZero);

            if (scaled < 1.0)
                return 1;

            return scaled >= maxHealth ? maxHealth : (uint)scaled;
        }

        /// <summary>
        /// What the save leaves behind: the damage the call site should write, and the health the summoner
        /// ends the hit on.
        /// </summary>
        public readonly struct SaveResult
        {
            /// <summary>What the pre-write hook leaves of the blow. Written to the vital by the call site.</summary>
            public uint DamageAfterSave { get; init; }

            /// <summary>The health the summoner ends on, once the damage above has been written.</summary>
            public uint HealthAfterSave { get; init; }
        }

        /// <summary>
        /// Resolves a fired save into the two numbers the handler needs.
        ///
        /// THE SAVE IS PAID FOR IN DAMAGE WHEREVER IT CAN BE. A summoner above the restore line is brought
        /// DOWN to it by leaving exactly (currentHealth - restore) damage on the blow, so the ordinary
        /// vital write lands them on the restore line and no health is ever conjured. Only a summoner
        /// already BELOW the line needs a heal, and the handler applies exactly the shortfall - which is why
        /// this returns the target health rather than a delta the caller might apply twice.
        ///
        /// That split matters because a save that healed unconditionally would be a net heal on any hit that
        /// found the player near full health, turning a death save into a sustain button.
        /// </summary>
        public static SaveResult Resolve(uint currentHealth, uint maxHealth, double restoreFraction)
        {
            var restore = RestoreAmount(maxHealth, restoreFraction);

            return new SaveResult
            {
                DamageAfterSave = currentHealth > restore ? currentHealth - restore : 0u,
                HealthAfterSave = restore,
            };
        }
    }
}
