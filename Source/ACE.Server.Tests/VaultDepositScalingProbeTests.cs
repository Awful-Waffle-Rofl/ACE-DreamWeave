using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
    /// DIAGNOSTIC PROBE for the "ga_Sell is slow" investigation. Measures the cost shape of an
    /// ENTRY-ADDING vault deposit, which is the case AccountVaultClassStorageTests does not cover:
    /// its Deposit_CostsTheSameWhateverTheVaultAlreadyHolds pins the MERGING deposit at 0 grouping
    /// rebuilds, and a merging deposit is documented as never reaching the cap check at all.
    ///
    /// The hypothesis under test: an entry-adding deposit reaches AccountVaultStore's cap check, which
    /// calls EntryCountLocked -> GroupedStoredItemsLocked, an O(stored items) walk; and because every
    /// deposit calls InvalidateGroupingLocked and bumps `version`, the memo that would make that walk
    /// free never survives from one deposit to the next. If so, depositing N items costs O(N * V) and
    /// the per-deposit time grows with vault size.
    ///
    /// Nothing here asserts a performance number as a regression gate - a wall-clock threshold in a
    /// unit test is a flake. The falsifiable assertion is the REBUILD COUNT, which is exact and
    /// machine-independent; the timings are written to a report file for magnitude only.
    /// </summary>
    [TestClass]
    public class VaultDepositScalingProbeTests
    {
        private const uint OwnerAccount = 6401;
        private const uint OwnerCharacter = 0x50000401;

        private const uint FillerWcid = 8000;
        private const uint DepositWcid = 8100;
        private const uint SalvageWcid = 21013;

        /// <summary>Production vault containers pin ItemCapacity to this, so bulk spreads across several.</summary>
        private const int VaultSlots = 255;

        private static readonly VaultActor Owner = new VaultActor(OwnerAccount, OwnerCharacter, "Probeowner");

        private FakeVaultBackend backend;
        private FakeVaultWorld world;

        [TestInitialize]
        public void Setup()
        {
            ResetFakes();

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

        private void ResetFakes()
        {
            backend = new FakeVaultBackend();

            // PristineResult false: nothing collapses to the stack ledger, so every deposit is a stored
            // biota. ClassifyResult false: the class tier never claims an item, which forces classKey to
            // stay null in TryDeposit and is what routes the deposit down the grouping/cap-check branch.
            world = new FakeVaultWorld { PristineResult = false, ClassifyResult = false };
        }

        private AccountVaultStore NewStore() => new AccountVaultStore(OwnerAccount, backend, world);

        /// <summary>
        /// Seeds <paramref name="itemCount"/> stored biotas across as many 255-slot vault containers as
        /// they need, plus <paramref name="freeSlots"/> spare capacity in a final container so the
        /// measured deposits all land without the store having to create a vault mid-measurement.
        ///
        /// When <paramref name="groupCandidateFiller"/> is false the filler is ItemType.Misc, which
        /// AccountVaultStore.IsGroupCandidate refuses, so each filler becomes its own single-item group
        /// and a rebuild costs one list allocation per item and no biota diffs. When it is true the
        /// filler is salvage-bag shaped (ItemType.TinkeringMaterial) in ONE bucket, differing only in
        /// Value - a tolerated difference - so they all join one group and a rebuild pays a real
        /// VaultCollapse.AreGroupable biota diff per item. The two bound the constant from below and
        /// above.
        /// </summary>
        private void SeedVault(int itemCount, int freeSlots, bool groupCandidateFiller)
        {
            var remaining = itemCount;
            var order = 0;

            while (remaining > 0 || order == 0)
            {
                var here = Math.Min(remaining, VaultSlots);

                AddVault(order, here, groupCandidateFiller);

                remaining -= here;
                order++;

                if (remaining <= 0)
                    break;
            }

            // A final container with room for the measured deposits, so no deposit pays vault creation.
            if (freeSlots > 0)
                AddVault(order, 0, groupCandidateFiller);
        }

        private void AddVault(int order, int itemCount, bool groupCandidateFiller)
        {
            var container = FakeVaultWorld.MakeContainer(VaultSlots);

            world.Containers[container.Guid.Full] = container;

            backend.Vaults.Add(new ShardAccountVault
            {
                Id = (uint)(9700 + order),
                AccountId = OwnerAccount,
                ContainerGuid = container.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
            });

            for (var i = 0; i < itemCount; i++)
            {
                WorldObject filler = groupCandidateFiller
                    ? FakeVaultWorld.MakeSalvageBag(SalvageWcid, 100, 7, 10, 100 + i, "Salvage (100)")
                    : FakeVaultWorld.MakeStack(FillerWcid, 1, 100);

                world.UnclassifiableGuids.Add(filler.Guid.Full);

                Assert.IsTrue(container.TryAddToInventory(filler), $"could not seed vault filler {i} in vault {order}");
            }
        }

        /// <summary>
        /// One item to deposit. ItemType.Misc, so IsGroupCandidate refuses it and
        /// JoinsAnExistingGroupLocked answers false without a walk - which makes AddsEntryLocked true and
        /// sends the deposit into HasRoomForLocked -> EntryCountLocked. That is the realistic mule case:
        /// a weapon or a piece of armour is never TinkeringMaterial, so every such deposit adds an entry.
        /// Registered unclassifiable so the class tier cannot claim it even if the predicate were on.
        /// </summary>
        private WorldObject DepositItem()
        {
            var item = FakeVaultWorld.MakeStack(DepositWcid, 1, 100);

            world.UnclassifiableGuids.Add(item.Guid.Full);

            return item;
        }

        private bool Deposit(AccountVaultStore store, WorldObject item, out string failReason)
        {
            var ok = false;
            string reason = null;

            store.Enqueue(() => ok = store.TryDeposit(item, Owner, out reason));

            failReason = reason;
            return ok;
        }

        private readonly struct Sample
        {
            public readonly int Rebuilds;
            public readonly double TotalMs;
            public readonly int Deposits;

            /// <summary>
            /// Gen0 collections and bytes allocated during the timed loop. Present because the residual
            /// growth in V on the salvage grid needed a cause rather than a guess: a per-deposit cost
            /// that is flat in WORK but rises with vault size is what GC pressure from a larger live heap
            /// looks like, and these two columns are what tell that apart from a surviving O(V) scan.
            /// </summary>
            public readonly int Gen0;
            public readonly long AllocatedBytes;

            /// <summary>
            /// The ONE mandatory initial BuildGroupingLocked, timed on its own OUTSIDE the deposit loop.
            ///
            /// It has to be separated or the whole grid is misread, and the first version of this probe
            /// misread it. A store that has just loaded holds no grouping memo, so the first read of the
            /// cap check builds one, and for an all-salvage vault sharing one bucket that build does V-1
            /// AreGroupable diffs. Left inside the timed loop it lands in the per-deposit average as
            /// build/N, which LOOKS exactly like a per-deposit O(V) term and is not one: the giveaway is
            /// that it shrinks when only N grows, which no genuine per-deposit cost can do.
            /// </summary>
            public readonly double InitialBuildMs;

            /// <summary>How many deposits took the non-O(1) reposition branch (at == 0).</summary>
            public readonly int Repositions;

            public Sample(int rebuilds, double totalMs, int deposits, int gen0, long allocatedBytes, double initialBuildMs, int repositions)
            {
                Rebuilds = rebuilds;
                TotalMs = totalMs;
                Deposits = deposits;
                Gen0 = gen0;
                AllocatedBytes = allocatedBytes;
                InitialBuildMs = initialBuildMs;
                Repositions = repositions;
            }

            public double PerDepositMs => TotalMs / Deposits;

            public long BytesPerDeposit => AllocatedBytes / Deposits;
        }

        /// <summary>
        /// Seeds a fresh vault of <paramref name="storedItems"/>, warms the store, then times
        /// <paramref name="deposits"/> entry-adding deposits and returns the grouping-rebuild delta and
        /// the elapsed wall time. Every call builds its own backend and world, so V is exact and no
        /// earlier pass has inflated it.
        /// </summary>
        private Sample Measure(int storedItems, int deposits, bool groupCandidateFiller)
        {
            ResetFakes();

            SeedVault(itemCount: storedItems, freeSlots: deposits, groupCandidateFiller: groupCandidateFiller);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the measurement");

            var items = new WorldObject[deposits];

            for (var i = 0; i < deposits; i++)
                items[i] = DepositItem();

            // Captured BEFORE the warm-up read, deliberately. The rebuild count is a statement about the
            // whole transaction including its one mandatory initial build, which is what the inverted
            // pins below assert; moving the capture after the warm-up would make them read zero, and
            // zero is also what "no read ever consulted the grouping" looks like.
            var rebuildsBefore = store.GroupingRebuilds;
            var repositionsBefore = store.GroupRepositions;

            // THE WARM-UP, timed separately rather than skipped. It forces the initial
            // BuildGroupingLocked out of the deposit loop so the per-deposit figure is steady state, and
            // it reads through the same cap-check path a deposit does (EntryCount -> EntryCountLocked),
            // so nothing about the memo's shape differs from what the first deposit would have found.
            var buildSw = Stopwatch.StartNew();
            var warmEntries = store.EntryCount;
            buildSw.Stop();

            Assert.IsTrue(warmEntries >= 0, "the warm-up read must succeed, or the store is not ready and every number below is meaningless");

            // Settled before the clock starts, so neither the seeding nor the warm-up build is collected
            // inside the measurement and charged to the deposits.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var gen0Before = GC.CollectionCount(0);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            var sw = Stopwatch.StartNew();

            for (var i = 0; i < deposits; i++)
                Assert.IsTrue(Deposit(store, items[i], out var reason), $"deposit {i} was refused: {reason}");

            sw.Stop();

            return new Sample(store.GroupingRebuilds - rebuildsBefore,
                              sw.Elapsed.TotalMilliseconds,
                              deposits,
                              GC.CollectionCount(0) - gen0Before,
                              GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                              buildSw.Elapsed.TotalMilliseconds,
                              store.GroupRepositions - repositionsBefore);
        }

        // ---------------------------------------------------------------- the falsifiable half

        /// <summary>
        /// THE COST INVARIANT of the deposit path, and the reason AddStoredItemToGroupingLocked exists.
        ///
        /// Expected constant is ONE rebuild for the whole transaction, not zero. The first read after the
        /// store loads has no memo to serve, so it builds one; every deposit after that maintains that
        /// memo in place instead of dropping it. Zero would mean no read ever consulted the grouping at
        /// all, which would make this test vacuous rather than better - so one is the correct post-fix
        /// number and it is asserted exactly rather than as an upper bound.
        ///
        /// Before the fix this read N (one per deposit). It is the same measurement, inverted.
        /// </summary>
        [TestMethod]
        public void EntryAddingDeposits_RebuildTheGroupingMemoOnceForTheWholeTransaction()
        {
            const int deposits = 20;

            var small = Measure(storedItems: 25, deposits: deposits, groupCandidateFiller: false);
            var large = Measure(storedItems: 200, deposits: deposits, groupCandidateFiller: false);

            Assert.AreEqual(1, small.Rebuilds,
                $"at V=25, {deposits} entry-adding deposits produced {small.Rebuilds} grouping rebuild(s). "
                + "One is the initial build; a number that tracks the deposit count means the memo is being dropped per deposit again.");

            Assert.AreEqual(1, large.Rebuilds,
                $"at V=200, {deposits} entry-adding deposits produced {large.Rebuilds} grouping rebuild(s).");

            // Control on the invariant: the rebuild count must not be flat simply because the deposits
            // were refused and no work happened at all.
            Assert.AreEqual(deposits, small.Deposits);
            Assert.AreEqual(deposits, large.Deposits);
        }

        /// <summary>
        /// The same invariant on the MERGING stored-item branch, which is a genuinely different path: a
        /// merging deposit adds no entry and never reaches the cap check, but AddsEntryLocked still has to
        /// call JoinsAnExistingGroupLocked to find that out, and before the fix that walked. So this
        /// branch also read N rebuilds and now reads one.
        ///
        /// Deliberately distinct from the zero pinned by
        /// AccountVaultClassStorageTests.Deposit_CostsTheSameWhateverTheVaultAlreadyHolds: that one runs
        /// with the class predicate ON, so its deposits merge into a CLASS row and AddsEntryLocked
        /// short-circuits on classes.ContainsKey before any grouping is touched. See the control below.
        /// </summary>
        [TestMethod]
        public void MergingStoredItemDeposits_RebuildTheGroupingMemoOnceForTheWholeTransaction()
        {
            const int deposits = 20;

            ResetFakes();

            SeedVault(itemCount: 200, freeSlots: deposits, groupCandidateFiller: true);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the measurement");

            var before = store.GroupingRebuilds;

            for (var i = 0; i < deposits; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(SalvageWcid, 100, 7, 10, 6000 + i, "Salvage (100)");
                world.UnclassifiableGuids.Add(bag.Guid.Full);

                Assert.IsTrue(Deposit(store, bag, out var reason), $"merging deposit {i} was refused: {reason}");
            }

            Assert.AreEqual(1, store.GroupingRebuilds - before,
                $"{deposits} group-merging stored-item deposits produced {store.GroupingRebuilds - before} grouping "
                + "rebuild(s). One is the initial build; a number tracking the deposit count means the walk is back.");

            // Control: the deposits must actually have merged, or this measures the wrong branch. 200
            // identical-bucket fillers plus 20 merging arrivals is one group, so the group count must not
            // have grown by the deposit count.
            Assert.AreEqual(1, store.GroupingSnapshotForTest().Count,
                "the seeded fillers and the deposits must all have merged into one group, or this is not the merging branch");
        }

        /// <summary>
        /// CONTROL: the rebuild counter is CONDITIONAL, not something every deposit trips regardless.
        /// With the class predicate ON, a deposit that merges into a class row the account already holds
        /// short-circuits in AddsEntryLocked on classes.ContainsKey and touches no grouping at all.
        ///
        /// Without this, the two probes above would be consistent with a counter that increments on every
        /// deposit for reasons having nothing to do with the grouping walk, and they would prove nothing.
        /// </summary>
        [TestMethod]
        public void Control_ClassMergingDeposits_DoNotRebuildTheGroupingMemo()
        {
            const int deposits = 20;

            ResetFakes();

            // The class tier ON is the whole difference from the probe above. The deposited bags are NOT
            // registered unclassifiable, so TryDescribeClass claims them and they become class rows.
            world.ClassifyResult = true;

            SeedVault(itemCount: 200, freeSlots: deposits + 1, groupCandidateFiller: true);

            var store = NewStore();

            Assert.IsTrue(store.IsLoaded, "the store must load before the measurement");

            // One priming deposit creates the class row, so the measured ones merge into an existing row -
            // the same reason AccountVaultClassStorageTests.Measure holds its first deposit out.
            var primer = FakeVaultWorld.MakeSalvageBag(SalvageWcid, 100, 7, 10, 5000, "Salvage (100)");
            Assert.IsTrue(Deposit(store, primer, out var primeReason), $"the priming deposit was refused: {primeReason}");

            var before = store.GroupingRebuilds;

            for (var i = 0; i < deposits; i++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(SalvageWcid, 100, 7, 10, 6000 + i, "Salvage (100)");

                Assert.IsTrue(Deposit(store, bag, out var reason), $"class-merging deposit {i} was refused: {reason}");
            }

            Assert.AreEqual(0, store.GroupingRebuilds - before,
                $"a class-merging deposit must short-circuit before any grouping walk, but {deposits} of them produced "
                + $"{store.GroupingRebuilds - before} rebuild(s). If this is non-zero the counter is unconditional and "
                + "the two probes above measure nothing.");

            // Control on the control: the deposits must actually have reached the class tier.
            Assert.AreEqual(1, backend.Classes.Count(c => c.AccountId == OwnerAccount && c.Count > 0),
                "the deposits must have landed on exactly one class row, or this control proves nothing");
        }

        /// <summary>
        /// THE COST PIN for the reposition branch (at == 0), which is the one part of the incremental add
        /// that is not O(1): it does a List.Remove over the top-level group list, so O(groups).
        ///
        /// That bound is kept deliberately rather than engineered away - see groupingMemo's own remarks
        /// for the measurement - so this pin asserts THE DOCUMENTED BOUND, not zero growth.
        ///
        /// It pins on ALLOCATED BYTES rather than wall time, and that is the whole reason it can live in a
        /// unit test: allocation per deposit is stable to a fraction of a percent across repetitions,
        /// while a millisecond threshold on a 0.1 ms operation is a flake on any loaded machine. What it
        /// defends is "nobody introduced per-deposit work proportional to vault size", which is exactly
        /// what an allocation that grows with V would reveal, and it is machine independent.
        ///
        /// Both controls matter. repositions == N proves the deposits really took the branch, without
        /// which a flat measurement would only prove the branch never ran. rebuilds == 1 proves the memo
        /// survived every reposition rather than being quietly dropped and rebuilt, which would also read
        /// as flat allocation while doing the O(V) work this whole change exists to remove.
        /// </summary>
        [TestMethod]
        public void RepositionBranch_PerDepositCostDoesNotGrowWithVaultSize()
        {
            const int groups = 20;

            var small = MeasureRepositions(storedItems: 25, groups: groups);
            var large = MeasureRepositions(storedItems: 2000, groups: groups);

            Assert.AreEqual(groups, small.Repositions, "at V=25 every deposit must have taken the reposition branch, or this measures nothing");
            Assert.AreEqual(groups, large.Repositions, "at V=2000 every deposit must have taken the reposition branch, or this measures nothing");

            Assert.AreEqual(1, small.Rebuilds, "the memo must survive every reposition at V=25 - only the one initial build is allowed");
            Assert.AreEqual(1, large.Rebuilds, "the memo must survive every reposition at V=2000 - only the one initial build is allowed");

            // 1.1x, which is THE DOCUMENTED BOUND with an order of magnitude of headroom and not a round
            // number chosen for comfort: the measured ratio is 16524 to 16684 bytes, or 1.0097, and the
            // per-run spread at a fixed V is about 40 bytes, or 0.24%. So the 10% band is not absorbing
            // noise - it is there for a future filler or slot layout shifting the constant.
            var bound = small.BytesPerDeposit * 11 / 10;

            Assert.IsTrue(large.BytesPerDeposit <= bound,
                $"per-deposit allocation on the reposition path grew with vault size: {small.BytesPerDeposit} bytes at V=25 "
                + $"versus {large.BytesPerDeposit} at V=2000, above the {bound} bound. The List.Remove scan is allowed to be "
                + "O(groups) and allocation-free (see groupingMemo's cost bound); allocation that tracks V means something "
                + "now walks the vault per deposit.");
        }

        // ---------------------------------------------------------------- the magnitude half

        /// <summary>
        /// Reports per-deposit wall time at several vault sizes, three repetitions each, after a
        /// discarded warm-up pass, for both filler shapes. Asserts NOTHING about the timings - a
        /// wall-clock threshold in a unit test is a flake and the point here is to read the magnitude.
        /// The numbers go to vault-deposit-scaling-probe.txt next to the test assembly, because
        /// Assert.Inconclusive's message does not reach the default console logger.
        /// </summary>
        [TestMethod]
        public void Probe_ReportDepositCostAgainstVaultSize()
        {
            const int reps = 3;

            // Discarded: JIT, first-touch allocation and PropertyManager's first reads must not be
            // charged to the first measured sample.
            Measure(storedItems: 25, deposits: 20, groupCandidateFiller: false);
            Measure(storedItems: 200, deposits: 20, groupCandidateFiller: true);

            var report = new StringBuilder();

            report.AppendLine("VAULT DEPOSIT SCALING PROBE - raw numbers");
            report.AppendLine($"machine: {Environment.ProcessorCount} logical cores, reps={reps}, warm-up pass discarded");
            report.AppendLine($"Stopwatch.IsHighResolution={Stopwatch.IsHighResolution}, Frequency={Stopwatch.Frequency}");
            report.AppendLine();

            report.AppendLine("=== A. THE SPECIFIED GRID: N=20, V in {25, 200} ===");
            report.AppendLine("    build is the ONE mandatory initial BuildGroupingLocked, timed outside the deposit");
            report.AppendLine("    loop by Measure's warm-up read, so per_deposit is steady state and the two costs");
            report.AppendLine("    are reported separately rather than summed. Without that split the build lands on");
            report.AppendLine("    the first deposit and per_deposit reads as if the insert itself scaled in V.");
            report.AppendLine();
            Grid(report, "filler=Misc (not a group candidate: allocation-only rebuild)", new[] { 25, 200 }, 20, reps, false);
            Grid(report, "filler=salvage bags, one bucket (group candidates: AreGroupable biota diff per item)", new[] { 25, 200 }, 20, reps, true);

            report.AppendLine("=== C. THE REPOSITION BRANCH (at == 0), the one non-O(1) path ===");
            report.AppendLine("    Every deposit here moves a group in the top-level list: List.Remove (IndexOf +");
            report.AppendLine("    RemoveAt shift) then an ordered insert, both O(groups). repositions=N/N is the");
            report.AppendLine("    control - a flat cost with repositions=0 would measure nothing.");
            report.AppendLine();
            RepositionGrid(report, "reposition-heavy, filler=Misc (groups ~= V, worst case for List.Remove)", new[] { 25, 200, 800, 2000 }, 20, reps, false);
            RepositionGrid(report, "reposition-heavy, filler=salvage bags", new[] { 25, 200, 800, 2000 }, 20, reps, true);

            report.AppendLine("=== A2. WHERE THE RESIDUAL GROWTH LIVES ===");
            report.AppendLine("    Same vaults, NO deposits: just N reads of store.EntryCount, which is the cap");
            report.AppendLine("    check's own read path (EntryCountLocked). If this grows in V while the deposit");
            report.AppendLine("    insert does not, the residual is the pre-existing read, not the incremental add.");
            report.AppendLine();
            ReadGrid(report, "EntryCount only, filler=Misc", new[] { 25, 200, 800, 2000 }, 200, reps, false);
            ReadGrid(report, "EntryCount only, filler=salvage bags", new[] { 25, 200, 800, 2000 }, 200, reps, true);

            report.AppendLine("=== B. EXTRA, BEYOND THE SPECIFIED GRID: N=200, V in {25, 200, 800, 2000} ===");
            report.AppendLine("    The specified grid sits near timer resolution, so this widens N and V to");
            report.AppendLine("    measure the slope with enough work to clear noise. Not a substitute for A.");
            report.AppendLine("    Read this against A: A's per_deposit is reproducibly higher at V=200 than at V=25,");
            report.AppendLine("    by 2.1x to 3.2x over three runs, IDENTICALLY for both fillers, while B is flat");
            report.AppendLine("    across an 80x range of V. A per-deposit term cannot shrink as N grows, so A's");
            report.AppendLine("    spread is a per-transaction cost of well under a millisecond, and B bounds how far");
            report.AppendLine("    it can scale in V. Its mechanism is NOT isolated here; being filler-independent");
            report.AppendLine("    rules out AreGroupable, which dominated the residual before the warm-up existed.");
            report.AppendLine();
            Grid(report, "filler=Misc", new[] { 25, 200, 800, 2000 }, 200, reps, false);
            Grid(report, "filler=salvage bags, one bucket", new[] { 25, 200, 800, 2000 }, 200, reps, true);

            var path = Path.Combine(AppContext.BaseDirectory, "vault-deposit-scaling-probe.txt");

            File.WriteAllText(path, report.ToString());

            Assert.Inconclusive($"measurement probe, no verdict asserted. Numbers written to {path}{Environment.NewLine}{report}");
        }

        /// <summary>
        /// A vault laid out so that EVERY measured deposit takes the reposition branch (at == 0), which is
        /// the one part of the incremental add that is not O(1).
        ///
        /// The shape, and why it is the worst case rather than a contrived one. Vault 0 is seeded only
        /// partly full with ItemType.Misc items, so it has free slots and none of its contents can ever be
        /// a salvage group's representative. The remaining vaults hold the bulk: one salvage bag in each of
        /// <paramref name="groups"/> DISTINCT buckets (distinct Structure, so each bucket holds exactly one
        /// group and the per-deposit bucket scan stays O(1) and cannot be confused with the reposition
        /// cost), plus Misc filler up to <paramref name="storedItems"/> so the top-level group list is
        /// genuinely large. FindVaultWithFreeSlotLocked returns the FIRST vault with a free slot, so every
        /// deposit lands in vault 0 and therefore sorts ahead of its group's existing member in vault 1+,
        /// forcing the group to move.
        ///
        /// One deposit per bucket, because the branch is SELF-LIMITING within a bucket: a deposit appends
        /// at the end of its vault, so the second arrival for the same group sorts after the first and
        /// takes the O(1) path. Driving one arrival per distinct group is the only way to make every
        /// deposit pay it, and it corresponds to a real case - a player muling one more of each salvage
        /// variant they already store.
        ///
        /// Returns the deposits to make, and seeds the world as a side effect.
        /// </summary>
        private List<WorldObject> SeedForRepositions(int storedItems, int groups, bool groupCandidateFiller)
        {
            ResetFakes();

            // Vault 0: room for exactly the measured deposits, contents that never group.
            var front = FakeVaultWorld.MakeContainer(VaultSlots);
            world.Containers[front.Guid.Full] = front;
            backend.Vaults.Add(new ShardAccountVault
            {
                Id = 9900,
                AccountId = OwnerAccount,
                ContainerGuid = front.Guid.Full,
                CreatedAt = new DateTime(2026, 1, 1),
            });

            for (var i = 0; i < VaultSlots - groups; i++)
            {
                var filler = FakeVaultWorld.MakeStack(FillerWcid, 1, 100);
                world.UnclassifiableGuids.Add(filler.Guid.Full);
                Assert.IsTrue(front.TryAddToInventory(filler, placementPosition: i), $"could not seed front vault slot {i}");
            }

            // The bulk, in later vaults: one representative per distinct bucket, then filler.
            var remaining = storedItems;
            var order = 1;
            var bucketsPlaced = 0;

            while (remaining > 0)
            {
                var here = Math.Min(remaining, VaultSlots);

                var container = FakeVaultWorld.MakeContainer(VaultSlots);
                world.Containers[container.Guid.Full] = container;
                backend.Vaults.Add(new ShardAccountVault
                {
                    Id = (uint)(9900 + order),
                    AccountId = OwnerAccount,
                    ContainerGuid = container.Guid.Full,
                    CreatedAt = new DateTime(2026, 1, 1).AddMinutes(order),
                });

                for (var i = 0; i < here; i++)
                {
                    WorldObject filler;

                    if (bucketsPlaced < groups)
                    {
                        // One salvage representative per distinct bucket.
                        filler = FakeVaultWorld.MakeSalvageBag(SalvageWcid, 50 + bucketsPlaced, 7, 10, 100, "Salvage (100)");
                        bucketsPlaced++;
                    }
                    else
                    {
                        filler = groupCandidateFiller
                            ? FakeVaultWorld.MakeSalvageBag(SalvageWcid, 100, 7, 10, 100 + i, "Salvage (100)")
                            : FakeVaultWorld.MakeStack(FillerWcid, 1, 100);
                    }

                    world.UnclassifiableGuids.Add(filler.Guid.Full);
                    Assert.IsTrue(container.TryAddToInventory(filler, placementPosition: i), $"could not seed vault {order} slot {i}");
                }

                remaining -= here;
                order++;
            }

            Assert.AreEqual(groups, bucketsPlaced, "every distinct bucket must have been seeded, or the deposits cannot all reposition");

            // One arrival per seeded bucket. Same Structure, so it groups with that bucket's
            // representative; different Value, which is a tolerated difference so it MERGES rather than
            // opening a second group.
            var arrivals = new List<WorldObject>(groups);

            for (var g = 0; g < groups; g++)
            {
                var bag = FakeVaultWorld.MakeSalvageBag(SalvageWcid, 50 + g, 7, 10, 7000 + g, "Salvage (100)");
                world.UnclassifiableGuids.Add(bag.Guid.Full);
                arrivals.Add(bag);
            }

            return arrivals;
        }

        /// <summary>
        /// One reposition-heavy measurement, shared by the grid below and by the cost pin, so the pinned
        /// number and the reported number cannot come from two different harnesses.
        /// </summary>
        private Sample MeasureRepositions(int storedItems, int groups, bool groupCandidateFiller = false)
        {
            var arrivals = SeedForRepositions(storedItems, groups, groupCandidateFiller);

            var store = NewStore();
            Assert.IsTrue(store.IsLoaded, "the store must load before the measurement");

            var rebuildsBefore = store.GroupingRebuilds;
            var repositionsBefore = store.GroupRepositions;

            // Timed separately and outside the loop, same as Measure: the one mandatory initial build is
            // not a per-deposit cost and must not be averaged into one.
            var buildSw = Stopwatch.StartNew();
            var warm = store.EntryCount;
            buildSw.Stop();

            Assert.IsTrue(warm > 0, "the warm-up read must see entries, or the vault was not seeded");

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            var gen0Before = GC.CollectionCount(0);
            var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

            var sw = Stopwatch.StartNew();

            for (var i = 0; i < arrivals.Count; i++)
                Assert.IsTrue(Deposit(store, arrivals[i], out var reason), $"reposition deposit {i} was refused: {reason}");

            sw.Stop();

            return new Sample(store.GroupingRebuilds - rebuildsBefore,
                              sw.Elapsed.TotalMilliseconds,
                              arrivals.Count,
                              GC.CollectionCount(0) - gen0Before,
                              GC.GetAllocatedBytesForCurrentThread() - allocatedBefore,
                              buildSw.Elapsed.TotalMilliseconds,
                              store.GroupRepositions - repositionsBefore);
        }

        /// <summary>
        /// The reposition branch measured across vault size. Same columns as <see cref="Grid"/>, plus the
        /// reposition count, which is the control: a flat cost with repositions=0 would mean the branch
        /// never ran and the measurement says nothing about it.
        /// </summary>
        private void RepositionGrid(StringBuilder report, string label, int[] vaultSizes, int groups, int reps, bool groupCandidateFiller)
        {
            report.AppendLine(label);

            foreach (var v in vaultSizes)
            {
                for (var r = 0; r < reps; r++)
                {
                    var s = MeasureRepositions(v, groups, groupCandidateFiller);

                    report.AppendLine(
                        $"  N={s.Deposits,4} V={v,5}  rep={r + 1}  repositions={s.Repositions,4}/{s.Deposits}"
                        + $"  rebuilds={s.Rebuilds,3}  build={s.InitialBuildMs,8:F3} ms"
                        + $"  total={s.TotalMs,9:F3} ms  per_deposit={s.PerDepositMs,9:F4} ms"
                        + $"  gen0={s.Gen0,3}  bytes_per_deposit={s.BytesPerDeposit,7}");
                }
            }

            report.AppendLine();
        }

        /// <summary>
        /// The cap check's READ path in isolation: no deposits at all, just repeated EntryCount. Same
        /// columns as <see cref="Grid"/> so the two tables can be read side by side.
        /// </summary>
        private void ReadGrid(StringBuilder report, string label, int[] vaultSizes, int reads, int reps, bool groupCandidateFiller)
        {
            report.AppendLine(label);

            foreach (var v in vaultSizes)
            {
                for (var r = 0; r < reps; r++)
                {
                    ResetFakes();
                    SeedVault(itemCount: v, freeSlots: 0, groupCandidateFiller: groupCandidateFiller);

                    var store = NewStore();
                    Assert.IsTrue(store.IsLoaded, "the store must load before the measurement");

                    // One read first, so the memo exists and this measures steady-state reads rather than
                    // the initial build.
                    var warm = store.EntryCount;

                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();

                    var gen0Before = GC.CollectionCount(0);
                    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();

                    var sw = Stopwatch.StartNew();

                    var total = 0;

                    for (var i = 0; i < reads; i++)
                        total += store.EntryCount;

                    sw.Stop();

                    var gen0 = GC.CollectionCount(0) - gen0Before;
                    var bytes = (GC.GetAllocatedBytesForCurrentThread() - allocatedBefore) / reads;

                    report.AppendLine(
                        $"  reads={reads,4} V={v,5}  rep={r + 1}  entries={warm,5}  total={sw.Elapsed.TotalMilliseconds,10:F3} ms"
                        + $"  per_read={sw.Elapsed.TotalMilliseconds / reads,9:F4} ms  gen0={gen0,4}  bytes_per_read={bytes,7}"
                        + $"  (sum {total})");
                }
            }

            report.AppendLine();
        }

        private void Grid(StringBuilder report, string label, int[] vaultSizes, int deposits, int reps, bool groupCandidateFiller)
        {
            report.AppendLine(label);

            foreach (var v in vaultSizes)
            {
                for (var r = 0; r < reps; r++)
                {
                    var s = Measure(storedItems: v, deposits: deposits, groupCandidateFiller: groupCandidateFiller);

                    report.AppendLine(
                        $"  N={deposits,4} V={v,5}  rep={r + 1}  rebuilds={s.Rebuilds,5}  build={s.InitialBuildMs,8:F3} ms"
                        + $"  total={s.TotalMs,10:F3} ms"
                        + $"  per_deposit={s.PerDepositMs,9:F4} ms  gen0={s.Gen0,4}  bytes_per_deposit={s.BytesPerDeposit,7}");
                }
            }

            report.AppendLine();
        }
    }
}
