using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.ThreadLootTestFixtures;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads Task 10: the deal (ruling R19), personal piles, per-member delivery and its retry flags (R20), the
    /// pile round counts (R21), and the solo placer's untouched text. A Player cannot be built in this harness, so the
    /// delivery pass is driven through ThreadGroupCacheDelivery.Run's DeliveryWorld seams with real Containers and
    /// Chests, and only the live wiring is pinned on source.
    ///
    /// No PropertyManager key is read by any driven path: GroupScaling.Compute takes every tunable as an argument, and
    /// the materialiser and bonus builder are seams.
    /// </summary>
    [TestClass]
    public class GroupCacheDeliveryTests
    {
        private const uint Owner = 0x50000010u;
        private const uint Member = 0x50000020u;
        private const uint Member2 = 0x50000030u;

        // ------------------------------------------------------------------------------------------------------
        // Fixtures
        // ------------------------------------------------------------------------------------------------------

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>
        /// A pooled, CLEARED group run of Owner plus the given members, at the default tunables. Every member holds a
        /// key, so every roster member holds a deal seat (ruling R32); <see cref="KeylessGroupRun"/> is the exception.
        /// </summary>
        private static ThreadDungeonRun ClearedGroupRun(params uint[] others)
        {
            var run = KeylessGroupRun(others);

            foreach (var other in others)
                run.SetMemberKey(other, 0x80001000u + (other & 0xFFFu));

            return run;
        }

        /// <summary>A pooled, CLEARED group run whose non-owner members hold NO key.</summary>
        private static ThreadDungeonRun KeylessGroupRun(params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80005678u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            run.MarkPooledLoot(true);
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: the run must be Cleared");
            Assert.IsTrue(run.IsGroup, "fixture: the run must be a group run");
            return run;
        }

        private static WorldObject Valued(int value)
        {
            var item = Item();
            item.Value = value;
            return item;
        }

        /// <summary>A Stackable of the given wcid/size, at unit value 5 and unit burden 10 (ThreadLootStackingTests' Stack shape).</summary>
        private static Stackable StackedValued(uint wcid, int size, int unitValue = 5, int unitBurden = 10, int max = 1000)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid,
                WeenieType = WeenieType.Stackable,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.StackSize, size },
                    { PropertyInt.MaxStackSize, max },
                    { PropertyInt.StackUnitEncumbrance, unitBurden },
                    { PropertyInt.StackUnitValue, unitValue },
                },
            };

            return new Stackable(weenie, new ObjectGuid(NextGuid()));
        }

        private static ThreadLootLedgerEntry Entry(WorldObject rare = null, uint recipient = 0)
            => new ThreadLootLedgerEntry(false, false, null, null, rare, rare == null ? null : "Finder", 1, recipient);

        /// <summary>A registered Thread Cache chest stamped for <paramref name="ownerGuid"/>.</summary>
        private static Chest OwnedChest(ThreadDungeonRun run, uint ownerGuid, int capacity = 120)
        {
            var chest = new Chest(new Weenie
            {
                WeenieClassId = 1003603,
                WeenieType = WeenieType.Chest,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemsCapacity, capacity } },
            }, new ObjectGuid(NextGuid()));

            chest.P_DungeonCacheOwnerGuid = ownerGuid;
            Assert.IsTrue(run.TryRegisterCache(chest));
            return chest;
        }

        private static List<CacheCandidate> Candidates(int n)
            => Enumerable.Range(0, n).Select(i => new CacheCandidate(CacheCandidateStage.Ring, 0x01500100, i, 0f, 0f, 0f, 1f, true)).ToList();

        private static Func<ThreadLootLedgerEntry, List<WorldObject>> Materializer(List<WorldObject> created, int perEntry = 3)
            => e =>
            {
                var items = new List<WorldObject>();
                for (var i = 0; i < perEntry; i++)
                {
                    var item = Valued(100 + created.Count);
                    created.Add(item);
                    items.Add(item);
                }

                // MaterializeEntry's shape: the held rare, once, last.
                if (e.HeldRare != null)
                    items.Add(e.HeldRare);

                return items;
            };

        private static readonly Func<ThreadDungeonRun, double, List<WorldObject>> NoBonus = (r, b) => throw new AssertFailedException("no bonus is pending");

        private static readonly Func<ThreadDungeonRun, double, int, List<WorldObject>> NoPooledRolls =
            (r, f, c) => throw new AssertFailedException("no pooled boss rolls are pending");

        /// <summary>The world of a pass, with every presence and chat seam recorded.</summary>
        private sealed class FakeWorld
        {
            public readonly HashSet<uint> Inside = new HashSet<uint>();
            public readonly List<(uint Guid, string Text)> Told = new List<(uint, string)>();
            public readonly List<uint> FormCalls = new List<uint>();
            public readonly List<uint> Moves = new List<uint>();
            public readonly List<Container> Delivered = new List<Container>();
            public Func<uint, Func<bool>, CacheFormPass> FormAt;

            public ThreadGroupCacheDelivery.DeliveryWorld Build(ThreadDungeonRun run, Func<ThreadLootLedgerEntry, List<WorldObject>> materialize = null,
                Func<ThreadDungeonRun, double, List<WorldObject>> bonus = null)
            {
                return new ThreadGroupCacheDelivery.DeliveryWorld
                {
                    IsInside = g => Inside.Contains(g),
                    FormAt = (g, hasMore) => { FormCalls.Add(g); return FormAt(g, hasMore); },
                    MoveFor = g => Moves.Add(g),
                    Tell = (g, t) => Told.Add((g, t)),
                    Deliver = (c, r) => Delivered.Add(c),
                    Materialize = materialize ?? (_ => new List<WorldObject>()),
                    BuildBonus = bonus ?? NoBonus,
                };
            }
        }

        /// <summary>A FormAt that forms real chests for the member and fills them from the member's pile.</summary>
        private static Func<uint, Func<bool>, CacheFormPass> FormsChests(ThreadDungeonRun run, List<Chest> formed, int capacity = 120)
            => (g, hasMore) => ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, hasMore, () => Candidates(1),
                _ => { var c = OwnedChest(run, g, capacity); formed.Add(c); return c; },
                (r, c) => ThreadGroupCacheDelivery.FillFromPile(r, c, g),
                (c, res) => { });

        private static List<WorldObject> PileItems(ThreadDungeonRun run, uint guid)
        {
            var taken = run.TakeHeldPile(guid);
            foreach (var held in taken)
                Assert.IsTrue(run.AddToHeldPile(guid, held), "put the pile back");
            return taken.Select(h => h.Item).ToList();
        }

        private static List<HeldPileItem> PileHeld(ThreadDungeonRun run, uint guid)
        {
            var taken = run.TakeHeldPile(guid);
            foreach (var held in taken)
                Assert.IsTrue(run.AddToHeldPile(guid, held), "put the pile back");
            return taken;
        }

        // ------------------------------------------------------------------------------------------------------
        // SnakeDeal
        // ------------------------------------------------------------------------------------------------------

        private static List<List<long>> Snake(int seats, params long[] values)
            => ThreadGroupCacheDelivery.SnakeDeal(values, v => v, seats);

        [TestMethod]
        public void SnakeDeal_two_seats_alternates_first_pick()
        {
            var hands = Snake(2, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

            Assert.AreEqual(2, hands.Count);
            CollectionAssert.AreEqual(new long[] { 10, 7, 6, 3, 2 }, hands[0]);
            CollectionAssert.AreEqual(new long[] { 9, 8, 5, 4, 1 }, hands[1]);
        }

        [TestMethod]
        public void SnakeDeal_three_seats_turns_back_at_the_end_of_each_lap()
        {
            var hands = Snake(3, 1, 2, 3, 4, 5, 6, 7, 8, 9);

            CollectionAssert.AreEqual(new long[] { 9, 4, 3 }, hands[0]);
            CollectionAssert.AreEqual(new long[] { 8, 5, 2 }, hands[1]);
            CollectionAssert.AreEqual(new long[] { 7, 6, 1 }, hands[2]);
        }

        [TestMethod]
        public void SnakeDeal_four_seats_balances_an_even_run_exactly()
        {
            var hands = Snake(4, 1, 2, 3, 4, 5, 6, 7, 8);

            CollectionAssert.AreEqual(new long[] { 8, 1 }, hands[0]);
            CollectionAssert.AreEqual(new long[] { 7, 2 }, hands[1]);
            CollectionAssert.AreEqual(new long[] { 6, 3 }, hands[2]);
            CollectionAssert.AreEqual(new long[] { 5, 4 }, hands[3]);
            Assert.IsTrue(hands.All(h => h.Sum() == 9));
        }

        [TestMethod]
        public void SnakeDeal_is_stable_for_equal_values_and_handles_edge_inputs()
        {
            var items = new[] { ("a", 5L), ("b", 5L), ("c", 5L), ("d", 9L) };
            var hands = ThreadGroupCacheDelivery.SnakeDeal(items, i => i.Item2, 2);

            // Sorted: d(9), a, b, c (input order kept among the 5s). Seats 0, 1, 1, 0.
            CollectionAssert.AreEqual(new[] { "d", "c" }, hands[0].Select(i => i.Item1).ToList());
            CollectionAssert.AreEqual(new[] { "a", "b" }, hands[1].Select(i => i.Item1).ToList());

            Assert.AreEqual(0, ThreadGroupCacheDelivery.SnakeDeal(new long[] { 1, 2 }, v => v, 0).Count, "no seats, no hands");
            var empty = ThreadGroupCacheDelivery.SnakeDeal<long>(null, v => v, 3);
            Assert.AreEqual(3, empty.Count);
            Assert.IsTrue(empty.All(h => h.Count == 0));
            CollectionAssert.AreEqual(new long[] { 3, 2, 1 }, Snake(1, 1, 3, 2)[0], "one seat takes everything, highest first");
        }

        [TestMethod]
        public void SnakeDeal_spreads_hand_values_within_the_largest_item()
        {
            var random = new Random(20260917);
            var values = Enumerable.Range(0, 97).Select(_ => (long)random.Next(1, 50000)).ToArray();

            foreach (var seats in new[] { 2, 3, 4, 6, 9 })
            {
                var hands = Snake(seats, values);

                Assert.AreEqual(values.Length, hands.Sum(h => h.Count), $"{seats} seats: every item dealt once");
                CollectionAssert.AreEquivalent(values, hands.SelectMany(h => h).ToArray(), $"{seats} seats: the same items");
                Assert.IsTrue(hands.Max(h => h.Count) - hands.Min(h => h.Count) <= 1, $"{seats} seats: counts differ by at most one");

                var spread = hands.Max(h => h.Sum()) - hands.Min(h => h.Sum());
                Assert.IsTrue(spread <= values.Max(), $"{seats} seats: value spread {spread} exceeds the largest item {values.Max()}");
            }
        }

        // ------------------------------------------------------------------------------------------------------
        // Deal
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Deal_conserves_every_item_into_exactly_one_pile()
        {
            var run = ClearedGroupRun(Member, Member2);
            var created = new List<WorldObject>();
            var bonusItems = new List<WorldObject>();

            for (var i = 0; i < 5; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var carried1 = Valued(7);
            var carried2 = Valued(70000);
            Assert.IsTrue(run.TryAddOverflow(carried1, null));
            Assert.IsTrue(run.TryAddOverflow(carried2, null));
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var round = ThreadGroupCacheDelivery.Deal(run, Materializer(created), (r, b) =>
            {
                var items = new List<WorldObject> { Valued(5000), Valued(6000) };
                bonusItems.AddRange(items);
                return items;
            });

            Assert.AreEqual(1, round);
            Assert.AreEqual(0, run.LedgerCount);
            Assert.AreEqual(0, run.OverflowCount);
            Assert.IsFalse(run.IsLootBonusPending);
            Assert.IsFalse(run.IsPlacementWanted, "nothing left undealt");

            var all = created.Concat(new[] { carried1, carried2 }).Concat(bonusItems).ToList();
            var piles = run.Roster.Select(m => PileItems(run, m.Guid)).ToList();
            var piled = piles.SelectMany(p => p).ToList();

            Assert.AreEqual(15 + 2 + 6, all.Count);
            Assert.AreEqual(all.Count, piled.Count, "every item piled exactly once");
            CollectionAssert.AreEquivalent(all, piled);
            Assert.AreEqual(piled.Count, piled.Distinct().Count(), "no item in two piles");
            Assert.IsTrue(all.All(i => !i.IsDestroyed));
            Assert.IsTrue(run.Roster.All(m => PileHeld(run, m.Guid).All(h => h.Round == 1)), "every item carries the deal round");
            Assert.IsTrue(run.HasUnclaimedLoot, "held piles are unclaimed loot");
        }

        [TestMethod]
        public void Deal_sends_a_rare_to_its_recipient_and_deals_one_without_a_roster_recipient()
        {
            var run = ClearedGroupRun(Member, Member2);
            var toMember2 = Valued(1);
            var noRecipient = Valued(2);
            var offRoster = Valued(3);
            var omitted = Valued(4);

            Assert.IsTrue(run.TryAppendLootEntry(Entry(toMember2, Member2)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry(noRecipient, 0)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry(offRoster, 0x50000099u)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry(omitted, Member)));

            var created = new List<WorldObject>();
            var materialize = Materializer(created, perEntry: 0);

            // The fourth entry's materialisation leaves the rare out; it still owes the rare a home.
            Func<ThreadLootLedgerEntry, List<WorldObject>> leavesOutLast = e => ReferenceEquals(e.HeldRare, omitted) ? new List<WorldObject>() : materialize(e);

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, leavesOutLast, NoBonus));

            var member2 = PileHeld(run, Member2);
            var member = PileHeld(run, Member);
            var owner = PileHeld(run, Owner);

            var rare = member2.Single(h => ReferenceEquals(h.Item, toMember2));
            Assert.AreEqual("Finder", rare.RareFinderName, "the finder name travels with the rare");
            Assert.IsTrue(member.Any(h => ReferenceEquals(h.Item, omitted)), "an omitted rare still reaches its recipient");

            var all = owner.Concat(member).Concat(member2).Select(h => h.Item).ToList();
            Assert.AreEqual(1, all.Count(i => ReferenceEquals(i, noRecipient)), "a rare with no recipient is dealt once");
            Assert.AreEqual(1, all.Count(i => ReferenceEquals(i, offRoster)), "a recipient off the roster is dealt once");
            Assert.AreEqual(4, all.Count);
        }

        [TestMethod]
        public void Deal_builds_the_boss_bonus_once_per_member_with_B_into_that_members_pile()
        {
            var run = ClearedGroupRun(Member, Member2);
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var calls = new List<double>();
            var built = new List<List<WorldObject>>();

            ThreadGroupCacheDelivery.Deal(run, _ => throw new AssertFailedException("no ledger entry"), (r, b) =>
            {
                Assert.AreSame(run, r);
                calls.Add(b);
                var items = new List<WorldObject> { Valued(10), Valued(20), Valued(30) };
                built.Add(items);
                return items;
            });

            Assert.AreEqual(3, calls.Count, "one bonus per roster member");
            Assert.IsTrue(calls.All(b => b == run.Group.RewardBonus), "every bonus is built with B");
            Assert.AreEqual(1.1, run.Group.RewardBonus, 1e-9, "N = 3 at the defaults");

            // Built in roster order, each straight into that member's pile, not dealt.
            CollectionAssert.AreEqual(built[0], PileItems(run, Owner));
            CollectionAssert.AreEqual(built[1], PileItems(run, Member));
            CollectionAssert.AreEqual(built[2], PileItems(run, Member2));

            // No bonus pending: the builder is not called at all.
            var fresh = ClearedGroupRun(Member);
            Assert.IsTrue(fresh.TryAppendLootEntry(Entry()));
            ThreadGroupCacheDelivery.Deal(fresh, Materializer(new List<WorldObject>()), NoBonus);
        }

        [TestMethod]
        public void Deal_claims_at_most_sixty_entries_and_marks_the_rest_wanted()
        {
            var run = ClearedGroupRun(Member);
            var materialised = 0;

            for (var i = 0; i < 70; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            Func<ThreadLootLedgerEntry, List<WorldObject>> counting = e => { materialised++; return new List<WorldObject> { Valued(1) }; };

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, counting, NoBonus));
            Assert.AreEqual(ThreadGroupCacheDelivery.MaxEntriesPerDeal, materialised);
            Assert.AreEqual(10, run.LedgerCount, "the rest waits");
            Assert.IsTrue(run.IsPlacementWanted, "left-over ledger sets the run-level flag");

            // The next deal claims the flag and the rest, and leaves nothing wanted.
            Assert.AreEqual(2, ThreadGroupCacheDelivery.Deal(run, counting, NoBonus));
            Assert.AreEqual(70, materialised);
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsFalse(run.IsPlacementWanted);
            Assert.AreEqual(70, run.Roster.Sum(m => PileItems(run, m.Guid).Count));
        }

        [TestMethod]
        public void Deal_with_nothing_to_deal_consumes_no_round_and_clears_the_placement_flag()
        {
            var run = ClearedGroupRun(Member);
            run.MarkPlacementWanted();

            Assert.AreEqual(0, ThreadGroupCacheDelivery.Deal(run, _ => throw new AssertFailedException("nothing to materialise"), NoBonus));
            Assert.IsFalse(run.IsPlacementWanted);
            Assert.IsFalse(run.AnyHeldPile);

            Assert.IsTrue(run.TryAppendLootEntry(Entry()));
            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, Materializer(new List<WorldObject>()), NoBonus), "the first real deal is round 1");
        }

        [TestMethod]
        public void Deal_on_an_ended_run_destroys_what_no_pile_accepts()
        {
            var run = ClearedGroupRun(Member);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));
            var created = new List<WorldObject>();
            run.MarkEnded("test");

            ThreadGroupCacheDelivery.Deal(run, Materializer(created), NoBonus);

            Assert.AreEqual(3, created.Count);
            Assert.IsTrue(created.All(i => i.IsDestroyed), "an Ended run refuses the pile, so the item is destroyed, not stranded");
            Assert.IsFalse(run.AnyHeldPile);
        }

        [TestMethod]
        public void Deal_keeps_a_held_rare_when_materialising_its_entry_throws()
        {
            var run = ClearedGroupRun(Member);
            var rare = Valued(9);
            Assert.IsTrue(run.TryAppendLootEntry(Entry(rare, Member)));

            ThreadGroupCacheDelivery.Deal(run, _ => throw new InvalidOperationException("roll failed"), NoBonus);

            Assert.IsFalse(rare.IsDestroyed);
            CollectionAssert.AreEqual(new[] { rare }, PileItems(run, Member));
        }

        // ------------------------------------------------------------------------------------------------------
        // Final review F2 (ruling R32): keyless members get no deal seat
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_keyless_seat_receives_nothing_and_the_other_seats_split_everything()
        {
            var run = KeylessGroupRun(Member, Member2);
            run.SetMemberKey(Member2, 0x80001030u);
            CollectionAssert.AreEqual(new[] { Owner, Member2 }, run.DealSeats().ToArray(), "Member holds no key");

            for (var i = 0; i < 4; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var created = new List<WorldObject>();
            var bonusCalls = 0;

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, Materializer(created), (r, b) =>
            {
                bonusCalls++;
                var items = new List<WorldObject> { Valued(5000) };
                created.AddRange(items);
                return items;
            }));

            Assert.AreEqual(2, bonusCalls, "one boss bonus per SEAT, not per roster member");
            Assert.IsFalse(run.HasHeldPile(Member), "the keyless member is dealt nothing");

            var owner = PileItems(run, Owner);
            var member2 = PileItems(run, Member2);
            Assert.AreEqual(12 + 2, created.Count);
            CollectionAssert.AreEquivalent(created, owner.Concat(member2).ToList(), "the two seats split everything");
            Assert.AreEqual(7, owner.Count, "6 snake-dealt items plus one bonus");
            Assert.AreEqual(7, member2.Count);
            Assert.IsTrue(created.All(i => !i.IsDestroyed));
        }

        [TestMethod]
        public void A_rare_for_a_keyless_recipient_is_dealt_like_a_rare_with_no_recipient()
        {
            var run = KeylessGroupRun(Member, Member2);
            run.SetMemberKey(Member2, 0x80001030u);

            var toKeyless = Valued(1);
            var toKeyed = Valued(2);
            Assert.IsTrue(run.TryAppendLootEntry(Entry(toKeyless, Member)));
            Assert.IsTrue(run.TryAppendLootEntry(Entry(toKeyed, Member2)));

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, Materializer(new List<WorldObject>(), perEntry: 0), NoBonus));

            Assert.IsFalse(run.HasHeldPile(Member), "not piled for the keyless recipient");
            Assert.IsTrue(PileHeld(run, Member2).Any(h => ReferenceEquals(h.Item, toKeyed) && h.RareFinderName == "Finder"), "a keyed recipient still gets theirs directly");

            var all = PileItems(run, Owner).Concat(PileItems(run, Member2)).ToList();
            Assert.AreEqual(1, all.Count(i => ReferenceEquals(i, toKeyless)), "dealt once, over the seats");
            Assert.AreEqual(2, all.Count);
        }

        // ------------------------------------------------------------------------------------------------------
        // Final review F3: a deal is capped by rolls as well as entries
        // ------------------------------------------------------------------------------------------------------

        private static ThreadLootLedgerEntry RolledEntry(int rolls)
            => new ThreadLootLedgerEntry(false, false, null, null, null, null, rolls, 0);

        [TestMethod]
        public void A_ledger_of_high_roll_entries_stops_at_the_roll_cap_and_leaves_the_rest_banked()
        {
            Assert.AreEqual(120, ThreadGroupCacheDelivery.MaxRollsPerDeal);

            var run = ClearedGroupRun(Member);
            var entries = Enumerable.Range(0, 5).Select(_ => RolledEntry(50)).ToList();
            foreach (var entry in entries)
                Assert.IsTrue(run.TryAppendLootEntry(entry));

            var claimed = new List<ThreadLootLedgerEntry>();
            Func<ThreadLootLedgerEntry, List<WorldObject>> recording = e => { claimed.Add(e); return new List<WorldObject> { Valued(1) }; };

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, recording, NoBonus));
            CollectionAssert.AreEqual(entries.Take(2).ToList(), claimed, "50 + 50 = 100; a third whole entry would reach 150, past 120");
            Assert.AreEqual(3, run.LedgerCount, "the rest stays banked, in kill order");
            Assert.IsTrue(run.IsPlacementWanted, "and re-arms the retry");

            // Exact sets (Threads item 4, 2026-09-18): every ledger set is exactly the floor (120 untimed), so the next
            // deal's first claim - the one claim that may split - takes just the 20 rolls that complete the set, hands
            // it out, and goes on to the next set with whole entries.
            Assert.AreEqual(2, ThreadGroupCacheDelivery.Deal(run, recording, NoBonus));
            CollectionAssert.AreEqual(new[] { 50, 50, 20, 30, 50 }, claimed.Select(e => e.Rolls).ToList());
            Assert.AreEqual(3, ThreadGroupCacheDelivery.Deal(run, recording, NoBonus));
            CollectionAssert.AreEqual(new[] { 50, 50, 20, 30, 50, 40, 10 }, claimed.Select(e => e.Rolls).ToList());
            Assert.AreEqual(0, run.LedgerCount);
            Assert.IsFalse(run.IsPlacementWanted);
        }

        [TestMethod]
        public void The_roll_cap_fills_up_to_exactly_120_and_the_first_entry_is_always_claimed()
        {
            var run = ClearedGroupRun(Member);
            var entries = new[] { RolledEntry(100), RolledEntry(20), RolledEntry(1) };
            foreach (var entry in entries)
                Assert.IsTrue(run.TryAppendLootEntry(entry));

            var claimed = new List<ThreadLootLedgerEntry>();
            Func<ThreadLootLedgerEntry, List<WorldObject>> recording = e => { claimed.Add(e); return new List<WorldObject>(); };

            ThreadGroupCacheDelivery.Deal(run, recording, NoBonus);
            CollectionAssert.AreEqual(new[] { entries[0], entries[1] }, claimed, "100 + 20 = 120 is within the cap; the 1-roll entry is not");

            Assert.IsFalse(run.TryClaimNextLootEntry(0, out _), "a budget of 0 claims nothing");
            Assert.AreEqual(1, run.LedgerCount);
            Assert.IsTrue(run.TryClaimNextLootEntry(1, out var last));
            Assert.AreSame(entries[2], last);
        }

        // ------------------------------------------------------------------------------------------------------
        // FillFromPile
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void FillFromPile_sorts_the_whole_pile_by_value_before_placing_and_returns_the_rest_with_their_round()
        {
            // Owner correction, 2026-09-22: the pile is merged and sorted as a WHOLE before any of it is split into
            // chests, so placement order is by Value descending (none of these five items is Stackable, so they are
            // all "everything else"), not pile-insertion order. Refused items simply go back to the pile - it is
            // re-consolidated and re-sorted from scratch on the next fill, so their RETURN order carries no meaning.
            var run = ClearedGroupRun(Member);
            var existing = Item();
            var cache = Cache(capacity: 3);
            Assert.IsTrue(cache.TryAddToInventory(existing, 0));

            var a = Valued(1);
            var rare = Valued(2);
            var c = Valued(3);
            var d = Valued(4);
            var e = Valued(5);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(a, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(rare, "Finder", 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(c, null, 2)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(d, null, 2)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(e, null, 3)));

            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, Member);

            // Highest value first: e(5), d(4) fill the two free slots; c(3), rare(2), a(1) are refused.
            CollectionAssert.AreEqual(new[] { e, d }, result.Added, "capacity 3 with one item already in it; sorted by Value descending");
            Assert.AreEqual(0, result.RaresLanded.Count, "the rare (Value 2) did not make the cut this pass");
            Assert.AreEqual(1, e.PlacementPosition, "placed after the existing item, never shifting it");
            Assert.AreEqual(2, d.PlacementPosition);
            Assert.AreEqual(0, existing.PlacementPosition);

            var left = PileHeld(run, Member);
            CollectionAssert.AreEquivalent(new[] { c, rare, a }, left.Select(h => h.Item).ToList(), "refused items go back to the same pile");
            Assert.AreEqual(2, left.Single(h => ReferenceEquals(h.Item, c)).Round);
            Assert.AreEqual(1, left.Single(h => ReferenceEquals(h.Item, rare)).Round);
            Assert.AreEqual(1, left.Single(h => ReferenceEquals(h.Item, a)).Round);
            Assert.AreEqual("Finder", left.Single(h => ReferenceEquals(h.Item, rare)).RareFinderName, "a refused rare keeps its finder name");
            Assert.AreEqual(0, run.OverflowCount, "never to the shared overflow");
            Assert.IsFalse(run.HasHeldPile(Owner));

            var snapshot = run.SnapshotRoster().Single(s => s.Guid == Member);
            Assert.AreEqual(2, snapshot.PilesReceived, "rounds 2 (d) and 3 (e) reached the cache");
            Assert.AreEqual(2, snapshot.PilesForfeited, "rounds 1 and 2 are still (partly) held");
        }

        [TestMethod]
        public void FillFromPile_merges_identical_stacks_across_every_round_in_the_pile_and_marks_every_round_received()
        {
            // Owner correction, 2026-09-22: the WHOLE pile is merged before any of it is split into chests, not just
            // what one delivery pass collects, so identical stackables from several deal rounds land as the fewest
            // possible stacks. Every absorbed round must still be marked received (ruling R21) even though it places
            // no item of its own.
            var run = ClearedGroupRun(Member);
            var cache = Cache(capacity: 10);

            var s1 = StackedValued(9700u, 10);
            var s2 = StackedValued(9700u, 15);
            var s3 = StackedValued(9700u, 5);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s1, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s2, null, 2)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s3, null, 3)));

            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, Member);

            Assert.AreEqual(1, result.Added.Count, "three identical stacks merged into one");
            Assert.AreEqual(30, result.Added[0].StackSize);
            Assert.IsTrue(s2.IsDestroyed && s3.IsDestroyed, "merged away");
            Assert.IsFalse(s1.IsDestroyed, "the first stack is the survivor");

            var snapshot = run.SnapshotRoster().Single(s => s.Guid == Member);
            Assert.AreEqual(3, snapshot.PilesReceived, "rounds 1, 2 and 3 all reached the cache, via the one merged stack");
        }

        [TestMethod]
        public void FillFromPile_tops_up_a_matching_stack_already_in_the_cache_from_an_earlier_round()
        {
            // The bug this round-2 fix targets directly: a member refilling the SAME chest across deal rounds used
            // to grow a second stack beside one already sitting there. TopUpExisting must top up the existing stack
            // instead, conserving quantity/value/burden and marking the new round received.
            var run = ClearedGroupRun(Member);
            var cache = Cache(capacity: 10);

            var already = StackedValued(9701u, 20);
            Assert.IsTrue(cache.TryAddToInventory(already, 0));

            var topUp = StackedValued(9701u, 5);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(topUp, null, 2)));

            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, Member);

            Assert.AreEqual(0, result.Added.Count, "nothing takes a fresh slot; it topped up the existing stack");
            Assert.AreEqual(1, cache.Inventory.Count, "still one stack in the cache, not two");
            Assert.AreEqual(25, already.StackSize, "quantity conserved");
            Assert.AreEqual(125, already.Value, "value conserved: (20 + 5) x unit value 5");
            Assert.AreEqual(250, already.EncumbranceVal, "burden conserved: (20 + 5) x unit burden 10");
            Assert.IsTrue(topUp.IsDestroyed, "fully absorbed into the existing stack");

            // Code review of PR #1284, finding 2: the CACHE's own aggregates (Container.Value/EncumbranceVal, what
            // Player_Inventory.AdjustStack keeps in step on the player's own root container) must track the top-up
            // too, not just the surviving stack object.
            Assert.AreEqual(125, cache.Value, "the cache's own aggregate Value matches its one surviving stack");
            Assert.AreEqual(250, cache.EncumbranceVal, "the cache's own aggregate EncumbranceVal matches its one surviving stack");

            var snapshot = run.SnapshotRoster().Single(s => s.Guid == Member);
            Assert.AreEqual(1, snapshot.PilesReceived, "round 2 is marked received even though it placed nothing new");
        }

        [TestMethod]
        public void FillFromPile_a_survivor_landing_never_marks_received_a_round_still_held_by_its_co_survivor()
        {
            // Code review of PR #1284, finding 1: when MaxStackSize forces MORE THAN ONE surviving stack (the
            // ThreadLootStackingTests.cs:69 shape - 3x60 into a 100 cap merges to two stacks, ONE destroyed), a
            // cache with room for only one of the two survivors must not falsely mark received the round the OTHER
            // (still-refused) survivor owns. Only the round of the item MergeGroup actually destroyed (the third
            // stack, index 2) may ride along with a landed survivor - never a co-survivor's own round.
            var run = ClearedGroupRun(Member);
            var cache = Cache(capacity: 1); // room for exactly one of the two survivors

            var s1 = StackedValued(9710u, 60, unitValue: 1, unitBurden: 0, max: 100);
            var s2 = StackedValued(9710u, 60, unitValue: 1, unitBurden: 0, max: 100);
            var s3 = StackedValued(9710u, 60, unitValue: 1, unitBurden: 0, max: 100);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s1, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s2, null, 2)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(s3, null, 3)));

            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, Member);

            // Fixture: 180 total / max 100 merges to two survivors (100 + 80), s3 (round 3) is the one destroyed.
            // s1 (round 1, the first survivor) lands; s2 (round 2, the second survivor) is refused - the cache had
            // room for only one.
            Assert.AreEqual(1, result.Added.Count, "only one of the two survivors fits");
            Assert.AreSame(s1, result.Added[0], "s1 is the first survivor (MergeGroup keeps the group's own first items)");
            Assert.AreEqual(100, s1.StackSize);
            Assert.AreEqual(80, s2.StackSize, "s2 is the second survivor - resized, not destroyed");
            Assert.IsTrue(s3.IsDestroyed, "s3 is the one MergeGroup actually destroyed");
            Assert.IsFalse(s2.IsDestroyed);

            var left = PileHeld(run, Member);
            Assert.AreEqual(1, left.Count, "s2 is still held, refused");
            Assert.AreSame(s2, left[0].Item);
            Assert.AreEqual(2, left[0].Round, "s2 keeps its own round - never silently marked received by s1 landing");

            var snapshot = run.SnapshotRoster().Single(s => s.Guid == Member);
            Assert.AreEqual(2, snapshot.PilesReceived, "rounds 1 (s1 landed) and 3 (destroyed into s1) - NOT round 2, which s2 still owns and still holds");
            Assert.AreEqual(1, snapshot.PilesForfeited, "round 2 is the only one still held");
        }

        [TestMethod]
        public void FillFromPile_skips_an_already_destroyed_item_in_the_pile_without_placing_or_returning_it()
        {
            // Code review of PR #1284, finding 3 (hardening): if ConsolidatePile/SortPileForPlacement threw AFTER an
            // earlier group inside that same call already merged and destroyed some of its surplus objects, the
            // `pile` this loop falls back to (its pre-consolidate value) can still reference a destroyed object.
            // Simulated directly here: a destroyed item sitting in the pile must never be placed, and must never be
            // handed back to the pile either (which would hand a destroyed object out again on the next fill).
            var run = ClearedGroupRun(Member);
            var cache = Cache(capacity: 5);

            var alive = Valued(1);
            var destroyed = Valued(2);
            destroyed.Destroy();

            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(destroyed, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(alive, null, 1)));

            var result = ThreadGroupCacheDelivery.FillFromPile(run, cache, Member);

            CollectionAssert.AreEqual(new[] { alive }, result.Added, "only the live item is placed");
            Assert.IsFalse(run.HasHeldPile(Member), "the destroyed item is dropped, not returned to the pile");
        }

        [TestMethod]
        public void FillFromPile_on_an_ended_run_destroys_what_the_cache_refuses()
        {
            var run = ClearedGroupRun(Member);
            var a = Valued(1);
            var b = Valued(2);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(a, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(b, null, 1)));
            run.MarkEnded("test");

            var result = ThreadGroupCacheDelivery.FillFromPile(run, Cache(capacity: 1), Member);

            // Sorted by Value descending (owner correction, 2026-09-22): b (Value 2) lands, a is refused.
            CollectionAssert.AreEqual(new[] { b }, result.Added);
            Assert.AreEqual(1, result.Destroyed);
            Assert.IsTrue(a.IsDestroyed);
            Assert.IsFalse(run.HasHeldPile(Member));
        }

        // ------------------------------------------------------------------------------------------------------
        // Run end, gates and helpers
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void DisposeForRunEnd_destroys_held_piles_and_counts_forfeited_rounds()
        {
            var run = ClearedGroupRun(Member, Member2);
            var a = Valued(1);
            var b = Valued(2);
            var c = Valued(3);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(a, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(b, null, 2)));
            Assert.IsTrue(run.AddToHeldPile(Member2, new HeldPileItem(c, null, 2)));
            run.MarkMemberDeliveryWanted(Member);

            Assert.IsTrue(run.MarkEnded("test"));
            var drain = ThreadLootPool.DisposeForRunEnd(run);

            Assert.AreEqual(3, drain.HeldPileItems);
            Assert.IsTrue(new[] { a, b, c }.All(i => i.IsDestroyed));
            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsFalse(run.IsMemberDeliveryWanted(Member));

            var snapshot = run.SnapshotRoster();
            Assert.AreEqual(0, snapshot.Single(s => s.Guid == Owner).PilesForfeited);
            Assert.AreEqual(2, snapshot.Single(s => s.Guid == Member).PilesForfeited);
            Assert.AreEqual(1, snapshot.Single(s => s.Guid == Member2).PilesForfeited);
        }

        [TestMethod]
        public void DisposeForRunEnd_on_a_solo_run_reports_no_held_pile()
        {
            var run = PooledRun();
            Assert.IsTrue(run.MarkEnded("test"));

            Assert.AreEqual(0, ThreadLootPool.DisposeForRunEnd(run).HeldPileItems);
        }

        [TestMethod]
        public void HasUnclaimedLoot_counts_held_piles_and_HasUnclaimedLootFor_counts_only_the_members_own()
        {
            var run = ClearedGroupRun(Member, Member2);
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.IsFalse(run.HasUnclaimedLootFor(Member));

            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Valued(1), null, 1)));
            Assert.IsTrue(run.HasUnclaimedLoot, "the TriggerPooledLoot gate sees a held pile once the ledger is dealt");
            Assert.IsTrue(run.HasUnclaimedLootFor(Member));
            Assert.IsFalse(run.HasUnclaimedLootFor(Member2), "another member's pile is not this member's loot");
            Assert.IsFalse(run.HasUnclaimedLootFor(0x50000099u), "not a member");

            Assert.IsTrue(run.TryAppendLootEntry(Entry()));
            Assert.IsTrue(run.HasUnclaimedLootFor(Member2), "the shared ledger is anyone's until dealt");

            // Solo: unchanged by construction; a solo run never holds a pile.
            var solo = PooledRun();
            Assert.IsFalse(solo.HasUnclaimedLoot);
            Assert.IsTrue(solo.TryAppendLootEntry(Entry()));
            Assert.IsTrue(solo.HasUnclaimedLoot);
        }

        [TestMethod]
        public void AnyCacheHoldsItemsFor_reads_only_the_members_own_caches()
        {
            var run = ClearedGroupRun(Member);
            var ownerChest = OwnedChest(run, Owner);
            var memberChest = OwnedChest(run, Member);
            var unstamped = Cache();
            Assert.IsTrue(run.TryRegisterCache(unstamped));

            Assert.IsFalse(ThreadLootPool.AnyCacheHoldsItemsFor(run, Member));
            Assert.IsTrue(ownerChest.TryAddToInventory(Item(), 0));
            Assert.IsTrue(unstamped.TryAddToInventory(Item(), 0));
            Assert.IsFalse(ThreadLootPool.AnyCacheHoldsItemsFor(run, Member), "the owner's and an unstamped cache are not the member's");
            Assert.IsTrue(ThreadLootPool.AnyCacheHoldsItemsFor(run, Owner));

            Assert.IsTrue(memberChest.TryAddToInventory(Item(), 0));
            Assert.IsTrue(ThreadLootPool.AnyCacheHoldsItemsFor(run, Member));
            Assert.IsFalse(ThreadLootPool.AnyCacheHoldsItemsFor(null, Member));
        }

        [TestMethod]
        public void FormAndFill_hasMore_overload_stops_when_the_predicate_turns_false()
        {
            var run = ClearedGroupRun(Member);
            var asked = 0;
            var formed = 0;

            var pass = ThreadCachePlacer.FormAndFill(run, ThreadCachePlacer.MaxCachesPerPass, () => ++asked <= 2, () => Candidates(1),
                _ => { var c = Cache(); Assert.IsTrue(run.TryRegisterCache(c)); return c; },
                (r, c) =>
                {
                    var result = new CacheFillResult();
                    var item = Item();
                    Assert.IsTrue(c.TryAddToInventory(item, 0));
                    result.Added.Add(item);
                    return result;
                },
                (c, res) => formed++);

            Assert.AreEqual(2, pass.CachesFormed);
            Assert.AreEqual(2, formed);
            Assert.AreEqual(3, asked, "asked before each cache, and once more to stop");
            Assert.IsFalse(pass.NoRoom);
        }

        [TestMethod]
        public void The_group_bonus_seam_is_the_two_argument_builder_and_no_group_path_reaches_the_solo_filler()
        {
            // Owner ruling, 2026-09-17: the group bonus is two halves behind two seams. The DUPLICATED half is the
            // per-seat currency and salvage; the POOLED half is the Legendary rolls, built once for the whole deal.
            var method = ThreadGroupCacheDelivery.BonusBuilder.Method;
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusPerSeatItems), method.Name);
            Assert.AreEqual(2, method.GetParameters().Length, "BuildBossBonusPerSeatItems(run, B)");

            var pooled = ThreadGroupCacheDelivery.PooledRollBuilder.Method;
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls), pooled.Name);
            Assert.AreEqual(3, pooled.GetParameters().Length, "BuildBossBonusPooledRolls(run, factor, cap)");

            // The SOLO path is now split the same way (2026-09-19): a fixed half behind its own seam, and the same
            // two pooled-roll seams this path uses. ThreadSoloBonusBatchingTests owns that half.
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusFixedItems), ThreadCacheFiller.BonusBuilder.Method.Name);
            Assert.AreEqual(nameof(ThreadDungeonRewardSpawner.BuildBossBonusPooledRolls), ThreadCacheFiller.BonusRollBuilder.Method.Name);

            var code = StripComments(Src("ThreadGroupCacheDelivery.cs"));
            Assert.IsFalse(code.Contains("ThreadCacheFiller.Fill"), "a group run never fills from the shared pool");
            Assert.IsFalse(code.Contains("ThreadCacheFiller.BonusBuilder"), "a group run never reaches the 1-arg bonus seam");
        }

        // ------------------------------------------------------------------------------------------------------
        // The split boss bonus (owner ruling, 2026-09-17, reversing the per-seat half of R32)
        // ------------------------------------------------------------------------------------------------------

        /// <summary>
        /// The shape of the ruling, in one deal: currency and salvage are built ONCE PER SEAT and land whole in that
        /// seat's pile; the item rolls are built ONCE for the deal and snake-dealt. Before this, the item rolls were
        /// duplicated per seat too, which made the boss the one term that did not scale like the rest of the loot.
        /// </summary>
        [TestMethod]
        public void The_per_seat_half_is_built_per_seat_and_the_pooled_half_exactly_once()
        {
            var run = ClearedGroupRun(Member, Member2);
            Assert.AreEqual(3, run.DealSeats().Count, "fixture: three seats");
            Assert.IsTrue(run.TryMarkLootBonusPending());

            var perSeatCalls = 0;
            var pooledCalls = 0;
            var perSeatFactors = new List<double>();
            var pooledFactors = new List<double>();
            var pooledCaps = new List<int>();
            var perSeatItems = new List<WorldObject>();
            var pooledItems = new List<WorldObject>();

            var round = ThreadGroupCacheDelivery.Deal(run,
                Materializer(new List<WorldObject>(), perEntry: 0),
                (r, b) =>
                {
                    perSeatCalls++;
                    perSeatFactors.Add(b);

                    // One Trade Note stack plus one salvage bag, the two components that stay duplicated.
                    var items = new List<WorldObject> { Valued(250000), Valued(10) };
                    perSeatItems.AddRange(items);
                    return items;
                },
                (r, factor, cap) =>
                {
                    pooledCalls++;
                    pooledFactors.Add(factor);
                    pooledCaps.Add(cap);

                    var items = Enumerable.Range(0, 9).Select(i => Valued(1000 + i)).ToList();
                    pooledItems.AddRange(items);
                    return items;
                });

            Assert.AreEqual(1, round);
            Assert.AreEqual(3, perSeatCalls, "the duplicated half is built once per seat");
            Assert.AreEqual(1, pooledCalls, "the pooled half is built exactly ONCE, not once per seat");

            CollectionAssert.AreEqual(new[] { run.Group.RewardBonus, run.Group.RewardBonus, run.Group.RewardBonus }, perSeatFactors.ToArray(),
                "every seat's duplicated half is built at B");

            Assert.AreEqual(3 * run.Group.RewardBonus, pooledFactors.Single(), 1e-9, "seats * B, not E * B");
            Assert.AreEqual(3 * ThreadDungeonRewardSpawner.DefaultLootCountCap, pooledCaps.Single(), "the cap rises with the seats");

            // Every seat holds its own full duplicated half: two items each, not two shared out.
            foreach (var seat in run.DealSeats())
            {
                var pile = PileItems(run, seat);
                Assert.AreEqual(2, pile.Count(i => perSeatItems.Contains(i)), $"seat 0x{seat:X8} keeps a whole currency-and-salvage set");
            }

            // The pooled nine are spread, not duplicated: three seats, three each, every item in exactly one pile.
            var pooledPiled = run.DealSeats().SelectMany(s => PileItems(run, s)).Where(i => pooledItems.Contains(i)).ToList();
            Assert.AreEqual(9, pooledPiled.Count, "every pooled roll dealt exactly once");
            Assert.AreEqual(9, pooledPiled.Distinct().Count(), "no pooled roll in two piles");
            CollectionAssert.AreEquivalent(pooledItems, pooledPiled);

            foreach (var seat in run.DealSeats())
                Assert.AreEqual(3, PileItems(run, seat).Count(i => pooledItems.Contains(i)), $"seat 0x{seat:X8} takes a third of the pool");
        }

        /// <summary>
        /// Conservation across both halves: everything built goes out, nothing is stranded, destroyed or duplicated,
        /// and the ledger items still share the same snake as the pooled rolls.
        /// </summary>
        [TestMethod]
        public void Nothing_is_stranded_or_duplicated_across_the_split_bonus()
        {
            var run = ClearedGroupRun(Member, Member2);
            var created = new List<WorldObject>();
            var perSeat = new List<WorldObject>();
            var pooled = new List<WorldObject>();

            for (var i = 0; i < 4; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            Assert.IsTrue(run.TryMarkLootBonusPending());

            ThreadGroupCacheDelivery.Deal(run, Materializer(created),
                (r, b) =>
                {
                    var items = new List<WorldObject> { Valued(250000) };
                    perSeat.AddRange(items);
                    return items;
                },
                (r, f, c) =>
                {
                    var items = Enumerable.Range(0, 7).Select(i => Valued(2000 + i)).ToList();
                    pooled.AddRange(items);
                    return items;
                });

            var all = created.Concat(perSeat).Concat(pooled).ToList();
            var piled = run.Roster.SelectMany(m => PileItems(run, m.Guid)).ToList();

            Assert.AreEqual(12 + 3 + 7, all.Count, "4 entries x 3 rolls, 3 per-seat sets, one pool of 7");
            Assert.AreEqual(all.Count, piled.Count, "every item piled exactly once");
            CollectionAssert.AreEquivalent(all, piled);
            Assert.AreEqual(piled.Count, piled.Distinct().Count(), "no item in two piles");
            Assert.IsTrue(all.All(i => !i.IsDestroyed), "nothing destroyed");
            Assert.IsFalse(run.IsLootBonusPending, "the bonus is spent");
        }

        /// <summary>
        /// The bonus is still exactly-once: the bossChestClaimed latch behind TryMarkLootBonusPending means a second
        /// deal builds neither half, however many deals follow.
        /// </summary>
        [TestMethod]
        public void The_split_bonus_is_still_exactly_once()
        {
            var run = ClearedGroupRun(Member);
            var perSeatCalls = 0;
            var pooledCalls = 0;

            Assert.IsTrue(run.TryMarkLootBonusPending());
            Assert.IsFalse(run.TryMarkLootBonusPending(), "the latch refuses a second boss");

            Func<ThreadDungeonRun, double, List<WorldObject>> perSeat = (r, b) => { perSeatCalls++; return new List<WorldObject> { Valued(250000) }; };
            Func<ThreadDungeonRun, double, int, List<WorldObject>> pooled = (r, f, c) => { pooledCalls++; return new List<WorldObject> { Valued(1000) }; };

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, Materializer(new List<WorldObject>(), perEntry: 0), perSeat, pooled));
            Assert.AreEqual(2, perSeatCalls, "two seats");
            Assert.AreEqual(1, pooledCalls);

            // A second deal has nothing to claim, so it consumes no round and builds nothing.
            Assert.AreEqual(0, ThreadGroupCacheDelivery.Deal(run, Materializer(new List<WorldObject>(), perEntry: 0), NoBonus, NoPooledRolls));
            Assert.AreEqual(2, perSeatCalls);
            Assert.AreEqual(1, pooledCalls);
        }

        /// <summary>
        /// A deal with no pooled builder supplied pools nothing and still deals the per-seat half, which is the shape
        /// every ledger-only test in this file relies on.
        /// </summary>
        [TestMethod]
        public void A_missing_pooled_builder_pools_nothing()
        {
            var run = ClearedGroupRun(Member);
            var perSeatCalls = 0;

            Assert.IsTrue(run.TryMarkLootBonusPending());

            Assert.AreEqual(1, ThreadGroupCacheDelivery.Deal(run, Materializer(new List<WorldObject>(), perEntry: 0),
                (r, b) => { perSeatCalls++; return new List<WorldObject> { Valued(250000) }; }));

            Assert.AreEqual(2, perSeatCalls);
            Assert.AreEqual(1, PileItems(run, Owner).Count);
            Assert.AreEqual(1, PileItems(run, Member).Count);
        }

        // ------------------------------------------------------------------------------------------------------
        // Run: per-member delivery and the retry flags (ruling R20, Task 9 carry)
        // ------------------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_NoRoom_pass_for_members_inside_clears_their_flags_and_does_not_rearm_the_tick_retry()
        {
            var run = ClearedGroupRun(Member);
            for (var i = 0; i < 4; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            // Armed as the Tick would find them: a run-level wait and both members flagged.
            run.MarkPlacementWanted();
            run.MarkMemberDeliveryWanted(Owner);
            run.MarkMemberDeliveryWanted(Member);

            var world = new FakeWorld { FormAt = (g, hasMore) => new CacheFormPass { NoRoom = true } };
            world.Inside.Add(Owner);
            world.Inside.Add(Member);

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run, Materializer(new List<WorldObject>())));

            CollectionAssert.AreEqual(new[] { Owner, Member }, world.FormCalls, "each inside member gets one pass, in roster order");
            Assert.IsTrue(run.HasHeldPile(Owner) && run.HasHeldPile(Member), "no room: the piles are kept");
            Assert.IsFalse(run.IsMemberDeliveryWanted(Owner), "served inside: not flagged (R20)");
            Assert.IsFalse(run.IsMemberDeliveryWanted(Member));
            Assert.IsFalse(run.IsPlacementWanted, "the ledger was fully dealt");
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(run, g => throw new AssertFailedException("no flagged member to look up")),
                "the once-a-second Tick retry stays off");

            CollectionAssert.AreEqual(new[] { (Owner, ThreadCachePlacer.NoRoomMessage), (Member, ThreadCachePlacer.NoRoomMessage) }, world.Told);

            // A second pass (e.g. the member's own arrival trigger) still does not arm anything.
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run));
            Assert.IsFalse(run.IsMemberDeliveryWanted(Owner) || run.IsMemberDeliveryWanted(Member) || run.IsPlacementWanted);
        }

        [TestMethod]
        public void A_member_outside_is_flagged_and_a_member_inside_gets_caches_formed_at_them()
        {
            var run = ClearedGroupRun(Member);
            for (var i = 0; i < 4; i++)
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var formed = new List<Chest>();
            var world = new FakeWorld();
            world.FormAt = FormsChests(run, formed);
            world.Inside.Add(Owner);

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run, Materializer(new List<WorldObject>())));

            CollectionAssert.AreEqual(new[] { Owner }, world.FormCalls, "only the member inside forms caches");
            Assert.AreEqual(1, formed.Count);
            Assert.AreEqual(Owner, formed[0].P_DungeonCacheOwnerGuid);
            Assert.AreEqual(6, formed[0].Inventory.Count, "12 items snake-dealt over 2 seats");
            Assert.IsFalse(run.HasHeldPile(Owner));
            Assert.IsFalse(run.IsMemberDeliveryWanted(Owner));

            Assert.IsTrue(run.HasHeldPile(Member));
            Assert.IsTrue(run.IsMemberDeliveryWanted(Member), "not inside: waits for arrival (R20)");
            CollectionAssert.AreEqual(new[] { (Owner, ThreadCachePlacer.FormedMessage) }, world.Told, "one line, to the member it formed for");

            // The member arrives; the next pass serves them and clears the flag.
            world.Inside.Add(Member);
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run));

            Assert.AreEqual(2, formed.Count);
            Assert.AreEqual(Member, formed[1].P_DungeonCacheOwnerGuid);
            Assert.AreEqual(6, formed[1].Inventory.Count);
            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsFalse(run.IsMemberDeliveryWanted(Member));
            Assert.AreEqual(1, run.SnapshotRoster().Single(s => s.Guid == Member).PilesReceived);
        }

        [TestMethod]
        public void Auto_tops_up_each_members_own_caches_without_presence()
        {
            var run = ClearedGroupRun(Member);
            var ownerChest = OwnedChest(run, Owner);
            var memberChest = OwnedChest(run, Member);
            var ownerItems = new[] { Valued(1), Valued(2) };
            var memberItems = new[] { Valued(3) };

            foreach (var item in ownerItems)
                Assert.IsTrue(run.AddToHeldPile(Owner, new HeldPileItem(item, null, 1)));
            foreach (var item in memberItems)
                Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(item, null, 1)));

            var world = new FakeWorld { FormAt = (g, h) => throw new AssertFailedException("nobody is inside") };

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run));

            CollectionAssert.AreEquivalent(ownerItems, ownerChest.Inventory.Values.ToList());
            CollectionAssert.AreEquivalent(memberItems, memberChest.Inventory.Values.ToList());
            CollectionAssert.AreEqual(new Container[] { ownerChest, memberChest }, world.Delivered);
            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsFalse(run.IsMemberDeliveryWanted(Owner) || run.IsMemberDeliveryWanted(Member), "a delivered pile needs no wait");
            Assert.AreEqual(0, world.Told.Count);
        }

        [TestMethod]
        public void Summon_forms_from_the_requesters_pile_at_them_and_leaves_other_piles_when_nothing_was_dealt()
        {
            var run = ClearedGroupRun(Member);
            var memberChest = OwnedChest(run, Member);
            var ownerItem = Valued(1);
            var memberItem = Valued(2);
            Assert.IsTrue(run.AddToHeldPile(Owner, new HeldPileItem(ownerItem, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(memberItem, null, 1)));

            var formed = new List<Chest>();
            var world = new FakeWorld();
            world.FormAt = FormsChests(run, formed);
            world.Inside.Add(Owner);
            world.Inside.Add(Member);

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Summon, Member, world.Build(run));

            CollectionAssert.AreEqual(new[] { Member }, world.FormCalls, "Summon acts for the requester only");
            Assert.AreEqual(0, memberChest.Inventory.Count, "Summon forms at the member, it does not top up");
            Assert.AreEqual(1, formed.Count);
            Assert.AreSame(memberItem, formed[0].Inventory.Values.Single());
            Assert.IsTrue(run.HasHeldPile(Owner), "another member's pile is untouched");
            Assert.IsFalse(ownerItem.IsDestroyed);
        }

        [TestMethod]
        public void Summon_that_deals_serves_the_other_members_too()
        {
            var run = ClearedGroupRun(Member);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var formed = new List<Chest>();
            var world = new FakeWorld();
            world.FormAt = FormsChests(run, formed);
            world.Inside.Add(Member);

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Summon, Member, world.Build(run, Materializer(new List<WorldObject>(), perEntry: 2)));

            CollectionAssert.AreEqual(new[] { Member }, world.FormCalls);
            Assert.IsFalse(run.HasHeldPile(Member));
            Assert.IsTrue(run.HasHeldPile(Owner));
            Assert.IsTrue(run.IsMemberDeliveryWanted(Owner), "a pile this pass dealt to someone outside waits for them");
        }

        [TestMethod]
        public void Move_acts_for_an_inside_requester_only_and_deals_nothing()
        {
            var run = ClearedGroupRun(Member);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var world = new FakeWorld { FormAt = (g, h) => throw new AssertFailedException("Move forms nothing from a pile") };

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Move, Member, world.Build(run, _ => throw new AssertFailedException("Move deals nothing")));
            Assert.AreEqual(0, world.Moves.Count, "not inside: nothing moves");
            Assert.IsTrue(run.IsPlacementWanted, "undealt loot is left for the Tick retry");

            world.Inside.Add(Member);
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Move, Member, world.Build(run, _ => throw new AssertFailedException("Move deals nothing")));
            CollectionAssert.AreEqual(new[] { Member }, world.Moves);

            world.Inside.Add(0x50000099u);
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Move, 0x50000099u, world.Build(run));
            Assert.AreEqual(1, world.Moves.Count, "a non-member moves nothing");
            Assert.AreEqual(1, run.LedgerCount);
        }

        [TestMethod]
        public void A_run_that_is_not_cleared_delivers_nothing()
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150), new RosterSeat(Member, "M", 8, 150) };
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80005679u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(2));
            run.MarkPooledLoot(true);
            Assert.IsTrue(run.TryAppendLootEntry(Entry()));

            var world = new FakeWorld { FormAt = (g, h) => throw new AssertFailedException("not cleared") };
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run, _ => throw new AssertFailedException("not cleared")));

            Assert.AreEqual(1, run.LedgerCount);
            Assert.IsFalse(run.AnyHeldPile);
        }

        // ------------------------------------------------------------------------------------------------------
        // Fix round 1
        // ------------------------------------------------------------------------------------------------------

        private static Func<ThreadLootLedgerEntry, List<WorldObject>> OneItemEach => _ => new List<WorldObject> { Valued(10) };

        [TestMethod]
        public void SnakeDeal_start_continues_the_snake_from_a_given_position()
        {
            // Seats by snake position over 3 seats: 0 1 2 2 1 0 | 0 1 2 ...
            CollectionAssert.AreEqual(new long[] { 9, 8 }, Snake3(2, 9, 8)[2], "positions 2 and 3 are both seat 2");
            CollectionAssert.AreEqual(new long[] { 9, 8 }, Snake3(5, 9, 8)[0], "position 5 is seat 0, and position 6 wraps to seat 0");

            var fromFour = Snake3(4, 9, 8, 7);
            CollectionAssert.AreEqual(new long[] { 8, 7 }, fromFour[0]);
            CollectionAssert.AreEqual(new long[] { 9 }, fromFour[1], "position 4 is seat 1");
            Assert.AreEqual(0, fromFour[2].Count);

            CollectionAssert.AreEqual(Snake3(0, 5, 4, 3, 2, 1).Select(h => h.Sum()).ToList(), Snake3(6, 5, 4, 3, 2, 1).Select(h => h.Sum()).ToList(), "start is taken modulo one full lap pair");
            CollectionAssert.AreEqual(Snake3(0, 5, 4).Select(h => h.Sum()).ToList(), Snake3(-4, 5, 4).Select(h => h.Sum()).ToList(), "a negative start reads as 0");
        }

        private static List<List<long>> Snake3(int start, params long[] values) => ThreadGroupCacheDelivery.SnakeDeal(values, v => v, 3, start);

        [TestMethod]
        public void ReserveSnakeSet_rotates_the_first_pick_one_seat_per_set_from_the_run_offset()
        {
            // User ruling on #1212 (2026-09-18): first pick = (runOffset + setIndex) mod seats. Replaces the Task 10
            // cursor that advanced by set SIZE, which pinned the first pick whenever the size was a multiple of 2 x seats.
            var run = ClearedGroupRun(Member, Member2);
            run.SetSnakeRunOffset(4);

            CollectionAssert.AreEqual(new[] { 1, 2, 0, 1, 2, 0 }, Enumerable.Range(0, 6).Select(_ => run.ReserveSnakeSet(3)).ToList(), "4 mod 3 = 1, then one seat per set");
            Assert.AreEqual(0, run.ReserveSnakeSet(2), "a seat left: (4 + 6) mod 2");
            Assert.AreEqual(0, run.ReserveSnakeSet(1));
            Assert.AreEqual(0, run.ReserveSnakeSet(0), "no seat reserves nothing");
            Assert.AreEqual(2, run.ReserveSnakeSet(5), "(4 + 8) mod 5: the zero-seat call did not count as a set (it would read 3)");

            run.SetSnakeRunOffset(0);
            Assert.AreEqual(4, run.SnakeRunOffset, "the offset is fixed once, per run");
        }

        [TestMethod]
        public void The_run_offset_is_drawn_once_from_the_source_on_the_first_set_and_normalised_into_range()
        {
            var saved = ThreadDungeonRun.SnakeRunOffsetSource;
            var draws = 0;

            try
            {
                ThreadDungeonRun.SnakeRunOffsetSource = () => { draws++; return -1; };

                var run = ClearedGroupRun(Member, Member2);
                Assert.AreEqual(-1, run.SnakeRunOffset, "nothing is drawn before the first set");

                run.ReserveSnakeSet(3);
                run.ReserveSnakeSet(3);

                Assert.AreEqual(1, draws, "drawn once per run");
                Assert.AreEqual(ThreadDungeonRun.SnakeRunOffsetRange - 1, run.SnakeRunOffset, "a negative draw wraps into [0, range)");
            }
            finally
            {
                ThreadDungeonRun.SnakeRunOffsetSource = saved;
            }

            // The shipped source is the process RNG, and its range is a multiple of every seat count 1..16.
            StringAssert.Contains(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonRun_LootPool.cs"),
                "SnakeRunOffsetSource = () => ACE.Common.ThreadSafeRandom.Next(0, SnakeRunOffsetRange - 1);");
            Assert.IsTrue(Enumerable.Range(1, 16).All(n => ThreadDungeonRun.SnakeRunOffsetRange % n == 0));
        }
        [TestMethod]
        public void Six_one_item_deals_over_three_members_give_each_pile_two_items()
        {
            var run = ClearedGroupRun(Member, Member2);

            for (var i = 1; i <= 6; i++)
            {
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));
                Assert.AreEqual(i, ThreadGroupCacheDelivery.Deal(run, OneItemEach, NoBonus));
            }

            CollectionAssert.AreEqual(new[] { 2, 2, 2 }, run.Roster.Select(m => PileItems(run, m.Guid).Count).ToList());
        }

        [TestMethod]
        public void Two_two_item_deals_over_three_seats_reach_the_third_seat()
        {
            var run = ClearedGroupRun(Member, Member2);

            for (var deal = 0; deal < 2; deal++)
            {
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));
                Assert.IsTrue(run.TryAppendLootEntry(Entry()));
                ThreadGroupCacheDelivery.Deal(run, OneItemEach, NoBonus);
            }

            Assert.IsTrue(PileItems(run, Member2).Count >= 1, "the snake carried on past seat 1 instead of restarting at the owner");
            Assert.AreEqual(4, run.Roster.Sum(m => PileItems(run, m.Guid).Count));
        }

        [TestMethod]
        public void HandOut_that_throws_destroys_what_it_had_not_handed_out()
        {
            var run = ClearedGroupRun(Member);
            var dealt = Enumerable.Range(1, 5).Select(v => (Item: Valued(v * 100), RareFinderName: (string)null)).ToList();
            var calls = 0;

            var handed = ThreadGroupCacheDelivery.HandOut(run, run.DealSeats(), dealt, 1, (guid, held) =>
            {
                if (++calls == 3)
                    throw new InvalidOperationException("pile failed");
                return run.AddToHeldPile(guid, held);
            });

            Assert.AreEqual(2, handed);
            var piled = run.Roster.SelectMany(m => PileItems(run, m.Guid)).ToList();
            Assert.AreEqual(2, piled.Count);
            Assert.IsTrue(piled.All(i => !i.IsDestroyed));
            Assert.AreEqual(3, dealt.Count(d => d.Item.IsDestroyed), "everything not handed out is destroyed, including the item whose call threw");
            Assert.IsTrue(dealt.All(d => d.Item.IsDestroyed || piled.Contains(d.Item)), "no item is left without a home");
        }

        [TestMethod]
        public void Deal_hands_out_and_rearms_in_a_finally()
        {
            var deal = PooledLootSourceText.MethodBody(Src("ThreadGroupCacheDelivery.cs"), "internal static int Deal(");
            var tryAt = deal.IndexOf("MaterializeAndPile(", StringComparison.Ordinal);
            var finallyAt = deal.IndexOf("finally", tryAt, StringComparison.Ordinal);
            // Final review F2 (ruling R32) changed this pinned call: the hand-out takes the deal's seat list.
            var seats = deal.IndexOf("var seats = run.DealSeats();", StringComparison.Ordinal);
            // Code review of #1212, finding 2: the finally buffers what the deal built and hands out whatever buffer is due.
            // Threads item 4 (exact sets): += because a set completed mid-step is handed out inside MaterializeAndPile too.
            var handOut = deal.IndexOf("tally.HandedOut += BufferAndFlush(run, seats, dealt, pooledDealt, ", finallyAt, StringComparison.Ordinal);
            Assert.IsTrue(seats >= 0 && seats < tryAt, "the seats are read once, before the deal materialises");

            var flush = PooledLootSourceText.MethodBody(Src("ThreadGroupCacheDelivery.cs"), "internal static int BufferAndFlush(");
            var buffer = flush.IndexOf("BufferOrDestroy(run, dealt, ", StringComparison.Ordinal);
            var take = flush.IndexOf("run.TakeDealBuffer(ledgerDue, pooledDue)", buffer, StringComparison.Ordinal);
            var handOutDue = flush.IndexOf("HandOut(run, seats, due, round, run.AddToHeldPile);", take, StringComparison.Ordinal);
            Assert.IsTrue(buffer >= 0 && take > buffer && handOutDue > take, "buffer first, then take what is due, then hand it out");
            var undealt = deal.IndexOf("if (run.HasUndealtLoot)", handOut, StringComparison.Ordinal);
            var rearm = deal.IndexOf("run.MarkPlacementWanted();", undealt, StringComparison.Ordinal);

            Assert.IsTrue(tryAt >= 0 && finallyAt > tryAt && handOut > finallyAt && undealt > handOut && rearm > undealt);
        }

        [TestMethod]
        public void A_pile_an_empty_chest_refuses_is_destroyed_after_one_pass()
        {
            var run = ClearedGroupRun(Member);
            var a = Valued(1);
            var b = Valued(2);
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(a, null, 1)));
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(b, null, 1)));

            var formed = new List<Chest>();
            var world = new FakeWorld();
            world.FormAt = FormsChests(run, formed, capacity: 0);
            world.Inside.Add(Member);

            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run));

            Assert.AreEqual(1, formed.Count);
            Assert.IsTrue(formed[0].IsDestroyed, "the fresh cache that accepted nothing is not left standing");
            Assert.IsFalse(run.HasHeldPile(Member), "the refused pile is emptied");
            Assert.IsTrue(a.IsDestroyed && b.IsDestroyed);
            Assert.IsFalse(run.IsMemberDeliveryWanted(Member));
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(run), "nothing is left to hold the copy alive");
            Assert.AreEqual(0, world.Told.Count, "no cache formed and no NoRoom: no line");
        }

        [TestMethod]
        public void GroupLootOwed_holds_while_a_placed_cache_still_holds_items()
        {
            var run = ClearedGroupRun(Member);
            var chest = OwnedChest(run, Member);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(run), "an empty cache owes nothing");

            var item = Item();
            Assert.IsTrue(chest.TryAddToInventory(item, 0));
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "an unlooted cache is owed, so the reap waits");

            Assert.IsTrue(chest.TryRemoveFromInventory(item.Guid, out _));
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(run), "looted: nothing owed");

            var solo = PooledRun();
            var soloCache = Cache();
            Assert.IsTrue(solo.TryRegisterCache(soloCache));
            Assert.IsTrue(soloCache.TryAddToInventory(Item(), 0));
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(solo), "solo is never group-owed");
        }

        [TestMethod]
        public void A_stale_delivery_flag_counts_as_unclaimed_so_one_pass_clears_it()
        {
            var run = ClearedGroupRun(Member);
            run.MarkMemberDeliveryWanted(Member);

            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsTrue(run.HasUnclaimedLoot, "a flag alone lets the Tick's trigger through instead of returning NotApplicable every second");

            var world = new FakeWorld { FormAt = (g, h) => throw new AssertFailedException("no pile to form") };
            world.Inside.Add(Member);
            ThreadGroupCacheDelivery.Run(run, CacheRequestMode.Auto, Owner, world.Build(run));

            Assert.IsFalse(run.IsMemberDeliveryWanted(Member));
            Assert.IsFalse(run.HasUnclaimedLoot);
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(run, g => throw new AssertFailedException("no flagged member")));
        }

        [TestMethod]
        public void An_arrival_that_finds_the_latch_busy_flags_the_member_for_the_tick_retry()
        {
            Assert.IsTrue(ThreadDungeonManager.ArrivalNeedsTickRetry(CacheRequestOutcome.Busy));
            Assert.IsFalse(ThreadDungeonManager.ArrivalNeedsTickRetry(CacheRequestOutcome.Queued));
            Assert.IsFalse(ThreadDungeonManager.ArrivalNeedsTickRetry(CacheRequestOutcome.NotApplicable));

            var arrived = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs"), "public static void OnPlayerArrived(Player player, uint instance)");
            var wants = arrived.IndexOf("ArrivalWantsDelivery(", StringComparison.Ordinal);
            var trigger = arrived.IndexOf("var outcome = PooledLootTrigger(run);", wants, StringComparison.Ordinal);
            var busy = arrived.IndexOf("if (ArrivalNeedsTickRetry(outcome))", trigger, StringComparison.Ordinal);
            var flag = arrived.IndexOf("run.MarkMemberDeliveryWanted(guid);", busy, StringComparison.Ordinal);

            Assert.IsTrue(wants >= 0 && trigger > wants && busy > trigger && flag > busy);
        }

        // ------------------------------------------------------------------------------------------------------
        // Source-text pins
        // ------------------------------------------------------------------------------------------------------

        private static string Src(string file) => PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/" + file);

        private static string StripComments(string source)
            => string.Join("\n", source.Split('\n').Where(l => !l.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        [TestMethod]
        public void Execute_starts_with_the_group_branch_and_keeps_every_solo_pin()
        {
            var src = Src("ThreadCachePlacer.cs");
            var execute = PooledLootSourceText.MethodBody(src, "private static void Execute(ThreadDungeonRun run, Landblock landblock, CacheRequestMode mode, uint requesterGuid, ThreadLootRollBudget budget)");
            var code = StripComments(execute.Substring(1)).TrimStart();

            StringAssert.StartsWith(code, "if (run.IsGroup)", "the group branch is the first statement");

            var branch = code.IndexOf("ThreadGroupCacheDelivery.Execute(run, landblock, mode, requesterGuid, budget);", StringComparison.Ordinal);
            var returns = code.IndexOf("return;", branch, StringComparison.Ordinal);
            var cleared = code.IndexOf("if (run.State != ThreadDungeonRunState.Cleared)", StringComparison.Ordinal);
            Assert.IsTrue(branch > 0 && returns > branch && cleared > returns, "the group branch returns before the solo body");

            // The solo body's pinned text, in order (ThreadCachePlacerTests pins the same). Round 4 (owner ruling
            // 2026-09-22): solo builds into the pile, waits for the whole list, then reuses the group pile path -
            // ThreadGroupCacheDelivery is now called from the solo body too (FillFromPile, DestroyUnplaceable),
            // so the old "exactly one hand-off" pin is replaced by an ordering pin instead.
            var build = code.IndexOf("ThreadCacheFiller.BuildPile(run, budget, ledgerSnapshot)", StringComparison.Ordinal);
            var topUp = code.IndexOf("run.PlacedCachesSnapshot()", StringComparison.Ordinal);
            // FillFromPile appears in the top-up loop (between topUp and form); FormAndFillAtMember's own use of
            // it lives inside ThreadCachePlacer.cs, not repeated in this method's own body.
            var fillFromPile = topUp < 0 ? -1 : code.IndexOf("ThreadGroupCacheDelivery.FillFromPile(r, c, run.OwnerGuid)", topUp, StringComparison.Ordinal);
            var form = fillFromPile < 0 ? -1 : code.IndexOf("FormAndFillAtMember(run, landblock, owner,", fillFromPile, StringComparison.Ordinal);
            Assert.IsTrue(cleared < build && build < topUp && topUp < fillFromPile && fillFromPile < form,
                "build into the pile, wait for the whole list, top up, then form and fill through the group pile path");
            Assert.AreEqual(1, code.Split(new[] { "FormedMessage" }, StringSplitOptions.None).Length - 1, "one success line per solo pass");
            StringAssert.Contains(code, "ThreadGroupCacheDelivery.Execute(run, landblock, mode, requesterGuid, budget)", "the group hand-off is still the first statement");
        }

        [TestMethod]
        public void The_owner_request_delegates_with_the_owner_and_the_attempt_stamps_the_player_it_forms_at()
        {
            var src = Src("ThreadCachePlacer.cs");

            var legacy = PooledLootSourceText.MethodBody(src, "public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode)");
            StringAssert.Contains(legacy, "return RequestPlacement(run, mode, run?.OwnerGuid ?? 0);");

            var request = PooledLootSourceText.MethodBody(src, "public static CacheRequestOutcome RequestPlacement(ThreadDungeonRun run, CacheRequestMode mode, uint requesterGuid)");
            StringAssert.Contains(request, "Execute(run, landblock, stepMode, requesterGuid, budget);",
                "each chain step forwards the requester; stepMode is the request's own mode on the first step and Auto after it");

            var attempt = PooledLootSourceText.MethodBody(src, "private static Container TryPlaceEmpty(");
            var ownerTest = attempt.IndexOf("owner.Guid.Full == run.OwnerGuid", StringComparison.Ordinal);
            var solo = attempt.IndexOf("ThreadDungeonRewardSpawner.CreateCacheChest(run, \"pooled cache\")", StringComparison.Ordinal);
            var member = attempt.IndexOf("ThreadDungeonRewardSpawner.CreateCacheChest(run, \"pooled cache\", owner.Guid.Full)", StringComparison.Ordinal);
            Assert.IsTrue(ownerTest >= 0 && ownerTest < solo && solo < member, "the owner keeps the owner-stamped factory; a member gets their own stamp");

            StringAssert.Contains(src, "internal static bool IsOwnerInside(ThreadDungeonRun run, Player owner)\n            => ThreadRunPresence.IsMemberInside(run, owner);",
                "invariant 1: one inside test");

            var move = PooledLootSourceText.MethodBody(src, "private static void MoveCaches(");
            StringAssert.Contains(move, "if (run.IsGroup && !ThreadLootPool.IsCacheFor(old, owner.Guid.Full))", "a member moves only their own caches, and solo never filters");

            var loop = PooledLootSourceText.MethodBody(src, "internal static CacheFormPass FormAndFill(");
            StringAssert.Contains(loop, "hasMore()", "the first FormAndFill body is the shared loop");
            StringAssert.Contains(src, "return FormAndFill(run, maxCaches, () => run.HasUnclaimedLoot && !step.Exhausted, candidatesForNextCache, tryPlaceEmpty, fill, deliver);");
        }

        [TestMethod]
        public void HasUnclaimedLoot_and_the_run_end_drain_count_held_piles_in_source()
        {
            var pool = Src("ThreadDungeonRun_LootPool.cs");
            // 2026-09-18: pooled boss-bonus rolls not yet built are unclaimed loot too (the resumable pooled half).
            StringAssert.Contains(pool, "public bool HasUnclaimedLoot { get { lock (stateLock) return lootLedger.Count > 0 || lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal || AnyHeldPile || AnyMemberDeliveryWanted; } }");
            // Code review of #1212, finding 2: built-but-undealt items in the deal buffers are undealt loot.
            StringAssert.Contains(pool, "internal bool HasUndealtLoot { get { lock (stateLock) return lootLedger.Count > 0 || lootBonusPending || pooledBonusRollsPending > 0 || lootOverflow.Count > 0 || AnyBufferedForDeal; } }");

            var dispose = PooledLootSourceText.MethodBody(Src("ThreadLootPool.cs"), "public static LootPoolDrain DisposeForRunEnd(ThreadDungeonRun run)");
            var drainPool = dispose.IndexOf("run.DrainLootPool()", StringComparison.Ordinal);
            var drainBuffered = dispose.IndexOf("foreach (var item in drain.Buffered)", StringComparison.Ordinal);
            var drainPiles = dispose.IndexOf("run.DrainHeldPiles()", StringComparison.Ordinal);
            var destroy = dispose.IndexOf("item.Destroy();", drainPiles, StringComparison.Ordinal);
            Assert.IsTrue(drainPool >= 0 && drainBuffered > drainPool && drainPiles > drainBuffered && destroy > drainPiles);
        }
    }
}
