using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The SOLO boss bonus, built across delivery steps (2026-09-19). Before this, a claiming step built the whole
    /// bonus - Trade Notes, salvage bags and about ten Legendary-table rolls - in one unbudgeted call, which is what
    /// put a 72 ms step in a stage clear whose step budget is 8 ms. It is now split the way the group path has been
    /// since the bonus was pooled: the fixed half whole, the rolls counted once and built a batch at a time.
    ///
    /// No PropertyManager key is read by any path driven here: every builder and counter is a seam.
    /// </summary>
    [TestClass]
    public class ThreadSoloBonusBatchingTests
    {
        private sealed class FakeClock
        {
            public double Ms;
            public double Read() => Ms;
        }

        private static readonly Func<ThreadDungeonRun, List<WorldObject>> NoFixed = _ => new List<WorldObject>();

        private static ThreadLootLedgerEntry Entry()
            => new ThreadLootLedgerEntry(false, false, null, null, null, null, 1, 0);

        private static uint[] WcidsInSlotOrder(Container cache)
            => cache.Inventory.Values.OrderBy(i => i.PlacementPosition ?? 0).Select(i => i.WeenieClassId).ToArray();

        /// <summary>A pooled run with the bonus latch armed and <paramref name="entries"/> single-roll ledger entries.</summary>
        private static ThreadDungeonRun BonusRun(int entries = 0)
        {
            var run = PooledRun();
            Assert.IsTrue(run.TryMarkLootBonusPending());

            for (var i = 0; i < entries; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            return run;
        }

        [TestMethod]
        public void The_claiming_step_counts_the_rolls_once_and_parks_them_instead_of_building_them()
        {
            var run = BonusRun();
            var cache = Cache();
            var counts = 0;
            var built = 0;

            var result = ThreadCacheFiller.Fill(run, cache, _ => throw new AssertFailedException("no ledger entry"),
                _ => new List<WorldObject> { Item(900), Item(901) },
                new ThreadLootRollBudget(2),
                countBonusRolls: (r, factor, cap) =>
                {
                    counts++;
                    Assert.AreEqual(1.0, factor, "a solo run has no group reward bonus");
                    Assert.AreEqual(ThreadDungeonRewardSpawner.DefaultLootCountCap, cap, "counted at one cache's cap, as the unbatched build asked for");
                    return 10;
                },
                buildBonusRolls: (r, factor, rolls) => { built += rolls; return Enumerable.Range(0, rolls).Select(_ => Item(910)).ToList(); });

            Assert.IsTrue(result.BonusClaimed);
            Assert.AreEqual(1, counts, "counted once, at the claim");

            // The step's roll cap is 2: the fixed half spent both, so nothing is left for a roll this step.
            Assert.AreEqual(0, built);
            Assert.AreEqual(10, run.PooledBonusRollsPending);
            Assert.IsTrue(run.HasUnclaimedLoot, "parked rolls keep the delivery chain alive");
            Assert.IsFalse(run.IsLootBonusPending, "the latch is one-shot");
            CollectionAssert.AreEqual(new uint[] { 900, 901 }, WcidsInSlotOrder(cache));
        }

        [TestMethod]
        public void The_rolls_are_built_across_steps_within_each_step_budget_and_every_counted_roll_is_built_exactly_once()
        {
            var run = BonusRun();
            var cache = Cache();
            var clock = new FakeClock();
            var batches = new List<int>();
            var created = new List<WorldObject>();

            Func<ThreadDungeonRun, double, int, List<WorldObject>> rolls = (r, factor, count) =>
            {
                batches.Add(count);
                clock.Ms += 3 * count;   // 3 ms a roll, against an 8 ms step budget

                var items = Enumerable.Range(0, count).Select(_ => Item(910)).ToList();
                created.AddRange(items);
                return items;
            };

            var steps = 0;

            while (run.HasUnclaimedLoot && steps < 50)
            {
                clock = new FakeClock();
                ThreadCacheFiller.Fill(run, cache, _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                    new ThreadLootRollBudget(ThreadLootRollBudget.DefaultRollsPerStep, clock.Read, ThreadLootRollBudget.DefaultStepBudgetMs),
                    countBonusRolls: (_, _, _) => 10, buildBonusRolls: rolls);
                steps++;
            }

            // A timed budget claims one roll at a time and stops after the roll that crosses its budget: 3 rolls a step.
            CollectionAssert.AreEqual(new[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, batches, "one roll per claim under a timed budget");
            Assert.AreEqual(4, steps, "3 rolls a step at 3 ms each against 8 ms, then the step that finds nothing left");
            Assert.AreEqual(10, created.Count, "every counted roll built");
            Assert.AreEqual(10, cache.Inventory.Count, "and placed");
            Assert.AreEqual(0, run.PooledBonusRollsPending);
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void An_untimed_step_takes_the_rolls_in_batches_of_what_its_roll_cap_has_left()
        {
            var run = BonusRun();
            var batches = new List<int>();

            ThreadCacheFiller.Fill(run, Cache(), _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                new ThreadLootRollBudget(4),
                countBonusRolls: (_, _, _) => 10,
                buildBonusRolls: (r, f, count) => { batches.Add(count); return Enumerable.Range(0, count).Select(_ => Item(910)).ToList(); });

            CollectionAssert.AreEqual(new[] { 4 }, batches, "the whole remaining roll cap in one call");
            Assert.AreEqual(6, run.PooledBonusRollsPending, "the rest waits for the next step");
        }

        [TestMethod]
        public void Order_is_unchanged_the_fixed_half_then_the_rolls_then_the_ledger()
        {
            var run = BonusRun(entries: 2);
            var cache = Cache();

            ThreadCacheFiller.Fill(run, cache, _ => new List<WorldObject> { Item(300) },
                _ => new List<WorldObject> { Item(900), Item(901) },
                ThreadLootRollBudget.Unlimited(),
                countBonusRolls: (_, _, _) => 3,
                buildBonusRolls: (r, f, count) => Enumerable.Range(0, count).Select(_ => Item(910)).ToList());

            CollectionAssert.AreEqual(new uint[] { 900, 901, 910, 910, 910, 300, 300 }, WcidsInSlotOrder(cache),
                "notes and bags, then the Legendary rolls, then the ledger");
            Assert.IsFalse(run.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_step_that_spends_its_budget_on_bonus_rolls_claims_no_ledger_rolls_so_nothing_overtakes_the_bonus()
        {
            var run = BonusRun(entries: 4);
            var cache = Cache();
            var ledgerClaims = 0;

            ThreadCacheFiller.Fill(run, cache, _ => { ledgerClaims++; return new List<WorldObject> { Item(300) }; }, NoFixed,
                new ThreadLootRollBudget(3),
                countBonusRolls: (_, _, _) => 5,
                buildBonusRolls: (r, f, count) => Enumerable.Range(0, count).Select(_ => Item(910)).ToList());

            Assert.AreEqual(0, ledgerClaims, "the bonus rolls took the whole step");
            Assert.AreEqual(2, run.PooledBonusRollsPending);
            Assert.AreEqual(4, run.LedgerCount);
            CollectionAssert.AreEqual(new uint[] { 910, 910, 910 }, WcidsInSlotOrder(cache));
        }

        [TestMethod]
        public void A_counter_that_returns_nothing_or_throws_leaves_the_bonus_with_no_rolls_and_the_latch_spent()
        {
            var zero = BonusRun();
            ThreadCacheFiller.Fill(zero, Cache(), _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                ThreadLootRollBudget.Unlimited(),
                countBonusRolls: (_, _, _) => 0,
                buildBonusRolls: (r, f, count) => throw new AssertFailedException("nothing to build"));

            Assert.AreEqual(0, zero.PooledBonusRollsPending);
            Assert.IsFalse(zero.HasUnclaimedLoot);
            Assert.IsFalse(zero.IsLootBonusPending);

            var threw = BonusRun();
            ThreadCacheFiller.Fill(threw, Cache(), _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                ThreadLootRollBudget.Unlimited(),
                countBonusRolls: (_, _, _) => throw new InvalidOperationException("count failed"),
                buildBonusRolls: (r, f, count) => throw new AssertFailedException("nothing to build"));

            Assert.AreEqual(0, threw.PooledBonusRollsPending, "a count that threw parks nothing");
            Assert.IsFalse(threw.HasUnclaimedLoot);
        }

        [TestMethod]
        public void A_batch_that_throws_costs_only_its_own_rolls_and_the_rest_are_still_built()
        {
            var run = BonusRun();
            var cache = Cache();
            var calls = 0;

            ThreadCacheFiller.Fill(run, cache, _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                new ThreadLootRollBudget(6),
                countBonusRolls: (_, _, _) => 6,
                buildBonusRolls: (r, f, count) =>
                {
                    if (++calls == 1)
                        throw new InvalidOperationException("roll failed");

                    return Enumerable.Range(0, count).Select(_ => Item(910)).ToList();
                });

            // The untimed budget takes the whole cap in one batch, so the throw costs all six rolls here; the point is
            // that the fill does not propagate it and the run is left consistent.
            Assert.AreEqual(0, run.PooledBonusRollsPending);
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.AreEqual(0, cache.Inventory.Count);
        }

        [TestMethod]
        public void A_run_that_ends_before_its_rolls_are_built_simply_never_builds_them()
        {
            var run = BonusRun();

            ThreadCacheFiller.Fill(run, Cache(), _ => throw new AssertFailedException("no ledger entry"), NoFixed,
                new ThreadLootRollBudget(1),
                countBonusRolls: (_, _, _) => 8,
                buildBonusRolls: (r, f, count) => Enumerable.Range(0, count).Select(_ => Item(910)).ToList());

            Assert.AreEqual(7, run.PooledBonusRollsPending);

            Assert.IsTrue(run.MarkEnded("abandoned"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(7, drain.PooledBonusRolls, "reported as loot the player never received");
            Assert.AreEqual(0, run.PooledBonusRollsPending);
            StringAssert.Contains(ThreadDungeonManager.UnclaimedPooledLootLine(run, drain), "pooledRolls=7");
        }

        [TestMethod]
        public void The_solo_bonus_seams_are_the_fixed_half_and_the_same_two_pooled_builders_the_group_path_uses()
        {
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusFixedItems), ThreadCacheFiller.BonusBuilder.Method.Name);
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BossBonusPooledRollCount), ThreadCacheFiller.BonusRollCounter.Method.Name);
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls), ThreadCacheFiller.BonusRollBuilder.Method.Name);

            var src = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadCacheFiller.cs");
            StringAssert.Contains(src, "Fill(run, cache, EntryMaterializer, BonusBuilder, budget, BonusRollCounter, BonusRollBuilder)",
                "the production entry point passes both halves");

            // The fixed half is the first two steps of the old whole-bonus builder, by delegation, so a solo player's
            // currency and salvage are byte-for-byte what they were.
            var spawner = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRewardSpawner.cs");
            StringAssert.Contains(spawner, "public static List<WorldObject> BuildBossBonusFixedItems(ThreadDungeonRun run) => BuildBossBonusPerSeatItems(run, 1.0);");
        }
    }
}
