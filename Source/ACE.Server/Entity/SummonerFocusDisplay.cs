using System;
using System.Collections.Generic;

using ACE.Entity.Enum.Properties;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Entity
{
    /// <summary>
    /// Player-facing text for Summoner focus casters (Docs/SummonerCasters/DESIGN.md). The client has no label for the
    /// trade (the focus's rolled damage bonus goes to the wielder's pets and the wielder's own damage is cut), and a
    /// client-drawn "Damage bonus" line would imply the wielder benefits, so the native float is not sent and the trade
    /// is drawn as "Property Details" lines instead, the same approach as <see cref="LifeCasterDisplay"/>. Display only:
    /// the stored ElementalDamageMod stays on the item and keeps driving <see cref="SummonerFocus"/>.
    /// </summary>
    public static class SummonerFocusDisplay
    {
        /// <summary>
        /// The "Property Details:" lines for a focus; empty for anything else. The numbers are the live pet multipliers
        /// from <see cref="SummonerFocusMath"/> and the live tunables, on the stored damage mod plus item enchantments
        /// (the same source LifeCasterDisplay uses), so the text cannot drift from what the hit sites apply.
        /// </summary>
        public static IEnumerable<string> GetAppraisalLines(WorldObject wo)
        {
            var lines = new List<string>();

            if (!SummonerFocus.IsSummonerFocus(wo))
                return lines;

            var elementalDamageMod = DisplayElementalDamageMod(wo);

            var dealt = SummonerFocusMath.DealtMultiplier(elementalDamageMod, SummonerFocus.PetDamageScale());
            var taken = SummonerFocusMath.TakenMultiplier(elementalDamageMod, SummonerFocus.PetDefenseScale(), SummonerFocus.PetDefenseFloor());

            // the "take Y% less damage" clause only when the ROUNDED percentage is above 0, so a tiny scale never prints "take 0% less damage"
            if (Math.Round((1.0 - taken) * 100.0, 1) > 0.0)
                lines.Add($"- Summoner Focus: your summons deal +{(dealt - 1.0) * 100.0:0.#}% damage and take {(1.0 - taken) * 100.0:0.#}% less damage while this is wielded");
            else
                lines.Add($"- Summoner Focus: your summons deal +{(dealt - 1.0) * 100.0:0.#}% damage while this is wielded");
            lines.Add($"- Your own damage is {(1.0 - SummonerFocus.WielderDamageModSetting()) * 100.0:0.#}% lower while this is wielded");

            // an imbue is not a weapon mod, so its transfer (and this line) does not follow the weapon-mod gate
            var imbueSuffix = StrengthSuffix(SummonerFocus.PetImbueTransferPercent());

            foreach (var imbue in SummonerFocus.PetImbues)
            {
                if (wo.HasImbuedEffect(imbue))
                    lines.Add($"- Your summons share this focus's {PrismaticDriftStone.GetRendName(imbue)}{imbueSuffix}");
            }

            return lines;
        }

        /// <summary>" (at N% strength)" for a transfer share below 1.0, N rounded to a whole number; empty at the full share.</summary>
        internal static string StrengthSuffix(double share)
        {
            // decided on the shown whole number, so a share that rounds to 100 never prints "(at 100% strength)"
            var percent = Math.Round(share * 100.0, MidpointRounding.AwayFromZero);

            return percent < 100.0 ? $" (at {percent:0}% strength)" : "";
        }

        /// <summary>
        /// The damage mod the pet numbers are computed from: the very sum combat uses
        /// (<see cref="WorldObject.SumCasterElementalDamageMod"/>: stored mod, wielder and weapon enchantments, Attunement),
        /// so the text cannot drift from the hit. An unwielded focus reads with no wielder.
        /// </summary>
        internal static double DisplayElementalDamageMod(WorldObject focus) =>
            WorldObject.SumCasterElementalDamageMod(focus, focus.Wielder as Creature);

        /// <summary>
        /// The route tag a weapon-mod appraisal line carries on a Summoner focus, inserted between the mod's display name
        /// and its "[NN%]" bracket: " (Summon)" for a mod only the pets read, " (You + Summon)" for one both read. A transfer
        /// share below 100% (summoner_pet_mod_transfer_percent) folds in as " (Summon 50%)". Empty for a Wielder-route mod,
        /// for any item that is not a focus, and for a null definition. Gated by the caller's weapon_mods_enabled block,
        /// the same switch the old route lines rode (WeaponModRegistry.Enabled reads weapon_mods_enabled).
        /// </summary>
        public static string RouteTag(WorldObject item, WeaponModDefinition definition)
        {
            if (definition == null || !SummonerFocus.IsSummonerFocus(item))
                return string.Empty;

            string label;

            switch (SummonerFocus.PetRoute(definition))
            {
                case SummonerPetRoute.PetsOnly:
                    label = "Summon";
                    break;
                case SummonerPetRoute.Shared:
                    label = "You + Summon";
                    break;
                default:
                    return string.Empty;
            }

            // decided on the shown whole number, so a share that rounds to 100 never prints " 100%"
            var percent = Math.Round(SummonerFocus.PetModTransferPercent() * 100.0, MidpointRounding.AwayFromZero);

            return percent < 100.0 ? $" ({label} {percent:0}%)" : $" ({label})";
        }

        /// <summary>
        /// Removes the native ElementalDamageMod from a focus's appraisal, so the client never draws a "Damage bonus" line
        /// implying the wielder benefits. Called from AppraiseInfo after the property dictionaries are built; the item's
        /// stored value is untouched.
        /// </summary>
        public static void SuppressClientDamageBonusLine(WorldObject wo, Dictionary<PropertyFloat, double> propertiesFloat)
        {
            if (!SummonerFocus.IsSummonerFocus(wo))
                return;

            propertiesFloat?.Remove(PropertyFloat.ElementalDamageMod);
        }
    }
}
