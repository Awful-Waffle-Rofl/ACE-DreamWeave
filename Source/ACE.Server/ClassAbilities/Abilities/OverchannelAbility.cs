using System;

using ACE.Entity.Enum;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.ClassAbilities.Abilities
{
    /// <summary>
    /// Archmage T2: +9.5% damage per rank on damaging spells of ANY school (+28.5% at rank 3), scaled by
    /// Arcane Lore. No longer single-projectile-only (user, 2026-07-15) - the bonus synergizes with Spell
    /// AOE, carrying into the radiated copies because both the primary and each child route through
    /// SpellProjectile.CalculateDamage (Player.GetClassAbilitySpellDamageMod).
    ///
    /// SCHOOL-AGNOSTIC AS OF 2026-09-13. It was War Magic only until then; the school test came off the
    /// Overchannel block in GetClassAbilitySpellDamageMod, so a damaging projectile takes the bonus
    /// whatever school it belongs to. The Void Damage block in that method is deliberately left as a
    /// SEPARATE branch rather than merged with this one - a caster holding both must get each bonus
    /// exactly once, and merging them would double-apply on a void cast.
    ///
    /// SCHOOL-AGNOSTIC IS NOT SPELL-AGNOSTIC, and holding that distinction is what
    /// <see cref="DamageBonusApplies"/> exists for. The bonus is applied at exactly one site, so it reaches
    /// SpellType.Projectile casts and nothing else - not a buff or a debuff, not Harm, and not the two life
    /// projectiles, which resolve in their own branch and carry no Overchannel term.
    ///
    /// Its resource cost widened with the damage and is gated by that same predicate: a qualifying cast of
    /// any school pays +100% mana (Player.GetClassAbilityManaSurcharge), stacking with Spell AOE's
    /// surcharge to +200% on an Arc cast when both are learned. Bespoke; carries IPassiveStatAbility
    /// because it hooks no combat event of its own.
    /// </summary>
    public class OverchannelAbility : IClassAbility, IPassiveStatAbility, IAbilityReadout
    {
        public ClassAbilityDefinition Definition { get; } = new ClassAbilityDefinition
        {
            Id = ClassAbilityId.Overchannel,
            AbilityClass = ClassAbilityClass.Archmage,
            Tier = 2,
            Name = "overchannel",
            DisplayName = "Overchannel",
            Description = "Increases the damage of your damaging spells, of any school, by 9.5% per rank " +
                          "(+28.5% at rank 3); higher Arcane Lore increases the bonus. Boosted casts cost " +
                          "+100% mana (stacking with Spell AOE to +200% on an Arc cast).",
            MaxRank = 3,
            CostPerRank = new[] { 2, 2, 2 },   // premium reprice 2026-09-13: 1/rank -> 2/rank
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
        };

        /// <summary>
        /// The spell-damage multiplier at a given rank (1.0 = none), any school: 1 + rank*perRank + the Arcane
        /// Lore rider fraction + the equipment-mod term. Pure for testability (mirrors AttackSpeedAbility /
        /// VoidDamageAbility).
        ///
        /// <paramref name="gearModFraction"/> is the Overchannel equipment mod, a STANDALONE mod: same axis,
        /// additive, NOT rank-gated, so at rank 0 this degenerates to 1 + gearModFraction. The ability's own
        /// per-rank base and Arcane Lore rider stay rank-gated. Defaults to 0, which reproduces the
        /// pre-equipment-mod behavior exactly.
        /// </summary>
        public static float DamageMultiplier(int rank, double percentPerRank, double arcaneLoreFraction, double gearModFraction = 0.0)
        {
            var abilityBonus = rank <= 0 ? 0.0 : Math.Max(0, rank) * percentPerRank + Math.Max(0.0, arcaneLoreFraction);
            var gearBonus = Math.Max(0.0, gearModFraction);

            if (abilityBonus <= 0.0 && gearBonus <= 0.0)
                return 1.0f;

            return (float)(1.0 + abilityBonus + gearBonus);
        }

        /// <summary>
        /// Whether Overchannel's damage bonus can reach a cast of this shape - and therefore whether its
        /// mana surcharge may be charged for it. Pure, so the invariant is testable without a live Player.
        ///
        /// DERIVED FROM THE CALL PATH, NOT FROM SCHOOL MEMBERSHIP. <see cref="DamageMultiplier"/> is applied
        /// in exactly one place, Player.GetClassAbilitySpellDamageMod, whose only consumer is the war/void
        /// branch of SpellProjectile.CalculateDamage - the else of
        /// "if (Spell.MetaSpellType == SpellType.LifeProjectile)". SpellType.Projectile is therefore the
        /// whole of what can take the bonus, whatever school the spell belongs to.
        ///
        /// Everything else is excluded because the bonus provably cannot reach it:
        ///  - Enchantment / FellowEnchantment / EnchantmentProjectile - buffs and debuffs: an armor buff, a
        ///    Strength cast, an Imperil. These have no damage roll for the bonus to multiply. An
        ///    EnchantmentProjectile does reach CalculateDamage, but SpellProjectile.OnCollideObject throws
        ///    the damage number away and applies the enchantment instead, so the multiplier is computed and
        ///    discarded;
        ///  - LifeProjectile - Martyr's Hecatomb and Curse of Raven Fury take the OTHER branch of that if,
        ///    which composes Player.ApplyLifeProjectileClassAbilityDamage and carries no Overchannel term;
        ///  - Boost / FellowBoost - Harm and the heals resolve outside SpellProjectile altogether, in
        ///    WorldObject_Magic.HandleCastSpell_Boost over Player.ApplyHarmClassAbilityDamage, which
        ///    likewise carries no Overchannel term;
        ///  - Transfer (Drain), Dispel and the portal types - no damage roll at all.
        ///
        /// LIFE MAGIC IS WHY THIS CANNOT BE WRITTEN AS A SCHOOL TEST. That school holds both heals and
        /// damaging spells, so "which school" answers the wrong question; "which spell shape" answers this
        /// one. Item Enchantment and Creature Enchantment are the schools the missing condition actually
        /// hurt - a cast there can never be a Projectile, so every one of them was being surcharged for a
        /// bonus it could not receive.
        /// </summary>
        public static bool DamageBonusApplies(SpellType metaSpellType)
        {
            return metaSpellType == SpellType.Projectile;
        }

        /// <summary>
        /// Mirrors the rank/affinity/gear terms fed into DamageMultiplier above (x100 for display) - the
        /// two must stay in step.
        /// </summary>
        public ClassAbilityReadout GetReadout(Player player, int rank)
        {
            var rankBonus = rank <= 0 ? 0.0 : rank * PropertyManager.GetDouble("class_ability_overchannel_percent_per_rank").Item;

            // Affinity is a MULTIPLIER on the rank term, reported as the AMOUNT it adds so the three
            // displayed terms stay in one unit and still sum to Effective.
            var multiplier = player.GetClassAbilityAffinityMultiplier(Skill.ArcaneLore);

            var skill = rankBonus * 100.0;

            var affinity = (rankBonus * multiplier - rankBonus) * 100.0;

            var gear = player.GetEquippedModValue(EquipmentModId.Overchannel) * 100.0;

            var total = skill + affinity + gear;

            return new ClassAbilityReadout
            {
                HasValue = true,
                Skill = skill,
                Affinity = affinity,
                Gear = gear,
                Effective = total,
                Unit = "%",
                Label = "spell dmg",
                Per = null,
                CapNote = null,
            };
        }
    }
}
