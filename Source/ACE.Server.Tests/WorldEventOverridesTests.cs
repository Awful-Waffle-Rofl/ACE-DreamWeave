using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Live global overrides for the JSON wave/boss dials (TECH-DESIGN "Live overrides"):
    /// <see cref="WorldEventOverrides"/> itself, and the def-level Effective* accessors
    /// (CrowdHealthDef/PaceDef/SourceThemeDef/BossDef/ScaledCount) that consult it. Every test replaces
    /// <see cref="WorldEventOverrides.DoubleSource"/>/<see cref="WorldEventOverrides.LongSource"/> with a
    /// fixed lookup and restores the original in a finally, mirroring
    /// <see cref="WorldEventRosterSelector.DialSource"/>'s test seam pattern.
    /// </summary>
    [TestClass]
    public class WorldEventOverridesTests
    {
        private const double Epsilon = 1e-9;

        private static void WithSources(IReadOnlyDictionary<string, double> doubles, IReadOnlyDictionary<string, long> longs, Action body)
        {
            var originalDouble = WorldEventOverrides.DoubleSource;
            var originalLong = WorldEventOverrides.LongSource;

            try
            {
                WorldEventOverrides.DoubleSource = key => doubles != null && doubles.TryGetValue(key, out var v) ? v : 0;
                WorldEventOverrides.LongSource = key => longs != null && longs.TryGetValue(key, out var v) ? v : 0;

                body();
            }
            finally
            {
                WorldEventOverrides.DoubleSource = originalDouble;
                WorldEventOverrides.LongSource = originalLong;
            }
        }

        // ---- (a) WorldEventOverrides.Double/Long: 0 = JSON value, non-zero = override -------------------

        [TestMethod]
        public void Double_ZeroOverride_LeavesTheJsonValue()
        {
            WithSources(new Dictionary<string, double> { ["world_events_crowd_health_cap"] = 0 }, null, () =>
            {
                Assert.AreEqual(3.0, WorldEventOverrides.Double("world_events_crowd_health_cap", 3.0), Epsilon);
            });
        }

        [TestMethod]
        public void Double_NonZeroOverride_ReplacesTheJsonValue()
        {
            WithSources(new Dictionary<string, double> { ["world_events_crowd_health_cap"] = 9.5 }, null, () =>
            {
                Assert.AreEqual(9.5, WorldEventOverrides.Double("world_events_crowd_health_cap", 3.0), Epsilon);
            });
        }

        [TestMethod]
        public void Long_ZeroOverride_LeavesTheJsonValue()
        {
            WithSources(null, new Dictionary<string, long> { ["world_events_max_alive"] = 0 }, () =>
            {
                Assert.AreEqual(24, WorldEventOverrides.Long("world_events_max_alive", 24));
            });
        }

        [TestMethod]
        public void Long_NonZeroOverride_ReplacesTheJsonValue()
        {
            WithSources(null, new Dictionary<string, long> { ["world_events_max_alive"] = 40 }, () =>
            {
                Assert.AreEqual(40, WorldEventOverrides.Long("world_events_max_alive", 24));
            });
        }

        // ---- (a2) round-2 review: negative and non-finite overrides are ALSO "unset" ----------------------

        [TestMethod]
        public void Double_NegativeOverride_IsTreatedAsUnset()
        {
            WithSources(new Dictionary<string, double> { ["world_events_crowd_health_cap"] = -9.5 }, null, () =>
            {
                Assert.AreEqual(3.0, WorldEventOverrides.Double("world_events_crowd_health_cap", 3.0), Epsilon,
                    "a negative override must fall back to the JSON value exactly like 0 does");
            });
        }

        [TestMethod]
        public void Double_NonFiniteOverride_IsTreatedAsUnset()
        {
            foreach (var badValue in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                WithSources(new Dictionary<string, double> { ["world_events_crowd_health_cap"] = badValue }, null, () =>
                {
                    Assert.AreEqual(3.0, WorldEventOverrides.Double("world_events_crowd_health_cap", 3.0), Epsilon,
                        $"a non-finite override ({badValue}) must fall back to the JSON value");
                });
            }
        }

        [TestMethod]
        public void Long_NegativeOverride_IsTreatedAsUnset()
        {
            WithSources(null, new Dictionary<string, long> { ["world_events_max_alive"] = -40 }, () =>
            {
                Assert.AreEqual(24, WorldEventOverrides.Long("world_events_max_alive", 24),
                    "a negative override must fall back to the JSON value exactly like 0 does");
            });
        }

        [TestMethod]
        public void ActiveOverrides_ExcludesNegativeAndNonFiniteValues()
        {
            WithSources(
                new Dictionary<string, double> { ["world_events_crowd_health_cap"] = -5.0, ["world_events_boss_per_player"] = double.NaN, ["world_events_pace_health_step"] = 2.0 },
                new Dictionary<string, long> { ["world_events_max_alive"] = -30 },
                () =>
                {
                    var active = WorldEventOverrides.ActiveOverrides();

                    CollectionAssert.AreEqual(new[] { "world_events_pace_health_step=2" }, active.ToList());
                });
        }

        [TestMethod]
        public void Double_SourceThrows_LeavesTheJsonValue()
        {
            var original = WorldEventOverrides.DoubleSource;

            try
            {
                WorldEventOverrides.DoubleSource = key => throw new InvalidOperationException("no shard config");

                Assert.AreEqual(3.0, WorldEventOverrides.Double("world_events_crowd_health_cap", 3.0), Epsilon);
            }
            finally
            {
                WorldEventOverrides.DoubleSource = original;
            }
        }

        // ---- (b) ActiveOverrides()/ActiveOverridesSummary() -----------------------------------------------

        [TestMethod]
        public void ActiveOverrides_ListsOnlyNonZeroKeys_SortedByKey()
        {
            WithSources(
                new Dictionary<string, double> { ["world_events_crowd_health_cap"] = 5.0, ["world_events_boss_per_player"] = 0 },
                new Dictionary<string, long> { ["world_events_max_alive"] = 30 },
                () =>
                {
                    var active = WorldEventOverrides.ActiveOverrides();

                    CollectionAssert.AreEqual(
                        new[] { "world_events_crowd_health_cap=5", "world_events_max_alive=30" },
                        active.ToList());
                });
        }

        [TestMethod]
        public void ActiveOverrides_NothingSet_IsEmpty()
        {
            WithSources(null, null, () =>
            {
                Assert.AreEqual(0, WorldEventOverrides.ActiveOverrides().Count);
                Assert.AreEqual("world event dial overrides: none active", WorldEventOverrides.ActiveOverridesSummary());
            });
        }

        // ---- (c) def-level Effective* accessors consult the same seam -------------------------------------

        [TestMethod]
        public void CrowdHealthDef_Effective_HonoursOverrides()
        {
            var crowd = new CrowdHealthDef { StartAt = 8, PerParticipant = 0.1, Cap = 3.0 };

            WithSources(
                new Dictionary<string, double> { ["world_events_crowd_health_per_participant"] = 0.5, ["world_events_crowd_health_cap"] = 6.0 },
                new Dictionary<string, long> { ["world_events_crowd_health_start_at"] = 20 },
                () =>
                {
                    Assert.AreEqual(20, crowd.EffectiveStartAt);
                    Assert.AreEqual(0.5, crowd.EffectivePerParticipant, Epsilon);
                    Assert.AreEqual(6.0, crowd.EffectiveCap, Epsilon);

                    // Resolve(int) must actually USE the effective dials, not just expose them.
                    // participants=30: over = max(0, 30-20) = 10, mult = 1 + 0.5*10 = 6.0, capped at 6.0.
                    Assert.AreEqual(6.0, crowd.Resolve(30), Epsilon);
                });
        }

        [TestMethod]
        public void CrowdHealthDef_Effective_ZeroOverrides_MatchTheJsonValue()
        {
            var crowd = new CrowdHealthDef { StartAt = 8, PerParticipant = 0.1, Cap = 3.0 };

            WithSources(null, null, () =>
            {
                Assert.AreEqual(8, crowd.EffectiveStartAt);
                Assert.AreEqual(0.1, crowd.EffectivePerParticipant, Epsilon);
                Assert.AreEqual(3.0, crowd.EffectiveCap, Epsilon);
            });
        }

        [TestMethod]
        public void PaceDef_Effective_HonoursOverrides()
        {
            var pace = new PaceDef();   // 60 / 120 / +2 / +0.25 / cap 4.0

            WithSources(
                new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 30.0, ["world_events_pace_health_step"] = 0.5 },
                new Dictionary<string, long> { ["world_events_pace_quantity_step"] = 5 },
                () =>
                {
                    Assert.AreEqual(30.0, pace.EffectiveMinWaveSeconds, Epsilon);
                    Assert.AreEqual(120.0, pace.EffectiveMaxWaveSeconds, Epsilon, "maxWaveSeconds was not overridden, so it must still read the JSON value");
                    Assert.AreEqual(5, pace.EffectiveQuantityStep);
                    Assert.AreEqual(0.5, pace.EffectiveHealthStep, Epsilon);
                    Assert.AreEqual(4.0, pace.EffectiveHealthCap, Epsilon);
                });
        }

        // ---- round-2 review: the pace min/max PAIR guard --------------------------------------------------

        [TestMethod]
        public void PaceDef_EffectiveWindow_BothDialsIndividuallyValid_NotInverted_ReturnsThemUnchanged()
        {
            var pace = new PaceDef();   // JSON 60 / 120

            WithSources(new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 30.0 }, null, () =>
            {
                var window = pace.EffectiveWindow();

                Assert.AreEqual(30.0, window.Min, Epsilon);
                Assert.AreEqual(120.0, window.Max, Epsilon);
                Assert.IsFalse(window.WasInverted);
            });
        }

        [TestMethod]
        public void PaceDef_EffectiveWindow_MinOverrideAboveTheJsonMax_ClampsToAnOrderedPair()
        {
            // JSON max is 120 (untouched); a min override of 500 is individually a perfectly valid positive
            // finite dial, but read together with the untouched max it inverts the pair. Expected: max
            // stays 120 (it was never overridden), min is pulled down to max - 1 = 119.
            var pace = new PaceDef();

            WithSources(new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 500.0 }, null, () =>
            {
                var window = pace.EffectiveWindow();

                Assert.AreEqual(120.0, window.Max, Epsilon, "max was never overridden and must be untouched");
                Assert.AreEqual(119.0, window.Min, Epsilon, "min must clamp to max - 1");
                Assert.IsTrue(window.Min < window.Max, "the returned pair must always be strictly ordered");
                Assert.IsTrue(window.WasInverted);
            });
        }

        [TestMethod]
        public void PaceDef_EffectiveWindow_BothOverriddenAndInverted_ClampsToAnOrderedPair()
        {
            var pace = new PaceDef();

            WithSources(
                new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 200.0, ["world_events_pace_max_wave_seconds"] = 50.0 },
                null,
                () =>
                {
                    var window = pace.EffectiveWindow();

                    Assert.AreEqual(50.0, window.Max, Epsilon);
                    Assert.AreEqual(49.0, window.Min, Epsilon);
                    Assert.IsTrue(window.WasInverted);
                });
        }

        [TestMethod]
        public void PaceDef_EffectiveWindow_ClampWouldGoNegative_FloorsAtZero()
        {
            // max override of 0.5 with an inverted min: max - 1 would be negative, so min floors at 0.
            var pace = new PaceDef();

            WithSources(
                new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 10.0, ["world_events_pace_max_wave_seconds"] = 0.5 },
                null,
                () =>
                {
                    var window = pace.EffectiveWindow();

                    Assert.AreEqual(0.5, window.Max, Epsilon);
                    Assert.AreEqual(0.0, window.Min, Epsilon);
                });
        }

        [TestMethod]
        public void WorldEventPaceController_InvertedWindowOverride_StillEvaluatesUsingTheClampedPair()
        {
            // Behavioural proof, not just the pure PaceDef.EffectiveWindow() check above: a wave that
            // clears in 60s must be read against the CLAMPED window (min 119 / max 120), not the raw
            // inverted override (min 500 / max 120) - it is "too fast" (below 119) either way here, but the
            // point is Evaluate() actually goes through EffectiveWindow() and not the raw accessors.
            var pace = new PaceDef();

            WithSources(new Dictionary<string, double> { ["world_events_pace_min_wave_seconds"] = 500.0 }, null, () =>
            {
                var controller = new WorldEventPaceController(pace, runId: 999);

                controller.Evaluate(new[] { new PaceObservation(60.0, quantityWasMaxed: false) }, oldestAliveWaveIsStale: false);

                Assert.AreEqual(PaceDef.DefaultQuantityStep, controller.QuantityBonus,
                    "60s is below the clamped min (119s), so PushHarder must have fired");
            });
        }

        [TestMethod]
        public void WorldEventPaceController_PushHarder_UsesTheOverriddenQuantityStep()
        {
            var pace = new PaceDef();

            WithSources(null, new Dictionary<string, long> { ["world_events_pace_quantity_step"] = 9 }, () =>
            {
                var controller = new WorldEventPaceController(pace);

                controller.PushHarder(quantityWasMaxed: false);

                Assert.AreEqual(9, controller.QuantityBonus, "PushHarder must read the LIVE override, not the compiled-in PaceDef.QuantityStep (2)");
            });
        }

        [TestMethod]
        public void BossDef_Effective_HonoursOverrides()
        {
            var boss = new BossDef { PerPlayer = 0.15, HealthCap = 8.0, ThroughputFloorHealth = 100000, ThroughputCapHealth = 6400000,
                TargetKillSeconds = 300.0, ThroughputCalibration = 1.0, MinSampleSeconds = 120.0 };

            WithSources(
                new Dictionary<string, double> { ["world_events_boss_per_player"] = 0.3, ["world_events_boss_target_kill_seconds"] = 600.0 },
                new Dictionary<string, long> { ["world_events_boss_throughput_floor_health"] = 250000 },
                () =>
                {
                    Assert.AreEqual(0.3, boss.EffectivePerPlayer, Epsilon);
                    Assert.AreEqual(8.0, boss.EffectiveHealthCap, Epsilon, "cap was not overridden");
                    Assert.AreEqual(250000u, boss.EffectiveThroughputFloorHealth);
                    Assert.AreEqual(6400000u, boss.EffectiveThroughputCapHealth, "throughputCapHealth was not overridden");
                    Assert.AreEqual(600.0, boss.EffectiveTargetKillSeconds, Epsilon);
                    Assert.AreEqual(1.0, boss.EffectiveThroughputCalibration, Epsilon, "calibration was not overridden");
                    Assert.AreEqual(120.0, boss.EffectiveMinSampleSeconds, Epsilon, "minSampleSeconds was not overridden");

                    // ResolveHealthMult(powerSum) must actually use EffectivePerPlayer/EffectiveHealthCap.
                    // clamp(1 + 0.3*10, 1.0, 8.0) = 4.0.
                    Assert.AreEqual(4.0, boss.ResolveHealthMult(10.0), Epsilon);
                });
        }

        [TestMethod]
        public void SourceThemeDef_Effective_HonoursOverrides()
        {
            var theme = new SourceThemeDef
            {
                Id = "ambush",
                WaveIntervalSeconds = 45.0,
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 },
                OverflowPerChampion = 4,
                SyntheticEliteHealthMult = 2.0,
                SyntheticChampionHealthMult = 3.0
            };

            WithSources(
                new Dictionary<string, double>
                {
                    ["world_events_wave_interval_seconds"] = 20.0,
                    ["world_events_wave_count_per_participant"] = 3.0,
                    ["world_events_synthetic_elite_health_mult"] = 5.0
                },
                new Dictionary<string, long>
                {
                    ["world_events_max_alive"] = 50,
                    ["world_events_wave_count_base"] = 10,
                    ["world_events_wave_count_cap"] = 40,
                    ["world_events_overflow_per_champion"] = 8
                },
                () =>
                {
                    Assert.AreEqual(20.0, theme.EffectiveWaveIntervalSeconds, Epsilon);
                    Assert.AreEqual(50, theme.EffectiveMaxAlive);
                    Assert.AreEqual(10, theme.EffectiveWaveCountBase);
                    Assert.AreEqual(3.0, theme.EffectiveWaveCountPerParticipant, Epsilon);
                    Assert.AreEqual(40, theme.EffectiveWaveCountCap);
                    Assert.AreEqual(8, theme.EffectiveOverflowPerChampion);
                    Assert.AreEqual(5.0, theme.EffectiveSyntheticEliteHealthMult, Epsilon);
                    Assert.AreEqual(3.0, theme.EffectiveSyntheticChampionHealthMult, Epsilon, "champion mult was not overridden");

                    // ResolveWaveCount must actually use the effective base/perParticipant/cap:
                    // base 10 + ceil(3.0 * 5) = 25, under cap 40, above base 10 -> 25.
                    Assert.AreEqual(25, theme.ResolveWaveCount(5));
                    Assert.AreEqual(25, theme.ResolveWaveCountRaw(5));
                });
        }

        [TestMethod]
        public void SourceThemeDef_Effective_NullWaveCount_ResolvesToZero()
        {
            var theme = new SourceThemeDef { Id = "no_wavecount" };

            WithSources(null, null, () =>
            {
                Assert.AreEqual(0, theme.EffectiveWaveCountBase);
                Assert.AreEqual(0, theme.ResolveWaveCount(10));
                Assert.AreEqual(0, theme.ResolveWaveCountRaw(10));
            });
        }

        // ---- round-2 review: wave_count_cap below the effective base - no behaviour change, log-only ----

        [TestMethod]
        public void SourceThemeDef_Effective_CapBelowBase_ResolveWaveCountStillFloorsAtBase()
        {
            // wave_count_base overridden to 50, wave_count_cap overridden to 10 (below the new base). No
            // guard clamps this pair - ScaledCount.Resolve's own Math.Max(base, Math.Min(cap, raw)) already
            // wins regardless, so the compose-time Warn (WorldEventManager.Start) is diagnostic-only and
            // must never change what ResolveWaveCount actually returns.
            var theme = new SourceThemeDef
            {
                Id = "ambush",
                WaveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 }
            };

            WithSources(
                null,
                new Dictionary<string, long> { ["world_events_wave_count_base"] = 50, ["world_events_wave_count_cap"] = 10 },
                () =>
                {
                    Assert.AreEqual(50, theme.EffectiveWaveCountBase);
                    Assert.AreEqual(10, theme.EffectiveWaveCountCap);
                    Assert.IsTrue(theme.EffectiveWaveCountCap < theme.EffectiveWaveCountBase, "the scenario this test covers");

                    // ScaledCount.Resolve(base=50, per=1.5, cap=10, participants) = Max(50, Min(10, raw)) = 50 always.
                    Assert.AreEqual(50, theme.ResolveWaveCount(0));
                    Assert.AreEqual(50, theme.ResolveWaveCount(100));
                });
        }

        [TestMethod]
        public void ScaledCount_OverrideNeverLeaksIntoObjectiveHealth()
        {
            // The override lives at the SourceThemeDef call site, not on ScaledCount itself, so a waveCount
            // override must never move a SEPARATE ScaledCount instance (objectiveHealth) that happens to
            // share the class.
            var waveCount = new ScaledCount { Base = 4, PerParticipant = 1.5, Cap = 18 };
            var objectiveHealth = new ScaledCount { Base = 1000, PerParticipant = 0, Cap = 5000 };

            var theme = new SourceThemeDef { Id = "ambush", WaveCount = waveCount, ObjectiveHealth = objectiveHealth };

            WithSources(null, new Dictionary<string, long> { ["world_events_wave_count_base"] = 100 }, () =>
            {
                Assert.AreEqual(100, theme.EffectiveWaveCountBase);
                Assert.AreEqual(1000, objectiveHealth.Resolve(0), "objectiveHealth must be completely unaffected by the waveCount override");
            });
        }
    }
}
