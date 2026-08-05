using System.Collections.Generic;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Network.Enum;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Player-facing text for LIFE ("blood") casters, and the suppression of the client lines it replaces.
    ///
    /// WHY THIS EXISTS. The client composes two of its appraisal lines by looking a damage type up in a
    /// name table it owns, and that table has no entry for DamageType.Health (128):
    ///
    ///     "Properties: Attuned, Bonded, Resistance Cleaving: , Ivoryable"   <- blank after the colon
    ///     "Damage bonus for  spells:"                                        <- blank before "spells"
    ///
    /// Both were confirmed live on wcid 1000241. The values themselves arrive intact (the +8.0%/+4.0%
    /// figures render fine) - only the noun is missing, because the name comes from the client and
    /// ACE.DatLoader is read-only. W_DamageType cannot be changed to something the client knows, because
    /// GetCasterElementalDamageModifier gates on `weapon.W_DamageType != damageType` and Hecatomb's
    /// Spell.DamageType is Health - relabel it and the caster silently stops working.
    ///
    /// So the fix is the one this codebase already uses for custom mechanics: DO NOT SEND the properties
    /// whose only job is to make the client draw those lines, and draw our own in the "Property Details:"
    /// block instead. Same approach as the 8130-8135 weapon-mod records, which "carry no
    /// [AssessmentProperty] and are never sent" - except these are native properties, so the suppression
    /// has to happen per-item in AppraiseInfo rather than by omitting an attribute.
    ///
    /// THE SERVER STILL READS THEM. Suppression is display-only: ResistanceModifierType and
    /// ElementalDamageMod stay on the weenie and keep driving GetWeaponResistanceModifier and
    /// GetCasterElementalDamageModifier exactly as before. Nothing here touches the mechanic.
    /// </summary>
    public static class LifeCasterDisplay
    {
        /// <summary>
        /// True when this item is the kind of caster whose appraisal the client cannot label - i.e. it
        /// declares Health as its damage type. Everything below keys off this one test.
        /// </summary>
        public static bool IsLifeCaster(WorldObject wo)
        {
            return wo != null && wo.W_DamageType == DamageType.Health;
        }

        /// <summary>
        /// The "Property Details:" lines for a life caster. Deliberately terse, matching the client's own
        /// register rather than flavor text: the block sits directly under the client's "Properties:" line
        /// and should read as a continuation of it.
        /// </summary>
        public static IEnumerable<string> GetAppraisalLines(WorldObject wo)
        {
            var lines = new List<string>();

            if (!IsLifeCaster(wo))
                return lines;

            // fixed-value cleave (the ResistanceModifierType route)
            if (wo.ResistanceModifierType == DamageType.Health && (wo.ResistanceModifier ?? 0) > 0)
                lines.Add("- Resistance Cleaving: Life");

            // skill-scaled cleave (the ImbuedEffectType route). Named "Life Rending" to match the elemental
            // rends' own naming, and listed separately because the two are combined by Math.Max in
            // GetWeaponResistanceModifier - an item carrying both gains nothing from the weaker one.
            if (wo.GetImbuedEffects().HasFlag(ImbuedEffectType.HealthRending))
                lines.Add("- Life Rending");

            // The conservative damage multiplier.
            //
            // Read LIVE and enchantment-inclusive, so this line says what the client's own suppressed line
            // would have said:
            //   - Green Garnet tinkering writes +0.01 per application straight onto the property
            //     (WeaponTinkerMaterial, WeaponClass.Caster), so tinkered gains appear here with no extra work;
            //   - wielder/weapon enchantments are ADDITIVE on top and are what AppraiseInfo folds in at
            //     AddPropertyEnchantments before sending, so they are added here for the same reason.
            // Both are also exactly what GetCasterElementalDamageModifier reads at damage time, so the number
            // shown and the number applied cannot drift.
            var enchantmentBonus = ResistMaskHelper.GetElementalDamageBonus(wo);
            var elementalDamageMod = (wo.ElementalDamageMod ?? 1.0) + enchantmentBonus;

            if (elementalDamageMod > 1.0)
            {
                var vsMonsters = (elementalDamageMod - 1.0) * 100.0;
                var vsPlayers = vsMonsters * WorldObject.ElementalDamageBonusPvPReduction;

                // NO GREEN HIGHLIGHT AVAILABLE HERE. The client colours a buffed line from the ResistColor /
                // ResistHighlight masks, and those address NATIVE property lines by property id - this text
                // lives in PropertyString.Use, which the client draws as plain text. Since the whole point of
                // this block is that the client cannot label DamageType.Health, sending the native property
                // back just to earn the colour would restore the blank "Damage bonus for  spells" line.
                // So the enchantment is called out in TEXT instead: the information the colour would have
                // carried, in the one channel we control.
                var suffix = enchantmentBonus != 0
                    ? $" (includes +{enchantmentBonus * 100.0:0.#}% from enchantments)"
                    : string.Empty;

                lines.Add($"- Damage bonus for Life: +{vsMonsters:0.#}% vs. monsters, +{vsPlayers:0.#}% vs. players{suffix}");
            }

            return lines;
        }

        /// <summary>
        /// Removes the properties whose ONLY effect on a life caster is to make the client draw a line it
        /// cannot fill in. Called from AppraiseInfo after the property dictionaries are built and before
        /// they are sent; the item's stored values are untouched.
        /// </summary>
        public static void SuppressUnlabelledClientLines(WorldObject wo,
            Dictionary<PropertyInt, int> propertiesInt, Dictionary<PropertyFloat, double> propertiesFloat)
        {
            if (!IsLifeCaster(wo))
                return;

            // "Properties: ... Resistance Cleaving: , ..." - the client prints the label from this int alone
            propertiesInt?.Remove(PropertyInt.ResistanceModifierType);

            // "Damage bonus for  spells:" - the whole block hangs off this float
            propertiesFloat?.Remove(PropertyFloat.ElementalDamageMod);
        }
    }
}
