using System;
using System.Collections.Generic;

using ACE.Server.Realms;
using ACE.Server.ThreadDungeons;
using ACE.Server.ThreadDungeons.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Group Threads admission and gem liveness (rulings R6, R7, R10, invariant 6): roster admission by guid, the
    /// late-answer rule of the group-start dialog, and the gem-to-run resolution the use path and the sweeper share,
    /// including an owner who holds a Cleared group run alongside a newer live one. Pure plus source-text pins for
    /// the wiring a Player is needed to exercise. No PropertyManager key is read.
    /// </summary>
    [TestClass]
    public class GroupAdmissionTests
    {
        private const uint Owner = 0x50000010u;
        private const uint Member = 0x50000020u;
        private const uint Stranger = 0x50000030u;

        private static GroupScaling GroupOf(int n)
            => GroupScaling.Compute(n, GroupScaling.DefaultEffortPerMember, GroupScaling.DefaultCountShare, GroupScaling.DefaultGroupMaxMonsters,
                GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap, GroupScaling.DefaultRewardBonusPerMember,
                GroupScaling.DefaultRewardBonusCap, 2.7);

        private static DungeonGemSpec Spec() => new DungeonGemSpec("filos_doom", 200, 6, "any", 1, new (string, double)[0], 0, 0);

        private static ThreadDungeonRun GroupRun(uint instance, uint gemGuid, params uint[] members)
        {
            var seats = new List<RosterSeat> { new RosterSeat(Owner, "Owner", 7, 200) };
            foreach (var m in members)
                seats.Add(new RosterSeat(m, $"M{m:X}", 8, 200));

            var dungeon = new DungeonEntryDef { Id = "filos_doom", Landblock = 0x0150, ExitPortalWcid = 1003601 };
            return new ThreadDungeonRun(instance, seats, gemGuid, Spec(), dungeon, DateTime.UtcNow, TimeSpan.FromMinutes(180), GroupOf(seats.Count));
        }

        [TestMethod]
        public void Roster_guids_are_admitted_and_nobody_else()
        {
            var run = GroupRun(0x80001234u, 0x80000099u, Member);

            Assert.IsTrue(EphemeralRealm.AcceptsRosterGuid(run, Owner));
            Assert.IsTrue(EphemeralRealm.AcceptsRosterGuid(run, Member));
            Assert.IsFalse(EphemeralRealm.AcceptsRosterGuid(run, Stranger));
            Assert.IsFalse(EphemeralRealm.AcceptsRosterGuid(null, Owner));
        }

        [TestMethod]
        public void Accepts_admits_roster_guids_after_the_owner_reference_check()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/Realms/EphemeralRealm.cs");
            var body = PooledLootSourceText.MethodBody(source, "public bool Accepts(Player player)");

            var ownerCheck = body.IndexOf("if (player == Owner)", StringComparison.Ordinal);
            var rosterCheck = body.IndexOf("if (Run != null && player != null && AcceptsRosterGuid(Run, player.Guid.Full))", StringComparison.Ordinal);
            var fellowship = body.IndexOf("OpenToFellowship &&", StringComparison.Ordinal);

            Assert.IsTrue(ownerCheck >= 0, "owner reference check kept");
            Assert.IsTrue(rosterCheck > ownerCheck, "roster admission follows the owner reference check");
            Assert.IsTrue(fellowship > rosterCheck, "the fellowship rule for other ephemeral realms is kept");
        }

        [TestMethod]
        public void A_late_or_timed_out_answer_starts_nothing()
        {
            var sent = new DateTime(2026, 9, 17, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(Confirmation_ThreadGroupStart.IsLate(sent, sent, false));
            Assert.IsFalse(Confirmation_ThreadGroupStart.IsLate(sent, sent.AddSeconds(29.0), false));
            Assert.IsFalse(Confirmation_ThreadGroupStart.IsLate(sent, sent.AddSeconds(29.49), false));
            Assert.IsTrue(Confirmation_ThreadGroupStart.IsLate(sent, sent.AddSeconds(29.5), false));
            Assert.IsTrue(Confirmation_ThreadGroupStart.IsLate(sent, sent.AddSeconds(31), false));
            Assert.IsTrue(Confirmation_ThreadGroupStart.IsLate(sent, sent, true), "a timeout is late whenever it arrives");
        }

        [TestMethod]
        public void The_confirmation_checks_lateness_before_hopping_onto_the_player_queue()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/Confirmation_ThreadGroupStart.cs");
            var body = PooledLootSourceText.MethodBody(source, "public override void ProcessConfirmation(bool response, bool timeout = false)");

            var late = body.IndexOf("if (IsLate(SentUtc, DateTime.UtcNow, timeout))", StringComparison.Ordinal);
            var hop = body.IndexOf("player.EnqueueAction(new ActionEventDelegate(() => ThreadDungeonGemHandler.OnGroupStartAnswered(player, gemGuid, response, offered, solo)));", StringComparison.Ordinal);

            Assert.IsTrue(late >= 0, "late check present");
            Assert.IsTrue(hop > late, "the answer is queued only after the late check, carrying the offer's exclusions");
        }

        [TestMethod]
        public void The_confirmation_carries_the_offers_exclusions_and_never_null()
        {
            var offered = new[] { ("Ann", RosterExclusionReason.PkTimer) };

            var withList = new Confirmation_ThreadGroupStart(new ACE.Entity.ObjectGuid(Owner), 0x80000099u, DateTime.UtcNow, offered);
            Assert.AreEqual(1, withList.OfferedExclusions.Count);
            Assert.AreEqual(("Ann", RosterExclusionReason.PkTimer), withList.OfferedExclusions[0]);

            var legacy = new Confirmation_ThreadGroupStart(new ACE.Entity.ObjectGuid(Owner), 0x80000099u, DateTime.UtcNow);
            Assert.IsNotNull(legacy.OfferedExclusions);
            Assert.AreEqual(0, legacy.OfferedExclusions.Count);

            var soloFallback = new Confirmation_ThreadGroupStart(new ACE.Entity.ObjectGuid(Owner), 0x80000099u, DateTime.UtcNow, offered, true);
            Assert.IsTrue(soloFallback.SoloOffer, "the solo fallback is flagged so the answer can tell the two offers apart");
            Assert.IsFalse(withList.SoloOffer, "the group offer is not");
            Assert.IsFalse(legacy.SoloOffer);

            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");
            StringAssert.Contains(PooledLootSourceText.MethodBody(handler, "private static bool OfferGroupStart("),
                "new Confirmation_ThreadGroupStart(player.Guid, gem.Guid.Full, DateTime.UtcNow, form.Exclusions)");
            StringAssert.Contains(AnswerBody(), "if (ReportFormation(player, form, minPlayerLevel, offeredExclusions))");
            StringAssert.Contains(PooledLootSourceText.MethodBody(handler, "private static bool ReportFormation("),
                "GroupRosterRules.NewExclusions(alreadyReported, form.Exclusions)");
        }

        [TestMethod]
        public void A_gem_gone_at_answer_time_is_reported_and_starts_nothing()
        {
            Assert.AreEqual("Your Thread gem is no longer in your pack.", ThreadDungeonGemHandler.GemGoneAtAnswerMessage);

            var body = AnswerBody();

            StringAssert.Contains(body,
                "if (!(player.FindObject(gemGuid, Player.SearchLocations.MyInventory) is Gem gem))\n            {\n                Say(player, GemGoneAtAnswerMessage);\n                return;\n            }");

            var gone = body.IndexOf("Say(player, GemGoneAtAnswerMessage);", StringComparison.Ordinal);
            Assert.IsTrue(gone >= 0 && gone < body.IndexOf("StartSoloRun(", StringComparison.Ordinal), "reported before any start");
        }

        private static string AnswerBody()
            => PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "System.Collections.Generic.IReadOnlyList<(string Name, RosterExclusionReason Reason)> offeredExclusions, bool soloOffer)");

        [TestMethod]
        public void A_busy_owner_is_refused_at_answer_time_before_any_start()
        {
            var body = AnswerBody();

            var gemFound = body.IndexOf("Say(player, GemGoneAtAnswerMessage);", StringComparison.Ordinal);
            var busy = body.IndexOf("if (player.IsBusy || player.Teleporting || player.suicideInProgress)\n            {\n                player.SendWeenieError(WeenieError.YoureTooBusy);\n                return;\n            }", StringComparison.Ordinal);
            var solo = body.IndexOf("StartSoloRun(", StringComparison.Ordinal);
            var group = body.IndexOf("StartGroupRun(", StringComparison.Ordinal);

            Assert.IsTrue(gemFound >= 0, "gem-found check present");
            Assert.IsTrue(busy > gemFound, "busy check follows the gem-found check");
            Assert.IsTrue(solo > busy, "busy check precedes the solo start");
            Assert.IsTrue(group > busy, "busy check precedes the group start");
        }

        [TestMethod]
        public void Answer_time_failures_without_their_own_message_say_the_thread_did_not_open_except_a_bound_gem()
        {
            Assert.AreEqual("The Thread did not open.", ThreadDungeonGemHandler.ThreadDidNotOpenMessage);

            var body = AnswerBody();

            StringAssert.Contains(body, "if (player.IsDead)\n            {\n                Say(player, ThreadDidNotOpenMessage);\n                return;\n            }");
            StringAssert.Contains(body, "if (string.IsNullOrEmpty(text) || !DungeonGemSpec.TryParse(text, out var spec, out _) || spec.Seed == 0)\n            {\n                Say(player, ThreadDidNotOpenMessage);\n                return;\n            }");
            StringAssert.Contains(body, "if (spec.IsBound)\n                return;", "an already-bound gem stays silent");
            StringAssert.Contains(body, "Say(player, decision == GemUseDecision.Refuse ? reason : ThreadDidNotOpenMessage);");

            var dead = body.IndexOf("if (player.IsDead)", StringComparison.Ordinal);
            Assert.IsTrue(dead >= 0 && dead < body.IndexOf("StartSoloRun(", StringComparison.Ordinal));
        }

        [TestMethod]
        public void An_owner_on_a_live_roster_is_told_before_any_offer()
        {
            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");
            var offer = PooledLootSourceText.MethodBody(handler, "private static bool OfferGroupStart(");

            var roster = offer.IndexOf("if (ThreadDungeonManager.IsOnLiveRoster(player.Guid.Full))\n            {\n                Say(player, ThreadDungeonManager.OnLiveRosterReason);\n                return true;\n            }", StringComparison.Ordinal);
            Assert.IsTrue(roster >= 0, "live-roster refusal present");
            Assert.IsTrue(roster < offer.IndexOf("FormRoster(", StringComparison.Ordinal), "checked before the roster is formed");
            Assert.IsTrue(roster < offer.IndexOf("EnqueueSend(", StringComparison.Ordinal), "checked before the dialog is sent");
        }

        [TestMethod]
        public void A_non_fellowed_start_never_reads_the_group_tunables()
        {
            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");

            StringAssert.Contains(PooledLootSourceText.MethodBody(handler, "private static bool StartRun("),
                "if (player.Fellowship != null && ThreadDungeonManager.GroupModeEnabled)");
            StringAssert.Contains(AnswerBody(), "if (player.Fellowship == null || !ThreadDungeonManager.GroupModeEnabled)");
            Assert.IsFalse(handler.Contains("ThreadDungeonManager.GroupModeEnabled && player.Fellowship"), "old operand order is gone");
        }

        /// <summary>
        /// Explicit consent (owner ruling, 2026-09-17). No, on either offer, starts nothing: the !yes branch says the
        /// Thread stays closed and returns, and there is no StartSoloRun in it. A Yes on the GROUP offer that re-forms
        /// to something that is no longer a group also starts nothing, so the only StartSoloRun left in the answer is
        /// the one behind the soloOffer flag.
        ///
        /// A source pin because the whole branch needs a live Player, a live gem and a ConfirmationManager; the lines
        /// it sends are asserted for real in GroupRosterRulesTests.
        /// </summary>
        [TestMethod]
        public void A_refused_offer_starts_nothing_and_only_a_confirmed_solo_offer_opens_solo()
        {
            var body = AnswerBody();

            StringAssert.Contains(body,
                "if (!yes)\n            {\n                Say(player, GroupRosterRules.ThreadStaysClosedText);\n                return;\n            }");

            StringAssert.Contains(body,
                "if (soloOffer)\n            {\n                StartSoloRun(gem, player, spec, dungeon);\n                return;\n            }");

            StringAssert.Contains(body,
                "if (!form.IsGroup)\n            {\n                Say(player, GroupRosterRules.FellowshipCannotJoinText);\n                return;\n            }");

            Assert.AreEqual(1, body.Split(new[] { "StartSoloRun(" }, StringSplitOptions.None).Length - 1,
                "exactly one solo start in the answer, behind the soloOffer flag");

            var no = body.IndexOf("if (!yes)", StringComparison.Ordinal);
            var solo = body.IndexOf("StartSoloRun(", StringComparison.Ordinal);
            Assert.IsTrue(no >= 0 && no < solo, "No is answered before any start is reached");

            // The offer side: a roster that is not a group asks a second question instead of opening solo.
            var offer = PooledLootSourceText.MethodBody(
                PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs"),
                "private static bool OfferGroupStart(");

            StringAssert.Contains(offer, "GroupRosterRules.SoloOfferText(form.Exclusions, minPlayerLevel, form.LockoutRemaining)");
            Assert.IsFalse(offer.Contains("StartSoloRun("), "the offer never opens a solo run itself");
        }

        [TestMethod]
        public void Group_gems_and_keys_compose_the_group_bound_line_and_a_keys_free_entry_uses_the_first_entry_line()
        {
            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");

            StringAssert.Contains(PooledLootSourceText.MethodBody(handler, "private static void BindOwnerGemAndEnter("),
                "ComposeLongDesc(bound, dungeon.Name, gem.Structure ?? 0, gem.MaxStructure ?? 0, run.IsGroup)");
            StringAssert.Contains(PooledLootSourceText.MethodBody(handler, "private static void BindOwnerGemAndEnter("),
                "DungeonGemRules.FirstEntryMessage(gem.Name, dungeon.Name, gem.Structure ?? 0)");

            var use = PooledLootSourceText.MethodBody(handler, "public static bool TryHandleUse(Gem gem, Player player)");
            StringAssert.Contains(use, "ComposeLongDesc(spec, run.Dungeon.Name, gem.Structure ?? 0, gem.MaxStructure ?? 0, run.IsGroup)");
            StringAssert.Contains(use, "DungeonGemRules.EntryMessage(charge, gem.Name, run.Dungeon.Name, gem.Structure ?? 0)");

            var grant = PooledLootSourceText.MethodBody(handler, "private static void GrantKey(");
            var attuned = grant.IndexOf("key.Attuned = AttunedStatus.Attuned;", StringComparison.Ordinal);
            var desc = grant.IndexOf("key.SetProperty(PropertyString.LongDesc, ComposeLongDesc(keySpec, dungeonName, key.Structure ?? 0, key.MaxStructure ?? 0, groupRun: true));", StringComparison.Ordinal);
            var place = grant.IndexOf("target.TryCreateInInventoryWithNetworking(key)", StringComparison.Ordinal);
            Assert.IsTrue(desc > attuned && desc < place, "the key's description is rewritten with the group line before it is placed");
        }

        [TestMethod]
        public void A_members_key_is_renamed_Thread_Key_right_after_Attuned_is_set()
        {
            var handler = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");

            var grant = PooledLootSourceText.MethodBody(handler, "private static void GrantKey(");
            var attuned = grant.IndexOf("key.Attuned = AttunedStatus.Attuned;", StringComparison.Ordinal);
            var name = grant.IndexOf("key.SetProperty(PropertyString.Name, GroupRosterRules.KeyItemName);", StringComparison.Ordinal);
            var plural = grant.IndexOf("key.SetProperty(PropertyString.PluralName, GroupRosterRules.KeyItemPluralName);", StringComparison.Ordinal);
            var desc = grant.IndexOf("key.SetProperty(PropertyString.LongDesc, ComposeLongDesc(keySpec, dungeonName, key.Structure ?? 0, key.MaxStructure ?? 0, groupRun: true));", StringComparison.Ordinal);

            Assert.IsTrue(attuned >= 0 && name > attuned && plural > name && desc > plural,
                "Name and PluralName are set, in that order, right after Attuned and before the description rewrite");

            StringAssert.Contains(GroupRosterRules.KeyItemName, "Thread Key");
            Assert.AreEqual("Thread Key", GroupRosterRules.KeyItemName);
            Assert.AreEqual("Thread Keys", GroupRosterRules.KeyItemPluralName);
        }

        [TestMethod]
        public void The_sweeper_tests_liveness_with_IsRunGem_resolved_by_the_gems_run_id()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSweeper.cs");

            StringAssert.Contains(PooledLootSourceText.MethodBody(source, "public static bool IsDeadBoundGem(WorldObject item)"),
                "return IsDeadBoundGemCore(spec, item.Guid.Full, ThreadDungeonManager.GetRun);");
            StringAssert.Contains(PooledLootSourceText.MethodBody(source, "internal static bool IsDeadBoundGemCore("),
                "return run == null || !run.IsRunGem(itemGuid);");
            Assert.IsFalse(source.Contains("GemGuid != item.Guid.Full"), "the owner-gem-only liveness test is gone");
        }

        [TestMethod]
        public void The_gem_use_resolves_liveness_by_the_gems_run_id_and_IsRunGem()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonGemHandler.cs");
            var body = PooledLootSourceText.MethodBody(source, "public static bool TryHandleUse(Gem gem, Player player)");

            StringAssert.Contains(body, "var run = DungeonGemRules.ResolveGemRun(spec, ThreadDungeonManager.GetRun);");
            StringAssert.Contains(body, "var runIsLive = DungeonGemRules.IsRunLiveForGem(run, gem.Guid.Full);");
            Assert.IsFalse(body.Contains("run.GemGuid == gem.Guid.Full"), "the owner-gem-only liveness test is gone");
            Assert.IsFalse(body.Contains("GetRunForOwner"), "a gem never resolves its run by owner guid");
        }

        [TestMethod]
        public void Sweeper_core_keeps_owner_gems_and_keys_and_drops_everything_else()
        {
            var run = GroupRun(0x80001234u, 0x80000099u, Member);
            run.SetMemberKey(Member, 0x800000AAu);

            var runs = new Dictionary<uint, ThreadDungeonRun> { { run.Instance, run } };
            Func<uint, ThreadDungeonRun> getRun = id => runs.TryGetValue(id, out var r) ? r : null;

            var ownerSpec = Spec().WithBinding(run.Instance, Owner);
            var keySpec = Spec().WithBinding(run.Instance, Member);

            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(ownerSpec, 0x80000099u, getRun), "owner gem is live");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(keySpec, 0x800000AAu, getRun), "member key is live");
            Assert.IsTrue(ThreadDungeonSweeper.IsDeadBoundGemCore(keySpec, 0x800000BBu, getRun), "an unrecorded gem is dead");
            Assert.IsTrue(ThreadDungeonSweeper.IsDeadBoundGemCore(Spec().WithBinding(0x80009999u, Owner), 0x80000099u, getRun), "a gem whose run is gone is dead");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(Spec(), 0x80000099u, getRun), "an unbound gem is never dead");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(null, 0x80000099u, getRun));

            run.SetMemberKey(Member, 0);
            Assert.IsTrue(ThreadDungeonSweeper.IsDeadBoundGemCore(keySpec, 0x800000AAu, getRun), "a cleared key record makes the key dead");
        }

        /// <summary>
        /// An owner holding a Cleared group run (held for its members' loot) and a newer live run: every gem and key
        /// resolves to the run named by its OWN spec. Resolving by owner guid would pick the live run (the manager's
        /// owner lookup prefers it) and declare the old run's gem and key dead.
        /// </summary>
        [TestMethod]
        public void An_owner_holding_two_runs_resolves_each_gem_to_its_own_run()
        {
            const uint OldGem = 0x80000101u;
            const uint OldKey = 0x80000102u;
            const uint NewGem = 0x80000201u;
            const uint NewKey = 0x80000202u;

            var held = GroupRun(0x80001111u, OldGem, Member);
            held.SetMemberKey(Member, OldKey);
            held.MarkPopulated(0, 0, 0);
            Assert.AreEqual(ThreadDungeonRunState.Cleared, held.State, "precondition: the old run is a held Cleared group run");

            var live = GroupRun(0x80002222u, NewGem, Member);
            live.SetMemberKey(Member, NewKey);
            live.MarkPopulated(10, 10, 0);
            Assert.AreEqual(ThreadDungeonRunState.Active, live.State, "precondition: the new run is live");

            var runs = new Dictionary<uint, ThreadDungeonRun> { { held.Instance, held }, { live.Instance, live } };
            Func<uint, ThreadDungeonRun> getRun = id => runs.TryGetValue(id, out var r) ? r : null;

            var oldOwnerSpec = Spec().WithBinding(held.Instance, Owner);
            var oldKeySpec = Spec().WithBinding(held.Instance, Member);
            var newOwnerSpec = Spec().WithBinding(live.Instance, Owner);

            Assert.AreSame(held, DungeonGemRules.ResolveGemRun(oldOwnerSpec, getRun));
            Assert.AreSame(held, DungeonGemRules.ResolveGemRun(oldKeySpec, getRun));
            Assert.AreSame(live, DungeonGemRules.ResolveGemRun(newOwnerSpec, getRun));
            Assert.IsNull(DungeonGemRules.ResolveGemRun(Spec(), getRun), "an unbound gem has no run");

            Assert.IsTrue(DungeonGemRules.IsRunLiveForGem(held, OldGem));
            Assert.IsTrue(DungeonGemRules.IsRunLiveForGem(held, OldKey));
            Assert.IsFalse(DungeonGemRules.IsRunLiveForGem(live, OldGem), "the old gem is not the new run's");
            Assert.IsFalse(DungeonGemRules.IsRunLiveForGem(live, OldKey));
            Assert.IsFalse(DungeonGemRules.IsRunLiveForGem(null, OldGem));

            Assert.IsTrue(DungeonGemRules.IsKeyUse(held, Member, OldKey));
            Assert.IsFalse(DungeonGemRules.IsKeyUse(live, Member, OldKey), "the old key is not a key of the new run");
            Assert.IsFalse(DungeonGemRules.IsKeyUse(held, Owner, OldGem), "the owner gem is never a key use");
            Assert.IsFalse(DungeonGemRules.IsKeyUse(held, Stranger, OldKey), "a key is only a key for its own member");

            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(oldOwnerSpec, OldGem, getRun), "the held run's owner gem survives");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(oldKeySpec, OldKey, getRun), "the held run's key survives");
            Assert.IsFalse(ThreadDungeonSweeper.IsDeadBoundGemCore(newOwnerSpec, NewGem, getRun));

            // The trap this guards: an owner-guid lookup returns the live run, where the old gem is not live.
            var byOwner = ThreadDungeonManager.GetRunForOwnerCore(new[] { held, live }, Owner);
            Assert.AreSame(live, byOwner);
            Assert.IsFalse(DungeonGemRules.IsRunLiveForGem(byOwner, OldGem));
        }
    }
}
