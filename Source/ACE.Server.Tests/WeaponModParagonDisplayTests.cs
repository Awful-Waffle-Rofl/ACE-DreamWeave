using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The appraisal block for Paragon weapons. Strings are pinned exactly: the wording is owner-fixed and the
    /// market and chat surfaces reuse the same helper. Tunables the paths read are seeded and restored.
    /// </summary>
    [TestClass]
    public class WeaponModParagonDisplayTests
    {
        private bool priorEnabled;
        private double priorScale;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            priorEnabled = PropertyManager.GetBool("weapon_mods_enabled").Item;
            priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", priorEnabled);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id} is not a live row");
            return definition;
        }

        private static uint nextGuid = 0x7D7C0000;   // static range, clear of GuidManager and the other kits
        private static uint nextWcid = 994900;

        /// <summary>
        /// A weapon of a REAL weenie type (the display uses the same carrier predicate as the rating reads, which
        /// filters on it), carrying the given gems in slot order.
        /// </summary>
        private static WorldObject Make(WeenieType weenieType, ItemType itemType, CombatUse? combatUse, params WeaponModId[] gems)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = weenieType,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)itemType },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Paragon Display Test Item" } },
            };

            if (combatUse != null)
                weenie.PropertiesInt[PropertyInt.CombatUse] = (int)combatUse.Value;

            var item = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNotNull(item, $"fixture: no WorldObject for {weenieType}");
            Assert.AreEqual(weenieType, item.WeenieType, "fixture: weenie type");

            for (var i = 0; i < gems.Length; i++)
                item.SetProperty(ParagonModRegistry.Slots[i], (int)gems[i]);

            return item;
        }

        private static WorldObject Weapon(params WeaponModId[] gems) =>
            Make(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, CombatUse.Melee, gems);

        private static WorldObject WeaponAtLevel(int level, params WeaponModId[] gems)
        {
            var weapon = Weapon(gems);
            weapon.SetProperty(PropertyInt.ItemMaxLevel, level);
            return weapon;
        }

        private static List<string> Lines(WorldObject weapon) => WeaponModDisplay.GetAppraisalLines(weapon);

        // ================= non-Paragon weapons are unchanged =================

        [TestMethod]
        public void NonParagon_TourmalineOnly_LinesAreTheGoldenFormat()
        {
            var weapon = Weapon();
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 4);
            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, Row(WeaponModId.Ambush), 0.5);

            var lines = Lines(weapon);

            CollectionAssert.AreEqual(new[]
            {
                "- Ambush [50%]: +15% damage against a full-health target",
                "- Devastation [67%]: +4 critical damage rating",
            }, lines);

            // byte-identical to the pre-Paragon construction, then alphabetical by name (owner ruling 2026-11-25)
            var legacy = WeaponModTinkerSet.ReadSpecials(weapon)
                .OrderBy(s => s.Definition.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(s => $"- {WeaponModDisplay.Describe(s.Definition, s.Magnitude, WeaponModValue.MagnitudeScale(), weapon)}")
                .ToList();

            CollectionAssert.AreEqual(legacy, lines);
        }

        [TestMethod]
        public void NonParagon_NoSpecials_NoLines() =>
            Assert.AreEqual(0, Lines(Weapon()).Count);

        // ================= Tourmaline + Paragon on the same mod =================

        [TestMethod]
        public void Both_TierA_Uncapped()
        {
            var weapon = WeaponAtLevel(51, WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 3);

            CollectionAssert.AreEqual(new[]
            {
                "- Devastation [67%]: +4 critical damage rating (rolled 50% + Paragon 20%)",
                "- Paragon 51/55: Devastation x1",
            }, Lines(weapon));
        }

        [TestMethod]
        public void Both_TierA_Capped()
        {
            var weapon = Weapon(WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 6);

            Assert.AreEqual("- Devastation [100%]: +6 critical damage rating (rolled 100% + Paragon 20%, capped)", Lines(weapon)[0]);
        }

        [TestMethod]
        public void Both_TierB_Uncapped_And_Capped()
        {
            var weapon = Weapon(WeaponModId.Ambush);
            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, Row(WeaponModId.Ambush), 0.5);

            Assert.AreEqual("- Ambush [70%]: +21% damage against a full-health target (rolled 50% + Paragon 20%)", Lines(weapon)[0]);

            var capped = Weapon(WeaponModId.Ambush, WeaponModId.Ambush);
            WeaponModTinkerSet.ApplySpecialAtFraction(capped, Row(WeaponModId.Ambush), 0.9);

            Assert.AreEqual("- Ambush [100%]: +30% damage against a full-health target (rolled 90% + Paragon 40%, capped)", Lines(capped)[0]);
        }

        [TestMethod]
        public void Both_CombinedMagnitudeMatchesTheCombatRead()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Ambush);
            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, Row(WeaponModId.Ambush), 0.5);

            var combat = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, true, 1.0);

            Assert.AreEqual(combat, WeaponModParagonDisplay.CombinedMagnitude(weapon, Row(WeaponModId.Ambush), 1.0), 1e-9);
        }

        // ================= Paragon-only =================

        [TestMethod]
        public void ParagonOnly_TierB_And_TierA_Rows()
        {
            var weapon = WeaponAtLevel(53, WeaponModId.Ambush, WeaponModId.Devastation, WeaponModId.ShieldBypass);

            CollectionAssert.AreEqual(new[]
            {
                "- Ambush [20%]: +6% damage against a full-health target (Paragon)",
                "- Devastation [17%]: +1 critical damage rating (Paragon)",
                "- Shield Bypass [20%]: 10% of a shield ignored (Paragon)",
                "- Paragon 53/55: Ambush x1, Devastation x1, Shield Bypass x1",
            }, Lines(weapon));
        }

        [TestMethod]
        public void TwoGemsOnOneMod_ShowsX2_AndFortyPercent()
        {
            var weapon = WeaponAtLevel(52, WeaponModId.Devastation, WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 3);

            var lines = Lines(weapon);

            Assert.AreEqual("- Devastation [83%]: +5 critical damage rating (rolled 50% + Paragon 40%)", lines[0]);
            Assert.AreEqual("- Paragon 52/55: Devastation x2", lines[1]);
        }

        // ================= summary line =================

        [TestMethod]
        public void Summary_UsesItemMaxLevel_AndAlphabeticalOrder()
        {
            // slot order is Heft first; the summary still reads alphabetically
            var weapon = Weapon(WeaponModId.Heft, WeaponModId.Ambush, WeaponModId.Heft, WeaponModId.Heft);
            weapon.SetProperty(PropertyInt.ItemMaxLevel, 54);

            Assert.AreEqual("- Paragon 54/55: Ambush x1, Heft x3", WeaponModParagonDisplay.SummaryLine(weapon));
        }

        [TestMethod]
        public void Summary_EmptySlots_NoLine()
        {
            var weapon = Weapon();
            weapon.SetProperty(PropertyInt.ItemMaxLevel, 52);

            Assert.AreEqual(string.Empty, WeaponModParagonDisplay.SummaryLine(weapon));
            Assert.AreEqual(0, Lines(weapon).Count);
        }

        [TestMethod]
        public void Block_OrderIsModsAlphabeticalThenSummaryThenTinkers()
        {
            var weapon = WeaponAtLevel(53, WeaponModId.Ambush, WeaponModId.Devastation, WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 3);

            var composition = new[]
            {
                MaterialType.Iron, MaterialType.Iron, MaterialType.Iron, MaterialType.Iron,
                MaterialType.Brass, MaterialType.Brass, MaterialType.Brass,
                MaterialType.Granite, MaterialType.Granite, MaterialType.Granite,
            };

            weapon.SetProperty(PropertyInt.WeaponModTinkerCount, 1);
            weapon.SetProperty(PropertyString.WeaponModTinkerLog, WeaponModTinkerSet.SerializeLog(composition));

            var lines = Lines(weapon);

            Assert.AreEqual(4, lines.Count, string.Join(" | ", lines));
            // Ambush (Paragon-only) sorts ahead of Devastation (Tourmaline + Paragon): name order, not source order
            Assert.AreEqual("- Ambush [20%]: +6% damage against a full-health target (Paragon)", lines[0]);
            Assert.AreEqual("- Devastation [83%]: +5 critical damage rating (rolled 50% + Paragon 40%)", lines[1]);
            Assert.AreEqual("- Paragon 53/55: Ambush x1, Devastation x2", lines[2]);
            Assert.AreEqual("- Tinkers: " + WeaponModDisplay.DescribeComposition(composition), lines[3], "Tinkers is present and last");
            Assert.IsTrue(lines[3].StartsWith("- Tinkers: 4 Iron"), lines[3]);
        }

        // ================= isolation =================

        [TestMethod]
        public void ReadSpecials_And_SpecialCount_ExcludeParagon()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Devastation);

            Assert.AreEqual(0, WeaponModTinkerSet.SpecialCount(weapon));
            Assert.AreEqual(0, WeaponModTinkerSet.ReadSpecials(weapon).Count);

            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 3);

            Assert.AreEqual(1, WeaponModTinkerSet.SpecialCount(weapon));
        }

        [TestMethod]
        public void EveryRow_FiveGemsPlusARecord_IsAsciiAndShort()
        {
            var checkedRows = 0;

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                var weapon = Weapon(definition.Id, definition.Id, definition.Id, definition.Id, definition.Id);
                weapon.SetProperty(PropertyInt.ItemMaxLevel, 55);

                if (definition.Tier == WeaponModTier.B)
                    WeaponModTinkerSet.ApplySpecialAtFraction(weapon, definition, 0.5, 1.0);
                else
                    WeaponModTinkerSet.ApplySpecial(weapon, definition, WeaponModValue.MaxMagnitude(definition, 1.0, weapon) / 2.0);

                var lines = Lines(weapon);

                Assert.IsTrue(lines.Count >= 2, $"{definition.Id}: {string.Join(" | ", lines)}");

                foreach (var line in lines)
                {
                    Assert.IsTrue(line.All(c => c >= 32 && c < 127), $"{definition.Id}: {line}");
                    Assert.IsTrue(line.Length <= 120, $"{definition.Id} ({line.Length}): {line}");
                }

                checkedRows++;
            }

            Assert.IsTrue(checkedRows >= 20, $"only {checkedRows} rows checked");
        }

        // ================= capped is judged on the UNROUNDED fractions =================

        [TestMethod]
        public void Capped_UsesUnroundedFractions()
        {
            var shieldBypass = Row(WeaponModId.ShieldBypass);

            // record 0.4005 of a 0.5 ceiling is 80.1%: it displays as 80, and 80 + 20 is not over 100, but the
            // true sum 100.1% is
            var over = Weapon(WeaponModId.ShieldBypass);
            over.SetProperty(shieldBypass.Record, 0.4005);

            Assert.AreEqual("- Shield Bypass [100%]: 50% of a shield ignored (rolled 80% + Paragon 20%, capped)", Lines(over)[0]);

            // exactly 100 is not capped
            var exact = Weapon(WeaponModId.ShieldBypass);
            exact.SetProperty(shieldBypass.Record, 0.4);

            Assert.AreEqual("- Shield Bypass [100%]: 50% of a shield ignored (rolled 80% + Paragon 20%)", Lines(exact)[0]);
        }

        [TestMethod]
        public void RecordAboveCeiling_ReadsHundred_Capped_AndKeepsTheRecordMagnitude()
        {
            var devastation = Row(WeaponModId.Devastation);
            var weapon = Weapon(WeaponModId.Devastation);
            weapon.SetProperty(devastation.Record, 8.0);

            Assert.AreEqual("- Devastation [100%]: +8 critical damage rating (rolled 100% + Paragon 20%, capped)", Lines(weapon)[0]);
            Assert.AreEqual(8.0, WeaponModParagonDisplay.CombinedMagnitude(weapon, devastation, 1.0), 1e-9);
        }

        // ================= equivalence with the combat-side reads =================

        [TestMethod]
        public void TierA_CombinedMagnitude_EqualsRecordPlusRatingAdd()
        {
            foreach (var id in new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst })
            {
                foreach (var scale in new[] { 1.0, 0.7 })
                {
                    var definition = Row(id);
                    var weapon = Weapon(id, id);
                    var record = System.Math.Max(1.0, System.Math.Round(WeaponModValue.MaxMagnitude(definition, scale, weapon) * 0.5));
                    WeaponModTinkerSet.ApplySpecial(weapon, definition, record);

                    var expected = record + WeaponModParagon.TierARatingAdd(new[] { weapon }, definition.NativeInt.Value, true, scale);

                    Assert.AreEqual(expected, WeaponModParagonDisplay.CombinedMagnitude(weapon, definition, scale), 1e-9, $"{id} at {scale}");
                }
            }
        }

        [TestMethod]
        public void ShieldBypass_CombinedMagnitude_EqualsRecordPlusIgnoreShieldAdd()
        {
            var definition = Row(WeaponModId.ShieldBypass);

            foreach (var scale in new[] { 1.0, 0.7 })
            {
                var weapon = Weapon(WeaponModId.ShieldBypass, WeaponModId.ShieldBypass);
                weapon.SetProperty(definition.Record, 0.2);

                var expected = 0.2 + WeaponModParagon.IgnoreShieldAdd(weapon, true, false, scale);

                Assert.AreEqual(expected, WeaponModParagonDisplay.CombinedMagnitude(weapon, definition, scale), 1e-9, $"scale {scale}");
            }
        }

        [TestMethod]
        public void DefensiveWeapon_CeilingFollowsTheCarrier()
        {
            var definition = Row(WeaponModId.DefensiveWeapon);

            var oneHanded = Make(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, CombatUse.Melee, WeaponModId.DefensiveWeapon);
            var twoHanded = Make(WeenieType.MeleeWeapon, ItemType.MeleeWeapon, CombatUse.TwoHanded, WeaponModId.DefensiveWeapon);

            WeaponModTinkerSet.ApplySpecialAtFraction(oneHanded, definition, 0.5, 1.0);
            WeaponModTinkerSet.ApplySpecialAtFraction(twoHanded, definition, 0.5, 1.0);

            Assert.AreEqual(70.0, WeaponModParagonDisplay.CombinedMagnitude(oneHanded, definition, 1.0), 1e-9);
            Assert.AreEqual(140.0, WeaponModParagonDisplay.CombinedMagnitude(twoHanded, definition, 1.0), 1e-9);

            Assert.AreEqual("- Defensive Weapon [70%]: +70 shield armor level against frontal blows (rolled 50% + Paragon 20%)", Lines(oneHanded)[0]);
            Assert.AreEqual("- Defensive Weapon [70%]: +140 shield armor level against frontal blows (rolled 50% + Paragon 20%)", Lines(twoHanded)[0]);
        }

        // ================= malformed slots, level, muted scale, carrier =================

        [TestMethod]
        public void UnknownId_IsSkipped_InLinesAndSummary()
        {
            var weapon = WeaponAtLevel(53, WeaponModId.Heft, (WeaponModId)9999, WeaponModId.Ambush);

            var lines = Lines(weapon);

            Assert.AreEqual(3, lines.Count, string.Join(" | ", lines));
            Assert.AreEqual("- Ambush [20%]: +6% damage against a full-health target (Paragon)", lines[0]);
            Assert.IsTrue(lines[1].StartsWith("- Heft [20%]") && lines[1].EndsWith("(Paragon)"), lines[1]);
            Assert.AreEqual("- Paragon 53/55: Ambush x1, Heft x1", lines[2]);

            Assert.AreEqual(0, Lines(Weapon((WeaponModId)9999)).Count);
        }

        [TestMethod]
        public void SlotGap_StopsTheRead()
        {
            var weapon = WeaponAtLevel(52, WeaponModId.Heft);
            weapon.SetProperty(PropertyInt.ParagonModSlot3, (int)WeaponModId.Ambush);

            var lines = Lines(weapon);

            Assert.AreEqual(2, lines.Count, string.Join(" | ", lines));
            Assert.IsTrue(lines[0].StartsWith("- Heft"), lines[0]);
            Assert.AreEqual("- Paragon 52/55: Heft x1", lines[1]);
        }

        [TestMethod]
        public void Level_FallsBackToFiftyPlusGems_AndClampsToFiftyThroughFiftyFive()
        {
            Assert.AreEqual("- Paragon 51/55: Heft x1", WeaponModParagonDisplay.SummaryLine(Weapon(WeaponModId.Heft)));
            Assert.AreEqual("- Paragon 53/55: Heft x3", WeaponModParagonDisplay.SummaryLine(Weapon(WeaponModId.Heft, WeaponModId.Heft, WeaponModId.Heft)));
            Assert.AreEqual("- Paragon 55/55: Heft x5", WeaponModParagonDisplay.SummaryLine(Weapon(WeaponModId.Heft, WeaponModId.Heft, WeaponModId.Heft, WeaponModId.Heft, WeaponModId.Heft)));
            Assert.AreEqual("- Paragon 55/55: Heft x1", WeaponModParagonDisplay.SummaryLine(WeaponAtLevel(60, WeaponModId.Heft)));
            Assert.AreEqual("- Paragon 50/55: Heft x1", WeaponModParagonDisplay.SummaryLine(WeaponAtLevel(40, WeaponModId.Heft)));
        }

        [TestMethod]
        public void MutedScale_HidesParagonOnlyLines_ButKeepsTheSummary()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 0.0));

            var weapon = WeaponAtLevel(52, WeaponModId.Ambush, WeaponModId.Devastation);

            var lines = Lines(weapon);

            Assert.AreEqual(1, lines.Count, string.Join(" | ", lines));
            Assert.AreEqual("- Paragon 52/55: Ambush x1, Devastation x1", lines[0]);
        }

        [TestMethod]
        public void Ammunition_WithAStraySlot_ShowsNothing()
        {
            var arrows = Make(WeenieType.Ammunition, ItemType.MissileWeapon, CombatUse.Ammo, WeaponModId.Devastation);

            Assert.IsTrue(WeaponModParagon.HasParagonMods(arrows), "control: the stray slot is there");
            Assert.AreEqual(0, Lines(arrows).Count);
            Assert.AreEqual(string.Empty, WeaponModParagonDisplay.SummaryLine(arrows));
        }

        [TestMethod]
        public void EveryCarrierWeenieType_ShowsItsParagonLines()
        {
            var carriers = new[]
            {
                (WeenieType.Caster, ItemType.Caster, (CombatUse?)CombatUse.Melee),
                (WeenieType.MeleeWeapon, ItemType.MeleeWeapon, CombatUse.Melee),
                (WeenieType.MissileLauncher, ItemType.MissileWeapon, CombatUse.Missile),
                (WeenieType.Missile, ItemType.MissileWeapon, CombatUse.Missile),
            };

            foreach (var (weenieType, itemType, combatUse) in carriers)
            {
                var weapon = Make(weenieType, itemType, combatUse, WeaponModId.Devastation);
                weapon.SetProperty(PropertyInt.ItemMaxLevel, 51);

                CollectionAssert.AreEqual(new[]
                {
                    "- Devastation [17%]: +1 critical damage rating (Paragon)",
                    "- Paragon 51/55: Devastation x1",
                }, Lines(weapon), weenieType.ToString());
            }
        }
    }
}
