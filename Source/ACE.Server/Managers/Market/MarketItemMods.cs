using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using log4net;

using ACE.Common.Extensions;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
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
    /// PARAGON (2026-11-16). A weapon carrying Paragon gems also lists them: a mod held from both sources carries the
    /// combined (Tourmaline + Paragon, clamped) magnitude from WeaponModParagonDisplay, Paragon-only mods follow as
    /// rows, and <see cref="ParagonSummary"/> is appended to the description. Paragon rows are EXCLUDED from
    /// WeaponQualityTier, which stays summed from the Tourmaline-only rows.
    ///
    /// NULL, NEVER EMPTY (HARD). Mods is null when nothing is shown; MarketSnapshotFieldTests' FromItem /
    /// FromWeenie mirror test depends on it, since FromWeenie leaves all three fields null.
    /// </summary>
    public static class MarketItemMods
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        public const string KindWeapon = "weapon";
        public const string KindArmor = "armor";
        public const string KindJewelry = "jewelry";

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

                // Paragon gems are read through WeaponModParagonDisplay (the one formatter); a Paragon-only weapon
                // has no Tourmaline record, so HasAnyWeaponMod alone would list it with no mods.
                var paragonGems = WeaponModParagonDisplay.ReadGems(item);

                if ((HasAnyWeaponMod(item) || paragonGems.Count > 0) && IsOn(WeaponModsEnabledSource, true))
                {
                    var specials = WeaponModTinkerSet.ReadSpecials(item);

                    // The Tourmaline-only rows. The quality tier is summed from THESE and never from the combined
                    // rows below: Paragon mods do not count toward the aura (owner ruling).
                    var tourmalineRows = specials
                        .Select(s => WeaponRow(s.Definition, s.Magnitude, item))
                        .ToList();

                    var weaponRows = new List<ItemMod>();
                    var scale = WeaponModValue.MagnitudeScale();

                    for (var i = 0; i < specials.Count; i++)
                    {
                        var definition = specials[i].Definition;

                        // A row the weapon also holds Paragon gems for carries the COMBINED (Tourmaline + Paragon,
                        // clamped) magnitude, the same figure the appraisal line prints.
                        weaponRows.Add(paragonGems.Any(g => g.Definition.Id == definition.Id)
                            ? WeaponRow(definition, WeaponModParagonDisplay.CombinedMagnitude(item, definition, scale), item)
                            : tourmalineRows[i]);
                    }

                    // Paragon-only mods follow, in slot order, skipping a row a muted scale resolves to nothing
                    // (the same rule WeaponModParagonDisplay.ModLines applies).
                    foreach (var (definition, _) in paragonGems)
                    {
                        if (item.GetProperty(definition.Record) != null)
                            continue;

                        var magnitude = WeaponModParagonDisplay.CombinedMagnitude(item, definition, scale);

                        if (WeaponModValue.IsLiveMagnitude(definition, magnitude))
                            weaponRows.Add(WeaponRow(definition, magnitude, item));
                    }

                    mods.AddRange(weaponRows);

                    // Summed from the rows just built, NOT WeaponQualityTiers.Evaluate(item). Evaluate
                    // re-walks ReadSpecials and re-reads weapon_mod_magnitude_scale independently, so a
                    // scale retune between building the rows and computing the tier (as can happen mid
                    // backfill) could sum the rows to one total and the tier to another. Summing the
                    // already-built Tourmaline rows' IntensityPct (0 when unreportable, exactly what
                    // TotalIntensity accumulates via the same WeaponModDisplay.IntensityPercent call)
                    // keeps the projected tier and the Tourmaline rows internally consistent, always.
                    var tier = WeaponQualityTiers.FromTotal(tourmalineRows.Sum(r => r.IntensityPct ?? 0));

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

                // Cloak Mantle rows follow the equipment rows (kind "armor"), then the Jewelry Aspect rows. Presence is
                // checked first, so an item with neither reads no setting.
                mods.AddRange(CloakRows(item));
                mods.AddRange(JewelryRows(item));

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

        /// <summary>
        /// The mod names on a live item, for free-text search (ItemTextSearch): exactly the Name of each row
        /// <see cref="Apply"/> projects for the web market (weapon rows first, then equipment rows, each in
        /// registry order, each only while its switch is on), which is the definition's DisplayName. It runs
        /// Apply itself rather than a second walk, so /mule search and the web search can never disagree
        /// about which mods an item has. Empty, never null.
        /// </summary>
        public static IReadOnlyList<string> SearchNames(WorldObject item) => SearchNames(item, includeJewelryMods: true);

        /// <summary>
        /// <see cref="SearchNames(WorldObject)"/>, optionally without the Jewelry Aspect rows (kind "jewelry").
        /// </summary>
        public static IReadOnlyList<string> SearchNames(WorldObject item, bool includeJewelryMods)
        {
            var snapshot = new ListingSnapshot();

            Apply(item, snapshot);

            return snapshot.Mods == null
                ? Array.Empty<string>()
                : snapshot.Mods.Where(mod => includeJewelryMods || mod.Kind != KindJewelry).Select(mod => mod.Name).Where(name => !string.IsNullOrEmpty(name)).ToList();
        }

        /// <summary>
        /// The Paragon summary for the description field ("Paragon 53/55: Devastation x2, Ambush x1"), or null. Gated by
        /// the same weapon_mods_enabled seam <see cref="Apply"/> uses, so the web never shows Paragon text the game hides.
        /// Null for every non-Paragon item.
        /// </summary>
        public static string ParagonSummary(WorldObject item)
        {
            if (item == null || !WeaponModParagonDisplay.IsParagonCarrier(item) || !IsOn(WeaponModsEnabledSource, true))
                return null;

            var summary = WeaponModParagonDisplay.SummaryLine(item);

            if (string.IsNullOrEmpty(summary))
                return null;

            // SummaryLine is an appraisal bullet ("- Paragon ..."); the description field is prose.
            return summary.StartsWith("- ") ? summary.Substring(2) : summary;
        }

        private static bool HasAnyWeaponMod(WorldObject item) =>
            WeaponModRegistry.AllMods.Any(definition => item.GetProperty(definition.Record) != null);

        private static bool HasAnyEquipmentMod(WorldObject item) =>
            EquipmentModRegistry.AllMods.Any(definition => item.GetProperty(definition.Property) != null);

        /// <summary>
        /// One weapon row, from the same calls WeaponModDisplay.Describe(definition, magnitude, scale, item) makes.
        /// <paramref name="item"/> is the carrier, so a per-item ceiling (Defensive Weapon) reads the same
        /// intensity here as on the appraisal panel; null reads the row's full MaxRoll.
        /// </summary>
        internal static ItemMod WeaponRow(WeaponModDefinition definition, double magnitude, WorldObject item = null)
        {
            var percent = WeaponModDisplay.IntensityPercent(definition, magnitude, WeaponModValue.MagnitudeScale(), item);

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
                Requirement = EquipmentModDisplay.AbilityRequirement(definition),
            };
        }

        public const string RearGuardName = "Rear Guard";
        public const string ProcAmplifierName = "Proc Amplifier";

        /// <summary>
        /// The Cloak Mantle rows of a cloak, Rear Guard then Proc Amplifier, each only when the cloak carries it and, for Rear Guard, cloak_rear_guard_enabled is on. The
        /// effect text is CloakMantleDisplay's, the same text the appraisal line prints. Token and name are the same string.
        /// </summary>
        internal static List<ItemMod> CloakRows(WorldObject item)
        {
            var rows = new List<ItemMod>();

            // Rear Guard is gated by cloak_rear_guard_enabled (off: the mantle gives no armor, so no row). The Proc Amplifier has no switch.
            var rearGuard = ReadSwitch("cloak_rear_guard_enabled", true) ? CloakMantleDisplay.RearGuardEffect(item) : null;

            if (rearGuard != null)
                rows.Add(new ItemMod { Kind = KindArmor, Token = RearGuardName, Name = RearGuardName, Effect = rearGuard });

            var amplifier = CloakMantleDisplay.ProcAmplifierEffect(item);

            if (amplifier != null)
                rows.Add(new ItemMod { Kind = KindArmor, Token = ProcAmplifierName, Name = ProcAmplifierName, Effect = amplifier });

            return rows;
        }

        /// <summary>
        /// The Jewelry Aspect rows of an aspected ring, bracelet or necklace: one per aspect type with a count of at least
        /// 1, in the order Defense, Attack, Utility. Token is the type, name carries the layer, count is the type's aspect
        /// count, and the effect is JewelryAspectDisplay.LayerText (the text after the label on the appraisal line). None while jewelry_aspects_enabled is off (the
        /// appraisal advertises no layer then either).
        /// </summary>
        internal static List<ItemMod> JewelryRows(WorldObject item)
        {
            var rows = new List<ItemMod>();

            if (!JewelryAspectMath.IsAspected(item) || !ReadSwitch("jewelry_aspects_enabled", true))
                return rows;

            var counts = JewelryAspectMath.ReadCounts(item);

            AddJewelryRow(rows, AspectType.Defense, "Defense", "Defense (Fortitude)", counts.Defense);
            AddJewelryRow(rows, AspectType.Attack, "Attack", "Attack (Penetration)", counts.Attack);
            AddJewelryRow(rows, AspectType.Utility, "Utility", "Utility (Resolve)", counts.Utility);

            return rows;
        }

        private static void AddJewelryRow(List<ItemMod> rows, AspectType type, string token, string name, int count)
        {
            if (count < 1)
                return;

            rows.Add(new ItemMod { Kind = KindJewelry, Token = token, Name = name, Count = count, Effect = JewelryAspectDisplay.LayerText(type, count) });
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
