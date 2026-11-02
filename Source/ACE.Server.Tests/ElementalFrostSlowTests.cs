using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency Frost (Archmage T3): Creature.TryApplyClassAbilityFrostSlow, which writes the SAME
    /// movement-slow slot Pinning Shot's post-pin slow uses but needs no pin. Covers the merge rule (stronger
    /// slow wins, later end), the new-landing return value, the refusals, and that Pinning Shot's own entry
    /// point still refuses without a pin.
    ///
    /// Reads no PropertyManager key: every factor and duration is passed explicitly, so this class needs no
    /// seeding and is safe to run alone or in any order.
    /// </summary>
    [TestClass]
    public class ElementalFrostSlowTests
    {
        /// <summary>
        /// A live creature in State.Return. Return is the state in which neither the pin's cancel nor the Frost
        /// re-issue touches the physics layer, which a dat-free fixture does not have (the same reason
        /// ClassAbilityReflectAndPinTests.PinnableTarget uses it).
        /// </summary>
        private static Creature Target()
        {
            var target = TestCreatures.CreateDefender(maxHealth: 50);

            target.MonsterState = Creature.State.Return;

            Assert.IsFalse(target.IsDead, "the fixture target must be alive, or a refusal proves nothing");

            return target;
        }

        [TestMethod]
        public void Frost_LandsWithNoPin_WhilePinningShotsSlowStillRefusesWithoutOne()
        {
            var target = Target();

            Assert.IsFalse(target.IsClassAbilityPinned, "control: no pin");

            Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f),
                "Pinning Shot's entry point keeps its pin-only gate");

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlow(10.0, 0.6f), "Frost needs no pin");

            Assert.AreEqual(0.6f, target.CurrentMovementSlowFactor, 1e-6f, "Frost wrote the shared slot");
            Assert.AreEqual(0.6f, target.MovementSpeedFactor, 1e-6f, "and the emit sites read it");
        }

        [TestMethod]
        public void NewLandingTrue_RefreshFalse_AndEndIsTheLaterOfTheTwo()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.6f, 100.0, out var landed, out var changed));
            Assert.IsTrue(landed);
            Assert.IsTrue(changed, "1.0 -> 0.6 is a speed change");
            Assert.AreEqual(110.0, target.ClassAbilityMoveSlowUntil, 1e-9);

            // same factor, later: a refresh that extends the window, with no speed change (no move re-issue)
            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.6f, 105.0, out landed, out changed));
            Assert.IsFalse(landed, "a refresh is not a new landing");
            Assert.IsFalse(changed, "same factor: nothing to re-issue");
            Assert.AreEqual(115.0, target.ClassAbilityMoveSlowUntil, 1e-9);

            // stronger but shorter: the factor upgrades and the window is NOT shortened
            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(2.0, 0.4f, 106.0, out landed, out changed));
            Assert.IsFalse(landed);
            Assert.IsTrue(changed);
            Assert.AreEqual(0.4f, target.GetMovementSlowFactorAt(107.0), 1e-6f);
            Assert.AreEqual(115.0, target.ClassAbilityMoveSlowUntil, 1e-9, "the later end time is kept");
        }

        [TestMethod]
        public void WeakerSlow_WhileOneIsRunning_IsRefusedAndChangesNothing()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.4f, 100.0, out _, out _));

            Assert.IsFalse(target.TryApplyClassAbilityFrostSlowState(30.0, 0.8f, 105.0, out var landed, out var changed),
                "a weaker slow never displaces a running one");
            Assert.IsFalse(landed);
            Assert.IsFalse(changed);

            Assert.AreEqual(0.4f, target.GetMovementSlowFactorAt(105.0), 1e-6f, "factor untouched");
            Assert.AreEqual(110.0, target.ClassAbilityMoveSlowUntil, 1e-9, "window not extended by a refused slow");
        }

        [TestMethod]
        public void EqualSlow_WhileOneIsRunning_IsAcceptedAsARefresh()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.6f, 100.0, out _, out _));
            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.6f, 108.0, out var landed, out _));

            Assert.IsFalse(landed);
            Assert.AreEqual(118.0, target.ClassAbilityMoveSlowUntil, 1e-9);
        }

        [TestMethod]
        public void AfterTheWindowCloses_AWeakerSlowLandsFresh()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.4f, 100.0, out _, out _));
            Assert.AreEqual(1.0f, target.GetMovementSlowFactorAt(110.0), 0.0f, "lapsed at its end");

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.8f, 111.0, out var landed, out _));
            Assert.IsTrue(landed, "nothing was running, so this is a new landing");
            Assert.AreEqual(0.8f, target.GetMovementSlowFactorAt(112.0), 1e-6f);
            Assert.AreEqual(121.0, target.ClassAbilityMoveSlowUntil, 1e-9, "a lapsed window is not carried forward");
        }

        [TestMethod]
        public void SharesTheSlotWithPinningShot_TheStrongerSlowWins()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 8.0), "the fixture target must be pinnable");
            Assert.IsTrue(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f), "Pinning Shot's slow armed");

            var pinSlowEnd = target.ClassAbilityMoveSlowUntil;

            Assert.IsFalse(target.TryApplyClassAbilityFrostSlow(10.0, 0.6f),
                "a weaker Frost slow must not displace Pinning Shot's stronger one");
            Assert.AreEqual(0.2f, target.CurrentMovementSlowFactor, 1e-6f);
            Assert.AreEqual(pinSlowEnd, target.ClassAbilityMoveSlowUntil, 1e-9);

            Assert.IsFalse(target.TryApplyClassAbilityFrostSlow(1.0, 0.1f), "a stronger Frost slow refreshes, not a new landing");
            Assert.AreEqual(0.1f, target.CurrentMovementSlowFactor, 1e-6f);
            Assert.AreEqual(pinSlowEnd, target.ClassAbilityMoveSlowUntil, 1e-9, "and never shortens the pin's window");
        }

        [TestMethod]
        public void PinSlow_DoesNotOverwriteAStrongerFrostSlow()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlow(60.0, 0.1f), "a strong Frost slow is running");
            var frostEnd = target.ClassAbilityMoveSlowUntil;

            Assert.IsTrue(target.TryApplyClassAbilityPin(5.0, 8.0), "the fixture target must be pinnable");
            Assert.IsFalse(target.TryApplyClassAbilityMoveSlow(10.0, 0.2f), "a weaker pin slow is refused under a stronger slow");

            Assert.AreEqual(0.1f, target.CurrentMovementSlowFactor, 1e-6f, "the weaker pin slow must not weaken the Frost slow");
            Assert.AreEqual(frostEnd, target.ClassAbilityMoveSlowUntil, 1e-9, "the Frost end time is left alone");
        }

        /// <summary>
        /// Code-review fix: a weaker slow must not STRETCH a stronger one. The pin's window here (115) ends after
        /// the running Frost slow (110); taking the later end would keep the strong 0.1 slow alive 5 s longer
        /// than Frost ever applied it.
        /// </summary>
        [TestMethod]
        public void PinSlow_WeakerThanTheRunningSlow_LeavesItsFactorAndEndTimeAlone()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.1f, 100.0, out _, out _));   // ends 110

            target.ClassAbilityPinnedUntil = 105.0;
            Assert.IsFalse(target.ApplyPinSlowState(10.0, 0.2f, 101.0), "refused");   // pin window would end 115

            Assert.AreEqual(0.1f, target.GetMovementSlowFactorAt(101.0), 1e-6f, "stronger factor kept");
            Assert.AreEqual(110.0, target.ClassAbilityMoveSlowUntil, 1e-9, "a weaker slow must not stretch the stronger one");
        }

        [TestMethod]
        public void FrostSlow_WeakerThanTheRunningPinSlow_LeavesItsFactorAndEndTimeAlone()
        {
            var target = Target();

            target.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(target.ApplyPinSlowState(5.0, 0.2f, 101.0));   // ends 110

            Assert.IsFalse(target.TryApplyClassAbilityFrostSlowState(30.0, 0.6f, 102.0, out var landed, out var changed),
                "a weaker Frost slow is refused under the stronger pin slow");
            Assert.IsFalse(landed);
            Assert.IsFalse(changed);

            Assert.AreEqual(0.2f, target.GetMovementSlowFactorAt(102.0), 1e-6f, "stronger factor kept");
            Assert.AreEqual(110.0, target.ClassAbilityMoveSlowUntil, 1e-9, "and not stretched to Frost's 132");
        }

        [TestMethod]
        public void PinSlow_EqualToTheRunningSlow_TakesTheLaterEnd()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(10.0, 0.2f, 100.0, out _, out _));   // ends 110

            target.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(target.ApplyPinSlowState(10.0, 0.2f, 101.0));

            Assert.AreEqual(115.0, target.ClassAbilityMoveSlowUntil, 1e-9, "equal strength refreshes to the later end");
        }

        [TestMethod]
        public void PinSlow_StrongerThanARunningFrostSlow_Replaces_TheFactor()
        {
            var target = Target();

            Assert.IsTrue(target.TryApplyClassAbilityFrostSlowState(30.0, 0.6f, 100.0, out _, out _));   // ends 130

            target.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(target.ApplyPinSlowState(10.0, 0.2f, 101.0));   // pin window ends 115

            Assert.AreEqual(0.2f, target.GetMovementSlowFactorAt(101.0), 1e-6f, "the stronger pin slow wins");
            Assert.AreEqual(130.0, target.ClassAbilityMoveSlowUntil, 1e-9, "and Frost's later end is not shortened");
        }

        [TestMethod]
        public void PinSlow_WithNothingRunning_WritesItsOwnFactorAndPinDerivedWindow()
        {
            var target = Target();

            target.ClassAbilityPinnedUntil = 105.0;
            Assert.IsTrue(target.ApplyPinSlowState(10.0, 0.2f, 101.0));

            Assert.AreEqual(0.2f, target.ClassAbilityMoveSlowFactor, 1e-6f);
            Assert.AreEqual(115.0, target.ClassAbilityMoveSlowUntil, 1e-9, "pin end + slow seconds, as before");
        }

        [TestMethod]
        public void Refusals_DurationFactorDeadAndIneligible()
        {
            foreach (var bad in new[] { 0.0f, -0.5f, 1.0f, 1.5f })
            {
                var target = Target();

                Assert.IsFalse(target.TryApplyClassAbilityFrostSlow(10.0, bad), $"factor {bad} is outside (0.0, 1.0)");
                Assert.AreEqual(0.0, target.ClassAbilityMoveSlowUntil, 0.0, $"factor {bad} armed nothing");
            }

            var noDuration = Target();
            Assert.IsFalse(noDuration.TryApplyClassAbilityFrostSlow(0.0, 0.6f));
            Assert.IsFalse(noDuration.TryApplyClassAbilityFrostSlow(-1.0, 0.6f));
            Assert.AreEqual(0.0, noDuration.ClassAbilityMoveSlowUntil, 0.0);

            var dead = Target();
            dead.Health.Current = 0;
            Assert.IsTrue(dead.IsDead, "control: the fixture is dead");
            Assert.IsFalse(dead.TryApplyClassAbilityFrostSlow(10.0, 0.6f));
            Assert.AreEqual(0.0, dead.ClassAbilityMoveSlowUntil, 0.0);

            var piece = ElementalProficiencyTestFixtures.LiveGamePiece();
            Assert.IsFalse(piece.TryApplyClassAbilityFrostSlow(10.0, 0.6f), "IsEligibleTarget refuses a GamePiece");
            Assert.AreEqual(1.0f, piece.CurrentMovementSlowFactor, 0.0f);
        }

        [TestMethod]
        public void PublicEntryPoint_UsesTheRunningTimeClock()
        {
            var target = Target();

            var before = Timers.RunningTime;
            Assert.IsTrue(target.TryApplyClassAbilityFrostSlow(10.0, 0.6f));
            var after = Timers.RunningTime;

            Assert.IsTrue(target.ClassAbilityMoveSlowUntil >= before + 10.0 - 1e-9 && target.ClassAbilityMoveSlowUntil <= after + 10.0 + 1e-9,
                "the window is measured on Timers.RunningTime, the clock CurrentMovementSlowFactor and Monster_Tick read");
        }
    }
}
