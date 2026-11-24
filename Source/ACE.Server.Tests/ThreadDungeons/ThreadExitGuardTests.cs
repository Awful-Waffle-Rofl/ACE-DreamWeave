using System;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;
using ACE.Server.WorldObjects.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using static ACE.Server.Tests.ThreadDungeons.PeddlerTestFixtures;

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
            Assert.AreEqual("The Wandering Peddler packs up as you turn to go, and your Thread Cache forms. Collect it before you leave.", ThreadExitRules.ShopReleasedMessage);
        }

        private const string ShopMessage = "The Wandering Peddler packs up as you turn to go, and your Thread Cache forms. Collect it before you leave.";

        /// <summary>No PropertyManager key is read: runs are built directly by the peddler fixtures.</summary>
        private static ThreadDungeonRun OpenSoloShop()
        {
            var run = ActiveSoloRun();
            GivePlan(run);
            ClearWithPiles(run, 3, run.OwnerGuid);
            OpenShop(run, run.OwnerGuid);
            return run;
        }

        [TestMethod]
        public void An_exit_with_the_shop_open_releases_it_holds_once_and_messages_once()
        {
            var run = OpenSoloShop();
            var owner = run.OwnerGuid;
            var sent = new System.Collections.Generic.List<string>();

            Assert.IsTrue(ThreadExitGuard.WouldReleaseShop(run, owner, true), "prediction first: pure");
            Assert.IsTrue(run.IsPeddlerShopOpen(owner), "the prediction released nothing");

            Assert.IsTrue(ThreadExitGuard.TryReleaseOpenShop(run, owner, true, sent.Add), "the exit is held");
            Assert.IsFalse(run.IsPeddlerShopOpen(owner), "the shop is released");
            Assert.IsTrue(run.IsPeddlerShopDone(owner));
            Assert.AreEqual(1, sent.Count);
            Assert.AreEqual(ShopMessage, sent[0]);

            Assert.IsFalse(ThreadExitGuard.WouldReleaseShop(run, owner, true), "a second exit follows the normal loot-prompt path");
            Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(run, owner, true, sent.Add), "nothing more to release, nothing more sent");
            Assert.AreEqual(1, sent.Count);
        }

        [TestMethod]
        public void With_no_open_shop_nothing_is_released_held_or_sent()
        {
            var sent = new System.Collections.Generic.List<string>();

            var solo = ActiveSoloRun();
            Assert.IsFalse(ThreadExitGuard.WouldReleaseShop(solo, solo.OwnerGuid, true));
            Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(solo, solo.OwnerGuid, true, sent.Add));

            var done = OpenSoloShop();
            Assert.IsTrue(done.TryMarkShopDone(done.OwnerGuid));
            Assert.IsFalse(ThreadExitGuard.WouldReleaseShop(done, done.OwnerGuid, true), "a done shop is the prompt path as before");
            Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(done, done.OwnerGuid, true, sent.Add));

            Assert.IsFalse(ThreadExitGuard.WouldReleaseShop(null, 0x50000001u, true));
            Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(null, 0x50000001u, true, sent.Add));
            Assert.AreEqual(0, sent.Count);

            // positive control: the run still prompts exactly as before.
            Assert.IsTrue(ThreadExitRules.ShouldPromptExit(true, ThreadDungeonRunState.Cleared, true, true, true, false, false));
        }

        [TestMethod]
        public void A_player_outside_the_run_copy_is_never_held_and_the_shop_stays_open()
        {
            var run = OpenSoloShop();
            var owner = run.OwnerGuid;
            var sent = new System.Collections.Generic.List<string>();

            Assert.IsFalse(ThreadExitGuard.WouldReleaseShop(run, owner, false));
            Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(run, owner, false, sent.Add));
            Assert.IsTrue(run.IsPeddlerShopOpen(owner), "not released");
            Assert.AreEqual(0, sent.Count, "no message");
        }

        [TestMethod]
        public void A_lost_release_race_falls_through_to_the_normal_exit_without_a_message()
        {
            var run = OpenSoloShop();
            var owner = run.OwnerGuid;
            var sent = new System.Collections.Generic.List<string>();
            var original = ThreadPeddlerShop.RequestPlacement;

            try
            {
                ThreadPeddlerShop.RequestPlacement = (r, g) => CacheRequestOutcome.Queued;

                // Another done source (poll / Yes / /spawncache) has won the latch. The Release-false branch between the
                // open-check and the latch has no seam, so it is pinned by the source regex below.
                Assert.IsTrue(run.TryMarkShopDone(owner));
                Assert.IsFalse(ThreadPeddlerShop.Release(run, owner, "poll"), "fixture: the race is lost");
                Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(run, owner, true, sent.Add), "not held, not messaged");
                Assert.AreEqual(0, sent.Count);
            }
            finally
            {
                ThreadPeddlerShop.RequestPlacement = original;
            }
        }

        [TestMethod]
        public void A_group_member_with_an_open_shop_is_held_and_only_their_shop_is_released()
        {
            var run = ActiveGroupRun(Member);
            GivePlan(run);
            ClearWithPiles(run, 3, Owner, Member);
            OpenShop(run, Owner);
            OpenShop(run, Member);
            var sent = new System.Collections.Generic.List<string>();

            Assert.IsTrue(ThreadExitGuard.TryReleaseOpenShop(run, Member, true, sent.Add));
            Assert.IsFalse(run.IsPeddlerShopOpen(Member));
            Assert.IsTrue(run.IsPeddlerShopOpen(Owner), "the other member keeps shopping");
            Assert.AreEqual(1, sent.Count);
        }

        [TestMethod]
        public void The_release_requests_exactly_one_placement_for_that_member()
        {
            var run = OpenSoloShop();
            var owner = run.OwnerGuid;
            var calls = new System.Collections.Generic.List<(ThreadDungeonRun Run, uint Guid)>();
            var original = ThreadPeddlerShop.RequestPlacement;

            try
            {
                ThreadPeddlerShop.RequestPlacement = (r, g) => { calls.Add((r, g)); return CacheRequestOutcome.Queued; };

                Assert.IsTrue(ThreadExitGuard.TryReleaseOpenShop(run, owner, true, _ => { }));
                Assert.IsFalse(ThreadExitGuard.TryReleaseOpenShop(run, owner, true, _ => { }));

                Assert.AreEqual(1, calls.Count, "exactly one placement request");
                Assert.AreSame(run, calls[0].Run);
                Assert.AreEqual(owner, calls[0].Guid);
            }
            finally
            {
                ThreadPeddlerShop.RequestPlacement = original;
            }
        }

        private static string StripLineComments(string text)
            => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", "");

        /// <summary>Source pins on the Player-bound exit sites, which cannot run without a live Player. Mutation-proofed by hand.</summary>
        [TestMethod]
        public void The_exit_sites_hold_on_the_release_before_the_bypass_and_the_arm()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadExitGuard.cs");
            var hold = StripLineComments(PooledLootSourceText.MethodBody(source, "public static bool TryHoldExit(Player player, Action reissue)"));
            var would = StripLineComments(PooledLootSourceText.MethodBody(source, "public static bool WouldHoldExit(Player player)"));
            var core = StripLineComments(PooledLootSourceText.MethodBody(source, "internal static bool TryReleaseOpenShop(ThreadDungeonRun run, uint playerGuid, bool inside, Action<string> send)"));

            var holdMatch = System.Text.RegularExpressions.Regex.Match(hold,
                @"if \(TryReleaseOpenShop\(run, playerGuid, ThreadCachePlacer\.IsOwnerInside\(run, player\),[\s\S]*?\)\)\s*return true;");
            Assert.IsTrue(holdMatch.Success, "TryHoldExit holds when the release wins");
            Assert.IsTrue(holdMatch.Index < hold.IndexOf("ConsumeBypass(", StringComparison.Ordinal), "release first: no bypass consumed");
            Assert.IsTrue(holdMatch.Index < hold.IndexOf("ArmIfSoloOwner(", StringComparison.Ordinal), "a held exit never arms");

            var wouldMatch = System.Text.RegularExpressions.Regex.Match(would,
                @"if \(WouldReleaseShop\(run, guid, ThreadCachePlacer\.IsOwnerInside\(run, player\)\)\)\s*return true;");
            Assert.IsTrue(wouldMatch.Success, "WouldHoldExit predicts the hold");
            Assert.IsTrue(wouldMatch.Index < would.IndexOf("ShouldPromptExit(", StringComparison.Ordinal));
            Assert.IsFalse(would.Contains("TryReleaseOpenShop"), "the prediction has no side effects");

            Assert.IsTrue(System.Text.RegularExpressions.Regex.IsMatch(core, @"if \(!ThreadPeddlerShop\.Release\(run, playerGuid,[^)]*\)\)\s*return false;"),
                "a lost race falls through");
        }
    }
}
