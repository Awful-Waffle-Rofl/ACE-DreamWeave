using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3: whenever the player avoids an incoming attack by ANY means - a normal evade (melee or
    /// missile), a class-ability parry or block, or a resisted spell - a 10/20/30% chance by rank to blind
    /// the attacker, lowering all of its attack skills by 30 for 20 seconds.
    ///
    /// FIRES ON ALL AVOIDANCE KINDS (a DECIDED design call). That is what makes it a Rogue T3 rather than a
    /// second Parry rider: it pays out for a dodge-built character, a shield-built one, and a magic-defence
    /// one alike, and it is the reason the trigger is wired at three separate sites (Player.OnEvade,
    /// Player.OnClassAbilityAttackAvoided, WorldObject.TryResistSpell) instead of one.
    ///
    /// IT STACKS WITH RETAIL DIRTY FIGHTING'S BLINDING ASSAULT, and that is engineered, not incidental.
    /// The debuff is written as a hand-made enchantment entry through
    /// EnchantmentManager.AddClassAbilityDebuff in a fork-reserved synthetic SpellCategory
    /// (SpellCategory_ClassAbility_PocketSand). Effective values take the top layer of EACH category and
    /// then SUM across categories, so an entry in a private category adds to Dirty Fighting's
    /// DFAttackSkillDebuff entry instead of duelling it for one slot. Put it in DF's category and it would
    /// silently replace-or-be-replaced depending on power level.
    ///
    /// RE-PROCCING REFRESHES, IT DOES NOT STACK ON ITSELF. AddClassAbilityDebuff keys on
    /// (category, spell id) and resets the existing entry's clock, so a Rogue avoiding ten attacks in a row
    /// holds one 20 second -30, not a growing pile. There is deliberately no cooldown: the refresh IS the
    /// containment, and a cooldown would only add a timer the player cannot see.
    /// </summary>
    public class PocketSandAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.PocketSand,
            AbilityClass = ClassAbilityClass.Rogue,
            Tier = 3,
            Name = "pocketsand",
            DisplayName = "Pocket Sand",
            Description = "Whenever you avoid an attack - evade, parry, block, or resist a spell - you have a " +
                          "10/20/30% chance by rank to fling sand in the attacker's eyes, lowering all of its " +
                          "attack skills by 30 for 20 seconds. Stacks with Dirty Fighting's Blinding Assault.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 2, 3 },
            Implemented = true,
            AffinitySkill = Skill.Deception,
        };

        /// <summary>
        /// The proc chance at a given rank: base + step*(rank-1) plus the CAPPED Deception rider. Pure for
        /// testability, matching SundermarkAbility.Chance / ShieldBlockAbility.BlockChance.
        ///
        /// <paramref name="affinityCap"/> bounds the Deception rider only (class_ability_affinity_chance_cap).
        /// It exists here for the same reason it exists on every other chance-on-hit entry with an affinity:
        /// GetClassAbilityScaling returns a RAW quotient (effectiveSkill / divisor) with no upper bound of
        /// its own, so without the clamp a high Deception saturates this toward a literal certainty - which
        /// on an ability that fires on EVERY avoidance would mean permanent uptime from the first dodge. A
        /// cap of 0 means uncapped. There is no equipment mod for this entry, so no gear term sits outside
        /// the clamp.
        /// </summary>
        public static double Chance(int rank, double chanceBase, double chanceStep, double deceptionFraction, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rider = Math.Max(0.0, deceptionFraction);

            if (affinityCap > 0.0)
                rider = Math.Min(rider, affinityCap);

            return Math.Max(0.0, chanceBase + (rank - 1) * chanceStep + rider);
        }

        /// <summary>
        /// Reports the PROC CHANCE, not the -30 magnitude: the magnitude is rank-invariant, so chance is the
        /// only number rank moves and the only one worth a live readout. Affinity carries the RAW (pre-clamp)
        /// Deception rider while Effective uses the clamped one, matching AcidProcAbility and Shield Block.
        ///
        /// The null-conditional on the scaling read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_pocketsand_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_pocketsand_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var deception = Math.Max(0.0, (player?.GetClassAbilityScaling(Skill.Deception,
                PropertyManager.GetDouble("class_ability_pocketsand_deception_per_trained").Item,
                PropertyManager.GetDouble("class_ability_pocketsand_deception_per_spec").Item) ?? 0.0) * 0.01);

            // Chance() applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && deception > affinityCap;

            var chance = Chance(rank, chanceBase, chanceStep, deception, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rank <= 0 ? 0.0 : (chanceBase + (rank - 1) * chanceStep) * 100.0,
                Affinity = deception * 100.0,   // RAW (pre-clamp), so Total shows what the rider would be uncapped
                Gear = 0.0,
                Effective = chance * 100.0,
                Unit = "%",
                Label = "blind proc",
                Per = null,
                CapNote = affinityCapBites ? "affinity cap" : null,
            };
        }
    }
}
