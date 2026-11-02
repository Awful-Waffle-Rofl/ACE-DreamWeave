using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events pooled loot, the claim-time half: the share driver (ONE budget per landblock tick across every
    /// claimant, round-robin) and the share chain (fill out of world, place once, hand back on failure, destroy on
    /// dispose), driven through WorldEventShareEnv's seams with an injected clock and a fake landblock queue whose
    /// Pump runs exactly what was queued before it started, as ActionQueue.RunActions does.
    /// </summary>
    [TestClass]
    public class WorldEventShareChainTests
    {
        private const uint Seat = 0x50000301u;

        private sealed class Rig
        {
            public double Clock;
            public double MsPerRoll;
            public double MsPerEnter;
            public int Capacity = 120;
            public bool HeldOk = true;
            public bool Valid = true;
            public int? RollBatchNow;
            public Func<Container, Position, bool> Enter = (_, _) => true;

            public readonly Queue<Action> Queue = new Queue<Action>();
            public readonly List<Container> Chests = new List<Container>();
            public readonly List<Container> Entered = new List<Container>();
            public readonly List<WorldObject> Held = new List<WorldObject>();
            public readonly List<uint> Lifetime = new List<uint>();
            public readonly List<WorldObject> Rolled = new List<WorldObject>();
            public readonly List<string> Messages = new List<string>();
            public readonly List<(WorldObject Rare, string FinderName)> Announced = new List<(WorldObject Rare, string FinderName)>();
            public readonly List<double> TickMs = new List<double>();

            public readonly WorldEventShareEnv Env;

            public Rig(int rollBatch = 12, int budgetMs = 8)
            {
                Env = new WorldEventShareEnv
                {
                    RunId = 7,
                    RollBatch = () => RollBatchNow ?? rollBatch,
                    StepBudgetMs = () => budgetMs,
                    Clock = () => Clock,
                    Enqueue = a => Queue.Enqueue(a),
                    CreateChest = (_, _) => { var c = Cache(Capacity); Chests.Add(c); return c; },
                    EnterWorld = (c, p) => { Clock += MsPerEnter; var ok = Enter(c, p); if (ok) Entered.Add(c); return ok; },
                    AddHeld = wo => { if (HeldOk) Held.Add(wo); return HeldOk; },
                    MarkCacheLifetime = g => Lifetime.Add(g),
                    RunValid = () => Valid,
                    Materialize = _ => { Clock += MsPerRoll; var items = new List<WorldObject> { Item(), Item() }; Rolled.AddRange(items); return items; },
                    AnnounceRares = (_, l) => Announced.AddRange(l),
                    SendOwner = (_, t) => Messages.Add(t),
                };
            }

            /// <summary>One landblock tick: runs what was queued when it started; returns how many actions ran.</summary>
            public int Pump()
            {
                var n = Queue.Count;
                var before = Clock;

                for (var i = 0; i < n; i++)
                    Queue.Dequeue()();

                TickMs.Add(Clock - before);
                return n;
            }

            public int PumpUntilIdle(int max = 1000)
            {
                var ticks = 0;

                while (Queue.Count > 0 && ticks < max)
                {
                    Pump();
                    ticks++;
                }

                Assert.AreEqual(0, Queue.Count, "the driver went idle");
                return ticks;
            }
        }

        private static ThreadLootLedgerEntry Record(int tier, WorldObject rare = null, uint rareFor = 0)
            => new ThreadLootLedgerEntry(false, false, new TreasureDeath { Tier = tier }, null, rare, rare != null ? "Finder" : null, 1, rare != null ? rareFor : 0);

        /// <summary>A pool dealt to <paramref name="seats"/>, each seat getting <paramref name="perSeat"/> records; <paramref name="rare"/> goes to the first seat.</summary>
        private static WorldEventLootPool Dealt(IReadOnlyList<uint> seats, int perSeat, WorldObject rare = null)
        {
            var pool = new WorldEventLootPool();

            for (var i = 0; i < perSeat * seats.Count; i++)
                pool.TryBank(Record(i + 1, i == 0 ? rare : null, i == 0 && rare != null ? seats[0] : 0));

            pool.CloseAndDeal(seats, 0);
            return pool;
        }

        private static WorldEventShareChain Begin(Rig rig, WorldEventShareDriver driver, WorldEventLootPool pool, uint seat)
        {
            Assert.IsTrue(pool.TryBeginDelivery(seat, out var share), "fixture: the share begins");

            var chain = new WorldEventShareChain(rig.Env, pool, share, seat, "Alpha", new Position(), new Position());
            Assert.IsTrue(pool.TryRegisterChain(chain));
            driver.Add(chain);
            return chain;
        }

        // ------------------------------------------------------------------------------------------------------
        // The shared per-tick budget
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void N_chains_share_one_budget_per_tick_and_all_complete()
        {
            var rig = new Rig(rollBatch: 12, budgetMs: 8) { MsPerRoll = 5, MsPerEnter = 5 };
            var driver = new WorldEventShareDriver(rig.Env);
            var seats = Enumerable.Range(0, 6).Select(i => 0x50000400u + (uint)i).ToList();
            var pool = Dealt(seats, 4);

            var chains = seats.Select(s => Begin(rig, driver, pool, s)).ToList();
            Assert.AreEqual(1, rig.Queue.Count, "ONE driver action, however many claimants");

            var firstTurnTick = new int[chains.Count];
            var tick = 0;

            while (rig.Queue.Count > 0 && tick < 500)
            {
                rig.Pump();
                tick++;

                Assert.IsTrue(rig.Queue.Count <= 1, "never more than one queued driver action");

                for (var i = 0; i < chains.Count; i++)
                {
                    if (firstTurnTick[i] == 0 && chains[i].Turns > 0)
                        firstTurnTick[i] = tick;
                }
            }

            // Every unit costs 5 ms against an 8 ms tick budget: the tick's first unit always runs, a second starts at
            // 5 ms, and the 10 ms mark is past the budget - so no tick ever exceeds the budget plus one unit.
            Assert.IsTrue(rig.TickMs.All(ms => ms <= 8 + 5), $"per-tick cost {string.Join(",", rig.TickMs)}");
            Assert.IsTrue(driver.MaxTickMs <= 8 + 5);

            Assert.IsTrue(chains.All(c => c.Outcome == "delivered"), "every claimant completes");
            Assert.AreEqual(6, rig.Held.Count);
            Assert.IsTrue(rig.Held.Cast<Container>().All(c => c.Inventory.Count == 8), "each chest holds its 4 rolls x 2 items");

            // Round-robin: at two units a tick, six claimants are all reached within three ticks.
            Assert.IsTrue(firstTurnTick.All(t => t >= 1 && t <= 3), $"first turns {string.Join(",", firstTurnTick)}");
            Assert.AreEqual(0, pool.LiveChainCount);
        }

        [TestMethod]
        public void The_roll_cap_is_a_total_per_tick_across_chains()
        {
            var rig = new Rig(rollBatch: 3, budgetMs: 8);
            var driver = new WorldEventShareDriver(rig.Env);
            var seats = Enumerable.Range(0, 4).Select(i => 0x50000500u + (uint)i).ToList();
            var pool = Dealt(seats, 5);

            var chains = seats.Select(s => Begin(rig, driver, pool, s)).ToList();
            var ticks = rig.PumpUntilIdle();

            Assert.IsTrue(driver.MaxTickUnits <= 3, $"max units in a tick {driver.MaxTickUnits}");
            Assert.AreEqual(8, ticks, "24 units at 3 a tick");
            Assert.IsTrue(chains.All(c => c.Outcome == "delivered"));
        }

        [TestMethod]
        public void Successor_skips_finished_chains_by_identity()
        {
            var a = new object();
            var b = new object();
            var c = new object();
            var d = new object();
            var e = new object();
            var list = new[] { a, b, c, d, e };

            // A and D finish this tick and D was served last: the next tick resumes at E, not B (the chain that would
            // slide into D's old index once A leaves).
            Assert.AreSame(e, WorldEventShareDriver.Successor(list, 3, x => x == a || x == d));
            Assert.AreEqual(2, WorldEventShareDriver.StartIndex(new[] { b, c, e }, e), "E's index once A and D have left");

            Assert.AreSame(b, WorldEventShareDriver.Successor(list, 4, x => x == a), "wraps past a finished chain");
            Assert.AreSame(c, WorldEventShareDriver.Successor(new[] { c }, 0, _ => false), "a lone live chain resumes at itself");
            Assert.IsNull(WorldEventShareDriver.Successor(list, 2, _ => true), "nothing live");
            Assert.IsNull(WorldEventShareDriver.Successor(list, -1, _ => false), "nothing served");

            Assert.AreEqual(0, WorldEventShareDriver.StartIndex(new[] { a, b }, c), "a resume chain no longer listed starts at 0");
            Assert.AreEqual(0, WorldEventShareDriver.StartIndex(new[] { a, b }, null));
        }

        [TestMethod]
        public void The_roll_cap_is_read_per_tick_so_a_change_applies_mid_run()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 6);
            var chain = Begin(rig, driver, pool, Seat);

            rig.Pump();
            Assert.AreEqual(1, chain.Rolls, "fixture: one roll at a cap of 1");

            rig.RollBatchNow = 3;
            rig.Pump();
            Assert.AreEqual(4, chain.Rolls, "the raised cap applies on the very next tick");
        }

        [TestMethod]
        public void A_throwing_tunable_read_falls_back_to_the_defaults()
        {
            var rig = new Rig();
            rig.Env.RollBatch = () => throw new InvalidOperationException("config read failed");
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 2);
            var chain = Begin(rig, driver, pool, Seat);

            rig.PumpUntilIdle();

            Assert.AreEqual("delivered", chain.Outcome, "the tick ran on the shipped defaults instead of throwing");
        }

        [TestMethod]
        public void An_Add_during_a_tick_does_not_queue_a_second_driver_action()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var other = 0x50000601u;
            var pool = Dealt(new[] { Seat, other }, 3);
            var materialize = rig.Env.Materialize;
            WorldEventShareChain late = null;

            rig.Env.Materialize = entry =>
            {
                if (late == null)
                {
                    late = Begin(rig, driver, pool, other);
                    Assert.AreEqual(0, rig.Queue.Count, "an Add while the tick runs queues nothing");
                }

                return materialize(entry);
            };

            var first = Begin(rig, driver, pool, Seat);
            Assert.AreEqual(1, rig.Queue.Count);

            var ticks = 0;

            while (rig.Queue.Count > 0 && ticks++ < 200)
            {
                rig.Pump();
                Assert.IsTrue(rig.Queue.Count <= 1, "never more than one queued driver action");
            }

            Assert.AreEqual("delivered", first.Outcome);
            Assert.AreEqual("delivered", late.Outcome);
        }

        [TestMethod]
        public void An_Add_after_the_driver_went_idle_schedules_exactly_once()
        {
            var rig = new Rig();
            var driver = new WorldEventShareDriver(rig.Env);
            var b = 0x50000611u;
            var c = 0x50000612u;
            var pool = Dealt(new[] { Seat, b, c }, 2);

            Begin(rig, driver, pool, Seat);
            rig.PumpUntilIdle();
            Assert.AreEqual(0, rig.Queue.Count, "fixture: idle");

            var second = Begin(rig, driver, pool, b);
            Assert.AreEqual(1, rig.Queue.Count, "an idle driver is scheduled again");

            var third = Begin(rig, driver, pool, c);
            Assert.AreEqual(1, rig.Queue.Count, "and only once");

            rig.PumpUntilIdle();
            Assert.AreEqual("delivered", second.Outcome);
            Assert.AreEqual("delivered", third.Outcome);
        }

        [TestMethod]
        public void A_throwing_Enqueue_leaves_no_registered_chain_and_the_share_retryable()
        {
            var rig = new Rig();
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 2);
            var enqueue = rig.Env.Enqueue;
            rig.Env.Enqueue = _ => throw new InvalidOperationException("queue gone");

            Assert.IsTrue(pool.TryBeginDelivery(Seat, out var share));
            var chain = new WorldEventShareChain(rig.Env, pool, share, Seat, "Alpha", new Position(), new Position());
            Assert.IsTrue(pool.TryRegisterChain(chain));

            // TryDeliver's catch path: the chain that was built owns the hand-back.
            Assert.ThrowsExactly<InvalidOperationException>(() => driver.Add(chain));
            chain.FailFromDriver();

            Assert.AreEqual(0, driver.Pending, "the chain left the driver");
            Assert.AreEqual(0, pool.LiveChainCount, "and the pool");
            Assert.AreEqual(WorldEventShareState.Pending, pool.ShareState(Seat));

            rig.Env.Enqueue = enqueue;
            var retry = Begin(rig, driver, pool, Seat);
            Assert.AreEqual(1, rig.Queue.Count, "the driver was left idle, so the retry schedules it");
            rig.PumpUntilIdle();
            Assert.AreEqual("delivered", retry.Outcome);
        }

        // ------------------------------------------------------------------------------------------------------
        // A driver whose landblock unloaded
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void IsStaleDriver_covers_reload_and_unload()
        {
            Assert.IsFalse(WorldEventShareDelivery.IsStaleDriver(true, () => throw new AssertFailedException("not consulted")), "the cache's own landblock");
            Assert.IsTrue(WorldEventShareDelivery.IsStaleDriver(false, () => false), "unloaded, or a different live object now at the same key: stale");
            Assert.IsFalse(WorldEventShareDelivery.IsStaleDriver(false, () => true), "another block that is still the loaded object is left alone");
        }

        [TestMethod]
        public void FailEach_keeps_going_when_one_hand_back_throws()
        {
            var rig = new Rig();
            var b = 0x50000631u;
            var pool = Dealt(new[] { Seat, b }, 2);
            var chains = new List<WorldEventShareChain>();

            foreach (var seat in new[] { Seat, b })
            {
                Assert.IsTrue(pool.TryBeginDelivery(seat, out var share));
                var chain = new WorldEventShareChain(rig.Env, pool, share, seat, "Alpha", new Position(), new Position());
                Assert.IsTrue(pool.TryRegisterChain(chain));
                chains.Add(chain);
            }

            var threw = WorldEventShareDelivery.FailEach(chains, c =>
            {
                if (c == chains[0])
                    throw new InvalidOperationException("first hand-back throws");

                c.FailFromDriver();
            }, "test");

            Assert.AreEqual(1, threw);
            Assert.AreEqual("handed-back", chains[1].Outcome, "the second chain is still handed back");
            Assert.AreEqual(WorldEventShareState.Pending, pool.ShareState(b));
            Assert.AreEqual(WorldEventShareState.Delivering, pool.ShareState(Seat), "fixture: the first really did not run");
        }

        [TestMethod]
        public void A_driver_on_an_unloaded_landblock_is_retired_and_its_chains_handed_back()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40504);
            var pool = Dealt(new[] { Seat }, 3, rare);
            var chain = Begin(rig, driver, pool, Seat);

            rig.Pump();
            Assert.AreEqual(WorldEventShareState.Delivering, pool.ShareState(Seat), "fixture: mid-delivery");

            // Unload clears the landblock's action queue: the driver's tick is gone.
            rig.Queue.Clear();

            var map = new Dictionary<string, WorldEventShareDriver> { ["old"] = driver };
            var stranded = WorldEventShareDelivery.RetireDrivers(map, new[] { "old" });

            Assert.AreEqual(0, map.Count);
            CollectionAssert.AreEqual(new[] { chain }, stranded);
            Assert.AreEqual(0, driver.Pending);

            foreach (var c in stranded)
                c.FailFromDriver();

            Assert.AreEqual("handed-back", chain.Outcome);
            Assert.AreEqual(WorldEventShareState.Pending, pool.ShareState(Seat));
            Assert.AreEqual(0, pool.LiveChainCount);
            Assert.IsFalse(rare.IsDestroyed);

            // The next click starts on a fresh driver and delivers what the first one had rolled, plus the rest.
            var fresh = new WorldEventShareDriver(rig.Env);
            var retry = Begin(rig, fresh, pool, Seat);
            rig.PumpUntilIdle();

            Assert.AreEqual("delivered", retry.Outcome);
            Assert.AreEqual(WorldEventShareState.Delivered, pool.ShareState(Seat));

            var placed = (Container)rig.Held.Single();
            Assert.AreEqual(7, placed.Inventory.Count, "3 rolls x 2 items plus the rare, nothing lost or re-rolled");
            Assert.AreEqual(6, rig.Rolled.Count);
        }

        // ------------------------------------------------------------------------------------------------------
        // The chain
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_budget_exhausted_tick_re_enqueues_once_and_does_not_complete()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 3);
            var chain = Begin(rig, driver, pool, Seat);

            Assert.AreEqual(1, rig.Pump());

            Assert.AreEqual(1, rig.Queue.Count, "re-enqueued exactly once");
            Assert.IsFalse(chain.Done);
            Assert.AreEqual(0, rig.Entered.Count, "the chest has not entered the world");
            Assert.AreEqual(WorldEventShareState.Delivering, pool.ShareState(Seat));
            Assert.AreEqual(2, chain.ChestForTest.Inventory.Count, "one roll this tick");
        }

        [TestMethod]
        public void EnterWorld_failing_twice_hands_the_share_back_and_a_retry_delivers()
        {
            var rig = new Rig { Enter = (_, _) => false };
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40500);
            var pool = Dealt(new[] { Seat }, 2, rare);
            var chain = Begin(rig, driver, pool, Seat);

            rig.PumpUntilIdle();

            Assert.AreEqual("handed-back", chain.Outcome);
            Assert.AreEqual(WorldEventShareState.Pending, pool.ShareState(Seat));
            Assert.AreEqual(2, rig.Chests.Count, "one chest in front, a second at the cache");
            Assert.IsTrue(rig.Chests.All(c => c.IsDestroyed), "both chests destroyed");
            Assert.IsFalse(rare.IsDestroyed);
            CollectionAssert.Contains(rig.Messages, WorldEventShareDelivery.ShareRetryMessage);

            var share = pool.SharesForTest()[Seat];
            Assert.AreSame(rare, share.Rares.Single().Rare, "the rare is restored, finder and all");
            Assert.AreEqual("Finder", share.Rares.Single().FinderName);
            Assert.AreEqual(4, share.Built.Count, "the rolled items are kept, not re-rolled");
            Assert.AreEqual(0, pool.LiveChainCount);

            // The retry (a second click): the share delivers what was already rolled, rare included.
            rig.Enter = (_, _) => true;
            var rolledBefore = rig.Rolled.Count;
            var retry = Begin(rig, driver, pool, Seat);
            rig.PumpUntilIdle();

            Assert.AreEqual("delivered", retry.Outcome);
            Assert.AreEqual(WorldEventShareState.Delivered, pool.ShareState(Seat));
            Assert.AreEqual(rolledBefore, rig.Rolled.Count, "nothing re-rolled");

            var placed = (Container)rig.Held.Single();
            Assert.AreEqual(5, placed.Inventory.Count);
            Assert.IsTrue(placed.Inventory.ContainsKey(rare.Guid));
            Assert.AreSame(rare, rig.Announced.Single().Rare, "the rare is announced when it lands");
            CollectionAssert.Contains(rig.Lifetime, placed.Guid.Full);
        }

        [TestMethod]
        public void Dispose_mid_chain_destroys_the_chest_and_everything_held_without_another_turn()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40501);
            var pool = Dealt(new[] { Seat }, 3, rare);
            var chain = Begin(rig, driver, pool, Seat);

            rig.Pump();
            var chest = chain.ChestForTest;
            Assert.IsNotNull(chest);
            Assert.IsTrue(chest.Inventory.ContainsKey(rare.Guid), "fixture: the rare is in the chest being filled");

            // The landblock queue is never pumped again (an unload clears it): Dispose alone must clean up.
            Assert.IsTrue(pool.Dispose() > 0);

            Assert.IsTrue(chain.Done);
            Assert.AreEqual("destroyed", chain.Outcome);
            Assert.IsTrue(chest.IsDestroyed);
            Assert.IsTrue(rare.IsDestroyed);
            Assert.IsTrue(rig.Rolled.All(i => i.IsDestroyed), "every rolled item went with the chest");
            Assert.AreEqual(0, pool.SharesForTest()[Seat].Entries.Count);
            Assert.AreEqual(0, pool.LiveChainCount);

            // And if the queue does run after all, the chain does nothing.
            rig.PumpUntilIdle();
            Assert.AreEqual(0, rig.Entered.Count);
        }

        [TestMethod]
        public void Dispose_before_the_first_turn_destroys_the_share_contents()
        {
            var rig = new Rig();
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40502);
            var pool = Dealt(new[] { Seat }, 2, rare);
            var chain = Begin(rig, driver, pool, Seat);

            pool.Dispose();

            Assert.AreEqual("destroyed", chain.Outcome);
            Assert.IsTrue(rare.IsDestroyed);
            Assert.AreEqual(0, rig.Chests.Count);
        }

        [TestMethod]
        public void A_closed_window_abandons_on_the_next_turn()
        {
            var rig = new Rig(rollBatch: 1);
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40503);
            var pool = Dealt(new[] { Seat }, 3, rare);
            var chain = Begin(rig, driver, pool, Seat);

            rig.Pump();
            var chest = chain.ChestForTest;
            rig.Valid = false;
            rig.PumpUntilIdle();

            Assert.AreEqual("abandoned", chain.Outcome);
            Assert.AreEqual(WorldEventShareState.Delivered, pool.ShareState(Seat), "an abandoned share is finished, not retryable");
            Assert.IsTrue(chest.IsDestroyed);
            Assert.IsTrue(rare.IsDestroyed);
            Assert.AreEqual(0, rig.Entered.Count);
        }

        [TestMethod]
        public void Dispose_mid_chain_destroys_prebuilt_and_refused_items_outside_the_chest()
        {
            var rig = new Rig(rollBatch: 12, budgetMs: 8) { Enter = (_, _) => false };
            var driver = new WorldEventShareDriver(rig.Env);
            var a = 0x50000621u;
            var b = 0x50000622u;
            var pool = Dealt(new[] { a, b }, 2);

            // B's first attempt rolls everything and is handed back, so its retry starts with 4 prebuilt items.
            Begin(rig, driver, pool, b);
            rig.PumpUntilIdle();
            Assert.AreEqual(4, pool.SharesForTest()[b].Built.Count, "fixture: B holds rolled items");

            rig.Enter = (_, _) => true;
            rig.Capacity = 3;

            // B's chest creation costs 10 ms, so the shared budget runs out (A already claimed a unit) the moment B's
            // prebuilt items fill its chest: B then waits with its overflow still held, as a busy tick would leave it.
            var createChest = rig.Env.CreateChest;
            rig.Env.CreateChest = (owner, name) => { if (owner == b) rig.Clock += 10; return createChest(owner, name); };

            var chainA = Begin(rig, driver, pool, a);
            var chainB = Begin(rig, driver, pool, b);

            // Tick 1: A rolls once (2 placed) and yields; B places 3 prebuilt, is full, and waits with 1 prebuilt.
            rig.Pump();

            // Tick 2: A rolls again at 10 ms (1 placed, 1 refused) and the budget's time is up. Neither chest is placed.
            rig.MsPerRoll = 10;
            rig.Pump();

            Assert.IsFalse(chainA.Done);
            Assert.IsFalse(chainB.Done);

            var inChests = new HashSet<WorldObject>(chainA.ChestForTest.Inventory.Values.Concat(chainB.ChestForTest.Inventory.Values));
            var outside = rig.Rolled.Where(i => !inChests.Contains(i)).ToList();
            Assert.AreEqual(2, outside.Count, "fixture: one prebuilt (B) and one refused (A) item sit outside any chest");
            Assert.IsFalse(outside.Any(i => i.IsDestroyed));

            pool.Dispose();

            Assert.IsTrue(outside.All(i => i.IsDestroyed), "the prebuilt and refused items are destroyed, not stranded");
            Assert.IsTrue(rig.Rolled.All(i => i.IsDestroyed));
            Assert.AreEqual("destroyed", chainA.Outcome);
            Assert.AreEqual("destroyed", chainB.Outcome);
            Assert.AreEqual(0, pool.LiveChainCount);
        }

        [TestMethod]
        public void A_handed_back_share_larger_than_the_chest_destroys_the_prebuilt_overflow_on_retry()
        {
            var rig = new Rig(rollBatch: 100) { Enter = (_, _) => false };
            var driver = new WorldEventShareDriver(rig.Env);
            var rare = Item(40505);
            var pool = Dealt(new[] { Seat }, 4, rare);

            Begin(rig, driver, pool, Seat);
            rig.PumpUntilIdle();
            Assert.AreEqual(8, pool.SharesForTest()[Seat].Built.Count, "fixture: 8 rolled items handed back");

            rig.Enter = (_, _) => true;
            rig.Capacity = 3;
            var retry = Begin(rig, driver, pool, Seat);
            rig.PumpUntilIdle();

            Assert.AreEqual("delivered", retry.Outcome);

            var placed = (Container)rig.Held.Single();
            Assert.AreEqual(3, placed.Inventory.Count);
            Assert.IsTrue(placed.Inventory.ContainsKey(rare.Guid), "the rare is placed first");

            var overflow = rig.Rolled.Where(i => !placed.Inventory.ContainsKey(i.Guid)).ToList();
            Assert.AreEqual(6, overflow.Count);
            Assert.IsTrue(overflow.All(i => i.IsDestroyed), "the prebuilt items that did not fit are destroyed");
            Assert.AreEqual(8, rig.Rolled.Count, "nothing re-rolled");
        }

        [TestMethod]
        public void AddHeld_refused_destroys_the_chest()
        {
            var rig = new Rig { HeldOk = false };
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 2);
            var chain = Begin(rig, driver, pool, Seat);

            rig.PumpUntilIdle();

            Assert.AreEqual("abandoned", chain.Outcome);
            Assert.AreEqual(1, rig.Entered.Count);
            Assert.IsTrue(rig.Entered.Single().IsDestroyed, "a chest the run will never clean up is destroyed");
            Assert.AreEqual(0, rig.Lifetime.Count);
            CollectionAssert.DoesNotContain(rig.Messages, WorldEventShareDelivery.ShareArrivedMessage);
        }

        [TestMethod]
        public void Capacity_overflow_destroys_the_remainder()
        {
            var rig = new Rig(rollBatch: 100) { Capacity = 3 };
            var driver = new WorldEventShareDriver(rig.Env);
            var pool = Dealt(new[] { Seat }, 4);
            var chain = Begin(rig, driver, pool, Seat);

            rig.PumpUntilIdle();

            Assert.AreEqual("delivered", chain.Outcome);

            var placed = (Container)rig.Held.Single();
            Assert.AreEqual(3, placed.Inventory.Count);
            Assert.AreEqual(4, rig.Rolled.Count, "two entries rolled, two never rolled");

            var outside = rig.Rolled.Where(i => !placed.Inventory.ContainsKey(i.Guid)).ToList();
            Assert.AreEqual(1, outside.Count);
            Assert.IsTrue(outside.All(i => i.IsDestroyed), "the item that did not fit is destroyed, not stranded");
            Assert.AreEqual(0, pool.SharesForTest()[Seat].Entries.Count);
        }
    }
}
