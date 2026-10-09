using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadExitGuardTests
    {
        private static bool Should(bool pooled = true, ThreadDungeonRunState state = ThreadDungeonRunState.Cleared, bool owner = true,
            bool inside = true, bool unclaimed = true, bool holds = false, bool bypass = false)
            => ThreadExitRules.ShouldPromptExit(pooled, state, owner, inside, unclaimed, holds, bypass);

        [TestMethod]
        public void Prompt_only_for_the_owner_of_a_cleared_pooled_run_with_loot_left()
        {
            Assert.IsTrue(Should());
            Assert.IsTrue(Should(unclaimed: false, holds: true), "loot sitting in a placed cache counts");
            Assert.IsFalse(Should(unclaimed: false, holds: false), "nothing left, nothing to ask");
            Assert.IsFalse(Should(pooled: false), "switch OFF never prompts");
            Assert.IsFalse(Should(state: ThreadDungeonRunState.Active), "an unfinished run can be re-entered with the gem");
            Assert.IsFalse(Should(state: ThreadDungeonRunState.Ended));
            Assert.IsFalse(Should(owner: false));
            Assert.IsFalse(Should(inside: false));
            Assert.IsFalse(Should(bypass: true), "the Yes re-issue goes through");
        }

        [TestMethod]
        public void A_refused_send_is_silent_for_our_own_prompt_and_reported_for_anyone_elses()
        {
            Assert.AreEqual(ExitPromptSend.Sent, ThreadExitRules.ClassifySend(true, null));
            Assert.AreEqual(ExitPromptSend.AlreadyPendingOurs, ThreadExitRules.ClassifySend(false, new Confirmation_ThreadExit(new ObjectGuid(0x50000001), () => { })));
            Assert.AreEqual(ExitPromptSend.BlockedByOtherPrompt, ThreadExitRules.ClassifySend(false, new Confirmation_Custom(new ObjectGuid(0x50000001), () => { })));
            Assert.AreEqual(ExitPromptSend.BlockedByOtherPrompt, ThreadExitRules.ClassifySend(false, null), "answered in between: tell the player to retry");
        }

        [TestMethod]
        public void Bypass_is_one_shot_and_expires()
        {
            var t0 = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

            ThreadExitGuard.GrantBypass(0x50000A01, t0);
            Assert.IsTrue(ThreadExitGuard.ConsumeBypass(0x50000A01, t0.AddSeconds(5)));
            Assert.IsFalse(ThreadExitGuard.ConsumeBypass(0x50000A01, t0.AddSeconds(5)), "one exit per Yes");

            ThreadExitGuard.GrantBypass(0x50000A02, t0);
            Assert.IsFalse(ThreadExitGuard.ConsumeBypass(0x50000A02, t0 + ThreadExitRules.BypassWindow + TimeSpan.FromSeconds(1)));

            Assert.IsFalse(ThreadExitGuard.ConsumeBypass(0x50000A03, t0), "never granted");
        }

        [TestMethod]
        public void Peek_reads_a_grant_without_spending_it()
        {
            var t0 = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

            Assert.IsFalse(ThreadExitGuard.PeekBypass(0x50000A05, t0), "never granted");

            ThreadExitGuard.GrantBypass(0x50000A05, t0);
            Assert.IsTrue(ThreadExitGuard.PeekBypass(0x50000A05, t0.AddSeconds(5)));
            Assert.IsTrue(ThreadExitGuard.PeekBypass(0x50000A05, t0.AddSeconds(5)), "a peek leaves the grant for the exit's own gate");
            Assert.IsTrue(ThreadExitGuard.ConsumeBypass(0x50000A05, t0.AddSeconds(5)), "the gate still spends it after a peek");
            Assert.IsFalse(ThreadExitGuard.PeekBypass(0x50000A05, t0.AddSeconds(5)), "consumed: gone");

            ThreadExitGuard.GrantBypass(0x50000A06, t0);
            Assert.IsFalse(ThreadExitGuard.PeekBypass(0x50000A06, t0 + ThreadExitRules.BypassWindow + TimeSpan.FromSeconds(1)), "an expired grant does not bypass");
            ThreadExitGuard.ConsumeBypass(0x50000A06, t0);
        }

        [TestMethod]
        public void Thread_exit_confirmation_is_a_custom_yes_no_that_acts_only_on_yes_for_an_online_player()
        {
            var ran = 0;
            var confirmation = new Confirmation_ThreadExit(new ObjectGuid(0x50000A04), () => ran++);

            Assert.IsInstanceOfType(confirmation, typeof(Confirmation_Custom));
            Assert.AreEqual(ConfirmationType.Yes_No, confirmation.ConfirmationType);

            confirmation.ProcessConfirmation(false);
            Assert.AreEqual(0, ran, "No leaves the player in place");

            confirmation.ProcessConfirmation(true);
            Assert.AreEqual(0, ran, "an offline player gets no re-issue");
        }

        [TestMethod]
        public void Pending_peek_is_read_only_and_empty_by_default()
        {
            var manager = new ConfirmationManager(null);

            Assert.IsFalse(manager.TryGetPending(ConfirmationType.Yes_No, out var pending));
            Assert.IsNull(pending);
        }

        [TestMethod]
        public void TryHoldExit_uses_the_shared_inside_check()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadExitGuard.cs"), "public static bool TryHoldExit(Player player, Action reissue)");

            StringAssert.Contains(body, "ThreadCachePlacer.IsOwnerInside(run, player)");
            Assert.IsFalse(body.Contains("Location.Instance == run.Instance"), "one inside rule (Instance AND Landblock), shared with /spawncache and delivery");
        }

        [TestMethod]
        public void Player_facing_lines_are_the_spec_text()
        {
            Assert.AreEqual("This Thread is complete and you will not be able to return. Your Thread Cache still holds loot. Leave anyway?", ThreadExitRules.PromptText);
            Assert.AreEqual("Answer your open confirmation first.", ThreadExitRules.AnswerOpenConfirmationMessage);
        }
    }
}
