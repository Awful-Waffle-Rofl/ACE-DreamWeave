using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>Pure perimeter and creature-containment rules (Open World Threads R4, R10-R20). Only FromTunables touches PropertyManager.</summary>
    [TestClass]
    public class OutdoorPerimeterRulesTests
    {
        private const double R = 100.0;

        private static OutdoorPerimeterSettings Def() => new OutdoorPerimeterSettings(
            OutdoorSiteTunables.DefaultWarningBandM, OutdoorSiteTunables.DefaultBackstopM, OutdoorSiteTunables.DefaultUnravelEnabled,
            OutdoorSiteTunables.DefaultUnravelStartPct, OutdoorSiteTunables.DefaultUnravelStepPct, OutdoorSiteTunables.DefaultUnravelLethal,
            OutdoorSiteTunables.DefaultUnravelGraceS, OutdoorSiteTunables.DefaultBackstopEnabled,
            OutdoorSiteTunables.DefaultContainmentMarginM, OutdoorSiteTunables.DefaultInteriorAwarenessM);

        private static PlayerTickInput In(PerimeterZone z, long hp = 1000, long max = 1000, double now = 100,
            double sinceArrival = 50, double sinceInbound = 0, bool tele = false, bool dead = false, bool logout = false)
            => new PlayerTickInput(z, tele, dead, logout, sinceArrival, sinceInbound, hp, max, now);

        private static PlayerPerimeterState St(int n = 0, double last = 0, bool warned = false, double lastTick = double.NegativeInfinity) => new PlayerPerimeterState(n, last, warned, lastTick);

        // ---- Classify ----

        [TestMethod]
        public void Classify_ExactEdges()
        {
            var s = Def();
            Assert.AreEqual(PerimeterZone.Inside, OutdoorPerimeterRules.Classify(0, R, true, s));
            Assert.AreEqual(PerimeterZone.Inside, OutdoorPerimeterRules.Classify(84.999, R, true, s));
            Assert.AreEqual(PerimeterZone.Inside, OutdoorPerimeterRules.Classify(85.0, R, true, s));
            Assert.AreEqual(PerimeterZone.Warning, OutdoorPerimeterRules.Classify(85.001, R, true, s));
            Assert.AreEqual(PerimeterZone.Warning, OutdoorPerimeterRules.Classify(100.0, R, true, s));
            Assert.AreEqual(PerimeterZone.Outside, OutdoorPerimeterRules.Classify(100.001, R, true, s));
            Assert.AreEqual(PerimeterZone.Outside, OutdoorPerimeterRules.Classify(140.0, R, true, s));
            Assert.AreEqual(PerimeterZone.Backstop, OutdoorPerimeterRules.Classify(140.001, R, true, s));
        }

        [TestMethod]
        public void Classify_OffFootprintIsBackstopEvenAtCentre()
        {
            Assert.AreEqual(PerimeterZone.Backstop, OutdoorPerimeterRules.Classify(0, R, false, Def()));
        }

        [TestMethod]
        public void Classify_UsesSettingsBandAndBackstop()
        {
            var s = Def() with { Band = 5, BackstopM = 10 };
            Assert.AreEqual(PerimeterZone.Inside, OutdoorPerimeterRules.Classify(95, R, true, s));
            Assert.AreEqual(PerimeterZone.Warning, OutdoorPerimeterRules.Classify(95.001, R, true, s));
            Assert.AreEqual(PerimeterZone.Outside, OutdoorPerimeterRules.Classify(110, R, true, s));
            Assert.AreEqual(PerimeterZone.Backstop, OutdoorPerimeterRules.Classify(110.5, R, true, s));
        }

        // ---- Tick: damage ----

        [TestMethod]
        public void Tick_DamageRamp_5_10_15_20_25_30_Percent()
        {
            var s = Def();
            var st = St();
            long[] expected = { 50, 100, 150, 200, 250, 300 };
            for (var i = 0; i < expected.Length; i++)
            {
                var r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Outside, hp: 100000, max: 1000, now: 100 + i), s);
                Assert.AreEqual(PerimeterAction.Damage, r.Action);
                Assert.AreEqual(expected[i], r.Damage, "tick " + (i + 1));
                Assert.AreEqual(i + 1, r.State.OutsideTicks);
                st = r.State;
            }
        }

        [TestMethod]
        public void Tick_DamageIsAtLeastOne()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 100, max: 10), Def());
            Assert.AreEqual(1, r.Damage); // 5% of 10 = 0.5 rounds away from zero to 1
            r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 100, max: 1), Def());
            Assert.AreEqual(1, r.Damage); // 0.05 rounds to 0, floored to 1
        }

        [TestMethod]
        public void Tick_NonLethal_SnapsBackOnWouldKillTick_LeavesOneHp_ClearsStack()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 2, warned: true), In(PerimeterZone.Outside, hp: 100, max: 1000), Def()); // tick 3 = 150
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, r.Action);
            Assert.AreEqual(99, r.Damage);
            Assert.AreEqual(1, 100 - r.Damage);
            Assert.AreEqual(0, r.State.OutsideTicks);
            Assert.IsFalse(r.State.WarnedThisVisit);
        }

        [TestMethod]
        public void Tick_NonLethal_ExactlyKillingDamageAlsoSnapsBack()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 50, max: 1000), Def()); // 50 dmg would leave 0
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, r.Action);
            Assert.AreEqual(49, r.Damage);
        }

        [TestMethod]
        public void Tick_NonLethal_SnapsWhenHealthWouldEndAtOne()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 51, max: 1000), Def()); // 50 dmg leaves 1
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, r.Action);
            Assert.AreEqual(50, r.Damage);
            r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 52, max: 1000), Def()); // leaves 2
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
            Assert.AreEqual(50, r.Damage);
        }

        [TestMethod]
        public void Tick_NonLethal_OneHpTakesNoDamage()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, hp: 1, max: 1000), Def());
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, r.Action);
            Assert.AreEqual(0, r.Damage);
        }

        [TestMethod]
        public void Tick_Lethal_DealsFullAmount()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 2), In(PerimeterZone.Outside, hp: 100, max: 1000), Def() with { Lethal = true });
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
            Assert.AreEqual(150, r.Damage);
            Assert.AreEqual(3, r.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_UsesCustomStartAndStep()
        {
            var s = Def() with { StartPct = 10, StepPct = 2 };
            var r = OutdoorPerimeterRules.Tick(St(n: 2), In(PerimeterZone.Outside, hp: 100000, max: 1000), s);
            Assert.AreEqual(140, r.Damage); // 10 + 2*2 = 14%
        }

        [TestMethod]
        public void Tick_UnravelDisabled_NoPenalty_ButBackstopStillApplies()
        {
            var s = Def() with { UnravelEnabled = false };
            var r = OutdoorPerimeterRules.Tick(St(n: 3), In(PerimeterZone.Outside), s);
            Assert.AreEqual(PerimeterAction.None, r.Action);
            Assert.AreEqual(0, r.Damage);
            Assert.AreEqual(3, r.State.OutsideTicks);
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Backstop), s).Action);
        }

        // ---- Tick: hard holds, suppression, rate limit ----

        private static void AssertHardHold(PlayerTickInput input, string why)
        {
            var before = St(n: 3, last: 90, warned: true, lastTick: 1);
            var r = OutdoorPerimeterRules.Tick(before, input, Def());
            Assert.AreEqual(PerimeterAction.None, r.Action, why);
            Assert.AreEqual(0, r.Damage, why);
            Assert.AreEqual(before, r.State, why);
            Assert.IsNull(r.MessageKey, why);
        }

        private static void AssertOutsideNoOp(PlayerTickInput input, string why)
        {
            var before = St(n: 3, last: 90, warned: true);
            var r = OutdoorPerimeterRules.Tick(before, input, Def());
            Assert.AreEqual(PerimeterAction.None, r.Action, why);
            Assert.AreEqual(0, r.Damage, why);
            Assert.AreEqual(3, r.State.OutsideTicks, why);
            Assert.AreEqual(input.NowSeconds, r.State.LastDecayAt, why);
            Assert.IsNull(r.MessageKey, why);
        }

        [TestMethod] public void Tick_HardHold_Teleporting() => AssertHardHold(In(PerimeterZone.Outside, tele: true), "teleporting");
        [TestMethod] public void Tick_HardHold_Dead() => AssertHardHold(In(PerimeterZone.Outside, dead: true), "dead");
        [TestMethod] public void Tick_HardHold_LoggingOut() => AssertHardHold(In(PerimeterZone.Outside, logout: true), "logout");
        [TestMethod] public void Tick_HardHold_InsideDoesNotDecay() => AssertHardHold(In(PerimeterZone.Inside, now: 1000, tele: true), "no decay on a hard hold");
        [TestMethod] public void Tick_Stale_OutsideIsNoOp() => AssertOutsideNoOp(In(PerimeterZone.Outside, sinceInbound: 5), "stale outside");
        [TestMethod] public void Tick_Grace_OutsideIsNoOp() => AssertOutsideNoOp(In(PerimeterZone.Outside, sinceArrival: 9.99), "grace outside");
        [TestMethod] public void Tick_Grace_BackstopIsNoOp() => AssertOutsideNoOp(In(PerimeterZone.Backstop, sinceArrival: 9.99), "grace backstop");

        [TestMethod]
        public void Tick_Stale_InsideStillDecays()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 3, last: 100), In(PerimeterZone.Inside, now: 102, sinceInbound: 5), Def());
            Assert.AreEqual(2, r.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_Grace_WarningStillDecays_ButDoesNotWarn()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 3, last: 100), In(PerimeterZone.Warning, now: 102, sinceArrival: 1), Def());
            Assert.AreEqual(2, r.State.OutsideTicks);
            Assert.AreEqual(PerimeterAction.None, r.Action);
            Assert.IsFalse(r.State.WarnedThisVisit);
        }

        [TestMethod]
        public void Tick_Grace_BoundaryIsInclusiveOfGraceSeconds()
        {
            AssertOutsideNoOp(In(PerimeterZone.Outside, sinceArrival: 9.999), "just inside grace");
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, sinceArrival: 10.0), Def());
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
        }

        [TestMethod] public void Tick_Stale_JustPastTwoSecondsIsNoOp() => AssertOutsideNoOp(In(PerimeterZone.Outside, sinceInbound: 2.001), "just stale");

        [TestMethod]
        public void Tick_EveryProcessedZoneStampsLastTickAt()
        {
            foreach (var z in new[] { PerimeterZone.Inside, PerimeterZone.Warning, PerimeterZone.Outside, PerimeterZone.Backstop })
            {
                var r = OutdoorPerimeterRules.Tick(St(), In(z, now: 123.5), Def());
                Assert.AreEqual(123.5, r.State.LastTickAt, z.ToString());
            }
        }

        [TestMethod]
        public void Tick_Inbound_ExactlyTwoSecondsStillTicks()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Outside, sinceInbound: 2.0), Def());
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
        }

        [TestMethod]
        public void Tick_RateLimit_FastCallerGetsOneHz()
        {
            var st = St();
            var damageTicks = 0;
            for (var i = 0; i < 40; i++) // 8 Hz for 5 s (exact binary steps)
            {
                var r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Outside, hp: 100000, now: 100 + i * 0.125), Def());
                if (r.Action == PerimeterAction.Damage) damageTicks++;
                st = r.State;
            }
            Assert.AreEqual(5, damageTicks);
            Assert.AreEqual(5, st.OutsideTicks);
        }

        [TestMethod]
        public void Tick_RateLimit_UnderPointNineSecondsIsNoOpAndPointNineTicks()
        {
            var st = St(lastTick: 100);
            var r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Outside, now: 100.899), Def());
            Assert.AreEqual(PerimeterAction.None, r.Action);
            Assert.AreEqual(st, r.State);
            r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Outside, now: 100.9), Def());
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
            Assert.AreEqual(100.9, r.State.LastTickAt);
        }

        [TestMethod]
        public void Tick_RateLimit_JitteryOneHzCadenceLosesNoTicks()
        {
            var st = St();
            var damageTicks = 0;
            for (var i = 0; i < 20; i++)
            {
                var r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Outside, hp: 100000, now: 100 + i * 0.99), Def());
                if (r.Action == PerimeterAction.Damage) damageTicks++;
                st = r.State;
            }
            Assert.AreEqual(20, damageTicks);
        }

        private static void AssertSuppressedOutsideThenInsideKeepsStack(OutdoorPerimeterSettings s, PlayerTickInput outsideTemplate)
        {
            var st = St(n: 3, last: 90);
            for (var i = 0; i <= 30; i++)
            {
                var r = OutdoorPerimeterRules.Tick(st, outsideTemplate with { NowSeconds = 100 + i }, s);
                Assert.AreEqual(PerimeterAction.None, r.Action);
                st = r.State;
            }
            var back = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Inside, now: 131), s);
            Assert.AreEqual(3, back.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_SuppressedOutside30s_ThenInside_StackUnchanged()
            => AssertSuppressedOutsideThenInsideKeepsStack(Def(), In(PerimeterZone.Outside, sinceInbound: 5));

        [TestMethod]
        public void Tick_UnravelDisabledOutside30s_ThenInside_StackUnchanged()
            => AssertSuppressedOutsideThenInsideKeepsStack(Def() with { UnravelEnabled = false }, In(PerimeterZone.Outside));
        [TestMethod]
        public void Tick_Clamps_StackAndPercent()
        {
            var s = Def() with { StartPct = 90, StepPct = 50 };
            var r = OutdoorPerimeterRules.Tick(St(n: 1), In(PerimeterZone.Outside, hp: 100000, max: 1000), s);
            Assert.AreEqual(1000, r.Damage); // 140% capped to 100%
            r = OutdoorPerimeterRules.Tick(St(n: 1000), In(PerimeterZone.Outside, hp: 100000, max: 1000), Def());
            Assert.AreEqual(1000, r.State.OutsideTicks);
        }
        // ---- Tick: decay ----

        [TestMethod]
        public void Tick_Decay_OneStepPerTwoSeconds_NeverBelowZero()
        {
            var s = Def();
            var st = St(n: 3, last: 100);
            var r = OutdoorPerimeterRules.Tick(st, In(PerimeterZone.Inside, now: 101), s);
            Assert.AreEqual(3, r.State.OutsideTicks); // only 1 s
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 102), s);
            Assert.AreEqual(2, r.State.OutsideTicks);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 103), s);
            Assert.AreEqual(2, r.State.OutsideTicks);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 104), s);
            Assert.AreEqual(1, r.State.OutsideTicks);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 106), s);
            Assert.AreEqual(0, r.State.OutsideTicks);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 200), s);
            Assert.AreEqual(0, r.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_Decay_LongGapDropsStackToZero()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 20, last: 100), In(PerimeterZone.Inside, now: 160), Def());
            Assert.AreEqual(0, r.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_Decay_MultiStepAdvancesClockByWholeSteps()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 10, last: 100), In(PerimeterZone.Inside, now: 107), Def());
            Assert.AreEqual(7, r.State.OutsideTicks); // 3 steps
            Assert.AreEqual(106, r.State.LastDecayAt);
        }

        [TestMethod]
        public void Tick_Decay_StaysOnScheduleWhenTicksAre1p9sApart()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 5, last: 100), In(PerimeterZone.Inside, now: 101.9), Def());
            Assert.AreEqual(5, r.State.OutsideTicks);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 103.8), Def());
            Assert.AreEqual(4, r.State.OutsideTicks);
            Assert.AreEqual(102, r.State.LastDecayAt);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 105.7), Def());
            Assert.AreEqual(3, r.State.OutsideTicks);
            Assert.AreEqual(104, r.State.LastDecayAt);
        }

        [TestMethod]
        public void Tick_Decay_BackwardsClockDecaysNothing()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 5, last: 100), In(PerimeterZone.Inside, now: 50), Def());
            Assert.AreEqual(5, r.State.OutsideTicks);
            Assert.AreEqual(100, r.State.LastDecayAt);
        }

        [TestMethod]
        public void Tick_Decay_AlsoRunsInWarningBand()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 2, last: 100, warned: true), In(PerimeterZone.Warning, now: 102), Def());
            Assert.AreEqual(1, r.State.OutsideTicks);
        }

        [TestMethod]
        public void Tick_OutsideResetsDecayClock()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 1, last: 0), In(PerimeterZone.Outside, now: 100), Def());
            Assert.AreEqual(100, r.State.LastDecayAt);
            var back = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 101), Def());
            Assert.AreEqual(2, back.State.OutsideTicks); // not yet 2 s since leaving the outside zone
        }

        // ---- Tick: warn once per visit ----

        [TestMethod]
        public void Tick_Warns_OncePerVisit_ResetsOnInside()
        {
            var s = Def();
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Warning, now: 100), s);
            Assert.AreEqual(PerimeterAction.WarnEnteredBand, r.Action);
            Assert.AreEqual(OutdoorPerimeterRules.MsgWarnBand, r.MessageKey);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Warning, now: 101), s);
            Assert.AreEqual(PerimeterAction.None, r.Action);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Inside, now: 102), s);
            Assert.IsFalse(r.State.WarnedThisVisit);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Warning, now: 103), s);
            Assert.AreEqual(PerimeterAction.WarnEnteredBand, r.Action);
        }

        [TestMethod]
        public void Tick_Warn_NotRepeatedAfterOutsideAndBackToBand()
        {
            var s = Def();
            var r = OutdoorPerimeterRules.Tick(St(warned: true), In(PerimeterZone.Outside, now: 100), s);
            r = OutdoorPerimeterRules.Tick(r.State, In(PerimeterZone.Warning, now: 101), s);
            Assert.AreEqual(PerimeterAction.None, r.Action);
        }

        // ---- Tick: backstop ----

        [TestMethod]
        public void Tick_Backstop_Enabled_SnapsBackNoDamageClearsStack()
        {
            var r = OutdoorPerimeterRules.Tick(St(n: 4, warned: true), In(PerimeterZone.Backstop), Def());
            Assert.AreEqual(PerimeterAction.SnapBackToDrop, r.Action);
            Assert.AreEqual(0, r.Damage);
            Assert.AreEqual(0, r.State.OutsideTicks);
            Assert.AreEqual(OutdoorPerimeterRules.MsgBackstop, r.MessageKey);
            Assert.IsFalse(r.State.WarnedThisVisit);
        }

        [TestMethod]
        public void Tick_Backstop_Disabled_TreatedAsOutside()
        {
            var r = OutdoorPerimeterRules.Tick(St(), In(PerimeterZone.Backstop), Def() with { BackstopEnabled = false });
            Assert.AreEqual(PerimeterAction.Damage, r.Action);
            Assert.AreEqual(50, r.Damage);
            Assert.AreEqual(1, r.State.OutsideTicks);
        }

        // ---- Creature rules ----

        [TestMethod]
        public void ShouldReturnHome_Edges()
        {
            var s = Def();
            Assert.IsFalse(OutdoorPerimeterRules.ShouldReturnHome(97.999, R, s));
            Assert.IsFalse(OutdoorPerimeterRules.ShouldReturnHome(98.0, R, s));
            Assert.IsTrue(OutdoorPerimeterRules.ShouldReturnHome(98.001, R, s));
            Assert.IsTrue(OutdoorPerimeterRules.ShouldReturnHome(150, R, s));
        }

        [TestMethod]
        public void ShouldReturnHome_UsesMargin()
        {
            Assert.IsTrue(OutdoorPerimeterRules.ShouldReturnHome(95.5, R, Def() with { ContainmentMarginM = 5 }));
        }

        [TestMethod]
        public void IsValidTarget_Rules()
        {
            Assert.IsTrue(OutdoorPerimeterRules.IsValidTarget(true, 100.0, R));
            Assert.IsFalse(OutdoorPerimeterRules.IsValidTarget(true, 100.001, R));
            Assert.IsTrue(OutdoorPerimeterRules.IsValidTarget(false, 500, R));
            Assert.IsTrue(OutdoorPerimeterRules.IsValidTarget(true, 0, R));
        }

        [TestMethod]
        public void ShouldRefuseDamage_Rules()
        {
            Assert.IsFalse(OutdoorPerimeterRules.ShouldRefuseDamage(true, 100.0, R));
            Assert.IsTrue(OutdoorPerimeterRules.ShouldRefuseDamage(true, 100.001, R));
            Assert.IsFalse(OutdoorPerimeterRules.ShouldRefuseDamage(false, 500, R));
        }

        [TestMethod]
        public void ClampAwareness_Interior()
        {
            var s = Def();
            Assert.AreEqual(12f, OutdoorPerimeterRules.ClampAwareness(40f, true, 1000, s));
            Assert.AreEqual(8f, OutdoorPerimeterRules.ClampAwareness(8f, true, 1000, s));
            Assert.AreEqual(40f, OutdoorPerimeterRules.ClampAwareness(40f, false, 1000, s));
        }

        [TestMethod]
        public void ClampAwareness_DropDistance_FloorsAtTen()
        {
            var s = Def();
            Assert.AreEqual(45f, OutdoorPerimeterRules.ClampAwareness(80f, false, 50, s));
            Assert.AreEqual(10f, OutdoorPerimeterRules.ClampAwareness(80f, false, 12, s));
            Assert.AreEqual(10f, OutdoorPerimeterRules.ClampAwareness(80f, false, 15, s));
            Assert.AreEqual(10.5f, OutdoorPerimeterRules.ClampAwareness(80f, false, 15.5, s));
            Assert.AreEqual(6f, OutdoorPerimeterRules.ClampAwareness(6f, false, 12, s)); // never raised above current
        }

        [TestMethod]
        public void ClampAwareness_BothClampsCompose()
        {
            Assert.AreEqual(10f, OutdoorPerimeterRules.ClampAwareness(80f, true, 14, Def()));
            Assert.AreEqual(12f, OutdoorPerimeterRules.ClampAwareness(80f, true, 60, Def()));
        }

        [TestMethod]
        public void HomeRadiusFor_IsDoubleRadius()
        {
            Assert.AreEqual(200.0, OutdoorPerimeterRules.HomeRadiusFor(100.0));
            Assert.AreEqual(150.0, OutdoorPerimeterRules.HomeRadiusFor(75.0));
        }

        // ---- FromTunables ----

        [TestMethod]
        public void FromTunables_ReadsTheRightKeys()
        {
            try
            {
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.WarningBandKey, 1.5));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.BackstopMKey, 2.5));
                Assert.IsTrue(PropertyManager.ModifyBool(OutdoorSiteTunables.UnravelEnabledKey, false));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelStartPctKey, 3.5));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelStepPctKey, 4.5));
                Assert.IsTrue(PropertyManager.ModifyBool(OutdoorSiteTunables.UnravelLethalKey, true));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelGraceKey, 5.5));
                Assert.IsTrue(PropertyManager.ModifyBool(OutdoorSiteTunables.BackstopEnabledKey, false));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.ContainmentMarginKey, 6.5));
                Assert.IsTrue(PropertyManager.ModifyDouble(OutdoorSiteTunables.InteriorAwarenessKey, 7.5));

                var s = OutdoorPerimeterSettings.FromTunables();
                Assert.AreEqual(1.5, s.Band);
                Assert.AreEqual(2.5, s.BackstopM);
                Assert.IsFalse(s.UnravelEnabled);
                Assert.AreEqual(3.5, s.StartPct);
                Assert.AreEqual(4.5, s.StepPct);
                Assert.IsTrue(s.Lethal);
                Assert.AreEqual(5.5, s.GraceS);
                Assert.IsFalse(s.BackstopEnabled);
                Assert.AreEqual(6.5, s.ContainmentMarginM);
                Assert.AreEqual(7.5, s.InteriorAwarenessM);
            }
            finally
            {
                PropertyManager.ModifyDouble(OutdoorSiteTunables.WarningBandKey, OutdoorSiteTunables.DefaultWarningBandM);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.BackstopMKey, OutdoorSiteTunables.DefaultBackstopM);
                PropertyManager.ModifyBool(OutdoorSiteTunables.UnravelEnabledKey, OutdoorSiteTunables.DefaultUnravelEnabled);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelStartPctKey, OutdoorSiteTunables.DefaultUnravelStartPct);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelStepPctKey, OutdoorSiteTunables.DefaultUnravelStepPct);
                PropertyManager.ModifyBool(OutdoorSiteTunables.UnravelLethalKey, OutdoorSiteTunables.DefaultUnravelLethal);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.UnravelGraceKey, OutdoorSiteTunables.DefaultUnravelGraceS);
                PropertyManager.ModifyBool(OutdoorSiteTunables.BackstopEnabledKey, OutdoorSiteTunables.DefaultBackstopEnabled);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.ContainmentMarginKey, OutdoorSiteTunables.DefaultContainmentMarginM);
                PropertyManager.ModifyDouble(OutdoorSiteTunables.InteriorAwarenessKey, OutdoorSiteTunables.DefaultInteriorAwarenessM);
            }
        }
    }
}