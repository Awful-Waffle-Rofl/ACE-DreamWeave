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

            // the route table decides which line a mod lands on; combat reads no mod while weapon mods are off
            if (WeaponModRegistry.Enabled())
            {
                var modSuffix = StrengthSuffix(SummonerFocus.PetModTransferPercent());

                var petsOnly = CarriedModNames(wo, SummonerPetRoute.PetsOnly);
                if (petsOnly.Count > 0)
                    lines.Add($"- Empowers your summons, not you: {string.Join(", ", petsOnly)}{modSuffix}");

                var shared = CarriedModNames(wo, SummonerPetRoute.Shared);
                if (shared.Count > 0)
                    lines.Add($"- Also empowers your summons: {string.Join(", ", shared)}{modSuffix}");
            }

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
        /// The display names, in registry order, of the mods on <paramref name="route"/> the focus carries, as a record or as
        /// Paragon gems. Reads the record property straight off the item, the way WeaponModParagonDisplay does, NOT through
        /// WeaponModCombat.ReadWeaponOnly, which strips the PetsOnly mods on a focus and would always answer 0.
        /// </summary>
        private static List<string> CarriedModNames(WorldObject focus, SummonerPetRoute route)
        {
            var names = new List<string>();

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                if (SummonerFocus.PetRoute(definition) != route)
                    continue;

                // a Tourmaline record, or Paragon gems alone (combat transfers both through ReadFocusModForPets)
                if (focus.GetProperty(definition.Record) != null || WeaponModParagon.ParagonFraction(focus, definition.Id) > 0.0)
                    names.Add(definition.DisplayName);
            }

            return names;
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
