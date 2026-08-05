using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Guid dedup in Player.GetAllPossessions.
    /// <para />
    /// Corrupt shard data can give one biota both a Container and a Wielder instance-id at once, which puts
    /// the same object into Inventory and EquippedObjects both. Player.AuditItemSpells then keys that list
    /// with ToDictionary(i => i.Guid, ...) during login, on the world-manager thread, with nothing catching
    /// the ArgumentException - so one corrupt row terminated the whole server process and dropped every
    /// other connected player.
    /// <para />
    /// The invariant enforced here is set-equality, not arithmetic: whatever the shape of the input, the
    /// result must contain each possessed guid exactly once and must not lose a possession. Losing one
    /// would silently dispel that item's enchantments during the audit; keeping a duplicate would crash.
    /// Objects are built from in-memory weenies with static-range guids (no database, no dat files).
    /// </summary>
    [TestClass]
    public class PossessionEnumerationTests
    {
        private static uint nextGuid = 0x7D000000;

        private static Stackable CreateItem(uint wcid = 9001)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, 1 },
                    { PropertyInt.MaxStackSize, 100 },
                },
            };

            return new Stackable(weenie, new ObjectGuid(nextGuid++));
        }

        private static Container CreateSidePack()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 136,
                WeenieType = WeenieType.Container,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemsCapacity, 10 },
                },
            };

            return new Container(weenie, new ObjectGuid(nextGuid++));
        }

        /// <summary>
        /// Stands in for Player.Inventory.Values / Player.EquippedObjects.Values: a guid-keyed dictionary's
        /// value collection, so a single source can never hold the same guid twice (only cross-source
        /// duplication is reachable in the live bug).
        /// </summary>
        private static ICollection<WorldObject> Source(params WorldObject[] items)
        {
            var keyed = new Dictionary<ObjectGuid, WorldObject>();

            foreach (var item in items)
                keyed[item.Guid] = item;

            return keyed.Values;
        }

        private static List<WorldObject> Enumerate(ICollection<WorldObject> inventory, ICollection<WorldObject> equipped, out List<WorldObject> duplicates)
        {
            var reported = new List<WorldObject>();
            var results = Player.GetAllPossessions(inventory, equipped, reported.Add);

            duplicates = reported;
            return results;
        }

        private static void AssertEachGuidOnce(List<WorldObject> results)
        {
            Assert.AreEqual(results.Count, results.Select(i => i.Guid).Distinct().Count(), "result contains a duplicate guid");
        }

        // ---- normal case: nothing deduped, nothing lost ----

        [TestMethod]
        public void Enumerate_ReturnsInventorySideContainerContentsAndEquipped()
        {
            var loose = CreateItem();
            var sidePack = CreateSidePack();
            var packed = CreateItem();
            var equipped = CreateItem();

            Assert.IsTrue(sidePack.TryAddToInventory(packed));

            var results = Enumerate(Source(loose, sidePack), Source(equipped), out var duplicates);

            CollectionAssert.AreEquivalent(new List<WorldObject> { loose, sidePack, packed, equipped }, results);
            AssertEachGuidOnce(results);
            Assert.AreEqual(0, duplicates.Count, "nothing should be reported as a duplicate");
        }

        [TestMethod]
        public void Enumerate_EmptySourcesReturnEmpty()
        {
            var results = Enumerate(Source(), Source(), out var duplicates);

            Assert.AreEqual(0, results.Count);
            Assert.AreEqual(0, duplicates.Count);
        }

        // ---- the login crash: the same object reachable from two sources ----

        [TestMethod]
        public void Enumerate_ObjectInBothInventoryAndEquipped_IsReturnedOnce()
        {
            // exactly the live corruption: one biota carrying Container AND Wielder pointing at the character
            var confused = CreateItem(622);
            var other = CreateItem();

            var results = Enumerate(Source(confused, other), Source(confused), out var duplicates);

            AssertEachGuidOnce(results);
            CollectionAssert.AreEquivalent(new List<WorldObject> { confused, other }, results);
            CollectionAssert.AreEqual(new List<WorldObject> { confused }, duplicates, "the duplicate occurrence should be reported once, naming the offending object");
        }

        [TestMethod]
        public void Enumerate_ObjectInBothSideContainerAndEquipped_IsReturnedOnce()
        {
            var sidePack = CreateSidePack();
            var confused = CreateItem();

            Assert.IsTrue(sidePack.TryAddToInventory(confused));

            var results = Enumerate(Source(sidePack), Source(confused), out var duplicates);

            AssertEachGuidOnce(results);
            CollectionAssert.AreEquivalent(new List<WorldObject> { sidePack, confused }, results);
            CollectionAssert.AreEqual(new List<WorldObject> { confused }, duplicates);
        }

        [TestMethod]
        public void Enumerate_ObjectInBothInventoryAndASideContainer_IsReturnedOnce()
        {
            var sidePack = CreateSidePack();
            var confused = CreateItem();

            Assert.IsTrue(sidePack.TryAddToInventory(confused));

            var results = Enumerate(Source(sidePack, confused), Source(), out var duplicates);

            AssertEachGuidOnce(results);
            CollectionAssert.AreEquivalent(new List<WorldObject> { sidePack, confused }, results);
            CollectionAssert.AreEqual(new List<WorldObject> { confused }, duplicates);
        }

        [TestMethod]
        public void Enumerate_ResultKeysByGuidWithoutThrowing()
        {
            // the exact expression that killed the server: Player_Spells.AuditItemSpells line 1 of the method
            var confused = CreateItem(622);
            var sidePack = CreateSidePack();
            var packed = CreateItem();

            Assert.IsTrue(sidePack.TryAddToInventory(packed));

            var results = Player.GetAllPossessions(Source(confused, sidePack, packed), Source(confused));

            var lookup = results.ToDictionary(i => i.Guid, i => i);

            AssertEachGuidOnce(results);
            Assert.AreEqual(results.Count, lookup.Count);
            CollectionAssert.AreEquivalent(new List<WorldObject> { confused, sidePack, packed }, results);
        }

        [TestMethod]
        public void Enumerate_ControlUndedupedConcatenationOfTheSameInputsStillThrows()
        {
            // control run: proves the crash the dedup prevents is genuinely reachable from these inputs,
            // so the test above is passing for the right reason rather than because nothing was wrong
            var confused = CreateItem(622);
            var inventory = Source(confused);
            var equipped = Source(confused);

            var undeduped = inventory.Concat(equipped).ToList();   // pre-fix shape of GetAllPossessions

            Assert.ThrowsExactly<System.ArgumentException>(() => undeduped.ToDictionary(i => i.Guid, i => i));
        }

        // ---- the audit's contract: every possession still resolves, nothing extra does ----

        [TestMethod]
        public void AuditLookup_ResolvesEveryPossessionAndNothingElse()
        {
            var loose = CreateItem();
            var sidePack = CreateSidePack();
            var packed = CreateItem();
            var equipped = CreateItem();
            var notPossessed = CreateItem();

            Assert.IsTrue(sidePack.TryAddToInventory(packed));

            var lookup = Player.GetAllPossessions(Source(loose, sidePack), Source(equipped))
                .ToDictionary(i => i.Guid, i => i);

            // an enchantment whose caster item is any of these must NOT be dispelled
            foreach (var possession in new[] { loose, (WorldObject)sidePack, packed, equipped })
            {
                Assert.IsTrue(lookup.TryGetValue(possession.Guid, out var found), $"lost possession 0x{possession.Guid.Full:X8}");
                Assert.AreSame(possession, found);
            }

            // an enchantment whose caster item is gone must still be dispelled
            Assert.IsFalse(lookup.ContainsKey(notPossessed.Guid));
        }

        [TestMethod]
        public void AuditLookup_StillResolvesADuplicatedPossession()
        {
            // dedup must not turn a corrupt-but-present item into a "non-possessed" item, which would
            // dispel its enchantments as a side effect of the crash fix
            var confused = CreateItem(622);

            var lookup = Player.GetAllPossessions(Source(confused), Source(confused))
                .ToDictionary(i => i.Guid, i => i);

            Assert.IsTrue(lookup.TryGetValue(confused.Guid, out var found));
            Assert.AreSame(confused, found);
        }
    }
}
