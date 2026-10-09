using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database.Models.Shard;
using ACE.Entity.Enum;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// THE CORRECTNESS GATE on AccountVaultStore.AddStoredItemToGroupingLocked, the incremental grouping
    /// maintenance that replaced a full rebuild per deposit.
    ///
    /// The speedup is worthless if it changes a grouping decision, and a rebuild COUNTER cannot see that:
    /// a memo kept alive across deposits would read as fast and quietly fragment vaults, because item k
    /// of a sale must be able to join a group item k-1 just created. So this is a differential test.
    /// After EVERY deposit it compares the incrementally maintained grouping against a from-scratch
    /// rebuild of the same live state, and requires them to be identical in group count, group ordering,
    /// and the membership AND member ordering of every group - not just the counts, since equal counts
    /// with swapped members is exactly the failure a count check waves through.
    ///
    /// Both sides come from the production code: GroupingSnapshotForTest serves the memo,
    /// GroupingOracleForTest calls the same BuildGroupingLocked a rebuild calls and leaves the memo
    /// alone. There is no second copy of the grouping rule in this file to drift out of step.
    /// </summary>
    [TestClass]
    public class VaultDepositGroupingOracleTests
    {
        private const uint OwnerAccount = 6501;
        private const uint OwnerCharacter = 0x50000501;

        private const uint SalvageWcid = 21013;
        private const uint OtherSalvageWcid = 21020;
        private const uint MiscWcid = 8300;

        private const int VaultSlots = 255;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Oracleowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = false };

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            VaultClassTestConfig.Seed();
            VaultFoldProfile.ResetForTest();
        }

        [TestCleanup]
        public void Cleanup()
        {
            VaultFoldProfile.ResetForTest();

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        private Container AddVault(int order)
        {
            var container = FakeVaultWorld.MakeContainer(VaultSlots);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = (uint)(9800 + order),
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            return container;
        }

        private bool Deposit(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out reason));

            failReason = reason;
            return ok;
        }

        /// <summary>
        /// One randomized item. The shape mix is what makes the comparison meaningful:
        ///
        /// - a salvage bag on one of two wcids, one of three Structure values and one of three
        ///   workmanship pairs, so items land in several BUCKETS and some buckets hold more than one;
        /// - a Value that varies freely, which is a TOLERATED difference, so two bags in the same bucket
        ///   with different Values must MERGE - that is the merging-deposit case;
        /// - an occasional bag carrying an untolerated difference (a distinct Name), which must refuse to
        ///   merge and open a second group INSIDE an occupied bucket - the case that makes group count
        ///   per bucket greater than one and the case an order-insensitive implementation gets wrong;
        /// - an occasional ItemType.Misc item, which is no group candidate at all and is always its own
        ///   group - the entry-adding case.
        /// </summary>
        private WorldObject RandomItem(Random rng)
        {
            var roll = rng.Next(100);

            if (roll < 20)
            {
                var misc = FakeVaultWorld.MakeStack(MiscWcid, 1, 100);
                world.UnclassifiableGuids.Add(misc.Guid.Full);
                return misc;
            }

            var wcid = rng.Next(2) == 0 ? SalvageWcid : OtherSalvageWcid;
            var structure = new[] { 100, 75, 50 }[rng.Next(3)];
            var (workmanship, numItems) = new[] { (7, 10), (154, 24), (3, 5) }[rng.Next(3)];

            // A distinct name is NOT in GroupableIntKeys, so it forces a separate group within the bucket.
            var name = roll < 30 ? $"Salvage variant {rng.Next(3)}" : "Salvage (100)";

            var bag = FakeVaultWorld.MakeSalvageBag(wcid, structure, workmanship, numItems, rng.Next(1, 5000), name);
            world.UnclassifiableGuids.Add(bag.Guid.Full);

            return bag;
        }

        // ---------------------------------------------------------------- the oracle

        [TestMethod]
        public void IncrementalGrouping_MatchesAFullRebuild_AtEveryStep()
        {
            // Fixed seeds, so a failure is reproducible. Several of them, because one seed exercises one
            // interleaving and the bucket collisions that matter are the rare ones.
            foreach (var seed in new[] { 1, 7, 12345, 98765, 20260927 })
                RunOracle(seed, deposits: 120, vaults: 3, preHoles: 0);
        }

        /// <summary>
        /// The same differential check with HOLES punched in an earlier vault before the deposits start.
        /// FindVaultWithFreeSlotLocked returns the FIRST vault with a free slot, so with a hole in vault 0
        /// a deposit lands AHEAD of everything in vaults 1 and 2 in EnumerateStoredItemsLocked order. That
        /// is the case where a new arrival can become its group's representative and the group has to MOVE
        /// in the emitted list - the ordering case an append-only incremental update would get wrong while
        /// still reporting perfect membership.
        /// </summary>
        [TestMethod]
        public void IncrementalGrouping_MatchesAFullRebuild_WhenDepositsLandAheadOfExistingItems()
        {
            foreach (var seed in new[] { 3, 42, 555, 20260928 })
                RunOracle(seed, deposits: 80, vaults: 3, preHoles: 12);
        }

        private void RunOracle(int seed, int deposits, int vaults, int preHoles)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = false };

            var rng = new Random(seed);

            var containers = new List<Container>();

            for (var v = 0; v < vaults; v++)
                containers.Add(AddVault(v));

            // Fill vaults 0..n-2 completely so that, with no holes, a deposit appends to the last vault.
            // Seeded directly into the container rather than through the store, so this setup is not
            // itself under test.
            for (var v = 0; v < vaults - 1; v++)
            {
                for (var i = 0; i < VaultSlots; i++)
                {
                    var filler = RandomItem(rng);
                    Assert.IsTrue(containers[v].TryAddToInventory(filler, placementPosition: i), $"could not seed vault {v} slot {i}");
                }
            }

            // Holes in vault 0, which is what sends later deposits to a position ahead of vaults 1..n-1.
            for (var h = 0; h < preHoles; h++)
            {
                var victim = containers[0].Inventory.Values.Skip(rng.Next(containers[0].Inventory.Count)).First();
                Assert.IsTrue(containers[0].TryRemoveFromInventory(victim.Guid), "could not punch a hole");
            }

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the comparison");

            // Baseline: the two views must agree before a single deposit, or the seam itself is wrong.
            AssertGroupingsMatch(store, seed, step: -1, note: "before any deposit");

            for (var step = 0; step < deposits; step++)
            {
                var item = RandomItem(rng);

                Assert.IsTrue(Deposit(store, item, out var reason), $"seed {seed}: deposit {step} was refused: {reason}");

                AssertGroupingsMatch(store, seed, step, note: $"after depositing 0x{item.Guid.Full:X8}");
            }
        }

        private static void AssertGroupingsMatch(AccountVaultStore store, int seed, int step, string note)
        {
            var incremental = store.GroupingSnapshotForTest();
            var oracle = store.GroupingOracleForTest();

            if (Matches(incremental, oracle))
                return;

            var message = new StringBuilder();

            message.AppendLine($"GROUPING DIVERGED. seed={seed} step={step} ({note}).");
            message.AppendLine("Rerun with that seed to reproduce.");
            message.AppendLine($"incremental: {incremental.Count} group(s), oracle: {oracle.Count} group(s)");
            message.AppendLine();
            message.AppendLine("first difference:");

            for (var i = 0; i < Math.Max(incremental.Count, oracle.Count); i++)
            {
                var a = i < incremental.Count ? Render(incremental[i]) : "<missing>";
                var b = i < oracle.Count ? Render(oracle[i]) : "<missing>";

                if (a == b)
                    continue;

                message.AppendLine($"  group[{i}] incremental = {a}");
                message.AppendLine($"  group[{i}] oracle      = {b}");
                break;
            }

            Assert.Fail(message.ToString());
        }

        private static bool Matches(List<List<uint>> a, List<List<uint>> b)
        {
            if (a.Count != b.Count)
                return false;

            for (var i = 0; i < a.Count; i++)
            {
                // SequenceEqual, not a set comparison: member ORDER inside a group is observable - a
                // withdraw can take from the front of a group (GroupTakeOrder.Front) - so two groups with
                // the same members in a different order are not the same answer.
                if (!a[i].SequenceEqual(b[i]))
                    return false;
            }

            return true;
        }

        private static string Render(List<uint> group) => "[" + string.Join(", ", group.Select(g => $"0x{g:X8}")) + "]";

        // ---------------------------------------------------------------- the discrimination proof

        /// <summary>
        /// Proves the oracle can FAIL, by constructing the exact mistake an append-only incremental
        /// update would make and showing the comparison catches it.
        ///
        /// Two salvage bags that differ only in Value - a tolerated difference - so they belong in ONE
        /// group. The second is placed at a LOWER position in an EARLIER vault than the first, so a full
        /// rebuild visits it first and it is the group's representative, and the group is emitted at its
        /// position. An implementation that appended the arrival to the group's member list, or that left
        /// the group where it was, would produce a different member order or a different group position -
        /// and this asserts that such an answer does not compare equal.
        ///
        /// This is deliberately a test of the COMPARISON, not of the store: it builds the wrong answer by
        /// hand. Without it, a passing differential test is consistent with a comparison that accepts
        /// anything.
        /// </summary>
        [TestMethod]
        public void OracleComparison_RejectsAWrongMemberOrderAndAWrongGroupPosition()
        {
            var g1 = new List<uint> { 0x1000, 0x2000 };
            var g2 = new List<uint> { 0x3000 };

            var correct = new List<List<uint>> { g1, g2 };

            var swappedMembers = new List<List<uint>> { new List<uint> { 0x2000, 0x1000 }, new List<uint> { 0x3000 } };
            var swappedGroups = new List<List<uint>> { g2, g1 };
            var mergedWrongly = new List<List<uint>> { new List<uint> { 0x1000, 0x2000, 0x3000 } };
            var splitWrongly = new List<List<uint>> { new List<uint> { 0x1000 }, new List<uint> { 0x2000 }, g2 };

            Assert.IsTrue(Matches(correct, new List<List<uint>> { new List<uint> { 0x1000, 0x2000 }, new List<uint> { 0x3000 } }),
                "the comparison must accept an identical answer, or every other assertion here is vacuous");

            Assert.IsFalse(Matches(correct, swappedMembers), "member order inside a group must not compare equal when swapped");
            Assert.IsFalse(Matches(correct, swappedGroups), "group order must not compare equal when swapped");
            Assert.IsFalse(Matches(correct, mergedWrongly), "two groups wrongly merged into one must not compare equal");
            Assert.IsFalse(Matches(correct, splitWrongly), "one group wrongly split in two must not compare equal");
        }
    }
}
