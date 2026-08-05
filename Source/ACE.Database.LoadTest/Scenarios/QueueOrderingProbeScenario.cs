using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using ACE.Database;

namespace ACE.Database.LoadTest.Scenarios
{
    /// <summary>
    /// SerializedShardDatabase drains one FIFO queue on one worker thread. It batches consecutive SaveBiota
    /// entries, and consecutive RemoveBiota entries, into single round trips - but a batch may only ever absorb
    /// CONSECUTIVE entries of the SAME kind. The first entry of a different kind terminates the drain, is stored
    /// in heldOver, and runs immediately afterward via RunHeldOver, which deliberately does not start a drain of
    /// its own. This is why selling an item to a vendor is safe: Player_Inventory.DeepSave enqueues SaveBiota(X)
    /// (the item's ContainerId is dirtied by the removal from inventory) and then RemoveBiota(X) microseconds
    /// later, and FIFO order guarantees the save always lands before the delete. If that ever reordered, the
    /// delete would no-op against a row that does not exist yet, the save would then insert it, and the sold item
    /// would resurrect in the database while the player keeps the payment - an item duplication bug.
    /// The invariant currently holds by construction, but nothing tests it. This scenario proves it holds, in
    /// both directions, under a real backlog rather than a synthetic unit test of the drain loop itself.
    ///
    /// Id layout (all offsets from SyntheticBiotaFactory.TestGuidRangeStart), chosen to stay clear of every other
    /// scenario's reserved range (login-under-delete-load reserves up to about +0x11000, integrity-check uses
    /// +0x3000 and +0x3100):
    ///   +0x20000 .. +0x20000+pairs-1   PHASE 1 ids (save-then-remove)
    ///   +0x30000 .. +0x30000+pairs-1   PHASE 2 ids (remove-then-save), a distinct sub-block so the two phases
    ///                                  can never collide even if a phase is interrupted mid-run.
    ///   +0xD0000 .. +0xD0000+pairs-1   PHASE 3 ids (one batched save, then N removes). Well clear of
    ///                                  save-batch-crossover's reserved +0x40000..+0xC0000 window.
    /// </summary>
    public class QueueOrderingProbeScenario : IScenario
    {
        public string Name => "queue-ordering-probe";

        public string Description =>
            "Verifies the shard queue preserves FIFO order across batch boundaries: save-then-remove must leave nothing, remove-then-save must leave the row. Args: --pairs=2000";

        public void Run(ScenarioArgs args)
        {
            var pairs = args.GetInt("pairs", 2000);
            var propertyGroups = args.GetInt("propertyGroups", 3);

            if (pairs > 0x10000)
                throw new ArgumentOutOfRangeException(nameof(pairs), "pairs must be <= 0x10000 to respect the reserved id layout.");

            var phase1BaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x20000;
            var phase2BaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0x30000;
            var phase3BaseId = SyntheticBiotaFactory.TestGuidRangeStart + 0xD0000;

            var phase1Passed = RunPhase1SaveThenRemove(phase1BaseId, pairs, propertyGroups);

            Console.WriteLine();

            var phase2Passed = RunPhase2RemoveThenSave(phase2BaseId, pairs, propertyGroups);

            Console.WriteLine();

            var phase3Passed = RunPhase3BatchedSaveThenRemoves(phase3BaseId, pairs, propertyGroups);

            // Three phases now, each checking `pairs` ids for survivors, so the gate rests on pairs * 3 observations.
            var overallPassed = phase1Passed && phase2Passed && phase3Passed;
            Metrics.Record("Ordering.Passed", overallPassed ? 1 : 0, MetricDirection.HigherIsBetter, pairs * 3);

            Console.WriteLine();
            Console.WriteLine(overallPassed
                ? "PASS: FIFO ordering held across batch boundaries in both directions."
                : "FAIL: FIFO ordering was violated - see phase output above.");
        }

        /// <summary>
        /// PHASE 1: for each of `pairs` distinct fresh ids, enqueue SaveBiota(X) then RemoveBiota(X). Expected:
        /// zero survivors. A survivor means the remove ran ahead of the save, no-opped, and the save then
        /// inserted a row that should not exist - exactly the duplication bug this scenario guards against.
        /// </summary>
        private static bool RunPhase1SaveThenRemove(uint baseId, int pairs, int propertyGroups)
        {
            Console.WriteLine("PHASE 1: save-then-remove must leave nothing.");
            Console.WriteLine($"Enqueuing {pairs} save+remove pairs for ids 0x{baseId:X8}-0x{baseId + (uint)Math.Max(pairs - 1, 0):X8}...");

            var ids = new List<uint>(pairs);
            for (var i = 0; i < pairs; i++)
                ids.Add(baseId + (uint)i);

            // CRITICAL: fire every pair's save AND remove from this single producer thread, back to back, with NO
            // wait between a pair's own callbacks and NO wait between pairs. Waiting per pair would mean the queue
            // never holds more than one entry, so the drain would never have anything to batch and the batch
            // boundary this scenario exists to exercise (a real S R S R S R ... backlog forcing heldOver on
            // essentially every iteration) would never be reached. A single producer thread also keeps enqueue
            // order deterministic, so any failure reproduces instead of being a heisenbug. Do NOT "tidy" this into
            // a per-pair wait - that would make the test pass regardless of whether the invariant holds.
            var remaining = new CountdownEvent(pairs * 2);

            foreach (var id in ids)
            {
                var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                var rwLock = new ReaderWriterLockSlim();

                DatabaseManager.Shard.SaveBiota(biota, rwLock, _ => remaining.Signal());
                DatabaseManager.Shard.RemoveBiota(id, _ => remaining.Signal());
            }

            remaining.Wait();

            List<uint> survivors;
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
                survivors = context.Biota.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList();

            if (survivors.Count == 0)
            {
                Console.WriteLine($"PASS: 0 of {pairs} save-then-remove pairs left a row behind.");
            }
            else
            {
                Console.WriteLine(
                    $"FAIL: {survivors.Count} of {pairs} save-then-remove pairs left a row behind " +
                    "(ordering inverted - the remove ran before the save, so it no-opped and the save then inserted).");
                Console.WriteLine("First surviving ids:");
                foreach (var id in survivors.Take(10))
                    Console.WriteLine($"  0x{id:X8}");

                // Cleanup for the failure case - phase 1 expects nothing left, so anything found is a leftover.
                var cleanupDone = new CountdownEvent(1);
                DatabaseManager.Shard.RemoveBiotasInParallel(survivors, _ => cleanupDone.Signal(), null);
                cleanupDone.Wait();
            }

            return survivors.Count == 0;
        }

        /// <summary>
        /// PHASE 2: the bidirectional counterpart. For each of `pairs` distinct fresh ids (a different block than
        /// phase 1), enqueue RemoveBiota(Y) FIRST - a no-op against a row that does not exist yet - then
        /// SaveBiota(Y). Expected: every row exists afterward. A missing row means the save was pulled ahead of
        /// the remove, catching a drain that reorders in the opposite direction from phase 1.
        /// </summary>
        private static bool RunPhase2RemoveThenSave(uint baseId, int pairs, int propertyGroups)
        {
            Console.WriteLine("PHASE 2: remove-then-save must leave the row.");
            Console.WriteLine($"Enqueuing {pairs} remove+save pairs for ids 0x{baseId:X8}-0x{baseId + (uint)Math.Max(pairs - 1, 0):X8}...");

            var ids = new List<uint>(pairs);
            for (var i = 0; i < pairs; i++)
                ids.Add(baseId + (uint)i);

            // Same no-waiting rule as phase 1 (see the comment there) - every pair's remove and save are enqueued
            // back to back from this single producer thread, with the wait deferred until every pair has been
            // enqueued, so the backlog reaches the drain's batch boundary rather than draining one entry at a time.
            var remaining = new CountdownEvent(pairs * 2);

            foreach (var id in ids)
            {
                DatabaseManager.Shard.RemoveBiota(id, _ => remaining.Signal());

                var biota = SyntheticBiotaFactory.Create(id, propertyGroups);
                var rwLock = new ReaderWriterLockSlim();
                DatabaseManager.Shard.SaveBiota(biota, rwLock, _ => remaining.Signal());
            }

            remaining.Wait();

            List<uint> present;
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
                present = context.Biota.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList();

            var missing = ids.Except(present).ToList();

            if (missing.Count == 0)
            {
                Console.WriteLine($"PASS: all {pairs} remove-then-save pairs left their row behind.");
            }
            else
            {
                Console.WriteLine(
                    $"FAIL: {missing.Count} of {pairs} remove-then-save pairs are missing their row " +
                    "(ordering inverted - the save ran before the remove, so the remove deleted it).");
                Console.WriteLine("First missing ids:");
                foreach (var id in missing.Take(10))
                    Console.WriteLine($"  0x{id:X8}");
            }

            // Cleanup - phase 2's whole point is that every row deliberately survives on success, so every
            // present row needs to be removed here, not just the failures.
            if (present.Count > 0)
            {
                var cleanupDone = new CountdownEvent(1);
                DatabaseManager.Shard.RemoveBiotasInParallel(present, _ => cleanupDone.Signal(), null);
                cleanupDone.Wait();
            }

            return missing.Count == 0;
        }

        /// <summary>
        /// PHASE 3: the shape the batched sell path actually produces. Player_Commerce.HandleActionSellItem now
        /// collects every sold item's off-player save and issues ONE SaveBiotasInParallel for all of them, then
        /// calls Vendor.ProcessItemsForPurchase, which fires one RemoveBiota per item. So the queue sees a single
        /// plain-Task entry followed by N batchable RemoveBiota entries - a different mix from phases 1 and 2,
        /// which only ever cross SaveBiota against RemoveBiota.
        ///
        /// This matters because a plain Task and a RemoveBiotaQueueItem are different queue-entry KINDS: the
        /// worker's remove drain must treat the Task as a non-batchable entry it may not pull ahead of. Expected:
        /// zero survivors, exactly as phase 1. A survivor means a remove ran before the batched save, no-opped,
        /// and the save then inserted a row for an item the player has already been paid for.
        /// </summary>
        private static bool RunPhase3BatchedSaveThenRemoves(uint baseId, int pairs, int propertyGroups)
        {
            Console.WriteLine("PHASE 3: one batched save, then N removes, must leave nothing.");
            Console.WriteLine($"Enqueuing 1 batched save of {pairs} items then {pairs} removes for ids 0x{baseId:X8}-0x{baseId + (uint)Math.Max(pairs - 1, 0):X8}...");

            var ids = new List<uint>(pairs);
            var items = new List<(ACE.Entity.Models.Biota biota, ReaderWriterLockSlim rwLock)>(pairs);

            for (var i = 0; i < pairs; i++)
            {
                var id = baseId + (uint)i;
                ids.Add(id);
                items.Add((SyntheticBiotaFactory.Create(id, propertyGroups), new ReaderWriterLockSlim()));
            }

            // Same no-waiting rule as the other phases: the batched save and every remove are enqueued back to
            // back from this one producer thread, so the removes really do sit behind the save in a backlog.
            var remaining = new CountdownEvent(pairs + 1);

            DatabaseManager.Shard.SaveBiotasInParallel(items, _ => remaining.Signal());

            foreach (var id in ids)
                DatabaseManager.Shard.RemoveBiota(id, _ => remaining.Signal());

            remaining.Wait();

            List<uint> survivors;
            using (var context = new ACE.Database.Models.Shard.ShardDbContext())
                survivors = context.Biota.Where(r => ids.Contains(r.Id)).Select(r => r.Id).ToList();

            if (survivors.Count == 0)
            {
                Console.WriteLine($"PASS: 0 of {pairs} items survived a batched save followed by their removes.");
            }
            else
            {
                Console.WriteLine(
                    $"FAIL: {survivors.Count} of {pairs} items survived " +
                    "(ordering inverted - a remove ran before the batched save, so it no-opped and the save then inserted).");
                Console.WriteLine("First surviving ids:");
                foreach (var id in survivors.Take(10))
                    Console.WriteLine($"  0x{id:X8}");

                var cleanupDone = new CountdownEvent(1);
                DatabaseManager.Shard.RemoveBiotasInParallel(survivors, _ => cleanupDone.Signal(), null);
                cleanupDone.Wait();
            }

            return survivors.Count == 0;
        }
    }
}
