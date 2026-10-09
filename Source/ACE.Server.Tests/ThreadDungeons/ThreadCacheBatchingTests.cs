using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The batching contract for pooled-loot delivery: a step spends at most its roll budget, a chain of steps
    /// terminates, an Ended run abandons without stranding a claimed entry, and the solo filler - the one producer
    /// that had no bound at all - is bounded now.
    ///
    /// The chain itself needs a live Landblock action queue, which this harness has no way to build, so the
    /// self-re-enqueue and its continuation rule are pinned on source text the way the rest of this suite pins the
    /// live wiring. Everything the budget actually decides is exercised behaviourally.
    ///
    /// No PropertyManager key is read by any path driven here: the batch size reaches the delivery code as
    /// ThreadDungeonRun.CacheRollBatchSize, stamped by ThreadDungeonManager.TryStart on the world thread.
    /// </summary>
    [TestClass]
    public class ThreadCacheBatchingTests
    {
        private static string PlacerSrc() => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCachePlacer.cs");

        private static ThreadLootLedgerEntry Entry(int rolls = 1, WorldObject rare = null)
            => new ThreadLootLedgerEntry(false, false, null, null, rare, rare == null ? null : "Finder", rolls, 0);

        /// <summary>A pooled run holding <paramref name="entries"/> single-roll ledger entries and no bonus.</summary>
        private static ThreadDungeonRun RunWithEntries(int entries, int rollsEach = 1)
        {
            var run = PooledRun();

            for (var i = 0; i < entries; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry(rollsEach)));

            return run;
        }

        /// <summary>One item per roll, so an item count is a roll count.</summary>
        private static Func<ThreadLootLedgerEntry, List<WorldObject>> OnePerRoll(List<WorldObject> created)
            => e =>
            {
                var items = new List<WorldObject>();

                for (var i = 0; i < e.Rolls; i++)
                {
                    var item = Item();
                    created.Add(item);
                    items.Add(item);
                }

                return items;
            };

        private static readonly Func<ThreadDungeonRun, List<WorldObject>> NoBonus = _ => new List<WorldObject>();

        // ------------------------------------------------------------------------------------------------------
        // The budget itself
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_budget_spends_down_to_exhausted_and_never_reports_a_negative_remainder()
        {
            var budget = new ThreadLootRollBudget(5);

            Assert.AreEqual(5, budget.Remaining);
            Assert.IsFalse(budget.Exhausted);

            budget.Spend(3);
            Assert.AreEqual(2, budget.Remaining);
            Assert.AreEqual(3, budget.Spent);
            Assert.IsFalse(budget.Exhausted);

            budget.Spend(9);
            Assert.AreEqual(0, budget.Remaining, "a step that overspends reports no remainder, never a negative one");
            Assert.IsTrue(budget.Exhausted);
        }

        [TestMethod]
        public void A_budget_below_one_still_admits_one_roll_so_a_step_always_makes_progress()
        {
            var run = RunWithEntries(2);
            var budget = new ThreadLootRollBudget(0);

            Assert.IsTrue(budget.TryClaimNext(run, out _), "the first claim of a step is always admitted");
            Assert.IsTrue(budget.Exhausted);
            Assert.IsFalse(budget.TryClaimNext(run, out _));
            Assert.AreEqual(1, run.LedgerCount);
        }

        [TestMethod]
        public void The_first_claim_of_a_step_splits_a_fat_entry_at_the_roll_cap_so_it_can_neither_wedge_the_ledger_nor_blow_the_step()
        {
            // A single entry carrying more rolls than a whole batch. #1210 admitted it WHOLE on a step's first claim so
            // it could not wedge the head of the ledger - which also meant one step could materialise up to
            // MaxRollsPerKill rolls. It is now split: the step takes exactly its cap, and the rest waits at the head.
            var run = PooledRun();
            var fat = Entry(rolls: 40);
            Assert.IsTrue(run.TryAppendLootEntry(fat));
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: 1)));

            var budget = new ThreadLootRollBudget(12);

            Assert.IsTrue(budget.TryClaimNext(run, out var first));
            Assert.AreEqual(12, first.Rolls, "a slice of exactly the cap");
            Assert.AreNotSame(fat, first, "a slice, not the ledger's own entry");
            Assert.AreEqual(12, budget.Spent);
            Assert.IsTrue(budget.Exhausted);

            Assert.IsFalse(budget.TryClaimNext(run, out _), "and the step stops there");
            Assert.AreEqual(2, run.LedgerCount, "the fat entry is still at the head, holding its other 28 rolls, in kill order");
            Assert.AreEqual(28, fat.RollsRemaining);
        }

        [TestMethod]
        public void An_unlimited_budget_is_a_fresh_one_every_time_and_never_exhausts()
        {
            var a = ThreadLootRollBudget.Unlimited();
            a.Spend(1_000_000);

            Assert.IsFalse(a.Exhausted);
            Assert.AreEqual(0, ThreadLootRollBudget.Unlimited().Spent, "Unlimited() hands back a new budget, not a shared one");
        }

        // ------------------------------------------------------------------------------------------------------
        // The solo filler, which had no bound at all before this
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_solo_fill_stops_at_the_step_budget_and_leaves_the_rest_in_kill_order()
        {
            var run = RunWithEntries(20);
            var created = new List<WorldObject>();
            var cache = Cache();

            var result = ThreadCacheFiller.Fill(run, cache, OnePerRoll(created), NoBonus, new ThreadLootRollBudget(12));

            Assert.AreEqual(12, result.EntriesClaimed, "one roll each, so twelve entries is the whole allowance");
            Assert.AreEqual(12, created.Count, "and nothing beyond the allowance was materialised");
            Assert.AreEqual(8, run.LedgerCount, "the rest is still pooled for the next step");
            Assert.AreEqual(12, cache.Inventory.Count);
        }

        [TestMethod]
        public void Repeated_budgeted_fills_deliver_the_whole_ledger_and_then_stop()
        {
            var run = RunWithEntries(20);
            var created = new List<WorldObject>();
            var cache = Cache();
            var materialize = OnePerRoll(created);

            var steps = 0;
            var claimed = 0;

            // The chain, by hand: keep stepping while the step ran out of budget with loot still pooled. That is
            // exactly ThreadCachePlacer's continuation rule, and it is what must terminate.
            while (steps < 100)
            {
                var budget = new ThreadLootRollBudget(12);
                claimed += ThreadCacheFiller.Fill(run, cache, materialize, NoBonus, budget).EntriesClaimed;
                steps++;

                if (!budget.Exhausted || !run.HasUnclaimedLoot)
                    break;
            }

            Assert.AreEqual(2, steps, "20 entries at 12 rolls a step is two steps");
            Assert.AreEqual(20, claimed);
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsFalse(run.HasUnclaimedLoot, "the chain terminates because there is nothing left, not because it gave up");
            Assert.AreEqual(20, cache.Inventory.Count);
        }

        [TestMethod]
        public void An_unbudgeted_fill_is_still_the_old_whole_pool_behaviour()
        {
            var run = RunWithEntries(20);
            var created = new List<WorldObject>();

            var result = ThreadCacheFiller.Fill(run, Cache(), OnePerRoll(created), NoBonus);

            Assert.AreEqual(20, result.EntriesClaimed, "the unbudgeted overload is unchanged, for callers outside the chain");
            Assert.AreEqual(0, run.LedgerCount);
        }

        [TestMethod]
        public void A_fill_charges_the_boss_bonus_its_item_count_against_the_step()
        {
            var run = RunWithEntries(20);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var created = new List<WorldObject>();
            var budget = new ThreadLootRollBudget(12);

            var result = ThreadCacheFiller.Fill(run, Cache(), OnePerRoll(created),
                _ => Enumerable.Range(0, 10).Select(_ => Item()).ToList(), budget);

            Assert.IsTrue(result.BonusClaimed);
            Assert.AreEqual(2, result.EntriesClaimed, "the bonus's ten items left room for two more rolls this step");
            Assert.IsTrue(budget.Exhausted);
            Assert.AreEqual(18, run.LedgerCount);
        }

        [TestMethod]
        public void A_fill_reports_how_long_it_took()
        {
            var result = ThreadCacheFiller.Fill(RunWithEntries(3), Cache(), OnePerRoll(new List<WorldObject>()), NoBonus);

            Assert.IsTrue(result.ElapsedMs >= 0, "the cache line's ms field is always populated, even for a fast fill");
        }

        // ------------------------------------------------------------------------------------------------------
        // The form-and-fill loop
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void An_exhausted_step_forms_no_further_cache_to_leave_empty()
        {
            // Without the budget clause on the loop's hasMore, the second iteration would stand up a fresh chest,
            // fill it with nothing (the budget is gone), destroy it again and log a "cache accepted nothing"
            // warning about a failure that never happened.
            var run = RunWithEntries(20);
            var budget = new ThreadLootRollBudget(12);
            var created = new List<WorldObject>();
            var formed = new List<Container>();

            var pass = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass,
                () => Enumerable.Range(0, 1).Select(i => new CacheCandidate(CacheCandidateStage.Ring, 0x01500100, i, 0f, 0f, 0f, 1f, true)).ToList(),
                _ =>
                {
                    var cache = Cache();
                    Assert.IsTrue(run.TryRegisterCache(cache));
                    return cache;
                },
                (r, c) => ThreadCacheFiller.Fill(r, c, OnePerRoll(created), NoBonus, budget),
                (cache, result) => formed.Add(cache),
                budget: budget);

            Assert.AreEqual(1, pass.CachesFormed);
            Assert.IsFalse(pass.FreshCacheAcceptedNothing, "no second cache was formed only to be destroyed");
            Assert.IsFalse(pass.NoRoom);
            Assert.AreEqual(1, formed.Count);
            Assert.AreEqual(12, formed[0].Inventory.Count);
            Assert.AreEqual(8, run.LedgerCount, "the rest waits for the chain's next step");
        }

        // ------------------------------------------------------------------------------------------------------
        // The group deal
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_group_deal_claims_no_more_than_the_step_budget()
        {
            var run = GroupRunWithEntries(20);
            var created = new List<WorldObject>();
            var budget = new ThreadLootRollBudget(12);

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, OnePerRoll(created), (r, b) => new List<WorldObject>(), rollBudget: budget));

            Assert.AreEqual(12, created.Count, "the deal materialised only this step's allowance");
            Assert.AreEqual(8, run.LedgerCount);
            Assert.IsTrue(budget.Exhausted);
            Assert.IsTrue(run.IsPlacementWanted, "undealt loot is handed to the Tick retry on every exit");
        }

        [TestMethod]
        public void A_group_boss_bonus_charges_the_per_seat_half_per_seat_and_the_pooled_build_once()
        {
            // The #1208 split, as the step budget sees it. Two seats, an empty ledger, the bonus pending:
            //   per-seat half: 3 items built for EACH of 2 seats -> 6 rolls charged;
            //   pooled half:   5 items built ONCE for the deal   -> 5 rolls charged, never 10.
            // A merge that billed the pooled build per seat would read 16; one that dropped the pooled charge, 6.
            var run = GroupRunWithEntries(0);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var perSeatBuilds = 0;
            var pooledBuilds = 0;
            var budget = new ThreadLootRollBudget(1000);

            ThreadGroupCacheDelivery.Deal(run, _ => throw new AssertFailedException("no ledger entry"),
                (r, b) => { perSeatBuilds++; return Enumerable.Range(0, 3).Select(_ => Item()).ToList(); },
                (r, factor, cap) => { pooledBuilds++; return Enumerable.Range(0, 5).Select(_ => Item()).ToList(); },
                budget);

            Assert.AreEqual(2, perSeatBuilds, "the per-seat half is built once per deal seat");
            Assert.AreEqual(1, pooledBuilds, "the pooled half is built once for the whole deal");
            Assert.AreEqual(2 * 3 + 5, budget.Spent, "per-seat charged per seat, pooled charged once");
        }

        [TestMethod]
        public void An_unbudgeted_group_deal_still_stops_at_the_old_per_deal_caps()
        {
            var run = GroupRunWithEntries(ThreadGroupCacheDelivery.MaxEntriesPerDeal + 10);
            var created = new List<WorldObject>();

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, OnePerRoll(created), (r, b) => new List<WorldObject>()));

            Assert.AreEqual(ThreadGroupCacheDelivery.MaxEntriesPerDeal, created.Count, "the step budget is a tighter bound, never a looser one");
            Assert.AreEqual(10, run.LedgerCount);
        }

        [TestMethod]
        public void An_ended_run_deals_nothing_and_strands_no_ledger_entry()
        {
            // The chain's per-step abandon: ThreadGroupCacheDelivery.Run and ThreadCachePlacer.Execute both re-test
            // the state at the top of every step, so a run that ends mid-spread claims nothing further. Invariant 1
            // matters here because a claim removes the entry BEFORE its items exist and nothing puts it back.
            var run = GroupRunWithEntries(5);
            Assert.IsTrue(run.MarkEnded("test"));

            var world = new ThreadGroupCacheDelivery.DeliveryWorld
            {
                IsInside = _ => throw new AssertFailedException("an ended run serves nobody"),
                FormAt = (g, h) => throw new AssertFailedException("an ended run forms no cache"),
                MoveFor = _ => throw new AssertFailedException("an ended run moves nothing"),
                Tell = (g, t) => throw new AssertFailedException("an ended run tells nobody"),
                Deliver = (c, r) => throw new AssertFailedException("an ended run delivers nothing"),
                Materialize = _ => throw new AssertFailedException("an ended run materialises nothing"),
                BuildBonus = (r, b) => throw new AssertFailedException("an ended run builds no bonus"),
            };

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, 0x50000010u, world, new ThreadLootRollBudget(12));

            Assert.AreEqual(5, run.LedgerCount, "every entry is still on the ledger, to be destroyed by the run-end drain");
        }

        private static ThreadDungeonRun GroupRunWithEntries(int entries)
        {
            var seats = new List<RosterSeat>
            {
                new RosterSeat(0x50000010u, "Owner", 7, 150),
                new RosterSeat(0x50000020u, "Member", 8, 150),
            };

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };

            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.MarkPooledLoot(true);
            run.SetMemberKey(0x50000020u, 0x80001020u);
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: the run must be Cleared");

            for (var i = 0; i < entries; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            return run;
        }

        // ------------------------------------------------------------------------------------------------------
        // Time-budgeted steps (2026-09-18). Every clock here is injected, so nothing depends on wall time.
        // ------------------------------------------------------------------------------------------------------

        /// <summary>A step clock a test drives by hand: materialisers advance it, the budget reads it.</summary>
        private sealed class FakeClock
        {
            public double Ms;
            public double Read() => Ms;
        }

        /// <summary><see cref="OnePerRoll"/>, advancing <paramref name="clock"/> by <paramref name="msPerRoll"/> for every roll it materialises.</summary>
        private static Func<ThreadLootLedgerEntry, List<WorldObject>> OnePerRollTimed(List<WorldObject> created, Func<FakeClock> clock, double msPerRoll,
            List<ThreadLootLedgerEntry> seen = null)
            => e =>
            {
                seen?.Add(e);
                var items = OnePerRoll(created)(e);
                clock().Ms += msPerRoll * e.Rolls;
                return items;
            };

        [TestMethod]
        public void A_timed_step_stops_after_the_roll_during_which_the_clock_crosses_the_budget()
        {
            var run = RunWithEntries(20);
            var clock = new FakeClock();
            var created = new List<WorldObject>();
            var budget = new ThreadLootRollBudget(12, clock.Read, 8);

            var result = ThreadCacheFiller.Fill(run, Cache(), OnePerRollTimed(created, () => clock, 3), NoBonus, budget);

            // 3 ms, 6 ms, 9 ms: the third roll is the one that crosses 8 ms, and the step stops right after it.
            Assert.AreEqual(3, result.EntriesClaimed);
            Assert.AreEqual(3, created.Count);
            Assert.IsTrue(budget.TimeUp && budget.Exhausted);
            Assert.AreEqual(17, run.LedgerCount, "the rest waits for the next step, in kill order");
        }

        [TestMethod]
        public void A_timed_step_always_materialises_at_least_one_roll_even_when_it_starts_over_budget()
        {
            var run = RunWithEntries(5);
            var clock = new FakeClock { Ms = 500 };
            var created = new List<WorldObject>();

            var result = ThreadCacheFiller.Fill(run, Cache(), OnePerRollTimed(created, () => clock, 1), NoBonus, new ThreadLootRollBudget(12, clock.Read, 8));

            Assert.AreEqual(1, result.EntriesClaimed, "progress is guaranteed: one roll, then the step yields");
            Assert.AreEqual(4, run.LedgerCount);
        }

        [TestMethod]
        public void The_control_setting_a_very_high_time_budget_reproduces_the_roll_cap_steps_of_1210()
        {
            // dynamic_dungeons_cache_step_budget_ms at its 250 ms ceiling, against rolls that cost 1 ms each: time never
            // decides, so the roll cap alone does - 12 a step, exactly the #1210 chain the untimed test above pins.
            var run = RunWithEntries(20);
            var created = new List<WorldObject>();
            var cache = Cache();
            var perStep = new List<int>();

            for (var steps = 0; steps < 100; steps++)
            {
                var clock = new FakeClock();
                var budget = new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.MaxStepBudgetMs);
                perStep.Add(ThreadCacheFiller.Fill(run, cache, OnePerRollTimed(created, () => clock, 1), NoBonus, budget).EntriesClaimed);

                if (!budget.Exhausted || !run.HasUnclaimedLoot)
                    break;
            }

            CollectionAssert.AreEqual(new[] { 12, 8 }, perStep);
            Assert.AreEqual(20, cache.Inventory.Count, "every roll delivered, none twice");
        }

        [TestMethod]
        public void A_single_100_roll_entry_spans_multiple_timed_steps_and_conserves_every_item()
        {
            // The fat-entry hole #1210 left open: one group kill can carry ThreadLootPool.MaxRollsPerKill rolls, and a
            // step used to materialise it whole. Timed at 1 ms a roll against an 8 ms budget, it now takes 8 a step.
            var run = PooledRun();
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: ThreadLootPool.MaxRollsPerKill, rare: rare)));

            var created = new List<WorldObject>();
            var claims = new List<ThreadLootLedgerEntry>();
            var cache = Cache(capacity: 120);
            var clock = new FakeClock();
            var materialize = OnePerRollTimed(created, () => clock, 1, claims);
            var perStep = new List<int>();
            var rareStep = -1;

            for (var step = 0; step < 100; step++)
            {
                clock = new FakeClock();
                var budget = new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs);
                var before = created.Count;
                var result = ThreadCacheFiller.Fill(run, cache, materialize, NoBonus, budget);

                perStep.Add(created.Count - before);

                if (result.RaresLanded.Count > 0)
                    rareStep = step;

                if (!budget.Exhausted || !run.HasUnclaimedLoot)
                    break;
            }

            Assert.AreEqual(13, perStep.Count, "100 rolls at 8 a step is 13 steps");
            Assert.IsTrue(perStep.All(n => n <= ThreadLootRollBudget.DefaultStepBudgetMs), "no step materialised more than its time allowed");
            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill, created.Count, "every roll materialised exactly once");
            Assert.IsTrue(claims.All(c => c.Rolls == 1), "a timed budget claims one roll at a time");
            Assert.AreEqual(1, claims.Count(c => c.HeldRare != null), "the rare rides only the final slice");
            Assert.AreSame(rare, claims.Last().HeldRare);
            Assert.AreEqual(perStep.Count - 1, rareStep, "and so lands in the last step, after every roll, as on a corpse");

            Assert.AreEqual(ThreadLootPool.MaxRollsPerKill + 1, cache.Inventory.Count, "item conservation: every roll and the rare, each in the cache once");
            Assert.IsTrue(created.All(i => cache.Inventory.ContainsKey(i.Guid)) && cache.Inventory.ContainsKey(rare.Guid));
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_part_claimed_entry_keeps_its_rare_on_the_ledger_and_the_run_end_drain_destroys_it()
        {
            var run = PooledRun();
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: 40, rare: rare)));

            Assert.IsTrue(new ThreadLootRollBudget(12).TryClaimNext(run, out var slice));
            Assert.IsNull(slice.HeldRare, "a non-final slice carries no rare");
            Assert.AreEqual(1, run.LedgerCount);

            Assert.IsTrue(run.MarkEnded("test"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(1, drain.LedgerEntries);
            CollectionAssert.Contains(drain.HeldRares, rare);
            Assert.IsTrue(rare.IsDestroyed, "invariant 5: the rare of a half-delivered entry is not stranded");
        }

        [TestMethod]
        public void A_whole_untouched_entry_is_handed_out_as_itself_and_a_final_slice_carries_the_rare_and_recipient()
        {
            var run = PooledRun();
            var whole = Entry(rolls: 3);
            var rare = Item();
            var fat = new ThreadLootLedgerEntry(false, false, null, null, rare, "Finder", 10, 0x50000077u);
            Assert.IsTrue(run.TryAppendLootEntry(whole));
            Assert.IsTrue(run.TryAppendLootEntry(fat));

            Assert.IsTrue(run.TryClaimLootRolls(5, allowPartial: false, out var first));
            Assert.AreSame(whole, first, "a claim that takes an untouched entry whole hands out the ledger's own object");

            Assert.IsFalse(run.TryClaimLootRolls(4, allowPartial: false, out _), "no split without allowPartial");
            Assert.IsTrue(run.TryClaimLootRolls(4, allowPartial: true, out var a));
            Assert.IsTrue(run.TryClaimLootRolls(4, allowPartial: true, out var b));
            Assert.IsTrue(run.TryClaimLootRolls(4, allowPartial: true, out var last));

            Assert.AreEqual(4, a.Rolls);
            Assert.AreEqual(4, b.Rolls);
            Assert.AreEqual(2, last.Rolls, "the remainder");
            Assert.IsNull(a.HeldRare);
            Assert.IsNull(b.HeldRare);
            Assert.AreEqual(0u, a.RareRecipientGuid);
            Assert.AreSame(rare, last.HeldRare);
            Assert.AreEqual("Finder", last.HeldRareFinderName);
            Assert.AreEqual(0x50000077u, last.RareRecipientGuid);
            Assert.AreEqual(0, run.LedgerCount);
        }

        [TestMethod]
        public void A_fat_group_entry_is_dealt_across_timed_steps_and_every_item_has_exactly_one_holder()
        {
            var run = GroupRunWithEntries(0);
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(new ThreadLootLedgerEntry(false, false, null, null, rare, "Finder", ThreadLootPool.MaxRollsPerKill, 0x50000010u)));

            var created = new List<WorldObject>();
            var clock = new FakeClock();
            var materialize = OnePerRollTimed(created, () => clock, 1);
            var steps = 0;

            while (run.HasUndealtLoot && steps < 100)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
                steps++;
            }

            Assert.AreEqual(13, steps, "8 rolls a step");

            var piled = HeldItems(run, 0x50000010u).Concat(HeldItems(run, 0x50000020u)).ToList();
            Assert.AreEqual(created.Count + 1, piled.Count, "every roll plus the rare is held");
            Assert.AreEqual(piled.Count, piled.Distinct().Count(), "and each by exactly one member");
            Assert.IsTrue(created.All(piled.Contains) && piled.Contains(rare));
            Assert.IsTrue(piled.All(i => !i.IsDestroyed));
        }

        [TestMethod]
        public void The_pooled_boss_half_is_claimed_once_counted_once_and_built_across_steps_with_every_item_held_once()
        {
            var run = GroupRunWithEntries(0);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var perSeatBuilds = 0;
            var counts = 0;
            var pooledCalls = new List<int>();
            var clock = new FakeClock();
            var built = new List<WorldObject>();

            Func<ThreadDungeonRun, double, List<WorldObject>> perSeat = (r, b) =>
            {
                perSeatBuilds++;
                var items = new List<WorldObject> { Item(), Item() };
                built.AddRange(items);
                return items;
            };

            Func<ThreadDungeonRun, double, int, int> count = (r, factor, cap) => { counts++; return 30; };

            Func<ThreadDungeonRun, double, int, List<WorldObject>> pooled = (r, factor, cap) =>
            {
                pooledCalls.Add(cap);
                clock.Ms += cap;
                var items = Enumerable.Range(0, cap).Select(_ => Item()).ToList();
                built.AddRange(items);
                return items;
            };

            var steps = 0;

            while (run.HasUndealtLoot && steps < 100)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, _ => throw new AssertFailedException("no ledger entry"), perSeat, pooled,
                    new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs), count);
                steps++;
            }

            Assert.AreEqual(2, perSeatBuilds, "TryClaimLootBonus fired once: the per-seat half was built once per seat, in one deal");
            Assert.AreEqual(1, counts, "the pooled half was counted once");
            Assert.AreEqual(30, pooledCalls.Sum(), "and exactly its count was built");
            Assert.IsTrue(pooledCalls.All(n => n == 1), "one roll per claim under a timed budget");
            Assert.IsTrue(steps >= 4, $"built across steps, not atomically (took {steps})");
            Assert.AreEqual(0, run.PooledBonusRollsPending);
            Assert.IsFalse(run.IsLootBonusPending);

            var piled = HeldItems(run, 0x50000010u).Concat(HeldItems(run, 0x50000020u)).ToList();
            Assert.AreEqual(built.Count, piled.Count, "item conservation: 2 seats x 2 per-seat items + 30 pooled, each held");
            Assert.AreEqual(piled.Count, piled.Distinct().Count(), "by exactly one member");
            Assert.AreEqual(2 * 2 + 30, piled.Count);
        }

        [TestMethod]
        public void Pooled_rolls_never_built_are_dropped_at_run_end_and_leave_nothing_unclaimed()
        {
            var run = GroupRunWithEntries(0);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var clock = new FakeClock();

            ThreadGroupCacheDelivery.Deal(run, _ => new List<WorldObject>(), (r, b) => new List<WorldObject>(),
                (r, factor, cap) => { clock.Ms += cap; return Enumerable.Range(0, cap).Select(_ => Item()).ToList(); },
                new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs),
                (r, factor, cap) => 30);

            Assert.AreEqual(22, run.PooledBonusRollsPending, "8 built this step, 22 still owed");
            Assert.IsTrue(run.HasUnclaimedLoot && run.IsPlacementWanted, "owed rolls keep the chain and the Tick retry alive");

            Assert.IsTrue(run.MarkEnded("test"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(22, drain.PooledBonusRolls, "reported, not destroyed: they were never objects");
            Assert.AreEqual(0, run.PooledBonusRollsPending);
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.IsFalse(run.TrySetPooledBonusRolls(5, 2.0), "an ended run accepts no more");
        }

        [TestMethod]
        public void A_member_who_logged_out_mid_clear_keeps_their_pile_and_is_flagged_for_delivery()
        {
            var run = GroupRunWithEntries(6);
            var clock = new FakeClock();
            var created = new List<WorldObject>();

            var world = new ThreadGroupCacheDelivery.DeliveryWorld
            {
                IsInside = _ => false, // both members logged out (or walked out) mid-clear
                FormAt = (g, h) => throw new AssertFailedException("nobody is inside to form a cache at"),
                MoveFor = _ => { },
                Tell = (g, t) => { },
                Deliver = (c, r) => { },
                Materialize = OnePerRollTimed(created, () => clock, 1),
                BuildBonus = (r, b) => new List<WorldObject>(),
            };

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, 0x50000010u, world,
                new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));

            Assert.AreEqual(6, created.Count);
            Assert.IsTrue(run.HasHeldPile(0x50000020u), "the pile is kept for them");
            Assert.IsTrue(run.TryClaimMemberDeliveryWanted(0x50000020u), "and they are flagged, so their arrival delivers it (ruling R20)");
            Assert.IsTrue(created.All(i => !i.IsDestroyed));
        }

        [TestMethod]
        public void Re_stamping_the_token_each_step_keeps_the_stale_takeover_from_firing_mid_chain_so_spawncache_stays_Busy()
        {
            var run = PooledRun();
            var t0 = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

            var token = run.TryBeginCachePlacement(t0);
            Assert.AreNotEqual(0L, token);

            // A chain that has been stepping for 40 s, re-stamped at 25 s by its latest step.
            run.RefreshCachePlacement(token, t0.AddSeconds(25));
            Assert.AreEqual(0L, run.TryBeginCachePlacement(t0.AddSeconds(40)), "a /spawncache 40 s in is Busy, not a takeover");
            Assert.IsTrue(run.IsCachePlacementInProgress(t0.AddSeconds(40)));
            Assert.IsTrue(run.IsCachePlacementCurrent(token));

            // Control: without a re-stamp the same request 31 s in WOULD take over.
            var other = PooledRun();
            var stale = other.TryBeginCachePlacement(t0);
            Assert.AreNotEqual(0L, other.TryBeginCachePlacement(t0.AddSeconds(31)));
            Assert.IsFalse(other.IsCachePlacementCurrent(stale));
        }

        // ------------------------------------------------------------------------------------------------------
        // The deal buffer (code review of #1212, finding 2) and the run-end report (finding 1)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>A pooled, Cleared group run of <paramref name="seatCount"/> keyed members (every one a deal seat) and no ledger.</summary>
        private static ThreadDungeonRun KeyedGroupRun(int seatCount)
        {
            var seats = Enumerable.Range(0, seatCount).Select(i => new RosterSeat(0x50000010u + (uint)i * 0x10u, $"P{i}", 7, 150)).ToList();
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };

            var group = GroupScaling.Compute(seatCount, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
            run.MarkPooledLoot(true);

            foreach (var seat in seats.Skip(1))
                run.SetMemberKey(seat.Guid, 0x80001000u + (seat.Guid & 0xFFFu));

            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: the run must be Cleared");
            Assert.AreEqual(seatCount, run.DealSeats().Count, "fixture: every member holds a deal seat");
            return run;
        }

        private static List<WorldObject> AllHeld(ThreadDungeonRun run)
            => run.DealSeats().SelectMany(g => HeldItems(run, g)).ToList();

        [TestMethod]
        public void A_timed_group_deal_buffers_its_small_steps_and_hands_out_one_set_at_the_floor()
        {
            // 3 ms a roll against 8 ms: 3 rolls a step. The floor is the roll cap (12), so steps 1-3 only buffer and
            // step 4 hands out one 12-roll set - the size #1210 dealt.
            var run = GroupRunWithEntries(30);
            var created = new List<WorldObject>();
            var clock = new FakeClock();
            var materialize = OnePerRollTimed(created, () => clock, 3);
            var buffered = new List<int>();
            var held = new List<int>();

            Assert.AreEqual(ThreadLootRollBudget.DefaultRollsPerStep, ThreadGroupCacheDelivery.DealFloor(new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, 8)));

            for (var step = 0; step < 4; step++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
                buffered.Add(run.DealBufferCount);
                held.Add(AllHeld(run).Count);
            }

            CollectionAssert.AreEqual(new[] { 3, 6, 9, 0 }, buffered);
            CollectionAssert.AreEqual(new[] { 0, 0, 0, 12 }, held);
            Assert.IsTrue(run.IsPlacementWanted, "buffered loot keeps the Tick retry alive");

            // The rest: 18 more rolls, the last set handed out when the ledger empties (below the floor).
            for (var steps = 0; run.HasUndealtLoot && steps < 100; steps++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
            }

            var piled = AllHeld(run);
            Assert.AreEqual(30, piled.Count);
            Assert.AreEqual(30, piled.Distinct().Count(), "every item held once");
            Assert.IsTrue(created.All(piled.Contains));
            Assert.AreEqual(0, run.DealBufferCount);
        }

        /// <summary>
        /// One simulated group clear: <paramref name="values"/> single-roll ledger entries (kill order) dealt over
        /// <paramref name="seatCount"/> seats, one Deal per step, each step with the budget <paramref name="budgetFor"/>
        /// makes (null: the unbounded deal). Returns each seat's share of the total value, after checking conservation.
        /// </summary>
        private static double[] SimulateGroupClear(IReadOnlyList<int> values, int seatCount, Func<FakeClock, ThreadLootRollBudget> budgetFor, double msPerRoll, out int deals)
        {
            var run = KeyedGroupRun(seatCount);

            foreach (var _ in values)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var created = new List<WorldObject>();
            var next = 0;

            Func<ThreadLootLedgerEntry, List<WorldObject>> materialize = e =>
            {
                var items = new List<WorldObject>();

                for (var i = 0; i < e.Rolls; i++)
                {
                    var item = Item();
                    item.Value = values[next++];
                    created.Add(item);
                    items.Add(item);
                }

                clock.Ms += msPerRoll * e.Rolls;
                return items;
            };

            deals = 0;

            while (run.HasUndealtLoot && deals < 5000)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(), rollBudget: budgetFor?.Invoke(clock));
                deals++;
            }

            Assert.IsFalse(run.HasUndealtLoot, "the clear finished");

            var perSeat = run.DealSeats().Select(g => HeldItems(run, g)).ToList();
            var all = perSeat.SelectMany(h => h).ToList();
            Assert.AreEqual(values.Count, all.Count, "item conservation");
            Assert.AreEqual(all.Count, all.Distinct().Count(), "each item held by exactly one seat");

            var total = (double)values.Sum();
            return perSeat.Select(h => h.Sum(i => (long)(i.Value ?? 0)) / total).ToArray();
        }

        // ------------------------------------------------------------------------------------------------------
        // Who picks first (user ruling on #1212, 2026-09-18): each dealt set is a fresh snake whose first pick goes
        // to seat (runOffset + setIndex) mod seats, runOffset drawn once per run.
        // ------------------------------------------------------------------------------------------------------

        /// <summary>A set of <paramref name="size"/> items with distinct values, the first one the most valuable.</summary>
        private static List<(WorldObject Item, string RareFinderName)> ValuedSet(int size)
            => Enumerable.Range(0, size).Select(i => { var item = Item(); item.Value = 1000 - i; return (item, (string)null); }).ToList();

        /// <summary>Hands one set out through the production hand-out and returns the deal-seat index that received its top item.</summary>
        private static int FirstPickSeat(ThreadDungeonRun run, int size = 12)
        {
            var seats = run.DealSeats();
            var set = ValuedSet(size);
            var top = set[0].Item;
            var topSeat = -1;

            ThreadGroupCacheDelivery.HandOut(run, seats, set, 1, (guid, held) =>
            {
                if (ReferenceEquals(held.Item, top))
                    topSeat = seats.ToList().IndexOf(guid);
                return true;
            });

            Assert.IsTrue(topSeat >= 0, "the top item was handed out");
            return topSeat;
        }

        [TestMethod]
        public void A_fixed_run_offset_rotates_the_first_pick_one_seat_per_set_and_each_set_is_a_fresh_snake()
        {
            var run = KeyedGroupRun(6);
            run.SetSnakeRunOffset(3);

            CollectionAssert.AreEqual(new[] { 3, 4, 5, 0, 1, 2, 3, 4 }, Enumerable.Range(0, 8).Select(_ => FirstPickSeat(run)).ToList());

            // One whole set on a fresh run, first pick at seat 3: forward lap 3,4,5,0,1,2 then back 2,1,0,5,4,3.
            // HandOut walks hand by hand, so collect each seat's values and check where each rank landed.
            var again = KeyedGroupRun(6);
            again.SetSnakeRunOffset(3);
            var seats = again.DealSeats().ToList();
            var bySeat = new Dictionary<int, List<long>>();

            ThreadGroupCacheDelivery.HandOut(again, seats, ValuedSet(12), 1, (guid, held) =>
            {
                var seat = seats.IndexOf(guid);
                if (!bySeat.ContainsKey(seat)) bySeat[seat] = new List<long>();
                bySeat[seat].Add(held.Item.Value ?? 0);
                return true;
            });

            // Value rank r (0 = top) goes to snake position r: seats 3,4,5,0,1,2,2,1,0,5,4,3.
            var expected = new[] { 3, 4, 5, 0, 1, 2, 2, 1, 0, 5, 4, 3 };
            for (var rank = 0; rank < 12; rank++)
                CollectionAssert.Contains(bySeat[expected[rank]], (long)(1000 - rank), $"rank {rank} goes to seat {expected[rank]}");
        }

        [TestMethod]
        public void Over_600_sets_each_seat_takes_the_first_pick_within_one_set_of_its_fair_share_at_2_to_6_seats()
        {
            // TOLERANCE, from the rule: first picks cycle through the seats exactly, so over S sets every seat takes
            // floor(S / n) or ceil(S / n) of them, and |frequency - 1/n| <= 1/S. S = 600, so 1/600.
            const int sets = 600;
            const double tolerance = 1.0 / sets;
            var offsets = new Random(20260918);

            for (var n = 2; n <= 6; n++)
            {
                var run = KeyedGroupRun(n);
                run.SetSnakeRunOffset(offsets.Next(0, ThreadDungeonRun.SnakeRunOffsetRange));

                var firstPicks = new int[n];

                for (var set = 0; set < sets; set++)
                    firstPicks[FirstPickSeat(run)]++;

                Console.WriteLine($"{n} seats: first picks {string.Join(" ", firstPicks)}");

                foreach (var count in firstPicks)
                    Assert.IsTrue(Math.Abs((double)count / sets - 1.0 / n) <= tolerance + 1e-12, $"{n} seats: {count}/{sets} first picks, fair share {1.0 / n:F4}");
            }
        }

        [TestMethod]
        public void Across_6000_short_clears_the_owner_is_first_pick_no_more_than_a_fair_share_at_2_to_6_seats()
        {
            // A short clear is ONE set, so its first pick is runOffset mod n: the owner (seat 0) is first exactly when
            // the draw lands on 0 mod n, probability 1/n with the lcm-sized range. Before the ruling it was always the owner.
            // TOLERANCE, from the binomial: over R runs the owner's frequency has sd sqrt(p (1 - p) / R) with p = 1/n;
            // the bound is p + 4.5 sd (one-sided P about 3.4e-6 per seat count). The source is seeded, so the test is
            // deterministic; the bound is what makes the seed irrelevant.
            const int runs = 6000;
            const double z = 4.5;
            var saved = ThreadDungeonRun.SnakeRunOffsetSource;

            try
            {
                var rng = new Random(7);
                ThreadDungeonRun.SnakeRunOffsetSource = () => rng.Next(0, ThreadDungeonRun.SnakeRunOffsetRange);

                for (var n = 2; n <= 6; n++)
                {
                    var ownerFirst = 0;

                    for (var r = 0; r < runs; r++)
                    {
                        if (FirstPickSeat(KeyedGroupRun(n), 2 * n) == 0)
                            ownerFirst++;
                    }

                    var p = 1.0 / n;
                    var bound = p + z * Math.Sqrt(p * (1 - p) / runs);
                    var freq = (double)ownerFirst / runs;

                    Console.WriteLine($"{n} seats: owner first in {ownerFirst}/{runs} = {freq:F4} (fair {p:F4}, bound {bound:F4})");
                    Assert.IsTrue(freq <= bound, $"{n} seats: owner first {freq:F4} > {bound:F4}");
                }
            }
            finally
            {
                ThreadDungeonRun.SnakeRunOffsetSource = saved;
            }
        }

        [TestMethod]
        public void A_long_heavy_tailed_six_seat_clear_keeps_the_seat_value_spread_inside_the_statistical_bound()
        {
            // Value mix: U{1..999}, and 5% of items multiplied by 50 (heavy-tailed, as real loot is). Before the ruling a
            // 6-seat clear of this mix handed seat 0 about 0.67 of all value, because every 12-item set started at seat 0.
            //
            // TOLERANCE, from the distribution, not the output. Model a seat's total as N/n independent draws of X
            // (random assignment). The snake is at least as balanced: it gives every seat exactly N/n items, and order
            // statistics of one set are positively correlated, so Var(seat A - seat B) within a set is at most the
            // independent-draw value. Then sd(share) = (sigma/mu) / sqrt(N n), a pair's share difference has sd
            // sqrt(2) sd(share), and bounding every one of the C(6,2) = 15 pairs at 4.5 of those sds (union bound,
            // P < 15 x 6.8e-6, about 1e-4) bounds max - min share.
            const int seatsN = 6;
            const int items = 12000;
            const double z = 4.5;

            var m1 = 500.0;                         // E[U], U uniform on 1..999
            var m2 = 1000.0 * 1999.0 / 6.0;         // E[U^2] = (999 + 1)(2 x 999 + 1) / 6
            var mu = 0.95 * m1 + 0.05 * 50 * m1;
            var ex2 = 0.95 * m2 + 0.05 * 2500 * m2;
            var cv = Math.Sqrt(ex2 - mu * mu) / mu; // about 3.62
            var tolerance = z * Math.Sqrt(2) * cv / Math.Sqrt((double)items * seatsN);

            var values = new Random(20260918);
            var mix = Enumerable.Range(0, items).Select(_ => { var v = values.Next(1, 1000); return values.NextDouble() < 0.05 ? v * 50 : v; }).ToList();
            var saved = ThreadDungeonRun.SnakeRunOffsetSource;

            try
            {
                var offsets = new Random(11);
                ThreadDungeonRun.SnakeRunOffsetSource = () => offsets.Next(0, ThreadDungeonRun.SnakeRunOffsetRange);

                // The production shape: 8 ms budget, 3 ms a roll, buffered to 12-roll sets.
                var shares = SimulateGroupClear(mix, seatsN,
                    c => new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, c.Read, ThreadLootRollBudget.DefaultStepBudgetMs), 3, out var deals);

                var spread = shares.Max() - shares.Min();
                Console.WriteLine($"{items} items, {deals} deals: shares {string.Join(" ", shares.Select(s => s.ToString("F4")))} spread={spread:F4} bound={tolerance:F4}");

                Assert.IsTrue(spread <= tolerance, $"max - min seat share {spread:F4} exceeds {tolerance:F4}");
                Assert.IsTrue(tolerance < 0.1, "the bound is tight enough to catch the pre-ruling bias (seat 0 near 0.67)");
            }
            finally
            {
                ThreadDungeonRun.SnakeRunOffsetSource = saved;
            }
        }
        [TestMethod]
        public void Buffered_items_are_destroyed_and_reported_at_run_end_and_the_report_sees_pooled_rolls_after_the_latch_was_claimed()
        {
            // Finding 1: the bonus latch was claimed (BonusPending is false), 8 pooled rolls were built and sit in the
            // pooled buffer, 22 were never built. The old gate (ledger/bonus/overflow/rares) was silent here.
            var run = GroupRunWithEntries(3);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var clock = new FakeClock();
            var built = new List<WorldObject>();

            ThreadGroupCacheDelivery.Deal(run, OnePerRollTimed(new List<WorldObject>(), () => clock, 1), (r, b) => new List<WorldObject>(),
                (r, factor, cap) => { clock.Ms += cap; var items = Enumerable.Range(0, cap).Select(_ => Item()).ToList(); built.AddRange(items); return items; },
                new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs),
                (r, factor, cap) => 30);

            Assert.AreEqual(25, run.PooledBonusRollsPending, "3 ledger rolls then 5 pooled rolls fit in 8 ms");
            Assert.AreEqual(5, built.Count);
            Assert.AreEqual(5, run.DealBufferCount, "the ledger set went out when the ledger emptied; the pooled half waits to be complete");
            Assert.IsTrue(built.All(i => !AllHeld(run).Contains(i)), "no pooled item was dealt before the half was built");
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "buffered loot is owed loot");

            Assert.IsTrue(run.MarkEnded("test"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(5, drain.Buffered.Count);
            Assert.IsTrue(built.All(i => i.IsDestroyed), "the buffered items are destroyed, not stranded");
            Assert.AreEqual(25, drain.PooledBonusRolls);
            Assert.IsFalse(drain.BonusPending, "the latch was claimed");
            Assert.IsTrue(drain.LedgerEntries == 0 && drain.Overflow.Count == 0 && drain.HeldRares.Count == 0, "so the pre-fix gate saw nothing");
            Assert.IsFalse(run.HasUnclaimedLoot);

            var line = ThreadDungeonManager.UnclaimedPooledLootLine(run, drain);
            Assert.IsNotNull(line, "the gate fires on pooled rolls and buffered items alone");
            StringAssert.Contains(line, "pooledRolls=25");
            StringAssert.Contains(line, "buffered=5");
            StringAssert.Contains(line, "bonus=False");

            var summary = ThreadDungeonManager.RunSummaryLine(run, drain);
            StringAssert.Contains(summary, "pooledLeft=25", "read from the drain, not the zeroed run");
            StringAssert.Contains(summary, "buffered=5");

            // Each clause of the gate on its own, and the quiet case.
            var ended = GroupRunWithEntries(0);
            Assert.IsTrue(ended.MarkEnded("test"));
            var empty = ThreadLootPool.DisposeForRunEnd(ended);
            Assert.IsNull(ThreadDungeonManager.UnclaimedPooledLootLine(ended, empty), "nothing left, no line");
            Assert.IsNotNull(ThreadDungeonManager.UnclaimedPooledLootLine(ended, new LootPoolDrain(0, false, 0) { PooledBonusRolls = 1 }));
            var onlyBuffered = new LootPoolDrain(0, false, 0);
            onlyBuffered.Buffered.Add(Item());
            Assert.IsNotNull(ThreadDungeonManager.UnclaimedPooledLootLine(ended, onlyBuffered));
            Assert.IsNull(ThreadDungeonManager.UnclaimedPooledLootLine(ended, null));
        }

        [TestMethod]
        public void A_chain_cut_at_the_400_step_cap_leaves_its_partial_buffer_owed_and_the_next_chain_deals_it()
        {
            // 10 ms a roll against 8 ms: one roll a step, so a 405-roll ledger outlasts ThreadCachePlacer.MaxChainSteps.
            // 400 rolls = 33 full 12-roll sets + 4 buffered when the cap cuts the chain.
            var entries = ThreadCachePlacer.MaxChainSteps + 5;
            var run = GroupRunWithEntries(entries);
            var created = new List<WorldObject>();
            var clock = new FakeClock();
            var materialize = OnePerRollTimed(created, () => clock, 10);

            for (var step = 0; step < ThreadCachePlacer.MaxChainSteps; step++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
            }

            Assert.AreEqual(ThreadCachePlacer.MaxChainSteps, created.Count);
            Assert.AreEqual(5, run.LedgerCount);
            Assert.AreEqual(4, run.DealBufferCount, "the cut leaves a partial set in the buffer");
            Assert.AreEqual(33 * 12, AllHeld(run).Count);
            Assert.IsTrue(run.IsPlacementWanted && run.HasUndealtLoot && ThreadRunPresence.GroupLootOwed(run), "and it is still owed and retried");
            Assert.IsTrue(created.All(i => !i.IsDestroyed));

            // The Tick's retry starts a fresh chain: 5 more steps empty the ledger, and that deal hands out the last 9.
            for (var steps = 0; run.HasUndealtLoot && steps < 100; steps++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
            }

            var piled = AllHeld(run);
            Assert.AreEqual(entries, piled.Count);
            Assert.AreEqual(entries, piled.Distinct().Count(), "never double-dealt");
            Assert.IsTrue(created.All(piled.Contains));
            Assert.AreEqual(0, run.DealBufferCount);
            Assert.IsFalse(run.HasUndealtLoot);
        }

        [TestMethod]
        public void An_ended_run_refuses_the_buffer_and_destroys_what_the_deal_built()
        {
            var run = GroupRunWithEntries(0);
            var items = new List<(WorldObject Item, string RareFinderName)> { (Item(), null), (Item(), null) };
            Assert.IsTrue(run.MarkEnded("test"));

            Assert.IsFalse(run.TryBufferForDeal(items, 2, pooled: false), "invariant 5: nothing is accepted once Ended");
            Assert.AreEqual(0, ThreadGroupCacheDelivery.BufferAndFlush(run, run.DealSeats(), items, new List<(WorldObject Item, string RareFinderName)>(), 2, false, 12, 1));
            Assert.IsTrue(items.All(i => i.Item.IsDestroyed));
            Assert.AreEqual(0, run.DealBufferCount);
        }
        private static List<WorldObject> HeldItems(ThreadDungeonRun run, uint guid)
        {
            var taken = run.TakeHeldPile(guid);
            foreach (var held in taken)
                Assert.IsTrue(run.AddToHeldPile(guid, held), "put the pile back");
            return taken.Select(h => h.Item).ToList();
        }

        // ------------------------------------------------------------------------------------------------------
        // The chain, pinned on source (it needs a live landblock action queue to run)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_delivery_chain_re_enqueues_itself_and_holds_one_token_across_the_whole_chain()
        {
            var request = PooledLootSourceText.MethodBody(PlacerSrc(),
                "public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid)");

            var begin = request.IndexOf("run.TryBeginCachePlacement(DateTime.UtcNow)", StringComparison.Ordinal);
            var current = request.IndexOf("run.IsCachePlacementCurrent(token)", StringComparison.Ordinal);
            var refresh = request.IndexOf("run.RefreshCachePlacement(token, DateTime.UtcNow)", StringComparison.Ordinal);
            // 2026-09-18: the step's budget is TIMED off its own Stopwatch, with the roll batch size as the hard cap.
            var budget = request.IndexOf("new ThreadLootRollBudget(run.CacheRollBatchSize, () => watch.Elapsed.TotalMilliseconds, run.CacheStepBudgetMs)", StringComparison.Ordinal);
            var step = request.IndexOf("Execute(run, landblock, stepMode, requesterGuid, budget);", StringComparison.Ordinal);
            var more = request.IndexOf("more = budget.Exhausted && run.HasUnclaimedLoot;", StringComparison.Ordinal);
            var requeue = request.IndexOf("landblock.EnqueueAction(new ActionEventDelegate(DeliverBatch));", StringComparison.Ordinal);
            var end = request.IndexOf("run.EndCachePlacement(token);", StringComparison.Ordinal);

            Assert.IsTrue(begin >= 0 && current > begin && refresh > current && budget > refresh && step > budget && more > step,
                "one token, re-stamped each step, then a fresh budget, then the step, then the continuation test");
            Assert.IsTrue(requeue > more && end > requeue, "a step that continues re-enqueues itself; only a terminal step releases the token");

            // The token is taken ONCE, outside the step, and the step body is what repeats. If the latch were taken
            // per step, a second request could interleave between two steps and break the ledger's kill order.
            Assert.AreEqual(1, request.Split(new[] { "TryBeginCachePlacement" }, StringSplitOptions.None).Length - 1);
            Assert.AreEqual(1, request.Split(new[] { "run.EndCachePlacement(token);" }, StringSplitOptions.None).Length - 1);
            StringAssert.Contains(request, "if (!requeued)", "the release is skipped exactly when the chain carries on");
            StringAssert.Contains(request, "finally", "and happens on every other exit, a throw included");

            // The chain cannot outlive the run, cannot run forever, and cannot silently drop what it did not finish.
            StringAssert.Contains(request, "steps < MaxChainSteps");
            StringAssert.Contains(request, "run.MarkPlacementWanted();");
            StringAssert.Contains(request, "stepMode = CacheRequestMode.Auto;", "only the first step performs a Summon or a Move");
            StringAssert.Contains(request, "maxStepMs", "the chain reports its worst single step, which is the number this change exists to move");
        }

        [TestMethod]
        public void A_chain_step_re_reads_presence_and_state_rather_than_caching_a_player()
        {
            var execute = PooledLootSourceText.MethodBody(PlacerSrc(),
                "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");

            var cleared = execute.IndexOf("if (run.State != ThreadDungeonRunState.Cleared)", StringComparison.Ordinal);
            var lookup = execute.IndexOf("PlayerManager.GetOnlinePlayer(run.OwnerGuid)", StringComparison.Ordinal);
            var inside = execute.IndexOf("IsOwnerInside(run, owner)", StringComparison.Ordinal);

            Assert.IsTrue(cleared >= 0 && lookup > cleared && inside > lookup,
                "every step re-tests the run state and re-looks-up the owner; a player who leaves mid-spread must not be cached across steps");
        }
    }
}
