using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    [TestClass]
    public class ThreadGuideRulesTests
    {
        private const uint Owner = 0x50000001u;

        private static DungeonGemSpec Plain() => new DungeonGemSpec("any", 50, 3, "any", 0, new (string, double)[0], 0, 0);

        private static DungeonGemSpec Guide(int rung = 50, int serial = 2, uint owner = Owner) => Plain().WithGuide(new GuideTag(rung, serial, owner));

        // ---- IsGuide / IsCurrent ---------------------------------------------------------------------

        [TestMethod]
        public void IsGuide_is_true_only_for_a_tagged_spec()
        {
            Assert.IsTrue(ThreadGuideRules.IsGuide(Guide()));
            Assert.IsFalse(ThreadGuideRules.IsGuide(Plain()));
            Assert.IsFalse(ThreadGuideRules.IsGuide(null));
        }

        [TestMethod]
        public void IsCurrent_requires_the_owner_and_the_current_serial()
        {
            Assert.IsTrue(ThreadGuideRules.IsCurrent(Guide(serial: 2), Owner, 2));
        }

        [TestMethod]
        public void IsCurrent_is_false_for_another_players_item()
        {
            Assert.IsFalse(ThreadGuideRules.IsCurrent(Guide(serial: 2), Owner + 1, 2));
        }

        [TestMethod]
        public void IsCurrent_is_false_for_a_superseded_serial()
        {
            Assert.IsFalse(ThreadGuideRules.IsCurrent(Guide(serial: 2), Owner, 3), "an older issue");
            Assert.IsFalse(ThreadGuideRules.IsCurrent(Guide(serial: 2), Owner, 1), "a serial from the future is not current either");
        }

        [TestMethod]
        public void IsCurrent_is_false_for_a_spec_with_no_guide_tag()
        {
            Assert.IsFalse(ThreadGuideRules.IsCurrent(Plain(), Owner, 2));
            Assert.IsFalse(ThreadGuideRules.IsCurrent(null, Owner, 2));
        }

        // ---- DecideGrant -----------------------------------------------------------------------------

        private static void AssertRefused(GuideRefusal expected, GuideGrantDecision decision)
        {
            Assert.IsFalse(decision.Granted, decision.ToString());
            Assert.AreEqual(expected, decision.Reason, decision.ToString());
            Assert.AreEqual(0, decision.Rung);
        }

        private static void AssertGranted(int expectedRung, GuideGrantDecision decision)
        {
            Assert.IsTrue(decision.Granted, decision.ToString());
            Assert.AreEqual(GuideRefusal.None, decision.Reason);
            Assert.AreEqual(expectedRung, decision.Rung);
        }

        [TestMethod]
        public void A_fresh_player_at_the_minimum_level_is_granted_the_first_rung()
        {
            Assert.AreEqual(50, ThreadGuideRules.MinPlayerLevel);
            AssertGranted(50, ThreadGuideRules.DecideGrant(true, 50, 0, false, false));
        }

        [TestMethod]
        public void After_a_win_the_next_rung_is_granted()
        {
            AssertGranted(75, ThreadGuideRules.DecideGrant(true, 120, 50, false, false));
            AssertGranted(375, ThreadGuideRules.DecideGrant(true, 275, 350, false, false));
        }

        /// <summary>
        /// A failed run leaves highestWon where it was, so once the run is over and nothing is outstanding the
        /// same rung is issued again. A destroyed item is the same inputs, so the same answer.
        /// </summary>
        [TestMethod]
        public void After_a_fail_or_a_destroyed_item_the_same_rung_is_reissued()
        {
            AssertGranted(125, ThreadGuideRules.DecideGrant(true, 130, 100, false, false));
        }

        [TestMethod]
        public void Disabled_refuses()
        {
            AssertRefused(GuideRefusal.Disabled, ThreadGuideRules.DecideGrant(false, 200, 0, false, false));
        }

        [TestMethod]
        public void Below_the_minimum_level_refuses()
        {
            AssertRefused(GuideRefusal.BelowMinimumLevel, ThreadGuideRules.DecideGrant(true, 49, 0, false, false));
        }

        [TestMethod]
        public void A_completed_ladder_refuses()
        {
            AssertRefused(GuideRefusal.LadderComplete, ThreadGuideRules.DecideGrant(true, 300, 375, false, false));
        }

        [TestMethod]
        public void An_outstanding_guide_item_refuses()
        {
            AssertRefused(GuideRefusal.HoldsGuideItem, ThreadGuideRules.DecideGrant(true, 100, 50, true, false));
        }

        [TestMethod]
        public void A_live_guide_run_refuses()
        {
            AssertRefused(GuideRefusal.GuideRunInProgress, ThreadGuideRules.DecideGrant(true, 100, 50, false, true));
        }

        /// <summary>The refusals are ordered, so the reason reported is the most fundamental one.</summary>
        [TestMethod]
        public void Refusals_are_checked_in_order()
        {
            AssertRefused(GuideRefusal.Disabled, ThreadGuideRules.DecideGrant(false, 10, 375, true, true));
            AssertRefused(GuideRefusal.BelowMinimumLevel, ThreadGuideRules.DecideGrant(true, 10, 375, true, true));
            AssertRefused(GuideRefusal.LadderComplete, ThreadGuideRules.DecideGrant(true, 300, 375, true, true));
            AssertRefused(GuideRefusal.HoldsGuideItem, ThreadGuideRules.DecideGrant(true, 300, 100, true, true));
        }
    }
}
