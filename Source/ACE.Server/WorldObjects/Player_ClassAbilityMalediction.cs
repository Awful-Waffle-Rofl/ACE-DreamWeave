using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Entity;
using ACE.Server.EquipmentMods;
using ACE.Server.Managers;

namespace ACE.Server.WorldObjects
{
    /// <summary>
    /// Malediction (Blood Mage T2) - the Player-side half of the bespoke integration. It lives in its own
    /// partial rather than in Player_ClassAbilityBuffs.cs because the effect is not a hook: it is read from
    /// EnchantmentManager while an enchantment registry entry is being built, not from a combat callback.
    /// The arithmetic is in MaledictionAbility; this only resolves rank + tunables + the Life Magic rider.
    /// </summary>
    partial class Player
    {
        /// <summary>
        /// The Malediction multipliers this player applies to a spell it is about to enchant with, as
        /// FRACTIONS (0.10 = +10% intensity, 0.25 = 25% longer). Returns FALSE - and both bonuses 0 - when
        /// the player has not learned Malediction, when class abilities are off, or when the spell is not a
        /// Vulnerability / Imperil, so the caller can leave the entry exactly as it was.
        ///
        /// Called once per registry entry built, not per damage event: the boosted value is what lands in
        /// the registry, so every attacker on that target reads it, which is the whole point of the ability.
        /// </summary>
        public bool TryGetMaledictionMods(Spell spell, out double intensityBonus, out double durationBonus)
        {
            intensityBonus = 0.0;
            durationBonus = 0.0;

            if (spell == null)
                return false;

            if (!TryGetClassAbility(ClassAbilityId.Malediction, out var rank))
                return false;

            if (!MaledictionAbility.AffectsSpell(spell.School, spell.StatModType, spell.StatModKey, spell.StatModVal))
                return false;

            var intensityBase = PropertyManager.GetDouble("class_ability_malediction_intensity_base").Item;
            var intensityStep = PropertyManager.GetDouble("class_ability_malediction_intensity_step").Item;

            var rankBonus = rank <= 0 ? 0.0 : intensityBase + (rank - 1) * intensityStep;

            // Creature Enchantment is MULTIPLICATIVE on this ability's own intensity rank bonus (2026-09-12
            // overhaul, shared 0.12 Trained / 0.17 Specialized rate pair) rather than a per-ability divisor.
            // IntensityBonus's fraction parameter stays additive in its own units, so this feeds it the
            // AMOUNT the multiplier adds (rankBonus * affinity - rankBonus), not the multiplier itself.
            // Untrained/Inactive Creature Enchantment contributes nothing, via the Player wrapper's neutral
            // 1.0 return.
            var affinity = GetClassAbilityAffinityMultiplier(Skill.CreatureEnchantment);
            var creatureEnchantment = rankBonus * affinity - rankBonus;

            // DEEPEN (machinery on Malediction, proven owned by the TryGetClassAbility gate above) - a
            // fourth additive summand inside the intensity expression, never a factor on the result, and
            // OUTSIDE the affinity multiply.
            //
            // ONLY THE INTENSITY. durationBonus below deliberately takes no gear term: the mod's registry
            // row is "+{0}pp debuff intensity" and nothing prices a duration extension here.
            intensityBonus = MaledictionAbility.IntensityBonus(rank,
                intensityBase,
                intensityStep,
                creatureEnchantment,
                GetEquippedModValue(EquipmentModId.Deepen));

            durationBonus = PropertyManager.GetDouble("class_ability_malediction_duration_bonus").Item;

            return true;
        }
    }
}
