using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Rogue T3: whenever the player avoids an incoming attack by ANY means - a normal evade (melee or
    /// missile), a class-ability parry or block, or a resisted spell - a 15/30/45% chance by rank to blind
    /// the attacker, lowering all of its attack skills by the tunable magnitude (class_ability_pocketsand_
    /// magnitude, default 40) for 20 seconds.
    ///
    /// SNEAK ATTACK RIDER (added 2026-09-13, user ruling): while a target carries this blind, Sneak Attack
    /// against it fires 100% of the time, from ANY attacker with Sneak Attack trained - not only the Rogue
    /// who threw the sand. This is wired at the READ side, not the write side: Creature.IsBlindedByPocketSand
    /// (Creature_ClassAbilityDebuffs.cs) checks for any live entry in the shared synthetic category
    /// regardless of caster, and Creature_Combat.GetSneakAttackMod folds that flag into the same 100% branch
    /// as attacking from behind (and skips the Assess Person front-reduction the same way behind does).
    /// Nothing here writes the debuff differently for this rider - it reads the same enchantment entry
    /// OpportunistAbility.TargetHasQualifyingMark already reads, just without that helper's caster filter.
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
    /// holds one 20 second debuff at the tunable magnitude (default -40), not a growing pile. There is
    /// deliberately no cooldown: the refresh IS the containment, and a cooldown would only add a timer the
    /// player cannot see.
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
                          "15/30/45% chance by rank to fling sand in the attacker's eyes, lowering all of its " +
                          "attack skills by 40 for 20 seconds. While it is blinded, every attack against it by " +
                          "someone with Sneak Attack trained is a Sneak Attack. Stacks with Dirty Fighting's " +
                          "Blinding Assault.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.Deception,
        };

        /// <summary>
        /// The proc chance at a given rank: the ability's OWN rank bonus (base + step*(rank-1)) SCALED by
        /// the Deception affinity multiplier, with the added amount CAPPED. Pure for testability, matching
        /// SundermarkAbility.Chance / ShieldBlockAbility.BlockChance.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.Deception) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Deception, leaving the chance bit-identical to rank alone. Floored
        /// at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD additive
        /// primitive, and an easy mistake because the two have identical shapes - degrades to rank-only
        /// rather than silently multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself
        /// (class_ability_affinity_chance_cap). It exists here for the same reason it exists on every other
        /// chance-on-hit entry with an affinity: the affinity is linear in a skill value the server does not
        /// constrain, so without the clamp a high Deception saturates this toward a literal certainty -
        /// which on an ability that fires on EVERY avoidance would mean permanent uptime from the first
        /// dodge. A cap of 0 means uncapped. There is no equipment mod for this entry, so no gear term sits
        /// outside the clamp.
        /// </summary>
        public static double Chance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0)
        {
            if (rank <= 0)
                return 0.0;

            var rankBonus = chanceBase + (rank - 1) * chanceStep;

            // The multiplier scales this ability's OWN rank bonus. What the cap bounds is the AMOUNT that
            // multiply ADDS, not the factor itself - the factor is a bare number like 1.51 and clamping it
            // against a tunable expressed in percentage points would be a unit error.
            var added = rankBonus * Math.Max(1.0, affinityMultiplier) - rankBonus;

            if (affinityCap > 0.0)
                added = Math.Min(added, affinityCap);

            return Math.Max(0.0, rankBonus + added);
        }

        /// <summary>
        /// Reports the PROC CHANCE, not the -40 magnitude: the magnitude is rank-invariant, so chance is the
        /// only number rank moves and the only one worth a live readout. Affinity is the CAPPED amount the
        /// Deception multiply ADDS (clamped the same way Chance() clamps it), so the three displayed terms
        /// still sum to Effective. Reporting the added amount rather than the bare factor keeps all three
        /// displayed terms in one unit.
        ///
        /// The null-conditional on the affinity read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host - and it falls
        /// back to 1.0, the NEUTRAL factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_pocketsand_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_pocketsand_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // NEUTRAL IS 1.0, NOT 0.0. The null-Player fallback must be the identity factor: 0.0 would not
            // omit the rider, it would multiply this ability's entire rank bonus away.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.Deception) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds,
            // so it is also the quantity the cap must be compared against.
            var deception = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // Chance() applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && deception > affinityCap;

            // The CAPPED added amount, so Affinity below matches what Chance() actually folds in.
            var clampedDeception = affinityCap > 0.0 ? Math.Min(deception, affinityCap) : deception;

            var chance = Chance(rank, chanceBase, chanceStep, multiplier, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = clampedDeception * 100.0,
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
