using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

using ACE.Entity.Enum;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.MlDigsite;
using ACE.Server.Tests.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The ML digsite encounter system's pure arithmetic: the weighted type roll, the corruption meter, the
    /// player-count curve, the wave clocks, the reap predicate and the payout tiering.
    ///
    /// THIS CLASS IS ORDER-INDEPENDENT BY CONSTRUCTION, and that is deliberate rather than lucky. It never
    /// calls PropertyManager.GetBool/GetLong/GetDouble, so it seeds nothing and needs nothing seeded: every
    /// function under test takes its tunables as parameters (which is why MlDigsiteRules is written that
    /// way), and the registration assertions at the bottom read
    /// DefaultPropertyManager.DefaultXProperties directly - the CODE defaults - rather than going through
    /// the live cache, which throws for an uncached key with no shard DB. The repo has a documented case of
    /// a class passing in the full suite and failing alone because an earlier class happened to seed a key;
    /// there is no key for that to happen to here.
    /// </summary>
    [TestClass]
    public class MlDigsiteRulesTests
    {
        // ---- the weighted type roll ----------------------------------------------------------------------

        /// <summary>
        /// The shipped weights (round 13 feedback, ratified by the tester 2026-09-18) are 70 / 25 / 5, which
        /// happen to total exactly 100 - the missing Relaria dig is still NOT a branch of this function at
        /// all (it is the pre-existing boss-variant map, rebalanced to 1% via ml_treasure_relaria_chance).
        /// Normalising is what makes the coincidence irrelevant: these three split the whole of what
        /// actually reaches the roll, whatever they happen to sum to.
        /// </summary>
        [TestMethod]
        public void PickType_splits_the_shipped_weights_across_their_own_total_not_across_100()
        {
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(70, 25, 5, 0.0));
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(70, 25, 5, 0.69));
            Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(70, 25, 5, 0.70));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(70, 25, 5, 0.95));
        }

        [TestMethod]
        public void PickType_puts_each_boundary_on_the_later_branch()
        {
            // Exactly at a boundary the earlier branch's half-open interval has closed, so the draw belongs
            // to the next shape. Pinned because an off-by-one here silently reweights the whole feature.
            Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(70, 25, 5, 70.0 / 100.0));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(70, 25, 5, 95.0 / 100.0));
        }

        [TestMethod]
        public void PickType_falls_back_to_the_majority_shape_when_every_weight_is_zeroed()
        {
            // An operator who zeroed one weight by accident must still get encounters. Producing NONE would
            // be invisible - a completed dig would simply stop doing anything.
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(0, 0, 0, 0.0));
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(0, 0, 0, 0.999));
        }

        [TestMethod]
        public void PickType_treats_a_negative_weight_as_zero()
        {
            // -5 must not subtract from the total and drag the other boundaries around.
            Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(-5, 10, 0, 0.5));
        }

        [TestMethod]
        public void PickType_clamps_a_roll_outside_the_unit_interval()
        {
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(65, 27, 7, -1.0));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(65, 27, 7, 1.0));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(65, 27, 7, 5.0));
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(65, 27, 7, double.NaN));
        }

        [TestMethod]
        public void PickType_returns_the_only_weighted_shape_for_every_roll()
        {
            for (var i = 0; i < 100; i++)
            {
                var roll = i / 100.0;

                Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(0, 1, 0, roll), $"roll {roll}");
                Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(0, 0, 1, roll), $"roll {roll}");
            }
        }

        // ---- the corruption meter ------------------------------------------------------------------------

        [TestMethod]
        public void NextMeter_climbs_by_the_step_and_clamps_at_the_maximum()
        {
            Assert.AreEqual(2, MlDigsiteRules.NextMeter(0, 2, 100));
            Assert.AreEqual(100, MlDigsiteRules.NextMeter(99, 2, 100), "the meter overshot its own maximum");
            Assert.AreEqual(100, MlDigsiteRules.NextMeter(100, 2, 100));
        }

        [TestMethod]
        public void NextMeter_refuses_to_run_backwards_on_a_negative_step()
        {
            // The meter is defined as climbing while the priority mob lives; a negative tunable must not make
            // the encounter unlosable.
            Assert.AreEqual(50, MlDigsiteRules.NextMeter(50, -10, 100));
        }

        [TestMethod]
        public void NextMeter_is_zero_when_the_maximum_is_not_a_usable_bound()
        {
            Assert.AreEqual(0, MlDigsiteRules.NextMeter(50, 2, 0));
            Assert.AreEqual(0, MlDigsiteRules.NextMeter(50, 2, -1));
        }

        [TestMethod]
        public void MeterFailed_only_at_the_maximum()
        {
            Assert.IsFalse(MlDigsiteRules.MeterFailed(99, 100));
            Assert.IsTrue(MlDigsiteRules.MeterFailed(100, 100));
            Assert.IsTrue(MlDigsiteRules.MeterFailed(150, 100));
            Assert.IsFalse(MlDigsiteRules.MeterFailed(0, 0), "a zero maximum must not read as instantly failed");
        }

        [TestMethod]
        public void BandsCrossed_reports_a_band_only_on_the_tick_that_crosses_it()
        {
            CollectionAssert.AreEqual(new[] { 25 }, MlDigsiteRules.BandsCrossed(24, 26, 100).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(26, 30, 100).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(25, 30, 100).ToArray(),
                "25 was already crossed when the meter reached exactly 25");
        }

        [TestMethod]
        public void BandsCrossed_reports_every_band_a_single_large_tick_jumped()
        {
            // At the shipped 2-per-tick on a max of 100 this cannot happen, which is exactly why it is
            // tested: a retuned meter_per_tick must not silently swallow the bands it skipped over.
            CollectionAssert.AreEqual(new[] { 25, 50, 75 }, MlDigsiteRules.BandsCrossed(0, 80, 100).ToArray());
            CollectionAssert.AreEqual(new[] { 50, 75 }, MlDigsiteRules.BandsCrossed(30, 76, 100).ToArray());
        }

        [TestMethod]
        public void BandsCrossed_is_empty_for_a_tick_that_did_not_move_or_moved_back()
        {
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(50, 50, 100).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(80, 10, 100).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(0, 80, 0).ToArray());
        }

        [TestMethod]
        public void BandsCrossed_scales_its_thresholds_to_a_retuned_maximum()
        {
            // 25% of 200 is 50, not 25.
            CollectionAssert.AreEqual(new[] { 25 }, MlDigsiteRules.BandsCrossed(49, 51, 200).ToArray());
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.BandsCrossed(24, 26, 200).ToArray());
        }

        [TestMethod]
        public void DamageRatingFor_adds_three_rating_per_tick_at_the_shipped_step()
        {
            // DamageRating is an ADDITIVE PERCENTAGE rating, so "+3% damage per tick" is "+3 rating per
            // tick". This is the assertion that pins that reading of the mechanism.
            Assert.AreEqual(0, MlDigsiteRules.DamageRatingFor(0, 0, 0.03));
            Assert.AreEqual(3, MlDigsiteRules.DamageRatingFor(0, 1, 0.03));
            Assert.AreEqual(30, MlDigsiteRules.DamageRatingFor(0, 10, 0.03));
        }

        [TestMethod]
        public void DamageRatingFor_builds_on_the_authored_rating_rather_than_replacing_it()
        {
            Assert.AreEqual(25, MlDigsiteRules.DamageRatingFor(10, 5, 0.03));
        }

        [TestMethod]
        public void DamageRatingFor_is_recomputed_from_the_tick_count_so_it_grows_linearly_not_geometrically()
        {
            // The runtime calls this every tick with the creature's AUTHORED rating and the running tick
            // count. If it were ever changed to add to the CURRENT rating instead, the field's damage would
            // compound. Five separate ticks must land exactly where one call with ticks=5 lands.
            var authored = 12;

            var stepwise = authored;
            for (var tick = 1; tick <= 5; tick++)
                stepwise = MlDigsiteRules.DamageRatingFor(authored, tick, 0.03);

            Assert.AreEqual(MlDigsiteRules.DamageRatingFor(authored, 5, 0.03), stepwise);
            Assert.AreEqual(27, stepwise);
        }

        [TestMethod]
        public void DamageRatingFor_caps_the_growth_a_retuned_meter_could_produce()
        {
            Assert.AreEqual((int)MlDigsiteRules.MaxDamageRatingGrowth,
                MlDigsiteRules.DamageRatingFor(0, 100000, 0.03), "a retuned meter produced an uncapped one-shot field");
        }

        [TestMethod]
        public void DamageRatingFor_leaves_the_rating_alone_for_a_dead_or_broken_step()
        {
            Assert.AreEqual(7, MlDigsiteRules.DamageRatingFor(7, 0, 0.03));
            Assert.AreEqual(7, MlDigsiteRules.DamageRatingFor(7, -3, 0.03));
            Assert.AreEqual(7, MlDigsiteRules.DamageRatingFor(7, 5, 0.0));
            Assert.AreEqual(7, MlDigsiteRules.DamageRatingFor(7, 5, double.NaN));
        }

        // ---- the player-count curve ----------------------------------------------------------------------

        [TestMethod]
        public void ScaledSpawnCount_is_the_shipped_curve()
        {
            // max(base, min(cap, base + ceil(per * n))) at the shipped base 3 / per 1.5 / cap 12.
            Assert.AreEqual(3, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, 0));
            Assert.AreEqual(5, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, 1));
            Assert.AreEqual(6, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, 2));
            Assert.AreEqual(12, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, 10), "the cap did not bind");
        }

        [TestMethod]
        public void ScaledSpawnCount_treats_a_negative_participant_count_as_an_empty_site()
        {
            Assert.AreEqual(3, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, -4));
        }

        [TestMethod]
        public void ScaledSpawnCount_never_returns_less_than_the_base_even_under_a_smaller_cap()
        {
            Assert.AreEqual(3, MlDigsiteRules.ScaledSpawnCount(3, 1.5, 1, 5), "a mis-set cap emptied the wave");
        }

        // ---- the reap predicate --------------------------------------------------------------------------

        private static bool ShouldEnd(MlDigsiteState state, bool landblockLoaded, bool present,
            double sinceStartSeconds, double sincePresenceSeconds, out string reason)
        {
            return MlDigsiteRules.ShouldEnd(state, landblockLoaded, present,
                TimeSpan.FromSeconds(sinceStartSeconds), TimeSpan.FromSeconds(sincePresenceSeconds),
                TimeSpan.FromSeconds(1800), TimeSpan.FromSeconds(30), out reason);
        }

        [TestMethod]
        public void ShouldEnd_keeps_a_healthy_encounter_running()
        {
            Assert.IsFalse(ShouldEnd(MlDigsiteState.Active, true, true, 60, 0, out var reason));
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void ShouldEnd_ends_an_encounter_whose_landblock_unloaded()
        {
            // THE case no death hook can ever report: unloading destroys every creature without running
            // Die(), so without this the encounter would hold a concurrency slot forever.
            Assert.IsTrue(ShouldEnd(MlDigsiteState.Active, false, true, 10, 0, out var reason));
            Assert.AreEqual(MlDigsiteRules.EndReasons.LandblockUnloaded, reason);
        }

        [TestMethod]
        public void ShouldEnd_files_an_unloaded_encounter_as_unloaded_even_once_its_ttl_has_also_passed()
        {
            // Ordering: the landblock test comes first, so the reason stays the true one.
            Assert.IsTrue(ShouldEnd(MlDigsiteState.Active, false, false, 99999, 99999, out var reason));
            Assert.AreEqual(MlDigsiteRules.EndReasons.LandblockUnloaded, reason);
        }

        [TestMethod]
        public void ShouldEnd_expires_an_encounter_at_its_ttl()
        {
            Assert.IsTrue(ShouldEnd(MlDigsiteState.Active, true, true, 1800, 0, out var reason));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Expired, reason);
        }

        [TestMethod]
        public void ShouldEnd_files_a_site_with_nobody_alive_as_wiped_once_the_wipe_grace_has_elapsed()
        {
            // participantPresent is "anyone ALIVE in the radius" (MlDigsiteAudience skips the dead), so a
            // group that all died and a group that all left both land here, and both are paid the tier reached.
            Assert.IsFalse(ShouldEnd(MlDigsiteState.Active, true, false, 300, 29, out _),
                "the wipe grace was not honoured");

            Assert.IsTrue(ShouldEnd(MlDigsiteState.Active, true, false, 300, 30, out var reason));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Wiped, reason);
        }

        [TestMethod]
        public void ShouldEnd_never_wipes_an_encounter_someone_alive_is_standing_in()
        {
            Assert.IsFalse(ShouldEnd(MlDigsiteState.Active, true, true, 300, 99999, out _));
        }

        [TestMethod]
        public void ShouldEnd_treats_a_zero_ttl_or_grace_as_disabling_that_test()
        {
            Assert.IsFalse(MlDigsiteRules.ShouldEnd(MlDigsiteState.Active, true, true,
                TimeSpan.FromHours(50), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.FromSeconds(120), out _));

            Assert.IsFalse(MlDigsiteRules.ShouldEnd(MlDigsiteState.Active, true, false,
                TimeSpan.FromSeconds(10), TimeSpan.FromHours(50), TimeSpan.FromSeconds(1800), TimeSpan.Zero, out _));
        }

        [TestMethod]
        public void ShouldEnd_never_re_ends_an_already_ended_encounter()
        {
            Assert.IsFalse(ShouldEnd(MlDigsiteState.Ended, false, false, 99999, 99999, out _));
        }

        // ---- endless waves: clocks, tiers, checkpoints, scaling --------------------------------------------

        [TestMethod]
        public void WaveClockExpired_reports_the_time_limit_first_then_the_stall()
        {
            var limit = TimeSpan.FromSeconds(300);
            var stall = TimeSpan.FromSeconds(150);

            Assert.IsFalse(MlDigsiteRules.WaveClockExpired(TimeSpan.FromSeconds(299), TimeSpan.FromSeconds(149), limit, stall, out var none));
            Assert.IsNull(none);

            Assert.IsTrue(MlDigsiteRules.WaveClockExpired(TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(10), limit, stall, out var timeLimit));
            Assert.AreEqual(MlDigsiteRules.EndReasons.WaveTimeLimit, timeLimit);

            Assert.IsTrue(MlDigsiteRules.WaveClockExpired(TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(150), limit, stall, out var stalled));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Stalled, stalled);

            // both expired: the time limit is the one reported
            Assert.IsTrue(MlDigsiteRules.WaveClockExpired(TimeSpan.FromSeconds(400), TimeSpan.FromSeconds(400), limit, stall, out var both));
            Assert.AreEqual(MlDigsiteRules.EndReasons.WaveTimeLimit, both);
        }

        [TestMethod]
        public void WaveClockExpired_treats_zero_as_disabling_that_clock_only()
        {
            Assert.IsFalse(MlDigsiteRules.WaveClockExpired(TimeSpan.FromHours(5), TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(150), out _));
            Assert.IsTrue(MlDigsiteRules.WaveClockExpired(TimeSpan.FromHours(5), TimeSpan.FromSeconds(151), TimeSpan.Zero, TimeSpan.FromSeconds(150), out var reason));
            Assert.AreEqual(MlDigsiteRules.EndReasons.Stalled, reason);

            Assert.IsFalse(MlDigsiteRules.WaveClockExpired(TimeSpan.FromSeconds(1), TimeSpan.FromHours(5), TimeSpan.FromSeconds(300), TimeSpan.Zero, out _));
        }

        // ---- round 17: NearestWaveClock, and the status line's honest time clause ------------------------

        [TestMethod]
        public void NearestWaveClock_picks_whichever_of_the_two_is_closer_to_expiry()
        {
            var limit = TimeSpan.FromSeconds(300);
            var stall = TimeSpan.FromSeconds(150);

            // wave-time-limit nearer: 300 - 250 = 50s left vs 150 - 60 = 90s left
            var kind = MlDigsiteRules.NearestWaveClock(TimeSpan.FromSeconds(250), TimeSpan.FromSeconds(60), limit, stall, out var remaining);
            Assert.AreEqual(MlDigsiteWaveClockKind.WaveTimeLimit, kind);
            Assert.AreEqual(TimeSpan.FromSeconds(50), remaining);

            // stall STRICTLY nearer: 300 - 100 = 200s left vs 150 - 140 = 10s left
            kind = MlDigsiteRules.NearestWaveClock(TimeSpan.FromSeconds(100), TimeSpan.FromSeconds(140), limit, stall, out remaining);
            Assert.AreEqual(MlDigsiteWaveClockKind.Stall, kind);
            Assert.AreEqual(TimeSpan.FromSeconds(10), remaining);
        }

        [TestMethod]
        public void NearestWaveClock_excludes_a_disabled_clock_and_reports_None_when_both_are_off()
        {
            var stall = TimeSpan.FromSeconds(150);

            // wave-time-limit disabled: only the stall clock can be reported
            var kind = MlDigsiteRules.NearestWaveClock(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.Zero, stall, out var remaining);
            Assert.AreEqual(MlDigsiteWaveClockKind.Stall, kind);
            Assert.AreEqual(TimeSpan.FromSeconds(90), remaining);

            // both disabled: None, remaining zeroed
            kind = MlDigsiteRules.NearestWaveClock(TimeSpan.FromHours(5), TimeSpan.FromHours(5), TimeSpan.Zero, TimeSpan.Zero, out remaining);
            Assert.AreEqual(MlDigsiteWaveClockKind.None, kind);
            Assert.AreEqual(TimeSpan.Zero, remaining);
        }

        [TestMethod]
        public void NearestWaveClock_ties_go_to_the_wave_time_limit_never_to_stall()
        {
            var limit = TimeSpan.FromSeconds(300);
            var stall = TimeSpan.FromSeconds(150);

            // both leave exactly 50s: sinceWaveStarted=250 -> 50 left; sinceLastKill=100 -> 50 left
            var kind = MlDigsiteRules.NearestWaveClock(TimeSpan.FromSeconds(250), TimeSpan.FromSeconds(100), limit, stall, out var remaining);
            Assert.AreEqual(MlDigsiteWaveClockKind.WaveTimeLimit, kind);
            Assert.AreEqual(TimeSpan.FromSeconds(50), remaining);
        }

        /// <summary>
        /// The invariant BuildStatusLine leans on: NearestWaveClock's reported remaining time is at or below
        /// zero IF AND ONLY IF WaveClockExpired(the SAME inputs) says the run is over. The two must never
        /// disagree, across every combination of which clock (if either) is enabled and which (if either) has
        /// actually expired.
        /// </summary>
        [TestMethod]
        public void NearestWaveClock_agrees_with_WaveClockExpired_about_whether_a_run_is_over()
        {
            var limit = TimeSpan.FromSeconds(300);
            var stall = TimeSpan.FromSeconds(150);

            var cases = new (TimeSpan sinceWave, TimeSpan sinceKill, TimeSpan limitArg, TimeSpan stallArg)[]
            {
                (TimeSpan.FromSeconds(299), TimeSpan.FromSeconds(149), limit, stall),   // neither expired
                (TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(10), limit, stall),    // wave limit expired
                (TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(150), limit, stall),    // stall expired
                (TimeSpan.FromSeconds(400), TimeSpan.FromSeconds(400), limit, stall),   // both expired
                (TimeSpan.FromHours(5), TimeSpan.FromSeconds(1), TimeSpan.Zero, stall), // wave limit disabled, not stalled
                (TimeSpan.FromHours(5), TimeSpan.FromSeconds(151), TimeSpan.Zero, stall), // wave limit disabled, stalled
                (TimeSpan.FromHours(5), TimeSpan.FromHours(5), TimeSpan.Zero, TimeSpan.Zero), // both disabled
            };

            foreach (var (sinceWave, sinceKill, limitArg, stallArg) in cases)
            {
                var expired = MlDigsiteRules.WaveClockExpired(sinceWave, sinceKill, limitArg, stallArg, out _);

                var kind = MlDigsiteRules.NearestWaveClock(sinceWave, sinceKill, limitArg, stallArg, out var remaining);

                // None means no clock is even enabled - WaveClockExpired can only ever be false there, and
                // remaining is a meaningless zero rather than a real "already expired" reading.
                if (kind == MlDigsiteWaveClockKind.None)
                {
                    Assert.IsFalse(expired,
                        $"WaveClockExpired must be false with no clock enabled, sinceWave={sinceWave} sinceKill={sinceKill}");
                    continue;
                }

                Assert.AreEqual(expired, remaining <= TimeSpan.Zero,
                    $"disagreement for sinceWave={sinceWave} sinceKill={sinceKill} limit={limitArg} stall={stallArg}");
            }
        }

        [TestMethod]
        public void ProgressClause_appends_the_live_wave_count_only_when_waveLeft_is_supplied()
        {
            Assert.AreEqual("Wave 8 of 8, payout tier 85%, 3 left.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.WavesAndMiniBoss, 8, 85, 0, 0, waveLeft: 3));

            // null reproduces exactly today's clause, unchanged
            Assert.AreEqual("Wave 6 of 8, payout tier 70%.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.WavesAndMiniBoss, 6, 70, 0, 0, waveLeft: null));
        }

        [TestMethod]
        public void StatusLine_with_waveLeft_produces_the_exact_shipped_example()
        {
            Assert.AreEqual("[Digsite] Wave 8 of 8, payout tier 85%, 3 left. 1m07s remain. A kill resets the clock.",
                MlDigsiteRules.StatusLine(MlDigsiteType.WavesAndMiniBoss, 8, 85, 0, 0, TimeSpan.FromSeconds(67),
                    "A kill resets the clock.", waveLeft: 3));
        }

        [TestMethod]
        public void StatusLine_with_null_waveLeft_reproduces_todays_string_exactly()
        {
            Assert.AreEqual("[Digsite] Wave 1 of 8, payout tier 15%. 4m05s remain.",
                MlDigsiteRules.StatusLine(MlDigsiteType.WavesAndMiniBoss, 1, 15, 0, 0, TimeSpan.FromSeconds(245)));
        }

        // ---- BuildStatusLine's OWN wiring (the gate, the TTL-vs-wave-clock comparison, and passing
        // waveLeft through) - not just the pure StatusLine/NearestWaveClock arithmetic those already pin.
        // Driven against MlDigsiteManager's internal testable overload
        // (BuildStatusLine(encounter, now, ttl, waveTimeLimit, stallTimeout, corruptionKillsRequired, tierPct)),
        // which takes every tunable as a parameter so nothing here touches PropertyManager - see that
        // overload's own doc comment for why CurrentTierPercent's payout tunables are excluded the same way.

        private static MlDigsiteEncounter LiveWaveEncounter(MlDigsiteType type, DateTime t0, int liveCreatures)
        {
            var encounter = new MlDigsiteEncounter(1, 0x50000001, "Digger", type, new ACE.Entity.Position(), t0);

            // Places the opening wave synchronously, the same way MlDigsiteManager.OpenEncounter does: bumps
            // WavesSpawned to 1, starts both wave clocks at t0, and leaves NextWaveDueUtc null (a LIVE wave,
            // not a breather).
            encounter.NoteWaveSpawned(t0);

            for (var i = 0; i < liveCreatures; i++)
                encounter.TrackWaveCreature(TestCreatures.CreateQuestBearer());

            return encounter;
        }

        [TestMethod]
        public void BuildStatusLine_shows_the_live_wave_count_and_the_stall_warning_when_stall_is_nearest()
        {
            var t0 = DateTime.UtcNow;
            var encounter = LiveWaveEncounter(MlDigsiteType.WavesAndMiniBoss, t0, liveCreatures: 3);

            // wave limit 300s, stall 150s, ttl 600s; 140s in: wave remaining 160s, stall remaining 10s,
            // ttl remaining 460s - stall is STRICTLY nearest.
            var line = MlDigsiteManager.BuildStatusLine(encounter, t0.AddSeconds(140),
                TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(150),
                corruptionKillsRequired: 3, tierPct: 42);

            StringAssert.Contains(line, ", 3 left.");
            StringAssert.Contains(line, "0m10s remain.");
            StringAssert.Contains(line, "A kill resets the clock.");
        }

        [TestMethod]
        public void BuildStatusLine_during_the_breather_shows_neither_the_count_nor_the_stall_warning_and_uses_the_TTL()
        {
            var t0 = DateTime.UtcNow;
            var encounter = LiveWaveEncounter(MlDigsiteType.WavesAndMiniBoss, t0, liveCreatures: 3);

            // A scheduled next wave (NextWaveDueUtc != null) is the breather - the gate must not fire.
            encounter.ScheduleNextWave(t0.AddSeconds(301));

            var now = t0.AddSeconds(50);

            var line = MlDigsiteManager.BuildStatusLine(encounter, now,
                TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(150),
                corruptionKillsRequired: 3, tierPct: 42);

            Assert.IsFalse(line.Contains("left."), "no live-wave count during the breather");
            Assert.IsFalse(line.Contains("A kill resets the clock."), "no stall warning during the breather");

            // 600 - 50 = 550s left on the TTL, untouched by either wave clock.
            Assert.AreEqual("[Digsite] Wave 1 of 8, payout tier 42%. 9m10s remain.", line);
        }

        [TestMethod]
        public void BuildStatusLine_leaves_a_non_Waves_type_exactly_as_today()
        {
            var t0 = DateTime.UtcNow;
            var encounter = new MlDigsiteEncounter(1, 0x50000001, "Digger", MlDigsiteType.BossRush, new ACE.Entity.Position(), t0);

            var now = t0.AddSeconds(50);

            var line = MlDigsiteManager.BuildStatusLine(encounter, now,
                TimeSpan.FromSeconds(600), TimeSpan.FromSeconds(300), TimeSpan.FromSeconds(150),
                corruptionKillsRequired: 3, tierPct: 0);

            var expected = MlDigsiteRules.StatusLine(MlDigsiteType.BossRush, 0, 0, 0, 0, TimeSpan.FromSeconds(550));

            Assert.AreEqual(expected, line, "a Boss Rush encounter must never see the Waves-only gate");
        }

        [TestMethod]
        public void ParseTierTable_reads_the_shipped_default_in_wave_order()
        {
            var errors = new List<string>();
            var tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers, errors);

            Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
            CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5, 6, 7 }, tiers.Select(t => t.Wave).ToArray());
            CollectionAssert.AreEqual(new[] { 0.15, 0.27, 0.39, 0.51, 0.63, 0.75, 0.87 }, tiers.Select(t => t.Fraction).ToArray());
        }

        [TestMethod]
        public void ParseTierTable_sorts_tolerates_whitespace_and_reports_every_bad_entry()
        {
            var errors = new List<string>();
            var tiers = MlDigsiteRules.ParseTierTable(" 3 : 0.5 , 1:0.1,, nope, 0:0.2, 2:-1, 4:abc, 3:0.9, 5:1.5 ", errors);

            CollectionAssert.AreEqual(new[] { 1, 3, 5 }, tiers.Select(t => t.Wave).ToArray());
            CollectionAssert.AreEqual(new[] { 0.1, 0.5, 1.0 }, tiers.Select(t => t.Fraction).ToArray(), "5:1.5 must clamp to 1");

            // nope, 0:0.2, 2:-1, 4:abc, the repeated 3, and the clamped 5 - six reports, nothing silent
            Assert.AreEqual(6, errors.Count, string.Join("; ", errors));
        }

        [TestMethod]
        public void ParseTierTable_returns_empty_for_blank_input()
        {
            Assert.AreEqual(0, MlDigsiteRules.ParseTierTable(null).Count);
            Assert.AreEqual(0, MlDigsiteRules.ParseTierTable("   ").Count);
        }

        [TestMethod]
        public void WaveTierFraction_pays_the_highest_tier_reached_plus_the_checkpoint_bonus()
        {
            var tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers);

            Assert.AreEqual(0.15, MlDigsiteRules.WaveTierFraction(1, 0, tiers, 0.05), 1e-9);
            Assert.AreEqual(0.75, MlDigsiteRules.WaveTierFraction(6, 0, tiers, 0.05), 1e-9);

            // wave 6 with its two checkpoints (3 and 6) killed
            Assert.AreEqual(0.85, MlDigsiteRules.WaveTierFraction(6, 2, tiers, 0.05), 1e-9);

            // past the last row (7) stays at the last row's fraction with no bonus...
            Assert.AreEqual(0.87, MlDigsiteRules.WaveTierFraction(20, 0, tiers, 0.05), 1e-9);
            // ...and the bonus cannot push the total past 1
            Assert.AreEqual(1.0, MlDigsiteRules.WaveTierFraction(9, 6, tiers, 0.05), 1e-9);
        }

        [TestMethod]
        public void WaveTierFraction_never_pays_zero_once_wave_one_was_reached()
        {
            // a table that starts above wave 1 still pays its first row for wave 1
            var sparse = MlDigsiteRules.ParseTierTable("3:0.4,6:0.8");
            Assert.AreEqual(0.4, MlDigsiteRules.WaveTierFraction(1, 0, sparse, 0.0), 1e-9);
            Assert.AreEqual(0.4, MlDigsiteRules.WaveTierFraction(5, 0, sparse, 0.0), 1e-9);
            Assert.AreEqual(0.8, MlDigsiteRules.WaveTierFraction(6, 0, sparse, 0.0), 1e-9);

            // a hand-built zero tier is floored, never paid as nothing
            var zero = new List<MlDigsiteWaveTier> { new MlDigsiteWaveTier(1, 0.0) };
            Assert.AreEqual(MlDigsiteRules.MinimumReachedFraction, MlDigsiteRules.WaveTierFraction(1, 0, zero, 0.0), 1e-9);

            // an empty table reads the shipped default instead of zeroing the payout
            Assert.AreEqual(0.15, MlDigsiteRules.WaveTierFraction(1, 0, new List<MlDigsiteWaveTier>(), 0.05), 1e-9);
            Assert.AreEqual(0.15, MlDigsiteRules.WaveTierFraction(1, 0, null, 0.05), 1e-9);

            // only an encounter that never placed a wave pays nothing
            Assert.AreEqual(0.0, MlDigsiteRules.WaveTierFraction(0, 3, sparse, 0.05), 1e-9);
        }

        [TestMethod]
        public void WaveTierFraction_ignores_a_negative_or_broken_bonus()
        {
            var tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers);

            Assert.AreEqual(0.39, MlDigsiteRules.WaveTierFraction(3, 1, tiers, -0.5), 1e-9);
            Assert.AreEqual(0.39, MlDigsiteRules.WaveTierFraction(3, 1, tiers, double.NaN), 1e-9);
            Assert.AreEqual(0.39, MlDigsiteRules.WaveTierFraction(3, -4, tiers, 0.05), 1e-9);
        }

        [TestMethod]
        public void IsCheckpointWave_is_every_nth_wave_and_off_for_a_non_positive_interval()
        {
            var hits = Enumerable.Range(1, 10).Where(n => MlDigsiteRules.IsCheckpointWave(n, 3)).ToArray();
            CollectionAssert.AreEqual(new[] { 3, 6, 9 }, hits);

            Assert.IsTrue(MlDigsiteRules.IsCheckpointWave(1, 1));
            Assert.IsFalse(MlDigsiteRules.IsCheckpointWave(3, 0));
            Assert.IsFalse(MlDigsiteRules.IsCheckpointWave(3, -3));
            Assert.IsFalse(MlDigsiteRules.IsCheckpointWave(0, 3));
        }

        [TestMethod]
        public void WaveSpawnCount_adds_the_per_wave_step_on_top_of_the_crowd_curve()
        {
            // wave 1 is exactly the crowd curve: 3 + ceil(1.5 * 2) = 6 for two players
            Assert.AreEqual(MlDigsiteRules.ScaledSpawnCount(3, 1.5, 12, 2), MlDigsiteRules.WaveSpawnCount(3, 1, 1, 2, 1.5, 12));
            Assert.AreEqual(6, MlDigsiteRules.WaveSpawnCount(3, 1, 1, 2, 1.5, 12));

            // +1 per wave after the first
            Assert.AreEqual(11, MlDigsiteRules.WaveSpawnCount(3, 1, 6, 2, 1.5, 12));

            // a zero step leaves every wave on the crowd curve
            Assert.AreEqual(6, MlDigsiteRules.WaveSpawnCount(3, 0, 9, 2, 1.5, 12));
        }

        [TestMethod]
        public void WaveSpawnCount_is_bounded_by_the_landblock_ceiling_however_deep_the_run_goes()
        {
            Assert.AreEqual(MlDigsiteRules.MaxWaveSpawnCount, MlDigsiteRules.WaveSpawnCount(3, 1, 500, 10, 1.5, 12));
            Assert.AreEqual(MlDigsiteRules.MaxWaveSpawnCount, MlDigsiteRules.WaveSpawnCount(3, long.MaxValue, 2, 1, 1.5, 12));
        }

        [TestMethod]
        public void WaveHealthMultiplier_grows_per_wave_up_to_its_cap()
        {
            Assert.AreEqual(1.0, MlDigsiteRules.WaveHealthMultiplier(1, 0.10, 3.0), 1e-9);
            Assert.AreEqual(1.5, MlDigsiteRules.WaveHealthMultiplier(6, 0.10, 3.0), 1e-9);
            Assert.AreEqual(3.0, MlDigsiteRules.WaveHealthMultiplier(50, 0.10, 3.0), 1e-9);

            Assert.AreEqual(1.0, MlDigsiteRules.WaveHealthMultiplier(6, 0.0, 3.0), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.WaveHealthMultiplier(6, double.NaN, 3.0), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.WaveHealthMultiplier(6, 0.10, 0.5), 1e-9, "a cap below 1 must not shrink health");
        }

        [TestMethod]
        public void WaveDamageRatingAddend_grows_per_wave_up_to_its_cap()
        {
            Assert.AreEqual(0, MlDigsiteRules.WaveDamageRatingAddend(1, 5, 60));
            Assert.AreEqual(25, MlDigsiteRules.WaveDamageRatingAddend(6, 5, 60));
            Assert.AreEqual(60, MlDigsiteRules.WaveDamageRatingAddend(40, 5, 60));
            Assert.AreEqual(60, MlDigsiteRules.WaveDamageRatingAddend(3, long.MaxValue, 60));

            Assert.AreEqual(0, MlDigsiteRules.WaveDamageRatingAddend(6, 0, 60));
            Assert.AreEqual(0, MlDigsiteRules.WaveDamageRatingAddend(6, 5, 0));
        }

        // ---- Boss Rush and group rewards --------------------------------------------------------------------

        [TestMethod]
        public void BossRushFraction_pays_in_full_on_a_kill_and_by_health_removed_otherwise()
        {
            Assert.AreEqual(1.0, MlDigsiteRules.BossRushFraction(0.9, true, 0.10), 1e-9);

            // took it to 35% health: 65% removed
            Assert.AreEqual(0.65, MlDigsiteRules.BossRushFraction(0.35, false, 0.10), 1e-9);

            // barely scratched: the floor
            Assert.AreEqual(0.10, MlDigsiteRules.BossRushFraction(0.98, false, 0.10), 1e-9);
            Assert.AreEqual(0.10, MlDigsiteRules.BossRushFraction(1.0, false, 0.10), 1e-9);
        }

        [TestMethod]
        public void BossRushFraction_survives_broken_inputs()
        {
            Assert.AreEqual(0.10, MlDigsiteRules.BossRushFraction(double.NaN, false, 0.10), 1e-9, "a broken sample reads as never hit");
            Assert.AreEqual(1.0, MlDigsiteRules.BossRushFraction(-3.0, false, 0.10), 1e-9);
            Assert.AreEqual(0.0, MlDigsiteRules.BossRushFraction(1.0, false, double.NaN), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.BossRushFraction(0.5, false, 7.0), 1e-9, "a floor above 1 clamps to 1");
        }

        [TestMethod]
        public void NonOwnerShare_splits_the_pool_between_the_shipped_bounds()
        {
            Assert.AreEqual(0.75, MlDigsiteRules.NonOwnerShare(1, 1.5, 0.20, 0.75), 1e-9);
            Assert.AreEqual(0.75, MlDigsiteRules.NonOwnerShare(2, 1.5, 0.20, 0.75), 1e-9);
            Assert.AreEqual(0.50, MlDigsiteRules.NonOwnerShare(3, 1.5, 0.20, 0.75), 1e-9);
            Assert.AreEqual(0.25, MlDigsiteRules.NonOwnerShare(6, 1.5, 0.20, 0.75), 1e-9);
            Assert.AreEqual(0.20, MlDigsiteRules.NonOwnerShare(8, 1.5, 0.20, 0.75), 1e-9);
            Assert.AreEqual(0.20, MlDigsiteRules.NonOwnerShare(40, 1.5, 0.20, 0.75), 1e-9);
        }

        [TestMethod]
        public void NonOwnerShare_fails_safe_on_mis_set_bounds()
        {
            Assert.AreEqual(0.75, MlDigsiteRules.NonOwnerShare(0, 1.5, 0.20, 0.75), 1e-9, "no helpers reads as the max");
            Assert.AreEqual(0.5, MlDigsiteRules.NonOwnerShare(3, 1.5, 0.9, 0.5), 1e-9, "a min above the max collapses onto the max");
            Assert.AreEqual(0.20, MlDigsiteRules.NonOwnerShare(3, double.NaN, 0.20, 0.75), 1e-9, "a broken pool pays the floor");
            Assert.AreEqual(1.0, MlDigsiteRules.NonOwnerShare(1, 9.0, 0.2, 4.0), 1e-9, "a max above 1 clamps to 1");
        }

        [TestMethod]
        public void ChestEligible_always_pays_the_owner()
        {
            Assert.IsTrue(MlDigsiteRules.ChestEligible(true, 0, false, null, TimeSpan.FromSeconds(90)));
        }

        [TestMethod]
        public void ChestEligible_requires_a_helper_to_have_fought_be_online_and_been_seen_recently()
        {
            var window = TimeSpan.FromSeconds(90);

            Assert.IsTrue(MlDigsiteRules.ChestEligible(false, 1200, true, TimeSpan.FromSeconds(45), window));

            Assert.IsFalse(MlDigsiteRules.ChestEligible(false, 0, true, TimeSpan.FromSeconds(1), window), "no damage credit");
            Assert.IsFalse(MlDigsiteRules.ChestEligible(false, 1200, false, TimeSpan.FromSeconds(1), window), "offline");
            Assert.IsFalse(MlDigsiteRules.ChestEligible(false, 1200, true, TimeSpan.FromSeconds(91), window), "left too long ago");
            Assert.IsFalse(MlDigsiteRules.ChestEligible(false, 1200, true, null, window), "never seen by the reap");
        }

        [TestMethod]
        public void ChestEligible_pays_a_wiped_helper_who_is_no_longer_present_at_the_end()
        {
            // THE reason this is "seen within the window" and not "present at the end": a wipe ends with nobody
            // alive at the site. Last seen 40 s ago (30 s wipe grace + reap granularity) is inside 90 s.
            Assert.IsTrue(MlDigsiteRules.ChestEligible(false, 800, true, TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(90)));
        }

        [TestMethod]
        public void ChestEligible_treats_a_non_positive_window_as_no_presence_test()
        {
            Assert.IsTrue(MlDigsiteRules.ChestEligible(false, 10, true, null, TimeSpan.Zero));
        }

        [TestMethod]
        public void EncounterPayoutFraction_routes_each_type_through_its_own_rule()
        {
            var tunables = new MlDigsitePayoutTunables
            {
                Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
                CheckpointBonus = 0.05,
                BossRushMinFraction = 0.10,
                PartialFraction = 0.35,
            };

            // Waves: by the tier of the highest wave CLEARED, whatever the result label says
            var waves = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 7, 1, 1.0, false, 6, false);
            Assert.AreEqual(0.80, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss, waves, tunables), 1e-9);

            // Boss Rush: by health removed, or in full on a kill
            var rushTimeout = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 0, 0, 0.4, false, 0, false);
            Assert.AreEqual(0.60, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, rushTimeout, tunables), 1e-9);

            var rushKill = new MlDigsitePayoutSnapshot(MlDigsiteResult.FullClear, 0, 0, 0.0, true, 0, false);
            Assert.AreEqual(1.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, rushKill, tunables), 1e-9);

            // Corruption: round 16 - progress-based (Corrupted mobs killed / required), not binary. A WIN
            // (FullClear) pays 1.0 regardless of how the snapshot's other fields are filled; a bail with zero
            // kills pays nothing, the same rule Waves already had for a zero-progress bail.
            var corruptWin = new MlDigsitePayoutSnapshot(MlDigsiteResult.FullClear, 1, 0, 1.0, false, 0, false);
            var corruptBailedNoKills = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 1, 0, 1.0, false, 0, true);
            Assert.AreEqual(1.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptWin, tunables), 1e-9);
            Assert.AreEqual(0.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptBailedNoKills, tunables), 1e-9);

            // a non-bail zero-kill end (a TTL expiry, say) floors at the wave-1 tier instead of paying nothing
            var corruptTimedOutNoKills = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 1, 0, 1.0, false, 0, false);
            Assert.AreEqual(0.15, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptTimedOutNoKills, tunables), 1e-9);

            // one of two required kills: half progress
            Assert.AreEqual(0.5, MlDigsiteRules.CorruptionProgressFraction(1, 2, false, tunables.Tiers), 1e-9);
        }

        // ---- the instant-bail ruling: a wave counts once it is CLEARED ---------------------------------------

        private static MlDigsitePayoutTunables DefaultPayoutTunables() => new MlDigsitePayoutTunables
        {
            Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
            CheckpointBonus = 0.05,
            BossRushMinFraction = 0.10,
            PartialFraction = 0.35,
        };

        private static double WavesFraction(int placed, int cleared, int checkpoints, bool bailed)
            => MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss,
                new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, placed, checkpoints, 1.0, false, cleared, bailed),
                DefaultPayoutTunables());

        [TestMethod]
        public void A_bail_before_any_wave_is_cleared_pays_nothing()
        {
            // THE exploit: wave 1 is placed synchronously when the dig opens, so paying on the wave placed let a
            // bail straight after the dig collect the 15% tier (about 34M XP) for no fighting.
            Assert.AreEqual(0.0, WavesFraction(placed: 1, cleared: 0, checkpoints: 0, bailed: true), 1e-9);
        }

        [TestMethod]
        public void A_bail_after_clearing_wave_three_pays_tier_three()
        {
            // wave 4 is up and unfinished; wave 3 is the highest cleared, and that is what pays
            Assert.AreEqual(0.39, WavesFraction(placed: 4, cleared: 3, checkpoints: 0, bailed: true), 1e-9);

            // plus the checkpoint bonus once the wave-3 checkpoint is down
            Assert.AreEqual(0.44, WavesFraction(placed: 4, cleared: 3, checkpoints: 1, bailed: true), 1e-9);
        }

        [TestMethod]
        public void A_wipe_during_wave_one_keeps_the_wave_one_floor()
        {
            // The group engaged and lost, which is not the same as the owner walking away.
            Assert.AreEqual(0.15, WavesFraction(placed: 1, cleared: 0, checkpoints: 0, bailed: false), 1e-9);
        }

        [TestMethod]
        public void Bail_and_wipe_pay_the_same_once_a_wave_is_cleared()
        {
            // The RoZ intent - "bail and wipe both pay whatever tier was reached" - with a cleared wave as what
            // counts as reached. They diverge ONLY before the first clear.
            for (var cleared = 1; cleared <= 12; cleared++)
            {
                Assert.AreEqual(WavesFraction(cleared + 1, cleared, 0, bailed: false), WavesFraction(cleared + 1, cleared, 0, bailed: true), 1e-9,
                    $"bail and wipe disagree after clearing wave {cleared}");
            }
        }

        [TestMethod]
        public void A_waves_encounter_that_never_placed_a_wave_pays_nothing_either_way()
        {
            Assert.AreEqual(0.0, WavesFraction(placed: 0, cleared: 0, checkpoints: 0, bailed: false), 1e-9);
            Assert.AreEqual(0.0, WavesFraction(placed: 0, cleared: 0, checkpoints: 0, bailed: true), 1e-9);
        }

        [TestMethod]
        public void The_bail_flag_only_moves_the_waves_payout()
        {
            var tunables = DefaultPayoutTunables();

            var rushBailed = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 0, 0, 0.4, false, 0, true);
            Assert.AreEqual(0.60, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, rushBailed, tunables), 1e-9);

            // round 16: Corruption stopped being binary. A bail with zero Corrupted kills pays nothing, the
            // same "bail before any progress" rule Waves already had.
            var corruptBailed = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 1, 0, 1.0, false, 0, true);
            Assert.AreEqual(0.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptBailed, tunables), 1e-9);

            // but a bail AFTER at least one kill pays that progress, unlike a bail with none at all
            var corruptBailedWithProgress = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 1, 0, 1.0, false, 0, true, corruptedKills: 1);
            Assert.AreEqual(1.0 / 3.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptBailedWithProgress, tunables), 1e-9);
        }

        [TestMethod]
        public void Deliver_pays_nothing_and_tells_the_owner_when_the_fraction_is_zero()
        {
            var deliver = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs"), "public static void Deliver(");

            var zeroGuard = deliver.IndexOf("if (fraction <= 0.0)", StringComparison.Ordinal);
            var pay = deliver.IndexOf("PayParticipants(", StringComparison.Ordinal);
            var chests = deliver.IndexOf("SpawnChests(", StringComparison.Ordinal);

            Assert.IsTrue(zeroGuard > 0, "Deliver must stop on a zero fraction");
            Assert.IsTrue(zeroGuard < pay && zeroGuard < chests, "the zero guard must come before any XP or chest");
            StringAssert.Contains(deliver, "MlDigsiteRules.ZeroPayoutMessage(feathers.Count)");
            Assert.AreEqual("Nothing has been won here yet. The dig closes empty.", MlDigsiteRules.EmptyBailMessage);
        }

        [TestMethod]
        public void A_zero_payout_with_a_recorded_feather_still_gives_the_owner_a_feathers_only_chest()
        {
            // Ruling 2026-09-18: a recorded Kept Siraluun feather always reaches the owner, even from a bail that
            // pays nothing - in a chest holding the feathers and nothing else.
            var feather = new ACE.Entity.Models.PropertiesCreateList { WeenieClassId = 1005480, DestinationType = DestinationType.Treasure, StackSize = 1, Shade = 1.0f };
            var encounter = new MlDigsiteEncounter(1, 0x50000001, "Digger", MlDigsiteType.WavesAndMiniBoss, new ACE.Entity.Position(), DateTime.UtcNow);

            var plan = MlDigsiteRewards.FeatherOnlyChestPlan(encounter, new[] { feather });

            Assert.AreEqual(0x50000001u, plan.OwnerGuid, "the owner, and only the owner");
            Assert.IsTrue(plan.IsDigger);
            Assert.AreEqual(0.0, plan.Fraction, "fraction 0 is what keeps currency and loot out");
            Assert.IsNotNull(plan.Position);
            CollectionAssert.AreEqual(new uint[] { 1005480 }, plan.KeptSiraluunDrops.Select(r => r.WeenieClassId).ToArray());

            // Fraction 0 places no currency and no loot: every count BuildChest fills from is 0 (FillStack and
            // FillLoot return early on a count of 0), so the feathers are the whole chest.
            Assert.AreEqual(0, MlDigsiteRules.ScaleReward(5, plan.Fraction), "doubloons");
            Assert.AreEqual(0, MlDigsiteRules.ScaleReward(5, plan.Fraction), "trade notes");
            Assert.AreEqual(0, MlDigsiteRules.ScaleReward(15, plan.Fraction), "loot rolls");
            Assert.AreEqual(0, MlDigsiteRules.ScaleReward(229200000, plan.Fraction), "XP");

            // Deliver's zero branch: tells the owner, then spawns the feather chest only when feathers were
            // recorded, and returns before any XP or ordinary chest.
            var deliver = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs"), "public static void Deliver(");
            var zero = deliver.IndexOf("if (fraction <= 0.0)", StringComparison.Ordinal);
            var message = deliver.IndexOf("MlDigsiteRules.ZeroPayoutMessage(feathers.Count)", StringComparison.Ordinal);
            var guard = deliver.IndexOf("if (feathers.Count > 0)", StringComparison.Ordinal);
            var featherChest = deliver.IndexOf("SpawnChests(encounter, result, new[] { FeatherOnlyChestPlan(encounter, feathers) })", StringComparison.Ordinal);
            var pay = deliver.IndexOf("PayParticipants(", StringComparison.Ordinal);

            Assert.IsTrue(zero > 0 && zero < message && message < guard && guard < featherChest && featherChest < pay,
                $"zero={zero} message={message} guard={guard} featherChest={featherChest} pay={pay}");
        }

        [TestMethod]
        public void A_zero_payout_bail_says_closes_empty_only_when_no_feather_was_recorded()
        {
            Assert.AreEqual("Nothing has been won here yet. The dig closes empty.", MlDigsiteRules.ZeroPayoutMessage(0));
            Assert.AreEqual("Nothing else was won here, but the Siraluun's feather is yours.", MlDigsiteRules.ZeroPayoutMessage(1));
            Assert.AreEqual(MlDigsiteRules.FeatherOnlyBailMessage, MlDigsiteRules.ZeroPayoutMessage(3));
        }

        [TestMethod]
        public void The_bail_prompt_and_status_line_price_a_bail()
        {
            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs"), "public static int CurrentTierPercent(");

            StringAssert.Contains(body, "assumeBailed: true");
        }

        [TestMethod]
        public void KeptSiraluunRollRole_is_the_boss_and_the_checkpoint_only()
        {
            Assert.IsTrue(MlDigsiteRules.KeptSiraluunRollRole(MlDigsiteRole.Boss));
            Assert.IsTrue(MlDigsiteRules.KeptSiraluunRollRole(MlDigsiteRole.Checkpoint));

            Assert.IsFalse(MlDigsiteRules.KeptSiraluunRollRole(MlDigsiteRole.Wave));
            Assert.IsFalse(MlDigsiteRules.KeptSiraluunRollRole(MlDigsiteRole.Priority));
            Assert.IsFalse(MlDigsiteRules.KeptSiraluunRollRole(MlDigsiteRole.MiniBoss));
        }

        // ---- payout tiering ------------------------------------------------------------------------------

        [TestMethod]
        public void PayoutFraction_pays_a_full_clear_in_full_whatever_the_partial_tunable_says()
        {
            Assert.AreEqual(1.0, MlDigsiteRules.PayoutFraction(MlDigsiteResult.FullClear, 0.35), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.PayoutFraction(MlDigsiteResult.FullClear, 0.0), 1e-9);
        }

        [TestMethod]
        public void PayoutFraction_pays_a_partial_clear_and_a_failure_the_same_reduced_share()
        {
            // The design sets both at 35%. They are distinct results only so the log line and the wording
            // can tell a timed-out clear from a lost one.
            Assert.AreEqual(0.35, MlDigsiteRules.PayoutFraction(MlDigsiteResult.PartialClear, 0.35), 1e-9);
            Assert.AreEqual(0.35, MlDigsiteRules.PayoutFraction(MlDigsiteResult.Failed, 0.35), 1e-9);
        }

        [TestMethod]
        public void PayoutFraction_clamps_a_mis_set_tunable_into_the_unit_interval()
        {
            Assert.AreEqual(1.0, MlDigsiteRules.PayoutFraction(MlDigsiteResult.Failed, 2.0), 1e-9,
                "a failure was allowed to pay more than a win");
            Assert.AreEqual(0.0, MlDigsiteRules.PayoutFraction(MlDigsiteResult.Failed, -1.0), 1e-9);
            Assert.AreEqual(0.0, MlDigsiteRules.PayoutFraction(MlDigsiteResult.Failed, double.NaN), 1e-9);
        }

        [TestMethod]
        public void ScaleReward_scales_and_rounds()
        {
            Assert.AreEqual(100L, MlDigsiteRules.ScaleReward(100, 1.0));
            Assert.AreEqual(35L, MlDigsiteRules.ScaleReward(100, 0.35));

            // The shipped chest loot count, reduced: 15 * 0.35 = 5.25 -> 5.
            Assert.AreEqual(5L, MlDigsiteRules.ScaleReward(15, 0.35));
        }

        [TestMethod]
        public void ScaleReward_scales_the_shipped_xp_and_luminance_together()
        {
            Assert.AreEqual(416666667L, MlDigsiteRules.ScaleReward(416666667, 1.0));
            Assert.AreEqual(145833333L, MlDigsiteRules.ScaleReward(416666667, 0.35));
            Assert.AreEqual(66667L, MlDigsiteRules.ScaleReward(66667, 1.0));
            Assert.AreEqual(23333L, MlDigsiteRules.ScaleReward(66667, 0.35));
        }

        [TestMethod]
        public void ScaleReward_never_pays_a_negative_or_broken_amount()
        {
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(-100, 1.0));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(100, 0.0));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(100, -1.0));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(100, double.NaN));
            Assert.AreEqual(0L, MlDigsiteRules.ScaleReward(0, 1.0));
        }

        // ---- admission -----------------------------------------------------------------------------------

        [TestMethod]
        public void IsAdmitted_lets_an_ordinary_dig_open_an_encounter()
        {
            Assert.IsTrue(MlDigsiteRules.IsAdmitted(true, false, false, 3, 20, out var reason));
            Assert.IsNull(reason);
        }

        [TestMethod]
        public void IsAdmitted_refuses_each_of_the_four_gates_with_a_reason()
        {
            Assert.IsFalse(MlDigsiteRules.IsAdmitted(false, false, false, 0, 20, out var disabled));
            Assert.IsNotNull(disabled);

            Assert.IsFalse(MlDigsiteRules.IsAdmitted(true, true, false, 0, 20, out var already));
            Assert.IsNotNull(already);

            Assert.IsFalse(MlDigsiteRules.IsAdmitted(true, false, true, 0, 20, out var tooClose));
            Assert.IsNotNull(tooClose);

            Assert.IsFalse(MlDigsiteRules.IsAdmitted(true, false, false, 20, 20, out var capped));
            Assert.IsNotNull(capped);
        }

        [TestMethod]
        public void IsAdmitted_treats_a_zero_or_negative_cap_as_no_encounters_never_as_unlimited()
        {
            Assert.IsFalse(MlDigsiteRules.IsAdmitted(true, false, false, 0, 0, out _));
            Assert.IsFalse(MlDigsiteRules.IsAdmitted(true, false, false, 0, -5, out _));
        }

        // ---- the roster ------------------------------------------------------------------------------------

        [TestMethod]
        public void Roster_has_entries_for_every_role_an_encounter_can_ask_for()
        {
            foreach (MlDigsiteRole role in Enum.GetValues(typeof(MlDigsiteRole)))
            {
                Assert.IsTrue(MlDigsiteRoster.Entries(role).Count > 0,
                    $"role {role} has no roster entries; every spawn of that role would be refused");
            }
        }

        [TestMethod]
        public void Roster_never_draws_the_creature_an_owner_ruling_cut_from_the_island()
        {
            // BESTIARY.md section 5b, round 11 (2026-09-15): wcid 1002871 "Drumtaken Armored Tusker" came off
            // the island. Its weenie is kept defined but unreferenced, so drawing it here would put a
            // creature back into the world that a ruling took out of it.
            CollectionAssert.DoesNotContain(MlDigsiteRoster.AllWcids().ToArray(),
                MlDigsiteRoster.CutFromTheIslandWcid);
        }

        [TestMethod]
        public void Roster_draws_only_custom_fork_weenies_from_the_island_bestiary()
        {
            foreach (var wcid in MlDigsiteRoster.AllWcids())
            {
                Assert.IsTrue(wcid >= 1000000,
                    $"wcid {wcid} is a retail weenie; the digsite roster is meant to be the island's own bestiary");
            }
        }

        [TestMethod]
        public void Roster_keeps_the_apex_bosses_out_of_the_wave_and_miniboss_bands()
        {
            // A Boss Rush must not be reachable as a wave mob, and the mini-boss band must stay short of the
            // named apex units.
            var bosses = MlDigsiteRoster.Entries(MlDigsiteRole.Boss).Select(e => e.Wcid).ToHashSet();
            var waves = MlDigsiteRoster.Entries(MlDigsiteRole.Wave).Select(e => e.Wcid).ToHashSet();
            var minis = MlDigsiteRoster.Entries(MlDigsiteRole.MiniBoss).Select(e => e.Wcid).ToHashSet();

            Assert.AreEqual(0, bosses.Intersect(waves).Count(), "an apex boss is drawable as wave trash");
            Assert.AreEqual(0, bosses.Intersect(minis).Count(), "an apex boss is drawable as a mini-boss");
            Assert.AreEqual(0, waves.Intersect(minis).Count(), "a creature is both wave trash and a mini-boss");
        }

        [TestMethod]
        public void Roster_never_draws_the_relaria_boss_which_belongs_to_the_map_variant_alone()
        {
            // 1004120 Aun Relaria the Unburied is the boss-variant MAP's creature. Reaching it two ways would
            // make the rarest thing in the feature commonplace.
            CollectionAssert.DoesNotContain(MlDigsiteRoster.AllWcids().ToArray(), 1004120u);
        }

        [TestMethod]
        public void Roster_Pick_returns_an_entry_from_the_role_it_was_asked_for()
        {
            var rng = new Random(12345);

            foreach (MlDigsiteRole role in Enum.GetValues(typeof(MlDigsiteRole)))
            {
                var allowed = MlDigsiteRoster.Entries(role).Select(e => e.Wcid).ToHashSet();

                for (var i = 0; i < 50; i++)
                {
                    var picked = MlDigsiteRoster.Pick(role, rng);

                    Assert.IsNotNull(picked, $"role {role} returned no pick");
                    Assert.IsTrue(allowed.Contains(picked.Value.Wcid),
                        $"role {role} drew wcid {picked.Value.Wcid}, which is not in its own band");
                }
            }
        }

        // ---- the Kept Siraluun rare miniboss -------------------------------------------------------------

        [TestMethod]
        public void Roster_KeptSiraluun_carries_all_eight_shipped_weenies_at_their_base_variants_levels()
        {
            // wcid, Level (PropertyInt 25, unchanged from the base variant - see each weenie's own SQL
            // header) per Content/sql/weenies/1005470-1005477.
            var expected = new Dictionary<uint, int>
            {
                { 1005470u, 215 }, // Kept Kithless Siraluun
                { 1005471u, 240 }, // Kept Badlands Siraluun
                { 1005472u, 185 }, // Kept Littoral Siraluun
                { 1005473u, 185 }, // Kept Marsh Siraluun
                { 1005474u, 200 }, // Kept Strand Siraluun
                { 1005475u, 185 }, // Kept Tidal Siraluun
                { 1005476u, 215 }, // Kept Timber Siraluun
                { 1005477u, 240 }, // Kept Untamed Siraluun
            };

            var actual = MlDigsiteRoster.KeptSiraluun;

            Assert.AreEqual(expected.Count, actual.Count, "the Kept Siraluun table must carry exactly the eight shipped weenies");

            foreach (var entry in actual)
            {
                Assert.IsTrue(expected.TryGetValue(entry.Wcid, out var level),
                    $"wcid {entry.Wcid} is not one of the eight shipped Kept Siraluun weenies");
                Assert.AreEqual(level, entry.Level, $"wcid {entry.Wcid} Level moved from its base variant's");
            }
        }

        [TestMethod]
        public void RollKeptSiraluun_never_substitutes_at_a_zero_chance()
        {
            for (var i = 0; i < 100; i++)
                Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(0.0, i / 100.0), $"roll {i / 100.0}");
        }

        [TestMethod]
        public void RollKeptSiraluun_always_substitutes_at_a_chance_of_one()
        {
            // roll is a uniform draw in [0, 1), the same convention PickType's roll uses, so it is always
            // strictly less than a clamped chance of 1.0.
            for (var i = 0; i < 1000; i++)
                Assert.IsTrue(MlDigsiteRules.RollKeptSiraluun(1.0, i / 1000.0), $"roll {i / 1000.0}");
        }

        [TestMethod]
        public void RollKeptSiraluun_at_the_shipped_default_substitutes_roughly_one_roll_in_twenty()
        {
            const double chance = 0.05;

            Assert.IsTrue(MlDigsiteRules.RollKeptSiraluun(chance, 0.0));
            Assert.IsTrue(MlDigsiteRules.RollKeptSiraluun(chance, 0.0499));
            Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(chance, 0.05));
            Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(chance, 0.5));
        }

        [TestMethod]
        public void RollKeptSiraluun_treats_a_non_finite_or_negative_chance_as_off()
        {
            Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(-1.0, 0.0));
            Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(double.NaN, 0.0));
            Assert.IsFalse(MlDigsiteRules.RollKeptSiraluun(0.5, double.NaN));
        }

        private static readonly IReadOnlyList<MlDigsiteRosterEntry> KeptSiraluunPool = new[]
        {
            new MlDigsiteRosterEntry(1005472, 185, "Kept Littoral Siraluun"),
            new MlDigsiteRosterEntry(1005474, 200, "Kept Strand Siraluun"),
            new MlDigsiteRosterEntry(1005470, 215, "Kept Kithless Siraluun"),
            new MlDigsiteRosterEntry(1005471, 240, "Kept Badlands Siraluun"),
        };

        [TestMethod]
        public void PickKeptSiraluun_prefers_the_entry_sharing_the_ordinary_picks_level()
        {
            var rng = new Random(9001);

            for (var i = 0; i < 100; i++)
            {
                var picked = MlDigsiteRules.PickKeptSiraluun(KeptSiraluunPool, 200, rng);

                Assert.AreEqual(1005474u, picked.Wcid, "the only pool entry at Level 200 must always win when it exists");
            }
        }

        [TestMethod]
        public void PickKeptSiraluun_falls_back_to_a_uniform_draw_across_the_whole_pool_when_no_level_matches()
        {
            var rng = new Random(777);
            var seen = new HashSet<uint>();

            for (var i = 0; i < 200; i++)
                seen.Add(MlDigsiteRules.PickKeptSiraluun(KeptSiraluunPool, 205, rng).Wcid);

            // 205 matches nothing in the pool (185/200/215/240), so every entry must be reachable.
            CollectionAssert.AreEquivalent(KeptSiraluunPool.Select(e => e.Wcid).ToArray(), seen.ToArray());
        }

        [TestMethod]
        public void PickKeptSiraluun_refuses_an_empty_pool()
        {
            Assert.ThrowsExactly<ArgumentException>(() =>
                MlDigsiteRules.PickKeptSiraluun(Array.Empty<MlDigsiteRosterEntry>(), 200, new Random(1)));
        }

        // ---- crowd scaling --------------------------------------------------------------------------------
        //
        // The digsite system's own tunables, at their shipped defaults (per_player 0.15, health_cap 3.0,
        // dr_per_player 10, dr_cap 100) - deliberately the SAME numbers the Bluespire ladder's D6 crowd
        // scaling ships, but through the digsite system's OWN keys, with no start-at floor (D6's dial has
        // one; the digsite tunables name none, so scaling begins from the first participant).

        [TestMethod]
        public void CrowdHealthMultiplier_scales_from_the_first_participant_with_no_start_at_floor()
        {
            const double perPlayer = 0.15;
            const double cap = 3.0;

            Assert.AreEqual(1.0, MlDigsiteRules.CrowdHealthMultiplier(0, perPlayer, cap), 1e-9, "nobody present is unscaled");
            Assert.AreEqual(1.15, MlDigsiteRules.CrowdHealthMultiplier(1, perPlayer, cap), 1e-9, "a solo participant already scales - no start-at floor");
            Assert.AreEqual(1.30, MlDigsiteRules.CrowdHealthMultiplier(2, perPlayer, cap), 1e-9);
            Assert.AreEqual(cap, MlDigsiteRules.CrowdHealthMultiplier(20, perPlayer, cap), 1e-9, "the cap binds well short of 20 participants");
        }

        [TestMethod]
        public void CrowdDamageRatingAddend_scales_from_the_first_participant_with_no_start_at_floor()
        {
            const long perPlayer = 10;
            const long cap = 100;

            Assert.AreEqual(0, MlDigsiteRules.CrowdDamageRatingAddend(0, perPlayer, cap));
            Assert.AreEqual(10, MlDigsiteRules.CrowdDamageRatingAddend(1, perPlayer, cap), "a solo participant already adds rating - no start-at floor");
            Assert.AreEqual(20, MlDigsiteRules.CrowdDamageRatingAddend(2, perPlayer, cap));
            Assert.AreEqual(cap, MlDigsiteRules.CrowdDamageRatingAddend(50, perPlayer, cap), "the cap binds at 10 participants and holds");
        }

        // ---- the Kept Siraluun's feather, delivered through the chest -------------------------------------

        private static PropertiesCreateList CreateListRow(uint wcid, float shade, DestinationType destination = DestinationType.Contain | DestinationType.Treasure, int stackSize = 0)
            => new PropertiesCreateList { WeenieClassId = wcid, Shade = shade, DestinationType = destination, StackSize = stackSize };

        /// <summary>The exact shape every Kept Siraluun weenie ships (Content/sql/weenies/1005470-1005477): a feather at 5%, paired with a shade-0.95 no-drop row.</summary>
        private static List<PropertiesCreateList> FeatherPair(float featherShade = 0.05f)
            => new List<PropertiesCreateList>
            {
                CreateListRow(11363, featherShade),
                CreateListRow(0, 1.0f - featherShade),
            };

        [TestMethod]
        public void ResolveCreateListDrops_a_shade_one_row_is_always_selected()
        {
            var rows = new List<PropertiesCreateList> { CreateListRow(11363, 1.0f) };

            for (var i = 0; i < 50; i++)
            {
                var roll = i / 50.0;
                var drops = MlDigsiteRules.ResolveCreateListDrops(rows, () => roll);

                Assert.AreEqual(1, drops.Count, $"roll {roll}");
                Assert.AreEqual(11363u, drops[0].WeenieClassId);
            }
        }

        [TestMethod]
        public void ResolveCreateListDrops_the_shipped_5_percent_feather_pair_stays_5_percent()
        {
            var rows = FeatherPair();

            // Below the 5% threshold: the feather row wins.
            Assert.AreEqual(1, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.0).Count);
            Assert.AreEqual(11363u, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.0)[0].WeenieClassId);
            Assert.AreEqual(1, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.0499).Count);

            // Above 5% (kept clear of the exact float-promoted boundary of a 0.05f shade): the no-drop row
            // (wcid 0) wins and is filtered out of the result entirely.
            Assert.AreEqual(0, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.06).Count);
            Assert.AreEqual(0, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.5).Count);
            Assert.AreEqual(0, MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.999).Count);
        }

        [TestMethod]
        public void ResolveCreateListDrops_never_returns_the_wcid_zero_no_drop_placeholder()
        {
            var rows = new List<PropertiesCreateList> { CreateListRow(0, 1.0f) };

            var drops = MlDigsiteRules.ResolveCreateListDrops(rows, () => 0.0);

            Assert.AreEqual(0, drops.Count);
        }

        [TestMethod]
        public void ResolveCreateListDrops_keeps_a_shade_zero_or_non_treasure_row_unconditionally()
        {
            var containOnly = CreateListRow(11363, 0.0f, DestinationType.Contain);
            var shadeZeroTreasure = CreateListRow(11364, 0.0f, DestinationType.Contain | DestinationType.Treasure);

            var drops = MlDigsiteRules.ResolveCreateListDrops(
                new List<PropertiesCreateList> { containOnly, shadeZeroTreasure }, () => 0.999);

            CollectionAssert.AreEquivalent(new uint[] { 11363u, 11364u }, drops.Select(d => d.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void ResolveCreateListDrops_draws_a_fresh_roll_for_each_new_chunk()
        {
            // Two independent disjoint pairs back to back: totalProbability reaches 1.0 after the first
            // pair, so the second pair must draw its OWN roll rather than reusing the first chunk's.
            var rows = new List<PropertiesCreateList>
            {
                CreateListRow(1u, 1.0f), // chunk 1: always wins
                CreateListRow(2u, 0.05f), // chunk 2, row A
                CreateListRow(0u, 0.95f), // chunk 2, row B (no-drop)
            };

            var rolls = new Queue<double>(new[] { 0.0, 0.0 }); // chunk 1 roll, chunk 2 roll
            var drops = MlDigsiteRules.ResolveCreateListDrops(rows, () => rolls.Dequeue());

            CollectionAssert.AreEqual(new uint[] { 1u, 2u }, drops.Select(d => d.WeenieClassId).ToArray());
        }

        [TestMethod]
        public void ResolveCreateListDrops_handles_null_or_empty_input()
        {
            Assert.AreEqual(0, MlDigsiteRules.ResolveCreateListDrops(null, () => 0.0).Count);
            Assert.AreEqual(0, MlDigsiteRules.ResolveCreateListDrops(new List<PropertiesCreateList>(), () => 0.0).Count);
        }

        [TestMethod]
        public void RecordKeptSiraluunDrop_is_restricted_to_kept_siraluun_wcids_only()
        {
            // An ordinary Drumtaken mini-boss (e.g. 1002848, MlDigsiteRole.MiniBoss) must stay corpseless
            // with NO drop - only the eight Kept Siraluun wcids ever feed ResolveCreateListDrops.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var resolve = PooledLootSourceText.MethodBody(src,
                "private static IReadOnlyList<PropertiesCreateList> ResolveKeptSiraluunDrops(MlDigsiteEncounter encounter, Creature creature)");

            StringAssert.Contains(resolve, "keptSiraluunWcids.Contains(creature.WeenieClassId)");
            StringAssert.Contains(src, "new HashSet<uint>(MlDigsiteRoster.KeptSiraluun.Select(e => e.Wcid))");
        }

        [TestMethod]
        public void The_death_hook_records_the_drop_in_the_same_call_as_the_death_and_never_finishes()
        {
            // The feather ordering: the drop is resolved first and handed to NoteCreatureDeath, which records it
            // atomically with the death and refuses once Ended. A separate "add drop" call after the death note
            // is exactly the window the #1213 review found.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var hook = PooledLootSourceText.MethodBody(src, "public static void OnEncounterCreatureDied(Creature creature)");

            var resolve = hook.IndexOf("ResolveKeptSiraluunDrops(encounter, creature)", StringComparison.Ordinal);
            var note = hook.IndexOf("encounter.NoteCreatureDeath(creature.Guid.Full, now, creature, drops, out var waveNowEmpty)", StringComparison.Ordinal);

            Assert.IsTrue(resolve >= 0 && note > resolve, "the drop must be resolved first and passed into NoteCreatureDeath");
            Assert.IsFalse(hook.Contains("AddKeptSiraluunDrop"), "no separate drop write may follow the death note");
            Assert.IsFalse(hook.Contains("Finish("), "the death hook only requests an end; the tick finishes it");
            StringAssert.Contains(hook, "TryRequestEnd(");
        }

        [TestMethod]
        public void Finish_has_exactly_one_caller_and_it_is_the_tick()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            StringAssert.Contains(src, "private static void Finish(MlDigsiteEncounter encounter, MlDigsiteResult result, string reason)");

            var calls = System.Text.RegularExpressions.Regex.Matches(src, @"(?<![A-Za-z_.])Finish\(encounter").Count;
            Assert.AreEqual(1, calls, "Finish must be called from exactly one place");

            var tick = PooledLootSourceText.MethodBody(src, "public static void Tick()");
            StringAssert.Contains(tick, "if (encounter.TryGetEndRequest(out var reason, out var result))");
            StringAssert.Contains(tick, "Finish(encounter, result, reason);");

            // the end request is consumed after the landblock tick: WorldManager must keep the order
            var world = PooledLootSourceText.Read("Source/ACE.Server/Managers/WorldManager.cs");
            var landblocks = world.IndexOf("LandblockManager.Tick(Timers.PortalYearTicks);", StringComparison.Ordinal);
            var digsite = world.IndexOf("ACE.Server.MlDigsite.MlDigsiteManager.Tick();", StringComparison.Ordinal);
            Assert.IsTrue(landblocks >= 0 && digsite > landblocks, "MlDigsiteManager.Tick must run after LandblockManager.Tick");
        }

        [TestMethod]
        public void The_kept_siraluun_drop_reaches_the_chest_on_every_outcome_not_scaled_by_fraction()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var buildChest = PooledLootSourceText.MethodBody(src,
                "private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)");
            var fillKept = PooledLootSourceText.MethodBody(src,
                "private static int FillKeptSiraluunDrops(MlDigsiteEncounter encounter, Chest chest, IReadOnlyList<PropertiesCreateList> drops, ref int placement)");

            // BuildChest runs once per Deliver call regardless of result (full/partial/fail all reach it -
            // SpawnChest is called unconditionally from Deliver), and FillKeptSiraluunDrops takes no fraction
            // parameter at all, so it cannot be scaling the recorded drop the way FillStack/FillLoot do.
            StringAssert.Contains(buildChest, "FillKeptSiraluunDrops(encounter, chest, plan.KeptSiraluunDrops, ref placement)");
            Assert.IsFalse(fillKept.Contains("fraction"),
                "the Kept Siraluun drop must not be scaled by the payout fraction - it always pays");

            // ... and it goes into the OWNER's chest only, never split into a helper's
            var plan = PooledLootSourceText.MethodBody(src,
                "private static List<ChestPlan> PlanChests(MlDigsiteEncounter encounter, double fraction, IReadOnlyList<uint> eligible,");
            StringAssert.Contains(plan, "KeptSiraluunDrops = isDigger ? keptDrops : null,");
            StringAssert.Contains(plan, "Fraction = isDigger ? fraction : fraction * share,");
        }

        [TestMethod]
        public void Every_chest_is_stamped_to_its_own_player()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var build = PooledLootSourceText.MethodBody(src,
                "private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)");

            StringAssert.Contains(build, "chest.P_DungeonCacheOwnerGuid = plan.OwnerGuid;");
            Assert.IsFalse(build.Contains("P_DungeonCacheOwnerGuid = encounter.DiggerGuid"), "a helper's chest must not be stamped to the digger");
        }

        [TestMethod]
        public void Deliver_pays_through_the_one_payout_rule_and_the_eligibility_selector()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var deliver = PooledLootSourceText.MethodBody(src,
                "public static void Deliver(MlDigsiteEncounter encounter, MlDigsiteResult result)");

            StringAssert.Contains(deliver, "MlDigsiteRules.EncounterPayoutFraction(encounter.Type, snapshot, MlDigsiteTunables.Payout)");
            StringAssert.Contains(deliver, "MlDigsiteRules.SelectEligible(encounter.DiggerGuid, encounter.DamageCreditSnapshot(),");
            Assert.IsFalse(src.Contains("MlDigsiteAudience.Participants"), "the audience scan must no longer decide who is paid");
        }

        // ---- /digsite bail --------------------------------------------------------------------------------

        [TestMethod]
        public void BailRefusal_lets_the_owner_bail_and_refuses_everyone_else()
        {
            Assert.IsNull(MlDigsiteRules.BailRefusal(true, null));
            Assert.IsNull(MlDigsiteRules.BailRefusal(true, "Someone Else"), "an owner standing in another site still owns their own");

            Assert.AreEqual("Only Digger Dan, who dug this site, can call it off.", MlDigsiteRules.BailRefusal(false, "Digger Dan"));
            Assert.AreEqual("You have no digsite encounter to call off.", MlDigsiteRules.BailRefusal(false, null));
        }

        [TestMethod]
        public void BailPrompt_names_the_share_a_bail_would_pay()
        {
            Assert.AreEqual("Call off your digsite encounter now? The fight ends and pays what you have reached: 65% of the full cache.",
                MlDigsiteRules.BailPrompt(65));
            StringAssert.Contains(MlDigsiteRules.BailPrompt(250), "100%");
        }

        [TestMethod]
        public void Bail_is_owner_only_confirmed_first_and_only_ever_requests_the_end()
        {
            var commands = PooledLootSourceText.Read("Source/ACE.Server/Command/Handlers/MlDigsiteCommands.cs");
            var handleBail = PooledLootSourceText.MethodBody(commands, "private static void HandleBail(Player player)");

            var refusal = handleBail.IndexOf("MlDigsiteRules.BailRefusal(", StringComparison.Ordinal);
            var confirm = handleBail.IndexOf("player.ConfirmationManager.EnqueueSend(confirmation, prompt)", StringComparison.Ordinal);

            Assert.IsTrue(refusal >= 0 && confirm > refusal, "a non-owner must be refused before any dialog is sent");
            StringAssert.Contains(handleBail, "MlDigsiteManager.FindOwnedEncounter(player)");
            Assert.IsFalse(handleBail.Contains("TryRequestEnd"), "the command itself must end nothing; only the Yes does");

            var confirmation = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/Confirmation_DigsiteBail.cs");
            var process = PooledLootSourceText.MethodBody(confirmation, "public override void ProcessConfirmation(bool response, bool timeout = false)");

            StringAssert.Contains(process, "if (!response)");
            StringAssert.Contains(process, "Confirmation_ThreadGroupStart.IsLate(SentUtc, DateTime.UtcNow, timeout)");
            StringAssert.Contains(process, "player.EnqueueAction(");

            var manager = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var requestBail = PooledLootSourceText.MethodBody(manager, "public static string RequestBail(Player player, uint encounterId)");

            StringAssert.Contains(requestBail, "encounter.DiggerGuid != player.Guid.Full", "ownership is re-checked on the answer");
            StringAssert.Contains(requestBail, "encounter.TryRequestEnd(MlDigsiteRules.EndReasons.Bailed, result)");
            Assert.IsFalse(requestBail.Contains("Finish("), "a bail only requests; the tick finishes");
        }

        // ---- group eligibility ----------------------------------------------------------------------------

        [TestMethod]
        public void SelectEligible_pays_the_owner_first_and_every_qualifying_helper()
        {
            const uint owner = 100, a = 7, b = 9;
            var credit = new Dictionary<uint, float> { { b, 50f }, { a, 900f }, { owner, 300f } };

            var eligible = MlDigsiteRules.SelectEligible(owner, credit, g => true, g => TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(90));

            CollectionAssert.AreEqual(new[] { owner, a, b }, eligible, "owner first, then helpers in guid order, the owner never twice");
        }

        [TestMethod]
        public void SelectEligible_pays_nothing_to_a_helper_who_dealt_no_damage()
        {
            const uint owner = 100;

            // player 8 stood at the site the whole fight but never hit anything: not in the ledger at all
            var credit = new Dictionary<uint, float> { { 7, 400f } };

            var eligible = MlDigsiteRules.SelectEligible(owner, credit, g => true, g => TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(90));

            CollectionAssert.AreEqual(new uint[] { owner, 7 }, eligible);
        }

        [TestMethod]
        public void SelectEligible_drops_an_offline_or_long_gone_helper_but_always_keeps_the_owner()
        {
            const uint owner = 100, offline = 7, gone = 8, stayed = 9;
            var credit = new Dictionary<uint, float> { { offline, 10f }, { gone, 10f }, { stayed, 10f } };

            var eligible = MlDigsiteRules.SelectEligible(owner, credit,
                g => g != offline && g != owner,
                g => g == gone ? TimeSpan.FromSeconds(300) : TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(90));

            CollectionAssert.AreEqual(new uint[] { owner, stayed }, eligible, "the owner is paid even offline and uncredited");
        }

        [TestMethod]
        public void SelectEligible_with_no_ledger_pays_the_owner_alone()
        {
            CollectionAssert.AreEqual(new uint[] { 100 }, MlDigsiteRules.SelectEligible(100, null, g => true, g => null, TimeSpan.FromSeconds(90)));
        }

        [TestMethod]
        public void A_three_player_group_gives_the_owner_full_and_the_two_helpers_the_scaled_share()
        {
            // The live test the PR body owes, computed end to end through the pure rules: wave 6 with its two
            // checkpoints killed, two helpers.
            var tunables = new MlDigsitePayoutTunables
            {
                Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
                CheckpointBonus = 0.05,
                BossRushMinFraction = 0.10,
                PartialFraction = 0.35,
            };

            var fraction = MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss,
                new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 7, 2, 1.0, false, 6, true), tunables);

            var helperShare = MlDigsiteRules.NonOwnerShare(2, 1.5, 0.20, 0.75);

            Assert.AreEqual(0.85, fraction, 1e-9, "owner chest");
            Assert.AreEqual(0.6375, fraction * helperShare, 1e-9, "each helper chest");
            Assert.AreEqual(13, MlDigsiteRules.ScaleReward(15, fraction), "owner loot rolls");
            Assert.AreEqual(10, MlDigsiteRules.ScaleReward(15, fraction * helperShare), "helper loot rolls");
        }

        // ---- source-text pins: what cannot be driven under this harness ---------------------------------
        //
        // TrySpawn needs a live world (WorldObjectFactory.CreateNewWorldObject, EnterWorld) and Announce
        // needs online players, so the ROLE GATING on the Kept Siraluun roll and the GREEN spawn-text call
        // sites are pinned against source text instead, the same way the reward chest's threading is below.

        [TestMethod]
        public void KeptSiraluun_substitution_is_gated_to_the_miniboss_and_boss_roles_only()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var trySpawn = PooledLootSourceText.MethodBody(src,
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            // eligibility (Boss / Checkpoint, pinned by KeptSiraluunRollRole's own test) AND the encounter's
            // one-roll latch, in one condition - so an endless run rolls at its first checkpoint only
            StringAssert.Contains(trySpawn, "MlDigsiteRules.KeptSiraluunRollRole(role) && encounter.TryClaimKeptSiraluunRoll()");
            StringAssert.Contains(trySpawn, "RollKeptSiraluun");
            StringAssert.Contains(trySpawn, "PickKeptSiraluun");
        }

        [TestMethod]
        public void Per_wave_scaling_composes_with_crowd_scaling_in_one_health_write()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var scaling = PooledLootSourceText.MethodBody(src,
                "private static void ApplySpawnScaling(Creature creature, int participants, int waveNumber, bool applyCrowdHealth)");

            StringAssert.Contains(scaling, "var healthMult = crowdHealth * waveHealth;");
            StringAssert.Contains(scaling, "WorldEventSpawner.ScaledStartingValue(");
            StringAssert.Contains(scaling, "MlDigsiteRules.WaveDamageRatingAddend(waveNumber");
            StringAssert.Contains(scaling, "MlDigsiteRules.CrowdDamageRatingAddend(participants");
        }

        [TestMethod]
        public void The_checkpoint_miniboss_is_tracked_outside_the_live_wave_and_skipped_while_the_last_one_lives()
        {
            var spawner = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var trySpawn = PooledLootSourceText.MethodBody(spawner,
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            StringAssert.Contains(trySpawn, "else if (role == MlDigsiteRole.Checkpoint)");
            StringAssert.Contains(trySpawn, "encounter.TrackCheckpoint(creature);");

            var manager = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var spawnNext = PooledLootSourceText.MethodBody(manager,
                "private static int SpawnNextWave(MlDigsiteEncounter encounter, Random rng, bool announceStart = true)");

            StringAssert.Contains(spawnNext, "MlDigsiteRules.IsCheckpointWave(waveNumber, MlDigsiteTunables.CheckpointEvery)");
            StringAssert.Contains(spawnNext, "encounter.TryClaimCheckpoint(waveNumber)");
            StringAssert.Contains(spawnNext, "if (encounter.CheckpointAlive)");

            var driveWaves = PooledLootSourceText.MethodBody(manager,
                "private static void DriveWaves(MlDigsiteEncounter encounter, DateTime now)");

            // a wave clock running out ENDS the run; nothing brings a mini-boss forward any more
            StringAssert.Contains(driveWaves, "encounter.TryRequestEnd(reason, MlDigsiteResult.Scored)");
            Assert.IsFalse(driveWaves.Contains("MlDigsiteRole.MiniBoss"), "no forced mini-boss on the Waves path");
            Assert.IsFalse(manager.Contains("MarkReduced"), "the reduced tier is gone from the Waves path");
        }

        [TestMethod]
        public void Every_digsite_spawn_gets_crowd_scaling_applied_before_entering_the_world()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var trySpawn = PooledLootSourceText.MethodBody(src,
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            var scaling = trySpawn.IndexOf("ApplySpawnScaling", StringComparison.Ordinal);
            var enterWorld = trySpawn.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(scaling >= 0, "TrySpawn must call ApplySpawnScaling");
            Assert.IsTrue(enterWorld >= 0, "TrySpawn must call EnterWorld");
            Assert.IsTrue(scaling < enterWorld, "crowd scaling must be applied before EnterWorld, not after");
        }

        [TestMethod]
        public void Every_digsite_spawn_gets_the_radar_color_stamp_before_entering_the_world()
        {
            // Round 17 tester feedback: every digsite spawn must be told apart from ordinary wildlife on
            // radar, so the stamp is unconditional on role - and, like every other property write in
            // TrySpawn, it must land before EnterWorld or a CreateObject packet could go out without it.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteSpawner.cs");
            var trySpawn = PooledLootSourceText.MethodBody(src,
                "public static Creature TrySpawn(MlDigsiteEncounter encounter, MlDigsiteRole role, Random rng, int waveNumber = 1)");

            var stamp = trySpawn.IndexOf("MlDigsiteTunables.RadarColor", StringComparison.Ordinal);
            var enterWorld = trySpawn.IndexOf("creature.EnterWorld()", StringComparison.Ordinal);

            Assert.IsTrue(stamp >= 0, "TrySpawn must read MlDigsiteTunables.RadarColor");
            Assert.IsTrue(enterWorld >= 0, "TrySpawn must call EnterWorld");
            Assert.IsTrue(stamp < enterWorld, "the radar color stamp must land before EnterWorld, not after");
        }

        [TestMethod]
        public void Spawn_announcing_chat_lines_are_sent_in_the_client_green_worldbroadcast_colour()
        {
            // ChatMessageType.WorldBroadcast (0x14) renders pale green client-side - see
            // ACE.Entity.Enum.ChatMessageType.cs:264-269. Every line announcing a wave, mini-boss, boss or
            // priority mob SPAWNING must use it; every other digsite chat line stays the ordinary colour.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            StringAssert.Contains(src, "Announce(encounter, OpeningLine(encounter.Type), ChatMessageType.WorldBroadcast);");
            StringAssert.Contains(src,
                "Announce(encounter, \"Something heavier shoulders its way up through the loose earth alongside them.\", ChatMessageType.WorldBroadcast);");

            // Result narration is NOT a spawn announcement and must stay on the default colour.
            StringAssert.Contains(src, "Announce(encounter, \"The heavier thing falls, and the cache grows richer for it.\");");
            Assert.IsFalse(src.Contains("Announce(encounter, \"The heavier thing falls, and the cache grows richer for it.\", ChatMessageType.WorldBroadcast)"),
                "a checkpoint kill is not a spawn and must not be green");
        }

        [TestMethod]
        public void The_opening_wave_spawn_does_not_announce_a_redundant_Wave_1_line()
        {
            // OpenEncounter's own type-specific opening line (OpeningLine) already tells players a wave/field
            // just went up; a "Wave 1/N begins." line under it would be redundant, not additive. Every LATER
            // wave from DriveWaves must still announce.
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");

            var openEncounter = PooledLootSourceText.MethodBody(src,
                "private static void OpenEncounter(MlDigsiteEncounter encounter, long forcedMechanicSetId = 0)");
            var driveWaves = PooledLootSourceText.MethodBody(src,
                "private static void DriveWaves(MlDigsiteEncounter encounter, DateTime now)");

            StringAssert.Contains(openEncounter, "SpawnNextWave(encounter, rng, announceStart: false)");
            Assert.IsFalse(openEncounter.Contains("SpawnNextWave(encounter, rng)"),
                "OpenEncounter must not call SpawnNextWave without suppressing the opening wave announcement");

            StringAssert.Contains(driveWaves, "SpawnNextWave(encounter, NewRandom())");
            Assert.IsFalse(driveWaves.Contains("announceStart: false"),
                "a LATER wave (not the opening spawn) must still announce");
        }

        // ---- the reward chest's threading ------------------------------------------------------------------
        //
        // Pinned on SOURCE TEXT, the same way PooledLootWiringTests pins Creature.Die: putting a chest into
        // the world needs a live landblock and a running tick, so the property that matters here cannot be
        // driven under this harness. What CAN be checked is that the world entry is not reachable from the
        // calling thread at all.

        [TestMethod]
        public void The_reward_chest_is_built_on_the_anchor_landblocks_own_thread_not_the_killers()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var spawn = PooledLootSourceText.MethodBody(src,
                "private static void SpawnChests(MlDigsiteEncounter encounter, MlDigsiteResult result, IReadOnlyList<ChestPlan> plans)");
            var build = PooledLootSourceText.MethodBody(src,
                "private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)");

            // On a win SpawnChest is reached synchronously from Creature.Die, so it runs on whichever landblock
            // thread killed the objective. That creature can be up to SpawnRadiusMetres from the anchor at spawn
            // and can roam to TetherRadius, so it may die on a DIFFERENT landblock than the one the chest stands
            // on. Landblock.AddWorldObjectInternal logs that case at ERROR and warns it may still crash.
            Assert.IsFalse(spawn.Contains("EnterWorld"),
                "SpawnChest must not enter the world on the calling thread; it resolves a landblock and queues");

            StringAssert.Contains(spawn, "EnqueueAction(new ActionEventDelegate(",
                "the build must be handed to the anchor landblock's own action queue");

            // The entry itself lives in the queued builder, and nowhere else.
            StringAssert.Contains(build, "EnterWorld()");
        }

        [TestMethod]
        public void The_queued_chest_build_carries_its_own_guard_because_Deliver_no_longer_wraps_it()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var spawn = PooledLootSourceText.MethodBody(src,
                "private static void SpawnChests(MlDigsiteEncounter encounter, MlDigsiteResult result, IReadOnlyList<ChestPlan> plans)");

            // Deliver wraps its SpawnChest call in a try/catch, but the queued work runs AFTER Deliver has
            // returned, so that catch cannot see it. Without a guard inside the lambda an exception escapes
            // into the landblock tick.
            StringAssert.Contains(spawn, "catch (Exception",
                "the queued build must not be able to throw into the landblock tick");
        }

        [TestMethod]
        public void The_reward_latch_stays_outside_the_queued_work()
        {
            var src = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteRewards.cs");
            var deliver = PooledLootSourceText.MethodBody(src,
                "public static void Deliver(MlDigsiteEncounter encounter, MlDigsiteResult result)");
            var build = PooledLootSourceText.MethodBody(src,
                "private static void BuildChest(MlDigsiteEncounter encounter, MlDigsiteResult result, ChestPlan plan)");

            // The write-once claim has to be taken on the calling thread. If it moved inside the queued build,
            // two ends racing (a death hook and a reap) could both queue before either claimed, and the
            // encounter would pay twice.
            StringAssert.Contains(deliver, "TryClaimReward()");
            Assert.IsFalse(build.Contains("TryClaimReward"),
                "the reward latch must be claimed before the build is queued, never inside it");
        }

        // ---- tracker: status lines and boss HP bands (round 13 feedback item E, part 3) ------------------

        [TestMethod]
        public void WaveStartLine_names_the_wave_against_the_fixed_total_of_eight()
        {
            Assert.AreEqual("Wave 1 of 8 begins.", MlDigsiteRules.WaveStartLine(1));
            Assert.AreEqual("Wave 8 of 8 begins.", MlDigsiteRules.WaveStartLine(8));
        }

        [TestMethod]
        public void ProgressClause_shows_the_wave_and_the_payout_tier_for_a_Waves_encounter()
        {
            Assert.AreEqual("Wave 6 of 8, payout tier 70%.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.WavesAndMiniBoss, 6, 70, 0, 0));
        }

        [TestMethod]
        public void ProgressClause_shows_Corrupted_kills_for_a_Corruption_encounter()
        {
            Assert.AreEqual("Corrupted slain: 1 of 3.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.CorruptionMeter, 0, 0, 1, 0));

            Assert.AreEqual("Corrupted slain: 2 of 5.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.CorruptionMeter, 0, 0, 2, 0, corruptedKillsRequired: 5));
        }

        [TestMethod]
        public void ProgressClause_shows_boss_health_for_a_BossRush_encounter()
        {
            Assert.AreEqual("Boss at 88% health.",
                MlDigsiteRules.ProgressClause(MlDigsiteType.BossRush, 0, 0, 0, 88));
        }

        [TestMethod]
        public void StatusLine_appends_a_minutes_seconds_time_remaining_clause()
        {
            Assert.AreEqual("[Digsite] Wave 1 of 8, payout tier 15%. 4m05s remain.",
                MlDigsiteRules.StatusLine(MlDigsiteType.WavesAndMiniBoss, 1, 15, 0, 0, TimeSpan.FromSeconds(245)));
        }

        [TestMethod]
        public void StatusLine_floors_a_negative_time_left_at_zero_rather_than_going_negative()
        {
            Assert.AreEqual("[Digsite] Corrupted slain: 1 of 3. 0m00s remain.",
                MlDigsiteRules.StatusLine(MlDigsiteType.CorruptionMeter, 0, 0, 1, 0, TimeSpan.FromSeconds(-30)));
        }

        [TestMethod]
        public void BossHealthMilestoneCrossed_is_true_at_or_below_the_percentage_of_max()
        {
            Assert.IsTrue(MlDigsiteRules.BossHealthMilestoneCrossed(50, 100, 50));
            Assert.IsTrue(MlDigsiteRules.BossHealthMilestoneCrossed(25, 100, 50));
            Assert.IsFalse(MlDigsiteRules.BossHealthMilestoneCrossed(51, 100, 50));
            Assert.IsFalse(MlDigsiteRules.BossHealthMilestoneCrossed(50, 0, 50), "a zero max is never crossed");
        }

        [TestMethod]
        public void DueBossHealthMilestones_latches_each_tier_at_most_once_against_the_callers_own_set()
        {
            var fired = new HashSet<int>();

            CollectionAssert.AreEqual(new[] { 75, 50 }, MlDigsiteRules.DueBossHealthMilestones(45, 100, fired));

            // A second sample at the same health must not re-fire tiers already latched.
            CollectionAssert.AreEqual(Array.Empty<int>(), MlDigsiteRules.DueBossHealthMilestones(45, 100, fired));

            CollectionAssert.AreEqual(new[] { 25 }, MlDigsiteRules.DueBossHealthMilestones(20, 100, fired));
        }

        [TestMethod]
        public void BossHealthMilestonePercents_is_75_50_25_descending()
        {
            CollectionAssert.AreEqual(new[] { 75, 50, 25 }, MlDigsiteRules.BossHealthMilestonePercents.ToArray());
        }

        // ---- tunable registration --------------------------------------------------------------------------
        //
        // These read DefaultPropertyManager's dictionaries directly - the CODE defaults - rather than
        // PropertyManager.GetX, which throws for an uncached key with no shard DB. Nothing is seeded and
        // nothing needs restoring, which is what keeps this class order-independent.

        [TestMethod]
        public void MasterSwitch_ships_on_because_a_new_setting_is_a_kill_switch_not_a_gate()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties.ContainsKey("ml_digsite_enabled"),
                "ml_digsite_enabled is not registered");

            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["ml_digsite_enabled"].Item,
                "ml_digsite_enabled must default ON - this repo's standing rule is that a new setting is a kill switch, never a gate");
        }

        [TestMethod]
        public void Every_long_tunable_is_registered_with_its_shipped_default()
        {
            var expected = new Dictionary<string, long>
            {
                { "ml_digsite_weight_waves", 40 },
                { "ml_digsite_weight_corruption", 30 },
                { "ml_digsite_weight_bossrush", 30 },
                { "ml_digsite_max_concurrent", 20 },
                { "ml_digsite_ttl_seconds", 600 },
                { "ml_digsite_wipe_grace_seconds", 30 },
                { "ml_digsite_presence_window_seconds", 90 },
                { "ml_digsite_inter_wave_seconds", 1 },
                { "ml_digsite_checkpoint_every", 3 },
                { "ml_digsite_wave_count_per_wave", 1 },
                { "ml_digsite_wave_dr_per_wave", 5 },
                { "ml_digsite_wave_dr_cap", 60 },
                { "ml_digsite_wave_time_limit_seconds", 300 },
                { "ml_digsite_stall_timeout_seconds", 150 },
                { "ml_digsite_wave_count_base", 3 },
                { "ml_digsite_wave_count_cap", 12 },
                { "ml_digsite_corruption_kills_required", 3 },
                { "ml_digsite_corruption_field_spawn_seconds", 20 },
                { "ml_digsite_corruption_field_spawn_count", 5 },
                { "ml_digsite_chest_loot_count", 15 },
                { "ml_digsite_chest_doubloons", 2 },
                { "ml_digsite_chest_trade_notes", 5 },
                { "ml_digsite_chest_treasure_death_id", 2001 },
                { "ml_digsite_chest_ttl_seconds", 600 },
                { "ml_digsite_full_xp", 425000000 },
                { "ml_digsite_full_luminance", 80000 },
                { "ml_digsite_crowd_dr_per_player", 10 },
                { "ml_digsite_crowd_dr_cap", 100 },
                { "ml_digsite_status_interval_seconds", 30 },
                { "ml_digsite_priority_script", 0x56 },
                { "ml_digsite_forced_miniboss_seconds", 180 },
            };

            foreach (var pair in expected)
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultLongProperties.ContainsKey(pair.Key),
                    $"{pair.Key} is not registered in DefaultLongProperties");

                Assert.AreEqual(pair.Value, DefaultPropertyManager.DefaultLongProperties[pair.Key].Item,
                    $"{pair.Key} default moved");
            }
        }

        [TestMethod]
        public void Every_double_tunable_is_registered_with_its_shipped_default()
        {
            var expected = new Dictionary<string, double>
            {
                { "ml_digsite_separation_metres", 80.0 },
                { "ml_digsite_audience_radius_metres", 60.0 },
                { "ml_digsite_spawn_radius_metres", 12.0 },
                { "ml_digsite_wave_count_per_participant", 1.5 },
                { "ml_digsite_corrupted_health_multiplier", 6.0 },
                { "ml_digsite_fail_payout_multiplier", 0.85 },
                { "ml_digsite_partial_payout_fraction", 0.35 },
                { "ml_digsite_checkpoint_bonus", 0.05 },
                { "ml_digsite_wave_health_per_wave", 0.10 },
                { "ml_digsite_wave_health_cap", 3.0 },
                { "ml_digsite_bossrush_min_fraction", 0.10 },
                { "ml_digsite_group_share_pool", 1.5 },
                { "ml_digsite_group_share_max", 0.75 },
                { "ml_digsite_group_share_min", 0.20 },
                { "ml_digsite_boss_tether_radius", 40.0 },
                { "ml_digsite_kept_siraluun_chance", 0.05 },
                { "ml_digsite_crowd_per_player", 0.15 },
                { "ml_digsite_crowd_health_cap", 3.0 },
                { "ml_digsite_priority_scale", 1.25 },
            };

            foreach (var pair in expected)
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(pair.Key),
                    $"{pair.Key} is not registered in DefaultDoubleProperties");

                Assert.AreEqual(pair.Value, DefaultPropertyManager.DefaultDoubleProperties[pair.Key].Item, 1e-9,
                    $"{pair.Key} default moved");
            }
        }

        [TestMethod]
        public void Every_string_tunable_is_registered_with_the_rules_default_it_falls_back_to()
        {
            // The PropertyManager default and the MlDigsiteTunables fallback must be the SAME string, or an
            // unseeded read and a seeded one disagree. Both sides point at the MlDigsiteRules constant.
            var expected = new Dictionary<string, string>
            {
                { "ml_digsite_wave_tiers", MlDigsiteRules.DefaultWaveTiers },
                { "ml_digsite_bossrush_mechanic", MlDigsiteRules.DefaultBossRushMechanic },
            };

            foreach (var pair in expected)
            {
                Assert.IsTrue(DefaultPropertyManager.DefaultStringProperties.ContainsKey(pair.Key),
                    $"{pair.Key} is not registered in DefaultStringProperties");

                Assert.AreEqual(pair.Value, DefaultPropertyManager.DefaultStringProperties[pair.Key].Item,
                    $"{pair.Key} default drifted from its MlDigsiteRules fallback");
            }
        }

        [TestMethod]
        public void The_two_retired_digsite_keys_are_no_longer_registered()
        {
            // Endless waves replaced the fixed wave count, and the wipe test replaced the abandon test; the
            // shard migration 2026-09-18-05 deletes any leftover rows.
            Assert.IsFalse(DefaultPropertyManager.DefaultLongProperties.ContainsKey("ml_digsite_waves"));
            Assert.IsFalse(DefaultPropertyManager.DefaultLongProperties.ContainsKey("ml_digsite_abandon_grace_seconds"));
        }

        [TestMethod]
        public void The_two_treasure_map_tuning_changes_shipped()
        {
            // Both are CHANGED defaults, so each also required a config-defaults snapshot entry and a shard
            // migration; this pins the source side of that pair.
            Assert.AreEqual(0.01, DefaultPropertyManager.DefaultDoubleProperties["ml_treasure_drop_chance"].Item, 1e-9,
                "the map drop chance should now be 1 percent");

            Assert.AreEqual(0.01, DefaultPropertyManager.DefaultDoubleProperties["ml_treasure_relaria_chance"].Item, 1e-9,
                "Relaria should now be the 1 percent dig outcome, not the 5 percent one");
        }

        // ---- round 14 feedback: announcement text ---------------------------------------------------------

        [TestMethod]
        public void TypeName_renders_a_player_readable_name_for_every_shape_never_the_enum_token()
        {
            Assert.AreEqual("Waves", MlDigsiteRules.TypeName(MlDigsiteType.WavesAndMiniBoss));
            Assert.AreEqual("Corruption", MlDigsiteRules.TypeName(MlDigsiteType.CorruptionMeter));
            Assert.AreEqual("Boss Rush", MlDigsiteRules.TypeName(MlDigsiteType.BossRush));

            // The stale enum token in particular must never reach a player: that shape stopped ending in a
            // mini-boss when #1225 made it endless.
            foreach (MlDigsiteType type in Enum.GetValues(typeof(MlDigsiteType)))
                StringAssert.DoesNotMatch(MlDigsiteRules.TypeName(type), new Regex("WavesAndMiniBoss"));
        }

        // ---- round 14 feedback: wave messaging ------------------------------------------------------------

        [TestMethod]
        public void WaveClearedLine_names_the_cleared_wave_the_next_one_and_the_breather()
        {
            // Round 15: the exact owner-approved text, with the running count. Round 16: named against the
            // fixed total of 8.
            Assert.AreEqual("Wave 3 of 8 cleared - 3 down so far. Wave 4 rises in 10 seconds.",
                MlDigsiteRules.WaveClearedLine(3, TimeSpan.FromSeconds(10)));
        }

        [TestMethod]
        public void WaveClearedLine_first_wave_reads_one_down()
        {
            Assert.AreEqual("Wave 1 of 8 cleared - 1 down so far. Wave 2 rises in 10 seconds.",
                MlDigsiteRules.WaveClearedLine(1, TimeSpan.FromSeconds(10)));
        }

        [TestMethod]
        public void WaveClearedLine_says_one_second_singular()
        {
            StringAssert.Contains(MlDigsiteRules.WaveClearedLine(1, TimeSpan.FromSeconds(1)), "1 second.");
        }

        [TestMethod]
        public void WaveClearedLine_with_no_breather_does_not_promise_zero_seconds()
        {
            // ml_digsite_inter_wave_seconds is allowed to be 0, and "rises in 0 seconds" is a sentence that
            // reads as a bug rather than as a pace.
            var line = MlDigsiteRules.WaveClearedLine(5, TimeSpan.Zero);

            Assert.AreEqual("Wave 5 of 8 cleared - 5 down so far. Wave 6 is right behind them.", line);
            StringAssert.DoesNotMatch(line, new Regex("0 second"));
        }

        // ---- round 14 feedback: the time-forced checkpoint mini-boss --------------------------------------

        [TestMethod]
        public void ForcedCheckpointDue_fires_at_the_threshold_and_not_before()
        {
            var after = TimeSpan.FromSeconds(180);

            Assert.IsFalse(MlDigsiteRules.ForcedCheckpointDue(TimeSpan.FromSeconds(179), after));
            Assert.IsTrue(MlDigsiteRules.ForcedCheckpointDue(TimeSpan.FromSeconds(180), after));
            Assert.IsTrue(MlDigsiteRules.ForcedCheckpointDue(TimeSpan.FromSeconds(181), after));
        }

        [TestMethod]
        public void ForcedCheckpointDue_is_disabled_by_a_non_positive_threshold()
        {
            // The same "0 turns this clock off" convention WaveClockExpired uses, so an operator can switch
            // the forced spawn off without also having to push the threshold past the TTL.
            Assert.IsFalse(MlDigsiteRules.ForcedCheckpointDue(TimeSpan.FromHours(1), TimeSpan.Zero));
            Assert.IsFalse(MlDigsiteRules.ForcedCheckpointDue(TimeSpan.FromHours(1), TimeSpan.FromSeconds(-30)));
        }

        /// <summary>
        /// THE MANAGER-SEAM half of the review finding, in the same source-text idiom
        /// <see cref="The_checkpoint_miniboss_is_tracked_outside_the_live_wave_and_skipped_while_the_last_one_lives"/>
        /// already uses for this file: DriveWaves cannot be unit-tested directly (it spawns creatures into a
        /// landblock), but its ORDER can be pinned, and order is the whole of the bug.
        ///
        /// The first version placed DriveForcedCheckpoint as the very first statement of DriveWaves, before
        /// the wave clocks had been consulted at all. Both assertions below fail against that version.
        /// </summary>
        [TestMethod]
        public void DriveWaves_asks_the_wave_clocks_before_it_ever_asks_for_a_forced_miniboss()
        {
            var manager = PooledLootSourceText.Read("Source/ACE.Server/MlDigsite/MlDigsiteManager.cs");
            var driveWaves = PooledLootSourceText.MethodBody(manager,
                "private static void DriveWaves(MlDigsiteEncounter encounter, DateTime now)");

            var endIndex = driveWaves.IndexOf("TryRequestEnd", StringComparison.Ordinal);
            var forcedIndex = driveWaves.IndexOf("DriveForcedCheckpoint(", StringComparison.Ordinal);

            Assert.IsTrue(endIndex >= 0, "DriveWaves should still be the place a wave clock ends the run");
            Assert.IsTrue(forcedIndex >= 0, "DriveWaves should still be what asks for the forced mini-boss");

            Assert.IsTrue(forcedIndex > endIndex,
                "the forced mini-boss must be asked for AFTER the wave clocks have decided the run continues, "
                + "or it can be summoned onto the very tick that tears the encounter down");

            // And it cannot be asked for without a resolved tick: there is no overload that omits one, so the
            // clocks cannot be skipped by a future caller either.
            StringAssert.Contains(driveWaves, "DriveForcedCheckpoint(encounter, now, tick)");

            var forced = PooledLootSourceText.MethodBody(manager,
                "private static void DriveForcedCheckpoint(MlDigsiteEncounter encounter, DateTime now, MlDigsiteWavesTick tick)");

            StringAssert.Contains(forced, "MlDigsiteRules.ShouldForceCheckpoint(tick,");
        }

        [TestMethod]
        public void WavesTick_resolves_each_tick_to_exactly_one_action()
        {
            // A scheduled wave: the clocks are not the caller's business, and WavesTick ignores clockExpired
            // outright rather than trusting the caller to have left it false.
            Assert.AreEqual(MlDigsiteWavesTick.Breather, MlDigsiteRules.WavesTick(waveScheduled: true, waveDue: false, clockExpired: false));
            Assert.AreEqual(MlDigsiteWavesTick.Breather, MlDigsiteRules.WavesTick(waveScheduled: true, waveDue: false, clockExpired: true));
            Assert.AreEqual(MlDigsiteWavesTick.SpawnWave, MlDigsiteRules.WavesTick(waveScheduled: true, waveDue: true, clockExpired: false));
            Assert.AreEqual(MlDigsiteWavesTick.SpawnWave, MlDigsiteRules.WavesTick(waveScheduled: true, waveDue: true, clockExpired: true));

            // A live wave: the clocks decide.
            Assert.AreEqual(MlDigsiteWavesTick.FightOn, MlDigsiteRules.WavesTick(waveScheduled: false, waveDue: false, clockExpired: false));
            Assert.AreEqual(MlDigsiteWavesTick.EndRun, MlDigsiteRules.WavesTick(waveScheduled: false, waveDue: false, clockExpired: true));
        }

        /// <summary>
        /// THE REGRESSION TEST for the review finding. The first version of DriveWaves asked for the forced
        /// mini-boss BEFORE it asked the wave clocks anything, so a run whose stall timer or wave time limit
        /// expired on the same tick got "Something climbs out" immediately followed by the run-ending line,
        /// and burned the encounter's one-shot latch on a mini-boss nobody could fight.
        ///
        /// Every other input here is the most favourable one possible - long overdue, no checkpoint standing -
        /// so EndRun is the only thing that can make this false. Delete the EndRun clause from
        /// ShouldForceCheckpoint and this fails.
        /// </summary>
        [TestMethod]
        public void ShouldForceCheckpoint_refuses_on_the_tick_that_ends_the_run_however_overdue_it_is()
        {
            var after = TimeSpan.FromSeconds(180);
            var wayOverdue = TimeSpan.FromSeconds(1800);

            Assert.IsFalse(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.EndRun, wayOverdue, after, checkpointAlive: false),
                "a forced mini-boss must never be placed onto the tick that tears the encounter down");

            // ... and the same inputs on any tick that is NOT ending the run do place it, which is what makes
            // the assertion above discriminating rather than vacuous.
            Assert.IsTrue(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.FightOn, wayOverdue, after, checkpointAlive: false));
            Assert.IsTrue(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.Breather, wayOverdue, after, checkpointAlive: false));
            Assert.IsTrue(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.SpawnWave, wayOverdue, after, checkpointAlive: false));
        }

        [TestMethod]
        public void ShouldForceCheckpoint_defers_while_a_checkpoint_is_still_standing()
        {
            var after = TimeSpan.FromSeconds(180);
            var overdue = TimeSpan.FromSeconds(300);

            Assert.IsFalse(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.FightOn, overdue, after, checkpointAlive: true),
                "a second mini-boss must never be stacked on the standing one");

            // DEFERRED, not cancelled: the same run places it once that checkpoint is dead. The caller's
            // one-shot latch is claimed only when this returns true, which is what makes that possible.
            Assert.IsTrue(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.FightOn, overdue, after, checkpointAlive: false));
        }

        [TestMethod]
        public void ShouldForceCheckpoint_still_honours_the_threshold_and_the_off_switch()
        {
            Assert.IsFalse(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.FightOn,
                TimeSpan.FromSeconds(179), TimeSpan.FromSeconds(180), checkpointAlive: false));

            Assert.IsFalse(MlDigsiteRules.ShouldForceCheckpoint(MlDigsiteWavesTick.FightOn,
                TimeSpan.FromHours(1), TimeSpan.Zero, checkpointAlive: false));
        }

        [TestMethod]
        public void ForcedCheckpoint_latch_yields_exactly_one_spawn_however_often_the_tick_asks()
        {
            // The elapsed-time test stays true for the rest of the run, so the one-shot latch - not the
            // predicate - is what stops a mini-boss per 1 s tick.
            var encounter = new MlDigsiteEncounter(1, 0x50000001, "Digger", MlDigsiteType.WavesAndMiniBoss,
                null, DateTime.UtcNow);

            var claims = 0;

            for (var tick = 0; tick < 50; tick++)
            {
                if (encounter.TryClaimForcedCheckpoint())
                    claims++;
            }

            Assert.AreEqual(1, claims, "the forced checkpoint must be claimable exactly once per encounter");
        }

        // ---- round 16: 8-wave win, Corrupted mob kill count, fail-payout multiplier, retuned weights -----

        [TestMethod]
        public void WaveWon_only_at_wave_eight_or_beyond()
        {
            Assert.IsFalse(MlDigsiteRules.WaveWon(1));
            Assert.IsFalse(MlDigsiteRules.WaveWon(7));
            Assert.IsTrue(MlDigsiteRules.WaveWon(8));
            Assert.IsTrue(MlDigsiteRules.WaveWon(9), "a retune that skipped a wave count must still read as won");
        }

        [TestMethod]
        public void WaveWon_a_non_positive_total_turns_the_win_condition_off()
        {
            Assert.IsFalse(MlDigsiteRules.WaveWon(100, totalWaves: 0));
            Assert.IsFalse(MlDigsiteRules.WaveWon(100, totalWaves: -1));
        }

        [TestMethod]
        public void CorruptionKillsWon_only_at_the_required_count_or_beyond()
        {
            Assert.IsFalse(MlDigsiteRules.CorruptionKillsWon(0, 3));
            Assert.IsFalse(MlDigsiteRules.CorruptionKillsWon(2, 3));
            Assert.IsTrue(MlDigsiteRules.CorruptionKillsWon(3, 3));
            Assert.IsTrue(MlDigsiteRules.CorruptionKillsWon(4, 3));
            Assert.IsFalse(MlDigsiteRules.CorruptionKillsWon(3, 0), "a non-positive requirement never reads as won");
        }

        [TestMethod]
        public void CorruptionProgressFraction_is_kills_over_required()
        {
            Assert.AreEqual(1.0 / 3.0, MlDigsiteRules.CorruptionProgressFraction(1, 3, false, null), 1e-9);
            Assert.AreEqual(2.0 / 3.0, MlDigsiteRules.CorruptionProgressFraction(2, 3, false, null), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.CorruptionProgressFraction(3, 3, false, null), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.CorruptionProgressFraction(9, 3, false, null), 1e-9, "clamped, never over 1");
        }

        [TestMethod]
        public void CorruptionProgressFraction_a_zero_kill_bail_pays_nothing()
        {
            Assert.AreEqual(0.0, MlDigsiteRules.CorruptionProgressFraction(0, 3, bailed: true, tiers: null), 1e-9);
        }

        [TestMethod]
        public void CorruptionProgressFraction_a_zero_kill_non_bail_end_floors_at_the_wave_one_tier()
        {
            var tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers);

            // "the same floor as wave 1" - a group that fought and ran out of time is not paid nothing.
            Assert.AreEqual(0.15, MlDigsiteRules.CorruptionProgressFraction(0, 3, bailed: false, tiers: tiers), 1e-9);

            // an empty/null table falls back to the shipped default's own wave-1 row, same as WaveTierFraction.
            Assert.AreEqual(0.15, MlDigsiteRules.CorruptionProgressFraction(0, 3, bailed: false, tiers: null), 1e-9);
        }

        [TestMethod]
        public void CorruptionProgressFraction_the_floor_applies_at_every_non_bail_kill_count_not_only_zero()
        {
            // Code-review ruling: max(kills / required, floor), not just a zero-kill special case. A group
            // that killed 1 of 10 required (raw 0.10, below the 0.15 floor) is not paid less than a group
            // that killed none.
            Assert.AreEqual(0.15, MlDigsiteRules.CorruptionProgressFraction(1, 10, bailed: false, tiers: null), 1e-9);

            // once raw progress clears the floor, the floor no longer binds.
            Assert.AreEqual(0.2, MlDigsiteRules.CorruptionProgressFraction(2, 10, bailed: false, tiers: null), 1e-9);

            // a WIN-sized count (raw 1.0) is naturally above the floor and unaffected by it.
            Assert.AreEqual(1.0, MlDigsiteRules.CorruptionProgressFraction(10, 10, bailed: false, tiers: null), 1e-9);
        }

        [TestMethod]
        public void CorruptionProgressFraction_a_bail_with_progress_is_never_floored()
        {
            // The floor is an owner protection for a run that was taken away from the group (a timer, a
            // wipe); a BAIL is the owner's own choice to stop, so a bail with some progress pays that raw
            // progress even when it is below the non-bail floor - only a ZERO-kill bail pays nothing.
            Assert.AreEqual(0.10, MlDigsiteRules.CorruptionProgressFraction(1, 10, bailed: true, tiers: null), 1e-9);
        }

        [TestMethod]
        public void EncounterPayoutFraction_a_WIN_always_pays_in_full_unmultiplied_for_every_shape()
        {
            var tunables = new MlDigsitePayoutTunables
            {
                Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
                CheckpointBonus = 0.05,
                BossRushMinFraction = 0.10,
                FailPayoutMultiplier = 0.85,
            };

            var wavesWin = new MlDigsitePayoutSnapshot(MlDigsiteResult.FullClear, 8, 0, 1.0, false, 8, false);
            var bossRushWin = new MlDigsitePayoutSnapshot(MlDigsiteResult.FullClear, 0, 0, 0.0, true, 0, false);
            var corruptionWin = new MlDigsitePayoutSnapshot(MlDigsiteResult.FullClear, 0, 0, 1.0, false, 0, false, corruptedKills: 3);

            Assert.AreEqual(1.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss, wavesWin, tunables), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, bossRushWin, tunables), 1e-9);
            Assert.AreEqual(1.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptionWin, tunables), 1e-9);
        }

        [TestMethod]
        public void EncounterPayoutFraction_multiplies_the_progress_fraction_by_the_fail_multiplier_for_every_shape()
        {
            var tunables = new MlDigsitePayoutTunables
            {
                Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
                CheckpointBonus = 0.0,
                BossRushMinFraction = 0.10,
                FailPayoutMultiplier = 0.85,
            };

            // Waves: wave 3 cleared (tier 0.39), stall timeout, no checkpoints.
            var waves = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 4, 0, 1.0, false, 3, false);
            Assert.AreEqual(0.39 * 0.85, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss, waves, tunables), 1e-9);

            // Boss Rush: 60% of the boss's health removed, TTL expiry.
            var bossRush = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 0, 0, 0.4, false, 0, false);
            Assert.AreEqual(0.6 * 0.85, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, bossRush, tunables), 1e-9);

            // Corruption: 1 of 3 Corrupted mobs killed, TTL expiry.
            var corruption = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 0, 0, 1.0, false, 0, false, corruptedKills: 1);
            Assert.AreEqual((1.0 / 3.0) * 0.85, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruption, tunables), 1e-9);
        }

        [TestMethod]
        public void EncounterPayoutFraction_a_bail_with_no_progress_at_all_pays_nothing_for_every_shape()
        {
            // The pre-existing Waves rule (a bail before any wave is cleared pays nothing) now applies to
            // Corruption too (a bail with zero Corrupted kills). Boss Rush never had this rule (a Boss Rush
            // bail with no damage dealt still pays the floor, ml_digsite_bossrush_min_fraction) - preserved
            // as-is, unchanged by round 16.
            var tunables = new MlDigsitePayoutTunables
            {
                Tiers = MlDigsiteRules.ParseTierTable(MlDigsiteRules.DefaultWaveTiers),
                CheckpointBonus = 0.05,
                BossRushMinFraction = 0.10,
                FailPayoutMultiplier = 0.85,
            };

            var wavesBailedNoProgress = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 1, 0, 1.0, false, 0, true);
            Assert.AreEqual(0.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.WavesAndMiniBoss, wavesBailedNoProgress, tunables), 1e-9);

            var corruptionBailedNoProgress = new MlDigsitePayoutSnapshot(MlDigsiteResult.Failed, 0, 0, 1.0, false, 0, true);
            Assert.AreEqual(0.0, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.CorruptionMeter, corruptionBailedNoProgress, tunables), 1e-9);

            var bossRushBailedNoDamage = new MlDigsitePayoutSnapshot(MlDigsiteResult.Scored, 0, 0, 1.0, false, 0, true);
            Assert.AreEqual(0.10 * 0.85, MlDigsiteRules.EncounterPayoutFraction(MlDigsiteType.BossRush, bossRushBailedNoDamage, tunables), 1e-9,
                "Boss Rush never had a zero-progress-bail-pays-zero rule; only its floor, still multiplied like any other non-win");
        }

        [TestMethod]
        public void PickType_splits_the_round_16_shipped_40_30_30_weights_across_their_own_total()
        {
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(40, 30, 30, 0.0));
            Assert.AreEqual(MlDigsiteType.WavesAndMiniBoss, MlDigsiteRules.PickType(40, 30, 30, 0.39));
            Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(40, 30, 30, 0.40));
            Assert.AreEqual(MlDigsiteType.CorruptionMeter, MlDigsiteRules.PickType(40, 30, 30, 0.69));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(40, 30, 30, 0.70));
            Assert.AreEqual(MlDigsiteType.BossRush, MlDigsiteRules.PickType(40, 30, 30, 0.99));
        }

        // ---- round 16 code review: the Corrupted-mob respawn retry (softlock fix) -----------------------

        [TestMethod]
        public void ShouldRetryCorruptedSpawn_only_when_a_spawn_is_owed_and_none_is_alive()
        {
            Assert.IsFalse(MlDigsiteRules.ShouldRetryCorruptedSpawn(pending: false, objectiveAlive: false), "nothing is owed");
            Assert.IsFalse(MlDigsiteRules.ShouldRetryCorruptedSpawn(pending: false, objectiveAlive: true), "nothing is owed");
            Assert.IsTrue(MlDigsiteRules.ShouldRetryCorruptedSpawn(pending: true, objectiveAlive: false), "owed, and none alive - retry");

            // the invariant the whole mechanism exists to protect: never a retry while one is already alive,
            // however the pending flag got set.
            Assert.IsFalse(MlDigsiteRules.ShouldRetryCorruptedSpawn(pending: true, objectiveAlive: true),
                "must never place a second Corrupted mob alongside a live one");
        }

        // ---- /testtreasuremap: forced-type encoding (PropertyInt.TreasureMapForcedDigsiteType) ----------

        [TestMethod]
        public void EncodeForcedType_null_is_zero()
        {
            Assert.AreEqual(0, MlDigsiteRules.EncodeForcedType(null));
        }

        [TestMethod]
        public void EncodeForcedType_is_the_enum_value_plus_one()
        {
            Assert.AreEqual(1, MlDigsiteRules.EncodeForcedType(MlDigsiteType.WavesAndMiniBoss));
            Assert.AreEqual(2, MlDigsiteRules.EncodeForcedType(MlDigsiteType.CorruptionMeter));
            Assert.AreEqual(3, MlDigsiteRules.EncodeForcedType(MlDigsiteType.BossRush));
        }

        [TestMethod]
        public void DecodeForcedType_zero_or_absent_is_null_meaning_roll()
        {
            Assert.IsNull(MlDigsiteRules.DecodeForcedType(0));
        }

        [TestMethod]
        public void DecodeForcedType_negative_is_null()
        {
            Assert.IsNull(MlDigsiteRules.DecodeForcedType(-5));
        }

        [TestMethod]
        public void DecodeForcedType_out_of_range_is_null_rather_than_an_arbitrary_type()
        {
            // Only three MlDigsiteType members exist (0-2), so an encoded value of 5 would decode to raw
            // type 4, which is not defined - a corrupt or hand-edited property must fall back to rolling,
            // never crash or silently pick a type.
            Assert.IsNull(MlDigsiteRules.DecodeForcedType(5));
        }

        [TestMethod]
        public void EncodeThenDecodeForcedType_round_trips_every_type()
        {
            foreach (MlDigsiteType type in new[] { MlDigsiteType.WavesAndMiniBoss, MlDigsiteType.CorruptionMeter, MlDigsiteType.BossRush })
            {
                var encoded = MlDigsiteRules.EncodeForcedType(type);
                Assert.AreEqual(type, MlDigsiteRules.DecodeForcedType(encoded));
            }
        }
    }
}
