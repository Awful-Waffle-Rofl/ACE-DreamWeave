using System;
using System.Collections.Generic;

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
            DescriptionTemplate = "Your damaging spells from any school deal more damage. Boosted casts cost " +
                          "+{v:class_ability_overchannel_mana_surcharge|pct}% mana; with Spell AOE, an Arc cast costs up to " +
                          "+{fn:arc_surcharge@max|pct}%.",
            // Live text: bonus = rank * class_ability_overchannel_percent_per_rank (GetReadout below, Player_ClassAbilityBuffs.cs:560-561);
            // surcharge = class_ability_overchannel_mana_surcharge. "+28.5% at rank 3" is the top rank's value (@max). "+200%" is the
            // Overchannel surcharge plus Spell AOE's on an Arc cast: arc_surcharge is ManaSurcharge, the method
            // Player.GetClassAbilityManaSurcharge calls (rank independent, so the description selects @max).
            RankTailTemplate = "+{per:class_ability_overchannel_percent_per_rank|pct}% spell damage",
            TextFormulas = new Dictionary<string, TextFormula>
            {
                ["arc_surcharge"] = new TextFormula(
                    (values, rank) => ManaSurcharge(values, true, true),
                    "class_ability_overchannel_mana_surcharge",
                    "class_ability_spellaoe_mana_surcharge"),
            },
            TunableSourceFiles = new[] { "WorldObjects/Player_ClassAbilityBuffs.cs" },
            MaxRank = 3,
            CostPerRank = new[] { 1, 1, 1 },   // Archmage pass 2026-10-24 (reprice wave 6): 2/rank -> 1/rank
            Implemented = true,
            AffinitySkill = Skill.ArcaneLore,
        };

        /// <summary>
        /// The mana surcharge FRACTION a cast pays (0.0 = none; 1.0 = +100%): Overchannel's own surcharge when
        /// <paramref name="overchannelApplies"/> plus Spell AOE's when <paramref name="spellAoeApplies"/>, floored at
        /// 0. Player.GetClassAbilityManaSurcharge decides the two flags (ownership and cast shape) and calls this
        /// with <see cref="ClassAbilityText.Live"/>; the live text's arc_surcharge formula passes both true, so the
        /// "+200% on an Arc cast" line is the very sum the mechanic charges. Each key is read only when its flag is set.
        /// </summary>
        public static double ManaSurcharge(ITunableValues values, bool overchannelApplies, bool spellAoeApplies)
        {
            var surcharge = 0.0;

            if (overchannelApplies)
                surcharge += values.GetDouble("class_ability_overchannel_mana_surcharge");

            if (spellAoeApplies)
                surcharge += values.GetDouble("class_ability_spellaoe_mana_surcharge");

            return Math.Max(0.0, surcharge);
        }

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
