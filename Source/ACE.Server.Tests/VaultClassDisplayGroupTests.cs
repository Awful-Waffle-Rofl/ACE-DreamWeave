using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository.Hierarchy;

using ACE.Database;
using ACE.Database.Models.Shard;
using ACE.Entity.Enum.Properties;
using ACE.Server.Entity.AccountVault;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using ShardAccountVault = ACE.Database.Models.Shard.AccountVault;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The class tier's DISPLAY partition: how many lines a set of account_vault_class rows draws as,
    /// how many entries those lines cost, and what a withdraw against one of them takes.
    ///
    /// WHAT WENT WRONG, because every test here is shaped by it. The vault panel groups stored items on
    /// (wcid, Structure, workmanship QUOTIENT rounded to two decimals, Name). A class key keys on the
    /// RAW (ItemWorkmanship, NumItemsInMaterial) pair instead, because a withdraw has to hand back
    /// exactly what was deposited. The class partition is therefore strictly finer than the display
    /// partition, so a set of bags that drew as ONE panel row before folding became N class rows after -
    /// and the entry count added those rows ungrouped. The fold INFLATED the entry count of the vaults
    /// it migrated. On production 2026-09-25 that was 2,503 rows standing in for roughly 1,300 drawn
    /// lines, three accounts pushed past the 1,000 entry cap, and 82 salvage deposits refused with
    /// "Your vault is full".
    ///
    /// Two invariants were documented in comments and had no test at all. Both have one here:
    /// EntryCount equals the number of lines GetEntries renders, and the display partition is never
    /// finer than the class partition.
    ///
    /// NOTHING HERE ASSERTS THAT ROWS ARE MERGED, and that is deliberate. Merging two rows that share a
    /// display bucket means choosing one raw pair and discarding the other, so a player would withdraw
    /// a bag carrying numbers they never deposited. The rows stay; only the counting and drawing group.
    /// </summary>
    [TestClass]
    public class VaultClassDisplayGroupTests
    {
        private const uint OwnerAccount = 6401;
        private const uint OwnerCharacter = 0x50000401;

        private const uint BagWcid = 21013;

        /// <summary>A wcid nothing in these tests classifies, used purely as vault bulk and as a ledger row.</summary>
        private const uint FillerWcid = 8000;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Groupowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        private MemoryAppender appender;
        private Hierarchy hierarchy;
        private Level priorLevel;
        private bool priorConfigured;

        /// <summary>
        /// Every PropertyManager key anything in this file reads, seeded here and restored in
        /// <see cref="Cleanup"/>.
        ///
        /// PropertyManager's caches are process-wide and shared by every test class in the run, so a
        /// class that reads a key it never seeded passes only when some earlier class happened to seed
        /// it, and fails when run alone. Every one of them is read by AccountVaultStore or
        /// AccountVaultManager on a path this file exercises.
        /// </summary>
        [TestInitialize]
        public void Setup()
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld();

            // The class tier only ever sees items the pristine check has already refused.
            world.PristineResult = false;
            world.ClassifyResult = true;

            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", 100000));
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_landblock",
                DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item));

            // account_vault_class_storage.
            VaultClassTestConfig.Seed();

            // The profile's counters are process-static, like PropertyManager's caches.
            VaultFoldProfile.ResetForTest();

            // The store's log markers are part of its contract with an operator, so one of them is
            // asserted on directly. Restored in Cleanup - log4net's hierarchy is process-wide, exactly
            // like PropertyManager's caches, and leaving an appender on it would follow every later
            // test class in the run.
            appender = new MemoryAppender();
            hierarchy = (Hierarchy)LogManager.GetRepository(typeof(AccountVaultStore).Assembly);
            priorLevel = hierarchy.Root.Level;
            priorConfigured = hierarchy.Configured;

            hierarchy.Root.AddAppender(appender);
            hierarchy.Root.Level = Level.All;
            hierarchy.Configured = true;
        }

        [TestCleanup]
        public void Cleanup()
        {
            // Guarded because the log4net capture is the LAST thing Setup does: anything above it
            // throwing - a PropertyManager key missing from the registry, say - leaves these null, and
            // an unguarded dereference here would throw a NullReferenceException on top of the real
            // failure and be the only thing reported. Nothing leaks in that case; there is nothing to
            // undo.
            if (hierarchy != null && appender != null)
            {
                hierarchy.Root.RemoveAppender(appender);
                hierarchy.Root.Level = priorLevel;
                hierarchy.Configured = priorConfigured;
            }

            VaultFoldProfile.ResetForTest();

            PropertyManager.ModifyLong("account_vault_entry_cap", DefaultPropertyManager.DefaultLongProperties["account_vault_entry_cap"].Item);
            PropertyManager.ModifyLong("account_vault_landblock", DefaultPropertyManager.DefaultLongProperties["account_vault_landblock"].Item);
            PropertyManager.ModifyBool("account_vault_class_storage", DefaultPropertyManager.DefaultBooleanProperties["account_vault_class_storage"].Item);
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        /// <summary>Everything the store has logged during this test.</summary>
        private List<string> Messages() => appender.GetEvents().Select(e => e.RenderedMessage).ToList();

        /// <summary>
        /// A salvage bag. <paramref name="numItems"/> and <paramref name="workmanship"/> are the RAW
        /// pair; what the player sees is their quotient, which is the whole subject of this file.
        /// </summary>
        private static WorldObject Bag(int value, int structure, int workmanship, int numItems, string name = null)
        {
            return FakeVaultWorld.MakeSalvageBag(BagWcid, structure, workmanship, numItems, value, name);
        }

        /// <summary>
        /// A bag whose rendered workmanship is exactly 8.00 however many items went into it: every
        /// index gives a DIFFERENT raw pair and the SAME quotient, which is the exact shape that
        /// inflated the entry count on production.
        /// </summary>
        private static WorldObject EightPointZeroBag(int index, int value, int structure = 100)
        {
            var numItems = 10 + index;

            return Bag(value, structure, workmanship: 8 * numItems, numItems: numItems);
        }

        private Container SeedVault(int order = 1, int capacity = 255)
        {
            var container = FakeVaultWorld.MakeContainer(capacity);

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

        private WorldObject SeedFiller(Container container)
        {
            var filler = FakeVaultWorld.MakeStack(FillerWcid, 1, 100);

            world.UnclassifiableGuids.Add(filler.Guid.Full);

            Assert.IsTrue(container.TryAddToInventory(filler), "could not seed a vault item");

            return filler;
        }

        private static bool Deposit(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out reason));

            failReason = reason;
            return ok;
        }

        private static bool Withdraw(AccountVaultStore store, VaultEntry entry, int amount, out List<WorldObject> withdrawn, out string failReason)
        {
            var ok = false;
            List<WorldObject> got = null;
            string reason = null;

            store.Enqueue(() => ok = store.TryWithdraw(entry, amount, Owner, out got, out reason, GroupTakeOrder.Front));

            withdrawn = got ?? new List<WorldObject>();
            failReason = reason;
            return ok;
        }

        /// <summary>
        /// Runs fold passes until <paramref name="expected"/> items have moved, and reports the total.
        ///
        /// A FIXED PASS BUDGET RATHER THAN "until a pass folds nothing", because a pass legitimately
        /// folds nothing while items remain: the fold drains ONE CLASS at a time and spends a whole
        /// pass discovering that its current target is finished before it picks another. Stopping at
        /// the first zero would leave the vault half folded, and the caller's count assertion would
        /// then read as a fold that had completed.
        /// </summary>
        private static int FoldPasses(AccountVaultStore store, int expected, int budget = 100)
        {
            var total = 0;

            for (var pass = 0; pass < (expected * 4) + 10 && total < expected; pass++)
            {
                var moved = 0;
                var clock = 1000.0 + pass;

                store.Enqueue(() => moved = store.FoldSomeStoredItemsOnQueue(clock, budget, out _));

                total += moved;
            }

            return total;
        }

        // ------------------------------------------------------- 1. the fold never costs entries

        /// <summary>
        /// TEST 1, and the one whose absence let the defect ship: folding a vault NEVER increases its
        /// entry count.
        ///
        /// Run at TWO vault sizes, because a single size passes on broken code. With N bags that share
        /// a rendered quotient but differ on the raw pair, the pre-fold count is 1 at every N (they are
        /// one group row) and the broken post-fold count is N. Any single N therefore has a constant
        /// that a wrong implementation can match by accident - "it went from 1 to 1" is only evidence
        /// that entries did not grow if the alternative would have grown differently at a different
        /// size. Two sizes pins the relationship rather than one of its values.
        /// </summary>
        [TestMethod]
        public void Fold_NeverIncreasesTheEntryCount_AtTwoVaultSizes()
        {
            AssertFoldIsEntryNeutral(bagCount: 4);
            AssertFoldIsEntryNeutral(bagCount: 9);
        }

        private void AssertFoldIsEntryNeutral(int bagCount)
        {
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            var container = SeedVault();

            for (var i = 0; i < bagCount; i++)
                Assert.IsTrue(container.TryAddToInventory(EightPointZeroBag(i, value: 100 + i)), "could not seed a salvage bag");

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before a fold pass can see anything in it");

            var before = store.EntryCount;

            Assert.AreEqual(1, before,
                $"{bagCount} bags that all render workmanship 8.00 at the same Structure and Name must draw as ONE panel row before the fold");

            var folded = FoldPasses(store, expected: bagCount);

            // Control: the count assertion below means nothing unless the fold actually ran. A fold
            // that refused everything would leave the count at 1 and pass.
            Assert.AreEqual(bagCount, folded, "every bag must have folded, or this measures nothing");

            Assert.AreEqual(bagCount, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0),
                "each distinct raw (ItemWorkmanship, NumItemsInMaterial) pair must keep its OWN class row - merging them is the lossy repair the fix forbids");

            var after = store.EntryCount;

            Assert.AreEqual(before, after,
                $"folding {bagCount} bags took the entry count from {before} to {after}. The fold must never cost a vault entries: " +
                $"those {bagCount} class rows draw as one panel line, so they must count as one.");

            // And the count is the number of lines, not a coincidence: the panel really does draw one.
            Assert.AreEqual(1, store.GetEntries(0, -1).Count(e => e.Kind == VaultEntryKind.Class));
        }

        // ------------------------------------------------------- 2. count equals render

        /// <summary>
        /// TEST 2. EntryCount() equals GetEntries().Count for a vault holding all three kinds at once,
        /// including a class that is only PARTLY folded - some of its bags already class rows, the rest
        /// still stored biotas.
        ///
        /// The invariant was documented at AccountVaultStore's EnumerateEntriesLocked and at
        /// VaultCollapse's grouping remarks and tested nowhere, which is how the two halves drifted:
        /// one counted rows while the other drew groups. They now read the same projection, and this
        /// is what says so.
        /// </summary>
        [TestMethod]
        public void EntryCount_EqualsTheNumberOfLinesGetEntriesRenders()
        {
            var container = SeedVault();

            // Two stored biotas nothing will ever classify: two lines of their own.
            SeedFiller(container);
            SeedFiller(container);

            // Five bags of one display group.
            for (var i = 0; i < 5; i++)
                Assert.IsTrue(container.TryAddToInventory(EightPointZeroBag(i, value: 500 + i)));

            // A collapsed-stackable ledger row, seeded straight onto the table so it is present before
            // the store's first read.
            backend.Stacks.Add(new AccountVaultStack
            {
                Id = 9900,
                AccountId = OwnerAccount,
                Wcid = FillerWcid + 1,
                Count = 7,
            });

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);

            // A PARTIAL fold: stop as soon as two bags have moved, leaving three where they were. The
            // fold drains one class at a time and each of these bags is its own class, so this is the
            // straightforward way to reach a half-migrated vault - which is the state the two halves of
            // the entry count are most likely to disagree in.
            var folded = FoldPasses(store, expected: 2);

            Assert.AreEqual(2, folded, "the setup wants a PARTLY folded class: two bags moved, three left stored");

            var entries = store.GetEntries(0, -1);

            Assert.AreEqual(store.EntryCount, entries.Count,
                "the entry cap counts what the panel draws, so EntryCount and GetEntries must be the same number");

            // Control: all three kinds really are present, and the class line really does cover both
            // folded rows. Without this the equality could hold over an empty or one-kind vault.
            Assert.AreEqual(2, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0), "two class rows were folded");

            var classEntries = entries.Where(e => e.Kind == VaultEntryKind.Class).ToList();

            Assert.AreEqual(1, classEntries.Count, "both folded rows share a display group, so they draw as one line");
            Assert.AreEqual(2, classEntries[0].ClassMembers.Count, "and that line names both of its member rows");
            Assert.AreEqual(2, classEntries[0].Count, "its count is the sum of its members' counts");

            Assert.AreEqual(1, entries.Count(e => e.Kind == VaultEntryKind.Ledger));
            Assert.AreEqual(3, entries.Count(e => e.Kind == VaultEntryKind.StoredItem),
                "two unclassifiable items plus one group row over the three bags that did not fold");

            Assert.AreEqual(5, entries.Count);
        }

        // ------------------------------------------------------- 3. a new bag on a drawn line is free

        /// <summary>
        /// TEST 3. At a full cap, a bag whose raw pair is NOVEL but whose display group already exists
        /// is ACCEPTED - it increments a line the panel is already drawing, so it costs no entry.
        ///
        /// This is the second half of the defect. AddsEntryLocked charged on "the class key is new",
        /// and a freshly salvaged bag almost always carries a novel raw pair, so the counted tier built
        /// to make hoarding free was charging an entry per deposit.
        ///
        /// THE CONTROL IS NOT OPTIONAL. Without it this test passes just as well against a build that
        /// stopped enforcing the cap at all, so the second half asserts that a genuinely new display
        /// group is still refused at the same cap.
        /// </summary>
        [TestMethod]
        public void Deposit_OfANovelRawPairOnAnAlreadyDrawnLine_IsFreeAtTheCap()
        {
            var store = NewStore();

            // (112, 14) renders 8.00 at Structure 100.
            Assert.IsTrue(Deposit(store, Bag(500, structure: 100, workmanship: 112, numItems: 14), out var reason), reason);

            Assert.AreEqual(1, store.EntryCount);

            // Exactly full.
            Assert.IsTrue(PropertyManager.ModifyLong("account_vault_entry_cap", store.EntryCount));

            // (104, 13) renders 8.00 too - a different class, the same drawn line.
            var joining = Bag(600, structure: 100, workmanship: 104, numItems: 13);

            Assert.IsTrue(Deposit(store, joining, out reason),
                $"a bag whose display group already exists must not be refused at the cap: {reason}");

            Assert.AreEqual(2, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0),
                "it must open its OWN class row - its raw pair differs, and that is what keeps a withdraw exact");

            Assert.AreEqual(1, store.EntryCount, "but it must not have cost an entry, because no new line appeared");

            // THE CONTROL: a genuinely new display group, at the same cap, is still refused.
            var newLine = Bag(700, structure: 42, workmanship: 112, numItems: 14);

            Assert.IsFalse(Deposit(store, newLine, out reason),
                "a deposit that opens a NEW display group adds an entry and must still be refused at the cap");

            StringAssert.Contains(reason ?? string.Empty, "entries");

            Assert.IsFalse(world.Destroyed.Contains(newLine), "a refused deposit must never destroy the item");
            Assert.AreEqual(2, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0));
        }

        // ------------------------------------------------------- 4. the multi-row withdraw is exact

        /// <summary>
        /// TEST 4. Draining a display group that spans several class rows hands back exactly the pooled
        /// total, with nothing stranded, and every bag carries the raw pair of the MEMBER it came from.
        ///
        /// The second half is the one that matters most. The whole reason the rows are not merged is
        /// that each one's canonical form is what its items are rebuilt from; a drain that rebuilt
        /// everything from the representative's payload would conserve the value perfectly and still
        /// hand the player back bags they never deposited.
        /// </summary>
        [TestMethod]
        public void Withdraw_AcrossSeveralMemberRows_ConservesValue_AndKeepsEachMembersRawPair()
        {
            var store = NewStore();

            // Three classes, two bags each, all rendering 8.00 at Structure 100 - one drawn line.
            var pairs = new[] { (wm: 80, num: 10), (wm: 88, num: 11), (wm: 96, num: 12) };
            var values = new[] { 13, 27, 1000, 1, 55, 98765 };

            var v = 0;

            foreach (var pair in pairs)
            {
                for (var i = 0; i < 2; i++)
                    Assert.IsTrue(Deposit(store, Bag(values[v++], structure: 100, workmanship: pair.wm, numItems: pair.num), out var reason), reason);
            }

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(3, entry.ClassMembers.Count, "three class rows must draw as one line");
            Assert.AreEqual(6, entry.Count, "the line's count is the sum of its members'");
            Assert.AreEqual(values.Sum(), entry.TotalValue, "and its pool is the sum of theirs");

            // Captured BEFORE the drain: the per-member totals the withdrawn bags have to reproduce.
            var expectedPerMember = entry.ClassMembers.ToDictionary(m => m.ClassKey, m => (m.Count, m.TotalValue));

            Assert.IsTrue(Withdraw(store, entry, 6, out var withdrawn, out var failReason), failReason);

            Assert.AreEqual(6, withdrawn.Count, "one object per item, never a stack");

            Assert.AreEqual(values.Sum(), withdrawn.Sum(w => w.Value ?? 0),
                "the pool must be conserved exactly across the whole group");

            // Nothing stranded: no row left holding a remainder, and no line left on the panel.
            Assert.AreEqual(0, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0));
            Assert.AreEqual(0, store.GetEntries(0, -1).Count(e => e.Kind == VaultEntryKind.Class));

            // Every bag carries its own member's raw pair, and each member's own pool is conserved.
            foreach (var pair in pairs)
            {
                var fromPair = withdrawn
                    .Where(w => w.GetProperty(PropertyInt.ItemWorkmanship) == pair.wm
                             && w.GetProperty(PropertyInt.NumItemsInMaterial) == pair.num)
                    .ToList();

                Assert.AreEqual(2, fromPair.Count,
                    $"two bags must come back carrying the raw pair ({pair.wm}, {pair.num}) they were deposited with");
            }

            foreach (var member in expectedPerMember)
            {
                // Rebuild the member's identity from its own payload, so this compares against the row
                // rather than against the test's own bookkeeping.
                Assert.IsTrue(VaultItemClass.TryParseCanonicalForm(
                    entry.ClassMembers.Single(m => m.ClassKey == member.Key).CanonicalForm, out _, out var overrides, out _));

                var wm = overrides.GetInt(PropertyInt.ItemWorkmanship);
                var num = overrides.GetInt(PropertyInt.NumItemsInMaterial);

                var fromMember = withdrawn
                    .Where(w => w.GetProperty(PropertyInt.ItemWorkmanship) == wm
                             && w.GetProperty(PropertyInt.NumItemsInMaterial) == num)
                    .ToList();

                Assert.AreEqual(member.Value.Count, fromMember.Count, $"class {member.Key} must hand over exactly its own item count");
                Assert.AreEqual(member.Value.TotalValue, fromMember.Sum(w => w.Value ?? 0),
                    $"class {member.Key} must hand over exactly its own pooled total, not a share of the group average");
            }
        }

        /// <summary>
        /// A PARTIAL drain takes from the FRONT members first, in class key ascending order, and leaves
        /// the untouched ones exactly as they were. The order is part of the contract: the same panel
        /// action twice has to do the same thing.
        /// </summary>
        [TestMethod]
        public void PartialWithdraw_DrainsMembersInClassKeyOrder_AndLeavesTheRestUntouched()
        {
            var store = NewStore();

            foreach (var pair in new[] { (wm: 80, num: 10), (wm: 88, num: 11), (wm: 96, num: 12) })
            {
                for (var i = 0; i < 2; i++)
                    Assert.IsTrue(Deposit(store, Bag(100, structure: 100, workmanship: pair.wm, numItems: pair.num), out var reason), reason);
            }

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(3, entry.ClassMembers.Count);

            var ordered = entry.ClassMembers.Select(m => m.ClassKey).ToList();

            CollectionAssert.AreEqual(ordered.OrderBy(k => k, StringComparer.Ordinal).ToList(), ordered,
                "members must be ordered by class key ascending, matching the DAO's own ORDER BY");

            // Three items: the whole first member and half the second.
            Assert.IsTrue(Withdraw(store, entry, 3, out var withdrawn, out var failReason), failReason);
            Assert.AreEqual(3, withdrawn.Count);

            var rows = backend.Classes.Where(c => c.AccountId == OwnerAccount && c.Count > 0)
                .ToDictionary(c => c.ClassKey, c => c.Count);

            Assert.IsFalse(rows.ContainsKey(ordered[0]), "the first member must be drained and reaped");
            Assert.AreEqual(1, rows[ordered[1]], "the second must have given up exactly one item");
            Assert.AreEqual(2, rows[ordered[2]], "and the third must be untouched");

            // The line is still one line, now over two rows.
            var after = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(2, after.ClassMembers.Count);
            Assert.AreEqual(3, after.Count);
            Assert.AreEqual(1, store.EntryCount);
        }

        /// <summary>
        /// THE UNWIND, at its worst interleaving: an earlier member's debit comes back
        /// <see cref="AccountVaultStackAdjustResult.Failed"/>, and then a LATER member cannot build its
        /// objects, so the whole drain unwinds over a debit nobody can account for.
        ///
        /// THE RULING BEING PINNED. The unwind refunds every committed member, the Failed one included.
        /// That is deliberate and it is the standing principle of the whole withdraw path: never
        /// destroy property on an unknown outcome. The cost is the case where the Failed debit truly
        /// never landed, in which the refund creates items that were never taken - bounded per call to
        /// the value of the requested withdrawal. What this diff changed is the BLAST RADIUS, from one
        /// row per call to N, which is why every such refund now emits its own log marker instead of
        /// disappearing into the generic unwind line.
        ///
        /// Both halves are asserted: the resulting table state, and the marker. The marker alone would
        /// pass against an unwind that logged and refunded nothing; the table state alone would pass
        /// against one that refunded silently, which is the thing an operator cannot then find.
        /// </summary>
        [TestMethod]
        public void Unwind_RefundingAMemberWhoseDebitWasFailed_RestoresEveryRow_AndLogsItsOwnMarker()
        {
            var store = NewStore();

            // Three classes of two items each, all one display group, all with different values so a
            // refund that recomputed a share could not land on the right number by accident.
            var pairs = new[] { (wm: 80, num: 10), (wm: 88, num: 11), (wm: 96, num: 12) };
            var values = new[] { 100, 201, 302, 403, 504, 605 };

            var v = 0;

            foreach (var pair in pairs)
            {
                for (var i = 0; i < 2; i++)
                    Assert.IsTrue(Deposit(store, Bag(values[v++], structure: 100, workmanship: pair.wm, numItems: pair.num), out var reason), reason);
            }

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(3, entry.ClassMembers.Count);
            Assert.AreEqual(6, entry.Count);

            // Captured BEFORE anything moves. The drain visits members in the order they appear here,
            // which is class key ascending - a SHA-256 ordering, so which deposit pair ends up first is
            // not predictable and must be read off the entry rather than assumed.
            var before = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            var appliedMember = entry.ClassMembers[0];
            var failedMember = entry.ClassMembers[1];
            var untouchedMember = entry.ClassMembers[2];

            // Member 2's adjust returns Failed, modelled by the fake as NOT applying the delta - the
            // harder half, because the caller has to be correct for a Failed that did not land as well
            // as for one that did.
            backend.ClassAdjustFailedKeys.Add(failedMember.ClassKey);

            var destroyedBefore = world.Destroyed.Count;
            var adjustsBefore = backend.ClassAdjustCalls;

            // MaterializeFailAfterCalls IS A COUNTDOWN, NOT A CALL INDEX: setting it to N lets the next
            // N calls succeed and makes EVERY call after them return null. The drain materializes one
            // probe plus (take - 1) objects per member, so with three members taking two each the call
            // sequence is member1 probe, member1 item2, member2 probe, member2 item2, member3 probe.
            // Four lets members 1 and 2 finish and kills member 3's PROBE, which is the branch that
            // unwinds with two members already committed. It is set AFTER the deposits on purpose -
            // each deposit's round-trip self-check materializes once and would otherwise eat the
            // countdown.
            world.MaterializeFailAfterCalls = 4;

            Assert.IsFalse(Withdraw(store, entry, 6, out var withdrawn, out var failReason),
                "a drain that cannot build a later member's objects must refuse rather than hand over a partial group");

            world.MaterializeFailAfterCalls = null;

            Assert.AreEqual(0, withdrawn.Count, "all or nothing across the whole group: nothing may be delivered");

            // Every object built for the two members that did commit is destroyed, not leaked.
            Assert.AreEqual(4, world.Destroyed.Count - destroyedBefore,
                "the four objects already built for members 1 and 2 must be destroyed by the unwind");

            // THE TABLE STATE. Member 1 was debited and refunded; member 2's debit never landed and its
            // refund was refused by the same switch, so it is unchanged; member 3 was never touched.
            // Every row is back exactly where it started.
            var after = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            foreach (var key in before.Keys)
            {
                Assert.IsTrue(after.ContainsKey(key), $"class {key} must still exist after the unwind");
                Assert.AreEqual(before[key], after[key],
                    $"class {key} must be back exactly where it started: expected {before[key]}, found {after[key]}");
            }

            Assert.AreEqual(1, store.EntryCount, "and the line is whole again");
            Assert.AreEqual(6, store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class).Count);

            // Two debits plus two refunds, and no more: the untouched member's row was never adjusted.
            Assert.AreEqual(4, backend.ClassAdjustCalls - adjustsBefore,
                "two debits and two refunds - member 3 was never debited, so it must never be refunded either");

            // THE MARKER. Its own line, greppable on its own, carrying everything needed to reconcile.
            var markers = Messages()
                .Where(m => m.StartsWith("[VAULT] CLASS REFUND OVER UNKNOWN DEBIT:", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(1, markers.Count,
                $"exactly one refund happened over a non-Applied debit, so exactly one marker must be logged; found {markers.Count}");

            var marker = markers[0];

            StringAssert.Contains(marker, $"account {OwnerAccount}", "the marker must name the account");
            StringAssert.Contains(marker, failedMember.ClassKey, "the marker must name the class whose debit is unaccounted for");
            StringAssert.Contains(marker, $"wcid {BagWcid}", "the marker must name the wcid");
            StringAssert.Contains(marker, $"+{failedMember.Count} item(s)", "the marker must say how many items were refunded");
            StringAssert.Contains(marker, $"+{failedMember.TotalValue} value", "the marker must say how much value was refunded");
            StringAssert.Contains(marker, AccountVaultStackAdjustResult.Failed.ToString(),
                "the marker must say which outcome the debit actually returned, or an operator cannot tell a Failed refund from an AppliedCountUnknown one");
            StringAssert.Contains(marker, "account_vault_log", "the marker must say what to reconcile against");

            // CONTROL: no marker for the member whose debit provably APPLIED. Without this the
            // assertion above would pass against an unwind that marked every refund, which would bury
            // the rare event this marker exists to surface under the ordinary one.
            Assert.IsFalse(markers.Any(m => m.Contains(appliedMember.ClassKey, StringComparison.Ordinal)),
                "a refund over a debit that provably applied is ordinary and must NOT carry this marker");

            Assert.IsFalse(markers.Any(m => m.Contains(untouchedMember.ClassKey, StringComparison.Ordinal)),
                "a member that was never debited must not be refunded, let alone marked");
        }

        /// <summary>
        /// The sibling test above covers a Failed debit whose REFUND also fails, because
        /// ClassAdjustFailedKeys is permanent and catches both calls. That coupling hides the shape the
        /// marker actually exists for: a debit that reported Failed without landing, followed by a
        /// refund that DOES land. The row is then over-credited, no exception was thrown, and the only
        /// artefact that says so is the log line. So this test proves the marker's numbers ARE the
        /// reconciliation delta - it asserts the row drifted by exactly the amount the marker names,
        /// rather than merely that a marker appeared.
        /// </summary>
        [TestMethod]
        public void Unwind_WhenAFailedDebitDidNotLandButItsRefundDid_OverCreditsByExactlyTheMarkedAmount()
        {
            var store = NewStore();

            var pairs = new[] { (wm: 80, num: 10), (wm: 88, num: 11), (wm: 96, num: 12) };
            var values = new[] { 100, 201, 302, 403, 504, 605 };

            var v = 0;

            foreach (var pair in pairs)
            {
                for (var i = 0; i < 2; i++)
                    Assert.IsTrue(Deposit(store, Bag(values[v++], structure: 100, workmanship: pair.wm, numItems: pair.num), out var reason), reason);
            }

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(3, entry.ClassMembers.Count);

            var before = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            var appliedMember = entry.ClassMembers[0];
            var failedMember = entry.ClassMembers[1];
            var untouchedMember = entry.ClassMembers[2];

            // ONE failure only: the DEBIT returns Failed and does not land, then the countdown is spent,
            // so the refund the unwind issues for the same key goes through and really credits the row.
            backend.ClassAdjustFailedCounts[failedMember.ClassKey] = 1;

            // Same countdown as the sibling test: let members 1 and 2 finish, kill member 3's probe, so
            // the unwind runs with two members already committed. See that test for the call sequence.
            world.MaterializeFailAfterCalls = 4;

            Assert.IsFalse(Withdraw(store, entry, 6, out var withdrawn, out _),
                "a drain that cannot build a later member's objects must refuse rather than hand over a partial group");

            world.MaterializeFailAfterCalls = null;

            Assert.AreEqual(0, withdrawn.Count, "all or nothing across the whole group: nothing may be delivered");

            var after = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            // The member whose debit provably applied is back exactly where it started, and so is the
            // one that was never touched. Only the ambiguous member drifted.
            Assert.AreEqual(before[appliedMember.ClassKey], after[appliedMember.ClassKey],
                "a debit that applied and was refunded must net to zero");
            Assert.AreEqual(before[untouchedMember.ClassKey], after[untouchedMember.ClassKey],
                "a member that was never debited must not move");

            // THE DRIFT, and the whole point of the test: the refund landed over a debit that did not,
            // so the row is up by exactly one member's worth.
            Assert.AreEqual(before[failedMember.ClassKey].Count + failedMember.Count, after[failedMember.ClassKey].Count,
                "the refund landed over a debit that did not, so the row is over-credited by the refunded count");
            Assert.AreEqual(before[failedMember.ClassKey].TotalValue + failedMember.TotalValue, after[failedMember.ClassKey].TotalValue,
                "and by the refunded value");

            // THE MARKER carries that exact drift, which is what makes it actionable.
            var markers = Messages()
                .Where(m => m.StartsWith("[VAULT] CLASS REFUND OVER UNKNOWN DEBIT:", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(1, markers.Count,
                $"exactly one refund happened over a non-Applied debit, so exactly one marker must be logged; found {markers.Count}");

            var marker = markers[0];

            StringAssert.Contains(marker, failedMember.ClassKey, "the marker must name the drifted class");
            StringAssert.Contains(marker, $"+{failedMember.Count} item(s)", "the marker's count must be the drift an operator has to reverse");
            StringAssert.Contains(marker, $"+{failedMember.TotalValue} value", "the marker's value must be the drift an operator has to reverse");

            // CONTROL: the ordinary refund is still unmarked, so the marker stays rare enough to act on.
            Assert.IsFalse(markers.Any(m => m.Contains(appliedMember.ClassKey, StringComparison.Ordinal)),
                "a refund over a debit that provably applied is ordinary and must NOT carry this marker");
        }

        /// <summary>
        /// THE SECOND REFUND PATH, which is a different one from the test above and shipped unmarked
        /// for a commit.
        ///
        /// A member that runs out of objects part way through building its OWN items does not reach
        /// the unwind: it destroys what it built, refunds its own debit on the spot, and removes itself
        /// from the committed list, so <c>UnwindClassDrain</c> never sees it. Its debit outcome was
        /// already recorded by then, so it can be refunding a `Failed` member with exactly the risk the
        /// marker exists to surface - and it logged nothing.
        ///
        /// The discriminator against the test above is the LAST assertion: the generic unwind line must
        /// be ABSENT. That is what proves the marker came from this path rather than from the unwind,
        /// which is the only way the two tests can be told apart from their output.
        /// </summary>
        [TestMethod]
        public void MidBuildShortfall_RefundingAFailedMember_LogsTheSameMarker_WithoutReachingTheUnwind()
        {
            var store = NewStore();

            // Two classes of two items each, one display group.
            foreach (var pair in new[] { (wm: 80, num: 10), (wm: 88, num: 11) })
            {
                for (var i = 0; i < 2; i++)
                    Assert.IsTrue(Deposit(store, Bag(100 + i, structure: 100, workmanship: pair.wm, numItems: pair.num), out var reason), reason);
            }

            var entry = store.GetEntries(0, -1).Single(e => e.Kind == VaultEntryKind.Class);

            Assert.AreEqual(2, entry.ClassMembers.Count);
            Assert.AreEqual(4, entry.Count);

            var before = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            // The FIRST member drained is the one made to fail, so the shortfall happens before any
            // other member has been touched and the unwind therefore has nothing to do.
            var failedMember = entry.ClassMembers[0];
            var untouchedMember = entry.ClassMembers[1];

            backend.ClassAdjustFailedKeys.Add(failedMember.ClassKey);

            var destroyedBefore = world.Destroyed.Count;
            var adjustsBefore = backend.ClassAdjustCalls;

            // THE CALL SEQUENCE, worked out rather than guessed, because this countdown has cost three
            // rounds already. MaterializeFailAfterCalls is a COUNTDOWN: N lets calls 1..N succeed and
            // makes call N+1 and everything after return null. The drain materializes one probe plus
            // (take - 1) items per member, so member 1 taking two items is:
            //
            //   call 1 = member 1's PROBE        <- must succeed, or the drain refuses before the
            //                                       row moves and no refund happens at all
            //   call 2 = member 1's SECOND ITEM  <- must fail, which is the mid-build shortfall
            //
            // So N = 1. It is set AFTER the four deposits because each one's round-trip self-check
            // materializes once and would otherwise eat the countdown - see the field's own remarks.
            world.MaterializeFailAfterCalls = 1;

            Assert.IsFalse(Withdraw(store, entry, 4, out var withdrawn, out var failReason),
                "a member that cannot build all its items must refuse rather than hand over part of a row");

            world.MaterializeFailAfterCalls = null;

            Assert.AreEqual(0, withdrawn.Count, "nothing may be delivered");

            // Only the probe was ever built, and it is destroyed.
            Assert.AreEqual(1, world.Destroyed.Count - destroyedBefore,
                "the one object built before the shortfall must be destroyed");

            // One debit and one refund, both on the failing member. The other member was never planned
            // past the point of failure, so it must never be adjusted.
            Assert.AreEqual(2, backend.ClassAdjustCalls - adjustsBefore,
                "one debit and one self-refund on the failing member, and nothing on the other");

            var after = backend.Classes
                .Where(c => c.AccountId == OwnerAccount)
                .ToDictionary(c => c.ClassKey, c => (c.Count, c.TotalValue));

            foreach (var key in before.Keys)
            {
                Assert.IsTrue(after.ContainsKey(key), $"class {key} must still exist");
                Assert.AreEqual(before[key], after[key], $"class {key} must be back exactly where it started");
            }

            Assert.AreEqual(1, store.EntryCount);

            // THE MARKER, from the mid-build refund site.
            var markers = Messages()
                .Where(m => m.StartsWith("[VAULT] CLASS REFUND OVER UNKNOWN DEBIT:", StringComparison.Ordinal))
                .ToList();

            Assert.AreEqual(1, markers.Count,
                $"the mid-build refund is a refund over a non-Applied debit and must carry the marker too; found {markers.Count}");

            StringAssert.Contains(markers[0], failedMember.ClassKey, "the marker must name the class whose debit is unaccounted for");
            StringAssert.Contains(markers[0], AccountVaultStackAdjustResult.Failed.ToString(), "and the outcome that debit returned");

            Assert.IsFalse(markers.Any(m => m.Contains(untouchedMember.ClassKey, StringComparison.Ordinal)),
                "the member that was never debited must not be marked");

            // THE DISCRIMINATOR against the unwind test: this path refunds itself and removes itself
            // from the committed list, so UnwindClassDrain is reached with nothing to do and its own
            // line never appears. Without this assertion the test would pass against a build that had
            // only ever marked the unwind path, which is the exact gap this test was written for.
            Assert.IsFalse(Messages().Any(m => m.Contains("unwinding a class withdraw part way through", StringComparison.Ordinal)),
                "the mid-build shortfall must refund itself without the unwind refunding anything, or this test is measuring the same path as the one above");
        }

        // ------------------------------------------------------- 5. the extracted bucket helper

        /// <summary>
        /// TEST 5. The extracted quotient helper, table-driven over every branch it has: the primary
        /// quotient, the recovery branch, both clamp bounds, and both sentinels.
        ///
        /// The table is the contract. Each row names which branch it is exercising, because the branch
        /// is the thing being pinned - a number alone would not say whether a change had moved the
        /// clamp, the recovery formula or the rounding.
        /// </summary>
        [TestMethod]
        public void WorkmanshipBucket_ReproducesEveryBranchOfTheQuotient()
        {
            var cases = new (int? wm, int? num, int? structure, int expected, string branch)[]
            {
                // The primary quotient, rounded to what the client renders. The worked example from the
                // grouping design: (77, 12) and (154, 24) both read 6.42 and must bucket together.
                (77, 12, 100, 642, "primary quotient, rounded to two decimals"),
                (154, 24, 100, 642, "primary quotient, the other half of the worked example"),
                (80, 10, 100, 800, "primary quotient, exact"),
                (10, 10, 100, 100, "primary quotient exactly on the lower bound, so no recovery"),
                (100, 10, 100, 1000, "primary quotient exactly on the upper bound, so no recovery"),

                // Below 1 and above 10 both fall through to the recovery formula, which reads
                // ItemWorkmanship and Structure and ignores NumItemsInMaterial.
                (50000, 1, 5, 100, "recovery: quotient above 10, 50000/10000/5 = 1.0"),
                (100000, 2, 5, 200, "recovery: quotient above 10, 100000/10000/5 = 2.0"),
                (64200, 1, 1, 642, "recovery: quotient above 10, 64200/10000/1 = 6.42"),
                (5, 10, 1, 100, "recovery: quotient below 1, then clamped up to 1.0"),
                (2000000, 1, 1, 1000, "recovery: clamped down to 10.0"),
                (50000, 1, null, 500, "recovery with Structure absent, which defaults to 1 rather than to -1"),

                // NumItemsInMaterial absent defaults to 1, which is the primary branch, not recovery.
                (5, null, null, 500, "absent NumItemsInMaterial defaults to 1"),

                // The two sentinels.
                (null, 10, 100, VaultCollapse.WorkmanshipBucketNone, "no ItemWorkmanship at all"),
                (0, 1, 0, VaultCollapse.WorkmanshipBucketUnkeyable, "0/0 in the recovery formula produces NaN, which no key can be made from"),
            };

            foreach (var (wm, num, structure, expected, branch) in cases)
            {
                Assert.AreEqual(expected, VaultCollapse.WorkmanshipBucket(wm, num, structure),
                    $"({wm?.ToString() ?? "null"}, {num?.ToString() ?? "null"}, {structure?.ToString() ?? "null"}): {branch}");
            }

            // The two sentinels must stay DISTINCT: an item with no workmanship and a corrupted one
            // must never bucket together, which is what the guarded cast is for.
            Assert.AreNotEqual(VaultCollapse.WorkmanshipBucketNone, VaultCollapse.WorkmanshipBucketUnkeyable);
        }

        /// <summary>
        /// AccountVaultStore's stored-item bucket key really does route through the extracted helper,
        /// asserted at the one input where the two could differ: the RECOVERY branch.
        ///
        /// This is the case the helper's own remarks call out. Two bags at Structure 5, (50000, 1) and
        /// (100000, 2), have the same RAW quotient of 50000, so a bucket key that dropped the recovery
        /// branch would put them in one bucket - and AreGroupable forgives ItemWorkmanship and
        /// NumItemsInMaterial outright once two items share a bucket, so the panel would merge two bags
        /// that appraise at different workmanship tiers. The recovery formula separates them as 1.0 and
        /// 2.0.
        ///
        /// The CONTROL is the second half: a pair that really does share a bucket still draws as one
        /// row, so this is not simply a build in which nothing ever groups.
        /// </summary>
        [TestMethod]
        public void BucketKey_RoutesThroughTheExtractedHelper_IncludingItsRecoveryBranch()
        {
            // The helper's answer, stated first so the panel assertion below is checked against
            // something rather than merely observed.
            Assert.AreEqual(100, VaultCollapse.WorkmanshipBucket(50000, 1, 5));
            Assert.AreEqual(200, VaultCollapse.WorkmanshipBucket(100000, 2, 5));
            Assert.AreEqual(700, VaultCollapse.WorkmanshipBucket(7, 1, 5));
            Assert.AreEqual(700, VaultCollapse.WorkmanshipBucket(14, 2, 5));

            var container = SeedVault();

            // Two bags the recovery branch separates. Identical Structure and Name, differing only on
            // the raw pair - so ONLY the bucket key can keep them apart.
            Assert.IsTrue(container.TryAddToInventory(Bag(100, structure: 5, workmanship: 50000, numItems: 1)));
            Assert.IsTrue(container.TryAddToInventory(Bag(100, structure: 5, workmanship: 100000, numItems: 2)));

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded);

            Assert.AreEqual(2, store.EntryCount,
                "the recovery branch must separate (50000, 1) from (100000, 2) at Structure 5 - they appraise at different workmanship tiers, so merging them would draw two distinguishable bags as one row");

            // CONTROL: a pair the helper DOES put in one bucket draws as one row, so the assertion
            // above is not simply a build in which grouping never fires.
            backend = new FakeVaultBackend();
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = true };

            var second = SeedVault();

            Assert.IsTrue(second.TryAddToInventory(Bag(100, structure: 5, workmanship: 7, numItems: 1)));
            Assert.IsTrue(second.TryAddToInventory(Bag(250, structure: 5, workmanship: 14, numItems: 2)));

            var control = NewStore();

            Assert.IsTrue(control.IsLoaded);

            Assert.AreEqual(1, control.EntryCount,
                "(7, 1) and (14, 2) both render 7.00, so they share a bucket and must draw as ONE row");
        }

        // ------------------------------------------------------- the memo

        /// <summary>
        /// The display grouping is MEMOIZED, and the memo is dropped whenever a class row moves.
        ///
        /// Both halves are load bearing and neither is enough alone. The cap check runs the grouping on
        /// every deposit, so without the memo a deposit would walk every class row the account holds -
        /// the exact O(N) the counted tier exists to avoid. And a memo that survived a mutation would
        /// hand a reader a removed row.
        /// </summary>
        [TestMethod]
        public void ClassDisplayGrouping_IsMemoized_AndDroppedWhenAClassRowMoves()
        {
            var store = NewStore();

            Assert.IsTrue(Deposit(store, EightPointZeroBag(0, value: 100), out var reason), reason);

            // One read to build the memo, THEN the baseline: the deposit itself drops the memo when it
            // applies the new count, so taking the baseline before this read would count that rebuild.
            Assert.AreEqual(1, store.EntryCount);

            var before = store.ClassGroupingRebuilds;

            // Repeated reads with nothing mutating serve the memo.
            for (var i = 0; i < 5; i++)
                Assert.AreEqual(1, store.EntryCount);

            Assert.AreEqual(before, store.ClassGroupingRebuilds, "a read with nothing mutated must serve the memo");

            // A deposit onto a NEW class row moves `classes`, so the next read must rebuild.
            Assert.IsTrue(Deposit(store, EightPointZeroBag(1, value: 200), out reason), reason);

            var entries = store.GetEntries(0, -1);

            Assert.IsTrue(store.ClassGroupingRebuilds > before, "a class mutation must drop the memo");

            Assert.AreEqual(1, entries.Count(e => e.Kind == VaultEntryKind.Class));
            Assert.AreEqual(2, entries.Single(e => e.Kind == VaultEntryKind.Class).ClassMembers.Count,
                "and the rebuilt grouping must see BOTH rows");
        }
    }
}
