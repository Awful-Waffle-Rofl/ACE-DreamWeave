using System;

using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Spellsword T1: landing ANY magic damage grants a Resonance stack (cap 5, 6 second idle window), and
    /// each stack adds +2/3/4% by rank to ALL of the player's magic damage. Full ramp +10/15/20%.
    /// SPELLSWORD-DESIGN.md sec 5d.
    ///
    /// WHY THIS CARRIES NO HOOK INTERFACE OF ITS OWN. The stack pool lives on the Player
    /// (Player_ClassAbilityBuffs.cs) and is read at the magic damage sites, exactly like Withering's void-DoT
    /// multiplier: there is no combat event this handler needs to be dispatched for, so it takes
    /// IPassiveStatAbility and is exempt from the "Implemented abilities must hook something" rule (see the
    /// doc comment on IPassiveStatAbility in ClassAbilityHooks.cs). Both halves of the mechanic - the grant
    /// and the read - happen at the same three damage sites, so a hook would only add indirection.
    ///
    /// THE SURFACE IS "ALL MAGIC DAMAGE", AND THAT IS SCHOOL-AGNOSTIC BY DESIGN (sec 5d). It is the entry
    /// that makes Spellsword a magic class rather than a melee class with sparks: a Spellsword who splashes
    /// Archmage builds stacks with their own war casts and gets the bonus on both their casts and their
    /// procs, and an Archmage who splashes Spellsword T1 gets a ramp they can hold while meleeing. War, void
    /// and life all qualify. Melee and missile damage does not - the trigger and the bonus are both
    /// spell-sourced.
    ///
    /// BLOOD PRICE IS THE PRECEDENT FOR THE WIRING, not a coincidence of shape. Blood Price is the other
    /// school-agnostic magic damage multiplier in the catalog, and because there is no single choke point for
    /// magic damage it is applied at THREE disjoint sites. Resonance rides the same three, for the same
    /// reason - see the notes on Player.GetResonanceMagicDamageMod.
    ///
    /// POWER NOTE (sec 5d). This is a general magic-damage multiplier rather than a proc-axis one, so it
    /// multiplies with Archmage's Overchannel, Void Damage and the rest. It belongs on the magic damage axis
    /// in POWER-LEDGER.md, and the worst-case cross-class table needs a Spellsword-T1 term.
    /// </summary>
    public class ResonanceAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Resonance,
            AbilityClass = ClassAbilityClass.Spellsword,
            Tier = 1,
            Name = "resonance",
            DisplayName = "Resonance",
            Description = "Landing any magic damage grants a Resonance stack (up to 5, lapsing after 6 seconds " +
                          "without a hit). Each stack adds 2/3/4% by rank to all of your magic damage - " +
                          "+10/15/20% at a full stack.",
            MaxRank = 3,
            CostPerRank = new[] { 3, 3, 3 },
            Implemented = true,
        };

        /// <summary>
        /// The magic-damage multiplier for a given rank and stack count (1.0 = none):
        /// 1 + stacks * perStack(rank). Pure for testability, matching the other stacking handlers
        /// (FrenzyAbility.AttackSpeedMultiplier, NetherRushAbility.CastSpeedMultiplier).
        ///
        /// Returns 1.0 for rank &lt;= 0 or stacks &lt;= 0, so a caller that has not looked up ownership still
        /// gets the inert answer. The caller owns clamping stacks to the cap.
        ///
        /// <paramref name="gearPerStack"/> is the HARMONICS equipment mod (EquipmentModId.Harmonics -
        /// named Harmonics rather than Resonance only because EquipmentModId.Resonance was already taken by
        /// the Archmage Spell AOE mod; see EquipmentModRegistry). It is a MACHINERY mod added to the
        /// PER-STACK rate INSIDE the ramp - 1.0 + stacks * (perStack + gear) - so it rides the same stack
        /// count the ability's own rate does rather than becoming a separate factor (DESIGN.md 3.3).
        /// Last and defaulted to 0, and adding 0.0 is exact in IEEE arithmetic, so an unmodded build
        /// reproduces the previous multiplier bit-for-bit.
        ///
        /// The "no bonus at all" test is on the SUM, following BloodChargeMath.DamageMultiplier: gear must
        /// still pay out if the ability's own tunable is ever set to zero.
        /// </summary>
        public static float DamageMultiplier(int rank, int stacks, double perStackR1, double perStackR2, double perStackR3, double gearPerStack = 0.0)
        {
            if (rank <= 0 || stacks <= 0)
                return 1.0f;

            var perStack = PerStack(rank, perStackR1, perStackR2, perStackR3) + Math.Max(0.0, gearPerStack);

            if (perStack <= 0.0)
                return 1.0f;

            return (float)(1.0 + stacks * perStack);
        }

        /// <summary>
        /// The per-stack magic-damage fraction at a given rank: r1/r2/r3 tunables (0.02 / 0.03 / 0.04).
        /// Ranks above 3 clamp to the rank-3 value rather than extrapolating, which is the same shape
        /// BloodChargeMath.StackCap uses - a rank the ladder does not define must not silently invent one.
        /// </summary>
        public static double PerStack(int rank, double perStackR1, double perStackR2, double perStackR3)
        {
            if (rank <= 0)
                return 0.0;

            if (rank == 1)
                return Math.Max(0.0, perStackR1);

            if (rank == 2)
                return Math.Max(0.0, perStackR2);

            return Math.Max(0.0, perStackR3);
        }

        /// <summary>
        /// Reports the PER-STACK magic-damage rate - Skill from PerStack() above, no affinity rider, Gear
        /// from the HARMONICS mod, which is added to that same per-stack rate inside the ramp
        /// (DamageMultiplier) so the two sum in one unit. The 5-stack cap bounds how many stacks a player
        /// can hold, not the per-stack rate itself, so it never reduces this readout and CapNote is always
        /// null.
        ///
        /// PER-STACK, NOT THE WHOLE RAMP - Per is "/stack" and the reader multiplies mentally by the pool.
        ///
        /// The null-conditional on the gear read is for the readout unit tests, which call this with a null
        /// Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var perStack = PerStack(rank,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r1").Item,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r2").Item,
                PropertyManager.GetDouble("class_ability_resonance_per_stack_r3").Item);

            var gearPerStack = rank <= 0
                ? 0.0
                : Math.Max(0.0, player?.GetEquippedModValue(EquipmentModId.Harmonics) ?? 0.0);

            var skill = perStack * 100.0;
            var gear = gearPerStack * 100.0;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = 0.0,
                Gear = gear,
                Effective = skill + gear,
                Unit = "%",
                Label = "magic dmg",
                Per = "/stack",
                CapNote = null,
            };
        }
    }
}
