using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.WorldObjects;

using Decision = ACE.Server.WorldObjects.Player.MeleeRepeatDecision;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the auto-repeat melee decision (Player.DecideMeleeRepeat) and the guard a delayed reacquire must
    /// pass (Player.ShouldResumeMeleeRepeat). A mob nudging the player out of sticky range used to end the
    /// whole attack chain; within ReacquireDistance it now reacquires instead, unless the player walked away.
    /// </summary>
    [TestClass]
    public class MeleeRepeatDecisionTests
    {
        private static Decision Decide(float dist, bool visible = true, bool alive = true, bool autoRepeat = true, bool busy = false, bool cancelled = false, bool moved = false)
            => Player.DecideMeleeRepeat(dist, () => visible, alive, autoRepeat, busy, cancelled, moved);

        [TestMethod]
        public void In_melee_range_continues_even_if_not_visible()
        {
            Assert.AreEqual(Decision.Continue, Decide(Player.MeleeDistance, visible: false));
            Assert.AreEqual(Decision.Continue, Decide(0f, visible: false));
        }

        [TestMethod]
        public void Within_sticky_range_and_visible_continues()
        {
            Assert.AreEqual(Decision.Continue, Decide(Player.StickyDistance, visible: true));
        }

        [TestMethod]
        public void Continue_is_unaffected_by_client_movement()
        {
            Assert.AreEqual(Decision.Continue, Decide(0.1f, moved: true));
        }

        [TestMethod]
        public void Nudged_past_melee_range_and_not_visible_reacquires()
        {
            Assert.AreEqual(Decision.Reacquire, Decide(1.0f, visible: false));
        }

        [TestMethod]
        public void Nudged_past_sticky_range_reacquires_up_to_reacquire_distance()
        {
            Assert.AreEqual(Decision.Reacquire, Decide(Player.StickyDistance + 0.01f));
            Assert.AreEqual(Decision.Reacquire, Decide(Player.ReacquireDistance));
        }

        [TestMethod]
        public void Beyond_reacquire_distance_stops_and_stays_inside_charge_fail_distance()
        {
            Assert.AreEqual(Decision.Stop, Decide(Player.ReacquireDistance + 0.01f));
            Assert.IsTrue(Player.ReacquireDistance <= 15.0f, "the charge MoveTo fails beyond FailDistance 15");
            Assert.IsTrue(Player.ReacquireDistance <= Player.RepeatDistance);
        }

        [TestMethod]
        public void Client_moving_stops_instead_of_reacquiring()
        {
            Assert.AreEqual(Decision.Stop, Decide(5f, moved: true));
            Assert.AreEqual(Decision.Stop, Decide(1.0f, visible: false, moved: true));
        }

        [TestMethod]
        public void Any_non_range_failure_stops()
        {
            foreach (var d in new[] { 5f, 0.1f })
            {
                Assert.AreEqual(Decision.Stop, Decide(d, alive: false));
                Assert.AreEqual(Decision.Stop, Decide(d, autoRepeat: false));
                Assert.AreEqual(Decision.Stop, Decide(d, busy: true));
                Assert.AreEqual(Decision.Stop, Decide(d, cancelled: true));
            }
        }

        [TestMethod]
        public void Visibility_is_only_evaluated_between_melee_and_sticky_distance()
        {
            int calls = 0;
            bool Vis() { calls++; return true; }

            Player.DecideMeleeRepeat(0.3f, Vis, true, true, false, false, false);
            Assert.AreEqual(0, calls, "in melee range: no visibility test");

            Player.DecideMeleeRepeat(Player.StickyDistance + 1f, Vis, true, true, false, false, false);
            Assert.AreEqual(0, calls, "beyond sticky range: no visibility test");

            Player.DecideMeleeRepeat(2f, () => { calls++; return true; }, false, true, false, false, false);
            Assert.AreEqual(0, calls, "dead target: no visibility test");

            Player.DecideMeleeRepeat(2f, Vis, true, true, false, false, false);
            Assert.AreEqual(1, calls, "sticky band: tested once");
        }

        private static bool Resume(bool seq = true, bool cancelled = false, bool target = true, bool targetAlive = true, bool melee = true,
            bool busy = false, bool alive = true, bool teleporting = false, bool suicide = false, bool landblock = true, bool moving = false)
            => Player.ShouldResumeMeleeRepeat(seq, cancelled, target, targetAlive, melee, busy, alive, teleporting, suicide, landblock, moving);

        [TestMethod]
        public void Resume_guard_passes_when_everything_is_in_order()
        {
            Assert.IsTrue(Resume());
        }

        [TestMethod]
        public void Resume_guard_blocks_each_failing_condition()
        {
            Assert.IsFalse(Resume(seq: false));
            Assert.IsFalse(Resume(cancelled: true));
            Assert.IsFalse(Resume(target: false));
            Assert.IsFalse(Resume(targetAlive: false));
            Assert.IsFalse(Resume(melee: false));     // /hom recall sets NonCombat
            Assert.IsFalse(Resume(busy: true));       // recall in progress
            Assert.IsFalse(Resume(alive: false));
            Assert.IsFalse(Resume(teleporting: true));
            Assert.IsFalse(Resume(suicide: true));
            Assert.IsFalse(Resume(landblock: false));
            Assert.IsFalse(Resume(moving: true));     // a key pressed during the refill delay
        }
    }
}
