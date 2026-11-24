using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Avatars.Gauntlet;

namespace ACE.Server.Tests.Avatars.Gauntlet
{
    [TestClass]
    public class GauntletScoreTests
    {
        private static GauntletSpawnedRow Row(double max, double dealt, bool alive, string role = "trash") => new(role, max, dealt, alive);

        // ---- Centi ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void Score_ClearBoundary_NoPartial()
        {
            // Not timed out mid-wave: the wave count stands alone, whatever the fraction says.
            Assert.AreEqual(500L, GauntletScore.Centi(5, 1.0, timedOutMidWave: false));
            Assert.AreEqual(500L, GauntletScore.Centi(5, 0.73, timedOutMidWave: false));
            Assert.AreEqual(0L, GauntletScore.Centi(0, 0.99, timedOutMidWave: false));
            Assert.AreEqual(1300L, GauntletScore.Centi(13, 0.0, timedOutMidWave: false));
        }

        [TestMethod]
        public void Score_PartialCappedAt99()
        {
            // A full fraction on a timeout would be 100; it must stay 99 so a partial never ties a full clear.
            Assert.AreEqual(1399L, GauntletScore.Centi(13, 1.0, timedOutMidWave: true));
            Assert.AreEqual(1399L, GauntletScore.Centi(13, 0.999, timedOutMidWave: true));
            Assert.AreEqual(1399L, GauntletScore.Centi(13, 5.0, timedOutMidWave: true), "a fraction above 1 is clamped");
            Assert.AreEqual(1347L, GauntletScore.Centi(13, 0.47, timedOutMidWave: true));
            Assert.AreEqual(1300L, GauntletScore.Centi(13, 0.0, timedOutMidWave: true));
            Assert.AreEqual(1300L, GauntletScore.Centi(13, -3.0, timedOutMidWave: true), "a negative fraction is clamped");
            Assert.AreEqual(1300L, GauntletScore.Centi(13, double.NaN, timedOutMidWave: true));
            Assert.AreEqual(99L, GauntletScore.Centi(0, 1.0, timedOutMidWave: true), "wave 1 timing out with everything but dead still scores below 1.00");
        }

        [TestMethod]
        public void Score_PartialIsFlooredNotRounded()
        {
            Assert.AreEqual(1300L + 56, GauntletScore.Centi(13, 0.569, timedOutMidWave: true));
            Assert.IsTrue(GauntletScore.Centi(13, 0.9999, timedOutMidWave: true) < GauntletScore.Centi(14, 0.0, timedOutMidWave: false));
        }

        // ---- Fraction ---------------------------------------------------------------------------------------------

        [TestMethod]
        public void Fraction_DeadCountsFull_OverkillClamped()
        {
            // A dead creature counts its whole spawn max whatever its damage history totals; a living one counts its damage
            // clamped to its own max, so a last-hit overkill cannot push the wave past 100%.
            var f = GauntletScore.Fraction(new[]
            {
                Row(1000, 0, alive: false),       // dead: 1000 of 1000 despite 0 recorded damage
                Row(2000, 500, alive: true),      // 500 of 2000
                Row(3000, 99999, alive: true),    // overkill: 3000 of 3000
            });

            Assert.AreEqual((1000.0 + 500 + 3000) / 6000, f, 1e-12);
            Assert.AreEqual(1.0, GauntletScore.Fraction(new[] { Row(10, 50, alive: true) }), "never above 1");
            Assert.AreEqual(0.0, GauntletScore.Fraction(new[] { Row(10, -5, alive: true) }), "negative damage counts as none");
        }

        [TestMethod]
        public void Fraction_EmptyOrZeroMax_IsZero()
        {
            Assert.AreEqual(0.0, GauntletScore.Fraction(new List<GauntletSpawnedRow>()));
            Assert.AreEqual(0.0, GauntletScore.Fraction(null));
            Assert.AreEqual(0.0, GauntletScore.Fraction(new[] { Row(0, 5, alive: true), Row(0, 0, alive: false) }), "a zero-max row is skipped");
        }

        [TestMethod]
        public void Fraction_UnspawnedUsesRoleMean()
        {
            // Two trash spawned (max 1000 dead, max 3000 untouched): the trash mean is 2000. Two more trash never spawned, so
            // they add 4000 to the denominator and 0 to the numerator.
            var f = GauntletScore.Fraction(
                new[] { Row(1000, 0, alive: false, "trash"), Row(3000, 0, alive: true, "trash") },
                new[] { new GauntletUnspawned("trash", 2, FallbackMax: 99999) });

            Assert.AreEqual(1000.0 / (4000 + 4000), f, 1e-12);
        }

        [TestMethod]
        public void Fraction_UnspawnedWithNoSpawnedOfTheRole_UsesTheFallback()
        {
            // The boss role has no spawned member, so its estimate is the plan's own figure; the trash mean must not leak into it.
            var f = GauntletScore.Fraction(
                new[] { Row(1000, 1000, alive: true, "trash") },
                new[] { new GauntletUnspawned("boss", 1, FallbackMax: 9000) });

            Assert.AreEqual(1000.0 / (1000 + 9000), f, 1e-12);
        }

        [TestMethod]
        public void Fraction_UnspawnedOnly_IsZero()
        {
            Assert.AreEqual(0.0, GauntletScore.Fraction(
                new List<GauntletSpawnedRow>(),
                new[] { new GauntletUnspawned("trash", 5, FallbackMax: 1000) }));
        }

        [TestMethod]
        public void Fraction_UnspawnedIgnoresNonPositiveCounts_AndBadFallback()
        {
            var spawned = new[] { Row(1000, 500, alive: true) };
            var plain = GauntletScore.Fraction(spawned);
            Assert.AreEqual(plain, GauntletScore.Fraction(spawned, new[] { new GauntletUnspawned("boss", 0, 5000) }));
            Assert.AreEqual(plain, GauntletScore.Fraction(spawned, new[] { new GauntletUnspawned("boss", -2, 5000) }));
            Assert.AreEqual(plain, GauntletScore.Fraction(spawned, new[] { new GauntletUnspawned("boss", 2, double.NaN) }));
            Assert.AreEqual(plain, GauntletScore.Fraction(spawned, new[] { new GauntletUnspawned("boss", 2, -1) }));
        }

        /// <summary>
        /// GOLDEN values. Each expected figure below was computed by running origin/master's
        /// Player.GetLiveWaveFraction body (Player_WaveChallenge.cs:708-748) over the same (max, dealt, alive) rows, with the
        /// arithmetic transcribed line for line into a scratch script and the result printed with full round-trip precision.
        /// The new Fraction, called with no unspawned entries, must equal those figures exactly (== on doubles). They are not
        /// derived from GauntletScore itself, so a rule change in Fraction cannot move the expectation with it.
        /// </summary>
        [TestMethod]
        public void Fraction_MatchesProvingGroundsForSpawnedOnlyWave()
        {
            // A: dead, partial, overkill, negative damage, a zero-max row (skipped), an exactly-full survivor.
            Assert.AreEqual(0.3557775490635913, GauntletScore.Fraction(new[]
            {
                Row(1234567, 0, alive: false),
                Row(7654321, 2500000.5, alive: true),
                Row(333333, 999999.0, alive: true),
                Row(4000000, -50.0, alive: true),
                Row(0, 100.0, alive: true),
                Row(987654, 987654.0, alive: true),
            }));

            // B: fractional damage, an untouched survivor, a dead one, a near-full one.
            Assert.AreEqual(0.5811501338536584, GauntletScore.Fraction(new[]
            {
                Row(1000000, 333333.333, alive: true),
                Row(2000000, 1999999.75, alive: true),
                Row(3000000, 0.0, alive: true),
                Row(500000, 0.0, alive: false),
                Row(750001, 123456.789, alive: true),
                Row(2999999, 2999999.0, alive: true),
            }));

            // C: values at the Gauntlet health clamp (2e9), where the sum leaves the 32-bit range.
            Assert.AreEqual(0.5833353910555559, GauntletScore.Fraction(new[]
            {
                Row(2000000000, 1500000000.25, alive: true),
                Row(2000000000, 2000000000.0, alive: false),
                Row(1999999999, 12345.5, alive: true),
            }));

            // D: small integers.
            Assert.AreEqual(0.2608695652173913, GauntletScore.Fraction(new[]
            {
                Row(5, 1.0, alive: true),
                Row(7, 2.0, alive: true),
                Row(11, 3.0, alive: true),
            }));
        }

        [TestMethod]
        public void Fraction_RoleLabelsDoNotChangeASpawnedOnlyWave()
        {
            var plain = new[] { Row(1000, 100, alive: true, "trash"), Row(8000, 4000, alive: true, "boss") };
            var f = GauntletScore.Fraction(plain);
            Assert.AreEqual(4100.0 / 9000, f, 1e-12);
        }

        // ---- IsNewRecord ------------------------------------------------------------------------------------------

        [TestMethod]
        public void NewRecord_TieDoesNotAnnounce()
        {
            Assert.IsFalse(GauntletScore.IsNewRecord(1347, snapshotScore: 1347), "a tie is not a record");
            Assert.IsFalse(GauntletScore.IsNewRecord(1300, snapshotScore: 1347));
            Assert.IsTrue(GauntletScore.IsNewRecord(1348, snapshotScore: 1347));
            Assert.IsTrue(GauntletScore.IsNewRecord(100, snapshotScore: 0), "the first ever score is a record");
            Assert.IsFalse(GauntletScore.IsNewRecord(0, snapshotScore: 0), "a run that cleared and dealt nothing is never announced");
            Assert.IsFalse(GauntletScore.IsNewRecord(-5, snapshotScore: -10));
        }
    }
}
