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
    /// The Group Threads roster contract on ThreadDungeonRun, the presence helper's Player-free paths, and the
    /// manager's pure live-roster lookups. No PropertyManager key is read: GroupScaling.Compute takes every
    /// tunable as an argument.
    /// </summary>
    [TestClass]
    public class ThreadDungeonRunRosterTests
    {
        private const uint Owner = 0x50000010u;
        private const uint GemGuid = 0x80000099u;

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        private static RosterSeat Seat(uint guid, string name = null, int level = 100)
            => new RosterSeat(guid, name ?? $"P{guid:X}", 7, level);

        private static ThreadDungeonRun GroupRun(params uint[] others) => GroupRunWithId(0x80001234u, others);

        private static ThreadDungeonRun GroupRunWithId(uint runId, params uint[] others)
        {
            var seats = new List<RosterSeat> { Seat(Owner, "Owner", 150) };
            seats.AddRange(others.Select(g => Seat(g)));
            var distinct = ThreadDungeonRun.OrderRoster(seats).Count;
            return NewGroupRun(runId, seats, GroupOf(distinct));
        }

        private static ThreadDungeonRun NewGroupRun(uint runId, IReadOnlyList<RosterSeat> seats, GroupScaling group)
        {
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(runId, seats, GemGuid, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        [TestMethod]
        public void Legacy_constructor_yields_a_roster_of_one_and_solo()
        {
            var run = NewRun();

            Assert.AreEqual(1, run.Roster.Count);
            Assert.AreSame(GroupScaling.Solo, run.Group);
            Assert.IsFalse(run.IsGroup);

            var owner = run.Roster[0];
            Assert.AreEqual(run.OwnerGuid, owner.Guid);
            Assert.AreEqual("Tester", owner.Name);
            Assert.AreEqual(7u, owner.AccountId);
            Assert.IsTrue(owner.IsOwner);
            Assert.IsTrue(run.IsRosterMember(run.OwnerGuid));
            Assert.AreEqual(1.0, run.TrashLootFactor);
            Assert.AreEqual(1.0, run.BossLootFactor);
        }

        [TestMethod]
        public void Seats_constructor_reads_owner_fields_from_the_first_seat()
        {
            var run = GroupRun(0x50000020u);

            Assert.AreEqual(Owner, run.OwnerGuid);
            Assert.AreEqual("Owner", run.OwnerName);
            Assert.AreEqual(7u, run.AccountId);
            Assert.AreEqual(150, run.OwnerLevelAtStart);
            Assert.AreEqual(GemGuid, run.GemGuid);
            Assert.IsTrue(run.IsGroup);
            Assert.AreEqual(2, run.Group.RosterSize);
            Assert.IsFalse(string.IsNullOrEmpty(run.StartGroup));
        }

        [TestMethod]
        public void Roster_is_owner_first_then_ascending_guid_without_duplicates()
        {
            // Owner guid sits between the others numerically, and duplicates (including the owner's) are offered.
            var seats = new List<RosterSeat>
            {
                Seat(Owner, "Owner"), Seat(0x50000030u), null, Seat(0x50000001u), Seat(Owner, "OwnerAgain"), Seat(0x50000030u, "Dup"), Seat(0x50000020u),
            };
            var run = NewGroupRun(0x80001234u, seats, GroupOf(4));

            CollectionAssert.AreEqual(new[] { Owner, 0x50000001u, 0x50000020u, 0x50000030u }, run.Roster.Select(m => m.Guid).ToArray());
            Assert.AreEqual("Owner", run.Roster[0].Name);
            Assert.IsTrue(run.Roster[0].IsOwner);
            Assert.IsTrue(run.Roster.Skip(1).All(m => !m.IsOwner));
            Assert.AreEqual("P50000030", run.GetMember(0x50000030u).Name, "the first seat for a guid wins");
            Assert.IsNull(run.GetMember(0x59999999u));
        }

        [TestMethod]
        public void Constructor_rejects_empty_seats_and_a_mismatched_group()
        {
            Assert.ThrowsExactly<ArgumentException>(() => NewGroupRun(1, new List<RosterSeat>(), GroupScaling.Solo));
            Assert.ThrowsExactly<ArgumentException>(() => NewGroupRun(1, null, GroupScaling.Solo));
            Assert.ThrowsExactly<ArgumentException>(() => NewGroupRun(1, new List<RosterSeat> { null, Seat(2) }, GroupScaling.Solo));
            Assert.ThrowsExactly<ArgumentException>(() => NewGroupRun(1, new List<RosterSeat> { Seat(Owner), Seat(2) }, GroupScaling.Solo));
            Assert.AreSame(GroupScaling.Solo, NewGroupRun(1, new List<RosterSeat> { Seat(Owner) }, null).Group, "null group reads as Solo");
        }

        [TestMethod]
        public void IsRunGem_matches_the_owner_gem_and_member_keys_only()
        {
            const uint member = 0x50000020u;
            var run = GroupRun(member);

            Assert.IsTrue(run.IsRunGem(GemGuid));
            Assert.IsFalse(run.IsRunGem(0x80000100u));
            Assert.IsFalse(run.IsRunGem(0), "0 is never a key");

            run.SetMemberKey(member, 0x80000100u);
            Assert.IsTrue(run.IsRunGem(0x80000100u));
            Assert.AreEqual(0x80000100u, run.MemberKeyGem(member));

            run.SetMemberKey(Owner, 0x80000200u);
            Assert.IsFalse(run.IsRunGem(0x80000200u), "the owner never gets a key");
            Assert.AreEqual(0u, run.MemberKeyGem(Owner));

            run.SetMemberKey(0x59999999u, 0x80000300u);
            Assert.IsFalse(run.IsRunGem(0x80000300u), "a non-member never gets a key");

            CollectionAssert.AreEqual(new[] { (member, 0x80000100u) }, run.MemberKeys().ToArray());

            run.SetMemberKey(member, 0);
            Assert.IsFalse(run.IsRunGem(0x80000100u), "a cleared key no longer matches");
            Assert.AreEqual(0, run.MemberKeys().Count);
        }

        [TestMethod]
        public void Free_key_entry_latches_once_per_non_owner_member()
        {
            var run = GroupRun(0x50000020u, 0x50000030u);

            Assert.IsTrue(run.TryClaimFreeKeyEntry(0x50000020u));
            Assert.IsFalse(run.TryClaimFreeKeyEntry(0x50000020u));
            Assert.IsTrue(run.TryClaimFreeKeyEntry(0x50000030u), "latched per member");
            Assert.IsFalse(run.TryClaimFreeKeyEntry(Owner), "the owner's gem is never free");
            Assert.IsFalse(run.TryClaimFreeKeyEntry(0x59999999u));
        }

        [TestMethod]
        public void Per_member_latches_and_counters()
        {
            const uint member = 0x50000020u;
            var run = GroupRun(member);

            Assert.IsFalse(run.MemberEntered(member));
            run.MarkMemberEntered(member);
            Assert.IsTrue(run.MemberEntered(member));

            run.AddMemberSecondsInside(member, 15);
            run.AddMemberSecondsInside(member, -15);
            run.AddMemberXp(member, 1000);
            run.AddMemberXp(member, 500);
            run.AddMemberLum(member, 40);
            run.SetMemberLevelEnd(member, 101);
            run.MarkMemberSurveyFiled(member);

            Assert.IsTrue(run.TryMarkFellowshipWarned(member));
            Assert.IsFalse(run.TryMarkFellowshipWarned(member));
            Assert.IsFalse(run.TryMarkFellowshipWarned(0x59999999u));

            var snap = run.SnapshotRoster().Single(s => s.Guid == member);
            Assert.IsTrue(snap.Entered);
            Assert.AreEqual(15, snap.SecondsInside);
            Assert.AreEqual(1500, snap.XpGained);
            Assert.AreEqual(40, snap.LumGained);
            Assert.AreEqual(101, snap.LevelEnd);
            Assert.AreEqual(100, snap.LevelAtStart);
            Assert.IsTrue(snap.SurveyFiled);
            Assert.IsFalse(snap.IsOwner);
            Assert.IsFalse(snap.KeyGranted, "no key yet");

            var ownerSnap = run.SnapshotRoster()[0];
            Assert.IsTrue(ownerSnap.IsOwner);
            Assert.IsTrue(ownerSnap.KeyGranted, "the owner holds the run gem");
            Assert.IsFalse(ownerSnap.SurveyFiled);
            run.MarkSurveyFiled();
            Assert.IsTrue(run.SnapshotRoster()[0].SurveyFiled, "the owner's survey reads the run-level latch");
            Assert.AreEqual(0, run.XpEarned, "per-member XP does not feed the run total");
        }

        [TestMethod]
        public void Held_piles_add_take_drain_and_count_rounds()
        {
            const uint a = 0x50000020u;
            const uint b = 0x50000030u;
            var run = GroupRun(a, b);

            var r1 = run.NextDealRound();
            var r2 = run.NextDealRound();
            Assert.AreEqual(1, r1);
            Assert.AreEqual(2, r2);

            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsTrue(run.AddToHeldPile(a, new HeldPileItem(Item(), null, r1)));
            Assert.IsTrue(run.AddToHeldPile(a, new HeldPileItem(Item(), "Finder", r2)));
            Assert.IsTrue(run.AddToHeldPile(b, new HeldPileItem(Item(), null, r1)));
            Assert.IsTrue(run.AddToHeldPile(b, new HeldPileItem(Item(), null, r1)));
            Assert.IsFalse(run.AddToHeldPile(0x59999999u, new HeldPileItem(Item(), null, r1)), "non-member refused");
            Assert.IsFalse(run.AddToHeldPile(a, null));
            Assert.IsFalse(run.AddToHeldPile(a, new HeldPileItem(null, null, r1)), "no item, nothing to hold");

            Assert.IsTrue(run.AnyHeldPile);
            Assert.IsTrue(run.HasHeldPile(a));
            Assert.IsFalse(run.HasHeldPile(Owner));

            // Snapshot before any drain already reports what would be forfeited.
            Assert.AreEqual(2, run.SnapshotRoster().Single(s => s.Guid == a).PilesForfeited);
            Assert.AreEqual(1, run.SnapshotRoster().Single(s => s.Guid == b).PilesForfeited);

            var taken = run.TakeHeldPile(a);
            Assert.AreEqual(2, taken.Count);
            Assert.AreEqual("Finder", taken[1].RareFinderName);
            Assert.IsFalse(run.HasHeldPile(a));
            Assert.AreEqual(0, run.TakeHeldPile(a).Count);
            run.MarkPileReceived(a, r1);
            run.MarkPileReceived(a, r2);
            run.MarkPileReceived(a, r2);

            run.MarkMemberDeliveryWanted(b);
            var drained = run.DrainHeldPiles();
            Assert.AreEqual(2, drained.Count, "b's two items");
            Assert.IsFalse(run.AnyHeldPile);
            Assert.IsFalse(run.IsMemberDeliveryWanted(b), "drain clears the wait");

            var snapA = run.SnapshotRoster().Single(s => s.Guid == a);
            var snapB = run.SnapshotRoster().Single(s => s.Guid == b);
            Assert.AreEqual(2, snapA.PilesReceived);
            Assert.AreEqual(0, snapA.PilesForfeited);
            Assert.AreEqual(0, snapB.PilesReceived);
            Assert.AreEqual(1, snapB.PilesForfeited, "two items from ONE round is one forfeited pile");
        }

        [TestMethod]
        public void Held_pile_add_is_refused_after_MarkEnded()
        {
            const uint member = 0x50000020u;
            var run = GroupRun(member);

            Assert.IsTrue(run.MarkEnded("test"));
            Assert.IsFalse(run.AddToHeldPile(member, new HeldPileItem(Item(), null, run.NextDealRound())));
            Assert.IsFalse(run.HasHeldPile(member));
        }

        [TestMethod]
        public void Delivery_wanted_flag_claims_once()
        {
            const uint member = 0x50000020u;
            var run = GroupRun(member);

            Assert.IsFalse(run.TryClaimMemberDeliveryWanted(member));
            run.MarkMemberDeliveryWanted(member);
            Assert.IsTrue(run.IsMemberDeliveryWanted(member));
            Assert.IsTrue(run.TryClaimMemberDeliveryWanted(member));
            Assert.IsFalse(run.TryClaimMemberDeliveryWanted(member));
            Assert.IsFalse(run.IsMemberDeliveryWanted(member));

            run.MarkMemberDeliveryWanted(0x59999999u);
            Assert.IsFalse(run.IsMemberDeliveryWanted(0x59999999u));
        }

        [TestMethod]
        public void MarkGroupResolved_is_write_once_and_ignores_garbage()
        {
            var run = GroupRun(0x50000020u);
            Assert.AreEqual(1.0, run.GroupHealthMult);
            Assert.AreEqual(1.0, run.GroupCountActual);

            run.MarkGroupResolved(double.NaN, 1.3);
            run.MarkGroupResolved(1.5, 0.0);
            run.MarkGroupResolved(-1.0, 1.3);
            Assert.AreEqual(1.0, run.GroupHealthMult, "garbage ignored");

            run.MarkGroupResolved(1.5, 2.0 / 1.5);
            run.MarkGroupResolved(3.0, 9.0);
            Assert.AreEqual(1.5, run.GroupCountActual, 1e-12, "first valid write wins");
            Assert.AreEqual(2.0 / 1.5, run.GroupHealthMult, 1e-12);

            Assert.AreEqual(run.Group.TrashLootFactor(2.0 / 1.5), run.TrashLootFactor, 1e-12);
            Assert.AreEqual(run.Group.BossLootFactor(), run.BossLootFactor, 1e-12);
            Assert.AreEqual(2.0 / 1.5 * 1.05, run.TrashLootFactor, 1e-12);
            Assert.AreEqual(2.0 * 1.05, run.BossLootFactor, 1e-12);
        }

        [TestMethod]
        public void Presence_with_no_online_players_is_empty()
        {
            Func<uint, Player> nobody = _ => null;
            var group = GroupRun(0x50000020u);
            var solo = NewRun();

            Assert.AreEqual(0, ThreadRunPresence.OnlineMembersInside(group, nobody).Count);
            Assert.AreEqual(0, ThreadRunPresence.MessageRecipients(group, nobody).Count);
            Assert.AreEqual(0, ThreadRunPresence.MessageRecipients(solo, nobody).Count);
            Assert.AreEqual(0, ThreadRunPresence.OnlineMembersInside(null, nobody).Count);
            Assert.AreEqual(0, ThreadRunPresence.MessageRecipients(null, nobody).Count);
            Assert.IsFalse(ThreadRunPresence.IsMemberInside(group, null));
            Assert.IsFalse(ThreadRunPresence.IsMemberInside(null, null));

            group.MarkMemberDeliveryWanted(0x50000020u);
            Assert.IsFalse(ThreadRunPresence.AnyWantedMemberInside(group, nobody), "wanted but offline is not inside");
        }

        [TestMethod]
        public void Presence_lookups_ask_for_roster_members_in_roster_order()
        {
            var asked = new List<uint>();
            Func<uint, Player> spy = g => { asked.Add(g); return null; };

            var group = GroupRun(0x50000030u, 0x50000001u);
            ThreadRunPresence.OnlineMembersInside(group, spy);
            CollectionAssert.AreEqual(new[] { Owner, 0x50000001u, 0x50000030u }, asked);

            asked.Clear();
            ThreadRunPresence.MessageRecipients(NewRun(), spy);
            CollectionAssert.AreEqual(new[] { 0x50000001u }, asked, "a solo run asks only for its owner (Tester, 0x50000001)");
        }

        [TestMethod]
        public void Group_loot_owed_and_delivery_wanted()
        {
            const uint member = 0x50000020u;
            Func<uint, Player> nobody = _ => null;

            var solo = PooledRun();
            Assert.IsTrue(solo.TryMarkLootBonusPending());
            solo.MarkPlacementWanted();
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(solo), "solo never reads as group-owed");
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(solo, nobody));
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(null));

            var group = GroupRun(member);
            group.MarkPooledLoot(true);
            group.SetMemberKey(member, 0x80000100u); // ruling R32: only a keyed member's flag is owed
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group));
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(group, nobody));

            group.MarkMemberDeliveryWanted(member);
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(group), "a waiting member owes loot whether or not they are online");
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(group, nobody), "but delivery needs them inside");
            group.TryClaimMemberDeliveryWanted(member);

            Assert.IsTrue(group.TryMarkLootBonusPending());
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(group), "a pending bonus is owed");
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(group, nobody), "no run-level placement wanted yet");

            group.MarkPlacementWanted();
            Assert.IsTrue(ThreadRunPresence.GroupDeliveryWanted(group, nobody));

            Assert.IsTrue(group.TryClaimLootBonus());
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(group, nobody), "placement wanted with nothing left is not wanted");
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group));
        }

        [TestMethod]
        public void Group_loot_owed_while_a_pile_is_held_without_a_flag()
        {
            // R20: a member who was INSIDE when delivery hit NoRoom keeps the remainder in their pile and is NOT
            // flagged. If they then die or walk out, the pile alone must keep the run from the empty-cleared reap.
            const uint member = 0x50000020u;
            Func<uint, Player> nobody = _ => null;

            var group = GroupRun(member);
            group.MarkPooledLoot(true);
            group.SetMemberKey(member, 0x80000100u); // ruling R32: only a keyed member's pile is owed
            Assert.IsTrue(group.AddToHeldPile(member, new HeldPileItem(Item(), null, group.NextDealRound())), "NoRoom remainder returned to the pile");
            Assert.IsFalse(group.IsMemberDeliveryWanted(member), "precondition: R20 leaves an inside NoRoom member unflagged");
            Assert.AreEqual(0, group.LedgerCount);
            Assert.IsFalse(group.IsLootBonusPending);

            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(group), "the held pile is owed even with no flag and nobody inside");
            Assert.IsFalse(ThreadRunPresence.GroupDeliveryWanted(group, nobody), "delivery still needs the member inside");

            Assert.AreEqual(1, group.TakeHeldPile(member).Count);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group), "delivered: nothing owed");

            Assert.IsTrue(group.AddToHeldPile(member, new HeldPileItem(Item(), null, group.NextDealRound())));
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(group));
            group.DrainHeldPiles();
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group), "drained at run end: nothing owed");

            var solo = PooledRun();
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(solo), "solo is never group-owed");
        }

        [TestMethod]
        public void Owner_start_sweep_never_ends_a_cleared_group_run()
        {
            var states = (ThreadDungeonRunState[])Enum.GetValues(typeof(ThreadDungeonRunState));

            foreach (var state in states)
            foreach (var isGroup in new[] { false, true })
            {
                var label = $"state={state} group={isGroup}";
                var held = state == ThreadDungeonRunState.Cleared && isGroup;

                Assert.AreEqual(held, ThreadDungeonManager.IsHeldGroupRun(state, isGroup), "held: " + label);
                Assert.AreEqual(state == ThreadDungeonRunState.Cleared && !isGroup, ThreadDungeonManager.EndsAtOwnersNextStart(state, isGroup), "ends: " + label);
                Assert.AreEqual(state != ThreadDungeonRunState.Ended && !held, ThreadDungeonManager.BlocksOwnersNextStart(state, isGroup), "blocks: " + label);
                Assert.AreEqual(state != ThreadDungeonRunState.Ended, ThreadDungeonManager.CountsTowardServerCap(state), "server cap: " + label);
            }

            // Solo is exactly today's rule: a Cleared solo run is ended by the owner's next start.
            Assert.IsTrue(ThreadDungeonManager.EndsAtOwnersNextStart(ThreadDungeonRunState.Cleared, false));
            // A Cleared group run is never ended by it, whatever it owes (members may be inside looting).
            Assert.IsFalse(ThreadDungeonManager.EndsAtOwnersNextStart(ThreadDungeonRunState.Cleared, true));
        }

        private static readonly (uint OwnerGuid, uint AccountId, ThreadDungeonRunState State, bool IsGroup)[] NoRuns
            = new (uint, uint, ThreadDungeonRunState, bool)[0];

        private const uint Account = 7u;
        private const uint OtherOwner = 0x50000099u;
        private const uint OtherAccount = 8u;

        private static bool Gate(IEnumerable<(uint, uint, ThreadDungeonRunState, bool)> runStates, out string reason,
            bool onLiveRoster = false, long maxRuns = 0, long perAccount = 0)
        {
            var counts = ThreadDungeonManager.CountStartGates(runStates, Owner, Account);
            return ThreadDungeonManager.PassesStartGates(counts, onLiveRoster, maxRuns, perAccount, out reason);
        }

        [TestMethod]
        public void Start_gate_allows_one_held_group_run_and_refuses_the_second()
        {
            var one = new[] { (Owner, Account, ThreadDungeonRunState.Cleared, true) };
            Assert.IsTrue(Gate(one, out var reason), "one held cleared group run: the next start is allowed");
            Assert.IsNull(reason);

            var two = new[] { (Owner, Account, ThreadDungeonRunState.Cleared, true), (Owner, Account, ThreadDungeonRunState.Cleared, true) };
            Assert.IsFalse(Gate(two, out reason), "two held cleared group runs: the next start is refused");
            Assert.AreEqual("Your last group Thread is still open while your fellowship collects its loot. You can open a new one when it closes.", reason);
            Assert.AreEqual(ThreadDungeonManager.HeldGroupRunLimitReason, reason);

            // Another owner's held runs do not count against this owner.
            var others = new[] { (OtherOwner, OtherAccount, ThreadDungeonRunState.Cleared, true), (OtherOwner, OtherAccount, ThreadDungeonRunState.Cleared, true) };
            Assert.IsTrue(Gate(others, out _));

            // Ended group runs are not held.
            var ended = new[] { (Owner, Account, ThreadDungeonRunState.Ended, true), (Owner, Account, ThreadDungeonRunState.Ended, true) };
            Assert.IsTrue(Gate(ended, out _));
        }

        [TestMethod]
        public void Start_gate_owner_open_run_rules_are_unchanged_for_solo()
        {
            Assert.IsTrue(Gate(NoRuns, out var reason));

            foreach (var live in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active })
            foreach (var isGroup in new[] { false, true })
            {
                Assert.IsFalse(Gate(new[] { (Owner, Account, live, isGroup) }, out reason), $"{live} group={isGroup} blocks");
                Assert.AreEqual(ThreadDungeonManager.OwnerRunOpenReason, reason);
            }

            // A Cleared solo run that somehow survived the sweep still blocks, exactly as before.
            Assert.IsFalse(Gate(new[] { (Owner, Account, ThreadDungeonRunState.Cleared, false) }, out reason));
            Assert.AreEqual("You already have a dungeon open. Give its gem to the Fragment Press to close it.", reason);

            // An open run outranks the held-run limit.
            var both = new[] { (Owner, Account, ThreadDungeonRunState.Active, true), (Owner, Account, ThreadDungeonRunState.Cleared, true), (Owner, Account, ThreadDungeonRunState.Cleared, true) };
            Assert.IsFalse(Gate(both, out reason));
            Assert.AreEqual(ThreadDungeonManager.OwnerRunOpenReason, reason);

            Assert.IsFalse(Gate(NoRuns, out reason, onLiveRoster: true));
            Assert.AreEqual("You are already part of an open Thread.", reason);
        }

        [TestMethod]
        public void Start_gate_server_cap_counts_held_runs_and_account_cap_allows_one()
        {
            var held = new[] { (Owner, Account, ThreadDungeonRunState.Cleared, true) };

            Assert.IsFalse(Gate(held, out var reason, maxRuns: 1), "the server cap counts a held run");
            Assert.AreEqual(ThreadDungeonManager.ServerBusyReason, reason);
            Assert.IsTrue(Gate(held, out _, maxRuns: 2));

            Assert.IsTrue(Gate(held, out _, perAccount: 1), "one held group run does not count toward the per-account cap");

            // A second held group run for the ACCOUNT (on another character) does count.
            var accountTwo = new[] { (Owner, Account, ThreadDungeonRunState.Cleared, true), (OtherOwner, Account, ThreadDungeonRunState.Cleared, true) };
            Assert.IsFalse(Gate(accountTwo, out reason, perAccount: 1));
            Assert.AreEqual(ThreadDungeonManager.AccountRunOpenReason, reason);
            Assert.IsTrue(Gate(accountTwo, out _, perAccount: 2));

            // A live run on another character of the account counts as before; the held allowance does not cover it.
            var liveOther = new[] { (OtherOwner, Account, ThreadDungeonRunState.Active, false) };
            Assert.IsFalse(Gate(liveOther, out reason, perAccount: 1));
            Assert.AreEqual("Your account already has a dungeon open.", reason);

            // Caps of 0 are off.
            var many = Enumerable.Range(0, 5).Select(i => ((uint)(0x51000000 + i), OtherAccount, ThreadDungeonRunState.Active, false)).ToArray();
            Assert.IsTrue(Gate(many, out _, maxRuns: 0, perAccount: 0));
            Assert.IsFalse(Gate(many, out reason, maxRuns: 5));
            Assert.AreEqual("The dungeon gates are busy. Try again in a few minutes.", reason);
        }

        [TestMethod]
        public void AdmitSeats_keeps_the_owner_drops_live_roster_members_and_caps_the_roster()
        {
            var ordered = ThreadDungeonRun.OrderRoster(new List<RosterSeat>
            {
                Seat(Owner, "Owner"), Seat(0x50000030u, "C"), Seat(0x50000001u, "A"), Seat(0x50000020u, "B"),
            });

            // The owner is never dropped, even if the lookup would say they are on a live roster.
            var (kept, dropped) = ThreadDungeonManager.AdmitSeats(ordered, g => g == Owner || g == 0x50000001u || g == 0x50000030u);
            CollectionAssert.AreEqual(new[] { Owner, 0x50000020u }, kept.Select(s => s.Guid).ToArray());
            CollectionAssert.AreEqual(new[] { "A", "C" }, dropped, "dropped names in roster order");

            var none = ThreadDungeonManager.AdmitSeats(ordered, _ => false);
            Assert.AreEqual(4, none.Kept.Count);
            Assert.AreEqual(0, none.Dropped.Count);

            var nullLookup = ThreadDungeonManager.AdmitSeats(ordered, null);
            Assert.AreEqual(4, nullLookup.Kept.Count, "no lookup drops nobody");

            var empty = ThreadDungeonManager.AdmitSeats(new List<RosterSeat>(), _ => true);
            Assert.AreEqual(0, empty.Kept.Count);
            Assert.AreEqual(0, empty.Dropped.Count);
            Assert.AreEqual(0, ThreadDungeonManager.AdmitSeats(null, _ => true).Kept.Count);

            // MaxRosterSize: 105 seats in, 100 kept, the 5 highest guids dropped in roster order.
            var big = new List<RosterSeat> { Seat(Owner, "Owner") };
            big.AddRange(Enumerable.Range(1, 104).Select(i => Seat((uint)(0x60000000 + i), $"M{i}")));
            var capped = ThreadDungeonManager.AdmitSeats(ThreadDungeonRun.OrderRoster(big), _ => false);
            Assert.AreEqual(GroupScaling.MaxRosterSize, capped.Kept.Count);
            Assert.AreEqual(Owner, capped.Kept[0].Guid);
            CollectionAssert.AreEqual(new[] { "M100", "M101", "M102", "M103", "M104" }, capped.Dropped);

            // A live-roster drop does not consume a slot: the next free seat takes it.
            var mixed = ThreadDungeonManager.AdmitSeats(ThreadDungeonRun.OrderRoster(big), g => g == 0x60000001u);
            Assert.AreEqual(GroupScaling.MaxRosterSize, mixed.Kept.Count);
            CollectionAssert.AreEqual(new[] { "M1", "M101", "M102", "M103", "M104" }, mixed.Dropped);
        }

        [TestMethod]
        public void Group_loot_owed_while_shared_overflow_is_unclaimed()
        {
            var group = GroupRun(0x50000020u);
            group.MarkPooledLoot(true);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group));

            Assert.IsTrue(group.TryAddOverflow(Item(), null));
            Assert.AreEqual(0, group.LedgerCount);
            Assert.IsFalse(group.IsLootBonusPending);
            Assert.IsFalse(group.AnyHeldPile);
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(group), "overflow alone is owed");

            Assert.AreEqual(1, group.TakeAllOverflow().Count);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(group));

            var solo = PooledRun();
            Assert.IsTrue(solo.TryAddOverflow(Item(), null));
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(solo), "solo is never group-owed");
        }
        [TestMethod]
        public void GetRunForOwnerCore_prefers_the_live_run_over_a_held_cleared_one()
        {
            var held = GroupRunWithId(0x80000003u, 0x50000020u);
            held.MarkPopulated(0, 0, 0);
            var live = GroupRunWithId(0x80000002u, 0x50000030u);
            live.MarkPopulated(10, 10, 0);
            var ended = GroupRunWithId(0x80000004u);
            ended.MarkEnded("test");

            Assert.AreSame(live, ThreadDungeonManager.GetRunForOwnerCore(new[] { held, ended, live }, Owner), "live wins whatever the order");
            Assert.AreSame(live, ThreadDungeonManager.GetRunForOwnerCore(new[] { live, held }, Owner));
            Assert.AreSame(held, ThreadDungeonManager.GetRunForOwnerCore(new[] { ended, held }, Owner), "a Cleared run is still found when it is the only one");
            Assert.IsNull(ThreadDungeonManager.GetRunForOwnerCore(new[] { ended }, Owner));
            Assert.IsNull(ThreadDungeonManager.GetRunForOwnerCore(new[] { live }, 0x50000030u), "members are not owners");
            Assert.IsNull(ThreadDungeonManager.GetRunForOwnerCore(null, Owner));
        }
        [TestMethod]
        public void IsOnLiveRosterCore_counts_starting_and_active_only()
        {
            const uint member = 0x50000020u;

            var starting = GroupRunWithId(0x80000001u, member);
            Assert.IsTrue(ThreadDungeonManager.IsOnLiveRosterCore(new[] { starting }, member), "Starting is live");
            Assert.IsTrue(ThreadDungeonManager.IsOnLiveRosterCore(new[] { starting }, Owner), "the owner is on the roster too");
            Assert.IsFalse(ThreadDungeonManager.IsOnLiveRosterCore(new[] { starting }, 0x59999999u));

            var active = GroupRunWithId(0x80000002u, member);
            active.MarkPopulated(10, 10, 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, active.State);
            Assert.IsTrue(ThreadDungeonManager.IsOnLiveRosterCore(new[] { active }, member), "Active is live");

            var cleared = GroupRunWithId(0x80000003u, member);
            cleared.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, cleared.State);
            Assert.IsFalse(ThreadDungeonManager.IsOnLiveRosterCore(new[] { cleared }, member), "Cleared releases the roster");

            var ended = GroupRunWithId(0x80000004u, member);
            ended.MarkEnded("test");
            Assert.IsFalse(ThreadDungeonManager.IsOnLiveRosterCore(new[] { ended }, member), "Ended releases the roster");

            Assert.IsFalse(ThreadDungeonManager.IsOnLiveRosterCore(null, member));
            Assert.IsTrue(ThreadDungeonManager.IsOnLiveRosterCore(new[] { null, cleared, active }, member));
        }

        [TestMethod]
        public void GetRunForMemberCore_prefers_current_instance_then_live_then_newest_cleared()
        {
            const uint member = 0x50000020u;

            var cleared = GroupRunWithId(0x80000003u, member);
            cleared.MarkPopulated(0, 0, 0);
            var active = GroupRunWithId(0x80000002u, member);
            active.MarkPopulated(10, 10, 0);
            var ended = GroupRunWithId(0x80000004u, member);
            ended.MarkEnded("test");

            var all = new[] { ended, cleared, active };

            Assert.AreSame(active, ThreadDungeonManager.GetRunForMemberCore(all, member, 0), "live beats cleared");
            Assert.AreSame(cleared, ThreadDungeonManager.GetRunForMemberCore(all, member, 0x80000003u), "the instance you stand in wins");
            Assert.AreSame(active, ThreadDungeonManager.GetRunForMemberCore(all, member, 0x80000004u), "an Ended run is never returned");
            Assert.AreSame(cleared, ThreadDungeonManager.GetRunForMemberCore(new[] { ended, cleared }, member, 0));
            Assert.IsNull(ThreadDungeonManager.GetRunForMemberCore(all, 0x59999999u, 0));
            Assert.IsNull(ThreadDungeonManager.GetRunForMemberCore(null, member, 0));
        }
    }
}
