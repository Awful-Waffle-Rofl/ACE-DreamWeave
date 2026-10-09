using System;

using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// TreasureMapHandler.RunDigStep needs a live Player/ActionChain/landblock to drive directly, so the two
    /// code-review fixes to its post-delay action (2026-09-18) are pinned against source text instead, the
    /// same way PooledLootWiringTests pins Creature.Die: what can be checked without a world is the ORDER and
    /// PRESENCE of the fix, not the runtime behaviour it produces.
    /// </summary>
    [TestClass]
    public class TreasureMapHandlerTests
    {
        private static string RunDigStepBody()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlTreasure/TreasureMapHandler.cs");

            return PooledLootSourceText.MethodBody(src, "private static void RunDigStep(WorldObject wo, Player player)");
        }

        [TestMethod]
        public void The_Ready_broadcast_re_reads_the_players_CURRENT_stance_not_the_stance_captured_before_the_delay()
        {
            // HandleActionChangeCombatMode carries no IsBusy gate, so a player who switches stance mid-dig
            // must snap back to Ready in the stance they are actually in now, not the one captured before the
            // animation delay.
            var body = RunDigStepBody();

            StringAssert.Contains(body, "new Motion(player.CurrentMotionState.Stance, MotionCommand.Ready)");
            Assert.IsFalse(body.Contains("new Motion(currentStance, MotionCommand.Ready)"),
                "the post-delay Ready broadcast must not reuse the stance captured before the animation delay");
        }

        [TestMethod]
        public void The_pickup_motion_still_starts_in_the_stance_captured_before_the_delay()
        {
            // Only the RETURN to Ready needs the live stance; the Pickup motion that starts the animation is
            // correctly anchored to whatever stance the player was in when the dig step began.
            var body = RunDigStepBody();

            StringAssert.Contains(body, "new Motion(currentStance, motion)");
        }

        [TestMethod]
        public void The_Ready_broadcast_use_done_event_and_IsBusy_clear_are_in_a_finally_block()
        {
            // A throw out of FinishDig (payout, boss spawn, encounter open) must not strand the player
            // mid-Pickup with IsBusy stuck true - that would reintroduce the stuck-pose bug this method was
            // fixed for, just through an exception path instead of a missing broadcast.
            var body = RunDigStepBody();

            var tryIndex = body.IndexOf("try", StringComparison.Ordinal);
            Assert.IsTrue(tryIndex >= 0, "RunDigStep's post-delay action must wrap FinishDig in a try block");

            var catchIndex = body.IndexOf("catch (Exception", tryIndex, StringComparison.Ordinal);

            // Searched forward from tryIndex, not from the start of the body, so the leading doc comment's
            // own prose explanation of the fix (which legitimately contains the word "finally") can never be
            // mistaken for the keyword it is describing - the doc comment sits entirely before the real "try".
            var finallyIndex = body.IndexOf("finally", tryIndex, StringComparison.Ordinal);
            var readyIndex = body.IndexOf("new Motion(player.CurrentMotionState.Stance, MotionCommand.Ready)", StringComparison.Ordinal);
            var useDoneIndex = body.IndexOf("SendUseDoneEvent()", StringComparison.Ordinal);
            var isBusyIndex = body.IndexOf("IsBusy = false", StringComparison.Ordinal);

            Assert.IsTrue(catchIndex > tryIndex, "the try must be followed by a catch");
            Assert.IsTrue(finallyIndex > catchIndex, "the catch must be followed by a finally");

            Assert.IsTrue(readyIndex > finallyIndex, "the Ready broadcast must be inside the finally block");
            Assert.IsTrue(useDoneIndex > finallyIndex, "SendUseDoneEvent must be inside the finally block");
            Assert.IsTrue(isBusyIndex > finallyIndex, "IsBusy = false must be inside the finally block");
        }

        [TestMethod]
        public void A_dig_step_exception_is_logged_rather_than_swallowed_silently()
        {
            var body = RunDigStepBody();

            var catchIndex = body.IndexOf("catch (Exception", StringComparison.Ordinal);
            var logIndex = body.IndexOf("log.Error(", StringComparison.Ordinal);

            Assert.IsTrue(catchIndex >= 0 && logIndex > catchIndex,
                "the catch around FinishDig must log the exception rather than swallowing it");
        }
    }
}
