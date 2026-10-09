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
    /// Threads item 4, the loot trickle (2026-09-18): ledger rolls built while a run is Active, parked in the run's
    /// prebuilt store, and delivered at the clear exactly where the same rolls built at the clear would have gone.
    ///
    /// The chain itself (Kick, the landblock re-enqueue) needs a live Landblock, which this harness cannot build, so
    /// it is pinned on source; everything a step decides is driven through ThreadLootTrickle.RunStep and ShouldRun.
    /// No PropertyManager key is read by any path driven here: the budget and cap reach the trickle as run fields.
    /// </summary>
    [TestClass]
    public class ThreadLootTrickleTests
    {
        private sealed class FakeClock
        {
            public double Ms;
            public double Read() => Ms;
        }

        private static ThreadLootLedgerEntry Entry(int rolls = 1, WorldObject rare = null, uint recipient = 0)
            => new ThreadLootLedgerEntry(false, false, null, null, rare, rare == null ? null : "Finder", rolls, recipient);

        /// <summary>A pooled SOLO run moved to Active (populated, not yet cleared).</summary>
        private static ThreadDungeonRun ActiveSoloRun()
        {
            var run = PooledRun();
            run.MarkPopulated(planned: 1000, spawned: 1000, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "fixture: Active");
            return run;
        }

        /// <summary>A pooled, keyed GROUP run of <paramref name="seatCount"/> seats moved to Active.</summary>
        private static ThreadDungeonRun ActiveGroupRun(int seatCount)
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

            run.MarkPopulated(planned: 1000, spawned: 1000, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "fixture: Active");
            return run;
        }

        private static void Clear(ThreadDungeonRun run)
        {
            for (var i = 0; i < 2000 && run.State == ThreadDungeonRunState.Active; i++)
                run.RecordKill(false);

            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: Cleared");
        }

        /// <summary>One item per roll, valued values[k] for the k-th roll materialised; each roll advances the clock.</summary>
        private static Func<ThreadLootLedgerEntry, List<WorldObject>> Valued(IReadOnlyList<int> values, Func<FakeClock> clock, double msPerRoll, List<WorldObject> created)
        {
            var next = 0;

            return e =>
            {
                var items = new List<WorldObject>();

                for (var r = 0; r < e.Rolls; r++)
                {
                    var item = Item();
                    item.Value = values[next++];
                    created?.Add(item);
                    items.Add(item);
                }

                clock().Ms += msPerRoll * e.Rolls;

                if (e.HeldRare != null)
                    items.Add(e.HeldRare);

                return items;
            };
        }

        private static ThreadLootRollBudget TrickleBudget(FakeClock clock, int budgetMs = ThreadLootTrickle.DefaultBudgetMs)
            => new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, budgetMs);

        /// <summary>Runs trickle steps (fresh clock each) until the store holds <paramref name="pieces"/> pieces or the ledger is empty.</summary>
        private static int TrickleUntil(ThreadDungeonRun run, Func<ThreadLootLedgerEntry, List<WorldObject>> materialize, Func<FakeClock> clock, Action<FakeClock> setClock, int pieces)
        {
            var steps = 0;

            while (run.PrebuiltCount < pieces && run.LedgerCount > 0 && steps < 10000)
            {
                setClock(new FakeClock());
                ThreadLootTrickle.RunStep(run, materialize, TrickleBudget(clock()));
                steps++;
            }

            return steps;
        }

        // ------------------------------------------------------------------------------------------------------
        // Gates
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_trickle_runs_only_for_an_Active_pooled_run_with_rolls_to_build_room_under_the_cap_a_live_landblock_and_someone_inside()
        {
            var run = ActiveSoloRun();
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            Assert.IsTrue(ThreadLootTrickle.ShouldRun(run, landblockDormant: false, anyoneInside: true));
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, landblockDormant: true, anyoneInside: true), "a dormant copy still runs its action queue; the trickle must not");
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, landblockDormant: false, anyoneInside: false), "nobody inside: logged out or walked out");

            run.LootTrickleBudgetMs = 0;
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, false, true), "budget 0 is the switch-off");
            run.LootTrickleBudgetMs = ThreadLootTrickle.DefaultBudgetMs;

            run.LootTrickleMaxPrebuilt = 0;
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, false, true), "no room under the cap");
            run.LootTrickleMaxPrebuilt = ThreadLootTrickle.DefaultMaxPrebuilt;

            var empty = ActiveSoloRun();
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(empty, false, true), "nothing to build");

            var starting = PooledRun();
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(starting, false, true), "not Active yet");

            Clear(run);
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, false, true), "from Cleared on, delivery owns the ledger");

            Assert.IsFalse(ThreadLootTrickle.ShouldRun(null, false, true));
            Assert.AreEqual(2, ThreadLootTrickle.DefaultBudgetMs);
            Assert.AreEqual(0, ThreadLootTrickle.MinBudgetMs);
            Assert.AreEqual(50, ThreadLootTrickle.MaxBudgetMs);
        }

        [TestMethod]
        public void A_trickle_step_builds_rolls_in_kill_order_one_at_a_time_and_stops_after_the_roll_that_crosses_its_budget()
        {
            var run = ActiveSoloRun();
            for (var i = 0; i < 10; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var created = new List<WorldObject>();
            var result = ThreadLootTrickle.RunStep(run, Valued(Enumerable.Range(0, 10).ToList(), () => clock, 1.5, created), TrickleBudget(clock));

            // 1.5 ms, 3.0 ms: the second roll crosses 2 ms.
            Assert.AreEqual(2, result.Pieces);
            Assert.AreEqual(2, result.ItemsBuilt);
            Assert.IsTrue(result.More, "out of budget with rolls waiting: continue next tick");
            Assert.AreEqual(8, run.LedgerCount);
            Assert.AreEqual(2, run.PrebuiltCount);
            Assert.AreEqual(2, run.PrebuiltObjects);

            Assert.IsTrue(run.TryTakePrebuilt(out var first));
            Assert.IsTrue(run.TryTakePrebuilt(out var second));
            Assert.AreEqual(0L, (long)(first.Items[0].Value ?? -1), "kill order");
            Assert.AreEqual(1L, (long)(second.Items[0].Value ?? -1));
        }

        [TestMethod]
        public void The_cap_pauses_the_trickle_and_the_rest_is_built_at_the_clear()
        {
            var run = ActiveSoloRun();
            run.LootTrickleMaxPrebuilt = 3;
            for (var i = 0; i < 10; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var materialize = Valued(Enumerable.Range(0, 10).ToList(), () => clock, 0.1, null);
            var result = ThreadLootTrickle.RunStep(run, materialize, TrickleBudget(clock, 50));

            Assert.AreEqual(3, run.PrebuiltObjects, "stops at the cap even with budget left");
            Assert.IsFalse(result.More, "a capped trickle pauses rather than spinning");
            Assert.IsFalse(ThreadLootTrickle.ShouldRun(run, false, true));
            Assert.AreEqual(7, run.LedgerCount);
        }

        // ------------------------------------------------------------------------------------------------------
        // Conservation
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Prebuilt_loot_is_owed_loot_to_every_reader_that_decides_on_it()
        {
            var run = ActiveGroupRun(2);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            ThreadLootTrickle.RunStep(run, Valued(new[] { 5 }, () => clock, 1, null), TrickleBudget(clock));

            Assert.AreEqual(0, run.LedgerCount);
            Assert.AreEqual(1, run.PrebuiltCount);
            Assert.IsTrue(run.HasUnclaimedLoot);
            Assert.IsTrue(run.HasUndealtLoot);
            Assert.IsTrue(run.HasUnclaimedLootFor(0x50000020u));

            Clear(run);
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "the reap guard holds the copy for it");

            run.MarkPlacementWanted();
            Assert.IsTrue(ThreadRunPresence.GroupDeliveryWanted(run, _ => null), "the Tick retry sees it with only prebuilt loot left");
        }

        [TestMethod]
        public void Prebuilt_items_and_their_rare_are_destroyed_and_reported_when_the_run_ends()
        {
            var run = ActiveSoloRun();
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: 1, rare: rare)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var created = new List<WorldObject>();
            ThreadLootTrickle.RunStep(run, Valued(new[] { 1, 2 }, () => clock, 0.1, created), TrickleBudget(clock, 50));

            Assert.AreEqual(2, run.PrebuiltCount);
            Assert.AreEqual(3, run.PrebuiltObjects, "two items and the rare");

            // Abandon, logout past the grace and empty-grace collapse all end through EndRun -> DisposeForRunEnd.
            Assert.IsTrue(run.MarkEnded("abandoned"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(3, drain.Prebuilt.Count);
            Assert.IsTrue(created.All(i => i.IsDestroyed) && rare.IsDestroyed, "nothing stranded out of world");
            Assert.AreEqual(0, run.PrebuiltCount);
            Assert.AreEqual(0, run.PrebuiltObjects);
            Assert.IsFalse(run.HasUnclaimedLoot);

            var line = ThreadDungeonManager.UnclaimedPooledLootLine(run, drain);
            Assert.IsNotNull(line, "the unclaimed-loot line fires on prebuilt loot alone");
            StringAssert.Contains(line, "prebuilt=3");

            StringAssert.Contains(ThreadDungeonManager.RunSummaryLine(run, drain), "prebuiltLeft=3");
        }

        [TestMethod]
        public void An_ended_run_refuses_the_store_and_the_step_destroys_what_it_built()
        {
            var run = ActiveSoloRun();
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: 1, rare: rare)));

            var built = new List<WorldObject>();

            // The run ends between the claim and the store (EndRun from another thread).
            Func<ThreadLootLedgerEntry, List<WorldObject>> endingMaterializer = e =>
            {
                var item = Item();
                built.Add(item);
                Assert.IsTrue(run.MarkEnded("test"));
                return new List<WorldObject> { item, e.HeldRare };
            };

            var clock = new FakeClock();
            var result = ThreadLootTrickle.RunStep(run, endingMaterializer, TrickleBudget(clock));

            Assert.AreEqual(0, result.Pieces);
            Assert.IsFalse(result.More);
            Assert.IsTrue(built.All(i => i.IsDestroyed) && rare.IsDestroyed);
            Assert.AreEqual(0, run.PrebuiltCount);
            Assert.IsFalse(run.TryAddPrebuilt(Entry(), new List<WorldObject>()), "invariant 5: nothing accepted once Ended");

            // The piece never reached the store, so DrainLootPool cannot see it: the loss is counted instead, and both
            // run-end lines carry it (code review of #1224, finding 1).
            Assert.AreEqual(1L, run.Perf.TrickleDroppedItems);
            Assert.AreEqual(1L, run.Perf.TrickleDroppedRares);

            var drain = ThreadLootPool.DisposeForRunEnd(run);
            Assert.AreEqual(0, drain.Prebuilt.Count);
            var line = ThreadDungeonManager.UnclaimedPooledLootLine(run, drain);
            Assert.IsNotNull(line, "the unclaimed-loot line fires on a trickle drop alone");
            StringAssert.Contains(line, "trickleDroppedOnEnd=1/1");
            StringAssert.Contains(ThreadDungeonManager.RunSummaryLine(run, drain), "trickleDroppedOnEnd=1/1");
        }

        [TestMethod]
        public void A_materialisation_that_throws_keeps_the_rare_for_the_clear_and_stops_the_step()
        {
            var run = ActiveSoloRun();
            var rare = Item();
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rolls: 1, rare: rare)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var result = ThreadLootTrickle.RunStep(run, _ => throw new InvalidOperationException("roll failed"), TrickleBudget(clock, 50));

            Assert.AreEqual(1, result.Pieces);
            Assert.IsFalse(result.More, "a throwing step ends the chain");
            Assert.AreEqual(1, run.LedgerCount, "the next entry waits");
            Assert.IsTrue(run.TryTakePrebuilt(out var piece));
            Assert.AreSame(rare, piece.Piece.HeldRare);
            Assert.AreEqual(0, piece.Items.Count, "the rolls are lost, as at the clear");
            Assert.IsFalse(rare.IsDestroyed);
        }

        [TestMethod]
        public void A_stale_trickle_token_is_taken_over_and_a_live_one_is_not()
        {
            var run = ActiveSoloRun();
            var t0 = new DateTime(2026, 9, 18, 12, 0, 0, DateTimeKind.Utc);

            var token = run.TryBeginTrickle(t0, ThreadLootTrickle.StaleAfter);
            Assert.AreNotEqual(0L, token);
            Assert.AreEqual(0L, run.TryBeginTrickle(t0.AddSeconds(4), ThreadLootTrickle.StaleAfter), "live chain");

            run.RefreshTrickle(token, t0.AddSeconds(4));
            Assert.AreEqual(0L, run.TryBeginTrickle(t0.AddSeconds(8), ThreadLootTrickle.StaleAfter), "re-stamped each step");

            // Landblock.Unload cleared the queue: no step re-stamps it, and the next Tick takes over.
            var next = run.TryBeginTrickle(t0.AddSeconds(10), ThreadLootTrickle.StaleAfter);
            Assert.AreNotEqual(0L, next);
            Assert.IsFalse(run.IsTrickleCurrent(token), "the dropped chain can no longer run");
            run.EndTrickle(token);
            Assert.IsTrue(run.IsTrickleCurrent(next), "and cannot release its successor");

            Assert.IsTrue(run.MarkEnded("test"));
            run.EndTrickle(next);
            Assert.AreEqual(0L, run.TryBeginTrickle(t0.AddSeconds(30), ThreadLootTrickle.StaleAfter), "nothing starts once Ended");
        }

        // ------------------------------------------------------------------------------------------------------
        // Delivery: only the build timing moves
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_solo_clear_delivers_prebuilt_and_fresh_rolls_in_kill_order_with_the_rare_landing_last()
        {
            var run = ActiveSoloRun();
            var rare = Item();
            for (var i = 0; i < 5; i++)
                Assert.IsTrue(run.TryAppendLootEntry(i == 2 ? Entry(rolls: 1, rare: rare) : Entry()));

            var clock = new FakeClock();
            var materialize = Valued(Enumerable.Range(0, 5).Select(v => v + 1).ToList(), () => clock, 2, null);

            // Build the first three ahead of the clear (the rare's entry among them), the rest at the clear. At 2 ms a
            // roll each trickle step builds exactly one, so the store stops at three.
            TrickleUntil(run, materialize, () => clock, c => clock = c, 3);
            Assert.AreEqual(3, run.PrebuiltCount);

            Clear(run);

            var cache = Cache();
            var fill = ThreadCacheFiller.Fill(run, cache, materialize, _ => new List<WorldObject>(), ThreadLootRollBudget.Unlimited());

            var order = cache.Inventory.Values.OrderBy(i => i.PlacementPosition).Select(i => ReferenceEquals(i, rare) ? -1 : (int)(i.Value ?? 0)).ToList();
            CollectionAssert.AreEqual(new[] { 1, 2, 3, -1, 4, 5 }, order, "kill order across the two stores; the rare after its own entry's roll");
            Assert.AreEqual(1, fill.RaresLanded.Count, "the rare is still announced");
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.AreEqual(2, run.Perf.BuiltAtClear);
        }

        /// <summary>
        /// One 6-seat group clear of <paramref name="values"/> single-roll entries with a fixed snake offset: the first
        /// <paramref name="prebuild"/> rolls built by the trickle while Active, the rest at the clear under the
        /// production step budget at <paramref name="msPerRoll"/> (at least 2 ms, so each trickle step builds exactly one
        /// roll and the store stops at <paramref name="prebuild"/>). Returns each seat's held values, sorted.
        /// </summary>
        private static List<long>[] GroupClear(IReadOnlyList<int> values, int prebuild, double msPerRoll)
        {
            var run = ActiveGroupRun(6);
            run.SetSnakeRunOffset(2);

            foreach (var _ in values)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var clock = new FakeClock();
            var materialize = Valued(values, () => clock, msPerRoll, null);

            if (prebuild > 0)
                TrickleUntil(run, materialize, () => clock, c => clock = c, prebuild);

            Assert.AreEqual(prebuild, run.PrebuiltCount);
            Clear(run);

            for (var steps = 0; run.HasUndealtLoot && steps < 5000; steps++)
            {
                clock = new FakeClock();
                ThreadGroupCacheDelivery.Deal(run, materialize, (r, b) => new List<WorldObject>(),
                    rollBudget: new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs));
            }

            Assert.IsFalse(run.HasUndealtLoot);

            return run.DealSeats().Select(seat =>
            {
                var pile = run.TakeHeldPile(seat);
                return pile.Select(h => (long)(h.Item.Value ?? 0)).OrderBy(v => v).ToList();
            }).ToArray();
        }

        [TestMethod]
        public void Prebuilding_moves_only_the_build_timing_every_seat_receives_exactly_the_same_values()
        {
            // Heavy-tailed values, fixed seed and fixed snake offset: any change to which rolls are sorted and snake-dealt
            // together would move value between seats and show up here.
            var rng = new Random(20260918);
            var values = Enumerable.Range(0, 240).Select(_ => { var v = rng.Next(1, 1000); return rng.NextDouble() < 0.05 ? v * 50 : v; }).ToList();

            var atClear = GroupClear(values, 0, 3);
            var allPrebuilt = GroupClear(values, 240, 3);
            var partPrebuilt = GroupClear(values, 100, 3);
            var fasterRolls = GroupClear(values, 0, 2);

            for (var seat = 0; seat < 6; seat++)
            {
                CollectionAssert.AreEqual(atClear[seat], allPrebuilt[seat], $"seat {seat}: every roll prebuilt");
                CollectionAssert.AreEqual(atClear[seat], partPrebuilt[seat], $"seat {seat}: 100 of 240 prebuilt, the rest at the clear");
                CollectionAssert.AreEqual(atClear[seat], fasterRolls[seat], $"seat {seat}: step timing does not change the sets either");
            }

            Assert.AreEqual(240, atClear.Sum(s => s.Count));
        }

        [TestMethod]
        public void The_trickle_is_started_by_the_manager_tick_and_its_chain_re_tests_every_gate_each_step()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs");
            StringAssert.Contains(manager, "ThreadLootTrickle.Kick(run, landblock, now);");
            StringAssert.Contains(manager, "PropertyManager.GetLong(\"dynamic_dungeons_loot_trickle_budget_ms\", ThreadLootTrickle.DefaultBudgetMs)");
            StringAssert.Contains(manager, "LootTrickleBudgetMs = lootTrickleBudgetMs,");
            StringAssert.Contains(manager, "LootTrickleMaxPrebuilt = lootTrickleMaxPrebuilt,");

            var trickle = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadLootTrickle.cs");
            var kick = PooledLootSourceText.MethodBody(trickle, "internal static bool Kick(ThreadDungeonRun run, Landblock landblock, DateTime nowUtc)");

            var current = kick.IndexOf("if (!run.IsTrickleCurrent(token))", StringComparison.Ordinal);
            var refresh = kick.IndexOf("run.RefreshTrickle(token, DateTime.UtcNow);", current, StringComparison.Ordinal);
            var gate = kick.IndexOf("if (!ShouldRun(run, landblock.IsDormant, AnyoneInside(run)))", refresh, StringComparison.Ordinal);
            var metric = kick.IndexOf("ServerMetrics.RecordThreadsLootTrickle(", gate, StringComparison.Ordinal);
            var requeue = kick.IndexOf("landblock.EnqueueAction(new ActionEventDelegate(Step));", metric, StringComparison.Ordinal);
            var release = kick.IndexOf("run.EndTrickle(token);", requeue, StringComparison.Ordinal);

            Assert.IsTrue(current >= 0 && refresh > current && gate > refresh && metric > gate && requeue > metric && release > requeue);
        }
    }
}
