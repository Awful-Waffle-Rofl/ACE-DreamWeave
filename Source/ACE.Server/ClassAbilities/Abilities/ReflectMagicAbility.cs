using System;

using ACE.Entity.Enum;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Vanguard T3: incoming PROJECTILES of every kind - arrows, crossbow bolts, thrown weapons, war bolts
    /// and void projectiles - have a 6/12/18% chance by rank to bounce straight back at whoever fired them.
    /// The Vanguard takes nothing on a bounce; the projectile resolves against the firer instead.
    ///
    /// PROJECTILES ONLY, AND THAT IS A HARD BOUNDARY, not an omission to be widened later: a melee swing, a
    /// non-projectile spell and a damage-over-time tick are all untouched. Reflecting anything that is not a
    /// projectile would give the entry a second, much larger coverage area on the same roll.
    ///
    /// THE BOUNDARY IS ENFORCED TWICE, on purpose. The two dispatch sites are projectile collision handlers
    /// and reach no melee path at all, which is the structural half; but a call-site list is only correct
    /// until someone adds a site, so <see cref="IsReflectableCombatType"/> re-decides it from the attack's
    /// own CombatType inside Player.TryReflectProjectile. That makes "a melee swing is never reflected" a
    /// pure unit test rather than a property of where two calls happen to sit today.
    ///
    /// NOTE THE NAME ASYMMETRY, which is deliberate and comes from the design table: the enum member is
    /// ReflectMagic and the canonical token is "reflect_magic", but it DISPLAYS to players as "Reflect" -
    /// because it is not magic-specific. Do not "fix" any of the three to match the others.
    ///
    /// BESPOKE, NOT HOOKED, and it carries IPassiveStatAbility for exactly the reason Pocket Sand, Parry and
    /// Shield Block do: the two sites a projectile can reach a player (ProjectileCollisionHelper for arrows
    /// and bolts, SpellProjectile.OnCollideObject for war and void) share no common sink and neither is a
    /// damage hook, so the roll is dispatched by hand from Player.TryReflectProjectile rather than through a
    /// hook bucket. A new IClassAbility hook interface would have to be dispatched from those same two
    /// hand-written calls and would buy nothing. ClassAbilityRegistryTests permits this precisely via the
    /// IPassiveStatAbility marker (an Implemented entry with no hook must carry it).
    ///
    /// THE REFLECT LATCH IS LOAD BEARING HERE - see Creature.TryEnterReflect / ExitReflect in
    /// Creature_MonsterEffects.cs, and the paragraph in ThornsAbility that explains why the loop is real
    /// rather than hypothetical. Reflected damage is applied through the firer's normal TakeDamage, which
    /// runs its monster-effect incoming filters; a monster carrying its own "reflect" effect answers by
    /// damaging the player, whose Thorns answers by damaging the monster, and so on. Player.TryReflectProjectile
    /// claims the latch around the whole application, so the first re-entrant reflect on the thread is
    /// refused and the exchange is bounded at depth 1.
    /// </summary>
    public class ReflectMagicAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.ReflectMagic,
            AbilityClass = ClassAbilityClass.Vanguard,
            Tier = 3,
            Name = "reflect_magic",
            DisplayName = "Reflect",
            Description = "Incoming projectiles of every kind - arrows, bolts, thrown weapons, war bolts, " +
                          "and void projectiles - have a 6/12/18% chance (by rank) to bounce straight back " +
                          "at whoever fired them. You take nothing; the projectile resolves against them " +
                          "instead. Projectiles only. Higher Missile Defense multiplies the chance.",
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },
            Implemented = true,
            AffinitySkill = Skill.MissileDefense,
        };

        /// <summary>
        /// The "projectiles only" rule as a pure decision, so it can be unit tested and so every future
        /// dispatch site inherits it instead of re-deciding it.
        ///
        /// Missile is arrows / bolts / thrown weapons; Magic is a spell PROJECTILE, and the only magic that
        /// reaches the one site that passes it is SpellProjectile.OnCollideObject - a flying, collidable
        /// projectile. Melee is excluded outright and is the case this method exists to refuse: a melee swing
        /// carries no projectile and must never be reflected no matter where this is called from.
        /// </summary>
        public static bool IsReflectableCombatType(CombatType combatType)
        {
            return combatType == CombatType.Missile || combatType == CombatType.Magic;
        }

        /// <summary>
        /// The reflect chance at a given rank: the ability's OWN rank bonus (base + step*(rank-1)) SCALED by
        /// the Missile Defense affinity multiplier, with the added amount CAPPED. Pure for testability,
        /// matching PocketSandAbility.Chance / SundermarkAbility.Chance.
        ///
        /// <paramref name="affinityMultiplier"/> is what
        /// Player.GetClassAbilityAffinityMultiplier(Skill.MissileDefense) returns: a factor >= 1.0 that is
        /// EXACTLY 1.0 at zero effective Missile Defense, leaving the chance bit-identical to rank alone.
        /// Floored at 1.0 here as well, so a caller that hands over 0.0 - the neutral value of the OLD
        /// additive primitive, and an easy mistake because the two have identical shapes - degrades to
        /// rank-only rather than silently multiplying the whole bonus away.
        ///
        /// <paramref name="affinityCap"/> bounds the AMOUNT the affinity multiply ADDS
        /// (rankBonus * multiplier - rankBonus), never the rank ladder and never the factor itself
        /// (class_ability_affinity_chance_cap). It applies here because this IS a proc chance, which is what
        /// that tunable's own doc comment scopes it to, and because the affinity is linear in a skill value
        /// the server does not constrain - without the clamp a high Missile Defense would walk a full
        /// negation of every incoming projectile toward certainty. A cap of 0 means uncapped. There is no
        /// equipment mod for this entry, so no gear term sits outside the clamp.
        /// </summary>
        public static double ReflectChance(int rank, double chanceBase, double chanceStep, double affinityMultiplier, double affinityCap = 0.0)
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
        /// Reports the PROC CHANCE - the only number rank moves, since a bounce is all-or-nothing and carries
        /// no magnitude of its own. Affinity is the CAPPED amount the Missile Defense multiply ADDS (clamped
        /// the same way ReflectChance clamps it), so Skill + Affinity + Gear still sums to Effective exactly,
        /// which ClassAbilityAffinityCapReadoutInvariantTests pins for this readout family.
        ///
        /// The null-conditional on the affinity read is for the readout unit tests, which call this with a
        /// null Player because Player's static initializer cannot run under the test host - and it falls
        /// back to 1.0, the NEUTRAL factor, never 0.0.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var chanceBase = PropertyManager.GetDouble("class_ability_reflectmagic_chance_base").Item;
            var chanceStep = PropertyManager.GetDouble("class_ability_reflectmagic_chance_step").Item;
            var affinityCap = PropertyManager.GetDouble("class_ability_affinity_chance_cap").Item;

            var rankBonus = rank <= 0 ? 0.0 : chanceBase + (rank - 1) * chanceStep;

            // NEUTRAL IS 1.0, NOT 0.0. The null-Player fallback must be the identity factor: 0.0 would not
            // omit the rider, it would multiply this ability's entire rank bonus away.
            var multiplier = player?.GetClassAbilityAffinityMultiplier(Skill.MissileDefense) ?? 1.0;

            // The RAW (pre-clamp) amount the multiply adds. This is the quantity the affinity cap bounds,
            // so it is also the quantity the cap must be compared against.
            var rawAdded = Math.Max(0.0, rankBonus * multiplier - rankBonus);

            // ReflectChance applies the clamp itself; this only records whether it bit, for CapNote.
            var affinityCapBites = affinityCap > 0.0 && rawAdded > affinityCap;

            // The CAPPED added amount, so Affinity below matches what ReflectChance actually folds in.
            var clampedAdded = affinityCap > 0.0 ? Math.Min(rawAdded, affinityCap) : rawAdded;

            var chance = ReflectChance(rank, chanceBase, chanceStep, multiplier, affinityCap);

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = rankBonus * 100.0,
                Affinity = clampedAdded * 100.0,
                Gear = 0.0,
                Effective = chance * 100.0,
                Unit = "%",
                Label = "projectile reflect",
                Per = null,
                CapNote = affinityCapBites ? "affinity cap" : null,
            };
        }
    }
}
