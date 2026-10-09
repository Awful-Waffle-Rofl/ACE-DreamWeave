using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Database;
using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvP Template Facets, Phase C: the coordinator wiring (Docs/Pvp/TEMPLATES.md "Freezing", "Lifecycle",
    /// "Commands"), driven through the same fakes as the rest of PvpMatchCoordinatorTests (this is a partial of that
    /// class). Every match is templated: the template is validated at join, at accept and at dispatch, frozen onto the
    /// bindings at dispatch, applied before the teleport, removed without fault when it does not apply, stamped onto the
    /// participant rows, and shown beside each name.
    /// </summary>
    public partial class PvpMatchCoordinatorTests
    {
        private static Rig TwoTemplateRig(string modesForMage = "1v1,2v2,ffa")
        {
            var rig = new Rig();
            rig.Sink.TemplateRows = new List<PvpTemplateRecord>
            {
                TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa"),
                TemplateRow("mage", "War Mage", 4, true, modesForMage),
            };
            return rig.Build();
        }

        private static void Reload(Rig rig, params PvpTemplateRecord[] rows)
        {
            rig.Sink.TemplateRows = rows.ToList();
            rig.Coordinator.BeginLoadTemplates();
        }

        // ================= join =================

        /// <summary>Drives: Join with no key and no remembered template. Refused NoTemplateChosen; nothing is queued.</summary>
        [TestMethod]
        public void Join_NoTemplateChosen_RefusesAndQueuesNothing()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { PreferredTemplateKey = null };

            var result = rig.Coordinator.Join(A, "1v1", false);

            Assert.AreEqual(PvpJoinRefusal.NoTemplateChosen, result.Refusal);
            Assert.AreEqual(0, rig.Coordinator.QueuedIds("1v1").Count);
        }

        /// <summary>Drives: Join with an explicit key. It overrides the remembered one, case-insensitively, and the result names it.</summary>
        [TestMethod]
        public void Join_ExplicitKey_OverridesThePreference()
        {
            var rig = TwoTemplateRig();
            rig.Gateway.Add(A, "Alpha");

            var result = rig.Coordinator.Join(A, "1v1", false, "Mage");

            Assert.IsTrue(result.Joined);
            Assert.AreEqual("mage", result.TemplateKey);
            Assert.AreEqual("War Mage", result.TemplateLabel);
        }

        /// <summary>
        /// Drives: Join on a template not offered for the mode, then on a disabled one. DISCRIMINATES the join-time
        /// validation: both refused TemplateNotOffered, nothing queued, and the room check never runs for them.
        /// </summary>
        [TestMethod]
        public void Join_TemplateNotOfferedForTheMode_OrDisabled_Refuses()
        {
            var rig = new Rig();
            rig.Sink.TemplateRows = new List<PvpTemplateRecord>
            {
                TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1"),
                TemplateRow("mage", "War Mage", 1, false, "1v1,2v2,ffa"),
            };
            rig.Build();
            rig.Gateway.Add(A, "Alpha");

            var wrongMode = rig.Coordinator.Join(A, "2v2", false);
            Assert.AreEqual(PvpJoinRefusal.TemplateNotOffered, wrongMode.Refusal);
            Assert.AreEqual(DefaultTemplateKey, wrongMode.TemplateKey);

            var disabled = rig.Coordinator.Join(A, "1v1", false, "mage");
            Assert.AreEqual(PvpJoinRefusal.TemplateNotOffered, disabled.Refusal);

            Assert.AreEqual(0, rig.Coordinator.QueuedIds("1v1").Count + rig.Coordinator.QueuedIds("2v2").Count);
            Assert.AreEqual(0, rig.Gateway.RoomChecks.Count);
        }

        /// <summary>Drives: Join while the player lacks pack room. DISCRIMINATES the join-time room precheck.</summary>
        [TestMethod]
        public void Join_NoPackRoom_Refuses_WithTheRoomLine()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.RoomRefusal[A] = "[Arena] room line";

            var result = rig.Coordinator.Join(A, "1v1", false);

            Assert.AreEqual(PvpJoinRefusal.TemplateNoRoom, result.Refusal);
            Assert.AreEqual("[Arena] room line", PvpMatchCoordinator.RefusalText(result));
            Assert.AreEqual(DefaultTemplateKey, rig.Gateway.RoomChecks.Single().Template.Key, "the room check is run against the template's definition");
        }

        /// <summary>Drives: Join by a character on the template account. Refused TemplateAccount (the fact alone flips it).</summary>
        [TestMethod]
        public void Join_TemplateAccountCharacter_IsRefused()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { IsTemplateAccount = true };

            var result = rig.Coordinator.Join(A, "1v1", false);

            Assert.AreEqual(PvpJoinRefusal.TemplateAccount, result.Refusal);
            Assert.AreEqual(PvpArenaText.TemplateAccountRefused, PvpMatchCoordinator.RefusalText(result));

            rig.Gateway.Players[A].Facts = rig.Gateway.Players[A].Facts with { IsTemplateAccount = false };
            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined, "control: the same character off the template account joins");
        }

        /// <summary>Drives: Join by a character still carrying a restore record. Refused TemplateLocked.</summary>
        [TestMethod]
        public void Join_StillTemplated_IsRefused()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { IsPvpTemplated = true };

            Assert.AreEqual(PvpJoinRefusal.TemplateLocked, rig.Coordinator.Join(A, "1v1", false).Refusal);
        }

        /// <summary>Drives: Join before the catalog read lands, and with pvp_template_enabled off. Both refused: templates are mandatory.</summary>
        [TestMethod]
        public void Join_CatalogNotLoaded_OrTemplatesOff_Refuses()
        {
            var rig = new Rig().Build(loadRatings: false);
            rig.Coordinator.BeginLoadRatings();
            rig.Tick();
            rig.Gateway.Add(A, "Alpha");

            Assert.AreEqual(PvpJoinRefusal.TemplatesUnavailable, rig.Coordinator.Join(A, "1v1", false).Refusal);

            rig.Coordinator.BeginLoadTemplates();
            rig.Tick();

            var saved = PvpTemplateSettings.EnabledSource;

            try
            {
                PvpTemplateSettings.EnabledSource = () => false;
                Assert.AreEqual(PvpJoinRefusal.TemplatesDisabled, rig.Coordinator.Join(A, "1v1", false).Refusal);
            }
            finally
            {
                PvpTemplateSettings.EnabledSource = saved;
            }

            Assert.IsTrue(rig.Coordinator.Join(A, "1v1", false).Joined, "control: loaded and on, the join goes through");
        }

        /// <summary>Drives: a duo join where the partner remembers a different template. Each fights on their own.</summary>
        [TestMethod]
        public void Join_Duo_PartnerUsesTheirOwnTemplate()
        {
            var rig = TwoTemplateRig();
            rig.Gateway.Add(A, "Alpha", fellowship: new[] { A, B });
            rig.Gateway.Add(B, "Bravo", fellowship: new[] { A, B }).Facts = rig.Gateway.Players[B].Facts with { PreferredTemplateKey = "mage" };
            rig.Gateway.Add(C, "Charlie");
            rig.Gateway.Add(D, "Delta");

            Assert.IsTrue(rig.Coordinator.Join(A, "2v2", true, DefaultTemplateKey).Joined);
            Assert.IsTrue(rig.Coordinator.Join(C, "2v2", false).Joined);
            Assert.IsTrue(rig.Coordinator.Join(D, "2v2", false).Joined);

            rig.Clock.Advance(rig.Dials.DuoVsSoloAfterSeconds);
            rig.Tick();
            rig.AcceptAll(A, B, C, D);
            rig.DriveAcceptedToLive(A);

            Assert.AreEqual("mage", rig.Gateway.Entered.Single(e => e.Id == B).Binding.Template.Key);
            Assert.AreEqual(DefaultTemplateKey, rig.Gateway.Entered.Single(e => e.Id == A).Binding.Template.Key);
        }

        // ================= accept =================

        /// <summary>
        /// Drives: Join x2, Tick (AwaitingAccept), the template is disabled and the catalog reloaded, Accept. DISCRIMINATES
        /// the accept-time re-check: the accept becomes a decline with the withdrawn line and NO lockout (not the player's
        /// doing); the other player is requeued at the front.
        /// </summary>
        [TestMethod]
        public void Accept_TemplateWithdrawn_IsADeclineWithoutLockout()
        {
            var rig = TwoTemplateRig();
            rig.Gateway.Add(A, "Alpha").Facts = rig.Gateway.Players[A].Facts with { PreferredTemplateKey = "mage" };
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State);

            Reload(rig, TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa"), TemplateRow("mage", "War Mage", 4, false, "1v1"));
            rig.Tick();

            var result = rig.Coordinator.Accept(A);

            Assert.AreEqual(PvpAnswerOutcome.Declined, result.Outcome);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] The mage template is no longer offered for 1v1, so you were taken out of this match with no penalty. Choose another with /arena templates."));
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A), "a withdrawn template is not the player's fault");
            CollectionAssert.AreEqual(new[] { B }, rig.Coordinator.QueuedIds("1v1").ToList());
        }

        /// <summary>Drives: the accept-time room re-check. A full pack at accept is a decline WITH the usual lockout.</summary>
        [TestMethod]
        public void Accept_NoPackRoom_IsADeclineWithLockout()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();

            rig.Gateway.RoomRefusal[A] = "[Arena] room line";

            Assert.AreEqual(PvpAnswerOutcome.Declined, rig.Coordinator.Accept(A).Outcome);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] room line"));
            Assert.IsTrue(rig.Coordinator.IsLockedOut(A));
        }

        // ================= dispatch: the third check, and the freeze =================

        /// <summary>
        /// Drives: 1v1 accepted, Tick (Staging), then B's template is withdrawn before dispatch, Tick. DISCRIMINATES the
        /// dispatch-time check: nobody is placed, B is removed with no penalty, A goes back to the front, no save.
        /// </summary>
        [TestMethod]
        public void Dispatch_TemplateWithdrawnAfterAccept_CancelsWithoutFault()
        {
            var rig = TwoTemplateRig();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo").Facts = rig.Gateway.Players[B].Facts with { PreferredTemplateKey = "mage" };
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Staging, rig.MatchOf(A).State);

            Reload(rig, TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa"), TemplateRow("mage", "War Mage", 4, true, "ffa"));
            rig.Tick();

            Assert.IsNull(rig.MatchOf(A), "the match is canceled");
            Assert.AreEqual(0, rig.Gateway.Entered.Count, "nobody is placed");
            Assert.IsTrue(rig.Gateway.Got(B, "[Arena] The mage template is no longer offered for 1v1, so you were taken out of this match with no penalty. Choose another with /arena templates."));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.OtherCouldNotEnter));
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToList());
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A) || rig.Coordinator.IsLockedOut(B));
            Assert.AreEqual(0, rig.Sink.Saves.Count);
        }

        /// <summary>
        /// Drives: 1v1 to Live, then the template is re-snapshotted (v2, new name) and the catalog reloaded, then a death
        /// and the post-match close. DISCRIMINATES the freeze: every binding the match ever publishes carries the SAME
        /// v1 definition object frozen at dispatch, and the stamp records v1.
        /// </summary>
        [TestMethod]
        public void Freeze_AReSnapshotAfterDispatch_NeverChangesTheMatch()
        {
            var rig = OneVOneLive(out var match);
            var frozen = rig.Gateway.Entered.Single(e => e.Id == A).Binding.Template;

            Assert.IsNotNull(frozen);
            Assert.AreEqual(1u, frozen.Version);

            Reload(rig, TemplateRow(DefaultTemplateKey, "Duelist Mk II", 2, true, "1v1,2v2,ffa"));
            rig.Tick();
            Assert.AreEqual(2u, rig.Coordinator.Templates.Get(DefaultTemplateKey).Version, "sanity: the catalog now holds v2");

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();
            rig.Gateway.Players[B].Instance = 0;
            rig.PostMatch();

            var bindings = rig.Gateway.PublishedBindings.Where(p => p.Id == A).Select(p => p.Binding).ToList();
            Assert.IsTrue(bindings.Count >= 3, "Countdown, Live and Resolving were published");
            Assert.IsTrue(bindings.All(b => ReferenceEquals(b.Template, frozen)), "every later binding carries the dispatch-time copy");

            var stamp = rig.Sink.StampWrites.Single().Stamps.Single(s => s.CharacterId == A);
            Assert.AreEqual(1u, stamp.TemplateVersion);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] 1v1 match: Alpha (Duelist) defeated Bravo (Duelist)."), "the result shows the frozen label, not the re-snapshot's");
        }

        /// <summary>
        /// The ONE kit-warm path: a match forming warms each distinct template's kit once (one player on Duelist and one on
        /// War Mage is two warms), before the accept window, and dispatch does not warm again.
        /// </summary>
        [TestMethod]
        public void WarmKit_RunsOncePerDistinctTemplate_WhenTheMatchForms()
        {
            var saved = PvpMatchCoordinator.WarmKit;
            var warmed = new List<string>();

            try
            {
                PvpMatchCoordinator.WarmKit = d => warmed.Add(d.Key);

                var rig = TwoTemplateRig();
                rig.Gateway.Add(A, "Alpha");
                rig.Gateway.Add(B, "Bravo").Facts = rig.Gateway.Players[B].Facts with { PreferredTemplateKey = "mage" };
                rig.Coordinator.Join(A, "1v1", false);
                rig.Coordinator.Join(B, "1v1", false);

                Assert.AreEqual(0, warmed.Count, "joining warms nothing");

                rig.Tick();
                Assert.AreEqual(PvpMatchState.AwaitingAccept, rig.MatchOf(A).State);
                CollectionAssert.AreEquivalent(new[] { DefaultTemplateKey, "mage" }, warmed);

                rig.AcceptAll(A, B);
                rig.DriveAcceptedToLive(A);
                Assert.AreEqual(2, warmed.Count, "dispatch does not warm a second time");
            }
            finally
            {
                PvpMatchCoordinator.WarmKit = saved;
            }
        }

        /// <summary>The five arena masks are forced on for every templated match, whatever the pvp_arena_suppress_* dials say.</summary>
        [TestMethod]
        public void Masks_AreForcedOn_EvenWithTheSuppressDialsOff()
        {
            var rig = new Rig { Dials = PvpTunables.Defaults with { SuppressClassAbilities = false, SuppressEquipmentMods = false, SuppressWeaponMods = false, SuppressPickupBoons = false, SuppressTurnSpeed = false } }.Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.DriveAcceptedToLive(A);

            foreach (var b in rig.Gateway.Entered.Select(e => e.Binding).Concat(rig.Gateway.PublishedBindings.Select(p => p.Binding)))
            {
                Assert.IsTrue(b.ClassAbilitiesSuppressed && b.EquipmentModsSuppressed && b.WeaponModsSuppressed && b.PickupBoonsSuppressed && b.TurnSpeedSuppressed);
                Assert.IsNotNull(b.Template);
            }
        }

        // ================= entry failure: no-fault removal =================

        /// <summary>
        /// Drives: 1v1 accepted; B's template apply fails at entry (the gateway reports EntryFailed and never teleports).
        /// The match is canceled without fault: B is never exited (never bound), A is exited and returned once and goes
        /// back to the front of the queue, nobody is locked out, nothing is saved or rated.
        /// </summary>
        [TestMethod]
        public void EntryFailure_TwoTeam_CancelsWithoutFault_AndRequeuesTheOther()
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Gateway.FailApplyFor.Add(B);
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.Tick(); // Staging, allocate
            rig.Tick(); // dispatch: A applies and arrives; B's apply fails and is reported
            rig.Tick(); // the EntryFailed intent is drained

            Assert.IsNull(rig.MatchOf(A), "canceled");
            Assert.IsTrue(rig.Gateway.Got(B, PvpArenaText.EntryFailedSelf));
            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.OtherCouldNotEnterPlaced));
            Assert.AreEqual(0, rig.Gateway.ExitCount(B), "B was never bound, so there is nothing to exit");
            Assert.AreEqual(1, rig.Gateway.ExitCount(A));
            CollectionAssert.AreEqual(new[] { A }, rig.Gateway.Returned);
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToList());
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A) || rig.Coordinator.IsLockedOut(B));
            Assert.AreEqual(0, rig.Sink.Saves.Count);
            Assert.AreEqual(1, rig.Spaces.Released.Count);
        }

        /// <summary>
        /// Drives: an FFA of 6 (floor 5) where one apply fails. The seat is dropped and the match carries on to Live
        /// with five; the failed player is never exited, rated or recorded.
        /// </summary>
        [TestMethod]
        public void EntryFailure_Ffa_DropsTheSeat_AndCarriesOnAboveTheFloor()
        {
            var rig = FfaRig(6, out var ids);
            rig.Gateway.FailApplyFor.Add(F);

            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);

            rig.Tick();
            rig.AcceptAll(ids);
            rig.Tick(); // Staging
            rig.Tick(); // dispatch
            rig.Tick(); // drain EntryFailed; the other five arrived -> Countdown

            var match = rig.MatchOf(A);
            Assert.IsNotNull(match);
            Assert.IsNull(rig.MatchOf(F), "the failed seat is gone");
            Assert.AreEqual(5, match.Teams.Count);
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Foxtrot could not enter the match. The match goes on without them."));

            rig.Clock.Advance(rig.Dials.CountdownSeconds);
            rig.Tick();
            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.AreEqual(0, rig.Gateway.ExitCount(F));
            Assert.IsTrue(rig.Gateway.Got(A, "[Arena] Tugak Brawl: Alpha (Duelist), Bravo (Duelist), Charlie (Duelist), Delta (Duelist), Echo (Duelist)."));
        }

        /// <summary>Drives: an FFA of exactly the floor (5) with one failed apply: below the floor, so canceled and the rest requeued.</summary>
        [TestMethod]
        public void EntryFailure_Ffa_BelowTheFloor_CancelsAndRequeues()
        {
            var rig = FfaRig(5, out var ids);
            rig.Dials = rig.Dials with { FfaTargetPlayers = 5 };
            rig.Gateway.FailApplyFor.Add(E);

            foreach (var id in ids)
                rig.Coordinator.Join(id, "ffa", false);

            rig.Tick();
            rig.AcceptAll(ids);
            rig.Tick();
            rig.Tick();
            rig.Tick();

            Assert.IsNull(rig.MatchOf(A));
            CollectionAssert.AreEquivalent(new[] { A, B, C, D }, rig.Coordinator.QueuedIds("ffa").ToList());
            Assert.AreEqual(0, rig.Gateway.ExitCount(E));
            Assert.AreEqual(4, rig.Gateway.Exited.Count, "the four placed are exited once each");
        }

        private static Rig OneVOneDispatched(Action<Rig> arrange)
        {
            var rig = new Rig().Build();
            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            arrange(rig);
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            rig.Tick(); // Staging, allocate
            rig.Tick(); // dispatch
            return rig;
        }

        /// <summary>
        /// Drives: B's apply fails because of B (a full pack). DISCRIMINATES the player-caused lockout: B is locked out,
        /// A still goes back to the front of the queue with no lockout, nothing is rated.
        /// </summary>
        [TestMethod]
        public void EntryFailure_PlayerCaused_LocksOutTheCauser_TheOtherIsRequeuedWithoutFault()
        {
            var rig = OneVOneDispatched(r => r.Gateway.PlayerCausedFailFor.Add(B));
            rig.Tick(); // drain

            Assert.IsNull(rig.MatchOf(A));
            Assert.IsTrue(rig.Coordinator.IsLockedOut(B), "a player-caused entry failure locks the causer out");
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A));
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToList());
            Assert.AreEqual(0, rig.Sink.Saves.Count);
        }

        /// <summary>
        /// Drives: B is busy for two applies, then not. DISCRIMINATES the busy retry: B is re-sent after the retry
        /// interval (not failed), the match reaches Countdown, and nobody is locked out.
        /// </summary>
        [TestMethod]
        public void EntryBusy_IsRetried_AndTheMatchGoesOn()
        {
            var rig = OneVOneDispatched(r => r.Gateway.BusyFailuresFor[B] = 2);

            for (var i = 0; i < 6; i++)
            {
                rig.Clock.Advance(PvpMatchCoordinator.EntryBusyRetryIntervalSeconds);
                rig.Tick();
            }

            Assert.AreEqual(3, rig.Gateway.Entered.Count(e => e.Id == B), "two busy refusals, then the third apply succeeds");
            Assert.AreEqual(PvpMatchState.Countdown, rig.MatchOf(A).State);
            Assert.IsFalse(rig.Coordinator.IsLockedOut(B));
            Assert.IsFalse(rig.Gateway.Got(B, PvpArenaText.EntryFailedSelf));
        }

        /// <summary>
        /// Drives: B stays busy. After the retry window the busy failure counts: B hears the busy line, is locked out,
        /// and A is requeued without fault.
        /// </summary>
        [TestMethod]
        public void EntryBusy_PastTheWindow_IsAPlayerCausedFailure()
        {
            var rig = OneVOneDispatched(r => r.Gateway.BusyFailuresFor[B] = 1000);

            for (var i = 0; i < 12 && rig.MatchOf(A) != null; i++)
            {
                rig.Clock.Advance(PvpMatchCoordinator.EntryBusyRetryIntervalSeconds);
                rig.Tick();
            }

            Assert.IsNull(rig.MatchOf(A));
            Assert.IsTrue(rig.Gateway.Got(B, PvpTemplateText.ApplyBusy));
            Assert.IsTrue(rig.Coordinator.IsLockedOut(B));
            Assert.IsFalse(rig.Coordinator.IsLockedOut(A));
            CollectionAssert.AreEqual(new[] { A }, rig.Coordinator.QueuedIds("1v1").ToList());
            Assert.IsTrue(rig.Gateway.Entered.Count(e => e.Id == B) >= 3, "re-sent repeatedly through the window (each retry is one interval after the busy report is drained)");
        }

        /// <summary>An EntryFailed intent for a seat that already arrived (or a match past Staging) changes nothing.</summary>
        [TestMethod]
        public void EntryFailure_StaleIntent_IsIgnored()
        {
            var rig = OneVOneLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.EntryFailed(B, match.MatchId, rig.Clock.Now));
            rig.Tick();

            Assert.AreEqual(PvpMatchState.Live, match.State);
            Assert.IsFalse(rig.Gateway.Got(B, PvpArenaText.EntryFailedSelf));
        }

        // ================= exits: each reaches the (player-side) restore exactly once =================

        /// <summary>
        /// The coordinator half of "match end, admin cancel, death and logout each restore exactly once": every path
        /// issues exactly ONE ExitMatch (whose ExitPvpMatchNow runs the idempotent restore first) per participant, and a
        /// death adds no second exit at close. The player half is PvpTemplateExitHookTests.
        /// </summary>
        [TestMethod]
        public void Exits_MatchEnd_Death_Logout_AdminCancel_EachExitOnce()
        {
            // match end + death: B dies (exited once after landing), A survives (exited once at close)
            var rig = OneVOneLive(out var match);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();
            rig.Gateway.Players[B].Instance = 0;
            rig.Tick();
            rig.PostMatch();
            Assert.AreEqual(PvpMatchState.Closed, match.State);
            Assert.AreEqual(1, rig.Gateway.ExitCount(A), "match end");
            Assert.AreEqual(1, rig.Gateway.ExitCount(B), "death");

            // logout
            rig = OneVOneLive(out match);
            rig.Logout(B, match.MatchId);
            rig.Tick();
            rig.PostMatch();
            Assert.AreEqual(1, rig.Gateway.ExitCount(B), "logout");
            Assert.AreEqual(1, rig.Gateway.ExitCount(A));

            // admin cancel
            rig = OneVOneLive(out match);
            rig.Coordinator.Cancel(match.MatchId);
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.ExitCount(A), "admin cancel");
            Assert.AreEqual(1, rig.Gateway.ExitCount(B), "admin cancel");
        }

        // ================= the per-tick backstop =================

        /// <summary>
        /// Drives: 1v1 to Live, ticks. The backstop is asked for each live participant at most once a second, and a
        /// BackstopFired intent is noted on the match.
        /// </summary>
        [TestMethod]
        public void Backstop_RunsForEachLiveParticipant_OncePerSecond_AndNotesMoves()
        {
            var rig = OneVOneLive(out var match);
            rig.Gateway.Backstops.Clear();

            rig.Clock.Advance(1);
            rig.Tick();
            CollectionAssert.AreEquivalent(new[] { A, B }, rig.Gateway.Backstops.Select(b => b.Id).ToList());
            Assert.IsTrue(rig.Gateway.Backstops.All(b => b.Match == match.MatchId));

            rig.Clock.Advance(0.25);
            rig.Tick();
            Assert.AreEqual(2, rig.Gateway.Backstops.Count, "not again within the second");

            rig.Intents.Enqueue(PvpMatchManager.BackstopFired(A, match.MatchId, rig.Clock.Now, 2));
            rig.Tick();
            Assert.AreEqual(2, rig.Coordinator.TemplateBackstopMoves(match.MatchId));
            Assert.AreEqual(PvpMatchState.Live, match.State, "moves alone never eject");
            Assert.AreEqual(0, rig.Gateway.ExitCount(A));
        }

        /// <summary>
        /// Drives: 1v1 Live, a BackstopFired intent reporting a personal item still worn after the move. DISCRIMINATES the
        /// survivor ejection: A is forfeited out, exited (so restored) and returned exactly once; B wins.
        /// </summary>
        [TestMethod]
        public void Backstop_Survivors_EjectAndRestoreTheParticipant()
        {
            var rig = OneVOneLive(out var match);

            rig.Intents.Enqueue(PvpMatchManager.BackstopFired(A, match.MatchId, rig.Clock.Now, 0, survivors: 1));
            rig.Tick();

            Assert.IsTrue(rig.Gateway.Got(A, PvpArenaText.TemplateBackstopEjected));
            Assert.AreEqual(ParticipantExit.ForfeitCommand, match.Teams.SelectMany(t => t.Members).Single(p => p.CharacterId == A).ExitReason);
            Assert.AreEqual(1, rig.Gateway.ExitCount(A));
            CollectionAssert.Contains(rig.Gateway.Returned, A);

            rig.Intents.Enqueue(PvpMatchManager.BackstopFired(A, match.MatchId, rig.Clock.Now, 0, survivors: 1));
            rig.Tick();
            Assert.AreEqual(1, rig.Gateway.ExitCount(A), "a repeat report never exits twice");
        }

        // ================= records and display =================

        /// <summary>
        /// Drives: a 1v1 on two different templates to a kill. The lineup at Live, the in-game result and the Discord
        /// post name each template; the stamps go out after the Saved result with its pvp_match id; /top's template
        /// lookup answers.
        /// </summary>
        [TestMethod]
        public void Display_AndStamps_NameEachPlayersTemplate()
        {
            string posted = null;
            PvpMatchCoordinator.QueuePvpForTest = text => posted = text;

            try
            {
                var rig = TwoTemplateRig();
                rig.Gateway.Add(A, "Alpha");
                rig.Gateway.Add(B, "Bravo").Facts = rig.Gateway.Players[B].Facts with { PreferredTemplateKey = "mage" };
                rig.Coordinator.Join(A, "1v1", false);
                rig.Coordinator.Join(B, "1v1", false);
                rig.Tick();
                rig.AcceptAll(A, B);
                var match = rig.DriveAcceptedToLive(A);

                Assert.IsTrue(rig.Gateway.Got(A, "[Arena] 1v1: Alpha (Duelist) vs Bravo (War Mage)."), "the arena announcement");

                rig.Death(B, match.MatchId, killer: A);
                rig.Tick();

                Assert.IsTrue(rig.Gateway.Got(B, "[Arena] 1v1 match: Alpha (Duelist) defeated Bravo (War Mage)."), "the results line");
                Assert.AreEqual("[Arena] 1v1 match: Alpha (Duelist) defeated Bravo (War Mage).", posted, "the Discord post");

                Assert.AreEqual(0, rig.Sink.StampWrites.Count, "nothing is stamped before the save result is drained");
                rig.Tick();

                var write = rig.Sink.StampWrites.Single();
                Assert.AreEqual(77u, write.DbMatchId, "stamped onto the saved pvp_match id");
                CollectionAssert.AreEquivalent(new[] { (A, DefaultTemplateKey, 1u), (B, "mage", 4u) }, write.Stamps.Select(s => (s.CharacterId, s.TemplateKey, s.TemplateVersion)).ToList());

                Assert.AreEqual("War Mage", rig.Coordinator.LatestTemplateLabel(B, "arena_1v1"));
                Assert.IsNull(rig.Coordinator.LatestTemplateLabel(B, "arena_2v2"));
            }
            finally
            {
                PvpMatchCoordinator.QueuePvpForTest = null;
            }
        }

        /// <summary>A save that comes back Failed drops the stamps: they target rows that were never written.</summary>
        [TestMethod]
        public void Stamps_AreDropped_WhenTheSaveFails()
        {
            var rig = OneVOneLive(out var match);
            rig.Sink.SaveResult = PvpMatchSaveResult.Failed;

            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();
            rig.Tick();

            Assert.AreEqual(0, rig.Sink.StampWrites.Count);
        }

        /// <summary>The boot read of the latest stamped templates feeds /top, and a newer resolved match wins over it.</summary>
        [TestMethod]
        public void LatestTemplates_BootReadFeedsTop_AndALaterMatchWins()
        {
            var rig = new Rig();
            rig.Sink.LatestTemplates = new List<PvpLatestParticipantTemplateRecord>
            {
                new PvpLatestParticipantTemplateRecord { CharacterId = A, Ladder = "arena_1v1", TemplateKey = "mage", TemplateVersion = 1 },
            };
            rig.Sink.TemplateRows = new List<PvpTemplateRecord> { TemplateRow(DefaultTemplateKey, "Duelist", 1, true, "1v1,2v2,ffa"), TemplateRow("mage", "War Mage", 1, true, "1v1") };
            rig.Build();

            Assert.AreEqual("War Mage", rig.Coordinator.LatestTemplateLabel(A, "arena_1v1"));

            rig.Gateway.Add(A, "Alpha");
            rig.Gateway.Add(B, "Bravo");
            rig.Coordinator.Join(A, "1v1", false);
            rig.Coordinator.Join(B, "1v1", false);
            rig.Tick();
            rig.AcceptAll(A, B);
            var match = rig.DriveAcceptedToLive(A);
            rig.Death(B, match.MatchId, killer: A);
            rig.Tick();

            Assert.AreEqual("Duelist", rig.Coordinator.LatestTemplateLabel(A, "arena_1v1"));
        }

        // ================= the catalog itself (pure) =================

        [TestMethod]
        public void Catalog_ModesParse_AndUnparseableRowsAreNeverOffered()
        {
            var broken = TemplateRow("broken", "Broken", 1, true, "1v1");
            broken.DefinitionJson = "{not json";

            var warnings = new List<string>();
            var catalog = PvpTemplateCatalog.Build(new[] { TemplateRow("duelist", "Duelist", 3, true, " FFA ,1v1,bogus,1v1"), broken }, warnings.Add);

            CollectionAssert.AreEqual(new[] { "1v1", "ffa" }, catalog.Get("duelist").Modes.ToList(), "canonical order, unknown dropped, duplicates collapsed");
            Assert.IsTrue(catalog.TryGetOffered("DUELIST", "ffa", out var entry));
            Assert.AreEqual(3u, entry.Definition.Version, "the row's version is authoritative");
            Assert.IsFalse(catalog.TryGetOffered("duelist", "2v2", out _));
            Assert.IsFalse(catalog.TryGetOffered("broken", "1v1", out _));
            Assert.AreEqual(1, warnings.Count);

            Assert.AreEqual("1v1,2v2", PvpTemplateCatalog.NormalizeModes("2v2, 1V1", out _));
            Assert.AreEqual("", PvpTemplateCatalog.NormalizeModes("none", out _));
            Assert.IsNull(PvpTemplateCatalog.NormalizeModes("1v1,3v3", out var error));
            StringAssert.Contains(error, "3v3");
        }

        /// <summary>
        /// /pvptemplate modes (owner ruling 2026-10-03, battlegrounds templated): "koth" and "bg_koth" both name King of the
        /// Hill and are stored as the full mode key, a stored "koth" column is offered for bg_koth, and the error lists koth.
        /// </summary>
        [TestMethod]
        public void Catalog_KothModeKey_AcceptedInBothForms_StoredFull()
        {
            CollectionAssert.AreEqual(new[] { "1v1", "2v2", "ffa", "bg_koth", "bg_ad" }, PvpTemplateCatalog.ModeKeys.ToList(), "derived from the mode catalogs, in display order");

            Assert.AreEqual("1v1,bg_koth", PvpTemplateCatalog.NormalizeModes("KOTH, 1v1", out _));
            Assert.AreEqual("bg_koth", PvpTemplateCatalog.NormalizeModes("bg_koth,koth", out _));

            var catalog = PvpTemplateCatalog.Build(new[] { TemplateRow("brawler", "Brawler", 1, true, "koth") });
            Assert.IsTrue(catalog.TryGetOffered("brawler", "bg_koth", out _));
            Assert.IsFalse(catalog.TryGetOffered("brawler", "1v1", out _));

            Assert.IsNull(PvpTemplateCatalog.NormalizeModes("ctf", out var error));
            StringAssert.Contains(error, "1v1, 2v2, tugak, koth");
        }
    }
}
