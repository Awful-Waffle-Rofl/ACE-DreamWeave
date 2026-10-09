using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum.Properties;
using ACE.Server.EquipmentMods;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Managers.Market
{
    /// <summary>
    /// The mods projection for the market snapshot (Docs/Market/EQUIPMENT-MODS-WEB-DESIGN.md): which mods an
    /// item carries, in what order, with what intensity and effect text, and the stable token the web app
    /// filters on.
    ///
    /// ONE TEXT WITH THE APPRAISAL PANEL (HARD). Every figure comes from the code paths
    /// WeaponModDisplay.Describe and EquipmentModDisplay.Describe use - IntensityPercent at the live scale, and
    /// the Effect helpers Describe itself calls - so a web row and a panel line can never disagree. Never add
    /// a parallel formatter here.
    ///
    /// NULL, NEVER EMPTY (HARD). Mods is null when nothing is shown; MarketSnapshotFieldTests' FromItem /
    /// FromWeenie mirror test depends on it, since FromWeenie leaves all three fields null.
    /// </summary>
    public static class MarketItemMods
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindWeapon = "weapon";
        public const string KindArmor = "armor";

        /// <summary>
        /// Test seam for weapon_mods_enabled. PropertyManager reads throw on a cache miss inside ACE.Server.Tests,
        /// so the production read falls back to the compiled default (true, PropertyManager.cs). A test that
        /// reassigns this must restore it in a finally.
        /// </summary>
        internal static Func<bool> WeaponModsEnabledSource = () => ReadSwitch("weapon_mods_enabled", true);

        /// <summary>Test seam for equipment_mods_enabled. Same rules as <see cref="WeaponModsEnabledSource"/>.</summary>
        internal static Func<bool> EquipmentModsEnabledSource = () => ReadSwitch("equipment_mods_enabled", true);

        /// <summary>The same read AppraiseInfo makes, degrading to the compiled default when the property cannot be read.</summary>
        internal static bool ReadSwitch(string key, bool compiledDefault)
        {
            try
            {
                return PropertyManager.GetBool(key).Item;
            }
            catch (Exception)
            {
                return compiledDefault;
            }
        }

        private static bool IsOn(Func<bool> source, bool compiledDefault) => source?.Invoke() ?? compiledDefault;

        /// <summary>
        /// Fills Mods, ModCapacity and WeaponQualityTier on a snapshot projected from a live item: weapon rows
        /// first, then equipment rows, each group in registry order and each only while its switch is on.
        ///
        /// PRESENCE IS CHECKED BEFORE ANY SWITCH OR SCALE READ. ReadSpecials reads weapon_mod_magnitude_scale
        /// unconditionally, so a mod-less item would otherwise touch PropertyManager for nothing.
        ///
        /// Never throws: a failure is logged and leaves all three fields null, the same degrade FromItem gives
        /// an unreadable spell book. Call OUTSIDE the item's BiotaDatabaseLock, like FromItem.
        /// </summary>
        public static void Apply(WorldObject item, ListingSnapshot snapshot)
        {
            if (item == null || snapshot == null)
                return;

            try
            {
                var mods = new List<ItemMod>();

                if (HasAnyWeaponMod(item) && IsOn(WeaponModsEnabledSource, true))
                {
                    var weaponRows = WeaponModTinkerSet.ReadSpecials(item)
                        .Select(s => WeaponRow(s.Definition, s.Magnitude))
                        .ToList();

                    mods.AddRange(weaponRows);

                    // Summed from the rows just built, NOT WeaponQualityTiers.Evaluate(item). Evaluate
                    // re-walks ReadSpecials and re-reads weapon_mod_magnitude_scale independently, so a
                    // scale retune between building the rows and computing the tier (as can happen mid
                    // backfill) could sum the rows to one total and the tier to another. Summing the
                    // already-built weapon rows' IntensityPct (0 when unreportable, exactly what
                    // TotalIntensity accumulates via the same WeaponModDisplay.IntensityPercent call)
                    // keeps the projected tier and the projected rows internally consistent, always.
                    var tier = WeaponQualityTiers.FromTotal(weaponRows.Sum(r => r.IntensityPct ?? 0));

                    if (tier != WeaponQualityTier.None)
                        snapshot.WeaponQualityTier = WeaponQualityTiers.NameFor(tier);
                }

                var capacity = item.GetProperty(PropertyInt.GearModCapacity) ?? 0;

                if ((capacity > 0 || HasAnyEquipmentMod(item)) && IsOn(EquipmentModsEnabledSource, true))
                {
                    foreach (var (definition, potency) in EquipmentModDisplay.GetMods(item))
                        mods.Add(ArmorRow(definition, potency));

                    if (capacity > 0)
                        snapshot.ModCapacity = capacity;
                }

                snapshot.Mods = mods.Count > 0 ? mods : null;
            }
            catch (Exception ex)
            {
                snapshot.Mods = null;
                snapshot.ModCapacity = null;
                snapshot.WeaponQualityTier = null;

                log.Error($"[MARKET] could not project the mods of 0x{item.Guid.Full:X8}; listing it with none: {ex.GetFullMessage()}");
            }
        }

        private static bool HasAnyWeaponMod(WorldObject item) =>
            WeaponModRegistry.AllMods.Any(definition => item.GetProperty(definition.Record) != null);

        private static bool HasAnyEquipmentMod(WorldObject item) =>
            EquipmentModRegistry.AllMods.Any(definition => item.GetProperty(definition.Property) != null);

        /// <summary>One weapon row, from the same calls WeaponModDisplay.Describe(definition, magnitude) makes.</summary>
        internal static ItemMod WeaponRow(WeaponModDefinition definition, double magnitude)
        {
            var percent = WeaponModDisplay.IntensityPercent(definition, magnitude);

            return new ItemMod
            {
                Kind = KindWeapon,
                Token = Token(definition.Id),
                Name = definition.DisplayName,
                IntensityPct = percent > 0 ? percent : (int?)null,
                Effect = WeaponModDisplay.Effect(definition, magnitude),
            };
        }

        /// <summary>One equipment row, from the same calls EquipmentModDisplay.Describe(definition, potency) makes.</summary>
        internal static ItemMod ArmorRow(EquipmentModDefinition definition, double potency)
        {
            var percent = EquipmentModDisplay.IntensityPercent(definition, potency);

            return new ItemMod
            {
                Kind = KindArmor,
                Token = Token(definition.Id),
                Name = definition.DisplayName,
                IntensityPct = percent > 0 ? percent : (int?)null,
                Effect = EquipmentModDisplay.Effect(definition, potency),
            };
        }

        public static string Token(WeaponModId id) => Token(id.ToString());

        public static string Token(EquipmentModId id) => Token(id.ToString());

        /// <summary>
        /// Splits an enum member name into proper-cased words separated by single spaces: WeakPoint -> "Weak
        /// Point", Heft -> "Heft". A space goes before an uppercase letter that follows a lowercase letter or a
        /// digit, and before the LAST capital of a run when a lowercase letter follows it, so AOEBlast -> "AOE
        /// Blast". Case is never changed.
        ///
        /// TOKEN STABILITY IS A CONTRACT: saved Browse URLs carry these strings. Derive them from the enum
        /// member, never from DisplayName, and never rename an existing member.
        /// </summary>
        public static string Token(string memberName)
        {
            if (string.IsNullOrEmpty(memberName))
                return string.Empty;

            var builder = new StringBuilder(memberName.Length + 4);

            for (var i = 0; i < memberName.Length; i++)
            {
                var c = memberName[i];

                if (i > 0 && char.IsUpper(c))
                {
                    var previous = memberName[i - 1];
                    var nextIsLower = i + 1 < memberName.Length && char.IsLower(memberName[i + 1]);

                    if (char.IsLower(previous) || char.IsDigit(previous) || (char.IsUpper(previous) && nextIsLower))
                        builder.Append(' ');
                }

                builder.Append(c);
            }

            return builder.ToString();
        }
    }
}
