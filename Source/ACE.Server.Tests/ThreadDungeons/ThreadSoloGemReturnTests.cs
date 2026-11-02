using System;

using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Owner ruling 2026-10-10 (superseding ruling A1): a SOLO Thread gem survives the clear, a death, a logout and an
    /// admin teleport, and is retired only by its owner's willing exit from the cleared copy; re-entering a cleared
    /// solo run is free. The pure decisions are driven directly; the wiring that needs a live Player (Teleport, Die,
    /// the exit guard, the gem handler) is pinned on source text. No PropertyManager key is read by anything here.
    /// </summary>
    [TestClass]
    public class ThreadSoloGemReturnTests
    {
        private const uint Owner = 0x50000071u;
        private const uint Stranger = 0x50000072u;
        private const uint MemberA = 0x50000073u;
        private const uint RunId = 0x80005678u;
        private const uint OtherInstance = 0x80005679u;
        private const uint GemGuid = 0x800000A1u;

        private static readonly DateTime Now = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);

        private const string ManagerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonManager.cs";
        private const string GuardPath = "Source/ACE.Server/ThreadDungeons/ThreadExitGuard.cs";
        private const string HandlerPath = "Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs";
        private const string PlayerLocationPath = "Source/ACE.Server/WorldObjects/Player_Location.cs";
        private const string PlayerDeathPath = "Source/ACE.Server/WorldObjects/Player_Death.cs";

        private static DungeonGemSpec BoundSpec(uint owner = Owner) => new DungeonGemSpec("any", 100, 6, "any", 1, new (string, double)[0], 0, 0).WithBinding(RunId, owner);

        private static ThreadDungeonRun SoloRun()
        {
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(RunId, Owner, "Owner", 7, GemGuid, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180));
        }

        private static ThreadDungeonRun GroupRun()
        {
            var seats = new System.Collections.Generic.List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 150), new RosterSeat(MemberA, "A", 8, 150) };
            var spec = new DungeonGemSpec("filos_doom", 100, 6, "any", 1, new (string, double)[0], 0, 0);
            var dungeon = new DungeonEntryDef { Id = "filos_doom", Name = "Filos Doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            var group = GroupScaling.Compute(2, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);
            return new ThreadDungeonRun(RunId, seats, GemGuid, spec, dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), group);
        }

        // ---------------------------------------------------------------------------------------------
        // The retire latch and every dead-gem path
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Retiring_the_owner_gem_makes_every_dead_gem_check_see_it_as_dead()
        {
            var run = SoloRun();
            run.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);

            Assert.IsFalse(run.IsOwnerGemRetired);
            Assert.IsTrue(run.IsRunGem(GemGuid), "a cleared solo run still knows its gem");
            Assert.IsTrue(DungeonGemRules.IsRunLiveForGem(run, GemGuid), "gem use: live, so it can re-enter");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(BoundSpec(), GemGuid, _ => run), "sweeper and Fragment Press: not dead");
            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(BoundSpec(), Owner, 1, DungeonGemRules.IsRunLiveForGem(run, GemGuid), true, 150, 150, out _));

            run.RetireOwnerGem();
            run.RetireOwnerGem();

            Assert.IsTrue(run.IsOwnerGemRetired);
            Assert.IsFalse(run.IsRunGem(GemGuid), "a retired owner gem is no longer the run's");
            Assert.IsFalse(DungeonGemRules.IsRunLiveForGem(run, GemGuid));
            Assert.IsTrue(ThreadDungeonSweeper.IsDeadBoundGemCore(BoundSpec(), GemGuid, _ => run), "the sweeper treats it as dead");
            Assert.AreEqual(GemUseDecision.ConsumeDeadGem, DungeonGemRules.Decide(BoundSpec(), Owner, 1, DungeonGemRules.IsRunLiveForGem(run, GemGuid), true, 150, 150, out _),
                "a lazy use consumes it");
            Assert.IsFalse(run.IsRunGem(0), "a guid of 0 never matches");
        }

        [TestMethod]
        public void Retiring_the_owner_gem_leaves_member_keys_alone()
        {
            var run = GroupRun();
            run.SetMemberKey(MemberA, 0x800000B1u);

            run.RetireOwnerGem();

            Assert.IsFalse(run.IsRunGem(GemGuid));
            Assert.IsTrue(run.IsRunGem(0x800000B1u), "a member key is untouched by the owner latch");
        }

        // ---------------------------------------------------------------------------------------------
        // ThreadWillingExit: the pure decision
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Only_a_solo_owner_is_armed()
        {
            Assert.IsTrue(ThreadWillingExit.ShouldArm(true, false, Owner, Owner), "solo owner");
            Assert.IsFalse(ThreadWillingExit.ShouldArm(true, true, Owner, Owner), "group owner: the group gem dies at run end");
            Assert.IsFalse(ThreadWillingExit.ShouldArm(true, false, Owner, Stranger), "not the owner");
            Assert.IsFalse(ThreadWillingExit.ShouldArm(false, false, Owner, Owner), "no run");
        }

        /// <summary>The full truth table, each row changing exactly one input from the retiring case.</summary>
        [TestMethod]
        public void ShouldRetire_truth_table()
        {
            bool Retire(bool armed = true, uint armedInstance = RunId, DateTime? armedUtc = null, uint from = RunId, uint to = 0,
                bool runFound = true, bool isGroup = false, ThreadDungeonRunState state = ThreadDungeonRunState.Cleared, bool isOwner = true)
                => ThreadWillingExit.ShouldRetire(armed, armedInstance, armedUtc ?? Now.AddSeconds(-5), Now, from, to, runFound, isGroup, state, isOwner);

            Assert.IsTrue(Retire(), "armed solo owner leaving the cleared copy");

            Assert.IsFalse(Retire(armed: false), "unarmed: a forced move (death, logout eviction, admin teleport)");
            Assert.IsFalse(Retire(armedInstance: OtherInstance), "armed for another instance");
            Assert.IsFalse(Retire(to: RunId), "a same-instance teleport is not leaving");
            Assert.IsFalse(Retire(runFound: false), "no run at the instance being left");
            Assert.IsFalse(Retire(isGroup: true), "group run: unchanged, its gem dies at run end");
            Assert.IsFalse(Retire(isOwner: false), "not the owner");

            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Ended })
                Assert.IsFalse(Retire(state: state), $"{state}: only a Cleared run's gem is retired");

            Assert.IsTrue(Retire(armedUtc: Now - ThreadWillingExit.MaxArmAge), "exactly at the bound is still fresh");
            Assert.IsFalse(Retire(armedUtc: Now - ThreadWillingExit.MaxArmAge - TimeSpan.FromSeconds(1)), "stale: an aborted recall's marker");
            Assert.IsFalse(Retire(armedUtc: Now.AddSeconds(1)), "a marker from the future is not trusted");

            Assert.AreEqual(TimeSpan.FromSeconds(60), ThreadWillingExit.MaxArmAge);
        }

        [TestMethod]
        public void A_recall_armed_while_active_retires_once_the_run_has_cleared_and_never_if_it_has_not()
        {
            var run = SoloRun();
            run.MarkPopulated(1, 1, 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, run.State);

            try
            {
                // Armed during an Active run, teleport lands while still Active: the gem is kept.
                ThreadWillingExit.ArmIfSoloOwner(run, Owner, RunId, Now);
                Assert.IsNull(ThreadWillingExit.TakeForTeleport(Owner, RunId, 0, Now.AddSeconds(8), _ => run));
                Assert.IsFalse(ThreadWillingExit.IsArmed(Owner), "the marker is taken either way");

                // Armed during an Active run, the last kill lands mid-animation: the teleport retires.
                ThreadWillingExit.ArmIfSoloOwner(run, Owner, RunId, Now);
                run.RecordKill(false);
                Assert.AreEqual(ThreadDungeonRunState.Cleared, run.State);
                Assert.AreSame(run, ThreadWillingExit.TakeForTeleport(Owner, RunId, 0, Now.AddSeconds(8), _ => run));
                Assert.IsNull(ThreadWillingExit.TakeForTeleport(Owner, RunId, 0, Now.AddSeconds(9), _ => run), "a marker is taken once");
            }
            finally
            {
                ThreadWillingExit.Disarm(Owner);
            }
        }

        [TestMethod]
        public void A_death_disarms_so_no_later_teleport_retires()
        {
            var run = SoloRun();
            run.MarkPopulated(0, 0, 0);

            try
            {
                ThreadWillingExit.ArmIfSoloOwner(run, Owner, RunId, Now);
                Assert.IsTrue(ThreadWillingExit.IsArmed(Owner));

                ThreadWillingExit.Disarm(Owner);

                Assert.IsFalse(ThreadWillingExit.IsArmed(Owner));
                Assert.IsNull(ThreadWillingExit.TakeForTeleport(Owner, RunId, 0, Now.AddSeconds(3), _ => run), "cleared by death: the death teleport keeps the gem");
                Assert.IsFalse(run.IsOwnerGemRetired);
            }
            finally
            {
                ThreadWillingExit.Disarm(Owner);
            }
        }

        [TestMethod]
        public void A_same_instance_teleport_leaves_the_marker_and_a_cross_instance_one_always_takes_it()
        {
            var run = SoloRun();
            run.MarkPopulated(0, 0, 0);

            try
            {
                ThreadWillingExit.ArmIfSoloOwner(run, Owner, RunId, Now);
                Assert.IsNull(ThreadWillingExit.TakeForTeleport(Owner, RunId, RunId, Now, _ => run));
                Assert.IsTrue(ThreadWillingExit.IsArmed(Owner), "a hop inside the copy is not an exit");

                // Stale, cross-instance: taken, nothing retired.
                Assert.IsNull(ThreadWillingExit.TakeForTeleport(Owner, RunId, 0, Now + ThreadWillingExit.MaxArmAge + TimeSpan.FromSeconds(1), _ => run));
                Assert.IsFalse(ThreadWillingExit.IsArmed(Owner), "removed even though it did not retire");
            }
            finally
            {
                ThreadWillingExit.Disarm(Owner);
            }
        }

        [TestMethod]
        public void Group_and_non_owner_exits_are_never_armed()
        {
            var group = GroupRun();
            group.MarkPopulated(0, 0, 0);

            try
            {
                ThreadWillingExit.ArmIfSoloOwner(group, Owner, RunId, Now);
                ThreadWillingExit.ArmIfSoloOwner(SoloRun(), Stranger, RunId, Now);
                ThreadWillingExit.ArmIfSoloOwner(null, Owner, RunId, Now);

                Assert.IsFalse(ThreadWillingExit.IsArmed(Owner));
                Assert.IsFalse(ThreadWillingExit.IsArmed(Stranger));
            }
            finally
            {
                ThreadWillingExit.Disarm(Owner);
                ThreadWillingExit.Disarm(Stranger);
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Re-entry: a cleared solo run is free
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_cleared_solo_owner_at_zero_entries_re_enters_for_free()
        {
            var clearedSolo = DungeonGemRules.IsClearedSoloReturn(isGroup: false, ThreadDungeonRunState.Cleared);
            Assert.IsTrue(clearedSolo);

            Assert.AreEqual(GemUseDecision.ReEnter, DungeonGemRules.Decide(BoundSpec(), Owner, 0, true, true, 150, 150, out var reason, freeEntry: clearedSolo));
            Assert.IsNull(reason, "not refused with NoEntriesReason");

            var charge = DungeonGemRules.DecideEntryCharge(false, 0, clearedSolo);
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, charge, "nothing spent, even at zero");
            Assert.AreEqual(DungeonGemRules.EntryCharge.Free, DungeonGemRules.DecideEntryCharge(false, 2, clearedSolo), "free with entries left too");

            Assert.AreEqual("You step back into Filos Doom. It is cleared, so returning costs no entry.",
                DungeonGemRules.EntryMessage(charge, "Thread Gem", "Filos Doom", 0, clearedSolo));
            Assert.AreEqual("You step back into Filos Doom. It is cleared, so returning costs no entry.", DungeonGemRules.ClearedReturnMessage("Filos Doom"));

            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(BoundSpec(), Stranger, 0, true, true, 150, 150, out var strangerReason, freeEntry: clearedSolo),
                "the owner check still runs first");
            Assert.AreEqual("This gem is bound to another adventurer's dungeon.", strangerReason);
        }

        [TestMethod]
        public void An_active_solo_run_and_every_group_run_keep_todays_entry_rules()
        {
            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Ended })
                Assert.IsFalse(DungeonGemRules.IsClearedSoloReturn(false, state), $"solo {state}");

            foreach (var state in new[] { ThreadDungeonRunState.Starting, ThreadDungeonRunState.Active, ThreadDungeonRunState.Cleared, ThreadDungeonRunState.Ended })
                Assert.IsFalse(DungeonGemRules.IsClearedSoloReturn(true, state), $"group {state}");

            var active = DungeonGemRules.IsClearedSoloReturn(false, ThreadDungeonRunState.Active);
            Assert.AreEqual(GemUseDecision.Refuse, DungeonGemRules.Decide(BoundSpec(), Owner, 0, true, true, 150, 150, out var reason, freeEntry: active));
            Assert.AreEqual(DungeonGemRules.NoEntriesReason, reason, "an Active solo owner at zero is still refused");
            Assert.AreEqual(DungeonGemRules.EntryCharge.Spend, DungeonGemRules.DecideEntryCharge(false, 2, active));
            Assert.AreEqual(DungeonGemRules.EntryCharge.NoEntries, DungeonGemRules.DecideEntryCharge(false, 0, active));
            Assert.AreEqual("You step back into Filos Doom. Entries left: 1.", DungeonGemRules.EntryMessage(DungeonGemRules.EntryCharge.Spend, "Thread Gem", "Filos Doom", 1, active));

            // A key's free first entry keeps the first-entry line, exactly as before.
            Assert.AreEqual(DungeonGemRules.FirstEntryMessage("Thread Key", "Filos Doom", 0),
                DungeonGemRules.EntryMessage(DungeonGemRules.EntryCharge.Free, "Thread Key", "Filos Doom", 0, clearedSoloReturn: false));

            // The legacy overloads are the no-return forms.
            Assert.AreEqual(DungeonGemRules.DecideEntryCharge(false, 0), DungeonGemRules.DecideEntryCharge(false, 0, false));
            Assert.AreEqual(DungeonGemRules.DecideEntryCharge(true, 0), DungeonGemRules.DecideEntryCharge(true, 0, false));
        }

        [TestMethod]
        public void The_grace_warning_keeps_the_gem_sentence_for_a_cleared_solo_gem_at_zero()
        {
            Assert.IsFalse(ThreadDungeonRun.GemCanReEnter(true, 0), "the old answer: no entries, no sentence");
            Assert.IsTrue(ThreadDungeonRun.GemCanReEnter(true, 0, clearedSoloReturn: true), "a cleared solo return is free");
            Assert.IsFalse(ThreadDungeonRun.GemCanReEnter(true, 0, clearedSoloReturn: false));
            Assert.IsTrue(ThreadDungeonRun.GemCanReEnter(false, 0, clearedSoloReturn: false), "a gem not in the pack keeps the sentence, as before");

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "private static void WarnIfEmptyGraceRunningOut(ThreadDungeonRun run, DateTime lastActiveUtc, TimeSpan window, DateTime now)");
            // Per recipient since the cleared group return (ThreadGroupClearedReturnTests): solo is unchanged inside IsClearedReturnFor.
            StringAssert.Contains(body, "var clearedReturn = DungeonGemRules.IsClearedReturnFor(run, guid);");
            StringAssert.Contains(body, "ThreadDungeonRun.GemCanReEnter(gem != null, gem?.Structure ?? 0, clearedReturn)");
        }

        [TestMethod]
        public void The_gem_handler_reads_the_cleared_solo_return_once_and_uses_it_for_decide_charge_and_line()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(HandlerPath), "public static bool TryHandleUse(Gem gem, Player player)");

            // Extended to a cleared GROUP return (ThreadGroupClearedReturnTests): the same one read, now through IsClearedReturnFor.
            var read = body.IndexOf("var clearedReturn = DungeonGemRules.IsClearedReturnFor(run, player.Guid.Full);", StringComparison.Ordinal);
            var decide = body.IndexOf("freeEntry: (isKey && !run.MemberEntered(player.Guid.Full)) || clearedReturn || unravelCredit);", StringComparison.Ordinal);
            var closed = body.IndexOf("Say(player, \"That dungeon has already closed.\");", StringComparison.Ordinal);
            var charge = body.IndexOf("DungeonGemRules.DecideEntryCharge(keyFree || unravelFree, gem.Structure ?? 0, clearedReturn);", StringComparison.Ordinal);
            var line = body.IndexOf("DungeonGemRules.EntryMessage(charge, gem.Name, run.Dungeon.Name, gem.Structure ?? 0, clearedReturn)", StringComparison.Ordinal);

            Assert.IsTrue(read >= 0 && read < decide && decide < closed && closed < charge && charge < line);
            Assert.AreEqual(1, CountOf(body, "IsClearedReturnFor("), "read once");
            Assert.AreEqual(0, CountOf(body, "IsClearedSoloReturn("), "no second, solo-only read");
        }

        // ---------------------------------------------------------------------------------------------
        // Source pins for the wiring that needs a live Player
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_clear_no_longer_destroys_the_solo_gem_and_tells_the_owner_how_it_ends()
        {
            Assert.AreEqual("Filos Doom is cleared. Your gem will bring you back if you fall; it crumbles when you leave by recall or the exit portal.",
                ThreadDungeonManager.SoloClearedMessage("Filos Doom"));
            Assert.AreEqual("It crumbles when you leave the cleared dungeon by recall or its exit portal, or when the dungeon closes.",
                ThreadDungeonGemHandler.SoloBoundLineTail);

            foreach (var text in new[] { ThreadDungeonManager.SoloClearedMessage("Filos Doom"), ThreadDungeonGemHandler.SoloBoundLineTail, DungeonGemRules.ClearedReturnMessage("Filos Doom") })
                foreach (var c in text)
                    Assert.IsTrue(c < 128, $"ASCII only: {text}");
        }

        [TestMethod]
        public void Teleport_takes_the_marker_after_the_fog_requeue_and_before_the_teleport_starts()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(PlayerLocationPath), "public void Teleport(Position _newPosition, bool fromPortal = false, string source = null)");

            var fog = body.IndexOf("delayTelport.AddAction(this, () => WorldManager.ThreadSafeTeleport(this, _newPosition, source: teleportSource));", StringComparison.Ordinal);
            var fogReturn = body.IndexOf("return;", fog < 0 ? 0 : fog, StringComparison.Ordinal);
            var guard = body.IndexOf("if (Location != null && Location.Instance != newPosition.Instance)", StringComparison.Ordinal);
            var hook = body.IndexOf("ACE.Server.ThreadDungeons.ThreadWillingExit.OnTeleport(this, Location.Instance, newPosition.Instance);", StringComparison.Ordinal);
            var teleporting = body.IndexOf("Teleporting = true;", StringComparison.Ordinal);

            Assert.IsTrue(fog >= 0 && fog < fogReturn && fogReturn < guard && guard < hook && hook < teleporting,
                "after the fog branch's early return (which re-enters Teleport), before Teleporting = true");
            Assert.AreEqual(1, CountOf(body, "ThreadWillingExit."), "one hook");
        }

        [TestMethod]
        public void Die_clears_the_marker_beside_the_thread_death_latch()
        {
            var source = PooledLootSourceText.Read(PlayerDeathPath);

            var latch = source.IndexOf("dynamicDungeonDeathInProgress = ACE.Server.ThreadDungeons.ThreadDungeonManager.GetRun(Location.Instance) != null;", StringComparison.Ordinal);
            var disarm = source.IndexOf("ACE.Server.ThreadDungeons.ThreadWillingExit.Disarm(Guid.Full);", StringComparison.Ordinal);
            var pvp = source.IndexOf("pvpMatchDeathInProgress = LatchPvpMatchDeath(", StringComparison.Ordinal);

            Assert.IsTrue(latch >= 0 && latch < disarm && disarm < pvp, "inside Die, next to the Threads latch");
        }

        [TestMethod]
        public void The_exit_guard_arms_only_on_its_not_held_path()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(GuardPath), "public static bool TryHoldExit(Player player, Action reissue)");

            var notHeld = body.IndexOf("hasUnclaimed, anyCacheHoldsItems, bypass))", StringComparison.Ordinal);
            var arm = body.IndexOf("ThreadWillingExit.ArmIfSoloOwner(run, playerGuid, player.Location.Instance, now);", StringComparison.Ordinal);
            var returnFalse = body.IndexOf("return false;", arm < 0 ? 0 : arm, StringComparison.Ordinal);
            var prompt = body.IndexOf("new Confirmation_ThreadExit(", StringComparison.Ordinal);

            Assert.IsTrue(notHeld >= 0 && notHeld < arm && arm < returnFalse && returnFalse < prompt, "armed inside the not-held branch, before its return");
            Assert.AreEqual(1, CountOf(body, "ThreadWillingExit."), "never on the held (prompt) path");
        }

        [TestMethod]
        public void The_retire_consumes_inline_on_the_owner_rather_than_through_the_landblock_queue()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "internal static void ConsumeRetiredOwnerGem(Player owner, ThreadDungeonRun run)");

            var retire = body.IndexOf("run.RetireOwnerGem();", StringComparison.Ordinal);
            var consume = body.IndexOf("ConsumeGemNow(owner, run.GemGuid, run.ToString());", StringComparison.Ordinal);

            Assert.IsTrue(retire >= 0 && retire < consume, "the latch first (authoritative), then the cleanup");
            Assert.IsFalse(body.Contains("EnqueueGemConsume"), "never queued onto the copy being left");
        }

        // ---------------------------------------------------------------------------------------------
        // Review F1: the login line for a relog into a held cleared solo copy
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void The_login_line_answers_for_live_runs_and_a_cleared_solo_run_whose_gem_lives()
        {
            Assert.IsTrue(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Starting, false, true), "Starting");
            Assert.IsTrue(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Active, false, true), "Active");
            Assert.IsTrue(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Active, true, true), "Active group, as before");
            Assert.IsTrue(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Cleared, false, true), "Cleared solo, live gem: the old code answered null here");
            Assert.IsFalse(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Cleared, false, false), "Cleared solo, retired gem");
            Assert.IsFalse(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Cleared, true, true), "Cleared group stays null");
            Assert.IsFalse(ThreadDungeonManager.StillOpenOnLogin(ThreadDungeonRunState.Ended, false, true), "Ended");

            // The line a cleared solo owner reads keeps the gem sentence even at zero entries.
            Assert.AreEqual("Your Thread in Filos Doom is still open. Use your Thread Gem to step back in before it collapses.",
                ThreadDungeonRun.BuildStillOpenLoginMessage("Filos Doom", ThreadDungeonRun.GemCanReEnter(true, 0, clearedSoloReturn: true)));

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath), "public static string GetStillOpenLoginMessage(Player player, uint savedInstance)");
            var gate = body.IndexOf("if (!StillOpenOnLogin(state, run.IsGroup, run.IsRunGem(run.GemGuid)))", StringComparison.Ordinal);
            var landblock = body.IndexOf("LandblockManager.GetEphemeralLandblock(run.Instance) == null", StringComparison.Ordinal);
            var line = body.IndexOf("ThreadDungeonRun.GemCanReEnter(gem != null, gem?.Structure ?? 0, clearedSoloReturn)", StringComparison.Ordinal);
            Assert.IsTrue(gate >= 0 && gate < landblock && landblock < line, "state gate, live copy, then the line with the free return");
        }

        // ---------------------------------------------------------------------------------------------
        // Review F2: a guide gem bound to a cleared run no longer holds the next grant
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_guide_gem_bound_to_a_cleared_run_is_not_outstanding_and_one_bound_to_a_live_run_is()
        {
            var gem = ThreadGuideFlow.BuildFragmentSpec(new GuideTag(50, 3, Owner)).WithSeed(7).WithBinding(RunId, Owner);
            ThreadDungeonRunState? Run(ThreadDungeonRunState s) => s;

            Assert.IsFalse(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 3, _ => Run(ThreadDungeonRunState.Cleared)), "Cleared: the rung is won");
            Assert.IsTrue(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 3, _ => Run(ThreadDungeonRunState.Active)), "Active: still in progress");
            Assert.IsTrue(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 3, _ => Run(ThreadDungeonRunState.Starting)));
            Assert.IsTrue(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 3, _ => null), "no run (gone): the old rule, the stale/sweep paths own it");
            Assert.IsTrue(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 3, null), "no resolver: the three-argument form, as before");
            Assert.IsFalse(ThreadGuideFlow.CountsAsOutstanding(gem, Owner, 4, _ => Run(ThreadDungeonRunState.Active)), "a stale serial never counts");

            var fragment = ThreadGuideFlow.BuildFragmentSpec(new GuideTag(50, 3, Owner));
            Assert.IsTrue(ThreadGuideFlow.CountsAsOutstanding(fragment, Owner, 3, _ => Run(ThreadDungeonRunState.Cleared)), "an unbound fragment is unaffected");

            var station = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadGuideStation.cs");
            StringAssert.Contains(station, "runId => ThreadDungeonManager.GetRun(runId)?.State");
        }

        // ---------------------------------------------------------------------------------------------
        // Review F3: a refused start leaves a held cleared solo run alone
        // ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void A_refused_start_does_not_sweep_and_a_passing_one_does()
        {
            const uint account = 7;
            var clearedSolo = (Owner, account, ThreadDungeonRunState.Cleared, false);
            var otherCharOnAccount = (Stranger, account, ThreadDungeonRunState.Active, false);

            // Per-account cap 2: the cleared run is left out (it would be swept), the other character's run counts,
            // so the start passes - and only then does TryStart sweep.
            Assert.IsTrue(ThreadDungeonManager.PassesStartGatesAfterSweep(new[] { clearedSolo, otherCharOnAccount }, Owner, account, false, 0, 2, out _));

            // Per-account cap 1: refused even with the cleared run gone, so nothing is swept.
            Assert.IsFalse(ThreadDungeonManager.PassesStartGatesAfterSweep(new[] { clearedSolo, otherCharOnAccount }, Owner, account, false, 0, 1, out var accountReason));
            Assert.AreEqual(ThreadDungeonManager.AccountRunOpenReason, accountReason);

            // On a live roster: refused, nothing swept.
            Assert.IsFalse(ThreadDungeonManager.PassesStartGatesAfterSweep(new[] { clearedSolo }, Owner, account, true, 0, 0, out var rosterReason));
            Assert.AreEqual(ThreadDungeonManager.OnLiveRosterReason, rosterReason);

            // Only the cleared solo run: it would be swept, so it does not block (the pre-ruling behaviour).
            Assert.IsTrue(ThreadDungeonManager.PassesStartGatesAfterSweep(new[] { clearedSolo }, Owner, account, false, 0, 1, out _));

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ManagerPath),
                "public static bool TryStart(Player owner, WorldObject gem, DungeonGemSpec spec, DungeonEntryDef dungeon,\n            IReadOnlyList<RosterSeat> seats, out ThreadDungeonRun run, out string reason,\n            out IReadOnlyList<string> droppedNames, string startGroup = null)");
            var pre = body.IndexOf("if (!PassesStartGatesAfterSweep(preGate, owner.Guid.Full, owner.Account.AccountId, IsOnLiveRoster(owner.Guid.Full), maxRuns, perAccount, out reason))", StringComparison.Ordinal);
            var preReturn = body.IndexOf("return false;", pre < 0 ? 0 : pre, StringComparison.Ordinal);
            var sweep = body.IndexOf("EndClearedRunsForOwner(owner.Guid.Full);", StringComparison.Ordinal);
            var gates = body.IndexOf("if (!PassesStartGates(gateCounts,", StringComparison.Ordinal);
            Assert.IsTrue(pre >= 0 && pre < preReturn && preReturn < sweep && sweep < gates, "pre-check refuses before the sweep; the authoritative gates still follow");
            Assert.AreEqual(1, CountOf(body, "EndClearedRunsForOwner("), "one sweep call site");
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
