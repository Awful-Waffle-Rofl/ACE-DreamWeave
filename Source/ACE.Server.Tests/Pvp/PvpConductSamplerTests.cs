using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Pvp;
using ACE.Server.Pvp.Battlegrounds;

namespace ACE.Server.Tests.Pvp
{
    /// <summary>
    /// The conduct sampler's pure rules (Docs/Pvp/BATTLEGROUNDS.md "Conduct signals"): the sample clock, last-active (the stamp, the
    /// objective, the pen and teleport freeze, movement counting for nothing), engaged seconds by radius and objective, and King of the Hill
    /// objective presence. Reads no setting and no PropertyManager key.
    /// </summary>
    [TestClass]
    public class PvpConductSamplerTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc);

        private static readonly Guid Match = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

        private static readonly Guid OtherMatch = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

        private static BattlegroundZoneSample At(uint id, double x, double y, double z = 0, bool inInstance = true, bool dead = false, bool teleporting = false, DateTime? stamp = null, Guid? stampMatch = null)
            => new BattlegroundZoneSample(id, 0, null, inInstance, dead, teleporting, x, y, z, stamp, stampMatch ?? (stamp.HasValue ? Match : (Guid?)null));

        private static ConductSeatInput Seat(uint id, int team, BattlegroundZoneSample sample, DateTime lastActive, bool inPen = false, bool onObjective = false)
            => new ConductSeatInput(id, team, inPen, sample, onObjective, lastActive);

        private static ConductSeatOutcome One(DateTime now, int seconds, double radius, params ConductSeatInput[] seats)
            => PvpConductSampler.Step(Match, now, seconds, radius, seats).First();

        // ================= the sample clock =================

        [TestMethod]
        public void DueSeconds_WholeSecondsOnly_RemainderCarries()
        {
            Assert.AreEqual(0, PvpConductSampler.DueSeconds(T0, T0.AddSeconds(0.75), out var a0));
            Assert.AreEqual(T0, a0, "under a second: the anchor stays");

            Assert.AreEqual(1, PvpConductSampler.DueSeconds(T0, T0.AddSeconds(1.25), out var a1));
            Assert.AreEqual(T0.AddSeconds(1), a1, "the 0.25 s remainder carries into the next sample");

            Assert.AreEqual(3, PvpConductSampler.DueSeconds(T0, T0.AddSeconds(3.9), out var a3));
            Assert.AreEqual(T0.AddSeconds(3), a3);
        }

        [TestMethod]
        public void DueSeconds_NoAnchorOrBackwardsClock_StartsAtNowWithNothingDue()
        {
            Assert.AreEqual(0, PvpConductSampler.DueSeconds(null, T0, out var a));
            Assert.AreEqual(T0, a);

            Assert.AreEqual(0, PvpConductSampler.DueSeconds(T0.AddSeconds(10), T0, out var b));
            Assert.AreEqual(T0, b, "a clock that ran backwards re-anchors at now");
        }

        // ================= last active =================

        /// <summary>Movement is never activity: a seat that walks across the map with no stamp keeps its last-active time.</summary>
        [TestMethod]
        public void Movement_DoesNotCount()
        {
            var lastActive = T0;
            var now = T0;

            foreach (var x in new[] { 0.0, 10.0, 40.0, 90.0, 140.0 })
            {
                now = now.AddSeconds(1);
                lastActive = One(now, 1, 30.0, Seat(1, 0, At(1, x, x), lastActive)).LastActiveUtc;
            }

            Assert.AreEqual(T0, lastActive, "five seconds of running about: still idle since T0");
        }

        [TestMethod]
        public void Stamp_OfThisMatch_RaisesLastActive_OfAnotherMatchDoesNot()
        {
            var now = T0.AddSeconds(20);

            Assert.AreEqual(T0.AddSeconds(15), One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0, stamp: T0.AddSeconds(15)), T0)).LastActiveUtc, "positive control");
            Assert.AreEqual(T0, One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0, stamp: T0.AddSeconds(15), stampMatch: OtherMatch), T0)).LastActiveUtc, "an earlier match's stamp never counts");
            Assert.AreEqual(T0.AddSeconds(10), One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0, stamp: T0.AddSeconds(5)), T0.AddSeconds(10))).LastActiveUtc, "an older stamp never lowers it");
        }

        [TestMethod]
        public void Stamp_InTheFuture_IsClampedToNow()
        {
            var now = T0.AddSeconds(20);

            Assert.AreEqual(now, One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0, stamp: now.AddSeconds(3)), T0)).LastActiveUtc);
        }

        [TestMethod]
        public void OnObjective_IsActivityNow_WhenStanding()
        {
            var now = T0.AddSeconds(42);

            Assert.AreEqual(now, One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0), T0, onObjective: true)).LastActiveUtc);
            Assert.AreEqual(T0, One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0), T0)).LastActiveUtc, "control: off the objective nothing moves");
            Assert.AreEqual(T0.AddSeconds(1), One(now, 1, 30.0, Seat(1, 0, At(1, 0, 0, dead: true), T0, onObjective: true)).LastActiveUtc, "a dead player's objective flag counts for nothing: only the 1 s freeze");
        }

        /// <summary>
        /// The pen freeze: while the seat is in the pen cycle the clock is pushed forward by the sampled seconds, so idle time (now - last
        /// active) does not grow; an active seat sampled the same way grows by the same seconds.
        /// </summary>
        [TestMethod]
        public void PenCycle_FreezesIdleTime()
        {
            var penLast = T0;
            var aliveLast = T0;
            var now = T0.AddSeconds(10);

            for (var i = 0; i < 30; i++)
            {
                now = now.AddSeconds(1);
                penLast = One(now, 1, 30.0, Seat(1, 0, null, penLast, inPen: true)).LastActiveUtc;
                aliveLast = One(now, 1, 30.0, Seat(2, 0, At(2, 0, 0), aliveLast)).LastActiveUtc;
            }

            Assert.AreEqual(10.0, (now - penLast).TotalSeconds, 1e-9, "in the pen: idle stays at the 10 s it had on entering");
            Assert.AreEqual(40.0, (now - aliveLast).TotalSeconds, 1e-9, "control: the same 30 samples outside the pen add 30 s of idle");
        }

        [TestMethod]
        public void Teleporting_AndDead_FreezeIdleTime()
        {
            var now = T0.AddSeconds(10);

            Assert.AreEqual(T0.AddSeconds(3), One(now, 3, 30.0, Seat(1, 0, At(1, 0, 0, teleporting: true), T0)).LastActiveUtc, "teleporting: pushed by the 3 s");
            Assert.AreEqual(T0.AddSeconds(3), One(now, 3, 30.0, Seat(1, 0, At(1, 0, 0, dead: true), T0)).LastActiveUtc, "dead: pushed by the 3 s");
            Assert.AreEqual(T0, One(now, 3, 30.0, Seat(1, 0, At(1, 0, 0), T0)).LastActiveUtc, "control: standing, it is not");
        }

        /// <summary>
        /// A stalled sample (120 s due at once) adds at most MaxIdleSecondsPerSample of idle time to a standing seat; a stamp inside the
        /// stall still counts; a normal 1 s sample is untouched.
        /// </summary>
        [TestMethod]
        public void StalledSample_AddsAtMostTheIdleCap()
        {
            var now = T0.AddSeconds(120);

            Assert.AreEqual(now.AddSeconds(-PvpConductSampler.MaxIdleSecondsPerSample), One(now, 120, 30.0, Seat(1, 0, At(1, 0, 0), T0)).LastActiveUtc);
            Assert.AreEqual(now.AddSeconds(-2), One(now, 120, 30.0, Seat(1, 0, At(1, 0, 0, stamp: now.AddSeconds(-2)), T0)).LastActiveUtc, "a later stamp still wins");
            Assert.AreEqual(T0, One(T0.AddSeconds(30), 1, 30.0, Seat(1, 0, At(1, 0, 0), T0)).LastActiveUtc, "control: a 1 s sample moves nothing");
        }

        [TestMethod]
        public void Freeze_NeverRunsAheadOfNow()
        {
            var now = T0.AddSeconds(2);

            Assert.AreEqual(now, One(now, 5, 30.0, Seat(1, 0, null, T0.AddSeconds(1), inPen: true)).LastActiveUtc);
        }

        // ================= engaged =================

        [TestMethod]
        public void Engaged_ByAStandingEnemyWithinTheRadius_Inclusive()
        {
            var now = T0.AddSeconds(5);
            var seats = new[]
            {
                Seat(1, 0, At(1, 0, 0), T0),
                Seat(2, 1, At(2, 30, 0), T0),   // exactly 30 m away: inclusive
                Seat(3, 0, At(3, 100, 100), T0),
            };

            var r = PvpConductSampler.Step(Match, now, 1, 30.0, seats).ToDictionary(o => o.CharacterId);

            Assert.IsTrue(r[1].Engaged, "an enemy 30 m away at radius 30");
            Assert.IsTrue(r[2].Engaged, "and the enemy is engaged with them");
            Assert.IsFalse(r[3].Engaged, "far from every enemy");

            var narrower = PvpConductSampler.Step(Match, now, 1, 29.9, seats).ToDictionary(o => o.CharacterId);
            Assert.IsFalse(narrower[1].Engaged, "29.9 m radius: 30 m is outside");
        }

        [TestMethod]
        public void Engaged_UsesThe3dDistance()
        {
            var seats = new[] { Seat(1, 0, At(1, 0, 0, 0), T0), Seat(2, 1, At(2, 20, 0, 25), T0) };

            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, seats)[0].Engaged, "20 m across and 25 m up is 32 m");
            Assert.IsTrue(PvpConductSampler.Step(Match, T0, 1, 33.0, seats)[0].Engaged);
        }

        [TestMethod]
        public void Teammates_DeadEnemies_PenEnemies_AndOtherInstances_NeverEngage()
        {
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 0, At(2, 1, 0), T0) })[0].Engaged, "a teammate next to you");
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 1, At(2, 1, 0, dead: true), T0) })[0].Engaged, "a dead enemy");
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 1, null, T0, inPen: true) })[0].Engaged, "an enemy in the pen");
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 1, At(2, 1, 0, inInstance: false), T0) })[0].Engaged, "an enemy outside the instance");
            Assert.IsTrue(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 1, At(2, 1, 0), T0) })[0].Engaged, "control: a standing enemy");
        }

        [TestMethod]
        public void Engaged_OnTheObjective_WithNoEnemyNear()
        {
            var r = PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0), T0, onObjective: true) });

            Assert.IsTrue(r[0].Engaged);
        }

        [TestMethod]
        public void NotStanding_IsNeverEngaged()
        {
            var enemy = Seat(9, 1, At(9, 1, 0), T0);

            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, null, T0, inPen: true, onObjective: true), enemy })[0].Engaged, "in the pen");
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, At(1, 0, 0, teleporting: true), T0, onObjective: true), enemy })[0].Engaged, "teleporting");
            Assert.IsFalse(PvpConductSampler.Step(Match, T0, 1, 30.0, new[] { Seat(1, 0, null, T0), enemy })[0].Engaged, "offline (no sample)");
        }

        [TestMethod]
        public void BadRadius_EngagesNobodyByProximity_ButTheObjectiveStillCounts()
        {
            foreach (var bad in new[] { 0.0, -5.0, double.NaN, double.PositiveInfinity })
            {
                var r = PvpConductSampler.Step(Match, T0, 1, bad, new[] { Seat(1, 0, At(1, 0, 0), T0), Seat(2, 1, At(2, 0, 0), T0, onObjective: true) });

                Assert.IsFalse(r[0].Engaged, $"radius {bad}");
                Assert.IsTrue(r[1].Engaged, $"radius {bad}: the objective");
            }
        }

        // ================= objective presence =================

        [TestMethod]
        public void OnKothObjective_IsTheZoneWidenedByTheMargin()
        {
            var zone = new KothZone(100, 100, 0, 6, 3);

            Assert.IsTrue(PvpConductSampler.OnKothObjective(zone, 6.0, At(1, 111, 100)), "11 m out with a 6 m margin on a 6 m zone");
            Assert.IsFalse(PvpConductSampler.OnKothObjective(zone, 6.0, At(1, 113, 100)), "13 m out");
            Assert.IsFalse(PvpConductSampler.OnKothObjective(zone, 0.0, At(1, 111, 100)), "no margin: the bare zone");
            Assert.IsFalse(PvpConductSampler.OnKothObjective(zone, 6.0, At(1, 100, 100, 10)), "above the zone's height tolerance");
            Assert.IsFalse(PvpConductSampler.OnKothObjective(zone, 6.0, At(1, 100, 100, inInstance: false)), "outside the instance");
            Assert.IsFalse(PvpConductSampler.OnKothObjective(zone, 6.0, null));
        }

        [TestMethod]
        public void Step_NullSeats_IsEmpty()
        {
            Assert.AreEqual(0, PvpConductSampler.Step(Match, T0, 1, 30.0, null).Count);
            Assert.AreEqual(0, PvpConductSampler.Step(Match, T0, 1, 30.0, new List<ConductSeatInput>()).Count);
        }
    }
}
