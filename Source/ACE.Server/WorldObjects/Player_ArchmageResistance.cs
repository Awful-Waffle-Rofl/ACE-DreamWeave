using System;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// The Player half of the three Archmage pass abilities (2026-10-24): Elemental Inversion and Arcane Hollow,
    /// which hand the resistance lookup a replacement INNATE term, and Volatile Wall, which acts in the war/void
    /// damage branch. The arithmetic lives in ArchmageResistanceMath and the handlers as pure statics; this file
    /// only reads the ranks, the tunables and the affinities and calls them. All three are PvE only through
    /// ArchmageResistanceMath.IsPveTarget, and a suppressed player (PK facet, arena mask) reads rank 0 through
    /// TryGetClassAbility.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// Elemental Inversion and (for magic damage) Arcane Hollow together: the innate value the target's
        /// resistance lookup for <paramref name="originalType"/> should use, or null to leave the lookup alone.
        /// Pass the result as GetResistanceMod's innateOverride. The damage type, the enchantment term and
        /// everything else stay those of the original type.
        ///
        /// The decision itself is the pure ArchmageResistanceMath.Decide: null for a non-PvE target, a type
        /// outside ArchmageResistanceMath.InnateResistTypes or an immune original type, and in each of those
        /// cases the roll is never drawn. Rolls Elemental Inversion at most once per call, so each site calls
        /// this exactly once per hit. <paramref name="magicDamage"/> is TRUE only at the war/void spell site; a
        /// weapon hit passes FALSE and so never takes Arcane Hollow.
        ///
        /// Elemental Proficiency Pierce (instant, that spell only, magic damage only) is decided HERE, once per hit:
        /// <paramref name="pierceShape"/> reports whether this hit took the resistance ignore (already folded into
        /// the returned override) or the flat bonus (which SpellProjectile applies at the final-damage site through
        /// GetElementalProficiencyDamageMod), whichever gives more damage (owner ruling 2026-11-02). None for every
        /// non-pierce hit, a weapon hit, no rank, and an immune target.
        /// </summary>
        public double? GetArchmageInnateOverride(Creature target, ResistanceType originalType, bool magicDamage,
            out ElementalProficiencySpellMath.PierceShape pierceShape)
        {
            pierceShape = ElementalProficiencySpellMath.PierceShape.None;

            // cheap early outs ahead of the rank reads; Decide re-checks both
            if (!ArchmageResistanceMath.IsPveTarget(target) || !ArchmageResistanceMath.IsCovered(originalType))
                return null;

            var chance = 0.0;
            if (TryGetClassAbility(ClassAbilityId.ElementalInversion, out var inversionRank))
            {
                chance = ElementalInversionAbility.ProcChance(inversionRank,
                    PropertyManager.GetDouble("class_ability_elementalinversion_chance_per_rank").Item,
                    GetClassAbilityAffinityMultiplier(Skill.ArcaneLore));
            }

            var hollow = 0.0;
            if (magicDamage && TryGetClassAbility(ClassAbilityId.ArcaneHollow, out var hollowRank))
            {
                hollow = ArcaneHollowAbility.IgnoreFraction(hollowRank,
                    PropertyManager.GetDouble("class_ability_arcanehollow_ignore_per_rank").Item,
                    GetClassAbilityAffinityMultiplier(Skill.MagicItemTinkering));
            }

            Func<double> roll = () => ThreadSafeRandom.Next(0.0f, 1.0f);
            Func<ResistanceType, double> innate = type => ArchmageResistanceMath.InnateResistance(target, type);

            // Elemental Proficiency Pierce: the larger of the ignore (folded into Hollow's fraction as
            // 1 - (1 - h)(1 - p)) and the flat x(1 + p), judged against the innate value after Inversion. Still
            // exactly one override call and at most one Inversion roll per hit.
            var pierce = magicDamage ? GetElementalProficiencyPierceMagnitude(originalType, target) : 0.0;
            if (pierce > 0.0)
                return ElementalProficiencySpellMath.DecideSpellInnate(ArchmageResistanceMath.IsPveTarget(target), originalType,
                    chance, hollow, pierce, roll, innate, out pierceShape);

            return ArchmageResistanceMath.Decide(ArchmageResistanceMath.IsPveTarget(target), originalType, magicDamage,
                chance, hollow, roll, innate);
        }

        /// <summary>The weapon-site form: no Pierce shape to report (Pierce is spell damage only).</summary>
        public double? GetArchmageInnateOverride(Creature target, ResistanceType originalType, bool magicDamage) =>
            GetArchmageInnateOverride(target, originalType, magicDamage, out _);

        /// <summary>
        /// Volatile Wall: the damage multiplier for this landed wall projectile, then records the hit on the
        /// cast's shared counter. Called once per landed projectile from the PvE block of the war/void branch of
        /// SpellProjectile.CalculateDamage, and gated on ArchmageResistanceMath.IsPveTarget here as well. The
        /// counter advances whether or not the player still owns the ability, so the count stays a true count
        /// of the cast's landed PvE hits; the bonus applies only while the ability is owned. 1.0 for a
        /// projectile with no counter (not a player wall cast).
        /// </summary>
        public float ApplyVolatileWall(SpellProjectile projectile, Creature target)
        {
            if (!ArchmageResistanceMath.IsPveTarget(target))
                return 1.0f;

            var counter = projectile?.VolatileWallCounter;

            if (counter == null)
                return 1.0f;

            var prior = counter.RecordLanded();

            if (!TryGetClassAbility(ClassAbilityId.VolatileWall, out var rank))
                return 1.0f;

            var bonus = VolatileWallAbility.PerProjectileBonus(rank,
                PropertyManager.GetDouble(VolatileWallAbility.BonusKey(rank)).Item,
                GetClassAbilityAffinityMultiplier(Skill.WarMagic));

            return VolatileWallAbility.DamageMultiplier(prior, bonus);
        }
    }
}
