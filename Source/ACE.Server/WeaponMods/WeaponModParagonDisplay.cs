using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum.Properties;
using ACE.Server.WorldObjects;

namespace ACE.Server.WeaponMods
{
    /// <summary>
    /// The one place Paragon weapon mods become player-facing text (the appraisal panel today; market and chat
    /// will reuse these methods rather than format their own).
    ///
    /// The lines describe the ITEM, not the wielder's current ruleset: PK facet or arena suppression may zero
    /// these effects in combat, exactly as it does for the Tourmaline rows, and the text does not say so.
    ///
    /// NO SECOND FORMULA. Every combined magnitude here comes from the same math combat reads: Tier B resolves
    /// <see cref="WeaponModParagon.CombinedFraction"/> through <see cref="WeaponModValue.MagnitudeFromFraction(WeaponModDefinition, double, double, WorldObject)"/>
    /// (exactly what WeaponModCombat.ReadWeaponOnly does), and Tier A is the Tourmaline record plus
    /// <see cref="WeaponModParagon.TierAAdd(WeaponModDefinition, double?, double, double, WorldObject)"/>.
    /// </summary>
    public static class WeaponModParagonDisplay
    {
        /// <summary>The level of an ungemmed Paragon weapon; an unset ItemMaxLevel reads as this plus the gem count.</summary>
        public const int BaseLevel = 50;

        /// <summary>The top of the Paragon range, printed as the denominator of the summary line.</summary>
        public const int MaxLevel = 55;

        /// <summary>
        /// TRUE when the weapon carries Paragon gems AND is a weapon type the combat and rating reads accept
        /// (<see cref="WeaponModParagon.IsCarrierWeenieType"/>), so ammunition or armor with a stray slot shows
        /// nothing, matching what combat applies.
        ///
        /// This cannot hide a legitimate Paragon weapon: every Paragon weapon is created by retail gem 1
        /// (W_LUMINOUSAMBEROFTHE1STTIERPARAGON_CLASS), whose dispatch in RecipeManager_New.cs (the "Paragon
        /// Weapons" switch on target.WeenieType, ~lines 458-475) accepts only Caster, MeleeWeapon and
        /// MissileLauncher targets. The predicate here also admits thrown Missile weapons, a superset of those.
        /// </summary>
        public static bool IsParagonCarrier(WorldObject weapon) =>
            WeaponModParagon.HasParagonMods(weapon) && WeaponModParagon.IsCarrierWeenieType(weapon.WeenieType);

        /// <summary>
        /// The distinct Paragon mods on the weapon with their gem counts, in first-slot order. Slots are read in
        /// order and stop at the first empty one, the same rule <see cref="WeaponModParagon.GemCount"/> applies.
        /// An id that is not a live catalog row is skipped (it is inert in combat too).
        /// </summary>
        public static List<(WeaponModDefinition Definition, int Gems)> ReadGems(WorldObject weapon)
        {
            var result = new List<(WeaponModDefinition, int)>();

            if (!IsParagonCarrier(weapon))
                return result;

            var seen = new HashSet<int>();

            foreach (var slot in ParagonModRegistry.Slots)
            {
                var value = weapon.GetProperty(slot);

                if (value == null)
                    break;

                if (!seen.Add(value.Value))
                    continue;

                if (WeaponModRegistry.TryGet((WeaponModId)value.Value, out var definition))
                    result.Add((definition, WeaponModParagon.GemCount(weapon, definition.Id)));
            }

            return result;
        }

        /// <summary>
        /// The magnitude combat would apply for <paramref name="definition"/> on this weapon: Tourmaline record
        /// plus Paragon gems, clamped at a perfect roll. Equals the Tourmaline-only magnitude when the weapon has
        /// no gems for the row.
        /// </summary>
        public static double CombinedMagnitude(WorldObject weapon, WeaponModDefinition definition, double scale)
        {
            var record = weapon.GetProperty(definition.Record);
            var paragon = WeaponModParagon.ParagonFraction(weapon, definition.Id);

            if (definition.Tier == WeaponModTier.B)
                return WeaponModValue.MagnitudeFromFraction(definition, WeaponModParagon.CombinedFraction(record, paragon), scale, weapon);

            var recorded = record == null || double.IsNaN(record.Value) ? 0.0 : record.Value;

            return recorded + WeaponModParagon.TierAAdd(definition, record, paragon, scale, weapon);
        }

        /// <summary>
        /// The Tourmaline record as an UNROUNDED, unclamped fraction of the row's perfect roll: the Tier B record
        /// itself, or a Tier A record over its ceiling. 0 when absent or the row has no ceiling.
        /// </summary>
        public static double RecordFraction(WorldObject weapon, WeaponModDefinition definition, double scale)
        {
            var record = weapon.GetProperty(definition.Record);

            if (record == null || double.IsNaN(record.Value) || record.Value <= 0.0)
                return 0.0;

            if (definition.Tier == WeaponModTier.B)
                return record.Value;

            var ceiling = WeaponModValue.MaxMagnitude(definition, scale, weapon);

            return double.IsNaN(ceiling) || ceiling <= 0.0 ? 0.0 : record.Value / ceiling;
        }

        /// <summary>
        /// The mod bullet lines, ALPHABETICAL by mod name (<see cref="ByName"/>) across Tourmaline and Paragon-only
        /// mods alike, so the same set of mods always reads in the same order and a search pattern such as
        /// "ambush.*devastation" matches every weapon carrying both (owner ruling 2026-11-25).
        /// Tourmaline-only reads exactly as it always has; a mod held from both sources carries the combined
        /// magnitude and a "(rolled r% + Paragon p%)" tail, with ", capped" when r + p exceeds 100; a Paragon-only
        /// mod carries "(Paragon)".
        /// </summary>
        public static List<string> ModLines(WorldObject weapon, double scale)
        {
            var lines = new List<(WeaponModDefinition Definition, string Line)>();

            if (weapon == null)
                return new List<string>();

            var gems = ReadGems(weapon);

            foreach (var (definition, tourmaline) in WeaponModTinkerSet.ReadSpecials(weapon))
            {
                var gemCount = 0;

                foreach (var (gemDefinition, count) in gems)
                {
                    if (gemDefinition.Id == definition.Id)
                        gemCount = count;
                }

                if (gemCount == 0)
                {
                    lines.Add((definition, $"- {WeaponModDisplay.Describe(definition, tourmaline, scale, weapon, tagSummonerRoute: true)}"));
                    continue;
                }

                var rolled = WeaponModDisplay.IntensityPercent(definition, tourmaline, scale, weapon);
                var paragon = (int)Math.Round(gemCount * ParagonModRegistry.FractionPerGem * 100.0, MidpointRounding.AwayFromZero);
                var capped = WeaponModParagon.IsOverCap(RecordFraction(weapon, definition, scale), gemCount)
                    ? ", capped" : string.Empty;
                var combined = CombinedMagnitude(weapon, definition, scale);

                lines.Add((definition, $"- {WeaponModDisplay.Describe(definition, combined, scale, weapon, tagSummonerRoute: true)} (rolled {rolled}% + Paragon {paragon}%{capped})"));
            }

            foreach (var (definition, _) in gems)
            {
                if (weapon.GetProperty(definition.Record) != null)
                    continue;

                var magnitude = CombinedMagnitude(weapon, definition, scale);

                // a Paragon-only row that resolves to nothing (a muted scale) is not shown
                if (!WeaponModValue.IsLiveMagnitude(definition, magnitude))
                    continue;

                lines.Add((definition, $"- {WeaponModDisplay.Describe(definition, magnitude, scale, weapon, tagSummonerRoute: true)} (Paragon)"));
            }

            return lines.OrderBy(l => l.Definition, ByName).Select(l => l.Line).ToList();
        }

        /// <summary>
        /// The one ordering every player-facing weapon mod list uses: DisplayName, case-insensitive, then id as a
        /// tie-break so the order is total. The appraisal lines, the Paragon summary, the market rows (and so the
        /// search text built from them) and the Salvage Roller summary all sort through this.
        /// </summary>
        public static readonly IComparer<WeaponModDefinition> ByName = Comparer<WeaponModDefinition>.Create((a, b) =>
        {
            var byName = StringComparer.OrdinalIgnoreCase.Compare(a?.DisplayName, b?.DisplayName);

            return byName != 0 ? byName : ((int)(a?.Id ?? 0)).CompareTo((int)(b?.Id ?? 0));
        });

        /// <summary>
        /// "Paragon 53/55: Ambush x1, Devastation x2" - each distinct Paragon mod with its gem count, alphabetical
        /// (<see cref="ByName"/>). Empty when the weapon holds no gems.
        /// </summary>
        public static string SummaryLine(WorldObject weapon)
        {
            var gems = ReadGems(weapon);

            if (gems.Count == 0)
                return string.Empty;

            var total = 0;

            foreach (var slot in ParagonModRegistry.Slots)
            {
                if (weapon.GetProperty(slot) == null)
                    break;

                total++;
            }

            // an unset ItemMaxLevel reads as the level the gems imply; the printed level stays in 50..55
            var level = Math.Clamp(weapon.GetProperty(PropertyInt.ItemMaxLevel) ?? BaseLevel + total, BaseLevel, MaxLevel);
            var parts = new List<string>();

            foreach (var (definition, count) in gems.OrderBy(g => g.Definition, ByName))
                parts.Add($"{definition.DisplayName} x{count}");

            return $"- Paragon {level}/{MaxLevel}: {string.Join(", ", parts)}";
        }
    }
}
