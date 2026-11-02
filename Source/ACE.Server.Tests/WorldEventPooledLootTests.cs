using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldEvents;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

using TreasureDeath = ACE.Database.Models.World.TreasureDeath;

namespace ACE.Server.Tests
{
    /// <summary>
    /// World Events pooled loot (world_events_pooled_loot_enabled): the death predicate, the bank, the equal-split deal,
    /// the budgeted fill step, and - pinned on source, because Creature.Die, the claim handler and the share chain need a
    /// live world, a Player and a Session - the wiring that ties them together. Every source pin is a fault function
    /// proven to discriminate by running it on a copy with the guarded line removed or moved.
    /// </summary>
    [TestClass]
    public class WorldEventPooledLootTests
    {
        private const uint SeatA = 0x50000101u;
        private const uint SeatB = 0x50000102u;
        private const uint SeatC = 0x50000103u;

        [TestCleanup]
        public void ResetSeams()
        {
            WorldEventLootBank.HeldRareRoller = null;
        }

        // ------------------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------------------

        private static WorldEvent NewEvent(bool? latch)
        {
            var evt = new WorldEvent(7, null, null, null);

            if (latch.HasValue)
                evt.LatchPooledLoot(latch.Value);

            return evt;
        }

        private static WorldObject Named(uint guid, string name)
            => new GenericObject(new Weenie
            {
                WeenieClassId = 1,
                WeenieType = WeenieType.Generic,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, name } },
            }, new ObjectGuid(guid));

        private static Creature EventCreature(WorldEvent evt, int level = 50)
        {
            TestGameTables.EnsureInitialized();

            var creature = new Creature(new Weenie { WeenieClassId = 42, WeenieType = WeenieType.Creature }, new ObjectGuid(NextGuid()));
            creature.P_WorldEvent = evt;
            creature.SetProperty(PropertyInt.WorldEventId, (int)evt.RunId);
            creature.Level = level;
            creature.DeathTreasureOverride = new TreasureDeath { Tier = 6 };
            return creature;
        }

        /// <summary>A banked record: one roll of a tier-N profile, optionally holding a rare rolled for <paramref name="rareFor"/>.</summary>
        private static ThreadLootLedgerEntry Record(int tier, WorldObject rare = null, uint rareFor = 0)
            => new ThreadLootLedgerEntry(false, false, new TreasureDeath { Tier = tier }, null, rare, rare != null ? "Finder" : null, 1, rare != null ? rareFor : 0);

        // ------------------------------------------------------------------------------------------------------
        // The death predicate
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Predicate_truth_table_needs_reference_stamp_latch_and_not_boss()
        {
            Assert.IsTrue(Creature.IsWorldEventPooledDeath(true, 7, true, false), "a non-boss event creature of a latched run pools");

            Assert.IsFalse(Creature.IsWorldEventPooledDeath(false, 7, true, false), "no back-reference");
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(true, null, true, false), "no run stamp");
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(true, 7, false, false), "latch off: a normal corpse");
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(true, 7, true, true), "the boss keeps its corpse");
        }

        [TestMethod]
        public void Predicate_reads_the_run_latch()
        {
            var on = NewEvent(true);
            var off = NewEvent(false);
            var never = NewEvent(null);

            Assert.IsNotNull(on.LootPool);
            Assert.IsNull(off.LootPool, "latched off: no bank at all");
            Assert.IsNull(never.LootPool, "a run that never went Active has no bank");

            Assert.IsTrue(Creature.IsWorldEventPooledDeath(on, 7, 0x80000001u));
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(off, 7, 0x80000001u));
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(never, 7, 0x80000001u));
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(null, 7, 0x80000001u));
            Assert.IsFalse(Creature.IsWorldEventPooledDeath(on, null, 0x80000001u));
        }

        [TestMethod]
        public void The_latch_takes_the_first_answer_only()
        {
            var evt = NewEvent(false);
            evt.LatchPooledLoot(true);
            Assert.IsNull(evt.LootPool, "a second latch cannot turn a run on mid-flight");

            var on = NewEvent(true);
            var pool = on.LootPool;
            on.LatchPooledLoot(false);
            Assert.AreSame(pool, on.LootPool, "nor off");
        }

        // ------------------------------------------------------------------------------------------------------
        // The bank
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void BankKill_banks_the_profile_and_a_rare_that_reaches_its_killer()
        {
            var evt = NewEvent(true);
            var rare = Item(40100);
            var rolledFor = new List<DamageHistoryInfo>();
            WorldEventLootBank.HeldRareRoller = (_, k) => { rolledFor.Add(k); return rare; };

            // A player-range guid reads as a player killer (DamageHistoryInfo.IsPlayer is a guid-range test); level 120
            // with no gate level is rare-eligible.
            var killer = new DamageHistoryInfo(Named(SeatA, "+Alpha"));

            Assert.IsTrue(WorldEventLootBank.BankKill(EventCreature(evt, 120), killer));
            Assert.AreEqual(1, evt.LootPool.Count);
            Assert.AreSame(killer, rolledFor.Single());

            // Offset 1 deals the record to B; the rare still goes to A, who killed it.
            evt.LootPool.CloseAndDeal(new[] { SeatA, SeatB }, 1);
            var shares = evt.LootPool.SharesForTest();

            Assert.AreEqual(1, shares[SeatB].Entries.Count);
            Assert.AreEqual(0, shares[SeatA].Entries.Count);
            Assert.AreSame(rare, shares[SeatA].Rares.Single().Rare);
            Assert.AreEqual("Alpha", shares[SeatA].Rares.Single().FinderName, "'+' trimmed, as on a corpse");
        }

        [TestMethod]
        public void BankKill_reads_the_gate_level_for_the_rare()
        {
            var evt = NewEvent(true);
            var rolls = 0;
            WorldEventLootBank.HeldRareRoller = (_, _) => { rolls++; return Item(40104); };

            // Uplifted from 50 to 120: the gate level is under 100, so no rare is rolled. Reading Level would roll one.
            var uplifted = EventCreature(evt, 120);
            uplifted.LootGateLevel = 50;

            Assert.IsTrue(WorldEventLootBank.BankKill(uplifted, new DamageHistoryInfo(Named(SeatA, "Alpha"))));
            Assert.AreEqual(0, rolls, "the gate level decides eligibility");
            Assert.AreEqual(1, evt.LootPool.Count, "the profile is still banked");

            // Control: the same creature without a gate level rolls.
            Assert.IsTrue(WorldEventLootBank.BankKill(EventCreature(evt, 120), new DamageHistoryInfo(Named(SeatA, "Alpha"))));
            Assert.AreEqual(1, rolls);
        }

        [TestMethod]
        public void A_closed_pool_refuses_the_bank_and_destroys_the_rare()
        {
            var evt = NewEvent(true);
            evt.LootPool.CloseAndDeal(new[] { SeatA }, 0);

            var rare = Item(40101);
            WorldEventLootBank.HeldRareRoller = (_, _) => rare;

            Assert.IsFalse(WorldEventLootBank.BankKill(EventCreature(evt, 120), new DamageHistoryInfo(Named(SeatA, "Alpha"))), "refused");
            Assert.IsTrue(rare.IsDestroyed, "a refused kill's rare has no home and is destroyed");
            Assert.AreEqual(0, evt.LootPool.Count);
            Assert.AreEqual(0, evt.LootPool.SharesForTest()[SeatA].Rares.Count, "and it never reached a share");
        }

        [TestMethod]
        public void BankKill_with_no_pool_banks_nothing()
        {
            var off = NewEvent(false);
            Assert.IsFalse(WorldEventLootBank.BankKill(EventCreature(off, 120), new DamageHistoryInfo(Named(SeatA, "Alpha"))));
        }
        [TestMethod]
        public void Dispose_destroys_undealt_rares_and_refuses_everything_after()
        {
            var pool = new WorldEventLootPool();
            var rare = Item(40103);

            Assert.IsTrue(pool.TryBank(Record(5, rare, SeatA)));
            Assert.AreEqual(1, pool.Dispose());
            Assert.IsTrue(rare.IsDestroyed);
            Assert.AreEqual(0, pool.Dispose(), "idempotent");
            Assert.IsFalse(pool.TryBank(Record(5)));
            Assert.IsFalse(pool.TryBeginDelivery(SeatA, out _));
        }

        // ------------------------------------------------------------------------------------------------------
        // Rare eligibility reads the GATE level
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Rare_eligibility_uses_the_gate_level_not_the_uplifted_level()
        {
            // Uplifted from 50 to 120. A level-80 player killer: the gate level (50) is below 100 and below the killer,
            // so the kill is NOT eligible; reading Level (120) would make it eligible.
            Assert.IsFalse(WorldEventLootBank.ResolveRareEligible(false, 50, 120, true, true, false, () => 80));
            Assert.IsTrue(Creature.ResolveCanGenerateRare(false, 120, true, true, false, () => 80), "fixture: plain Level would answer true");

            // No gate level recorded (never uplifted): Level is read.
            Assert.IsTrue(WorldEventLootBank.ResolveRareEligible(false, null, 120, true, true, false, () => 80));

            // The gate level still qualifies on its own merits.
            Assert.IsTrue(WorldEventLootBank.ResolveRareEligible(false, 100, 120, true, true, false, () => 200));
            Assert.IsTrue(WorldEventLootBank.ResolveRareEligible(false, 90, 120, true, true, false, () => 80), "gate above the killer");

            // Never for a non-player or Olthoi killer.
            Assert.IsFalse(WorldEventLootBank.ResolveRareEligible(true, 150, 150, true, false, false, () => 1));
            Assert.IsFalse(WorldEventLootBank.ResolveRareEligible(true, 150, 150, true, true, true, () => 1));
        }

        [TestMethod]
        public void BankKill_reads_eligibility_through_the_gate_level()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventLootPool.cs");
            var bank = PooledLootSourceText.MethodBody(src, "public static bool BankKill(Creature creature, DamageHistoryInfo killer)");
            const string Pinned = "ResolveRareEligible(creature.CanGenerateRare, creature.LootGateLevel, creature.Level, killer)";

            StringAssert.Contains(bank, Pinned);
            Assert.IsFalse(bank.Contains("ThreadLootPool.BankKill"), "never the Threads bank, which reads plain Level");

            var helper = PooledLootSourceText.ExpressionBody(src, "internal static bool ResolveRareEligible(bool current, int? lootGateLevel, int? level, bool killerPresent");
            StringAssert.Contains(helper, "Creature.LootGateLevelFor(lootGateLevel, level)");
        }

        // ------------------------------------------------------------------------------------------------------
        // The deal
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Deal_counts_differ_by_at_most_one_and_keep_kill_order()
        {
            for (var seats = 1; seats <= 7; seats++)
            {
                for (var records = 0; records <= 23; records++)
                {
                    for (var offset = 0; offset < seats + 2; offset++)
                    {
                        var list = Enumerable.Range(1, records).Select(i => Record(i)).ToList();
                        var seatList = Enumerable.Range(0, seats).Select(i => 0x50000200u + (uint)i).ToList();

                        var shares = WorldEventLootPool.Deal(list, seatList, offset, new List<WorldObject>());
                        var counts = seatList.Select(s => shares[s].Entries.Count).ToList();

                        Assert.AreEqual(records, counts.Sum(), $"every record dealt ({seats}/{records}/{offset})");
                        Assert.IsTrue(counts.Max() - counts.Min() <= 1, $"equal split ({seats}/{records}/{offset}): {string.Join(",", counts)}");

                        foreach (var seat in seatList)
                        {
                            var tiers = shares[seat].Entries.Select(e => e.Profile.Tier).ToList();
                            CollectionAssert.AreEqual(tiers.OrderBy(t => t).ToList(), tiers, "each share stays in kill order");
                        }
                    }
                }
            }
        }

        [TestMethod]
        public void Deal_is_round_robin_from_the_offset()
        {
            var shares = WorldEventLootPool.Deal(Enumerable.Range(1, 5).Select(i => Record(i)).ToList(), new[] { SeatA, SeatB, SeatC }, 1, new List<WorldObject>());

            CollectionAssert.AreEqual(new[] { 3 }, shares[SeatA].Entries.Select(e => e.Profile.Tier).ToArray());
            CollectionAssert.AreEqual(new[] { 1, 4 }, shares[SeatB].Entries.Select(e => e.Profile.Tier).ToArray());
            CollectionAssert.AreEqual(new[] { 2, 5 }, shares[SeatC].Entries.Select(e => e.Profile.Tier).ToArray());
        }

        [TestMethod]
        public void Rares_reach_the_seat_they_were_rolled_for()
        {
            var rareForC = Item(40200);
            var rareForA = Item(40201);
            var rareForStranger = Item(40202);

            // Record 1 is dealt to A, but its rare was rolled for C; record 2 is dealt to B, its rare is A's; record 3's
            // rare names nobody seated, so it follows the record (dealt to C).
            var records = new List<ThreadLootLedgerEntry>
            {
                Record(1, rareForC, SeatC),
                Record(2, rareForA, SeatA),
                Record(3, rareForStranger, 0x5000FFFFu),
            };

            var shares = WorldEventLootPool.Deal(records, new[] { SeatA, SeatB, SeatC }, 0, new List<WorldObject>());

            CollectionAssert.AreEqual(new[] { rareForA }, shares[SeatA].Rares.Select(r => r.Rare).ToArray());
            Assert.AreEqual(0, shares[SeatB].Rares.Count);
            CollectionAssert.AreEquivalent(new[] { rareForC, rareForStranger }, shares[SeatC].Rares.Select(r => r.Rare).ToArray());

            Assert.IsTrue(shares.Values.SelectMany(s => s.Entries).All(e => e.HeldRare == null), "the rare is split off the dealt record");
            Assert.AreEqual(3, shares.Values.Sum(s => s.Entries.Count), "the profile half is still dealt");
            Assert.AreEqual("Finder", shares[SeatA].Rares.Single().FinderName);
        }

        [TestMethod]
        public void No_seats_destroys_every_rare_at_the_deal()
        {
            var pool = new WorldEventLootPool();
            var rare = Item(40210);

            pool.TryBank(Record(4, rare, SeatA));
            pool.TryBank(Record(4));

            var summary = pool.CloseAndDeal(Array.Empty<uint>(), 0);

            Assert.AreEqual(2, summary.Records);
            Assert.AreEqual(0, summary.Seats);
            Assert.AreEqual(1, summary.RaresDestroyed);
            Assert.IsTrue(rare.IsDestroyed);
            Assert.IsFalse(pool.TryBeginDelivery(SeatA, out _), "nobody has a share");
        }

        [TestMethod]
        public void Delivery_states_begin_hand_back_and_finish()
        {
            var pool = new WorldEventLootPool();
            pool.TryBank(Record(4));
            pool.CloseAndDeal(new[] { SeatA, SeatB }, 0);

            Assert.IsTrue(pool.TryBeginDelivery(SeatA, out var share));
            Assert.AreEqual(SeatA, share.Seat);
            Assert.IsFalse(pool.TryBeginDelivery(SeatA, out _), "a share in flight cannot start twice");

            Assert.IsTrue(pool.EndDelivery(SeatA, false), "handed back");
            Assert.AreEqual(WorldEventShareState.Pending, pool.ShareState(SeatA));
            Assert.IsTrue(pool.TryBeginDelivery(SeatA, out _), "a handed-back share retries");
            Assert.AreEqual(2, share.Attempts);

            Assert.IsTrue(pool.EndDelivery(SeatA, true));
            Assert.AreEqual(WorldEventShareState.Delivered, pool.ShareState(SeatA));
            Assert.IsFalse(pool.TryBeginDelivery(SeatA, out _), "delivered is terminal");

            Assert.IsFalse(pool.TryBeginDelivery(SeatB, out _), "an empty share never starts");
            Assert.AreEqual(WorldEventShareState.Delivered, pool.ShareState(SeatB));
            Assert.IsNull(pool.ShareState(SeatC), "no seat, no share");
        }

        [TestMethod]
        public void Dispose_destroys_pending_shares_but_leaves_one_in_flight_to_its_chain()
        {
            var pool = new WorldEventLootPool();
            var rareA = Item(40300);
            var rareB = Item(40301);

            pool.TryBank(Record(4, rareA, SeatA));
            pool.TryBank(Record(4, rareB, SeatB));
            pool.CloseAndDeal(new[] { SeatA, SeatB }, 0);

            Assert.IsTrue(pool.TryBeginDelivery(SeatB, out var inFlight));

            pool.Dispose();

            Assert.IsTrue(rareA.IsDestroyed, "an unclaimed share vanishes, its rare with it");
            Assert.IsFalse(rareB.IsDestroyed, "the chain owns a share in flight");
            Assert.IsFalse(pool.EndDelivery(SeatB, false), "the chain is told the pool is gone, and destroys what it holds");
            Assert.AreSame(rareB, inFlight.Rares.Single().Rare);
        }

        [TestMethod]
        public void Finish_deals_to_ledger_seats_and_an_abort_in_the_window_disposes()
        {
            var evt = NewEvent(true);
            var rare = Item(40400);

            evt.Participation.CreditDamageForTest(SeatA, 10);
            evt.Participation.CreditKillForTest(SeatB);
            evt.CreditCombatContact(SeatC, 0, null, "Contact"); // not Active: refused, so C has no record

            evt.LootPool.TryBank(Record(3, rare, SeatA));
            evt.LootPool.TryBank(Record(3));
            evt.LootPool.TryBank(Record(3));

            evt.Finish(WorldEventOutcome.Success);

            Assert.AreEqual(WorldEventState.Rewarding, evt.State);
            Assert.IsTrue(evt.LootPool.IsClosed);

            var shares = evt.LootPool.SharesForTest();
            CollectionAssert.AreEquivalent(new[] { SeatA, SeatB }, shares.Keys.ToArray(), "seats are the ledger's records");
            Assert.AreEqual(3, shares.Values.Sum(s => s.Entries.Count));
            Assert.AreSame(rare, shares[SeatA].Rares.Single().Rare);

            evt.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.AreEqual(WorldEventState.Done, evt.State);
            Assert.IsTrue(evt.LootPool.IsDisposed);
            Assert.IsTrue(rare.IsDestroyed, "an unclaimed share's rare vanishes at window end");
        }

        [TestMethod]
        public void An_aborted_run_disposes_the_undealt_bank()
        {
            var evt = NewEvent(true);
            var rare = Item(40401);
            evt.Participation.CreditDamageForTest(SeatA, 10);
            evt.LootPool.TryBank(Record(3, rare, SeatA));

            evt.Finish(WorldEventOutcome.AbortedShutdown);

            Assert.IsTrue(evt.LootPool.IsDisposed);
            Assert.IsTrue(rare.IsDestroyed);
            Assert.IsNull(evt.LootPool.ShareState(SeatA), "an aborted run deals nothing");
        }

        // ------------------------------------------------------------------------------------------------------
        // The fill step
        // ------------------------------------------------------------------------------------------------------

        private sealed class FillHarness
        {
            public double Clock;
            public double StepStart;
            public readonly List<string> Placed = new List<string>();
            public readonly List<string> Refused = new List<string>();
            public int Capacity = 120;
            public double MsPerEntry = 5;

            public ThreadLootRollBudget Budget(int rolls, double ms)
            {
                StepStart = Clock;
                return new ThreadLootRollBudget(rolls, () => Clock - StepStart, ms);
            }

            public WorldEventShareFill.StepStop Step(List<string> prebuilt, List<ThreadLootLedgerEntry> entries, ThreadLootRollBudget budget)
                => WorldEventShareFill.RunStep(prebuilt, entries,
                    e => { Clock += MsPerEntry; return new List<string> { $"t{e.Profile.Tier}a", $"t{e.Profile.Tier}b" }; },
                    item => { if (Placed.Count >= Capacity) return false; Placed.Add(item); return true; },
                    () => Placed.Count >= Capacity, Refused, budget);
        }

        [TestMethod]
        public void Fill_step_stops_on_the_time_budget_and_the_next_step_resumes()
        {
            var h = new FillHarness();
            var entries = Enumerable.Range(1, 5).Select(i => Record(i)).ToList();
            var prebuilt = new List<string> { "rare" };

            // 5 ms per entry against an 8 ms budget: the first entry is always admitted, the second crosses 8 ms.
            Assert.AreEqual(WorldEventShareFill.StepStop.Exhausted, h.Step(prebuilt, entries, h.Budget(12, 8)));
            CollectionAssert.AreEqual(new[] { "rare", "t1a", "t1b", "t2a", "t2b" }, h.Placed, "prebuilt first, then kill order");
            Assert.AreEqual(3, entries.Count, "the rest waits for the next tick");

            Assert.AreEqual(WorldEventShareFill.StepStop.Exhausted, h.Step(prebuilt, entries, h.Budget(12, 8)));
            Assert.AreEqual(1, entries.Count);

            Assert.AreEqual(WorldEventShareFill.StepStop.Drained, h.Step(prebuilt, entries, h.Budget(12, 8)));
            Assert.AreEqual(11, h.Placed.Count);
            CollectionAssert.AreEqual(new[] { "t5a", "t5b" }, h.Placed.Skip(9).ToArray(), "resumed exactly where it stopped");
        }

        [TestMethod]
        public void Fill_step_obeys_the_roll_cap_when_the_clock_does_not_move()
        {
            var h = new FillHarness { MsPerEntry = 0 };
            var entries = Enumerable.Range(1, 7).Select(i => Record(i)).ToList();

            Assert.AreEqual(WorldEventShareFill.StepStop.Exhausted, h.Step(new List<string>(), entries, h.Budget(3, 8)));
            Assert.AreEqual(4, entries.Count, "three rolls, then yield");
        }

        [TestMethod]
        public void Fill_step_stops_at_capacity_and_reports_the_remainder()
        {
            var h = new FillHarness { Capacity = 3, MsPerEntry = 0 };
            var entries = Enumerable.Range(1, 4).Select(i => Record(i)).ToList();

            Assert.AreEqual(WorldEventShareFill.StepStop.Full, h.Step(new List<string>(), entries, h.Budget(100, 8)));
            CollectionAssert.AreEqual(new[] { "t1a", "t1b", "t2a" }, h.Placed);
            CollectionAssert.AreEqual(new[] { "t2b" }, h.Refused, "the item that did not fit is remainder, never a fourth slot");
            Assert.AreEqual(2, entries.Count, "unrolled entries are remainder too");
        }

        // ------------------------------------------------------------------------------------------------------
        // Wiring pins (source)
        // ------------------------------------------------------------------------------------------------------

        private const string HandleSignature = "private static bool Handle(WorldObject cache, Player player)";
        private const string Deliver = "WorldEventShareDelivery.TryDeliver(evt, cache, player);";
        private const string RetryGuard = "if (denial == ClaimDenial.AlreadyClaimedCharacter)\n                    " + Deliver;
        private const string GrantedLog = "result=granted payout=";

        /// <summary>
        /// The claim handler's delivery wiring: exactly two TryDeliver calls, neither inside lock (evt.ClaimedCharacters);
        /// one after the granted claim line, one behind the AlreadyClaimedCharacter retry guard.
        /// </summary>
        private static string HandlerFault(string body)
        {
            var lockAt = body.IndexOf("lock (evt.ClaimedCharacters)", StringComparison.Ordinal);

            if (lockAt < 0)
                return "no claim lock";

            var open = body.IndexOf('{', lockAt);
            var depth = 0;
            var close = -1;

            for (var i = open; i < body.Length; i++)
            {
                if (body[i] == '{')
                    depth++;
                else if (body[i] == '}' && --depth == 0)
                {
                    close = i;
                    break;
                }
            }

            if (close < 0)
                return "unbalanced claim lock";

            var calls = new List<int>();

            for (var at = body.IndexOf(Deliver, StringComparison.Ordinal); at >= 0; at = body.IndexOf(Deliver, at + 1, StringComparison.Ordinal))
                calls.Add(at);

            if (calls.Count != 2)
                return $"expected two TryDeliver calls, found {calls.Count}";

            if (calls.Any(c => c > lockAt && c < close))
                return "a TryDeliver call sits inside the claim lock";

            var granted = body.IndexOf(GrantedLog, StringComparison.Ordinal);

            if (granted < 0 || !calls.Any(c => c > granted))
                return "no delivery after the granted claim";

            if (body.IndexOf(RetryGuard, StringComparison.Ordinal) < 0)
                return "the retry is not behind the AlreadyClaimedCharacter guard";

            return null;
        }

        [TestMethod]
        public void Delivery_runs_outside_the_claim_lock()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventCacheHandler.cs"), HandleSignature);

            Assert.IsNull(HandlerFault(body));
        }

        [TestMethod]
        public void Delivery_pin_discriminates()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventCacheHandler.cs"), HandleSignature);
            const string Mark = "evt.ClaimedCharacters.Add(characterGuid);";
            Assert.IsTrue(body.Contains(Mark), "fixture: the mark is inside the lock");

            // A delivery moved INTO the critical section.
            var moved = body.Replace(Mark, Mark + "\n" + Deliver);
            var lastDeliver = moved.LastIndexOf(Deliver, StringComparison.Ordinal);
            moved = moved.Remove(lastDeliver, Deliver.Length);
            Assert.IsNotNull(HandlerFault(moved), "a delivery inside the lock must fail the pin");

            // The granted delivery removed.
            var granted = body.IndexOf(GrantedLog, StringComparison.Ordinal);
            var after = body.IndexOf(Deliver, granted, StringComparison.Ordinal);
            Assert.IsNotNull(HandlerFault(body.Remove(after, Deliver.Length)), "no granted delivery must fail the pin");

            // The retry behind another denial.
            Assert.IsNotNull(HandlerFault(body.Replace("ClaimDenial.AlreadyClaimedCharacter)\n", "ClaimDenial.AlreadyClaimedAccount)\n")),
                "a retry on a gate denial must fail the pin");
        }

        private const string DieSignature = "protected virtual void Die(DamageHistoryInfo lastDamager, DamageHistoryInfo topDamager)";
        private const string EventDecide = "var eventPooledLoot = ACE.Server.WorldEvents.WorldEventHooks.IsPooledLootDeath(this);";
        private const string EventBank = "ACE.Server.WorldEvents.WorldEventHooks.BankPooledKill(this, topDamager);";
        private const string EventFallback = "eventPooledLoot = false;";

        /// <summary>Die: decided once, banked inside a try whose catch falls back to a corpse, OR-ed into suppressCorpse.</summary>
        private static string DieFault(string body)
        {
            var decide = body.IndexOf(EventDecide, StringComparison.Ordinal);
            var bank = body.IndexOf(EventBank, StringComparison.Ordinal);

            if (decide < 0 || bank < decide)
                return "the bank is not behind the decision";

            var tryAt = body.IndexOf("try", decide, bank - decide, StringComparison.Ordinal);
            var catchAt = body.IndexOf("catch (Exception", bank, StringComparison.Ordinal);
            var fallback = catchAt < 0 ? -1 : body.IndexOf(EventFallback, catchAt, StringComparison.Ordinal);
            var catchEnd = catchAt < 0 ? -1 : body.IndexOf("}", catchAt, StringComparison.Ordinal);

            if (tryAt < 0 || catchAt < 0)
                return "the bank is not guarded";

            if (fallback < 0 || fallback > catchEnd)
                return "a throwing bank does not fall back to a corpse";

            var suppress = body.IndexOf("var suppressCorpse = ", StringComparison.Ordinal);
            var suppressEnd = suppress < 0 ? -1 : body.IndexOf(';', suppress);

            if (suppress < catchAt || suppressEnd < 0 || !body.Substring(suppress, suppressEnd - suppress).Contains("|| eventPooledLoot"))
                return "eventPooledLoot is not OR-ed into suppressCorpse after the bank";

            return null;
        }

        [TestMethod]
        public void Die_banks_event_kills_and_falls_back_to_a_corpse_on_a_throw()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Creature_Death.cs"), DieSignature);

            Assert.IsNull(DieFault(body));

            Assert.IsNotNull(DieFault(body.Replace(EventFallback, "")), "removing the fallback must fail the pin");
            Assert.IsNotNull(DieFault(body.Replace("|| eventPooledLoot ", "")), "dropping the OR must fail the pin");
            Assert.IsNotNull(DieFault(body.Replace(EventDecide, "var eventPooledLoot = IsPooledLootDeath(P_DungeonRun, GetProperty(PropertyInt.WorldEventId));")),
                "a different predicate must fail the pin");
        }

        private const string FinishSignature = "public void Finish(WorldEventOutcome outcome)";
        private const string CleanupSignature = "private void CompleteCleanup(double now, string reason)";

        private static string LifecycleFault(string finish, string cleanup, string source)
        {
            var rewarding = finish.IndexOf("Transition(WorldEventState.Rewarding", StringComparison.Ordinal);
            var deal = finish.IndexOf("DealLootPool();", StringComparison.Ordinal);
            var caches = finish.IndexOf("WorldEventRewardDelivery.SpawnCaches(this);", StringComparison.Ordinal);
            var creatures = finish.IndexOf("DestroyEventCreatures();", StringComparison.Ordinal);

            if (deal < 0 || deal < rewarding || deal > caches || deal > creatures)
                return "the deal is not on the Rewarding edge ahead of the caches";

            var dispose = cleanup.IndexOf("DisposeLootPool();", StringComparison.Ordinal);
            var transition = cleanup.IndexOf("Transition(WorldEventState.Cleanup", StringComparison.Ordinal);

            if (dispose < 0 || dispose > transition)
                return "the pool is not disposed ahead of the cleanup transition";

            var active = source.IndexOf("Transition(WorldEventState.Active", StringComparison.Ordinal);
            var latch = source.IndexOf("LatchPooledLoot(ReadPooledLootSwitch());", StringComparison.Ordinal);
            var becameActive = source.IndexOf("OnBecameActive(now);", StringComparison.Ordinal);

            if (latch < active || latch > becameActive)
                return "the latch is not on the Active edge ahead of the first spawn";

            return null;
        }

        [TestMethod]
        public void The_run_latches_at_Active_deals_at_Rewarding_and_disposes_at_cleanup()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEvent.cs");
            var finish = PooledLootSourceText.MethodBody(source, FinishSignature);
            var cleanup = PooledLootSourceText.MethodBody(source, CleanupSignature);

            Assert.IsNull(LifecycleFault(finish, cleanup, source));

            Assert.IsNotNull(LifecycleFault(finish.Replace("DealLootPool();", ""), cleanup, source), "no deal must fail the pin");
            Assert.IsNotNull(LifecycleFault(finish, cleanup.Replace("DisposeLootPool();", ""), source), "no dispose must fail the pin");
            Assert.IsNotNull(LifecycleFault(finish, cleanup, source.Replace("LatchPooledLoot(ReadPooledLootSwitch());", "")), "no latch must fail the pin");
        }

        [TestMethod]
        public void The_share_chest_is_held_like_a_cache_but_is_never_a_cache()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/WorldEvents/WorldEventShareDelivery.cs");

            Assert.IsFalse(src.Contains("SetProperty(PropertyBool.WorldEventCache"), "9027 would make the chest dispatch claims");
            Assert.IsFalse(src.Contains(".Adopt("), "Adopt refuses after the creature pass; the window is always after it");
            StringAssert.Contains(src, "AddHeld = wo => evt.AddHeld(wo),");
            StringAssert.Contains(src, "MarkCacheLifetime = guid => evt.AddCacheDecor(guid),");
            StringAssert.Contains(src, "chest.P_DungeonCacheOwnerGuid = ownerGuid;");
            StringAssert.Contains(src, "chest.SetProperty(PropertyFloat.ResetInterval, double.PositiveInfinity);");

            // The ordering (delivered only once in the world and held) is driven behaviourally in WorldEventShareChainTests.
        }
    }
}
