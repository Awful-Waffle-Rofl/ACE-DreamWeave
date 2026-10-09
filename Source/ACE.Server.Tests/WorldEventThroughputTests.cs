using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the measured-throughput boss sizing (TECH-DESIGN C16, BOSS-STANDARD.md section 3,
    /// flag world_events_boss_throughput_scaling_enabled): the accumulator, the sample gate, the clamped
    /// multiplier and the late-arrival term.
    ///
    /// Every case here is a plain object or a pure static - no clock, no Creature, no Player, no landblock
    /// (TECH-DESIGN D6).
    /// </summary>
    [TestClass]
    public class WorldEventThroughputTests
    {
        // The shipped dials (Content/events/axes/bosses.json), so the numbers below are the real ones.
        private const uint Floor = 100000u;
        private const uint Cap = 6400000u;
        private const double Target = 300.0;
        private const double Calibration = 1.0;

        // ---- the accumulator ---------------------------------------------------------------------------

        [TestMethod]
        public void NoteCleared_AccumulatesMaxHealth_AndIgnoresZero()
        {
            var t = new WorldEventThroughput();

            Assert.AreEqual(0.0, t.TotalHealthCleared, 0.0000001);
            Assert.AreEqual(0, t.ClearedCount);

            t.NoteCleared(1200);
            t.NoteCleared(800);

            Assert.AreEqual(2000.0, t.TotalHealthCleared, 0.0000001);
            Assert.AreEqual(2, t.ClearedCount);

            t.NoteCleared(0);

            Assert.AreEqual(2000.0, t.TotalHealthCleared, 0.0000001, "a zero-health death contributes nothing");
            Assert.AreEqual(2, t.ClearedCount, "and is not counted either");
        }

        [TestMethod]
        public void Throughput_IsTotalOverElapsed_AndZeroForANonPositiveElapsed()
        {
            var t = new WorldEventThroughput();

            Assert.AreEqual(0.0, t.Throughput(100), 0.0000001, "nothing cleared is no rate at all");

            t.NoteCleared(300000);

            Assert.AreEqual(1000.0, t.Throughput(300), 0.0000001);

            Assert.AreEqual(0.0, t.Throughput(0), 0.0000001);
            Assert.AreEqual(0.0, t.Throughput(-30), 0.0000001);
            Assert.AreEqual(0.0, t.Throughput(double.NaN), 0.0000001);
        }

        [TestMethod]
        public void HasSample_NeedsBothTheMinimumWindowAndSomethingCleared()
        {
            var t = new WorldEventThroughput();

            Assert.IsFalse(t.HasSample(300, 120), "no deaths, no sample however long the run has been");

            t.NoteCleared(50000);

            Assert.IsFalse(t.HasSample(119.9, 120), "one tick short of the window");
            Assert.IsTrue(t.HasSample(120, 120), "exactly the window is enough");
            Assert.IsTrue(t.HasSample(600, 120));

            Assert.IsFalse(t.HasSample(0, 120));
            Assert.IsFalse(t.HasSample(double.NaN, 120));
            Assert.IsFalse(t.HasSample(600, double.NaN));

            Assert.IsTrue(t.HasSample(1, 0), "a minSampleSeconds of 0 means the first death is enough");
        }

        // ---- the multiplier ----------------------------------------------------------------------------

        [TestMethod]
        public void ResolveBossMult_SizesTheBossToTheTargetKillTime()
        {
            // 10000 HP/s x 300 s = 3,000,000 HP wanted, against a 100,000 floor.
            Assert.AreEqual(30.0, WorldEventThroughput.ResolveBossMult(10000, Calibration, Target, Floor, Cap), 0.0000001);

            // The calibration dial is a straight multiplier on the measured rate.
            Assert.AreEqual(15.0, WorldEventThroughput.ResolveBossMult(10000, 0.5, Target, Floor, Cap), 0.0000001);
            Assert.AreEqual(60.0, WorldEventThroughput.ResolveBossMult(10000, 2.0, Target, Floor, Cap), 0.0000001);

            // So is the target time.
            Assert.AreEqual(15.0, WorldEventThroughput.ResolveBossMult(10000, Calibration, 150, Floor, Cap), 0.0000001);
        }

        [TestMethod]
        public void ResolveBossMult_FloorsAtOne_WhenTheGroupIsSlowerThanTheFloorImplies()
        {
            // 100 HP/s x 300 s = 30,000 HP, well under the 100,000 floor.
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(100, Calibration, Target, Floor, Cap), 0.0000001);

            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(0, Calibration, Target, Floor, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(-5000, Calibration, Target, Floor, Cap), 0.0000001);
        }

        [TestMethod]
        public void ResolveBossMult_CapsAtTheCapOverFloorRatio()
        {
            // 1,000,000 HP/s would want 300,000,000 HP - 3000x the floor, far past the 64x ceiling.
            Assert.AreEqual(64.0, WorldEventThroughput.ResolveBossMult(1000000, Calibration, Target, Floor, Cap), 0.0000001);

            // An overflow to infinity resolves to the ceiling too, never to NaN.
            Assert.AreEqual(64.0, WorldEventThroughput.ResolveBossMult(double.MaxValue, Calibration, Target, Floor, Cap), 0.0000001);
        }

        [TestMethod]
        public void ResolveBossMult_IsOneForEveryInputABossCannotBeSizedFrom()
        {
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(10000, Calibration, Target, 0, Cap), 0.0000001,
                "floorHealth 0 means this boss does not do throughput scaling");

            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(double.NaN, Calibration, Target, Floor, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(10000, double.NaN, Target, Floor, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(10000, Calibration, double.NaN, Floor, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(double.PositiveInfinity, Calibration, Target, Floor, Cap), 0.0000001);

            Assert.AreEqual(1.0, WorldEventThroughput.ResolveBossMult(10000, Calibration, Target, Floor, Floor - 1), 0.0000001,
                "a cap below the floor is treated as the floor - no headroom, so the multiplier is 1");
        }

        [TestMethod]
        public void CapRatio_IsCapOverFloor_AndNeverBelowOne()
        {
            Assert.AreEqual(64.0, WorldEventThroughput.CapRatio(Floor, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.CapRatio(0, Cap), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.CapRatio(Floor, Floor), 0.0000001);
            Assert.AreEqual(1.0, WorldEventThroughput.CapRatio(Floor, 50000), 0.0000001);
        }

        // ---- the late-arrival term ---------------------------------------------------------------------

        [TestMethod]
        public void ScaleForAudience_IsTheCrowdRatio_WithBothCountsFlooredAtOne()
        {
            Assert.AreEqual(60.0, WorldEventThroughput.ScaleForAudience(30, 8, 4), 0.0000001, "twice the crowd, twice the boss");
            Assert.AreEqual(30.0, WorldEventThroughput.ScaleForAudience(30, 4, 4), 0.0000001, "the same crowd changes nothing");
            Assert.AreEqual(15.0, WorldEventThroughput.ScaleForAudience(30, 2, 4), 0.0000001,
                "a shrinking crowd proposes less - it is WorldEvent.RatchetMult that refuses to apply it");

            Assert.AreEqual(7.5, WorldEventThroughput.ScaleForAudience(30, 0, 4), 0.0000001, "countNow floors at 1");
            Assert.AreEqual(30.0, WorldEventThroughput.ScaleForAudience(30, 1, 0), 0.0000001, "countAtSpawn floors at 1");
            Assert.AreEqual(30.0, WorldEventThroughput.ScaleForAudience(30, -5, -5), 0.0000001);

            Assert.IsTrue(double.IsNaN(WorldEventThroughput.ScaleForAudience(double.NaN, 4, 4)),
                "a non-finite multiplier is handed back untouched; RatchetMult discards it");
        }

        // ---- what counts -------------------------------------------------------------------------------

        [TestMethod]
        public void Counts_TakesWaveAndSourceDeaths_AndNeverTheBossOrTheScenery()
        {
            const uint bossGuid = 0x7000_0001u;

            Assert.IsTrue(WorldEventThroughput.Counts(0x7000_0002u, bossGuid, isDecor: false, isNpc: false),
                "a wave, overflow-champion or source death is exactly what the group cleared");

            Assert.IsFalse(WorldEventThroughput.Counts(bossGuid, bossGuid, isDecor: false, isNpc: false),
                "the boss never feeds the rate that sizes a boss");

            Assert.IsFalse(WorldEventThroughput.Counts(0x7000_0003u, bossGuid, isDecor: true, isNpc: false));
            Assert.IsFalse(WorldEventThroughput.Counts(0x7000_0004u, bossGuid, isDecor: false, isNpc: true));
            Assert.IsFalse(WorldEventThroughput.Counts(0, bossGuid, isDecor: false, isNpc: false));

            Assert.IsTrue(WorldEventThroughput.Counts(0x7000_0005u, 0, isDecor: false, isNpc: false),
                "no boss has been adopted yet, so nothing can collide with its guid");
        }
    }
}
