using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Legendary-key banking: the withdraw split (uses -> Durable 10-use keys + Aged 1-use keys), the
    /// item-count cap, and the set of key weenies /bank deposit accepts. The Player-level inventory flows
    /// themselves are exercised in-game; the split and the cap are pure and pinned here.
    /// </summary>
    [TestClass]
    public class BankLegendaryKeyTests
    {
        [DataTestMethod]
        [DataRow(0L, 0L, 0L)]
        [DataRow(1L, 0L, 1L)]
        [DataRow(9L, 0L, 9L)]
        [DataRow(10L, 1L, 0L)]
        [DataRow(15L, 1L, 5L)]
        [DataRow(30L, 3L, 0L)]
        [DataRow(-5L, 0L, 0L)]
        public void PlanLegendaryKeyWithdrawal_SplitsUsesIntoDurableAndAged(long uses, long durable, long aged)
        {
            var plan = Player.PlanLegendaryKeyWithdrawal(uses);
            Assert.AreEqual(durable, plan.Durable, $"durable keys for {uses} uses");
            Assert.AreEqual(aged, plan.Aged, $"aged keys for {uses} uses");
            if (uses > 0)
                Assert.AreEqual(uses, plan.Durable * 10 + plan.Aged, "uses must be conserved");
        }

        [TestMethod]
        public void LegendaryKeyWithdrawalWithinItemCap_CountsKeysNotUses()
        {
            // 100,000 uses is far past the 10,000 cap if counted per use, but is only 10,000 durable keys.
            Assert.IsTrue(Player.LegendaryKeyWithdrawalWithinItemCap(100_000), "10,000 durables is exactly at the cap");
            // 1 more use adds an aged key: 10,001 items.
            Assert.IsFalse(Player.LegendaryKeyWithdrawalWithinItemCap(100_001), "10,001 keys is over the cap");
            // Remainder keys count too: 99,999 uses = 9,999 durable + 9 aged = 10,008 items.
            Assert.IsFalse(Player.LegendaryKeyWithdrawalWithinItemCap(99_999), "durable + aged both count toward the cap");
            Assert.IsTrue(Player.LegendaryKeyWithdrawalWithinItemCap(15), "small withdrawals are within the cap");
        }

        private static Dictionary<uint, int> KeyUses() => BankLegendaryKeyTable.Uses;

        [TestMethod]
        public void LegendaryKeyUses_AcceptsEveryNonBondedLegendaryKey()
        {
            var uses = KeyUses();
            var expected = new Dictionary<uint, int>
            {
                { 48746, 1 }, { 48747, 1 }, { 48748, 2 }, { 48749, 3 }, { 48750, 4 }, { 48914, 1 },
                { 51558, 1 }, { 51586, 3 }, { 51648, 3 }, { 51954, 10 }, { 51963, 25 }, { 52010, 5 },
                { 72048, 1 }, { 72338, 3 }, { 72600, 1 }, { 72807, 2 }, { 87168, 4 },
            };
            foreach (var kvp in expected)
            {
                Assert.IsTrue(uses.TryGetValue(kvp.Key, out var v), $"key wcid {kvp.Key} must be depositable");
                Assert.AreEqual(kvp.Value, v, $"uses for key wcid {kvp.Key}");
            }
            Assert.AreEqual(expected.Count, uses.Count, "no unexpected extra key wcids");
        }

        [TestMethod]
        public void LegendaryKeyUses_RefusesBondedKeys()
        {
            // Bonded keys must never be banked: withdrawal hands out tradeable keys, which would launder them.
            var uses = KeyUses();
            foreach (var bonded in new uint[] { 72474, 72628, 72635, 72669 })
                Assert.IsFalse(uses.ContainsKey(bonded), $"bonded key wcid {bonded} must not be depositable");
        }
        [TestMethod]
        public void WithdrawKeyWcids_AreBoundToTheTableAndUses()
        {
            Assert.AreEqual(48746u, BankLegendaryKeyTable.AgedWcid, "aged key wcid");
            Assert.AreEqual(51954u, BankLegendaryKeyTable.DurableWcid, "durable key wcid");
            Assert.AreEqual(1, BankLegendaryKeyTable.Uses[BankLegendaryKeyTable.AgedWcid], "aged key is single use");
            Assert.AreEqual(BankLegendaryKeyTable.DurableUses, BankLegendaryKeyTable.Uses[BankLegendaryKeyTable.DurableWcid], "durable uses come from the table");
            Assert.AreEqual(10, BankLegendaryKeyTable.DurableUses);
        }

        [TestMethod]
        public void PlanLegendaryKeyWithdrawal_ConservesUsesFor1To200()
        {
            for (long n = 1; n <= 200; n++)
            {
                var (d, a) = Player.PlanLegendaryKeyWithdrawal(n);
                Assert.AreEqual(n, 10 * d + a, $"uses conserved for {n}");
                Assert.IsTrue(a < 10 && a >= 0 && d >= 0, $"aged remainder below one durable for {n}");
            }
        }

        /// <summary>Creator that succeeds <paramref name="capacity"/> times then reports a full pack, recording every wcid asked for.</summary>
        private static Func<uint, bool> Pack(int capacity, List<uint> asked)
        {
            var made = 0;
            return wcid => { asked.Add(wcid); return made++ < capacity; };
        }

        [TestMethod]
        public void CreateLegendaryKeys_FullSuccess_AsksDurableThenAged()
        {
            var asked = new List<uint>();
            var r = Player.CreateLegendaryKeys(25, Pack(int.MaxValue, asked));
            Assert.AreEqual((25L, 2L, 5L), r);
            CollectionAssert.AreEqual(new uint[] { 51954, 51954, 48746, 48746, 48746, 48746, 48746 }, asked);
        }

        [TestMethod]
        public void CreateLegendaryKeys_PackFillsAfterTwoDurables_DebitsTwentyAndSkipsAged()
        {
            var asked = new List<uint>();
            var r = Player.CreateLegendaryKeys(35, Pack(2, asked)); // plan: 3 durable + 5 aged
            Assert.AreEqual((20L, 2L, 0L), r);
            CollectionAssert.AreEqual(new uint[] { 51954, 51954, 51954 }, asked, "third durable attempt fails, no aged key is attempted");
        }

        [TestMethod]
        public void CreateLegendaryKeys_PackFillsDuringAged_DebitsDurablesPlusAgedCreated()
        {
            var asked = new List<uint>();
            var r = Player.CreateLegendaryKeys(27, Pack(4, asked)); // plan: 2 durable + 7 aged; room for 2 durable + 2 aged
            Assert.AreEqual((22L, 2L, 2L), r);
            Assert.AreEqual(10 * r.Item2 + r.Item3, r.Item1, "debit is 10 per durable plus 1 per aged created");
            CollectionAssert.AreEqual(new uint[] { 51954, 51954, 48746, 48746, 48746 }, asked);
        }

        [TestMethod]
        public void CreateLegendaryKeys_NothingFits_DebitsNothing()
        {
            var r = Player.CreateLegendaryKeys(15, Pack(0, new List<uint>()));
            Assert.AreEqual((0L, 0L, 0L), r);
        }
    }
}
