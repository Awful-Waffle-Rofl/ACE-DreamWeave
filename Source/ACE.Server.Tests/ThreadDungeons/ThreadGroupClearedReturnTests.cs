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
    /// The free return into a CLEARED GROUP run (Discord suggestion, owner-approved): a seated member whose share still
    /// waits inside re-enters without spending an entry, and is told so when they die in the cleared copy or are dealt
    /// a pile while outside. The decision is the pure DungeonGemRules.IsClearedReturn, read off a live run by
    /// IsClearedReturnFor; both are driven here with real runs and no Player. The gem use itself, the death hook and the
    /// live chat need a Player and are pinned on source only.
    ///
    /// No PropertyManager key is read by any driven path (GroupScaling.Compute takes every tunable as an argument).
    /// </summary>
    [TestClass]
    public class ThreadGroupClearedReturnTests
    {
        private const uint Owner = 0x50000110u;
        private const uint Member = 0x50000120u;
        private const uint Keyless = 0x50000130u;

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>A pooled group run of Owner, Member (keyed, so seated) and Keyless (no key, so no deal seat), after <paramref name="kills"/> of 2 kills.</summary>
        private static ThreadDungeonRun GroupRun(int kills = 2)
        {
            var seats = new List<RosterSeat>
            {
                new RosterSeat(Owner, "Owner", 7, 150),
                new RosterSeat(Member, "Member", 8, 150),
                new RosterSeat(Keyless, "Keyless", 9, 150),
            };

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(0x80005679u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            run.MarkPooledLoot(true);
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);

            for (var i = 0; i < kills; i++)
                run.RecordKill(false);

            run.SetMemberKey(Member, 0x80001120u);

            Assert.IsTrue(run.IsGroup, "fixture: a group run");
            CollectionAssert.AreEqual(new[] { Owner, Member }, run.DealSeats().ToList(), "fixture: the keyless member holds no deal seat");
            return run;
        }

        private static ThreadDungeonRun ClearedGroupRun()
        {
            var run = GroupRun();
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: Cleared");
            Assert.IsFalse(run.HasUnclaimedLoot, "fixture: nothing banked, so a share waits only where a test puts one");
            return run;
        }

        private static Chest OwnedChest(ThreadDungeonRun run, uint ownerGuid)
        {
            var chest = new Chest(new Weenie
            {
                WeenieClassId = 1003603,
                WeenieType = WeenieType.Chest,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.ItemsCapacity, 120 } },
            }, new ObjectGuid(NextGuid()));

            chest.P_DungeonCacheOwnerGuid = ownerGuid;
            Assert.IsTrue(run.TryRegisterCache(chest));
            return chest;
        }

        private static readonly ThreadDungeonRunState[] AllStates =
            { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared, ThreadDungeonRunState.Ended };

        // ---------------------------------------------------------------------------------------------
        // The pure decision: the five cases
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Solo_cleared_is_free_and_solo_is_unchanged_for_every_state_seat_and_share()
        {
            Assert.IsTrue(DungeonGemRules.IsClearedReturn(false, ThreadDungeonRunState.Cleared, false, false));

            foreach (var state in AllStates)
                foreach (var seat in new[] { false, true })
                    foreach (var share in new[] { false, true })
                        Assert.AreEqual(DungeonGemRules.IsClearedSoloReturn(false, state), DungeonGemRules.IsClearedReturn(false, state, seat, share),
                            $"solo {state} seat={seat} share={share}: exactly the old cleared-solo rule");
        }

        [TestMethod]
        public void Group_cleared_seated_with_a_share_waiting_is_free_even_at_zero_entries()
        {
            var free = DungeonGemRules.IsClearedReturn(true, ThreadDungeonRunState.Cleared, holdsSeat: true, shareWaiting: true);
            Assert.IsTrue(free);

            var key = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0).WithBinding(0x80005679u, Member);
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(key, Member, 0, true, true, 150, 150, out var reason, freeEntry: free));
            Assert.IsNull(reason, "not refused with NoEntriesReason");

            var charge = DungeonGemRules.DecideEntryCharge(false, 0, free);
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, charge, "nothing spent at zero");
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(false, 3, free), "nothing spent with entries left either");
            Assert.AreEqual("You step back into Filos Doom. It is cleared, so returning costs no entry.",
                DungeonGemRules.EntryMessage(charge, "Thread Key", "Filos Doom", 0, free));
        }

        [TestMethod]
        public void Group_cleared_seated_with_nothing_waiting_is_not_free()
        {
            var free = DungeonGemRules.IsClearedReturn(true, ThreadDungeonRunState.Cleared, holdsSeat: true, shareWaiting: false);
            Assert.IsFalse(free);

            var key = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0).WithBinding(0x80005679u, Member);
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(key, Member, 0, true, true, 150, 150, out var reason, freeEntry: free));
            Assert.AreEqual(DungeonGemRules.NoEntriesReason, reason, "today's rule: zero entries is refused");
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(false, 2, free), "today's rule: an entry is spent");
        }

        [TestMethod]
        public void Group_cleared_without_a_seat_is_not_free_even_with_a_share()
        {
            Assert.IsFalse(DungeonGemRules.IsClearedReturn(true, ThreadDungeonRunState.Cleared, holdsSeat: false, shareWaiting: true));
            Assert.IsFalse(DungeonGemRules.IsClearedReturn(true, ThreadDungeonRunState.Cleared, holdsSeat: false, shareWaiting: false));
        }

        [TestMethod]
        public void Group_not_cleared_is_not_free_for_anyone()
        {
            foreach (var state in AllStates.Where(s => s != ThreadDungeonRunState.Cleared))
                foreach (var seat in new[] { false, true })
                    foreach (var share in new[] { false, true })
                        Assert.IsFalse(DungeonGemRules.IsClearedReturn(true, state, seat, share), $"group {state} seat={seat} share={share}");
        }

        // ---------------------------------------------------------------------------------------------
        // The live-run read (IsClearedReturnFor), on real runs
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_seated_member_is_free_while_their_held_pile_waits_and_not_once_it_is_taken()
        {
            var run = ClearedGroupRun();
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Member), "control: seated, nothing waiting");

            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));
            Assert.IsTrue(DungeonGemRules.IsClearedReturnFor(run, Member), "seated, pile waiting");
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Owner), "another member's pile does not make the owner's return free");

            run.TakeHeldPile(Member);
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Member), "pile collected: back to today's rule");
        }

        [TestMethod]
        public void A_cache_formed_for_the_member_that_still_holds_items_counts_as_a_share_waiting()
        {
            var run = ClearedGroupRun();
            var chest = OwnedChest(run, Member);
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Member), "control: an empty cache is nothing waiting");

            Assert.IsTrue(chest.TryAddToInventory(Item()));
            Assert.IsTrue(DungeonGemRules.IsClearedReturnFor(run, Member), "their own cache holds items");
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Owner), "someone else's cache is not the owner's share");
        }

        [TestMethod]
        public void A_keyless_member_holds_no_seat_and_is_never_free()
        {
            var run = ClearedGroupRun();
            Assert.IsTrue(run.AddToHeldPile(Keyless, new HeldPileItem(Item(), null, 1)), "the roster accepts a pile for any member");

            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Keyless));
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, 0x59999999u), "a stranger");
        }

        [TestMethod]
        public void An_active_group_run_is_not_free_even_for_a_seated_member_with_a_pile()
        {
            var run = GroupRun(kills: 1);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State, "fixture: one kill short of the clear");
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));

            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Member));
        }

        [TestMethod]
        public void An_ended_group_run_and_a_null_run_are_never_free()
        {
            var run = ClearedGroupRun();
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));
            run.MarkEnded("test");

            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(run, Member), "Ended: the run is going away");
            Assert.IsFalse(DungeonGemRules.IsClearedReturnFor(null, Member), "reaped: no run, and the gem handler takes ConsumeDeadGem");
        }

        // ---------------------------------------------------------------------------------------------
        // The notice
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_notice_text_names_the_gem_for_the_owner_and_the_key_for_a_member_in_ascii()
        {
            Assert.AreEqual("Your share of this Thread waits inside. Re-enter with your key to collect it - it costs nothing while the run stands.",
                GroupRosterRules.ShareWaitsInsideText(false));
            Assert.AreEqual("Your share of this Thread waits inside. Re-enter with your gem to collect it - it costs nothing while the run stands.",
                GroupRosterRules.ShareWaitsInsideText(true));

            foreach (var text in new[] { GroupRosterRules.ShareWaitsInsideText(false), GroupRosterRules.ShareWaitsInsideText(true) })
                foreach (var c in text)
                    Assert.IsTrue(c < 128, $"ASCII only: {text}");
        }

        private static ThreadGroupCacheDelivery.DeliveryWorld OutsideWorld(List<(uint Guid, string Text)> told, Func<uint, bool> isOnline = null)
            => new ThreadGroupCacheDelivery.DeliveryWorld
            {
                IsInside = _ => false,
                FormAt = (g, h) => throw new AssertFailedException("nobody is inside"),
                MoveFor = _ => throw new AssertFailedException("no move"),
                Tell = (g, t) => told.Add((g, t)),
                Deliver = (c, r) => { },
                IsOnline = isOnline,
            };

        [TestMethod]
        public void A_member_dealt_a_pile_while_outside_is_told_once_and_flagged()
        {
            var run = ClearedGroupRun();
            var told = new List<(uint Guid, string Text)>();
            var world = OutsideWorld(told);

            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));
            ThreadGroupCacheDelivery.ServeMember(run, Member, false, world);

            Assert.IsTrue(run.IsMemberDeliveryWanted(Member), "still flagged for the arrival (ruling R20)");
            CollectionAssert.AreEqual(new[] { (Member, GroupRosterRules.ShareWaitsInsideText(false)) }, told);

            // A later deal step serves them again: no second line.
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 2)));
            ThreadGroupCacheDelivery.ServeMember(run, Member, false, world);
            Assert.AreEqual(1, told.Count, "once per member per run");
        }

        [TestMethod]
        public void A_free_cleared_return_leaves_the_keys_one_time_free_entry_unclaimed()
        {
            var run = ClearedGroupRun();
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));

            // The handler's sequence on a real run: the cleared-return read, then the keyFree claim guarded by it.
            var clearedReturn = DungeonGemRules.IsClearedReturnFor(run, Member);
            Assert.IsTrue(clearedReturn);
            var keyFree = !clearedReturn && run.TryClaimFreeKeyEntry(Member);
            Assert.IsFalse(keyFree);

            var charge = DungeonGemRules.DecideEntryCharge(keyFree, 0, clearedReturn);
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, charge, "still free at zero, through the clearedReturn argument");
            Assert.AreEqual(DungeonGemRules.ClearedReturnMessage("Filos Doom"), DungeonGemRules.EntryMessage(charge, "Thread Key", "Filos Doom", 0, clearedReturn));

            Assert.IsTrue(run.TryClaimFreeKeyEntry(Member), "the key's free entry is still there to claim");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "public static bool TryHandleUse(Gem gem, Player player)");
            Assert.AreEqual(1, CountOf(body, "var keyFree = isKey && !clearedReturn && run.TryClaimFreeKeyEntry(player.Guid.Full);"), "the guarded claim, exactly once");
            Assert.AreEqual(1, CountOf(body, "TryClaimFreeKeyEntry("), "no unguarded claim elsewhere in the use path");
        }

        private static int CountOf(string text, string needle)
        {
            var count = 0;
            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }

        [TestMethod]
        public void The_owner_dealt_a_pile_while_outside_is_told_with_the_gem_wording()
        {
            var run = ClearedGroupRun();
            var told = new List<(uint Guid, string Text)>();

            Assert.IsTrue(run.AddToHeldPile(Owner, new HeldPileItem(Item(), null, 1)));
            ThreadGroupCacheDelivery.ServeMember(run, Owner, false, OutsideWorld(told));

            CollectionAssert.AreEqual(new[] { (Owner, GroupRosterRules.ShareWaitsInsideText(true)) }, told);
        }

        [TestMethod]
        public void An_offline_member_is_not_told_and_keeps_the_notice_for_when_they_are_online()
        {
            var run = ClearedGroupRun();
            var told = new List<(uint Guid, string Text)>();

            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(Item(), null, 1)));
            ThreadGroupCacheDelivery.ServeMember(run, Member, false, OutsideWorld(told, _ => false));
            Assert.AreEqual(0, told.Count, "nobody to hear it");

            ThreadGroupCacheDelivery.ServeMember(run, Member, false, OutsideWorld(told, _ => true));
            CollectionAssert.AreEqual(new[] { (Member, GroupRosterRules.ShareWaitsInsideText(false)) }, told, "the latch was not spent while offline");
        }

        [TestMethod]
        public void A_keyless_member_with_a_pile_is_flagged_but_never_told_the_return_is_free()
        {
            var run = ClearedGroupRun();
            var told = new List<(uint Guid, string Text)>();

            Assert.IsTrue(run.AddToHeldPile(Keyless, new HeldPileItem(Item(), null, 1)));
            ThreadGroupCacheDelivery.ServeMember(run, Keyless, false, OutsideWorld(told));

            Assert.IsTrue(run.IsMemberDeliveryWanted(Keyless), "flagging is unchanged");
            Assert.AreEqual(0, told.Count, "no seat: the return is not free, so it is not promised");
        }

        [TestMethod]
        public void The_notice_latch_is_true_once_per_member_and_false_for_a_stranger()
        {
            var run = ClearedGroupRun();

            Assert.IsTrue(run.TryMarkShareWaitsNotice(Member));
            Assert.IsFalse(run.TryMarkShareWaitsNotice(Member));
            Assert.IsTrue(run.TryMarkShareWaitsNotice(Owner), "per member");
            Assert.IsFalse(run.TryMarkShareWaitsNotice(0x59999999u), "a stranger");
        }

        // ---------------------------------------------------------------------------------------------
        // Source pins for the wiring that needs a live Player
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Die_sends_the_death_notice_inside_the_thread_latch_after_the_unravel_hook()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Death.cs");

            var latch = source.IndexOf("dynamicDungeonDeathInProgress = ACE.Server.ThreadDungeons.ThreadDungeonManager.GetRun(Location.Instance) != null;", StringComparison.Ordinal);
            var gate = source.IndexOf("if (dynamicDungeonDeathInProgress)", latch < 0 ? 0 : latch, StringComparison.Ordinal);
            var unravel = source.IndexOf("ACE.Server.ThreadDungeons.ThreadUnravelDeath.OnPlayerDied(Guid.Full, Location.Instance);", StringComparison.Ordinal);
            var notice = source.IndexOf("ACE.Server.ThreadDungeons.ThreadDungeonManager.OnMemberDied(this, Location.Instance);", StringComparison.Ordinal);
            var disarm = source.IndexOf("ACE.Server.ThreadDungeons.ThreadWillingExit.Disarm(Guid.Full);", StringComparison.Ordinal);

            Assert.IsTrue(latch >= 0 && latch < gate && gate < unravel && unravel < notice && notice < disarm, "inside the latch's block, after the unravel hook");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs"),
                "public static void OnMemberDied(Player player, uint instance)");
            var decide = body.IndexOf("DungeonGemRules.IsClearedReturnFor(run, guid)", StringComparison.Ordinal);
            var latchNotice = body.IndexOf("run.TryMarkShareWaitsNotice(guid);", StringComparison.Ordinal);
            var send = body.IndexOf("GroupRosterRules.ShareWaitsInsideText(guid == run.OwnerGuid)", StringComparison.Ordinal);
            Assert.IsTrue(decide >= 0 && decide < latchNotice && latchNotice < send, "told only when the return is actually free, and the deal-time line latched off");
            StringAssert.Contains(body, "catch (Exception ex)");
        }

        [TestMethod]
        public void The_live_delivery_world_supplies_the_online_seam()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGroupCacheDelivery.cs"),
                "private static DeliveryWorld LiveWorld(ThreadDungeonRun run, Landblock landblock)");
            StringAssert.Contains(body, "IsOnline = guid => PlayerManager.GetOnlinePlayer(guid)?.Session != null,");
        }
    }
}
