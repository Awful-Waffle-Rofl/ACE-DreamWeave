using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers.Market;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Referencing Player.MaterialSalvage (via MarketSalvageMaterials's wcidByMaterial field
    /// initializer) is, in this assembly, the first thing that forces the CLR to run Player's own
    /// static type initializer - which unconditionally calls DatabaseManager.World.GetCachedWeenie
    /// for "portalmarketplace" and 10 PK-arena portal names (Player_Location.cs). With no live world
    /// database this NREs deep inside WorldDbContext.OnConfiguring. MuleSummonTests.cs and
    /// PortalDestinationGuardTests.cs hit the identical trap and fix it the identical way: seed those
    /// weenie names straight into WorldDatabaseWithEntityCache's private caches by reflection before
    /// anything in this class touches Player, so the lookup resolves in-memory and never reaches a DB.
    /// </summary>
    [TestClass]
    public class MarketSalvageMaterialsTests
    {
        private static uint nextWcid = 90900;

        private static bool playerStaticFieldsSeeded;

        private static void EnsurePlayerStaticFieldsSeeded()
        {
            if (playerStaticFieldsSeeded)
                return;

            var cacheField = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(cacheField, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");
            var nameField = typeof(WorldDatabaseWithEntityCache).GetField("weenieClassNameToClassIdCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(nameField, "WorldDatabaseWithEntityCache.weenieClassNameToClassIdCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)cacheField.GetValue(DatabaseManager.World);
            var nameCache = (ConcurrentDictionary<string, uint>)nameField.GetValue(DatabaseManager.World);

            foreach (var name in new[]
            {
                "portalmarketplace",
                "portalpkarenanew1", "portalpkarenanew2", "portalpkarenanew3", "portalpkarenanew4", "portalpkarenanew5",
                "portalpklarenanew1", "portalpklarenanew2", "portalpklarenanew3", "portalpklarenanew4", "portalpklarenanew5",
            })
            {
                var wcid = ++nextWcid;
                cache[wcid] = new Weenie { WeenieClassId = wcid, WeenieType = WeenieType.Generic };
                nameCache[name.ToLower()] = wcid;
            }

            playerStaticFieldsSeeded = true;
        }

        private Func<uint, Weenie> savedWeenieLookup;

        [TestInitialize]
        public void TestInitialize()
        {
            EnsurePlayerStaticFieldsSeeded();
            savedWeenieLookup = MarketManager.WeenieLookup;
            MarketManager.WeenieLookup = _ => null;
        }

        [TestCleanup]
        public void TestCleanup()
        {
            MarketManager.WeenieLookup = savedWeenieLookup;
        }

        [TestMethod]
        public void All_Has72Materials_AndNoCategoryHeaders()
        {
            var all = MarketSalvageMaterials.All();
            Assert.AreEqual(72, all.Count);
            foreach (var header in new[] { 3, 9, 56, 65, 72 })
                Assert.IsFalse(all.Any(m => m.MaterialType == header), $"category header {header} must not be orderable");
            Assert.IsFalse(MarketSalvageMaterials.TryGet(0, out _));
            Assert.IsTrue(MarketSalvageMaterials.TryGet((int)MaterialType.Granite, out var granite));
            Assert.AreEqual("Granite", granite.MaterialName);
            Assert.AreEqual((uint)Player.MaterialSalvage[(int)MaterialType.Granite], granite.Wcid);
        }

        [TestMethod]
        public void All_IsSortedByName()
        {
            var names = MarketSalvageMaterials.All().Select(m => m.MaterialName).ToList();
            CollectionAssert.AreEqual(names.OrderBy(n => n, StringComparer.Ordinal).ToList(), names);
        }

        [TestMethod]
        public void IsFullBagOf_RequiresTinkeringMaterial_Material_FullStructure_AndNotATool()
        {
            var bag = FakeVaultWorld.MakeSalvageBag(21013, 100, 40, 10, 100);
            bag.MaterialType = MaterialType.Granite;
            Assert.IsTrue(MarketSalvageMaterials.IsFullBagOf(bag, (int)MaterialType.Granite));
            Assert.IsFalse(MarketSalvageMaterials.IsFullBagOf(bag, (int)MaterialType.Iron), "wrong material");

            var partial = FakeVaultWorld.MakeSalvageBag(21013, 99, 40, 10, 100);
            partial.MaterialType = MaterialType.Granite;
            Assert.IsFalse(MarketSalvageMaterials.IsFullBagOf(partial, (int)MaterialType.Granite), "partial");

            bag.SetProperty(PropertyInt.SalvageToolCharges, 10);
            Assert.IsFalse(MarketSalvageMaterials.IsFullBagOf(bag, (int)MaterialType.Granite), "a Hammer is not a bag");

            Assert.IsFalse(MarketSalvageMaterials.IsFullBagOf(null, (int)MaterialType.Granite));
        }

        // ---- salvage HAMMER orders ----

        private const int TourmalineHammerCapacity = 10;

        /// <summary>
        /// A full-charge Hammer: TinkeringMaterial like a bag, the SAME MaterialType as its bag, but
        /// carrying SalvageToolCharges and a MaxStructure of 10 rather than the bag's 100.
        /// </summary>
        private static WorldObject Hammer(MaterialType material, int charges, int capacity = TourmalineHammerCapacity)
        {
            var hammer = FakeVaultWorld.MakeSalvageBag(1001910, charges, 40, 10, 100);
            hammer.MaterialType = material;
            hammer.MaxStructure = (ushort)capacity;
            hammer.SetProperty(PropertyInt.SalvageToolCharges, capacity);
            return hammer;
        }

        private static WorldObject Bag(MaterialType material, int structure = 100)
        {
            var bag = FakeVaultWorld.MakeSalvageBag(21013, structure, 40, 10, 100);
            bag.MaterialType = material;
            return bag;
        }

        /// <summary>
        /// The hammer material list is PROJECTED off SalvageForge.MaterialTable, never copied. Asserted
        /// against that dictionary rather than against a literal five, so a sixth Hammer added to the
        /// forge shows up here automatically instead of failing a hard-coded count.
        ///
        /// The Hollow Hammer (1002650) must not appear: it was never a MaterialTable member, which is
        /// why no exclusion filter exists anywhere for it.
        /// </summary>
        [TestMethod]
        public void AllHammers_IsExactlyTheSalvageForgeMaterialTable()
        {
            var hammers = MarketSalvageMaterials.AllHammers();

            CollectionAssert.AreEquivalent(
                SalvageForge.MaterialTable.Keys.Select(m => (int)m).ToList(),
                hammers.Select(h => h.MaterialType).ToList(),
                "the hammer material list must be the forge's table, not a copy of it");

            foreach (var entry in hammers)
            {
                Assert.AreEqual(MarketBuyOrderKind.SalvageHammer, entry.Kind);
                Assert.AreEqual(SalvageForge.MaterialTable[(MaterialType)entry.MaterialType].HammerWcid, entry.Wcid,
                    $"{entry.MaterialName} must carry the HAMMER wcid, not the bag's");
                Assert.IsTrue(entry.HasHammer);
            }

            Assert.IsFalse(hammers.Any(h => h.Wcid == SalvageForge.HollowHammerWcid),
                "the Hollow Hammer is a crafting tool, not a salvage hammer");
        }

        /// <summary>
        /// has_hammer on GET /v1/materials. Granite is the discriminator on the false side: it is a
        /// perfectly orderable BAG material with no Hammer, so a flag that was simply always true would
        /// pass every other assertion here.
        /// </summary>
        [TestMethod]
        public void All_CarriesHasHammer_ForExactlyTheHammerMaterials()
        {
            var all = MarketSalvageMaterials.All();
            var flagged = all.Where(m => m.HasHammer).Select(m => m.MaterialType).ToList();

            CollectionAssert.AreEquivalent(SalvageForge.MaterialTable.Keys.Select(m => (int)m).ToList(), flagged);

            Assert.IsTrue(MarketSalvageMaterials.HasHammer((int)MaterialType.Tourmaline));
            Assert.IsFalse(MarketSalvageMaterials.HasHammer((int)MaterialType.Granite));

            // Every row still describes the BAG, whatever has_hammer says.
            foreach (var m in all)
                Assert.AreEqual(MarketBuyOrderKind.SalvageBag, m.Kind);
        }

        /// <summary>
        /// The kind-aware lookup, which is what refuses a hammer order for a material that has no
        /// Hammer at placement instead of accepting an order nothing in the world could ever fill.
        /// </summary>
        [TestMethod]
        public void TryGet_HammerKind_ResolvesTheHammerWcid_AndRefusesAMaterialWithNoHammer()
        {
            Assert.IsTrue(MarketSalvageMaterials.TryGet((int)MaterialType.Tourmaline, MarketBuyOrderKind.SalvageHammer, out var hammer));
            Assert.AreEqual(1001910u, hammer.Wcid, "the Tourmaline Hammer's wcid, not the Tourmaline bag's");

            Assert.IsTrue(MarketSalvageMaterials.TryGet((int)MaterialType.Tourmaline, MarketBuyOrderKind.SalvageBag, out var bag));
            Assert.AreEqual((uint)Player.MaterialSalvage[(int)MaterialType.Tourmaline], bag.Wcid);
            Assert.AreNotEqual(bag.Wcid, hammer.Wcid, "the two kinds must not resolve to the same wcid");

            // Granite HAS salvage, so this is not the invalid-material case - it is the no-hammer one.
            Assert.IsTrue(MarketSalvageMaterials.TryGet((int)MaterialType.Granite, MarketBuyOrderKind.SalvageBag, out _));
            Assert.IsFalse(MarketSalvageMaterials.TryGet((int)MaterialType.Granite, MarketBuyOrderKind.SalvageHammer, out _));

            // And a material the market knows no salvage of at all fails for both kinds.
            Assert.IsFalse(MarketSalvageMaterials.TryGet(999, MarketBuyOrderKind.SalvageBag, out _));
            Assert.IsFalse(MarketSalvageMaterials.TryGet(999, MarketBuyOrderKind.SalvageHammer, out _));
        }

        /// <summary>
        /// The hammer predicate: the bag rule with the salvage-tool clause inverted, plus FULL CHARGES.
        /// The 7-of-10 case is the one the design decision turns on - a part-spent Hammer is usable but
        /// is worth less than a fresh one, and the buyer cannot inspect it before the fill.
        /// </summary>
        [TestMethod]
        public void IsFullHammerOf_RequiresATool_TheMaterial_AndAFullChargeCount()
        {
            var full = Hammer(MaterialType.Tourmaline, 10);
            Assert.IsTrue(MarketSalvageMaterials.IsFullHammerOf(full, (int)MaterialType.Tourmaline));

            var partCharge = Hammer(MaterialType.Tourmaline, 7);
            Assert.IsFalse(MarketSalvageMaterials.IsFullHammerOf(partCharge, (int)MaterialType.Tourmaline),
                "7 of 10 charges must not fill a hammer order");

            Assert.IsFalse(MarketSalvageMaterials.IsFullHammerOf(full, (int)MaterialType.Granite), "wrong material");

            var bag = Bag(MaterialType.Tourmaline);
            Assert.IsFalse(MarketSalvageMaterials.IsFullHammerOf(bag, (int)MaterialType.Tourmaline),
                "a full BAG is not a hammer, however full it is");

            // A tool with no MaxStructure at all is malformed, and is refused rather than assumed full.
            var noCapacity = Hammer(MaterialType.Tourmaline, 10);
            noCapacity.MaxStructure = null;
            Assert.IsFalse(MarketSalvageMaterials.IsFullHammerOf(noCapacity, (int)MaterialType.Tourmaline));

            Assert.IsFalse(MarketSalvageMaterials.IsFullHammerOf(null, (int)MaterialType.Tourmaline));
        }

        /// <summary>
        /// THE DISCRIMINATION THIS WHOLE FEATURE TURNS ON. A hammer and its bag share an ItemType and a
        /// MaterialType, so a predicate that forgot the kind would match both. Both objects are of the
        /// SAME material here on purpose: a fixture using two different materials would pass even if
        /// MatchesOrder ignored the kind entirely and matched on material alone.
        /// </summary>
        [TestMethod]
        public void MatchesOrder_TellsABagAndAHammerOfTheSameMaterialApart()
        {
            var material = (int)MaterialType.Tourmaline;
            var bag = Bag(MaterialType.Tourmaline);
            var hammer = Hammer(MaterialType.Tourmaline, 10);

            Assert.IsTrue(MarketSalvageMaterials.MatchesOrder(bag, material, MarketBuyOrderKind.SalvageBag));
            Assert.IsFalse(MarketSalvageMaterials.MatchesOrder(hammer, material, MarketBuyOrderKind.SalvageBag),
                "a bag order must never be filled with a Hammer");

            Assert.IsTrue(MarketSalvageMaterials.MatchesOrder(hammer, material, MarketBuyOrderKind.SalvageHammer));
            Assert.IsFalse(MarketSalvageMaterials.MatchesOrder(bag, material, MarketBuyOrderKind.SalvageHammer),
                "a hammer order must never be filled with a bag");

            // An unrecognised kind matches nothing rather than falling through to the bag rule.
            Assert.IsFalse(MarketSalvageMaterials.MatchesOrder(bag, material, (MarketBuyOrderKind)99));
            Assert.IsFalse(MarketSalvageMaterials.MatchesOrder(hammer, material, (MarketBuyOrderKind)99));
        }
    }
}
