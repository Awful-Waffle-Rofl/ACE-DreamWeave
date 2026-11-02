using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using ACE.Server.WorldEvents;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for <see cref="WorldEventPresence"/> with a hand-driven clock.
    /// </summary>
    [TestClass]
    public class WorldEventPresenceTests
    {
        private static readonly DateTime T0 = new DateTime(2026, 10, 19, 12, 0, 0, DateTimeKind.Utc);

        private const uint A = 1;
        private const uint B = 2;

        private static PresenceSample In(uint g) => new PresenceSample(g, "p" + g, true);
        private static PresenceSample Out(uint g) => new PresenceSample(g, "p" + g, false);

        /// <summary>Ticks every <paramref name="step"/> seconds for <paramref name="seconds"/>, returns the end time.</summary>
        private static DateTime Run(WorldEventPresence p, DateTime from, int seconds, int step, params PresenceSample[] who)
        {
            var t = from;

            for (var s = 0; s < seconds; s += step)
            {
                t = t.AddSeconds(step);
                p.Sample(t, who);
            }

            return t;
        }

        private static double Frac(WorldEventPresence p, uint g)
        {
            Assert.IsTrue(p.TryGetFraction(g, out var f));
            return f;
        }

        [TestMethod]
        public void BeforeBegin_OrZeroTotal_IsUnknown()
        {
            var p = new WorldEventPresence();

            Assert.IsFalse(p.TryGetFraction(A, out _), "never begun");

            p.Begin(T0);
            Assert.IsFalse(p.TryGetFraction(A, out _), "begun but nothing sampled: total 0");

            p.Sample(T0, new[] { In(A) });
            Assert.IsFalse(p.TryGetFraction(A, out _), "a zero-length tick adds no total");
        }

        [TestMethod]
        public void UnseenGuid_IsKnownWithZeroFraction()
        {
            var p = new WorldEventPresence();
            p.Begin(T0);
            Run(p, T0, 100, 5, In(A));

            Assert.IsTrue(p.TryGetFraction(B, out var f));
            Assert.AreEqual(0.0, f);
        }

        [TestMethod]
        public void StalledTick_IsClampedToFiveSeconds()
        {
            var p = new WorldEventPresence();
            p.Begin(T0);
            p.Sample(T0.AddSeconds(60), new[] { In(A) });

            Assert.AreEqual(5.0, p.TotalSeconds, 0.0001);
            Assert.AreEqual(1.0, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void BackwardsClock_AddsNothing()
        {
            var p = new WorldEventPresence();
            p.Begin(T0);
            p.Sample(T0.AddSeconds(5), new[] { In(A) });
            p.Sample(T0.AddSeconds(2), new[] { In(A) });

            Assert.AreEqual(5.0, p.TotalSeconds, 0.0001);
        }

        [TestMethod]
        public void LateJoiner_GetsOnlyTheirShare()
        {
            var p = new WorldEventPresence(120, 2);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));
            Run(p, t, 100, 5, In(A), In(B));

            Assert.AreEqual(1.0, Frac(p, A), 0.0001);
            Assert.AreEqual(0.5, Frac(p, B), 0.0001);
        }

        [TestMethod]
        public void Offline_AccruesNothing()
        {
            var p = new WorldEventPresence();
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A), In(B));
            Run(p, t, 100, 5, In(A));

            Assert.AreEqual(0.5, Frac(p, B), 0.0001);
        }

        [TestMethod]
        public void Death_StartsGrace_ThenGraceExpires()
        {
            var p = new WorldEventPresence(30, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A), In(B));

            p.OnDeath(A, t, insideArena: true);
            t = Run(p, t, 100, 5, Out(A), In(B));     // 30 s of grace, then 70 s absent

            Assert.AreEqual(130.0 / 200.0, Frac(p, A), 0.0001);
            Assert.AreEqual(1.0, Frac(p, B), 0.0001);
        }

        [TestMethod]
        public void Grace_EndsEarlyOnReentry()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));

            p.OnDeath(A, t, true);
            t = Run(p, t, 10, 5, Out(A));             // 10 s on grace
            t = Run(p, t, 10, 5, In(A));              // back in: grace cleared
            t = Run(p, t, 30, 5, Out(A));             // no grace any more

            // present 100 + 10 + 10 = 120 of 150
            Assert.AreEqual(120.0 / 150.0, Frac(p, A), 0.0001);
        }

        /// <summary>
        /// Code review F1: the corpse lies inside the arena for the death animation and the lifestone delay, so
        /// the dying player's own Inside samples arrive AFTER OnDeath. They must not cancel the unused grace.
        /// </summary>
        [TestMethod]
        public void Grace_SurvivesTheCorpsesOwnInsideSamples_BeforeTheTeleport()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 1, In(A));

            p.OnDeath(A, t, true);
            t = Run(p, t, 5, 1, In(A));               // the corpse, still inside
            t = Run(p, t, 20, 1, Out(A));             // at the lifestone, within the grace

            Assert.AreEqual(1.0, Frac(p, A), 0.0001, "every Out tick inside the grace accrues");
        }

        /// <summary>The reviewer's repro: 100 s in, death, 5 s in, 100 s out within grace reads 1.0, not 0.5122.</summary>
        [TestMethod]
        public void Grace_ReviewRepro_CorpseThenCorpseRun_IsFullPresence()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 1, In(A));

            p.OnDeath(A, t, true);
            t = Run(p, t, 5, 1, In(A));
            Run(p, t, 100, 1, Out(A));

            Assert.AreEqual(1.0, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void RepeatDeath_RestartsGrace_NeverStacks()
        {
            var p = new WorldEventPresence(30, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));

            p.OnDeath(A, t, true);
            t = Run(p, t, 20, 5, Out(A));
            p.OnDeath(A, t, true);                     // restarts to t + 30, not t + 10 + 30 stacked
            t = Run(p, t, 60, 5, Out(A));

            // present 100 + 20 + 30 = 150 of 180
            Assert.AreEqual(150.0 / 180.0, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void Logout_EndsGrace()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A), In(B));

            p.OnDeath(A, t, true);
            t = Run(p, t, 10, 5, In(B));               // A absent: logged out, grace ends
            t = Run(p, t, 40, 5, Out(A), In(B));       // A back, outside: grace must not resume

            // present 100 of 150
            Assert.AreEqual(100.0 / 150.0, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void Grace_CannotPushAboveTotal()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));

            p.OnDeath(A, t, true);
            Run(p, t, 100, 5, Out(A));

            Assert.AreEqual(1.0, Frac(p, A), 0.0001, "dt per tick is the cap, so grace cannot exceed the window");
        }

        [TestMethod]
        public void Snap_BothSidesOfTheBoundary()
        {
            // total 200 s, snapPct 2 -> max(3, 4) = 4 s snap window
            var snapped = new WorldEventPresence(120, 2);
            snapped.Begin(T0);
            var t = Run(snapped, T0, 196, 4, In(A));
            Run(snapped, t, 4, 4, Out(A));             // 4 s short of 200
            Assert.AreEqual(1.0, Frac(snapped, A), 0.0001, "exactly at the boundary snaps");

            var notSnapped = new WorldEventPresence(120, 2);
            notSnapped.Begin(T0);
            t = Run(notSnapped, T0, 195, 5, In(A));
            Run(notSnapped, t, 5, 5, Out(A));          // 5 s short of 200
            Assert.AreEqual(195.0 / 200.0, Frac(notSnapped, A), 0.0001, "one second past the boundary does not");
        }

        [TestMethod]
        public void Snap_FloorIsThreeSeconds_OnShortWindows()
        {
            var p = new WorldEventPresence(120, 2);
            p.Begin(T0);
            Run(p, T0, 2, 1, In(A));
            p.Sample(T0.AddSeconds(5), new[] { Out(A) });   // 3 s short of 5

            Assert.AreEqual(1.0, Frac(p, A), 0.0001, "pct window is 0.1 s but the 3 s floor applies");
        }

        [TestMethod]
        public void DeathOutsideArena_IsIgnored()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));

            p.OnDeath(A, t, insideArena: false);
            Run(p, t, 100, 5, Out(A));

            Assert.AreEqual(0.5, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void Close_StopsAccrual_AndIgnoresLateDeaths_Idempotently()
        {
            var p = new WorldEventPresence(120, 0);
            p.Begin(T0);
            var t = Run(p, T0, 100, 5, In(A));

            p.Close(t);
            p.Close(t.AddSeconds(1));
            p.OnDeath(A, t, true);
            Run(p, t, 100, 5, Out(A));

            Assert.AreEqual(100.0, p.TotalSeconds, 0.0001);
            Assert.AreEqual(1.0, Frac(p, A), 0.0001);
        }

        [TestMethod]
        public void LastInside_IsASnapshot()
        {
            var p = new WorldEventPresence();
            p.Begin(T0);
            var t = Run(p, T0, 10, 5, In(A));

            var snap = p.LastInside;
            Run(p, t, 10, 5, In(A), In(B));

            Assert.AreEqual(1, snap.Count);
            Assert.AreEqual(2, p.LastInside.Count);
            Assert.AreEqual(2, p.TrackedCount);
        }

        [TestMethod]
        public void ConcurrentOnDeathDuringSample_DoesNotThrow()
        {
            var p = new WorldEventPresence(120, 2);
            p.Begin(T0);

            var who = new List<PresenceSample>();
            for (uint g = 1; g <= 50; g++)
                who.Add(Out(g));

            var errors = new List<Exception>();

            var deaths = Task.Run(() =>
            {
                try
                {
                    for (var i = 0; i < 20000; i++)
                        p.OnDeath((uint)(i % 50 + 1), T0.AddSeconds(i % 100), true);
                }
                catch (Exception ex) { lock (errors) errors.Add(ex); }
            });

            try
            {
                for (var i = 1; i <= 2000; i++)
                {
                    p.Sample(T0.AddSeconds(i), who);
                    p.TryGetFraction(1, out _);
                    _ = p.LastInside.Count;
                }
            }
            catch (Exception ex) { lock (errors) errors.Add(ex); }

            deaths.Wait();

            Assert.AreEqual(0, errors.Count, errors.Count > 0 ? errors[0].ToString() : "");
        }
    }
}