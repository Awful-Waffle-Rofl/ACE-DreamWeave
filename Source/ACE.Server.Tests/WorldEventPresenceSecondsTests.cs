using System;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventPresence.TryGetPresentSeconds"/> with a hand-driven clock. No
    /// PropertyManager read.
    /// </summary>
    [TestClass]
    public class WorldEventPresenceSecondsTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);

        private const uint A = 1;
        private const uint B = 2;
        private const uint Never = 99;

        private static PresenceSample In(uint g) => new PresenceSample(g, "p" + g, true);
        private static PresenceSample Out(uint g) => new PresenceSample(g, "p" + g, false);

        private static double Seconds(WorldEventPresence p, uint g)
        {
            Assert.IsTrue(p.TryGetPresentSeconds(g, out var s), $"guid {g} is tracked");
            return s;
        }

        [TestMethod]
        public void Accrual_MatchesTheRawFractionTimesTotal()
        {
            // snapPct 0 and a long window keep the snap rule out of the comparison for the partial player.
            var p = new WorldEventPresence(graceSeconds: 0, snapPct: 0);

            p.Begin(T0);

            var t = T0;

            for (var i = 1; i <= 60; i++)
            {
                t = T0.AddSeconds(i * 2);

                // A is inside throughout; B only on every third tick.
                p.Sample(t, new[] { In(A), i % 3 == 0 ? In(B) : Out(B) });
            }

            var total = p.TotalSeconds;

            Assert.AreEqual(120.0, total, 1e-9);
            Assert.AreEqual(120.0, Seconds(p, A), 1e-9);
            Assert.AreEqual(40.0, Seconds(p, B), 1e-9);

            Assert.IsTrue(p.TryGetFraction(A, out var fa));
            Assert.IsTrue(p.TryGetFraction(B, out var fb));

            Assert.AreEqual(fa * total, Seconds(p, A), 1e-9);
            Assert.AreEqual(fb * total, Seconds(p, B), 1e-9);
        }

        [TestMethod]
        public void StalledTick_IsClampedLikeTheTotal()
        {
            var p = new WorldEventPresence(graceSeconds: 0, snapPct: 0);

            p.Begin(T0);
            p.Sample(T0.AddSeconds(60), new[] { In(A) });

            Assert.AreEqual(WorldEventPresence.MaxTickSeconds, Seconds(p, A), 1e-9);
            Assert.AreEqual(p.TotalSeconds, Seconds(p, A), 1e-9);
        }

        [TestMethod]
        public void UnknownGuid_IsFalse_AndSampledOutsideOnlyIsTrueWithZero()
        {
            var p = new WorldEventPresence();

            Assert.IsFalse(p.TryGetPresentSeconds(Never, out var before), "never begun");
            Assert.AreEqual(0.0, before);

            p.Begin(T0);
            p.Sample(T0.AddSeconds(2), new[] { Out(B) });

            Assert.IsFalse(p.TryGetPresentSeconds(Never, out var s), "never sampled");
            Assert.AreEqual(0.0, s);

            Assert.AreEqual(0.0, Seconds(p, B), "sampled outside only: tracked with 0");
        }

        [TestMethod]
        public void AfterClose_TheValueIsFrozen()
        {
            var p = new WorldEventPresence(graceSeconds: 0, snapPct: 0);

            p.Begin(T0);
            p.Sample(T0.AddSeconds(3), new[] { In(A) });
            p.Sample(T0.AddSeconds(6), new[] { In(A) });

            var atClose = Seconds(p, A);

            p.Close(T0.AddSeconds(6));
            p.Sample(T0.AddSeconds(9), new[] { In(A) });
            p.Sample(T0.AddSeconds(12), new[] { In(A) });

            Assert.AreEqual(6.0, atClose, 1e-9);
            Assert.AreEqual(atClose, Seconds(p, A), "no accrual after Close");
            Assert.AreEqual(atClose, Seconds(p, A), "repeat reads are stable");
        }

        [TestMethod]
        public void DeathGrace_CreditsOutsideSeconds_LikeTheFraction()
        {
            var p = new WorldEventPresence(graceSeconds: 10, snapPct: 0);

            p.Begin(T0);
            p.Sample(T0.AddSeconds(2), new[] { In(A) });
            p.OnDeath(A, T0.AddSeconds(2), insideArena: true);
            p.Sample(T0.AddSeconds(4), new[] { Out(A) });   // grace
            p.Sample(T0.AddSeconds(14), new[] { Out(A) });  // past grace (until = 12), clamped dt 5 not credited

            Assert.AreEqual(4.0, Seconds(p, A), 1e-9);
            Assert.IsTrue(p.TryGetFraction(A, out var f));
            Assert.AreEqual(f * p.TotalSeconds, Seconds(p, A), 1e-9);
        }
    }
}
