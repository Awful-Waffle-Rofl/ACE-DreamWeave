using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.DatLoader;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Command.Handlers;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Factories;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The counted vault storage tier's safety core: the predicate that decides when two stored items
    /// are interchangeable, the canonical key derived from that decision, and the one materializer that
    /// rebuilds an item from it.
    ///
    /// WHAT DISCRIMINATES AND WHAT PINS, stated up front because the distinction is not visible from a
    /// green run. TryDescribeClass, VaultItemClass and VaultReferenceCache are pure additions, so no
    /// test here can be shown to fail against the tree before them - the types would not compile. What
    /// each test below IS is written on it:
    ///
    /// - DISCRIMINATING against a plausible wrong implementation: every refusal test (each one fails if
    ///   the corresponding guard is removed or weakened), the round trip (fails if the materializer
    ///   drops any payload field or skips the absent case), the band tests (fail if Value is keyed at
    ///   band 0 or ignored at band 25), the coverage-guard test (fails if the guard stops throwing or
    ///   if TryDescribeClass swallows it into a true), and the reference-cache identity test (fails if
    ///   the cache stops caching or starts handing out a real guid).
    /// - PINS, which assert a chosen encoding rather than a behaviour: the canonical-form and class-key
    ///   literals. Their job is to fail loudly if the encoding ever changes, because a changed encoding
    ///   invalidates every stored class key.
    ///
    /// Every refusal test also asserts the ITEM DID NOT CHANGE. The predicate is read-only, and its
    /// consumers destroy biotas, so a predicate that quietly wrote a property while deciding would be
    /// corrupting the very items it was asked about.
    /// </summary>
    [TestClass]
    public class VaultItemClassTests
    {
        #region wcids and fixtures

        // Private probe wcids, deliberately outside anything the world database ships, so seeding them
        // into the weenie cache cannot collide with a real weenie another test class relies on.
        private const uint PlainBagWcid = 990201;
        private const uint OtherBagWcid = 990202;
        private const uint BuggedStructureBagWcid = 990203;
        private const uint EquippableProbeWcid = 990204;

        /// <summary>
        /// A probe whose template carries a ClothingBase, which is what sends CalculateObjDesc into the
        /// branch that reads DatManager.PortalDat. With no dats loaded that read throws, which is the
        /// only way to observe a render failure without a client installation.
        /// </summary>
        private const uint ClothingBaseProbeWcid = 990205;

        /// <summary>The real mod-hammer ClothingBase, as VaultCollapseTests records it from ace_world.</summary>
        private const uint ProbeClothingBase = 268435776;

        /// <summary>The bugged-template Structure. Salvage weenies 20988 and 21050 really ship one; Player_Crafting.GetSalvageBag:442 clears it on every bag it makes.</summary>
        private const int BuggedTemplateStructure = 57;

        private static int nextGuid = 0x7E100000;

        private static uint NextGuid() => (uint)Interlocked.Increment(ref nextGuid);

        /// <summary>
        /// The real salvage-bag template shape, as AccountVaultFakes.MakeSalvageBag documents it from
        /// ace_world: ItemType.TinkeringMaterial, MaxStackSize 1, MaxStructure 100. WeenieType.CraftTool
        /// rather than Stackable, because that is what salvage weenies really carry and it is what makes
        /// the real runtime type a CraftTool - which derives from Stackable, and is exactly why the
        /// predicate cannot test `item is Stackable`.
        /// </summary>
        private static Weenie SalvageWeenie(uint wcid, bool withTemplateStructure = false, bool withClothingBase = false)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ItemType, (int)ItemType.TinkeringMaterial },
                { PropertyInt.StackSize, 1 },
                { PropertyInt.MaxStackSize, 1 },
                { PropertyInt.StackUnitEncumbrance, 100 },
                { PropertyInt.StackUnitValue, 0 },
                { PropertyInt.EncumbranceVal, 100 },
                { PropertyInt.MaxStructure, 100 },
                { PropertyInt.MaterialType, (int)MaterialType.Silver },
            };

            if (withTemplateStructure)
                ints[PropertyInt.Structure] = BuggedTemplateStructure;

            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                ClassName = $"vaultclassprobe{wcid}",
                WeenieType = WeenieType.CraftTool,
                PropertiesInt = ints,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Salvage" } },
            };

            if (withClothingBase)
                weenie.PropertiesDID = new Dictionary<PropertyDataId, uint> { { PropertyDataId.ClothingBase, ProbeClothingBase } };

            return weenie;
        }

        /// <summary>Same reflection seam VaultCollapseTests.SeedHammerWeenie uses, so no live ace_world is needed.</summary>
        private static void SeedWeenie(Weenie weenie)
        {
            var field = typeof(WorldDatabaseWithEntityCache).GetField("weenieCache", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.IsNotNull(field, "WorldDatabaseWithEntityCache.weenieCache was not found by reflection - has it been renamed?");

            var cache = (ConcurrentDictionary<uint, Weenie>)field.GetValue(DatabaseManager.World);
            cache[weenie.WeenieClassId] = weenie;
        }

        /// <summary>
        /// Seeds GuidManager's private dynamicAlloc so WorldObjectFactory.CreateNewWorldObject - which
        /// the materializer uses - can allocate without a shard database. Same technique and same
        /// reasoning as VaultCollapseTests.EnsureGuidManagerConstructible; kept local so this class does
        /// not depend on that one having run.
        /// </summary>
        private static void EnsureGuidManagerConstructible()
        {
            var allocatorType = typeof(GuidManager).GetNestedType("DynamicGuidAllocator", BindingFlags.NonPublic);
            Assert.IsNotNull(allocatorType, "GuidManager.DynamicGuidAllocator was not found by reflection - has it been renamed?");

            var existing = typeof(GuidManager).GetField("dynamicAlloc", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(existing, "GuidManager.dynamicAlloc was not found by reflection - has it been renamed?");

            if (existing.GetValue(null) != null)
                return;

            var allocator = FormatterServices.GetUninitializedObject(allocatorType);

            void SetField(string name, object value) =>
                allocatorType.GetField(name, BindingFlags.NonPublic | BindingFlags.Instance).SetValue(allocator, value);

            SetField("min", ObjectGuid.DynamicMin);
            SetField("max", ObjectGuid.DynamicMax);
            SetField("current", 0x7C000000u);
            SetField("name", "test-dynamic");
            SetField("recycledGuids", Activator.CreateInstance(typeof(Queue<Tuple<DateTime, uint>>)));

            var availableIdsFieldType = allocatorType.GetField("availableIDs", BindingFlags.NonPublic | BindingFlags.Instance).FieldType;
            SetField("availableIDs", Activator.CreateInstance(availableIdsFieldType));
            SetField("useSequenceGapExhaustedMessageDisplayed", false);

            existing.SetValue(null, allocator);
        }

        [TestInitialize]
        public void TestInit()
        {
            SeedWeenie(SalvageWeenie(PlainBagWcid));
            SeedWeenie(SalvageWeenie(OtherBagWcid));
            SeedWeenie(SalvageWeenie(BuggedStructureBagWcid, withTemplateStructure: true));
            SeedWeenie(SalvageWeenie(EquippableProbeWcid));
            SeedWeenie(SalvageWeenie(ClothingBaseProbeWcid, withClothingBase: true));

            EnsureGuidManagerConstructible();

            // The reference cache is process-wide and these wcids are re-seeded per test, so a
            // reference built from a previous test's template must not survive into this one.
            VaultReferenceCache.Invalidate();
        }

        /// <summary>
        /// A stored bag: a fresh instance of the template plus exactly the five properties the salvage
        /// path writes (Player_Crafting.cs:332, :366, :367, :369 and :243).
        /// </summary>
        private static CraftTool MakeBag(uint wcid, int? structure, int? workmanship, int? numItems, int? value, string name = null)
        {
            var weenie = DatabaseManager.World.GetCachedWeenie(wcid);
            Assert.IsNotNull(weenie, $"probe weenie {wcid} was not seeded");

            var bag = new CraftTool(weenie, new ObjectGuid(NextGuid()));

            bag.Structure = structure.HasValue ? (ushort?)structure.Value : (ushort?)null;
            bag.ItemWorkmanship = workmanship;
            bag.NumItemsInMaterial = numItems;
            bag.Value = value;

            if (name != null)
                bag.Name = name;
            else if (structure.HasValue)
                bag.Name = $"Salvage ({structure.Value})";

            return bag;
        }

        /// <summary>
        /// A deterministic snapshot of everything the predicate could plausibly write. Compared before
        /// and after every refusal so a predicate that mutated while deciding would fail loudly.
        ///
        /// Deliberately does NOT go through WorldObject.Workmanship: that getter's legacy-recovery
        /// branch WRITES ItemWorkmanship (WorldObject_Properties.cs:1638), so a snapshot built with it
        /// would be the mutation it was meant to detect.
        /// </summary>
        private static string Snapshot(WorldObject item)
        {
            var text = new StringBuilder();

            text.Append("wcid=").Append(item.WeenieClassId.ToString(CultureInfo.InvariantCulture));
            text.Append(";type=").Append(item.WeenieType);

            void Ints(IDictionary<PropertyInt, int> dict)
            {
                if (dict == null)
                    return;

                foreach (var kvp in dict.OrderBy(k => (int)k.Key))
                    text.Append(";I").Append((int)kvp.Key).Append('=').Append(kvp.Value.ToString(CultureInfo.InvariantCulture));
            }

            Ints(item.Biota.PropertiesInt);

            if (item.Biota.PropertiesString != null)
            {
                foreach (var kvp in item.Biota.PropertiesString.OrderBy(k => (int)k.Key))
                    text.Append(";S").Append((int)kvp.Key).Append('=').Append(kvp.Value?.Length ?? -1).Append(':').Append(kvp.Value);
            }

            if (item.Biota.PropertiesFloat != null)
            {
                foreach (var kvp in item.Biota.PropertiesFloat.OrderBy(k => (int)k.Key))
                    text.Append(";F").Append((int)kvp.Key).Append('=').Append(kvp.Value.ToString("R", CultureInfo.InvariantCulture));
            }

            if (item.Biota.PropertiesDID != null)
            {
                foreach (var kvp in item.Biota.PropertiesDID.OrderBy(k => (int)k.Key))
                    text.Append(";D").Append((int)kvp.Key).Append('=').Append(kvp.Value.ToString(CultureInfo.InvariantCulture));
            }

            text.Append(";spells=").Append(item.Biota.PropertiesSpellBook?.Count ?? 0);
            text.Append(";ench=").Append(item.Biota.PropertiesEnchantmentRegistry?.Count ?? 0);
            text.Append(";attr=").Append(item.Biota.PropertiesAttribute?.Count ?? 0);

            return text.ToString();
        }

        /// <summary>Runs the predicate and asserts it refused for the stated reason, having changed nothing.</summary>
        private static void AssertRefuses(WorldObject item, string expectedReason, string because)
        {
            var before = Snapshot(item);

            var described = VaultCollapse.TryDescribeClass(item, out var overrides, out var reason);

            Assert.IsFalse(described, because);
            Assert.IsNull(overrides, "a refusal must never hand back a payload");
            Assert.AreEqual(expectedReason, reason, $"refusal reason for: {because}");
            Assert.AreEqual(before, Snapshot(item), "the predicate must not change the item it is asked about");
        }

        #endregion

        #region The predicate accepts what it must

        [TestMethod]
        public void Predicate_OrdinarySalvageBag_IsClassifiableAndCarriesTheFiveWrittenProperties()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);

            Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out var overrides, out var reason), $"refused: {reason}");

            Assert.AreEqual(87, overrides.GetInt(PropertyInt.Structure));
            Assert.AreEqual(640, overrides.GetInt(PropertyInt.ItemWorkmanship));
            Assert.AreEqual(100, overrides.GetInt(PropertyInt.NumItemsInMaterial));
            Assert.AreEqual(4213, overrides.GetInt(PropertyInt.Value));
            Assert.AreEqual("Salvage (87)", overrides.GetString(PropertyString.Name));

            // The payload carries EVERY whitelisted key, absent ones included, or the materializer
            // could not tell "leave the template's value alone" from "remove it".
            Assert.AreEqual(4, overrides.Ints.Count);
            Assert.AreEqual(1, overrides.Strings.Count);
        }

        [TestMethod]
        public void Predicate_BagWhoseTemplateCarriesAStructureItDoesNot_StillClassifies_AndRecordsTheAbsence()
        {
            // The real shape behind salvage weenies 20988 and 21050: the template ships a Structure and
            // GetSalvageBag clears it on every bag. The payload must record ABSENT, not "no opinion".
            var bag = MakeBag(BuggedStructureBagWcid, structure: null, workmanship: 300, numItems: 50, value: 900, name: "Salvage (0)");

            Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out var overrides, out var reason), $"refused: {reason}");
            Assert.IsNull(overrides.GetInt(PropertyInt.Structure), "an absent property must be recorded as absent");

            var rebuilt = VaultItemClass.Materialize(BuggedStructureBagWcid, overrides, pooledValue: null);

            Assert.IsNotNull(rebuilt);
            Assert.IsNull(rebuilt.Structure, "the materializer must REMOVE a property the payload records as absent, not leave the template's");

            var diff = VaultCollapse.Diff(rebuilt.Biota, bag.Biota, candidateIsStackable: false);
            CollectionAssert.AreEqual(new List<string>(), diff, string.Join(" | ", diff));
        }

        #endregion

        #region The theorem, as a test

        /// <summary>
        /// The lossless-and-reversible property, over a corpus: for every classifiable item, replaying
        /// its payload onto a fresh template reproduces it exactly.
        ///
        /// DISCRIMINATING. Drop any one field from VaultItemClass.Materialize's replay - or skip the
        /// absent case - and a corpus entry fails here.
        /// </summary>
        [TestMethod]
        public void ClassRoundTrip_MaterializedItem_DiffsEmptyAgainstTheOriginal()
        {
            var corpus = new List<CraftTool>
            {
                MakeBag(PlainBagWcid, structure: 100, workmanship: 700, numItems: 100, value: 12000),
                MakeBag(PlainBagWcid, structure: 1, workmanship: 5, numItems: 1, value: 3),
                MakeBag(PlainBagWcid, structure: 43, workmanship: 258, numItems: 43, value: 0),
                MakeBag(PlainBagWcid, structure: 87, workmanship: null, numItems: null, value: 41),
                MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213, name: "Bob's Lucky Silver"),
                MakeBag(PlainBagWcid, structure: 100, workmanship: 1000, numItems: 100, value: int.MaxValue),
                MakeBag(OtherBagWcid, structure: 55, workmanship: 330, numItems: 55, value: 777),
                MakeBag(BuggedStructureBagWcid, structure: null, workmanship: 1, numItems: 1, value: 1, name: "Salvage (0)"),
            };

            foreach (var original in corpus)
            {
                Assert.IsTrue(VaultCollapse.TryDescribeClass(original, out var overrides, out var reason),
                    $"corpus entry 0x{original.Guid.Full:X8} was refused: {reason}");

                var rebuilt = VaultItemClass.Materialize(original.WeenieClassId, overrides, pooledValue: null);

                Assert.IsNotNull(rebuilt, "the materializer must produce an item for a wcid the predicate accepted");

                var diff = VaultCollapse.Diff(rebuilt.Biota, original.Biota, candidateIsStackable: false);

                CollectionAssert.AreEqual(new List<string>(), diff,
                    $"rebuilding 0x{original.Guid.Full:X8} did not reproduce it: {string.Join(" | ", diff)}");
            }
        }

        [TestMethod]
        public void Materialize_PooledValueWinsOverThePayload_AndChangesNothingElse()
        {
            var original = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);

            Assert.IsTrue(VaultCollapse.TryDescribeClass(original, out var overrides, out _));

            var rebuilt = VaultItemClass.Materialize(PlainBagWcid, overrides, pooledValue: 999);

            Assert.AreEqual(999, rebuilt.Value, "a pooled share must override the payload's own Value");

            var diff = VaultCollapse.Diff(rebuilt.Biota, original.Biota, candidateIsStackable: false);

            Assert.AreEqual(1, diff.Count, string.Join(" | ", diff));
            StringAssert.Contains(diff[0], "PropertiesInt.Value:", "the pooled share must be the ONLY difference");
        }

        #endregion

        #region Refusals - every one of these is discriminating

        [TestMethod]
        public void Refuses_TinkeredBag()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            bag.NumTimesTinkered = 3;

            AssertRefuses(bag, "PropertiesInt.NumTimesTinkered", "a tinkered item must keep its own biota");
        }

        [TestMethod]
        public void Refuses_EnchantedBag()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            bag.Biota.PropertiesEnchantmentRegistry = new List<PropertiesEnchantmentRegistry> { new PropertiesEnchantmentRegistry() };

            AssertRefuses(bag, "PropertiesEnchantmentRegistry", "an item carrying live enchantments must keep its own biota");
        }

        [TestMethod]
        public void Refuses_SpellbookedBag()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            bag.Biota.PropertiesSpellBook = new Dictionary<int, float> { { 1234, 2.0f } };

            AssertRefuses(bag, "PropertiesSpellBook.1234", "an item carrying a spell must keep its own biota");
        }

        [TestMethod]
        public void Refuses_EquippableItem()
        {
            var bag = MakeBag(EquippableProbeWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            bag.ValidLocations = EquipMask.MeleeWeapon;

            AssertRefuses(bag, VaultCollapse.ClassRefusal.Equippable, "nothing equippable may become a counted class row");
        }

        [TestMethod]
        public void Refuses_StackedItem_AndAnItemThatCouldStack()
        {
            var stacked = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            stacked.StackSize = 5;

            AssertRefuses(stacked, VaultCollapse.ClassRefusal.Stacked, "a class row counts items and cannot express a stack size");

            var couldStack = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            couldStack.SetProperty(PropertyInt.MaxStackSize, 10);

            AssertRefuses(couldStack, VaultCollapse.ClassRefusal.Stacked, "an item that COULD stack is refused even while it holds one unit");
        }

        /// <summary>
        /// The C# type is NOT the stack test, and this is the guard on that.
        ///
        /// Every real salvage bag is a WeenieType.CraftTool, and CraftTool derives from Stackable
        /// (CraftTool.cs:9). A predicate written as `item is Stackable` would therefore refuse every
        /// single bag and the whole storage tier would collapse nothing, silently.
        /// </summary>
        [TestMethod]
        public void OrdinaryBagIsAStackableSubclass_AndIsStillClassifiable()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);

            Assert.IsInstanceOfType(bag, typeof(Stackable), "precondition: a real salvage bag IS a Stackable subclass, so the type cannot be the test");
            Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out _, out var reason), $"refused: {reason}");
        }

        [TestMethod]
        public void Refuses_TimedItem()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            bag.Lifespan = 3600;

            AssertRefuses(bag, VaultCollapse.ClassRefusal.Timed, "destroying and rebuilding a timed item restarts its clock");
        }

        [TestMethod]
        public void Refuses_AnythingThatIsNotExactlyTinkeringMaterial()
        {
            var misc = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            misc.ItemType = ItemType.Misc;

            AssertRefuses(misc, VaultCollapse.ClassRefusal.NotTinkeringMaterial, "only TinkeringMaterial may be classed");

            var combined = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            combined.ItemType = ItemType.TinkeringMaterial | ItemType.Misc;

            AssertRefuses(combined, VaultCollapse.ClassRefusal.NotTinkeringMaterial,
                "ItemType is a [Flags] enum and the test is EXACT equality, so a partial match must decline");
        }

        /// <summary>
        /// A PropertiesAttribute subclass that points at itself, so the signature walker meets a real
        /// reference cycle rather than a simulated one. Public because the walker reads it by
        /// reflection from ACE.Server, and reflection is access-checked against the declaring type.
        /// </summary>
        public class CyclicAttribute : PropertiesAttribute
        {
            public CyclicAttribute Self { get; set; }
        }

        [TestMethod]
        public void Refuses_AnObjectGraphItCouldNotWalkToTheBottom()
        {
            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);

            var cyclic = new CyclicAttribute();
            cyclic.Self = cyclic;

            bag.Biota.PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
            {
                { PropertyAttribute.Strength, cyclic },
            };

            // The truncation report carries no key at all, and it takes PRECEDENCE over the ordinary
            // prefix the same diff also produced: a graph that could not be walked was not proven to
            // differ only on the whitelist, whatever else the list says.
            AssertRefuses(bag, VaultCollapse.ClassRefusal.GraphTruncated, "a graph this code cannot walk has not been proven interchangeable");
        }

        // NOTE: the NoReference refusal is deliberately NOT tested here. Reaching it needs a wcid that
        // is not in the weenie cache, and GetCachedWeenie's miss path falls through to a real
        // WorldDbContext - which in this test host is either a null-config throw or, on a machine whose
        // ACE config does point at a live ace_world, an actual query. Either way the test would be
        // measuring the environment rather than the predicate. The path itself is a plain null check.

        #endregion

        #region Classes and keys

        [TestMethod]
        public void Predicate_RenamedBag_FormsItsOwnClass_OfCountOne()
        {
            var first = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);
            var second = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 9999);
            var renamed = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213, name: "Bob's Lucky Silver");

            // A rename is a CLASS, not a refusal: Name is in the whitelist, so the bag is still
            // reproducible from its template - it simply reproduces under a different name.
            Assert.IsTrue(VaultCollapse.TryDescribeClass(renamed, out var renamedOverrides, out var reason), $"a renamed bag must still be classifiable: {reason}");

            var keys = new List<string>();

            foreach (var bag in new[] { first, second })
            {
                Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out var overrides, out _));
                keys.Add(VaultItemClass.ClassKey(bag.WeenieClassId, overrides, VaultItemClass.ValueExcluded));
            }

            Assert.AreEqual(keys[0], keys[1], "two bags differing only in Value share a class at band 0");

            var renamedKey = VaultItemClass.ClassKey(renamed.WeenieClassId, renamedOverrides, VaultItemClass.ValueExcluded);

            Assert.AreNotEqual(keys[0], renamedKey, "a hand-renamed bag forms its own class of one");
        }

        [TestMethod]
        public void ClassKey_ExcludesValue_AtBandZero()
        {
            var cheap = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 3);
            var rich = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 250000);

            Assert.IsTrue(VaultCollapse.TryDescribeClass(cheap, out var cheapOverrides, out _));
            Assert.IsTrue(VaultCollapse.TryDescribeClass(rich, out var richOverrides, out _));

            Assert.AreEqual(
                VaultItemClass.ClassKey(PlainBagWcid, cheapOverrides, VaultItemClass.ValueExcluded),
                VaultItemClass.ClassKey(PlainBagWcid, richOverrides, VaultItemClass.ValueExcluded),
                "at band 0 Value is pooled, not keyed - the owner's ruling, and the whole 9.9x collapse depends on it");

            // The control: the payload really does carry two different Values, so the equality above is
            // the band doing its job rather than the payloads being identical.
            Assert.AreEqual(3, cheapOverrides.GetInt(PropertyInt.Value));
            Assert.AreEqual(250000, richOverrides.GetInt(PropertyInt.Value));
        }

        [TestMethod]
        public void ClassKey_IncludesValueBucket_AtBand25()
        {
            var near = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 100);
            var alsoNear = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 101);
            var far = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 250000);

            Assert.IsTrue(VaultCollapse.TryDescribeClass(near, out var nearOverrides, out _));
            Assert.IsTrue(VaultCollapse.TryDescribeClass(alsoNear, out var alsoNearOverrides, out _));
            Assert.IsTrue(VaultCollapse.TryDescribeClass(far, out var farOverrides, out _));

            Assert.AreEqual(
                VaultItemClass.ClassKey(PlainBagWcid, nearOverrides, 25),
                VaultItemClass.ClassKey(PlainBagWcid, alsoNearOverrides, 25),
                "100 and 101 sit in the same 25 percent band");

            Assert.AreNotEqual(
                VaultItemClass.ClassKey(PlainBagWcid, nearOverrides, 25),
                VaultItemClass.ClassKey(PlainBagWcid, farOverrides, 25),
                "at a positive band the Value bucket is part of identity");

            // And a key from one band can never be mistaken for a key from another.
            Assert.AreNotEqual(
                VaultItemClass.ClassKey(PlainBagWcid, nearOverrides, VaultItemClass.ValueExcluded),
                VaultItemClass.ClassKey(PlainBagWcid, nearOverrides, 25));
        }

        [TestMethod]
        public void ValueBand_IsMonotone_AndExactAtItsBoundaries()
        {
            // Bounds for a 25 percent band, by the definition in VaultItemClass.ValueBand: 1, 2, 3, 4,
            // 5, 6, 7, 8, 10, ... Each is the FIRST value of its own band.
            Assert.AreEqual(0, VaultItemClass.ValueBand(1, 25));
            Assert.AreEqual(1, VaultItemClass.ValueBand(2, 25));
            Assert.AreEqual(2, VaultItemClass.ValueBand(3, 25));
            Assert.AreEqual(3, VaultItemClass.ValueBand(4, 25));

            // Zero and negatives share the bottom band rather than producing a separate identity.
            Assert.AreEqual(0, VaultItemClass.ValueBand(0, 25));
            Assert.AreEqual(0, VaultItemClass.ValueBand(-5, 25));

            var previous = 0;

            for (var value = 1; value <= 5000; value++)
            {
                var band = VaultItemClass.ValueBand(value, 10);

                Assert.IsTrue(band >= previous, $"banding must never go backwards: {value} landed in {band} after {previous}");
                previous = band;
            }

            // int.MaxValue must terminate and stay in range rather than overflowing the bound.
            Assert.IsTrue(VaultItemClass.ValueBand(int.MaxValue, 10) > VaultItemClass.ValueBand(1000000, 10));
        }

        /// <summary>
        /// PIN, not a discriminator. The canonical text and the key derived from it are what a stored
        /// class row would be keyed by, so changing either invalidates every stored key. These literals
        /// exist to make that change impossible to land by accident.
        ///
        /// The two hostile inputs are asserted alongside: a locale whose number formatting differs, and
        /// a payload built in the opposite key order.
        /// </summary>
        [TestMethod]
        public void ClassKey_IsStable_AcrossProcessRuns()
        {
            var forward = new VaultItemClassOverrides(
                new[]
                {
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, -12345),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, 87),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, null),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, 100),
                },
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, "Salvage (87)") });

            var reversed = new VaultItemClassOverrides(
                new[]
                {
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, 100),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, null),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, 87),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, -12345),
                },
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, "Salvage (87)") });

            const string expectedCanonical = "v1|b0|w990201|I92=87|I105=~|I170=100|S1=12:Salvage (87)";
            // The first 16 bytes of SHA-256 over the canonical text above, recorded from this
            // implementation. It is a REGRESSION PIN, not an independent derivation: its job is to fail
            // if the encoding or the hash ever changes, because either would orphan every stored key.
            const string expectedKey = "192d27fc217e3a0c90273bcdd242562c";

            var original = CultureInfo.CurrentCulture;

            try
            {
                // A locale that renders a negative number and a group separator differently. If any
                // number below reached ToString() without InvariantCulture, this is where it shows.
                CultureInfo.CurrentCulture = new CultureInfo("de-DE");

                Assert.AreEqual(expectedCanonical, VaultItemClass.CanonicalForm(PlainBagWcid, forward, VaultItemClass.ValueExcluded));
                Assert.AreEqual(expectedCanonical, VaultItemClass.CanonicalForm(PlainBagWcid, reversed, VaultItemClass.ValueExcluded));

                Assert.AreEqual(expectedKey, VaultItemClass.ClassKey(PlainBagWcid, forward, VaultItemClass.ValueExcluded));
                Assert.AreEqual(expectedKey, VaultItemClass.ClassKey(PlainBagWcid, reversed, VaultItemClass.ValueExcluded));
            }
            finally
            {
                CultureInfo.CurrentCulture = original;
            }

            Assert.AreEqual(VaultItemClass.ClassKeyLength, expectedKey.Length);
            Assert.IsTrue(expectedKey.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "a class key is lowercase hex");
        }

        /// <summary>
        /// The length prefix on a string field, asserted on the encoding rather than through an
        /// outcome. Without it a name containing the field separators could canonicalize to the same
        /// text as a different payload - the false-POSITIVE shape, which here would merge two items
        /// that are not interchangeable.
        /// </summary>
        [TestMethod]
        public void CanonicalForm_CannotMergeTwoDifferentNames()
        {
            // The full whitelist, because the key-set contract refuses a partial payload outright. Only
            // the Name varies between the two payloads below, which is what this test is about.
            VaultItemClassOverrides Named(string name) => new VaultItemClassOverrides(
                WhitelistedInts(),
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, name) });

            var first = VaultItemClass.CanonicalForm(PlainBagWcid, Named("a|I92=2"), VaultItemClass.ValueExcluded);
            var second = VaultItemClass.CanonicalForm(PlainBagWcid, Named("a"), VaultItemClass.ValueExcluded);

            Assert.AreNotEqual(first, second);

            // An absent name and an empty one are different payloads and must stay different.
            var absent = VaultItemClass.CanonicalForm(PlainBagWcid, Named(null), VaultItemClass.ValueExcluded);
            var empty = VaultItemClass.CanonicalForm(PlainBagWcid, Named(""), VaultItemClass.ValueExcluded);

            Assert.AreNotEqual(absent, empty);
        }

        /// <summary>The exact whitelisted int half of a payload, for the key-set contract tests below.</summary>
        private static KeyValuePair<PropertyInt, int?>[] WhitelistedInts() => new[]
        {
            new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, 4213),
            new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, 87),
            new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, 640),
            new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, 100),
        };

        /// <summary>The exact whitelisted string half.</summary>
        private static KeyValuePair<PropertyString, string>[] WhitelistedStrings() => new[]
        {
            new KeyValuePair<PropertyString, string>(PropertyString.Name, "Salvage (87)"),
        };

        /// <summary>
        /// The payload's key set is part of the identity contract, so a payload that is not EXACTLY the
        /// whitelist must be refused at construction rather than canonicalized.
        ///
        /// WHY THIS IS NOT COSMETIC. CanonicalForm walks whatever the payload happens to hold, so a
        /// SHORT payload canonicalizes to a different text than a full one carrying the same values
        /// plus a null - two payloads that mean the same thing would key differently, and two that mean
        /// different things could key the same. Today the only producer is
        /// VaultCollapse.CaptureOverridesLocked, which always emits the full set; the guard is what
        /// stops that from being a coincidence the moment the storage tier adds its second caller, or
        /// the moment CollapsibleIntKeys gains a fifth key and a stale caller keeps sending four.
        ///
        /// DISCRIMINATING, with a real before: the constructor accepted both of these silently until
        /// the guard was added.
        /// </summary>
        [TestMethod]
        public void Overrides_RefuseAPayloadMissingAWhitelistedKey()
        {
            var missingInt = WhitelistedInts().Where(kvp => kvp.Key != PropertyInt.NumItemsInMaterial).ToArray();

            var thrown = Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(missingInt, WhitelistedStrings()),
                "a payload missing a whitelisted key must be refused, not canonicalized");

            StringAssert.Contains(thrown.Message, nameof(PropertyInt.NumItemsInMaterial),
                "the refusal must name the key nobody supplied, the way AssertBiotaCoverage names the property nobody decided about");

            Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(WhitelistedInts(), Array.Empty<KeyValuePair<PropertyString, string>>()),
                "the string half is part of the same contract");

            Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(null, null),
                "an empty payload is a missing key set, not an empty one");
        }

        /// <summary>
        /// The other direction, and a duplicate key with it. A key outside the whitelist is one the
        /// materializer would replay onto a fresh template without the predicate ever having proved the
        /// item differs from its template only on the whitelist - which is the exact hole this whole
        /// design exists to close.
        ///
        /// DISCRIMINATING, same real before.
        /// </summary>
        [TestMethod]
        public void Overrides_RefuseAPayloadCarryingAKeyOutsideTheWhitelist()
        {
            var extraInt = WhitelistedInts().Concat(new[] { new KeyValuePair<PropertyInt, int?>(PropertyInt.MaxStructure, 100) }).ToArray();

            var thrown = Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(extraInt, WhitelistedStrings()),
                "a key outside the whitelist must be refused");

            StringAssert.Contains(thrown.Message, nameof(PropertyInt.MaxStructure), "the refusal must name the offending key");

            var extraString = WhitelistedStrings().Concat(new[] { new KeyValuePair<PropertyString, string>(PropertyString.LongDesc, "x") }).ToArray();

            Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(WhitelistedInts(), extraString),
                "the string half is part of the same contract");

            // A DUPLICATE key is refused too. Set membership alone would pass it, and the canonical form
            // would then carry one key twice - so the check is on the count as well as the set.
            var duplicated = WhitelistedInts().Concat(new[] { new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, 999) }).ToArray();

            Assert.ThrowsExactly<ArgumentException>(
                () => new VaultItemClassOverrides(duplicated, WhitelistedStrings()),
                "a duplicated key must be refused - the canonical form would otherwise emit it twice");

            // The control, and it is the half that stops this test from passing for the wrong reason:
            // the EXACT whitelist must still be accepted.
            var ok = new VaultItemClassOverrides(WhitelistedInts(), WhitelistedStrings());

            Assert.AreEqual(4, ok.Ints.Count);
            Assert.AreEqual(1, ok.Strings.Count);
        }

        #endregion

        #region The coverage guard still protects the new predicate

        /// <summary>
        /// Adding a property to ACE.Entity.Models.Biota must still stop everything, INCLUDING this
        /// predicate, rather than silently opening a hole through which a modified item is judged
        /// interchangeable.
        ///
        /// DISCRIMINATING, and it is the only test in the tree that exercises the guard in its THROWING
        /// state: VaultCollapseTests.CoverageGuard_HoldsForTheCurrentBiotaShape only ever calls it while
        /// it is quiet. The synthetic property is pushed into the same private list the real inversion
        /// fills and removed again in a finally, so the guard is driven rather than simulated.
        /// </summary>
        [TestMethod]
        public void CoverageGuard_Throwing_StopsTryDescribeClassToo()
        {
            var field = typeof(VaultCollapse).GetField("UnhandledBiotaProperties", BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, "VaultCollapse.UnhandledBiotaProperties was not found by reflection - has it been renamed? This test must drive the guard, not scan nothing.");

            var unhandled = (List<string>)field.GetValue(null);
            Assert.AreEqual(0, unhandled.Count, "precondition: the guard is quiet before this test tampers with it");

            const string synthetic = "ZzSyntheticBiotaProperty";

            var bag = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 4213);

            Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out _, out _), "control: this bag classifies while the guard is quiet");

            try
            {
                unhandled.Add(synthetic);

                var thrown = Assert.ThrowsExactly<InvalidOperationException>(
                    () => VaultCollapse.AssertBiotaCoverage(),
                    "an unhandled Biota property must stop the server rather than be skipped");

                StringAssert.Contains(thrown.Message, synthetic, "the guard must name the property nobody decided about");

                AssertRefuses(bag, VaultCollapse.ClassRefusal.Threw,
                    "with the guard firing, the predicate must refuse rather than classify - a hole here destroys biotas");
            }
            finally
            {
                unhandled.Remove(synthetic);
            }

            VaultCollapse.AssertBiotaCoverage();
            Assert.IsTrue(VaultCollapse.TryDescribeClass(bag, out _, out _), "the guard must be quiet again once the synthetic property is removed");
        }

        #endregion

        #region The reference cache

        /// <summary>
        /// The cache's two load-bearing properties, and the answer to "does CalculateObjDesc behave on
        /// an ObjectGuid.Invalid object": one shared instance per wcid, and that instance really does
        /// carry ObjectGuid.Invalid, so nothing has to be destroyed and no dynamic guid is burned.
        ///
        /// DISCRIMINATING: stop caching and the reference-equality assertion fails; build the reference
        /// with CreateNewWorldObject instead and the guid assertion fails.
        /// </summary>
        [TestMethod]
        public void ReferenceCache_HandsOutOneInvalidGuidInstancePerWcid_AndRendersWithoutThrowing()
        {
            var first = VaultReferenceCache.Get(PlainBagWcid);
            var second = VaultReferenceCache.Get(PlainBagWcid);

            Assert.IsNotNull(first, "a seeded wcid must produce a reference");
            Assert.AreSame(first, second, "the reference must be cached, not rebuilt per call");

            Assert.AreEqual(ObjectGuid.Invalid.Full, first.Guid.Full,
                "the reference must hold ObjectGuid.Invalid, which is what makes it safe to cache without destroying it");

            // The question the storage tier's design turns on: rendering a reference built this way
            // must not throw. It is what NormalizeDatDerivedState does to every reference.
            first.CalculateObjDesc();

            Assert.AreEqual(ObjectGuid.Invalid.Full, first.Guid.Full, "rendering must not have moved the reference's guid");

            var other = VaultReferenceCache.Get(OtherBagWcid);

            Assert.IsNotNull(other);
            Assert.AreNotSame(first, other, "the cache is keyed by wcid");

            VaultReferenceCache.Invalidate(PlainBagWcid);

            Assert.AreNotSame(first, VaultReferenceCache.Get(PlainBagWcid), "invalidating one wcid must rebuild that wcid's reference");
            Assert.AreSame(other, VaultReferenceCache.Get(OtherBagWcid), "and must leave the others alone");
        }

        /// <summary>
        /// A reference whose render THREW must be handed back but not cached, so a transient dat
        /// failure costs one refusal rather than pinning that wcid as unclassifiable for the whole
        /// process lifetime.
        ///
        /// DISCRIMINATING, with a real before: the cache stored the reference unconditionally until this
        /// was changed. Its LIMIT is declared rather than hidden - the only way to make CalculateObjDesc
        /// throw without a client installation is to send it down the ClothingBase branch with
        /// DatManager.PortalDat null, so on a host where some earlier test class loaded the dats there
        /// is no failure to observe and this reports Inconclusive instead of passing vacuously.
        /// </summary>
        [TestMethod]
        public void ReferenceCache_DoesNotCacheAReferenceItCouldNotRender()
        {
            if (DatManager.PortalDat != null)
                Assert.Inconclusive("Skipped: client_portal.dat is loaded in this test host, so CalculateObjDesc succeeds on the probe and there is no render failure to observe.");

            var first = VaultReferenceCache.Get(ClothingBaseProbeWcid);
            var second = VaultReferenceCache.Get(ClothingBaseProbeWcid);

            Assert.IsNotNull(first, "a render failure is not a build failure - the reference is still handed back");
            Assert.AreNotSame(first, second,
                "a reference whose render threw must NOT be cached, or one transient dat failure makes that wcid permanently unclassifiable");

            // The control, and it is what stops the assertion above from passing merely because caching
            // is broken outright: a wcid that renders cleanly IS still cached.
            Assert.AreSame(VaultReferenceCache.Get(PlainBagWcid), VaultReferenceCache.Get(PlainBagWcid),
                "a reference that rendered cleanly must still be cached");
        }

        /// <summary>Same walk-up idiom as VaultCollapseTests.FindInSourceTree, kept local rather than shared.</summary>
        private static string FindInSourceTree(string relativePath)
        {
            var native = relativePath.Replace('/', Path.DirectorySeparatorChar);

            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, native);

                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        /// <summary>
        /// The ORDER-INDEPENDENT half of the non-caching guarantee, and it exists because the
        /// behavioural test above cannot cover a full-suite run.
        ///
        /// That test needs DatManager.PortalDat to be null to make CalculateObjDesc throw, so on any
        /// host where an earlier test class has loaded the dats it reports Inconclusive and covers
        /// nothing. Deleting the guard outright would then leave the whole suite green - exactly the
        /// silent regression shape VaultCollapseTests.IsPristineAndDiff_NormalizeTheirReferenceBeforeDiffingIt
        /// was written for, and this borrows that test's instrument.
        ///
        /// BE CLEAR ABOUT ITS LIMIT, the same one that test states: it proves the guard is WRITTEN
        /// ahead of the publish, not that it does anything. It catches deletion and reordering, which
        /// is precisely the gap; it cannot catch Normalize being changed to always return true.
        ///
        /// DISCRIMINATING and dat-independent: removing either line, or moving the publish above the
        /// guard, fails this.
        /// </summary>
        [TestMethod]
        public void ReferenceCache_SkipsThePublishWhenItCouldNotRender_SourceOrder()
        {
            const string relativePath = "Source/ACE.Server/Entity/AccountVault/VaultReferenceCache.cs";

            var path = FindInSourceTree(relativePath);
            Assert.IsNotNull(path, $"Could not find {relativePath} by walking up from {AppContext.BaseDirectory}.");

            var code = AccountVaultPurgeTests.StripComments(File.ReadAllText(path));

            var guard = code.IndexOf("if (!Normalize(reference))", StringComparison.Ordinal);
            var publish = code.IndexOf("references[wcid] = reference;", StringComparison.Ordinal);

            Assert.IsTrue(publish >= 0,
                "VaultReferenceCache no longer publishes a reference with 'references[wcid] = reference;' - this guard must be re-aimed rather than left scanning nothing.");

            Assert.IsTrue(guard >= 0,
                "VaultReferenceCache.Get must refuse to cache a reference whose render threw. Without that, one transient dat failure pins the wcid as unclassifiable for the whole process lifetime and only a manual cache clear releases it.");

            Assert.IsTrue(guard < publish,
                "the normalization guard must come BEFORE the publish, or the un-renderable reference is cached anyway and the guard is decoration.");
        }

        #endregion

        #region The dry run's accumulator

        [TestMethod]
        public void DryRunReport_CountsClassifiableItemsAndHistogramsTheRefusals()
        {
            var report = new VaultClassDryRunReport { Scope = "unit test" };

            // Three bags that share a class at band 0 (they differ only in Value), one renamed bag that
            // forms its own, and two refusals of different kinds.
            report.Classify(MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 1));
            report.Classify(MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 5000));
            report.Classify(MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 90000));
            report.Classify(MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 1, name: "Bob's Lucky Silver"));

            var tinkered = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 1);
            tinkered.NumTimesTinkered = 1;
            report.Classify(tinkered);

            var timed = MakeBag(PlainBagWcid, structure: 87, workmanship: 640, numItems: 100, value: 1);
            timed.Lifespan = 60;
            report.Classify(timed);

            Assert.AreEqual(6L, report.ItemsSeen);
            Assert.AreEqual(4L, report.Classifiable);

            Assert.AreEqual(2, report.DistinctClasses(0), "band 0 pools Value, so the three priced bags are one class and the renamed one is another");
            Assert.AreEqual(4, report.DistinctClasses(2), "band 25 keys the Value bucket, so the three priced bags separate");

            Assert.AreEqual(1L, report.Refusals["PropertiesInt.NumTimesTinkered"]);
            Assert.AreEqual(1L, report.Refusals[VaultCollapse.ClassRefusal.Timed]);

            var rendered = report.Render();

            StringAssert.Contains(rendered, "unit test");
            StringAssert.Contains(rendered, "SHIPPING");
            StringAssert.Contains(rendered, "NumTimesTinkered");
        }

        #endregion

        #region TryParseCanonicalForm

        /// <summary>
        /// A payload of arbitrary values, for the round-trip tests. Every whitelisted key, each once,
        /// with null meaning ABSENT - the same shape the predicate emits.
        /// </summary>
        private static VaultItemClassOverrides Payload(int? value, int? structure, int? workmanship, int? numItems, string name)
        {
            return new VaultItemClassOverrides(
                new[]
                {
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Value, value),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.Structure, structure),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.ItemWorkmanship, workmanship),
                    new KeyValuePair<PropertyInt, int?>(PropertyInt.NumItemsInMaterial, numItems),
                },
                new[] { new KeyValuePair<PropertyString, string>(PropertyString.Name, name) });
        }

        /// <summary>
        /// Every shape CanonicalForm can emit at band 0 parses back to a payload that re-emits the
        /// IDENTICAL text.
        ///
        /// That is the property under test, rather than field equality, because the text is what the
        /// class key hashes: a parse that recovered the right values but would re-canonicalize
        /// differently is exactly a stored row whose items come back under a different key than the one
        /// they are filed under.
        ///
        /// PropertyInt.Value is asserted ABSENT on the way back, in every case, including the ones
        /// where a value went in. At band 0 the text genuinely does not carry it - it is pooled in the
        /// row's total instead - and a parser that invented one would be inventing the number the
        /// withdraw path is supposed to compute.
        ///
        /// The separator-laden names are the ones that matter: the canonical form is LENGTH-PREFIXED
        /// rather than quoted, so a name containing the field separator, the key/value separator or the
        /// length terminator must survive untouched. A parser that split on those characters would pass
        /// every other case in this list.
        /// </summary>
        [DataTestMethod]
        [DataRow(4213, 87, 640, 100, "Salvage (87)")]
        [DataRow(0, 0, 0, 0, "")]
        [DataRow(int.MaxValue, 65535, 1000, 100, "Pyreal Salvage (100)")]
        [DataRow(1, 1, 1, 1, "a|b")]
        [DataRow(1, 1, 1, 1, "Name=Value")]
        [DataRow(1, 1, 1, 1, "12:34")]
        [DataRow(1, 1, 1, 1, "|I19=7|S1=4:oops")]
        [DataRow(1, 1, 1, 1, "v1|b0|w21013")]
        [DataRow(1, 1, 1, 1, "a b\tc\nd")]
        public void TryParseCanonicalForm_RoundTripsEveryShapeTheFormatterEmits(int value, int structure, int workmanship, int numItems, string name)
        {
            const uint wcid = 21013;

            var payload = Payload(value, structure, workmanship, numItems, name);
            var canonical = VaultItemClass.CanonicalForm(wcid, payload, VaultItemClass.ValueExcluded);

            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(canonical, out var parsedWcid, out var parsed, out var band),
                $"could not parse a canonical form this build wrote: {canonical}");

            Assert.AreEqual(wcid, parsedWcid);
            Assert.AreEqual(VaultItemClass.ValueExcluded, band);

            Assert.IsNull(parsed.GetInt(PropertyInt.Value),
                "at band 0 the text does not carry Value, so the parse must report it ABSENT rather than invent one");

            Assert.AreEqual(structure, parsed.GetInt(PropertyInt.Structure));
            Assert.AreEqual(workmanship, parsed.GetInt(PropertyInt.ItemWorkmanship));
            Assert.AreEqual(numItems, parsed.GetInt(PropertyInt.NumItemsInMaterial));
            Assert.AreEqual(name, parsed.GetString(PropertyString.Name));

            Assert.AreEqual(canonical, VaultItemClass.CanonicalForm(wcid, parsed, VaultItemClass.ValueExcluded),
                "a parsed payload must re-emit the identical text, or the row's class key would change under it");

            Assert.AreEqual(VaultItemClass.ClassKey(wcid, payload, VaultItemClass.ValueExcluded),
                            VaultItemClass.ClassKey(wcid, parsed, VaultItemClass.ValueExcluded),
                            "the round trip must preserve the class key exactly");
        }

        /// <summary>
        /// The absent shapes: a null for any whitelisted key, and a null Name, which is a DIFFERENT
        /// payload from an empty one and must stay different through the round trip.
        /// </summary>
        [TestMethod]
        public void TryParseCanonicalForm_RoundTripsAbsentValues()
        {
            const uint wcid = 21013;

            var payloads = new[]
            {
                Payload(null, null, null, null, null),
                Payload(500, null, 640, null, "Salvage"),
                Payload(null, 87, null, 100, null),
                Payload(null, 87, null, 100, ""),
            };

            var keys = new List<string>();

            foreach (var payload in payloads)
            {
                var canonical = VaultItemClass.CanonicalForm(wcid, payload, VaultItemClass.ValueExcluded);

                Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(canonical, out var parsedWcid, out var parsed, out _), canonical);

                Assert.AreEqual(wcid, parsedWcid);
                Assert.AreEqual(canonical, VaultItemClass.CanonicalForm(wcid, parsed, VaultItemClass.ValueExcluded));

                Assert.AreEqual(payload.GetInt(PropertyInt.Structure), parsed.GetInt(PropertyInt.Structure));
                Assert.AreEqual(payload.GetInt(PropertyInt.ItemWorkmanship), parsed.GetInt(PropertyInt.ItemWorkmanship));
                Assert.AreEqual(payload.GetInt(PropertyInt.NumItemsInMaterial), parsed.GetInt(PropertyInt.NumItemsInMaterial));
                Assert.AreEqual(payload.GetString(PropertyString.Name), parsed.GetString(PropertyString.Name));

                keys.Add(VaultItemClass.ClassKey(wcid, parsed, VaultItemClass.ValueExcluded));
            }

            // An absent Name and an empty one are different payloads, and the last two entries differ
            // only in that. If the parse collapsed them the two keys would match.
            Assert.AreNotEqual(keys[2], keys[3], "an absent Name and an empty Name must survive as different classes");
        }

        /// <summary>
        /// The refusals. Every one of these returns false rather than a best guess, because the caller
        /// materializes a real item from the result and hands it to a player.
        ///
        /// The band case is the interesting one and it is a DELIBERATE limitation rather than an
        /// oversight: above band 0 the text carries a band INDEX where the value would be, and turning
        /// an index back into a value is a guess. Nothing writes a band above 0 today (the storage tier
        /// pins ValueExcluded), so this refusal is unreachable in production and is here to stay that
        /// way loudly rather than silently.
        /// </summary>
        [TestMethod]
        public void TryParseCanonicalForm_RefusesAnythingItCannotRebuildExactly()
        {
            const uint wcid = 21013;

            var payload = Payload(4213, 87, 640, 100, "Salvage (87)");
            var canonical = VaultItemClass.CanonicalForm(wcid, payload, VaultItemClass.ValueExcluded);

            // Control first: the text this build writes DOES parse. Without it every assertion below
            // would pass against a parser that refused everything.
            Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(canonical, out _, out _, out _),
                "the control must parse, or the refusals below prove nothing");

            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm(null, out _, out _, out _), "null");
            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm("", out _, out _, out _), "empty");
            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm("not a canonical form", out _, out _, out _), "garbage");

            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm(canonical.Substring(0, canonical.Length - 3), out _, out _, out _),
                "a truncated row must be refused, never parsed to whatever survived the truncation");

            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm(canonical + "|I9999=1", out _, out _, out _),
                "a key outside the whitelist must be refused");

            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm("v99" + canonical.Substring(2), out _, out _, out _),
                "a text written by another format version must be refused");

            // A band above 0 carries a band index where the value would be.
            var banded = VaultItemClass.CanonicalForm(wcid, payload, 25);

            Assert.IsFalse(VaultItemClass.TryParseCanonicalForm(banded, out _, out _, out _),
                "a band index cannot be turned back into a Value, so a banded row must be refused rather than guessed at");
        }

        #endregion
    }
}
