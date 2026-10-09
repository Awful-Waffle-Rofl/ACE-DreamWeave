using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads, Task 9: the manager lifecycle. The pure decisions (survey recipients, presence sample, the
    /// R29 reap hold) are driven directly; the clear path is driven through the GemDestroyer / SurveyRecorder /
    /// ClearRewardHandler seams, where PlayerManager.GetOnlinePlayer returns null for every guid; the wiring that
    /// needs a live Player or world (Tick, EndRun telemetry, the arrival hook) is pinned on source text.
    /// No PropertyManager key is read by anything here.
    /// </summary>
    [TestClass]
    public class GroupRunLifecycleTests
    {
        private const uint Owner = 0x50000010u;
        private const uint MemberA = 0x50000020u;
        private const uint MemberB = 0x50000030u;

        private static readonly DateTime Now = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime NotExpired = Now.AddHours(3);

        private const string ManagerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs";
        private const string PlayerLocationPath = "Source/ACE.Server/WorldObjects/Player_Location.cs";

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        private static ThreadDungeonRun NewRun(params uint[] others)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150) };
            seats.AddRange(others.Select(g => new RosterSeat(g, $"P{g:X}", 8, 120)));

            var spec = new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(0x80001234u, seats, 0x80000099u, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
        }

        private static RosterMemberSnapshot Snap(uint guid, bool isOwner, bool entered)
            => new RosterMemberSnapshot { Guid = guid, Name = $"P{guid:X}", IsOwner = isOwner, Entered = entered };

        // ---------------------------------------------------------------------------------------------
        // SurveyRecipients (spec 6.5, ruling R22)
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Survey_owner_online_files_without_having_entered()
        {
            var roster = new[] { Snap(Owner, true, false) };

            CollectionAssert.AreEqual(new[] { Owner }, ThreadDungeonManager.SurveyRecipients(roster, _ => true).ToArray());
            Assert.AreEqual(0, ThreadDungeonManager.SurveyRecipients(roster, _ => false).Count, "an offline owner files nothing (ruling P2-R4)");
        }

        [TestMethod]
        public void Survey_member_needs_both_online_and_entered()
        {
            var online = new HashSet<uint> { Owner, MemberA };

            var notEntered = new[] { Snap(Owner, true, false), Snap(MemberA, false, false) };
            CollectionAssert.AreEqual(new[] { Owner }, ThreadDungeonManager.SurveyRecipients(notEntered, online.Contains).ToArray(), "online but never entered");

            var entered = new[] { Snap(Owner, true, false), Snap(MemberA, false, true) };
            CollectionAssert.AreEqual(new[] { Owner, MemberA }, ThreadDungeonManager.SurveyRecipients(entered, online.Contains).ToArray(), "online and entered");

            var offline = new[] { Snap(Owner, true, false), Snap(MemberB, false, true) };
            CollectionAssert.AreEqual(new[] { Owner }, ThreadDungeonManager.SurveyRecipients(offline, online.Contains).ToArray(), "entered but offline");
        }

        [TestMethod]
        public void Survey_recipients_keep_roster_order_and_tolerate_nulls()
        {
            var roster = new[] { Snap(Owner, true, false), Snap(MemberA, false, true), null, Snap(MemberB, false, true) };
            CollectionAssert.AreEqual(new[] { Owner, MemberA, MemberB }, ThreadDungeonManager.SurveyRecipients(roster, _ => true).ToArray());

            var offlineOwner = new[] { Snap(Owner, true, true), Snap(MemberA, false, true) };
            CollectionAssert.AreEqual(new[] { MemberA }, ThreadDungeonManager.SurveyRecipients(offlineOwner, g => g != Owner).ToArray(), "members file even when the owner is offline");

            Assert.AreEqual(0, ThreadDungeonManager.SurveyRecipients(null, _ => true).Count);
            Assert.AreEqual(0, ThreadDungeonManager.SurveyRecipients(roster, null).Count);
        }

        [TestMethod]
        public void Survey_recipients_from_a_real_roster_snapshot_follow_the_entered_latch()
        {
            var run = NewRun(MemberB, MemberA);

            Assert.AreEqual(1, ThreadDungeonManager.SurveyRecipients(run.SnapshotRoster(), _ => true).Count, "only the owner before anyone entered");

            run.MarkMemberEntered(MemberB);
            CollectionAssert.AreEqual(new[] { Owner, MemberB }, ThreadDungeonManager.SurveyRecipients(run.SnapshotRoster(), _ => true).ToArray());
        }

        [TestMethod]
        public void RecordSurvey_with_nobody_online_files_nothing_and_reads_no_tunable()
        {
            // Every guid is offline under the harness, so this returns before the gem-level gate, whose
            // PropertyManager read would otherwise throw here.
            var run = NewRun(MemberA);
            run.MarkMemberEntered(MemberA);

            ThreadDungeonManager.RecordSurvey(run);

            Assert.IsFalse(run.SurveyFiled);
            Assert.IsFalse(run.SnapshotRoster().Any(m => m.SurveyFiled));
        }

        // ---------------------------------------------------------------------------------------------
        // PresenceSample (ruling R24)
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Presence_sample_returns_the_inside_members_in_roster_order()
        {
            var run = NewRun(MemberB, MemberA);
            var inside = new HashSet<uint> { MemberB, Owner };

            CollectionAssert.AreEqual(new[] { Owner, MemberB }, ThreadDungeonManager.PresenceSample(run.Roster, inside.Contains).ToArray());
            Assert.AreEqual(0, ThreadDungeonManager.PresenceSample(run.Roster, _ => false).Count);
            CollectionAssert.AreEqual(new[] { Owner, MemberA, MemberB }, ThreadDungeonManager.PresenceSample(run.Roster, _ => true).ToArray());
            Assert.AreEqual(0, ThreadDungeonManager.PresenceSample(null, _ => true).Count);
            Assert.AreEqual(0, ThreadDungeonManager.PresenceSample(run.Roster, null).Count);
        }

        // ---------------------------------------------------------------------------------------------
        // ShouldEnd: R23 pin and the R29 group hold
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void R23_cleared_observed_empty_past_grace_ends_when_nothing_is_owed()
        {
            var end = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true, createCompleted: true,
                playerEverObserved: true, playerPresent: false, groupLootHeld: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(2), now: Now, expiresUtc: NotExpired, out var reason);

            Assert.IsTrue(end);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.ClearedAndEmpty, reason);
        }

        /// <summary>The discriminating pair: identical inputs except groupLootHeld.</summary>
        [TestMethod]
        public void R29_a_cleared_group_run_owing_loot_is_not_ended_for_standing_empty()
        {
            var held = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true, createCompleted: true,
                playerEverObserved: true, playerPresent: false, groupLootHeld: true, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(30), now: Now, expiresUtc: NotExpired, out var heldReason);

            Assert.IsFalse(held);
            Assert.IsNull(heldReason);

            var released = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true, createCompleted: true,
                playerEverObserved: true, playerPresent: false, groupLootHeld: false, sinceStart: TimeSpan.FromMinutes(20),
                sinceCleared: TimeSpan.FromMinutes(30), now: Now, expiresUtc: NotExpired, out var releasedReason);

            Assert.IsTrue(released);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.ClearedAndEmpty, releasedReason);
        }

        [TestMethod]
        public void R29_hold_does_not_stop_ttl_unload_or_load_failure()
        {
            var expired = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, groupLootHeld: true,
                TimeSpan.FromHours(4), TimeSpan.FromMinutes(30), Now, Now.AddMinutes(-1), out var expiredReason);
            Assert.IsTrue(expired);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.Expired, expiredReason);

            var unloaded = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, false, true, true, false, groupLootHeld: true,
                TimeSpan.FromMinutes(20), TimeSpan.FromMinutes(30), Now, NotExpired, out var unloadedReason);
            Assert.IsTrue(unloaded);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LandblockUnloaded, unloadedReason);

            var loadFailed = ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Starting, true, false, true, false, groupLootHeld: true,
                TimeSpan.FromSeconds(61), TimeSpan.Zero, Now, NotExpired, out var loadReason);
            Assert.IsTrue(loadFailed);
            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LoadFailed, loadReason);
        }

        [TestMethod]
        public void The_legacy_ShouldEnd_overload_is_the_nothing_owed_form()
        {
            var states = new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared, ThreadDungeonRunState.Ended };

            foreach (var state in states)
            foreach (var landblock in new[] { true, false })
            foreach (var observed in new[] { true, false })
            foreach (var present in new[] { true, false })
            foreach (var sinceCleared in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(5) })
            {
                var legacy = ThreadDungeonManager.ShouldEnd(state, landblock, false, observed, present, TimeSpan.FromMinutes(2), sinceCleared, Now, NotExpired, out var legacyReason);
                var explicitForm = ThreadDungeonManager.ShouldEnd(state, landblock, false, observed, present, false, TimeSpan.FromMinutes(2), sinceCleared, Now, NotExpired, out var explicitReason);

                Assert.AreEqual(legacy, explicitForm, $"{state} lb={landblock} obs={observed} present={present} since={sinceCleared}");
                Assert.AreEqual(legacyReason, explicitReason);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // The clear path (rulings R26, R29)
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_group_clear_keeps_the_gem_and_keys_and_still_records_the_survey()
        {
            var run = NewRun(MemberA);
            run.SetMemberKey(MemberA, 0x80000100u);
            run.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);

            var gems = 0;
            var surveys = 0;
            var rewards = 0;
            var order = new List<string>();
            var originalGem = ThreadDungeonManager.GemDestroyer;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            ThreadDungeonManager.GemDestroyer = _ => { gems++; order.Add("gem"); };
            ThreadDungeonManager.SurveyRecorder = _ => { surveys++; order.Add("survey"); };
            ThreadDungeonManager.ClearRewardHandler = (_, __) => rewards++;

            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
                ThreadDungeonManager.OnRunPopulated(run);

                Assert.AreEqual(0, gems, "R29: a group run's gem and keys survive the clear");
                Assert.AreEqual(1, surveys, "the survey is still recorded, once");
                Assert.AreEqual(0, rewards, "no recipient is online and inside under the harness, so no per-recipient reward");

                run.MarkEnded(DungeonRunTelemetry.EndReasons.Expired);
                ThreadDungeonManager.OnRunEnded(run);

                Assert.AreEqual(1, gems, "the gem and keys die at run end");
                CollectionAssert.AreEqual(new[] { "survey", "gem" }, order);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = originalGem;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
            }
        }

        [TestMethod]
        public void A_solo_clear_still_destroys_the_gem_before_the_survey()
        {
            var run = NewRun();
            Assert.IsFalse(run.IsGroup);
            run.MarkPopulated(0, 0, 0);

            var order = new List<string>();
            var rewards = 0;
            var originalGem = ThreadDungeonManager.GemDestroyer;
            var originalSurvey = ThreadDungeonManager.SurveyRecorder;
            var originalReward = ThreadDungeonManager.ClearRewardHandler;
            ThreadDungeonManager.GemDestroyer = _ => order.Add("gem");
            ThreadDungeonManager.SurveyRecorder = _ => order.Add("survey");
            ThreadDungeonManager.ClearRewardHandler = (_, owner) => { rewards++; Assert.IsNull(owner); };

            try
            {
                ThreadDungeonManager.OnRunPopulated(run);
            }
            finally
            {
                ThreadDungeonManager.GemDestroyer = originalGem;
                ThreadDungeonManager.SurveyRecorder = originalSurvey;
                ThreadDungeonManager.ClearRewardHandler = originalReward;
            }

            CollectionAssert.AreEqual(new[] { "gem", "survey" }, order);
            Assert.AreEqual(1, rewards, "solo keeps its single owner call, even with the owner offline");
        }

        [TestMethod]
        public void Player_facing_strings_are_verbatim()
        {
            Assert.AreEqual("Filos Doom is cleared. Your key will let you return until this Thread closes.", ThreadDungeonManager.GroupClearedMessage("Filos Doom", isOwner: false));
            Assert.AreEqual("Filos Doom is cleared. Your gem will let you return until this Thread closes.", ThreadDungeonManager.GroupClearedMessage("Filos Doom", isOwner: true));
            Assert.AreEqual("You are no longer in this Thread's fellowship. Kill experience here is no longer shared with the group.", ThreadDungeonManager.FellowshipLeftWarning);
        }

        [TestMethod]
        public void Arrival_delivers_to_a_flagged_member_or_one_holding_a_pile_in_a_cleared_group_run()
        {
            var cleared = ThreadDungeonRunState.Cleared;

            Assert.IsTrue(ThreadDungeonManager.ArrivalWantsDelivery(true, cleared, deliveryWanted: true, hasHeldPile: false), "flagged");
            Assert.IsTrue(ThreadDungeonManager.ArrivalWantsDelivery(true, cleared, deliveryWanted: false, hasHeldPile: true), "NoRoom then left: pile, no flag");
            Assert.IsTrue(ThreadDungeonManager.ArrivalWantsDelivery(true, cleared, deliveryWanted: true, hasHeldPile: true));
            Assert.IsFalse(ThreadDungeonManager.ArrivalWantsDelivery(true, cleared, deliveryWanted: false, hasHeldPile: false), "nothing owed to this member");

            Assert.IsFalse(ThreadDungeonManager.ArrivalWantsDelivery(false, cleared, true, true), "solo never delivers on arrival");

            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Ended })
                Assert.IsFalse(ThreadDungeonManager.ArrivalWantsDelivery(true, state, true, true), $"{state}");
        }

        [TestMethod]
        public void OnPlayerArrived_ignores_a_null_player_and_an_unknown_instance()
        {
            ThreadDungeonManager.OnPlayerArrived(null, 0x00012345u);
            ThreadDungeonManager.OnPlayerArrived(null, 0);
        }

        // ---------------------------------------------------------------------------------------------
        // The group loot hold on master's empty-copy mechanism (R29 + final review F1, merged with the
        // empty grace): one idle window per copy, pulled by the landblock heartbeat
        // ---------------------------------------------------------------------------------------------

        private static readonly TimeSpan Hold = TimeSpan.FromMinutes(GroupScaling.DefaultLootHoldMinutes);
        private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

        private static readonly ThreadDungeonRunState[] AllStates =
            { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared, ThreadDungeonRunState.Ended };

        [TestMethod]
        public void Hold_is_active_only_for_a_cleared_group_run_owing_loot_with_a_positive_hold()
        {
            var cleared = ThreadDungeonRunState.Cleared;

            Assert.IsTrue(ThreadDungeonRun.GroupLootHoldActive(cleared, isGroup: true, groupLootOwed: true, lootHold: Hold), "group + owed + hold");

            Assert.IsFalse(ThreadDungeonRun.GroupLootHoldActive(cleared, isGroup: false, groupLootOwed: true, lootHold: Hold), "solo");
            Assert.IsFalse(ThreadDungeonRun.GroupLootHoldActive(cleared, isGroup: true, groupLootOwed: false, lootHold: Hold), "nothing owed");
            Assert.IsFalse(ThreadDungeonRun.GroupLootHoldActive(cleared, isGroup: true, groupLootOwed: true, lootHold: TimeSpan.Zero), "a hold of 0 is no hold");
            Assert.IsFalse(ThreadDungeonRun.GroupLootHoldActive(cleared, isGroup: true, groupLootOwed: true, lootHold: TimeSpan.FromMinutes(-1)), "a negative hold is no hold");

            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Ended })
                Assert.IsFalse(ThreadDungeonRun.GroupLootHoldActive(state, true, true, Hold), $"{state}: only a Cleared run is held");
        }

        [TestMethod]
        public void The_unload_window_is_the_loot_hold_only_while_held_and_master_grace_otherwise()
        {
            Assert.AreEqual(Hold, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, groupLootHeld: true, lootHold: Hold), "held: the hold replaces the landblock default");
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, groupLootHeld: false, lootHold: Hold), "not held: master's null");
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, groupLootHeld: true, lootHold: TimeSpan.Zero), "a hold of 0 never becomes a zero window");

            Assert.AreEqual(Grace, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Starting, Grace, true, Hold), "an uncleared run keeps master's grace");
            Assert.AreEqual(Grace, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Active, Grace, true, Hold), "an uncleared run keeps master's grace");
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Ended, Grace, true, Hold), "an ended run has no window");

            // With the flag off, the four-argument form IS master's two-argument form, whatever the hold.
            foreach (var state in AllStates)
            foreach (var hold in new[] { TimeSpan.Zero, Hold })
                Assert.AreEqual(ThreadDungeonRun.ResolveUnloadInterval(state, Grace), ThreadDungeonRun.ResolveUnloadInterval(state, Grace, false, hold), $"{state} hold={hold}");
        }

        [TestMethod]
        public void A_hold_below_masters_grace_floor_is_raised_to_the_floor()
        {
            var floor = TimeSpan.FromMinutes(ThreadDungeonManager.EmptyGraceMinutesMin);
            Assert.AreEqual(floor, ThreadDungeonRun.EmptyWindowFloor, "the floor is master's grace clamp minimum");
            Assert.AreEqual(TimeSpan.FromMinutes(5), floor);

            Assert.AreEqual(floor, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, TimeSpan.FromMinutes(1)), "1 minute is raised");
            Assert.AreEqual(floor, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, TimeSpan.FromSeconds(30)), "30 seconds is raised");
            Assert.AreEqual(floor, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, floor), "exactly the floor is unchanged");
            Assert.AreEqual(TimeSpan.FromMinutes(6), ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, TimeSpan.FromMinutes(6)), "above the floor is unchanged");
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, TimeSpan.Zero), "0 is still no hold, never raised");

            // The heartbeat consequence: an empty copy on a 1-minute hold is still loaded at 4 minutes.
            var window = ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, true, TimeSpan.FromMinutes(1)).Value;
            Assert.IsFalse(Landblock.ShouldQueueForUnload(0, Now.AddMinutes(-4), Now, window));
            Assert.IsTrue(Landblock.ShouldQueueForUnload(0, Now.AddMinutes(-4), Now, TimeSpan.FromMinutes(1)), "the discriminating control: the raw hold would have unloaded it");
        }

        /// <summary>The pull model end to end on a real group run: the override follows the state AND the stored held flag.</summary>
        [TestMethod]
        public void A_real_group_runs_override_follows_the_held_flag_and_lapses_at_run_end()
        {
            var run = NewRun(MemberA);
            run.EmptyGrace = TimeSpan.FromMinutes(12);
            var realm = new EphemeralRealm(null) { Run = run, OpenToFellowship = false };

            Assert.AreEqual(Hold, run.Group.LootHold, "the run carries the lock-time hold");
            Assert.IsFalse(run.IsGroupLootHeld);

            run.SetGroupLootHeld(true);
            Assert.AreEqual(TimeSpan.FromMinutes(12), realm.UnloadIntervalOverride, "Starting: the grace, even with the flag set");

            run.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.AreEqual(Hold, realm.UnloadIntervalOverride, "Cleared and held: the hold");

            run.SetGroupLootHeld(false);
            Assert.IsNull(realm.UnloadIntervalOverride, "Cleared, loot all collected: master's landblock default");

            run.SetGroupLootHeld(true);
            run.MarkEnded(DungeonRunTelemetry.EndReasons.Expired);
            Assert.IsNull(realm.UnloadIntervalOverride, "Ended: nothing, flag or not");
        }

        [TestMethod]
        public void A_solo_runs_override_is_masters_even_if_the_held_flag_were_set()
        {
            var run = NewRun();
            Assert.IsFalse(run.IsGroup);
            Assert.AreEqual(TimeSpan.Zero, run.Group.LootHold, "Solo carries no hold");

            var realm = new EphemeralRealm(null) { Run = run, OpenToFellowship = false };
            run.SetGroupLootHeld(true);

            run.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
            Assert.IsNull(realm.UnloadIntervalOverride, "a zero hold cannot hold a solo copy");
        }

        /// <summary>
        /// Owner ruling (2026-09-27): a Cleared solo run gets no unload window, and so no countdown warning,
        /// even with a positive hold tunable and loot owed - GroupLootHoldActive requires isGroup, so a solo
        /// run's held answer is always false and ResolveUnloadInterval falls through to its Cleared/null case.
        /// The control is the same call with isGroup true: the hold applies exactly as
        /// <see cref="Hold_is_active_only_for_a_cleared_group_run_owing_loot_with_a_positive_hold"/> already pins.
        /// </summary>
        [TestMethod]
        public void Cleared_solo_run_gets_no_unload_window_even_with_a_positive_hold_and_loot_owed()
        {
            var soloHeld = ThreadDungeonRun.GroupLootHoldActive(ThreadDungeonRunState.Cleared, isGroup: false, groupLootOwed: true, lootHold: Hold);
            Assert.IsFalse(soloHeld, "solo is never held");
            Assert.IsNull(ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, soloHeld, Hold), "no window: the run falls straight to ShouldEnd's cleared-and-empty branch");

            var groupHeld = ThreadDungeonRun.GroupLootHoldActive(ThreadDungeonRunState.Cleared, isGroup: true, groupLootOwed: true, lootHold: Hold);
            Assert.IsTrue(groupHeld, "control: the same inputs, but a group run");
            Assert.AreEqual(Hold, ThreadDungeonRun.ResolveUnloadInterval(ThreadDungeonRunState.Cleared, Grace, groupHeld, Hold), "a group run keeps its hold window");
        }

        [TestMethod]
        public void The_heartbeat_unloads_a_held_empty_copy_only_after_the_hold()
        {
            var lastActive = Now.AddMinutes(-29);
            Assert.IsFalse(Landblock.ShouldQueueForUnload(0, lastActive, Now, Hold), "empty 29 minutes, hold 30: stays loaded");
            Assert.IsTrue(Landblock.ShouldQueueForUnload(0, Now.AddMinutes(-31), Now, Hold), "empty 31 minutes: queued for unload");
            Assert.IsFalse(Landblock.ShouldQueueForUnload(1, Now.AddMinutes(-31), Now, Hold), "a member inside always holds it");

            // Without the hold the same copy would have gone at the five-minute default.
            Assert.IsTrue(Landblock.ShouldQueueForUnload(0, lastActive, Now, Landblock.UnloadInterval), "the discriminating control");
        }

        /// <summary>The discriminating pair: identical inputs except groupLootHeld.</summary>
        [TestMethod]
        public void A_held_run_that_idles_out_ends_as_landblock_unloaded_and_still_files_cleared()
        {
            Assert.IsFalse(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: true, createCompleted: true,
                playerEverObserved: true, playerPresent: false, groupLootHeld: true, sinceStart: TimeSpan.FromMinutes(90),
                sinceCleared: TimeSpan.FromMinutes(60), now: Now, expiresUtc: NotExpired, out _), "held while the copy is loaded");

            Assert.IsTrue(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, landblockPresent: false, createCompleted: true,
                playerEverObserved: true, playerPresent: false, groupLootHeld: true, sinceStart: TimeSpan.FromMinutes(90),
                sinceCleared: TimeSpan.FromMinutes(60), now: Now, expiresUtc: NotExpired, out var reason), "the copy unloaded after the hold");

            Assert.AreEqual(DungeonRunTelemetry.EndReasons.LandblockUnloaded, reason);
            Assert.AreEqual(DungeonRunTelemetry.EndStates.Cleared, DungeonRunTelemetry.EndStateFor(ThreadDungeonRunState.Cleared, reason), "a cleared run still files as cleared");
        }

        [TestMethod]
        public void TTL_still_ends_a_held_run()
        {
            foreach (var expires in new[] { Now, Now.AddMinutes(-5) })
            {
                Assert.IsTrue(ThreadDungeonManager.ShouldEnd(ThreadDungeonRunState.Cleared, true, true, true, false, groupLootHeld: true,
                    TimeSpan.FromHours(3), TimeSpan.FromMinutes(30), Now, expires, out var reason));
                Assert.AreEqual(DungeonRunTelemetry.EndReasons.Expired, reason);
            }

            // And the countdown for a held copy is bounded by the TTL, exactly as master's grace countdown is.
            Assert.AreEqual(TimeSpan.FromMinutes(2), ThreadDungeonRun.GraceRemaining(Now, Hold, Now.AddMinutes(2), Now));
        }

        [TestMethod]
        public void The_held_flag_changes_nothing_when_false_so_solo_reaping_is_unchanged()
        {
            foreach (var state in AllStates)
            foreach (var landblock in new[] { true, false })
            foreach (var observed in new[] { true, false })
            foreach (var present in new[] { true, false })
            foreach (var sinceCleared in new[] { TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(45) })
            {
                var label = $"{state} lb={landblock} obs={observed} present={present} since={sinceCleared}";

                // GroupLootHoldActive is false for every solo run, so this is the whole solo input space.
                var legacy = ThreadDungeonManager.ShouldEnd(state, landblock, false, observed, present, TimeSpan.FromMinutes(2), sinceCleared, Now, NotExpired, out var legacyReason);
                var notHeld = ThreadDungeonManager.ShouldEnd(state, landblock, false, observed, present, false, TimeSpan.FromMinutes(2), sinceCleared, Now, NotExpired, out var notHeldReason);

                Assert.AreEqual(legacy, notHeld, label);
                Assert.AreEqual(legacyReason, notHeldReason, label);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Empty-grace warning recipients (merged ruling: "whoever is relevant")
        // ---------------------------------------------------------------------------------------------

        private const uint RunGem = 0x80000099u;
        private const uint KeyA = 0x80000100u;
        private const uint KeyB = 0x80000101u;

        [TestMethod]
        public void Solo_warning_goes_to_the_reachable_owner_only_exactly_as_master()
        {
            var run = NewRun();

            CollectionAssert.AreEqual(new[] { (Owner, RunGem) },
                ThreadDungeonRun.GraceWarningRecipients(false, Owner, RunGem, run.Roster, run.MemberKeyGem, _ => true).ToArray());

            Assert.AreEqual(0, ThreadDungeonRun.GraceWarningRecipients(false, Owner, RunGem, run.Roster, run.MemberKeyGem, _ => false).Count, "offline or inside: nobody, latch untouched");
            Assert.AreEqual(0, ThreadDungeonRun.GraceWarningRecipients(false, Owner, RunGem, run.Roster, run.MemberKeyGem, null).Count);
        }

        [TestMethod]
        public void Group_warning_goes_to_reachable_owner_and_keyed_members_in_roster_order()
        {
            var run = NewRun(MemberB, MemberA);
            run.SetMemberKey(MemberA, KeyA);
            run.SetMemberKey(MemberB, KeyB);

            CollectionAssert.AreEqual(new[] { (Owner, RunGem), (MemberA, KeyA), (MemberB, KeyB) },
                ThreadDungeonRun.GraceWarningRecipients(true, Owner, RunGem, run.Roster, run.MemberKeyGem, _ => true).ToArray(), "each with the gem they would step back in with");

            var reachable = new HashSet<uint> { MemberA, MemberB };
            CollectionAssert.AreEqual(new[] { (MemberA, KeyA), (MemberB, KeyB) },
                ThreadDungeonRun.GraceWarningRecipients(true, Owner, RunGem, run.Roster, run.MemberKeyGem, reachable.Contains).ToArray(), "members are warned with the owner offline or inside");

            run.SetMemberKey(MemberB, 0);
            CollectionAssert.AreEqual(new[] { (Owner, RunGem), (MemberA, KeyA) },
                ThreadDungeonRun.GraceWarningRecipients(true, Owner, RunGem, run.Roster, run.MemberKeyGem, _ => true).ToArray(), "a keyless member cannot step back in and is not warned");

            Assert.AreEqual(0, ThreadDungeonRun.GraceWarningRecipients(true, Owner, RunGem, run.Roster, run.MemberKeyGem, _ => false).Count, "nobody reachable");
            Assert.AreEqual(0, ThreadDungeonRun.GraceWarningRecipients(true, Owner, RunGem, null, run.MemberKeyGem, _ => true).Count);
        }

        [TestMethod]
        public void A_key_holders_warning_says_key_and_the_owner_keeps_masters_exact_text()
        {
            var fiveMinutes = TimeSpan.FromMinutes(5);

            const string ownerText = "Your Thread in Filos Doom collapses in 5 minutes. Use your Thread Gem to step back in before then.";
            const string keyText = "Your Thread in Filos Doom collapses in 5 minutes. Use your Thread Key to step back in before then.";

            Assert.AreEqual(ownerText, ThreadDungeonRun.BuildGraceWarning("Filos Doom", fiveMinutes, canReEnter: true), "master's form");
            Assert.AreEqual(ownerText, ThreadDungeonRun.BuildGraceWarning("Filos Doom", fiveMinutes, canReEnter: true, isKeyHolder: false), "owner and solo");
            Assert.AreEqual(keyText, ThreadDungeonRun.BuildGraceWarning("Filos Doom", fiveMinutes, canReEnter: true, isKeyHolder: true), "a member with a key");

            Assert.AreEqual("Your Thread in Filos Doom collapses in 30 seconds.",
                ThreadDungeonRun.BuildGraceWarning("Filos Doom", TimeSpan.FromSeconds(30), canReEnter: false, isKeyHolder: true), "no entries left drops the sentence for a key too");

            foreach (var text in new[] { ownerText, keyText })
                Assert.IsTrue(text.All(c => c < 128), "ASCII only");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void WarnIfEmptyGraceRunningOut(ThreadDungeonRun run, DateTime lastActiveUtc, TimeSpan window, DateTime now)");
            StringAssert.Contains(body, "var isKeyHolder = guid != run.OwnerGuid;");
            StringAssert.Contains(body, "ThreadDungeonRun.BuildGraceWarning(dungeonName, remaining, canReEnter, isKeyHolder)");
        }

        [TestMethod]
        public void The_warning_leaves_the_latch_alone_when_nobody_is_reachable_and_reads_the_resolved_window()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void WarnIfEmptyGraceRunningOut(ThreadDungeonRun run, DateTime lastActiveUtc, TimeSpan window, DateTime now)");

            var recipients = body.IndexOf("ThreadDungeonRun.GraceWarningRecipients(run.IsGroup, run.OwnerGuid, run.GemGuid, run.Roster, run.MemberKeyGem,", StringComparison.Ordinal);
            var empty = body.IndexOf("if (recipients.Count == 0)\n                return;", StringComparison.Ordinal);
            var remaining = body.IndexOf("ThreadDungeonRun.GraceRemaining(lastActiveUtc, window, run.ExpiresUtc, now)", StringComparison.Ordinal);
            var latch = body.IndexOf("run.TryAdvanceGraceWarning(remaining)", StringComparison.Ordinal);
            var loop = body.IndexOf("foreach (var (guid, gemGuid) in recipients)", StringComparison.Ordinal);
            var enqueue = body.IndexOf("recipient.EnqueueAction(", StringComparison.Ordinal);

            Assert.IsTrue(recipients >= 0 && recipients < empty && empty < remaining && remaining < latch && latch < loop && loop < enqueue);
            Assert.IsFalse(body.Contains("run.EmptyGrace"), "the window comes from ResolveUnloadInterval, never the grace alone");
        }


        // ---------------------------------------------------------------------------------------------
        // Final review F2 (ruling R32): keyless members owe nothing
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void R32_a_keyless_members_pile_and_flag_do_not_make_group_loot_owed()
        {
            var run = NewRun(MemberB, MemberA);
            run.MarkPooledLoot(true);
            run.SetMemberKey(MemberA, 0x80000100u);

            CollectionAssert.AreEqual(new[] { Owner, MemberA }, run.DealSeats().ToArray(), "owner plus keyed members, in roster order");

            Assert.IsTrue(run.AddToHeldPile(MemberB, new HeldPileItem(ThreadLootTestFixtures.Item(), null, run.NextDealRound())));
            run.MarkMemberDeliveryWanted(MemberB);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(run), "MemberB has no key: their pile and flag hold nothing");

            run.SetMemberKey(MemberB, 0x80000101u);
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "the same pile is owed once MemberB holds a key");

            run.SetMemberKey(MemberB, 0);
            Assert.IsFalse(ThreadRunPresence.GroupLootOwed(run), "released again");

            Assert.IsTrue(run.AddToHeldPile(MemberA, new HeldPileItem(ThreadLootTestFixtures.Item(), null, run.NextDealRound())));
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "a keyed member's pile is owed");
            run.TakeHeldPile(MemberA);

            run.MarkMemberDeliveryWanted(Owner);
            Assert.IsTrue(ThreadRunPresence.GroupLootOwed(run), "the owner always holds a seat, with no key record");
        }

        // ---------------------------------------------------------------------------------------------
        // Final review F5: S8 after a re-formed fellowship
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void F5_the_fellowship_warning_skips_a_reformed_fellowship_that_holds_every_roster_member()
        {
            var roster = NewRun(MemberA, MemberB).Roster;
            var everyone = new HashSet<uint> { Owner, MemberA, MemberB };
            var withStranger = new HashSet<uint> { Owner, MemberA, MemberB, 0x50000099u };
            var missingB = new HashSet<uint> { Owner, MemberA, 0x50000099u };

            Assert.IsFalse(ThreadDungeonManager.FellowshipLeftWarningDue(true, missingB.Contains, roster), "still the locked fellowship: never warned");
            Assert.IsFalse(ThreadDungeonManager.FellowshipLeftWarningDue(false, everyone.Contains, roster), "a new fellowship of the same people");
            Assert.IsFalse(ThreadDungeonManager.FellowshipLeftWarningDue(false, withStranger.Contains, roster), "every roster member is in it, plus someone else");
            Assert.IsTrue(ThreadDungeonManager.FellowshipLeftWarningDue(false, missingB.Contains, roster), "a roster member is missing");
            Assert.IsTrue(ThreadDungeonManager.FellowshipLeftWarningDue(false, null, roster), "no fellowship at all");
        }

        // ---------------------------------------------------------------------------------------------
        // Source-text pins for the wiring that needs a live world
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Tick_keeps_the_pinned_solo_retry_and_adds_the_group_branch()
        {
            var tick = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "public static void Tick()");

            var sample = tick.IndexOf("SampleMemberPresence(run);", StringComparison.Ordinal);
            var owed = tick.IndexOf("ThreadRunPresence.GroupLootOwed(run)", StringComparison.Ordinal);
            // Owner ruling (2026-09-27): groupLootHeld is GroupLootHoldActive alone - it requires isGroup, so a
            // solo run's answer is always false and it is never put on the loot hold.
            var held = tick.IndexOf("var groupLootHeld = ThreadDungeonRun.GroupLootHoldActive(state, run.IsGroup, groupLootOwed, run.Group.LootHold);", StringComparison.Ordinal);
            var store = tick.IndexOf("run.SetGroupLootHeld(groupLootHeld);", StringComparison.Ordinal);
            var shouldEnd = tick.IndexOf("ShouldEnd(state, landblock != null, createCompleted, run.PlayerEverObserved, playerPresent, groupLootHeld,", StringComparison.Ordinal);
            var window = tick.IndexOf("var emptyWindow = ThreadDungeonRun.ResolveUnloadInterval(state, run.EmptyGrace, run.IsGroupLootHeld, run.Group.LootHold);", StringComparison.Ordinal);
            var warn = tick.IndexOf("WarnIfEmptyGraceRunningOut(run, landblock.LastActiveUtc, emptyWindow.Value, now);", StringComparison.Ordinal);
            var solo = tick.IndexOf("if (!run.IsGroup && state == ThreadDungeonRunState.Cleared && run.IsPlacementWanted", StringComparison.Ordinal);
            var pinned = tick.IndexOf("ThreadCachePlacer.IsOwnerInside(run, PlayerManager.GetOnlinePlayer(run.OwnerGuid))", StringComparison.Ordinal);
            var retry = tick.IndexOf("RetryWaitingDelivery(run);", StringComparison.Ordinal);
            var group = tick.IndexOf("else if (state == ThreadDungeonRunState.Cleared && run.IsGroup && ThreadRunPresence.GroupDeliveryWanted(run))", StringComparison.Ordinal);
            var trigger = tick.IndexOf("PooledLootTrigger(run);", group < 0 ? 0 : group, StringComparison.Ordinal);
            var populate = tick.IndexOf("ThreadDungeonSpawner.TryPopulate(run, landblock);", StringComparison.Ordinal);

            Assert.IsTrue(sample >= 0 && sample < owed && owed < held && held < store && store < shouldEnd, "presence is sampled, then the hold is computed and stored for the heartbeat, before the reap decision");
            Assert.IsTrue(shouldEnd < window && window < warn && warn < solo, "the countdown reads the same window the heartbeat measures");
            Assert.AreEqual(0, CountOf(tick, "SetActive("), "no keep-alive refresh: the empty-copy lifetime is the heartbeat's override alone");
            Assert.IsFalse(tick.Contains("KeepsCopyAlive"), "the parallel keep-alive mechanism is gone");
            Assert.IsTrue(shouldEnd < solo && solo < pinned && pinned < retry, "the solo retry keeps its pinned owner condition");
            Assert.IsTrue(retry < group && group < trigger && trigger < populate, "the group branch retries through PooledLootTrigger before populating");
        }

        [TestMethod]
        public void AnnounceCleared_destroys_the_gem_only_for_solo_and_before_the_survey()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void AnnounceCleared(ThreadDungeonRun run)");

            var recipients = body.IndexOf("ThreadRunPresence.MessageRecipients(run)", StringComparison.Ordinal);
            var perMember = body.IndexOf("ClearRewardHandler(run, member);", StringComparison.Ordinal);
            var guard = body.IndexOf("if (!run.IsGroup)\n                GemDestroyer(run);", StringComparison.Ordinal);
            var gem = body.IndexOf("GemDestroyer(run);", StringComparison.Ordinal);
            var survey = body.IndexOf("SurveyRecorder(run);", StringComparison.Ordinal);

            var line = body.IndexOf("GroupClearedMessage(run.Dungeon.Name, member.Guid.Full == run.OwnerGuid)", StringComparison.Ordinal);

            Assert.IsTrue(recipients >= 0 && recipients < line && line < perMember, "each recipient's line names gem (owner) or key (member), then their rewards");
            Assert.IsTrue(guard >= 0, "GemDestroyer at the clear is solo-only (R29)");
            Assert.IsTrue(gem >= 0 && gem < survey, "GemDestroyer still precedes SurveyRecorder");
            Assert.AreEqual(1, CountOf(body, "GemDestroyer(run);"));
        }

        [TestMethod]
        public void AnnounceProgress_messages_every_recipient()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void AnnounceProgress(ThreadDungeonRun run)");

            StringAssert.Contains(body, "ThreadRunPresence.MessageRecipients(run)");
            Assert.IsFalse(body.Contains("GetOnlinePlayer(run.OwnerGuid)"), "no owner-only send left");
        }

        [TestMethod]
        public void RecordSurvey_iterates_recipients_on_their_own_queues()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "public static void RecordSurvey(ThreadDungeonRun run)");

            var recipients = body.IndexOf("SurveyRecipients(run.SnapshotRoster(), guid => PlayerManager.GetOnlinePlayer(guid) != null)", StringComparison.Ordinal);
            var spawned = body.IndexOf("run.Spawned <= 0", StringComparison.Ordinal);
            var minLevel = body.IndexOf("dynamic_dungeons_survey_min_level", StringComparison.Ordinal);
            var loop = body.IndexOf("foreach (var guid in recipients)", StringComparison.Ordinal);
            var enqueue = body.IndexOf("owner.EnqueueAction(", StringComparison.Ordinal);
            var record = body.IndexOf("DungeonSurveyRules.Record(", StringComparison.Ordinal);
            var runLatch = body.IndexOf("run.MarkSurveyFiled();", StringComparison.Ordinal);
            var memberLatch = body.IndexOf("run.MarkMemberSurveyFiled(guid);", StringComparison.Ordinal);

            Assert.IsTrue(recipients >= 0 && recipients < spawned && spawned < minLevel && minLevel < loop, "gates once, after the online check, before the loop");
            Assert.IsTrue(loop < enqueue && enqueue < record && record < runLatch && runLatch < memberLatch, "each filing runs on the recipient's queue and latches after the ledger write");
            Assert.AreEqual(1, CountOf(body, "EnqueueAction("));
        }

        [TestMethod]
        public void DestroyGem_reaches_every_online_key_holder()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "public static void DestroyGem(ThreadDungeonRun run)");

            var owner = body.IndexOf("EnqueueGemConsume(owner, run.GemGuid, runText);", StringComparison.Ordinal);
            var keys = body.IndexOf("run.MemberKeys()", StringComparison.Ordinal);
            var member = body.IndexOf("EnqueueGemConsume(member, keyGemGuid, runText);", StringComparison.Ordinal);

            Assert.IsTrue(owner >= 0 && owner < keys && keys < member);
            Assert.IsFalse(body.Contains("return;"), "an offline owner must not skip the members' keys");
        }

        [TestMethod]
        public void Telemetry_sets_every_member_level_before_building_the_row()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void RecordRunTelemetry(ThreadDungeonRun run, ThreadDungeonRunState priorState, string why)");

            var loop = body.IndexOf("foreach (var member in run.Roster)", StringComparison.Ordinal);
            var set = body.IndexOf("run.SetMemberLevelEnd(member.Guid, member.IsOwner ? charLevel : PlayerManager.FindByGuid(member.Guid)?.Level ?? 0);", StringComparison.Ordinal);
            var build = body.IndexOf("DungeonRunTelemetry.BuildRow(", StringComparison.Ordinal);

            Assert.IsTrue(loop >= 0 && loop < set && set < build);
        }

        [TestMethod]
        public void Arrival_hook_routes_through_OnPlayerArrived()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PlayerLocationPath), "private void NoteThreadDungeonArrival()");
            StringAssert.Contains(body, "ThreadDungeonManager.OnPlayerArrived(this, instance);");
            Assert.IsFalse(body.Contains("MarkPlayerObserved"), "the latch moved into OnPlayerArrived");

            var arrived = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "public static void OnPlayerArrived(Player player, uint instance)");
            var lookup = arrived.IndexOf("GetRun(instance)", StringComparison.Ordinal);
            var observed = arrived.IndexOf("run.MarkPlayerObserved();", StringComparison.Ordinal);
            var entered = arrived.IndexOf("run.MarkMemberEntered(guid);", StringComparison.Ordinal);
            var trigger = arrived.IndexOf("ArrivalWantsDelivery(run.IsGroup, run.State, run.IsMemberDeliveryWanted(guid), run.HasHeldPile(guid))", StringComparison.Ordinal);
            var call = arrived.IndexOf("PooledLootTrigger(run);", StringComparison.Ordinal);

            Assert.IsTrue(lookup >= 0 && lookup < observed && observed < entered && entered < trigger && trigger < call);
            Assert.IsFalse(arrived.Contains("GetRunForOwner"), "resolved by instance, never by owner guid");
        }

        private static int CountOf(string haystack, string needle)
        {
            var count = 0;
            for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                count++;
            return count;
        }
    }
}
