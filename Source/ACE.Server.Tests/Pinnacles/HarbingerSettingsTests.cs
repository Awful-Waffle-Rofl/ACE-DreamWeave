using System;
using System.Linq;

using ACE.Server.HazardMechanics;
using ACE.Server.Managers;
using ACE.Server.Pinnacles;
using ACE.Server.Pinnacles.Harbinger;
using ACE.Server.ThreadDungeons;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.Pinnacles
{
    /// <summary>Dat-free pins of the Harbinger settings, the hazard-args builders and the per-copy overrides. Seeds the two hazard-pct clamp keys (PinnacleTunables.HazardPctMinKey / HazardPctMaxKey) and the health multiplier key (PinnacleTunables.HarbingerHealthMultiplierKey) in WithSeeded and restores all three in its finally; reads no other PropertyManager value.</summary>
    [TestClass]
    public class HarbingerSettingsTests
    {
        private static readonly HarbingerSettings D = HarbingerSettings.Defaults;

        private static HazardRegion Region()
        {
            Assert.IsTrue(HazardRegionPresets.TryGet("harbinger", 0, out var r));
            return r;
        }

        // ---- defaults ----

        [TestMethod]
        public void Defaults_EqualThePlanValues()
        {
            Assert.AreEqual(0.02, D.AcidRate);
            Assert.AreEqual(0.16, D.AcidRateCap);
            Assert.AreEqual(60.0, D.AcidEscalateSeconds);
            Assert.AreEqual(-12.0, D.AcidMaxZ);
            Assert.AreEqual(0.75, D.AcidDamageScale);
            Assert.AreEqual(0.01, D.FinalAcidRate);
            Assert.AreEqual(-33.5, D.FinalAcidMaxZ);
            Assert.AreEqual(-2.0, D.CrystalDrainRate);
            Assert.AreEqual(0.30, D.WispPct);
            Assert.AreEqual(0.075, D.HailPct);
            Assert.AreEqual(0.375, D.FirePct);
            Assert.AreEqual(24, D.HailMaxFalling);
            Assert.AreEqual(0.25, D.HailRunSlow);
            Assert.AreEqual(0.7, D.HailAttackFactor);
            Assert.AreEqual(4.0, D.HailSlowSeconds);
            Assert.AreEqual(0.5, D.FinalScale);
            Assert.AreEqual(20.0, D.SummonSeconds);
            Assert.AreEqual(2, D.SummonCap);
            Assert.AreEqual(4, D.FinalSummonCap);
            Assert.AreEqual(8.0, D.CastSeconds);
            Assert.AreEqual(2.0, D.HealthMultiplier);
            Assert.AreEqual(8.0, D.CrystalCastSeconds);
            Assert.AreEqual(120.0, D.AttuneSeconds);

            // round 2 B1, B2: the doorway pressure and the four alcove signatures
            Assert.AreEqual(60.0, D.PressureSeconds);
            Assert.AreEqual(2.25, D.PoolSize);
            Assert.AreEqual(6, D.PoolCap);
            Assert.AreEqual(30.0, D.PoolDamage);
            Assert.AreEqual(10.0, D.TwinReviveSeconds);
            Assert.AreEqual(0.04, D.WhiteoutPct);
            Assert.AreEqual(3, D.WhiteoutMaxFalling);
            Assert.AreEqual(6, D.WhiteoutPressureMaxFalling);
            Assert.AreEqual(10.0, D.WhiteoutSpeed);
            Assert.AreEqual(14.0, D.WhiteoutPressureSpeed);
            Assert.AreEqual(1.2, D.WhiteoutBurstRadius);
            Assert.AreEqual(0.5, D.EmberBallRadius);

            // round 3 (owner playtest 2026-11-19): acid drips, conductor wisps, the faster weaker Ember roll
            Assert.AreEqual(2.0, D.DripSeconds);   // round 4: was 6
            Assert.AreEqual(1.2, D.DripPressureSeconds);   // round 4: was 3.5
            Assert.AreEqual(0.04, D.DripPct);
            Assert.AreEqual(10.0, D.AlcoveGraceSeconds, "round 4: hazards wait 10 s after the arrival");
            Assert.AreEqual(0.0, D.FilmDamage, "round 4: the acid film is visual only");
            Assert.AreEqual(0.15, D.PoolStep);
            Assert.AreEqual(0.6, D.PoolMaxDepth);
            Assert.AreEqual(100.0, D.PoolDamagePerM);
            Assert.AreEqual(0.20, D.ConductorWispPct);
            Assert.AreEqual(2, D.ConductorWisps);
            Assert.AreEqual(3, D.ConductorPressureWisps);
            Assert.AreEqual(0.03, D.EmberPctMin);
            Assert.AreEqual(0.10, D.EmberPctMax);
            Assert.AreEqual(2, D.EmberCount);
            Assert.AreEqual(3, D.EmberPressureCount);
            Assert.AreEqual(3.0, D.EmberEverySeconds);
            Assert.AreEqual(2.0, D.EmberPressureEverySeconds);
            Assert.AreEqual(4.0, D.EmberSpeed);
            Assert.AreEqual(5.0, D.EmberPressureSpeed);
        }

        [TestMethod]
        public void MeleeReach_IsDerivedFromTheScaledHeadRadius_NotMultipliedAgain()
        {
            Assert.AreEqual(15.8 - HarbingerLayout.HeadRadius - 0.5, D.MeleeReach, 1e-9);
            Assert.AreEqual(11.0913, D.MeleeReach, 1e-3, "15.8 - 4.2087 - 0.5");
        }

        [TestMethod]
        public void HailSlow_CarriesTheThreeFields()
            => Assert.AreEqual(new PinnacleHitSlow(0.25, 0.7, 4), D.HailSlow);

        private static void WithSeeded(double min, double max, Action body, double healthMultiplier = PinnacleTunables.DefaultHarbingerHealthMultiplier)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMinKey, min), "min key missing from DefaultDoubleProperties");
            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMaxKey, max), "max key missing from DefaultDoubleProperties");
            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HarbingerHealthMultiplierKey, healthMultiplier), "health multiplier key missing from DefaultDoubleProperties");

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMinKey, PinnacleTunables.DefaultHazardPctMin);
                PropertyManager.ModifyDouble(PinnacleTunables.HazardPctMaxKey, PinnacleTunables.DefaultHazardPctMax);
                PropertyManager.ModifyDouble(PinnacleTunables.HarbingerHealthMultiplierKey, PinnacleTunables.DefaultHarbingerHealthMultiplier);
            }
        }

        [TestMethod]
        public void Tunables_HealthMultiplier_IsTheLiveValue_Clamped()
        {
            WithSeeded(PinnacleTunables.DefaultHazardPctMin, PinnacleTunables.DefaultHazardPctMax, () =>
            {
                Assert.AreEqual(3.5, PinnacleTunables.HarbingerHealthMultiplier);
                Assert.AreEqual(3.5, PinnacleTunables.Harbinger().HealthMultiplier);
                Assert.AreEqual(3.5, new HarbingerScript(PinnacleTunables.Harbinger()).Data.HealthMultiplierFor(HarbingerData.Elements[0].CrystalWcid), "the live value reaches the script's Data (the crystals)");
            }, healthMultiplier: 3.5);

            WithSeeded(PinnacleTunables.DefaultHazardPctMin, PinnacleTunables.DefaultHazardPctMax, () =>
                Assert.AreEqual(10.0, PinnacleTunables.Harbinger().HealthMultiplier, "clamped to 10"), healthMultiplier: 50);

            WithSeeded(PinnacleTunables.DefaultHazardPctMin, PinnacleTunables.DefaultHazardPctMax, () =>
                Assert.AreEqual(0.1, PinnacleTunables.Harbinger().HealthMultiplier, "clamped to 0.1"), healthMultiplier: -1);
        }

        [TestMethod]
        public void Tunables_HealthMultiplier_Sanitize()
        {
            Assert.AreEqual(2.0, PinnacleTunables.SanitizeHarbingerHealthMultiplier(double.NaN));
            Assert.AreEqual(2.0, PinnacleTunables.SanitizeHarbingerHealthMultiplier(double.PositiveInfinity));
            Assert.AreEqual(0.1, PinnacleTunables.SanitizeHarbingerHealthMultiplier(0));
            Assert.AreEqual(10.0, PinnacleTunables.SanitizeHarbingerHealthMultiplier(11));
            Assert.AreEqual(1.5, PinnacleTunables.SanitizeHarbingerHealthMultiplier(1.5));
        }

        [TestMethod]
        public void Tunables_HealthMultiplier_IsRegistered_InBothSnapshots_WithItsDefault()
        {
            Assert.AreEqual("pinnacle_harbinger_health_multiplier", PinnacleTunables.HarbingerHealthMultiplierKey);
            Assert.AreEqual(2.0, DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HarbingerHealthMultiplierKey].Item);

            var d = DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HarbingerHealthMultiplierKey].Description;
            StringAssert.Contains(d, "0.1 to 10");
            StringAssert.Contains(d, "Read when a fight is created");
            StringAssert.Contains(d, "Default 2.0");

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            var meta = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");

            StringAssert.Contains(defaults, "pinnacle_harbinger_health_multiplier\tdouble\t2\t2\t");
            Assert.IsTrue(meta.Split('\n').Any(l => l.StartsWith(PinnacleTunables.HarbingerHealthMultiplierKey + "\t", StringComparison.Ordinal)));
        }

        [TestMethod]
        public void Tunables_HeadHealthMultiplier_IsRegistered_Clamped_AndTenTimesTheOldHead()
        {
            Assert.AreEqual("pinnacle_harbinger_head_health_multiplier", PinnacleTunables.HarbingerHeadHealthMultiplierKey);
            Assert.AreEqual(20.0, DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HarbingerHeadHealthMultiplierKey].Item);
            Assert.AreEqual(20.0, HarbingerSettings.Defaults.HeadHealthMultiplier);
            Assert.AreEqual(2.0, HarbingerSettings.Defaults.HealthMultiplier, "the crystals keep 2.0");

            var d = DefaultPropertyManager.DefaultDoubleProperties[PinnacleTunables.HarbingerHeadHealthMultiplierKey].Description;
            StringAssert.Contains(d, "0.1 to 50");
            StringAssert.Contains(d, "Default 20.0");

            var defaults = PooledLootSourceText.Read("Source/config-defaults.tsv");
            var meta = PooledLootSourceText.Read("Source/ACE.Server/config-metadata.tsv");
            StringAssert.Contains(defaults, "pinnacle_harbinger_head_health_multiplier\tdouble\t20\t20\t");
            Assert.IsTrue(meta.Split('\n').Any(l => l.StartsWith(PinnacleTunables.HarbingerHeadHealthMultiplierKey + "\t", StringComparison.Ordinal)));

            Assert.AreEqual(20.0, PinnacleTunables.SanitizeHarbingerHeadHealthMultiplier(double.NaN));
            Assert.AreEqual(0.1, PinnacleTunables.SanitizeHarbingerHeadHealthMultiplier(0));
            Assert.AreEqual(50.0, PinnacleTunables.SanitizeHarbingerHeadHealthMultiplier(51));
            Assert.AreEqual(12.5, PinnacleTunables.SanitizeHarbingerHeadHealthMultiplier(12.5));

            Assert.IsTrue(PropertyManager.ModifyDouble(PinnacleTunables.HarbingerHeadHealthMultiplierKey, 33), "head key missing from DefaultDoubleProperties");

            try
            {
                Assert.AreEqual(33.0, PinnacleTunables.Harbinger().HeadHealthMultiplier, "the live value reaches the settings");
                Assert.AreEqual(33.0, new HarbingerScript(PinnacleTunables.Harbinger()).Data.HealthMultiplierFor(HarbingerData.HeadWcid));
            }
            finally
            {
                PropertyManager.ModifyDouble(PinnacleTunables.HarbingerHeadHealthMultiplierKey, PinnacleTunables.DefaultHarbingerHeadHealthMultiplier);
            }
        }

        [TestMethod]
        public void HeadHealth_AtKnotLevels200_300_400_IsTenTimesTheOldHead_AndFarUnderTheIntClamp()
        {
            // golden values from the formula's own factors, not from the new code: the head is the Boss tier, T(L) the default curve
            foreach (var (level, oldHp, newHp) in new[] { (200, 6603u, 66030u), (300, 27190u, 271905u), (400, 111967u, 1119673u) })
            {
                var old = PinnacleScaling.TierHealth(PinnacleTier.Boss, level, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh, DungeonHealthCurve.DefaultTopLevel, 2.0);
                var now = PinnacleScaling.TierHealth(PinnacleTier.Boss, level, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh, DungeonHealthCurve.DefaultTopLevel, HarbingerSettings.Defaults.HeadHealthMultiplier);

                Assert.AreEqual(oldHp, old, $"level {level} old");
                Assert.AreEqual(newHp, now, $"level {level} new");

                Assert.AreEqual(old * 10.0, now, 10.0, $"level {level}: ten times (rounding only)");
                Assert.IsTrue(now < int.MaxValue / 1000u, $"level {level}: {now} is nowhere near the int clamp");
            }

            // the crystal (Elite tier at 2.0) is unchanged at every level
            Assert.AreEqual(3302u, PinnacleScaling.TierHealth(PinnacleTier.Elite, 200, DungeonHealthCurve.DefaultAnchorLow, DungeonHealthCurve.DefaultAnchorHigh, DungeonHealthCurve.DefaultTopLevel, HarbingerSettings.Defaults.HealthMultiplier));
        }

        [TestMethod]
        public void Tunables_Harbinger_IsTheDefaultsWithTheLiveClamp()
        {
            WithSeeded(0.10, 1.50, () =>
            {
                var s = PinnacleTunables.Harbinger();

                Assert.AreEqual(0.10, s.HazardPctMin);
                Assert.AreEqual(1.50, s.HazardPctMax);
                Assert.AreEqual(D with { HazardPctMin = 0.10, HazardPctMax = 1.50 }, s, "every other field is the default");
            });
        }

        // ---- builders ----

        [TestMethod]
        public void HailArgs_CarriesItsPinnedKeys()
        {
            var phase = D.HailArgs(0.01, final: false).ToString();

            StringAssert.Contains(phase, "drop_z=-18");
            StringAssert.Contains(phase, "ball_radius=0.4");
            StringAssert.Contains(phase, "max_falling=24");
            StringAssert.Contains(phase, "ball_wcid=1008263");
            StringAssert.Contains(phase, "burst_radius=1.2");
            StringAssert.Contains(phase, "pct=0.01");

            var final = D.HailArgs(0.01, final: true).ToString();

            StringAssert.Contains(final, "max_falling=12");
            StringAssert.Contains(final, "burst_radius=0.6");
            StringAssert.Contains(final, "pct=0.005", "the final halves the strength");
        }

        [TestMethod]
        public void LightningArgs_CarryCyclesAndTheMixedPool()
        {
            var phase = D.LightningArgs(0.3, final: false);

            Assert.AreEqual("4", phase.GetString("cycles"));
            Assert.AreEqual("mixed", phase.GetString("pattern"));
            Assert.AreEqual("row,column,diagonal,ring,sweep", phase.GetString("mixed_pool"));
            Assert.AreEqual("8", phase.GetString("n"));
            Assert.AreEqual("2.0", phase.GetString("move"));
            Assert.AreEqual("1.0", phase.GetString("rest"));
            Assert.AreEqual("2.0", phase.GetString("pillar_radius"));

            var final = D.LightningArgs(0.3, final: true);

            Assert.AreEqual("6", final.GetString("n"));
            Assert.AreEqual("2.0", final.GetString("move"));
            Assert.AreEqual("1.0", final.GetString("rest"));
            Assert.AreEqual("1.0", final.GetString("pillar_radius"));
            Assert.AreEqual("0.15", final.GetString("pct"));
        }

        [TestMethod]
        public void FireArgs_CarryWaveEvery()
        {
            var phase = D.FireArgs(0.25, final: false);

            Assert.AreEqual("6", phase.GetString("wave_every"));
            Assert.AreEqual("1", phase.GetString("max_in_flight"));
            Assert.AreEqual("4", phase.GetString("speed"));
            Assert.AreEqual("3", D.FireArgs(0.25, final: true).GetString("speed"));
            Assert.AreEqual("0.125", D.FireArgs(0.25, final: true).GetString("pct"));
        }

        [TestMethod]
        public void AcidArgs_CarryTheFillKeys_AndTheFinalRaisesNothingHigher()
        {
            var phase = D.AcidArgs(-35.95, 0.02, 2.0, final: false);

            Assert.AreEqual("6.5", phase.GetString("panel_size"), "the 5x5 sheet (round 2 A1)");
            Assert.AreEqual("-12", phase.GetString("max_z"));
            Assert.AreEqual("1.5", phase.GetString("damage_scale"), "level scale 2.0 x 0.75");
            Assert.AreEqual("0.02", phase.GetString("rate"));

            var final = D.AcidArgs(-35.95, 0.01, 2.0, final: true);

            Assert.AreEqual("-33.5", final.GetString("max_z"));
            Assert.AreEqual("0.75", final.GetString("damage_scale"), "x 0.5 in the final");
        }

        // ---- every builder passes the mechanic's own Validate on the harbinger region ----

        [TestMethod]
        public void Acid_Phase_Validates() => Assert.IsNull(FillRules.Validate(Region(), D.AcidArgs(-35.95, D.AcidRate, 1.0, false)));

        [TestMethod]
        public void Acid_Final_Validates() => Assert.IsNull(FillRules.Validate(Region(), D.AcidArgs(-35.95, D.FinalAcidRate, 1.0, true)));

        [TestMethod]
        public void Lightning_Phase_Validates() => Assert.IsNull(new WispSquadMechanic().Validate(Region(), D.LightningArgs(D.WispPct, false), "mixed"));

        [TestMethod]
        public void Lightning_Final_Validates() => Assert.IsNull(new WispSquadMechanic().Validate(Region(), D.LightningArgs(D.WispPct, true), "mixed"));

        [TestMethod]
        public void Hail_Phase_Validates() => Assert.IsNull(new RainMechanic().Validate(Region(), D.HailArgs(D.HailPct, false), null));

        [TestMethod]
        public void Hail_Final_Validates() => Assert.IsNull(new RainMechanic().Validate(Region(), D.HailArgs(D.HailPct, true), null));

        [TestMethod]
        public void Fire_Phase_Validates()
        {
            Assert.IsNull(new RollingBallMechanic().Validate(Region(), D.FireArgs(D.FirePct, false), BallPatterns.RingOut));
            Assert.IsNull(new RollingBallMechanic().Validate(Region(), D.FireArgs(D.FirePct, false), BallPatterns.RingIn));
        }

        [TestMethod]
        public void Fire_Final_Validates()
        {
            Assert.IsNull(new RollingBallMechanic().Validate(Region(), D.FireArgs(D.FirePct, true), BallPatterns.RingOut));
            Assert.IsNull(new RollingBallMechanic().Validate(Region(), D.FireArgs(D.FirePct, true), BallPatterns.RingIn));
        }

        [TestMethod]
        public void HailArgs_FinalMaxFalling_IsNeverZero()
        {
            var tiny = D with { HailMaxFalling = 1 };

            Assert.AreEqual("1", tiny.HailArgs(0.01, final: true).GetString("max_falling"));
            Assert.AreEqual("1", tiny.HailArgs(0.01, final: false).GetString("max_falling"));
        }

        // ---- overrides ----

        [TestMethod]
        public void Overrides_Empty_ReturnsTheBaseUnchanged()
        {
            Assert.IsTrue(HarbingerOverrides.TryApply(D, HazardArgs.Empty, out var r, out var e));
            Assert.IsNull(e);
            Assert.AreSame(D, r);
        }

        [TestMethod]
        public void Overrides_AppliesKnownKeys()
        {
            Assert.IsTrue(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_rate=0.03 summon_cap=3 hail_run_slow=0.5 melee_reach=9"), out var r, out var e), e);
            Assert.AreEqual(0.03, r.AcidRate);
            Assert.AreEqual(3, r.SummonCap);
            Assert.AreEqual(0.5, r.HailRunSlow);
            Assert.AreEqual(9.0, r.MeleeReach);
            Assert.AreEqual(D.AcidRateCap, r.AcidRateCap);
        }

        [TestMethod]
        public void Overrides_UnknownKey_IsRefusedByName()
        {
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_speed=1"), out var r, out var e));
            StringAssert.Contains(e, "acid_speed");
            Assert.AreSame(D, r);
        }

        [TestMethod]
        public void Overrides_RateCapBelowRate_IsRefused()
        {
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_rate=0.1 acid_rate_cap=0.05"), out _, out var e));
            StringAssert.Contains(e, "acid_rate_cap");
            Assert.IsTrue(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_rate=0.1 acid_rate_cap=0.1"), out _, out e), "control: equal is allowed");
        }

        [TestMethod]
        public void Overrides_BadValues_AreRefused()
        {
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_rate=fast"), out _, out var e));
            StringAssert.Contains(e, "acid_rate");
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("summon_cap=2.5"), out _, out e));
            StringAssert.Contains(e, "whole number");
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("acid_max_z=-5"), out _, out e));
            StringAssert.Contains(e, "out of range");
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("final_scale=1.5"), out _, out e), "the final is never stronger than a phase");
            StringAssert.Contains(e, "out of range");
            Assert.IsTrue(HarbingerOverrides.TryApply(D, HazardArgs.Parse("final_scale=1"), out _, out e), e);
            Assert.IsFalse(HarbingerOverrides.TryApply(D, HazardArgs.Parse("hazard_pct_min=2 hazard_pct_max=1"), out _, out e));
            StringAssert.Contains(e, "hazard_pct_min");
        }

        [TestMethod]
        public void Overrides_EveryKey_AcceptsItsBounds_AndRefusesJustOutside()
        {
            // a base on which no cross-field rule can fire at any single key's bound
            var open = D with { AcidRate = 0.001, AcidRateCap = 2, HazardPctMin = 0, HazardPctMax = PinnacleTunables.MaxHazardPct, EmberPctMin = 0, EmberPctMax = PinnacleTunables.MaxHazardPct };
            var inv = System.Globalization.CultureInfo.InvariantCulture;

            // the bounds are pinned HERE, independently of the table: a loosened table bound must fail this test, not move it
            var pinned = new (string Key, double Min, double Max)[]
            {
                ("acid_rate", 0.001, 2), ("acid_rate_cap", 0.001, 2), ("acid_escalate_seconds", 5, 600), ("acid_max_z", -35, -12),
                ("acid_damage_scale", 0.01, 10), ("wisp_pct", 0, 5), ("hail_pct", 0, 5), ("hail_max_falling", 1, 64),
                ("hail_run_slow", 0, 0.9), ("hail_attack_factor", 0.1, 1), ("hail_slow_seconds", 0, 60), ("fire_pct", 0, 5),
                ("final_scale", 0, 1), ("summon_seconds", 1, 600), ("summon_cap", 0, 16), ("final_summon_cap", 0, 16),
                ("cast_seconds", 1, 120), ("melee_reach", 1, 40), ("hazard_pct_min", 0, 5), ("hazard_pct_max", 0, 5),
                ("wall_fx", 0, 2), ("fog_acid", 0, 2), ("health_multiplier", 0.1, 10), ("head_health_multiplier", 0.1, 50), ("crystal_cast_seconds", 1, 120),
                ("attune_seconds", 0, 600),
                ("pressure_seconds", 1, 600), ("alcove_grace_seconds", 0, 60), ("pool_size", 2.25, 3), ("pool_cap", 0, 18), ("drip_seconds", 1, 120),
                ("drip_pressure_seconds", 1, 120), ("drip_pct", 0, 5), ("pool_step", 0.05, 0.5), ("pool_max_depth", 0.2, 0.65),
                ("pool_damage", 0, 1000), ("pool_damage_per_m", 0, 5000), ("film_damage", 0, 1000), ("twin_revive_seconds", 4, 120),
                ("conductor_wisp_pct", 0, 5), ("conductor_wisps", 1, 3), ("conductor_pressure_wisps", 1, 3), ("whiteout_pct", 0, 5),
                ("whiteout_max_falling", 1, 16), ("whiteout_pressure_max_falling", 1, 16), ("whiteout_speed", 2, 40),
                ("whiteout_pressure_speed", 2, 40), ("whiteout_burst_radius", 1.1, 3), ("ember_pct_min", 0, 5), ("ember_pct_max", 0, 5),
                ("ember_count", 1, 3), ("ember_pressure_count", 1, 3), ("ember_every_seconds", 2, 120),
                ("ember_pressure_every_seconds", 2, 120), ("ember_speed", 1, 12), ("ember_pressure_speed", 1, 12), ("ember_ball_radius", 0.3, 0.5),
            };

            CollectionAssert.AreEqual(pinned, new System.Collections.Generic.List<(string Key, double Min, double Max)>(HarbingerOverrides.Bounds));
            Assert.AreEqual(5.0, PinnacleTunables.MaxHazardPct, "the wisp/hail/fire/clamp rows above assume a 5.0 ceiling");
            var refusedBounds = new System.Collections.Generic.List<string>();

            foreach (var (key, min, max) in HarbingerOverrides.Bounds)
            {
                var step = HarbingerOverrides.IsWholeNumberKey(key) ? 1.0 : 0.001;

                // a bound is IN range: it is accepted, or refused only by the signature pre-flight (review F1 on 19d358426:
                // e.g. ember_speed=1 alone makes Ember roll's launch rate too high for the alcove), never as out of range
                foreach (var bound in new[] { min, max })
                {
                    if (!HarbingerOverrides.TryApply(open, HazardArgs.Parse($"{key}={bound.ToString("R", inv)}"), out _, out var e))
                    {
                        StringAssert.Contains(e, "unstartable", $"{key}={bound}: {e}");
                        refusedBounds.Add($"{key}={bound.ToString("R", inv)}");
                    }
                }

                Assert.IsFalse(HarbingerOverrides.TryApply(open, HazardArgs.Parse($"{key}={(min - step).ToString("R", inv)}"), out _, out var below), $"{key} below min");
                StringAssert.Contains(below, "out of range", key);
                Assert.IsFalse(HarbingerOverrides.TryApply(open, HazardArgs.Parse($"{key}={(max + step).ToString("R", inv)}"), out _, out var above), $"{key} above max");
                StringAssert.Contains(above, "out of range", key);
            }

            // at the default waves (2 every 3 s; 3 every 2 s pressed) a 1 m/s ball crosses the alcove too slowly: the launch
            // rate is too high for it, so those two minimums are refused alone (and pass with fewer or sparser waves)
            CollectionAssert.AreEqual(new[] { "ember_speed=1", "ember_pressure_speed=1" }, refusedBounds, "the bounds the signature pre-flight refuses on their own: " + string.Join(", ", refusedBounds));
        }

        [TestMethod]
        public void Overrides_KeysMatchTheBrief()
        {
            var expected = new[]
            {
                "acid_rate", "acid_rate_cap", "acid_escalate_seconds", "acid_max_z", "acid_damage_scale", "wisp_pct", "hail_pct",
                "hail_max_falling", "hail_run_slow", "hail_attack_factor", "hail_slow_seconds", "fire_pct", "final_scale",
                "summon_seconds", "summon_cap", "final_summon_cap", "cast_seconds", "melee_reach", "hazard_pct_min", "hazard_pct_max",
                "wall_fx", "fog_acid", "health_multiplier", "head_health_multiplier", "crystal_cast_seconds", "attune_seconds",
                "pressure_seconds", "alcove_grace_seconds", "pool_size", "pool_cap", "drip_seconds", "drip_pressure_seconds", "drip_pct", "pool_step",
                "pool_max_depth", "pool_damage", "pool_damage_per_m", "film_damage",
                "twin_revive_seconds", "conductor_wisp_pct", "conductor_wisps", "conductor_pressure_wisps", "whiteout_pct", "whiteout_max_falling", "whiteout_pressure_max_falling", "whiteout_speed",
                "whiteout_pressure_speed", "whiteout_burst_radius", "ember_pct_min", "ember_pct_max", "ember_count", "ember_pressure_count", "ember_every_seconds", "ember_pressure_every_seconds",
                "ember_speed", "ember_pressure_speed", "ember_ball_radius",
            };

            CollectionAssert.AreEquivalent(expected, new System.Collections.Generic.List<string>(HarbingerOverrides.Keys));
        }
    }
}
