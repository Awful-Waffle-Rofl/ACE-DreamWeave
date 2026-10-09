using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Templates;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// PvpTemplateEntrySequence (Docs/Pvp/TEMPLATES.md "Lifecycle"): the apply runs first, and the bind and the
    /// teleport happen ONLY from a successful apply's callback. A failed apply binds and teleports nothing and is
    /// reported to the coordinator (EntryFailed, a no-fault removal). Every step is a recording fake; the apply's
    /// callback is held so the test controls when (and with what) it completes.
    /// </summary>
    [TestClass]
    public class PvpTemplateEntrySequenceTests
    {
        private sealed class Steps
        {
            public readonly List<string> Order = new();
            public Action<PvpTemplateApplyResult> PendingCallback;
            public PvpTemplateDefinition AppliedDefinition;
            public Guid AppliedMatch;
            public bool EnterResult = true;
            public bool EnterThrows;
            public bool TeleportThrows;
            public string Failure;
            public bool FailurePlayerCaused;
            public bool FailureRetryable;

            public void Run(PvpPlayerBinding binding) => PvpTemplateEntrySequence.Run(
                binding,
                (def, matchId, cb) => { Order.Add("apply"); AppliedDefinition = def; AppliedMatch = matchId; PendingCallback = cb; },
                () => { Order.Add("enter"); if (EnterThrows) throw new InvalidOperationException("enter"); return EnterResult; },
                () => { Order.Add("teleport"); if (TeleportThrows) throw new InvalidOperationException("teleport"); },
                reason => Order.Add("restore"),
                (reason, playerCaused, retryable) => { Order.Add("report"); Failure = reason; FailurePlayerCaused = playerCaused; FailureRetryable = retryable; },
                (what, ex) => Order.Add("log"));
        }

        private static readonly PvpTemplateDefinition Duelist = new PvpTemplateDefinition { Key = "duelist", Version = 3 };

        private static PvpPlayerBinding Binding(PvpTemplateDefinition template = null, bool noMatch = false)
        {
            var match = noMatch ? null : new PvpMatch(Guid.NewGuid(), "arena_1v1",
                new List<PvpTeam> { new PvpTeam(0, new List<PvpParticipant> { new PvpParticipant(1, 1500) }), new PvpTeam(1, new List<PvpParticipant> { new PvpParticipant(2, 1500) }) },
                new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

            return new PvpPlayerBinding(match, 0, PvpMatchState.Staging, true, true, true, true, true, template: template);
        }

        /// <summary>DISCRIMINATES "teleport only after a successful apply": before the callback nothing but the apply has run.</summary>
        [TestMethod]
        public void NothingIsBoundOrTeleported_UntilTheApplyCallsBack()
        {
            var steps = new Steps();
            var binding = Binding(Duelist);

            steps.Run(binding);

            CollectionAssert.AreEqual(new[] { "apply" }, steps.Order);
            Assert.AreSame(Duelist, steps.AppliedDefinition, "the binding's frozen definition is what gets applied");
            Assert.AreEqual(binding.Match.MatchId, steps.AppliedMatch);
        }

        [TestMethod]
        public void Success_BindsThenTeleports()
        {
            var steps = new Steps();
            steps.Run(Binding(Duelist));

            steps.PendingCallback(PvpTemplateApplyResult.Ok());

            CollectionAssert.AreEqual(new[] { "apply", "enter", "teleport" }, steps.Order);
            Assert.IsNull(steps.Failure);
        }

        /// <summary>DISCRIMINATES the failure branch: a refused apply reports and neither binds nor teleports.</summary>
        [TestMethod]
        public void FailedApply_Reports_AndNeverBindsOrTeleports()
        {
            var steps = new Steps();
            steps.Run(Binding(Duelist));

            steps.PendingCallback(PvpTemplateApplyResult.Fail("no room"));

            CollectionAssert.AreEqual(new[] { "apply", "report" }, steps.Order);
            StringAssert.Contains(steps.Failure, "no room");
            Assert.IsFalse(steps.FailurePlayerCaused || steps.FailureRetryable, "a plain refusal carries no flags");
        }

        /// <summary>DISCRIMINATES the flag pass-through: a player-caused (and busy) refusal reaches the coordinator with both flags.</summary>
        [TestMethod]
        public void PlayerCausedAndBusy_AreCarriedToTheReport()
        {
            var steps = new Steps();
            steps.Run(Binding(Duelist));
            steps.PendingCallback(PvpTemplateApplyResult.Fail("busy", playerCaused: true, busy: true));
            Assert.IsTrue(steps.FailurePlayerCaused);
            Assert.IsTrue(steps.FailureRetryable);

            steps = new Steps();
            steps.Run(Binding(Duelist));
            steps.PendingCallback(PvpTemplateApplyResult.Fail("no room", playerCaused: true));
            Assert.IsTrue(steps.FailurePlayerCaused);
            Assert.IsFalse(steps.FailureRetryable);
        }

        [TestMethod]
        public void NullResult_IsAFailure()
        {
            var steps = new Steps();
            steps.Run(Binding(Duelist));

            steps.PendingCallback(null);

            CollectionAssert.AreEqual(new[] { "apply", "report" }, steps.Order);
        }

        /// <summary>A binding with no template (or no match) is never applied, bound or teleported: templates are mandatory.</summary>
        [TestMethod]
        public void NoTemplate_OrNoMatch_NeverApplies()
        {
            var steps = new Steps();
            steps.Run(Binding(template: null));
            CollectionAssert.AreEqual(new[] { "report" }, steps.Order);

            steps = new Steps();
            steps.Run(Binding(Duelist, noMatch: true));
            CollectionAssert.AreEqual(new[] { "report" }, steps.Order);

            steps = new Steps();
            steps.Run(null);
            CollectionAssert.AreEqual(new[] { "report" }, steps.Order);
        }

        /// <summary>DISCRIMINATES the bind-failure branch: the template comes off again, the failure is reported, and there is no teleport.</summary>
        [TestMethod]
        public void BindFails_AfterApply_RestoresAndReports_WithoutTeleport()
        {
            var steps = new Steps { EnterResult = false };
            steps.Run(Binding(Duelist));
            steps.PendingCallback(PvpTemplateApplyResult.Ok());
            CollectionAssert.AreEqual(new[] { "apply", "enter", "restore", "report" }, steps.Order);

            steps = new Steps { EnterThrows = true };
            steps.Run(Binding(Duelist));
            steps.PendingCallback(PvpTemplateApplyResult.Ok());
            CollectionAssert.AreEqual(new[] { "apply", "enter", "log", "restore", "report" }, steps.Order);
        }

        [TestMethod]
        public void TeleportThrows_IsLogged_AndNotReportedAsAnEntryFailure()
        {
            var steps = new Steps { TeleportThrows = true };
            steps.Run(Binding(Duelist));
            steps.PendingCallback(PvpTemplateApplyResult.Ok());

            CollectionAssert.AreEqual(new[] { "apply", "enter", "teleport", "log" }, steps.Order);
            Assert.IsNull(steps.Failure, "bound and templated: the staging timeout's no-fault cancel and its exit restore cover it");
        }
    }
}
