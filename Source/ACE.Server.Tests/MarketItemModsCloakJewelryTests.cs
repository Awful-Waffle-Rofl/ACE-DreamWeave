using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.Managers.Market;
using ACE.Server.Tests.KnotLoot;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.JewelryAspectTestKit;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The market mod rows for a cloak Mantle (Rear Guard, Proc Amplifier; kind "armor") and for Jewelry Aspects (kind
    /// "jewelry", one row per aspect type with a count). Row order, tokens, names, counts and effect text are the wire
    /// contract the market web app is built against, so they are pinned here byte for byte.
    /// </summary>
    [TestClass]
    public class MarketItemModsCloakJewelryTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void SeedTunables()
        {
            Seed();
            PropertyManager.ModifyBool("cloak_rear_guard_enabled", true);
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", 20.0);
        }

        [TestCleanup]
        public void RestoreTunables()
        {
            Restore();
            PropertyManager.ModifyBool("cloak_rear_guard_enabled", DefaultPropertyManager.DefaultBooleanProperties["cloak_rear_guard_enabled"].Item);
            PropertyManager.ModifyDouble("cloak_rear_guard_armor_per_level", DefaultPropertyManager.DefaultDoubleProperties["cloak_rear_guard_armor_per_level"].Item);
        }

        private static WorldObject LeveledCloak(int itemLevel)
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.SetProperty(PropertyInt64.ItemBaseXp, 100);
            cloak.SetProperty(PropertyInt.ItemMaxLevel, 10);
            cloak.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            cloak.SetProperty(PropertyInt64.ItemTotalXp, itemLevel * 100L);

            Assert.AreEqual(itemLevel, cloak.ItemLevel ?? 0, "control: the fixture must really carry that item level");

            return cloak;
        }

        private static WorldObject Ring(int attack, int defense, int utility) =>
            JewelryAspectApplicatorTests.Jewelry("Aspected Ring", EquipMask.FingerWearLeft | EquipMask.FingerWearRight, attack, defense, utility);

        private static List<ItemMod> Project(WorldObject item)
        {
            var snapshot = new ListingSnapshot();
            MarketItemMods.Apply(item, snapshot);
            return snapshot.Mods;
        }

        // ---------------- cloak ----------------

        [TestMethod]
        public void Cloak_WithBoth_RearGuardThenProcAmplifier_SharingTheAppraisalText()
        {
            var cloak = LeveledCloak(3);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.5);

            var mods = Project(cloak);

            Assert.AreEqual(2, mods.Count);
            Assert.AreEqual("armor", mods[0].Kind);
            Assert.AreEqual("Rear Guard", mods[0].Token);
            Assert.AreEqual("Rear Guard", mods[0].Name);
            Assert.IsNull(mods[0].IntensityPct);
            Assert.IsNull(mods[0].Count);
            Assert.AreEqual("60 armor vs attacks from behind (cloak level 3)", mods[0].Effect);
            Assert.AreEqual("armor", mods[1].Kind);
            Assert.AreEqual("Proc Amplifier", mods[1].Token);
            Assert.AreEqual("Proc Amplifier", mods[1].Name);
            Assert.AreEqual("+50%", mods[1].Effect);

            // ONE text source: each effect is exactly what follows the label on the appraisal line
            var lines = CloakMantleDisplay.GetAppraisalLines(cloak).ToList();
            Assert.AreEqual("Rear Guard: " + mods[0].Effect, lines[0]);
            Assert.AreEqual("Proc Amplifier: " + mods[1].Effect, lines[1]);
        }

        [TestMethod]
        public void Cloak_RearGuardOnly_OneRow()
        {
            var cloak = LeveledCloak(10);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);

            var mods = Project(cloak);

            Assert.AreEqual("Rear Guard", mods.Single().Name);
            Assert.AreEqual("200 armor vs attacks from behind (cloak level 10)", mods.Single().Effect);
        }

        [TestMethod]
        public void Cloak_RearGuardSwitchOff_NoRearGuardRow_AmplifierRowStays()
        {
            var cloak = LeveledCloak(3);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.5);

            PropertyManager.ModifyBool("cloak_rear_guard_enabled", false);

            var mods = Project(cloak);

            Assert.AreEqual("Proc Amplifier", mods.Single().Name, "a switched-off Mantle advertises nothing, like the jewelry rows");

            var guardOnly = LeveledCloak(3);
            guardOnly.SetProperty(PropertyBool.CloakRearGuard, true);

            Assert.IsNull(Project(guardOnly));

            PropertyManager.ModifyBool("cloak_rear_guard_enabled", true);
            Assert.AreEqual("Rear Guard", Project(guardOnly).Single().Name, "control: the row returns with the switch on");
        }

        [TestMethod]
        public void Cloak_AmplifierOnly_OneRow_AtTheRolledStrength()
        {
            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 1.5);

            var mods = Project(cloak);

            Assert.AreEqual("Proc Amplifier", mods.Single().Name);
            Assert.AreEqual("+150%", mods.Single().Effect, "the amplifier now rolls +100% to +200%");
        }

        [TestMethod]
        public void Cloak_WithNeither_NoRows_AndANonCloakCarryingTheProperties_NoRows()
        {
            Assert.IsNull(Project(UpgradedCloakTestItems.Item(cloak: true)));

            var notACloak = UpgradedCloakTestItems.Item(cloak: false);
            notACloak.SetProperty(PropertyBool.CloakRearGuard, true);
            notACloak.SetProperty(PropertyFloat.CloakProcAmplifier, 0.5);

            Assert.IsNull(Project(notACloak));
        }

        // ---------------- jewelry ----------------

        [TestMethod]
        public void Ring_AllThreeTypes_RowsInDefenseAttackUtilityOrder_WithCountsAndShareText()
        {
            var mods = Project(Ring(attack: 5, defense: 3, utility: 2));

            Assert.AreEqual(3, mods.Count);

            CollectionAssert.AreEqual(new[] { "jewelry", "jewelry", "jewelry" }, mods.Select(m => m.Kind).ToList());
            CollectionAssert.AreEqual(new[] { "Defense", "Attack", "Utility" }, mods.Select(m => m.Token).ToList());
            CollectionAssert.AreEqual(new[] { "Defense (Fortitude)", "Attack (Penetration)", "Utility (Resolve)" }, mods.Select(m => m.Name).ToList());
            CollectionAssert.AreEqual(new int?[] { 3, 5, 2 }, mods.Select(m => m.Count).ToList());
            Assert.IsTrue(mods.All(m => m.IntensityPct == null));
            Assert.AreEqual("3% less damage taken", mods[0].Effect);
            Assert.AreEqual("+4% elemental damage, or 4% of elemental resistance ignored if that is greater", mods[1].Effect);
            Assert.AreEqual("+4% chance on kill to remove a debuff", mods[2].Effect);
        }

        [TestMethod]
        public void Ring_EveryEffect_IsExactlyTheTextAfterTheLabelOnItsAppraisalLine()
        {
            var ring = Ring(attack: 5, defense: 3, utility: 2);
            var mods = Project(ring);
            var lines = JewelryAspectDisplay.GetAppraisalLines(ring).Skip(1).ToList();

            Assert.AreEqual(3, lines.Count);
            Assert.AreEqual("Fortitude: " + mods[0].Effect, lines[0]);
            Assert.AreEqual("Penetration: " + mods[1].Effect, lines[1]);
            Assert.AreEqual("Resolve: " + mods[2].Effect, lines[2]);
        }

        [TestMethod]
        public void Ring_OnlyTypesWithAtLeastOneAspectGetARow()
        {
            var mods = Project(Ring(attack: 0, defense: 1, utility: 0));

            Assert.AreEqual("Defense", mods.Single().Token);
            Assert.AreEqual(1, mods.Single().Count);

            mods = Project(Ring(attack: 4, defense: 0, utility: 6));

            CollectionAssert.AreEqual(new[] { "Attack", "Utility" }, mods.Select(m => m.Token).ToList());
        }

        [TestMethod]
        public void Ring_UtilityEffect_IsTheAppraisalResolveLineText()
        {
            var ring = Ring(attack: 0, defense: 0, utility: 8);

            Assert.AreEqual("+16% chance on kill to remove a debuff", Project(ring).Single().Effect);
            Assert.AreEqual("Resolve: +16% chance on kill to remove a debuff", JewelryAspectDisplay.GetAppraisalLines(ring).Last());
        }

        [TestMethod]
        public void Ring_WithoutAspects_NoRows_AndTheSwitchOffHidesThem()
        {
            Assert.IsNull(Project(Ring(0, 0, 0)));
            Assert.IsNull(Project(JewelryAspectApplicatorTests.Jewelry("Plain Ring", EquipMask.FingerWearLeft)));

            PropertyManager.ModifyBool("jewelry_aspects_enabled", false);

            Assert.IsNull(Project(Ring(2, 2, 2)), "a switched-off system advertises nothing, like the appraisal");
        }

        // ---------------- wire format, version, search ----------------

        [TestMethod]
        public void Json_CarriesCountAndTheJewelryKind_AndOmitsCountElsewhere()
        {
            var snapshot = new ListingSnapshot { Mods = Project(Ring(attack: 0, defense: 3, utility: 0)) };

            var json = JsonSerializer.Serialize(snapshot, MarketSnapshot.JsonOptions);

            StringAssert.Contains(json, "\"kind\":\"jewelry\"");
            StringAssert.Contains(json, "\"token\":\"Defense\"");
            StringAssert.Contains(json, "\"name\":\"Defense (Fortitude)\"");
            StringAssert.Contains(json, "\"count\":3");
            Assert.IsFalse(json.Contains("intensity_pct"), "null intensity is omitted");

            var cloak = LeveledCloak(3);
            cloak.SetProperty(PropertyBool.CloakRearGuard, true);

            var cloakJson = JsonSerializer.Serialize(new ListingSnapshot { Mods = Project(cloak) }, MarketSnapshot.JsonOptions);

            StringAssert.Contains(cloakJson, "\"kind\":\"armor\"");
            Assert.IsFalse(cloakJson.Contains("\"count\""), "count is null on a cloak row");

            var roundTrip = JsonSerializer.Deserialize<ListingSnapshot>(json, MarketSnapshot.JsonOptions);
            Assert.AreEqual(3, roundTrip.Mods.Single().Count);
        }

        [TestMethod]
        public void FromItem_ProjectsTheRowsAndStampsTheCurrentVersion()
        {
            var snapshot = MarketSnapshot.FromItem(Ring(attack: 1, defense: 0, utility: 0));

            Assert.AreEqual(9, MarketSnapshot.CurrentVersion);
            Assert.AreEqual(MarketSnapshot.CurrentVersion, snapshot.SnapshotVersion);
            Assert.AreEqual("Attack (Penetration)", snapshot.Mods.Single().Name);
        }

        [TestMethod]
        public void SearchNames_CoverTheNewNames_AndTheOneArgumentSearchTextLeavesJewelryOut()
        {
            CollectionAssert.AreEqual(
                new[] { "Defense (Fortitude)", "Utility (Resolve)" },
                MarketItemMods.SearchNames(Ring(attack: 0, defense: 4, utility: 6)).ToList());

            var cloak = UpgradedCloakTestItems.Item(cloak: true);
            cloak.SetProperty(PropertyFloat.CloakProcAmplifier, 1.0);

            CollectionAssert.AreEqual(new[] { "Proc Amplifier" }, MarketItemMods.SearchNames(cloak).ToList());
            Assert.AreEqual(0, MarketItemMods.SearchNames(Ring(0, 0, 0)).Count);

            // the roller filter's overload keeps its old text (lead type lives in the row name); the vault search opts in
            var ring = Ring(attack: 0, defense: 4, utility: 6);
            Assert.AreEqual(0, MarketItemMods.SearchNames(ring, includeJewelryMods: false).Count);
            Assert.IsFalse(ItemTextSearch.SearchText(ring).Contains("Fortitude"));
            StringAssert.Contains(ItemTextSearch.SearchText(ring, includeJewelryMods: true), "Defense (Fortitude)");
        }
    }
}