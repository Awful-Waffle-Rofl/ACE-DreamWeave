using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The market mods projection of a Paragon weapon. Rows carry the combined (Tourmaline + Paragon, clamped)
    /// magnitude from WeaponModParagonDisplay, Paragon-only mods are listed, and the quality tier stays
    /// Tourmaline-only. The wire shape is unchanged: only existing fields are filled.
    /// </summary>
    [TestClass]
    public class MarketItemModsParagonTests
    {
        private bool priorEnabled;
        private double priorScale;
        private IClothingIconSource savedClothingIcons;

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

            savedClothingIcons = MarketSnapshot.ClothingIcons;
            MarketSnapshot.ClothingIcons = null;
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", priorEnabled);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
            MarketSnapshot.ClothingIcons = savedClothingIcons;
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id} is not a live row");
            return definition;
        }

        private static uint nextGuid = 0x7D7B0000;
        private static uint nextWcid = 994950;

        private static WorldObject Weapon(params WeaponModId[] gems)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ItemWorkmanship, 10 },
                },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Paragon Market Test Sword" } },
            };

            var item = WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            Assert.IsNotNull(item, "fixture: no WorldObject");

            for (var i = 0; i < gems.Length; i++)
                item.SetProperty(ParagonModRegistry.Slots[i], (int)gems[i]);

            return item;
        }

        // ================= Paragon-only =================

        [TestMethod]
        public void ParagonOnly_WeaponListsItsMods_AndPresenceIsTrue()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Devastation);

            // no Tourmaline record at all: the presence check must still pass
            Assert.AreEqual(0, WeaponModTinkerSet.ReadSpecials(weapon).Count, "fixture: no Tourmaline records");

            var snapshot = MarketSnapshot.FromItem(weapon);

            Assert.IsNotNull(snapshot.Mods);
            CollectionAssert.AreEqual(new[] { "Ambush", "Devastation" }, snapshot.Mods.Select(m => m.Name).ToList());
            Assert.IsTrue(snapshot.Mods.All(m => m.Kind == MarketItemMods.KindWeapon));

            CollectionAssert.AreEqual(new[] { "Ambush", "Devastation" }, MarketItemMods.SearchNames(weapon).ToList(),
                "search sees the same Paragon-only rows");
        }

        [TestMethod]
        public void ParagonOnly_RowValuesEqualCombinedMagnitude()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Devastation, WeaponModId.Devastation);
            var scale = WeaponModValue.MagnitudeScale();
            var mods = MarketSnapshot.FromItem(weapon).Mods;

            foreach (var id in new[] { WeaponModId.Ambush, WeaponModId.Devastation })
            {
                var definition = Row(id);
                var expected = MarketItemMods.WeaponRow(definition, WeaponModParagonDisplay.CombinedMagnitude(weapon, definition, scale), weapon);
                var actual = mods.Single(m => m.Name == definition.DisplayName);

                Assert.AreEqual(expected.Effect, actual.Effect, id.ToString());
                Assert.AreEqual(expected.IntensityPct, actual.IntensityPct, id.ToString());
                Assert.AreEqual(expected.Token, actual.Token, id.ToString());
            }
        }

        [TestMethod]
        public void ParagonSummary_RidesOnTheExistingDescriptionField()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Devastation);
            weapon.SetProperty(PropertyInt.ItemMaxLevel, 52);

            var snapshot = MarketSnapshot.FromItem(weapon);

            Assert.AreEqual("Paragon 52/55: Ambush x1, Devastation x1", snapshot.LongDesc);
        }

        // ================= Tourmaline + Paragon =================

        [TestMethod]
        public void Both_RowCarriesTheCombinedMagnitude_AndRowOrderIsTourmalineThenParagonOnly()
        {
            var weapon = Weapon(WeaponModId.Ambush, WeaponModId.Ambush, WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, Row(WeaponModId.Ambush), 0.5);

            var scale = WeaponModValue.MagnitudeScale();
            var ambush = Row(WeaponModId.Ambush);
            var mods = MarketSnapshot.FromItem(weapon).Mods;

            CollectionAssert.AreEqual(new[] { "Ambush", "Devastation" }, mods.Select(m => m.Name).ToList());

            var combined = WeaponModParagonDisplay.CombinedMagnitude(weapon, ambush, scale);
            var tourmaline = WeaponModTinkerSet.ReadSpecials(weapon).Single().Magnitude;

            Assert.AreNotEqual(tourmaline, combined, "fixture: the gems must change the figure");
            Assert.AreEqual(WeaponModDisplay.Effect(ambush, combined), mods[0].Effect);
            Assert.AreEqual(WeaponModDisplay.IntensityPercent(ambush, combined, scale, weapon), mods[0].IntensityPct);
        }

        [TestMethod]
        public void Both_TierA_RowMatchesDescribeAndTheModLinesBracket()
        {
            var weapon = Weapon(WeaponModId.Devastation);
            var devastation = Row(WeaponModId.Devastation);
            WeaponModTinkerSet.ApplySpecial(weapon, devastation, 3);

            var scale = WeaponModValue.MagnitudeScale();
            var combined = WeaponModParagonDisplay.CombinedMagnitude(weapon, devastation, scale);
            var row = MarketSnapshot.FromItem(weapon).Mods.Single();

            Assert.AreEqual(WeaponModDisplay.Effect(devastation, combined), row.Effect);
            Assert.AreEqual(WeaponModDisplay.IntensityPercent(devastation, combined, scale, weapon), row.IntensityPct);

            var bullet = WeaponModParagonDisplay.ModLines(weapon, scale)[0];
            var match = System.Text.RegularExpressions.Regex.Match(bullet, @"\[(\d+)%\]");

            Assert.IsTrue(match.Success, bullet);
            Assert.AreEqual(int.Parse(match.Groups[1].Value), row.IntensityPct, "the market row and the appraisal bullet agree");
        }

        // ================= the aura stays Tourmaline-only =================

        [TestMethod]
        public void QualityTier_IgnoresParagonGems()
        {
            // Ambush + Quickening perfect, SecondWind at 0.9: Tourmaline total 290, one short of Exceptional (300).
            // Two SecondWind gems would lift the COMBINED row to 100 and the total to 300 if Paragon counted.
            WeaponModId[] ids = { WeaponModId.Ambush, WeaponModId.Quickening, WeaponModId.SecondWind };

            WorldObject Build(params WeaponModId[] gems)
            {
                var w = Weapon(gems);

                WeaponModTinkerSet.ApplySpecialAtFraction(w, Row(ids[0]), 1.0);
                WeaponModTinkerSet.ApplySpecialAtFraction(w, Row(ids[1]), 1.0);
                WeaponModTinkerSet.ApplySpecialAtFraction(w, Row(ids[2]), 0.9);

                return w;
            }

            var plain = MarketSnapshot.FromItem(Build());
            var gemmed = MarketSnapshot.FromItem(Build(WeaponModId.SecondWind, WeaponModId.SecondWind, WeaponModId.Focus));

            Assert.AreNotEqual("Exceptional", plain.WeaponQualityTier, "fixture: the plain weapon sits below Exceptional");
            Assert.AreEqual(plain.WeaponQualityTier, gemmed.WeaponQualityTier, "Paragon gems must not move the tier");

            // the combined row did rise, so the discrimination is real
            var plainRow = plain.Mods.Single(m => m.Name == Row(WeaponModId.SecondWind).DisplayName);
            var gemmedRow = gemmed.Mods.Single(m => m.Name == Row(WeaponModId.SecondWind).DisplayName);
            Assert.IsTrue((gemmedRow.IntensityPct ?? 0) > (plainRow.IntensityPct ?? 0), "fixture: combined row rose");

            // a Paragon-only weapon has no tier at all
            Assert.IsNull(MarketSnapshot.FromItem(Weapon(WeaponModId.Ambush, WeaponModId.Focus, WeaponModId.Devastation, WeaponModId.SecondWind)).WeaponQualityTier);
        }

        // ================= non-Paragon unchanged =================

        [TestMethod]
        public void NonParagon_ProjectionIsTheLegacyOutput()
        {
            var weapon = Weapon();
            WeaponModTinkerSet.ApplySpecialAtFraction(weapon, Row(WeaponModId.Ambush), 0.5);
            WeaponModTinkerSet.ApplySpecial(weapon, Row(WeaponModId.Devastation), 4);

            // the pre-Paragon construction, then alphabetical by name (owner ruling 2026-11-25)
            var legacy = WeaponModTinkerSet.ReadSpecials(weapon)
                .Select(s => MarketItemMods.WeaponRow(s.Definition, s.Magnitude, weapon))
                .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            var snapshot = MarketSnapshot.FromItem(weapon);

            Assert.AreEqual(legacy.Count, snapshot.Mods.Count);

            for (var i = 0; i < legacy.Count; i++)
            {
                Assert.AreEqual(legacy[i].Kind, snapshot.Mods[i].Kind);
                Assert.AreEqual(legacy[i].Token, snapshot.Mods[i].Token);
                Assert.AreEqual(legacy[i].Name, snapshot.Mods[i].Name);
                Assert.AreEqual(legacy[i].IntensityPct, snapshot.Mods[i].IntensityPct);
                Assert.AreEqual(legacy[i].Effect, snapshot.Mods[i].Effect);
                Assert.IsNull(snapshot.Mods[i].Count);
            }

            Assert.AreEqual(MarketAppraisal.Description(weapon), snapshot.LongDesc, "no Paragon line on a non-Paragon weapon");
        }

        [TestMethod]
        public void NoMods_StillNull()
        {
            var snapshot = MarketSnapshot.FromItem(Weapon());

            Assert.IsNull(snapshot.Mods);
            Assert.IsNull(snapshot.WeaponQualityTier);
        }

        [TestMethod]
        public void WeaponSwitchOff_ParagonRowsAreAbsentToo()
        {
            var weapon = Weapon(WeaponModId.Ambush);
            var saved = MarketItemMods.WeaponModsEnabledSource;

            try
            {
                MarketItemMods.WeaponModsEnabledSource = () => false;
                var snapshot = MarketSnapshot.FromItem(weapon);

                Assert.IsNull(snapshot.Mods);
                Assert.AreEqual(MarketAppraisal.Description(weapon), snapshot.LongDesc, "the Paragon summary is gated by the same switch");
            }
            finally
            {
                MarketItemMods.WeaponModsEnabledSource = saved;
            }
        }
    }
}
