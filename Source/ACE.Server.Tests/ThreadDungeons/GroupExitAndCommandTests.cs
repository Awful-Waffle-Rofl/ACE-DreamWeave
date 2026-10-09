using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads Task 11: the exit guard, /spawncache and /dd status|end resolve the run a roster member
    /// actually stands in with ThreadDungeonManager.GetRunForMember, never GetRunForOwner (a held cleared run and
    /// a newer live run can coexist for one owner, and only the instance the player is standing in is correct),
    /// and use the Task 10 per-member loot predicates so a non-owner member's own held pile drives their own
    /// prompt and command, never another member's pile. A Player cannot be built in this harness, so the wiring
    /// at the three call sites is pinned on source text and the decision logic is exercised through the pure
    /// ThreadExitRules/GetRunForMemberCore functions directly.
    /// </summary>
    [TestClass]
    public class GroupExitAndCommandTests
    {
        private const uint Owner = 0x50000040u;
        private const uint Member = 0x50000041u;

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        /// <summary>A pooled, CLEARED group run of Owner plus the given members, at the default tunables.</summary>
        private static ThreadDungeonRun ClearedGroupRun(uint runId, params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 150)));

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var run = new ThreadDungeonRun(runId, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            run.MarkPooledLoot(true);
            run.MarkPopulated(planned: 2, spawned: 2, bossWcid: 0);
            run.RecordKill(false);
            run.RecordKill(false);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State, "fixture: the run must be Cleared");
            Assert.IsTrue(run.IsGroup, "fixture: the run must be a group run");
            return run;
        }

        // --------------------------------------------------------------------------------------------------
        // Behaviour: a non-owner member with their own held pile must be promptable, off their own predicates.
        // --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void ShouldPromptExit_is_true_for_a_non_owner_member_with_a_held_pile_only()
        {
            var run = ClearedGroupRun(0x80006040u, Member);

            // Member's own held pile, no shared pool loot left (ledger/bonus/overflow all empty).
            Assert.IsTrue(run.AddToHeldPile(Member, new HeldPileItem(ThreadLootTestFixtures.Item(), null, 1)));

            var isRosterMember = run.IsRosterMember(Member);
            var hasUnclaimedForMember = run.IsGroup ? run.HasUnclaimedLootFor(Member) : run.HasUnclaimedLoot;
            var holdsForMember = run.IsGroup ? ThreadLootPool.AnyCacheHoldsItemsFor(run, Member) : ThreadLootPool.AnyCacheHoldsItems(run);

            Assert.IsTrue(isRosterMember, "the exit guard's isOwner parameter now means 'may be prompted': true for any roster member");
            Assert.IsFalse(run.OwnerGuid == Member, "sanity: Member is genuinely not the owner");
            Assert.IsTrue(hasUnclaimedForMember, "HasUnclaimedLootFor sees the member's own held pile");
            Assert.IsFalse(holdsForMember, "the pile is held, not yet placed in a cache");

            Assert.IsTrue(ThreadExitRules.ShouldPromptExit(run.PooledLoot, run.State, isRosterMember, insideRunInstance: true,
                hasUnclaimedForMember, holdsForMember, bypassActive: false));

            // The Owner, with nothing of their own outstanding, must NOT be prompted off the Member's pile.
            var ownerHasUnclaimed = run.HasUnclaimedLootFor(Owner);
            var ownerHolds = ThreadLootPool.AnyCacheHoldsItemsFor(run, Owner);
            Assert.IsFalse(ownerHasUnclaimed, "another member's held pile must not count for the owner");
            Assert.IsFalse(ownerHolds);
            Assert.IsFalse(ThreadExitRules.ShouldPromptExit(run.PooledLoot, run.State, run.IsRosterMember(Owner), insideRunInstance: true,
                ownerHasUnclaimed, ownerHolds, bypassActive: false));
        }

        // --------------------------------------------------------------------------------------------------
        // Contract carried from T2/T9/T10 (progress.md): an owner can hold two non-Ended runs (a held cleared
        // run and a newer live run); every resolution here must use GetRunForMember(guid, currentInstance), which
        // prefers the run whose instance the player is standing in, never GetRunForOwner.
        // --------------------------------------------------------------------------------------------------

        [TestMethod]
        public void GetRunForMember_resolves_the_held_cleared_run_the_owner_is_standing_in_over_a_newer_live_run()
        {
            var held = ClearedGroupRun(0x80006041u, Member);

            // A newer, still-live (Active) run for the same owner/member pair, a different instance.
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150), new RosterSeat(Member, "P" + Member.ToString("X"), 8, 150) };
            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var liveActive = new ThreadDungeonRun(0x80006043u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
            liveActive.MarkPopulated(planned: 10, spawned: 10, bossWcid: 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, liveActive.State, "fixture: the newer run must still be live (Active)");

            var all = new[] { held, liveActive };

            // Standing inside the held cleared run's own instance resolves to it, not the newer live run.
            Assert.AreSame(held, ThreadDungeonManager.GetRunForMemberCore(all, Owner, held.Instance),
                "the run under the owner's feet must win over a newer live run elsewhere");
            Assert.AreSame(held, ThreadDungeonManager.GetRunForMemberCore(all, Member, held.Instance));

            // Standing nowhere in particular (currentInstance 0) falls back to the live run, per R12's precedence.
            Assert.AreSame(liveActive, ThreadDungeonManager.GetRunForMemberCore(all, Owner, 0),
                "with no current-instance match, a live run beats a held cleared one");
        }

        // --------------------------------------------------------------------------------------------------
        // Source-text pins: the exit guard, /spawncache, /dd status, /dd end (no argument) and the admin list.
        // --------------------------------------------------------------------------------------------------

        private const string ExitGuardPath = "Source/ACE.Server/ThreadDungeons/ThreadExitGuard.cs";
        private const string CommandsPath = "Source/ACE.Server/Command/Handlers/ThreadDungeonCommands.cs";

        [TestMethod]
        public void Exit_guard_resolves_the_run_by_member_not_by_owner()
        {
            var source = PooledLootSourceText.Read(ExitGuardPath);

            Assert.IsFalse(source.Contains("GetRunForOwner"), "the exit guard must never look up a run by ownership alone");

            var would = PooledLootSourceText.MethodBody(source, "public static bool WouldHoldExit(Player player)");
            StringAssert.Contains(would, "ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance)");
            StringAssert.Contains(would, "ThreadCachePlacer.IsOwnerInside(run, player)");

            var tryHold = PooledLootSourceText.MethodBody(source, "public static bool TryHoldExit(Player player, Action reissue)");
            StringAssert.Contains(tryHold, "ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance)");
            StringAssert.Contains(tryHold, "ThreadCachePlacer.IsOwnerInside(run, player)");
        }

        [TestMethod]
        public void SpawnCache_resolves_the_run_by_member_and_passes_the_requester_to_RequestPlacement()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(CommandsPath), "public static void HandleSpawnCache(Session session, params string[] parameters)");

            StringAssert.Contains(body, "ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance)");
            StringAssert.Contains(body, "ThreadCachePlacer.IsOwnerInside(run, player)", "one inside rule, shared with delivery and the exit guard");
            StringAssert.Contains(body, "ThreadCachePlacer.RequestPlacement(run, SpawnCacheRules.ModeFor(hasUnclaimed), player.Guid.Full)",
                "a group run must not choose Summon/Move off another member's pile and then place at nobody");
        }

        [TestMethod]
        public void Dd_status_and_dd_end_with_no_argument_resolve_the_run_by_member()
        {
            var source = PooledLootSourceText.Read(CommandsPath);

            var status = PooledLootSourceText.MethodBody(source, "private static void HandleStatus(Session session)");
            StringAssert.Contains(status, "ThreadDungeonManager.GetRunForMember(player.Guid.Full, player.Location.Instance)");

            var end = PooledLootSourceText.MethodBody(source, "private static void HandleEnd(Session session, string[] a)");
            StringAssert.Contains(end, "ThreadDungeonManager.GetRunForMember(session.Player.Guid.Full, session.Player.Location.Instance)");

            Assert.IsFalse(source.Contains("GetRunForOwner"), "no ThreadDungeonCommands path should still resolve a run by ownership alone");
        }

        [TestMethod]
        public void Admin_run_list_reports_the_roster_size()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(CommandsPath), "private static void HandleList(Session session)");
            StringAssert.Contains(body, "roster={r.Group.RosterSize}");
        }
    }
}
