using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The Threads damage ceiling's pure half (DAMAGE-CEILING-PLAN.md steps 1 and 2): the compression curve, the group
    /// clamp, the withheld-rating budget, and the baseline (<see cref="DamageCeilingStandard"/>: the shared median of what
    /// actually spawns, and the automatic top) as the builder carries it on the plan. Reads no PropertyManager key; the plan cache is process-static, so the one test that uses
    /// it invalidates it before and after.
    /// </summary>
    [TestClass]
    public class DamageCeilingTests
    {
        /// <summary>The stamp curve these pins were written against: the shipped defense rates and damage at the full slope (1.0).</summary>
        private static double PreDialCurve(DungeonStatAxis axis, int fromLevel, int toLevel)
            => DungeonStatCurve.Ratio(axis, fromLevel, toLevel, DungeonStatCurve.DefaultDefenseRateAbove375,
                DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 326);

        private const double Cap = DamageCeiling.DefaultCap;

        /// <summary>A fixed reference ratio for the curve tests (the plan's own top is the pool max unless the dial forces one).</summary>
        private const double Top = 8.0;

        // ---- Compress ----------------------------------------------------------------------------------------

        [TestMethod]
        public void Compress_maps_the_reference_ratio_exactly_onto_the_cap()
            => Assert.AreEqual(3.0, DamageCeiling.Compress(7.9, 3.0, 7.9), 1e-9);

        [TestMethod]
        public void Compress_puts_three_and_a_half_times_the_median_near_two()
            => Assert.AreEqual(1.95, DamageCeiling.Compress(3.5, 3.0, 7.9), 0.02);

        [TestMethod]
        public void Compress_leaves_a_ratio_at_or_below_one_unchanged()
        {
            foreach (var r in new[] { 0.0, 0.25, 0.999, 1.0 })
                Assert.AreEqual(r, DamageCeiling.Compress(r, Cap, Top), 0.0, $"r {r}");
        }

        [TestMethod]
        public void Compress_clamps_a_ratio_above_the_reference_to_the_cap()
        {
            foreach (var r in new[] { 8.0, 8.0001, 12.0, 1000.0 })
                Assert.AreEqual(Cap, DamageCeiling.Compress(r, Cap, Top), 1e-12, $"r {r}");
        }

        [TestMethod]
        public void Compress_is_monotone_and_never_raises_a_ratio()
        {
            var previous = 0.0;

            for (var r = 0.1; r < 20; r += 0.05)
            {
                var c = DamageCeiling.Compress(r, Cap, Top);

                Assert.IsTrue(c >= previous - 1e-12, $"not monotone at {r}");
                Assert.IsTrue(c <= r + 1e-12, $"raised {r} to {c}");
                Assert.IsTrue(c <= Cap + 1e-12, $"{r} -> {c} is above the cap");
                previous = c;
            }
        }

        [TestMethod]
        public void Compress_with_the_reference_at_or_below_the_cap_is_a_plain_clamp()
        {
            Assert.AreEqual(2.5, DamageCeiling.Compress(2.5, 3.0, 3.0), 1e-12);
            Assert.AreEqual(3.0, DamageCeiling.Compress(4.0, 3.0, 3.0), 1e-12);
            Assert.AreEqual(2.0, DamageCeiling.Compress(2.0, 3.0, 2.0), 1e-12);
            Assert.AreEqual(3.0, DamageCeiling.Compress(7.0, 3.0, 2.0), 1e-12);
        }

        [TestMethod]
        public void Compress_reads_a_garbled_cap_as_the_default_and_a_garbled_top_as_a_plain_clamp()
        {
            var expected = DamageCeiling.Compress(3.5, Cap, Top);

            foreach (var cap in new[] { double.NaN, double.PositiveInfinity, double.NegativeInfinity, -1.0, 0.0, 0.5 })
                Assert.AreEqual(expected, DamageCeiling.Compress(3.5, cap, Top), 1e-12, $"cap {cap}");

            foreach (var top in new[] { double.NaN, double.PositiveInfinity, -2.0, 0.0 })
                Assert.AreEqual(Math.Min(3.5, Cap), DamageCeiling.Compress(3.5, Cap, top), 1e-12, $"top {top}: no usable top, a plain clamp");

            Assert.AreEqual(Cap, DamageCeiling.SanitizeCap(double.NaN));
            Assert.AreEqual(0.0, DamageCeiling.SanitizeReferenceRatio(double.NaN), "garbled reads as auto");
            Assert.AreEqual(0.0, DamageCeiling.SanitizeReferenceRatio(-1.0), "negative reads as auto");
            Assert.AreEqual(0.0, DamageCeiling.SanitizeReferenceRatio(0.0));
            Assert.AreEqual(4.5, DamageCeiling.SanitizeReferenceRatio(4.5), "a positive value is kept");
            Assert.AreEqual(1.0, DamageCeiling.SanitizeCap(1.0), "1 is a valid (flattening) cap");
            Assert.IsTrue(double.IsNaN(DamageCeiling.Compress(double.NaN, Cap, Top)), "a NaN ratio passes through unchanged");
        }

        // ---- targets, the group clamp and the budget ------------------------------------------------------------

        [TestMethod]
        public void TargetPerHit_is_the_median_times_the_compressed_ratio_and_a_missing_axis_is_untouched()
        {
            Assert.AreEqual(100.0 * DamageCeiling.Compress(3.5, Cap, Top), DamageCeiling.TargetPerHit(350, 100, Cap, Top), 1e-9);
            Assert.AreEqual(80.0, DamageCeiling.TargetPerHit(80, 100, Cap, Top), 0.0, "below the median");
            Assert.AreEqual(350.0, DamageCeiling.TargetPerHit(350, 0, Cap, Top), 0.0, "no median, no axis");
            Assert.AreEqual(0.0, DamageCeiling.TargetPerHit(0, 100, Cap, Top), 0.0, "no damage");
        }

        [TestMethod]
        public void GroupFactor_is_the_rating_multiplier_ratio()
        {
            Assert.AreEqual(1.0, DamageCeiling.GroupFactor(5, 0), 0.0);
            Assert.AreEqual(1.0, DamageCeiling.GroupFactor(5, -10), 0.0, "a negative bonus never lowers");
            Assert.AreEqual(420.0 / 120.0, DamageCeiling.GroupFactor(20, 300), 1e-12);
            Assert.AreEqual(115.0 / 100.0, DamageCeiling.GroupFactor(0, 15), 1e-12);
        }

        [TestMethod]
        public void FinalPerHit_holds_the_grouped_hit_under_the_solo_ceiling_and_never_raises()
        {
            foreach (var g in new[] { 0, 15, 45, 300 })
            foreach (var r in new[] { 0.5, 1.0, 2.0, 3.5, 7.9, 8.0, 20.0 })
            {
                var p = 1000.0 * r;
                var final = DamageCeiling.FinalPerHit(p, 1000, Cap, Top, 10, g);

                Assert.IsTrue(final <= p + 1e-9, $"r {r} G {g} raised");
                Assert.IsTrue(final * DamageCeiling.GroupFactor(10, g) <= Cap * 1000 + 1e-6, $"r {r} G {g}: {final} x g above the ceiling");
            }
        }

        /// <summary>
        /// Group_per_hit_ceiling_equals_solo (DAMAGE-CEILING-DESIGN.md goal 2): over a pool of creature ratios, the largest
        /// grouped hit P_final x g is identical for a roster of 1, 2 and 21 (G = 0, 15, 300 at the shipped dials).
        /// </summary>
        [TestMethod]
        public void Group_per_hit_ceiling_equals_solo()
        {
            const double Median = 2000.0;
            const int OwnRating = 5;

            double MaxGroupedHit(int roster)
            {
                var g = GroupScaling.DamageRatingBonusFor(roster, GroupScaling.DefaultDamageRatingPerMember, GroupScaling.DefaultDamageRatingCap);
                var factor = DamageCeiling.GroupFactor(OwnRating, g);
                var max = 0.0;

                for (var r = 0.25; r <= 12.0; r += 0.25)
                    max = Math.Max(max, DamageCeiling.FinalPerHit(Median * r, Median, Cap, Top, OwnRating, g) * factor);

                return max;
            }

            Assert.AreEqual(0, GroupScaling.DamageRatingBonusFor(1, 15, 300));
            Assert.AreEqual(15, GroupScaling.DamageRatingBonusFor(2, 15, 300));
            Assert.AreEqual(300, GroupScaling.DamageRatingBonusFor(21, 15, 300));

            var solo = MaxGroupedHit(1);

            Assert.AreEqual(Cap * Median, solo, 1e-6, "solo: the worst creature hits exactly the cap");
            Assert.AreEqual(solo, MaxGroupedHit(2), 1e-6, "roster of 2");
            Assert.AreEqual(solo, MaxGroupedHit(21), 1e-6, "roster of 21");
        }

        [TestMethod]
        public void WithheldRating_is_what_would_carry_the_compressed_hit_past_the_ceiling()
        {
            const double Cap3 = 3.0; // the arithmetic below is written for a cap of 3

            // At the cap already: nothing of the bonus fits.
            Assert.AreEqual(300, DamageCeiling.WithheldRating(3000, 1000, Cap3, 0, 300));

            // Half the cap: (100 + 0) x (3000 / 1500 - 1) = 100 of the 300 fits, 200 is withheld.
            Assert.AreEqual(100, DamageCeiling.AllowedGroupRating(1500, 1000, Cap3, 0, 300));
            Assert.AreEqual(200, DamageCeiling.WithheldRating(1500, 1000, Cap3, 0, 300));

            // The creature's own rating widens the allowance: (100 + 20) x 1 = 120.
            Assert.AreEqual(180, DamageCeiling.WithheldRating(1500, 1000, Cap3, 20, 300));

            // Far below the cap: everything fits.
            Assert.AreEqual(0, DamageCeiling.WithheldRating(100, 1000, Cap3, 0, 300));

            // Solo, or no axis: nothing to withhold.
            Assert.AreEqual(0, DamageCeiling.WithheldRating(3000, 1000, Cap3, 0, 0));
            Assert.AreEqual(0, DamageCeiling.WithheldRating(3000, 0, Cap3, 0, 300));
            Assert.AreEqual(0, DamageCeiling.WithheldRating(0, 1000, Cap3, 0, 300));
        }

        [TestMethod]
        public void ScaleDown_floors_and_never_drops_a_positive_value_below_one()
        {
            Assert.AreEqual(149, DamageCeiling.ScaleDown(300, 0.4999));
            Assert.AreEqual(1, DamageCeiling.ScaleDown(3, 0.01));
            Assert.AreEqual(0, DamageCeiling.ScaleDown(0, 0.5));
            Assert.AreEqual(300, DamageCeiling.ScaleDown(300, 1.0), "a factor of 1 leaves it");
            Assert.AreEqual(300, DamageCeiling.ScaleDown(300, double.NaN));
        }

        [TestMethod]
        public void Predict_is_base_times_the_engine_attribute_mod()
        {
            Assert.AreEqual(100.0 * (1 + (155 - 55) * 0.011), DamageCeiling.Predict(100, 155, false), 1e-4);
            Assert.AreEqual(100.0 * (1 + (155 - 55) * 0.008), DamageCeiling.Predict(100, 155, true), 1e-4);
            Assert.AreEqual(100.0, DamageCeiling.Predict(100, 10, false), 1e-9, "the attribute mod never drops below 1");
        }

        [TestMethod]
        public void ProjectedTo_carries_damage_and_the_attribute_up_their_own_curves()
        {
            var raw = new CreaturePerHit(330, new PerHitSource(1000, PropertyAttribute.Strength, 400, false), new PerHitSource(800, PropertyAttribute.Coordination, 420, true));
            var up = raw.ProjectedTo(410, PreDialCurve);

            Assert.AreEqual(410, up.Level);
            Assert.AreEqual(1000 * DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 330, 410, DungeonStatCurve.DefaultDefenseRateAbove375, DungeonStatCurve.DefaultMagicDefenseRateAbove300, 1.0, 326), up.Melee.BaseDamage, 1e-9);
            Assert.AreEqual(DungeonStatCurve.Scale(400, DungeonStatCurve.Ratio(DungeonStatAxis.Strength, 330, 410, 0.5, 0.5, 1.0, 326)), up.Melee.AttributeValue);
            Assert.AreEqual(DungeonStatCurve.Scale(420, DungeonStatCurve.Ratio(DungeonStatAxis.Coordination, 330, 410, 0.5, 0.5, 1.0, 326)), up.Missile.AttributeValue);
            Assert.IsTrue(up.Missile.IsBow);
            Assert.AreSame(raw, raw.ProjectedTo(300, PreDialCurve), "never projects downward");
            Assert.AreSame(raw, raw.ProjectedTo(330, PreDialCurve));
        }

        // ---- the baseline: what actually spawns ------------------------------------------------------------------

        /// <summary>A synthetic, deterministic per-hit read over the shipped roster's real levels: every wcid melees, a third shoot.</summary>
        private static CreaturePerHit SyntheticPerHit(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return CreaturePerHit.Empty;

            var melee = new PerHitSource(10 + wcid % 97 + level, PropertyAttribute.Strength, 100 + wcid % 50, false, isBody: true);
            var missile = wcid % 3 == 0 ? new PerHitSource(5 + wcid % 41 + level / 2.0, PropertyAttribute.Coordination, 150, true) : default;

            return new CreaturePerHit(level, melee, missile);
        }

        private static DungeonGemSpec Spec(int level, int seed) => new DungeonGemSpec("filos_doom", level, 6, "any", seed, null, 0, 0);

        private static DungeonPopulationLimits Limits(bool damageCeiling = true, double referenceRatio = DamageCeiling.DefaultReferenceRatio)
            => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, damageCeiling: damageCeiling, damageCeilingReferenceRatio: referenceRatio);

        private static (DungeonSpawnPlan Plan, int NextDraw) Build(int level, int seed, DungeonPopulationLimits limits, Func<uint, CreaturePerHit> perHitOf,
            ThreadPlanMemo memo = null, Func<uint, bool> fits = null)
        {
            Assert.IsNotNull(ShippedRosterFixture.Tables, "Content/events/axes/species not found from the test output directory");

            var rng = new Random(seed);
            var plan = DungeonPopulationBuilder.Build(Spec(level, seed), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, limits, rng,
                fitsDungeon: fits, memo: memo, perHitOf: perHitOf);

            return (plan, rng.Next());
        }

        private static DamageCeilingBaseline Baseline(int level, DungeonPopulationLimits limits, Func<uint, CreaturePerHit> perHitOf = null)
            => DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(level, 1), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, limits,
                perHitOf ?? SyntheticPerHit, null, null);

        /// <summary><see cref="SyntheticPerHit"/> with every seventh wcid hitting six times as hard, so the pool spreads past the cap.</summary>
        private static CreaturePerHit HeavyTailPerHit(uint wcid)
        {
            var p = SyntheticPerHit(wcid);
            return wcid % 7 == 0 ? new CreaturePerHit(p.Level, p.Melee.With(p.Melee.BaseDamage * 6, p.Melee.AttributeValue), p.Missile) : p;
        }

        private static readonly int[] Levels = { 200, 300, 410 };

        [TestMethod]
        public void The_median_and_top_are_the_same_on_every_run_at_one_level()
        {
            var seeds = new[] { 1, 2, 7, 1234 };

            foreach (var level in Levels)
            {
                var plans = seeds.Select(s => Build(level, s, Limits(), SyntheticPerHit).Plan).ToList();

                Assert.IsTrue(plans[0].PerHitMedian > 0, $"level {level}: positive control, a median was measured");
                Assert.IsTrue(plans[0].DamageCeilingTop > 1, $"level {level}: positive control, an automatic top was measured");
                Assert.IsTrue(plans.Select(p => ShippedRosterFixture.Fingerprint(p)).Distinct().Count() > 1, $"level {level}: guard, the seeds built different plans");

                foreach (var p in plans)
                {
                    Assert.AreEqual(plans[0].PerHitMedian, p.PerHitMedian, 0.0, $"level {level}: the median moved with the seed");
                    Assert.AreEqual(plans[0].DamageCeilingTop, p.DamageCeilingTop, 0.0, $"level {level}: top moved with the seed");
                    Assert.AreEqual(Cap, p.DamageCeilingCap);
                }
            }
        }

        [TestMethod]
        public void The_median_does_not_depend_on_the_dungeon_fit_filter()
        {
            foreach (var level in Levels)
            {
                var all = Build(level, 4, Limits(), SyntheticPerHit).Plan;
                var fitted = Build(level, 4, Limits(), SyntheticPerHit, fits: w => w % 5 != 0).Plan;

                Assert.IsTrue(all.PerHitMedian > 0, "positive control");
                Assert.AreEqual(all.PerHitMedian, fitted.PerHitMedian, 0.0, $"level {level}: a dungeon's fit filter moved the median");
                Assert.AreEqual(all.DamageCeilingTop, fitted.DamageCeilingTop, 0.0, $"level {level}: a dungeon's fit filter moved top");
            }
        }

        /// <summary>
        /// "What actually spawns": every trash and elite entry a plan draws, across seeds and levels, is a member of the
        /// baseline's pool (the pool is the union over every eligible family, so it must contain any single run's draw).
        /// </summary>
        [TestMethod]
        public void Every_creature_a_plan_draws_is_in_the_pool()
        {
            foreach (var level in Levels)
            {
                var pool = new HashSet<uint>(Baseline(level, Limits()).Members.Select(m => m.Wcid));
                var drawn = 0;

                Assert.IsTrue(pool.Count > 0, $"level {level}: positive control, a pool was resolved");

                foreach (var seed in Enumerable.Range(1, 40))
                {
                    foreach (var e in Build(level, seed, Limits(), SyntheticPerHit).Plan.Entries.Where(e => e.Role != DungeonRole.Boss))
                    {
                        drawn++;
                        Assert.IsTrue(pool.Contains(e.Wcid), $"level {level} seed {seed}: drew {e.Wcid} ({e.Role}), which is not in the pool");
                    }
                }

                Assert.IsTrue(drawn > 100, $"level {level}: guard, the plans drew creatures ({drawn})");
            }
        }

        [TestMethod]
        public void Pool_members_carry_the_tag_the_builder_gives_a_pick()
        {
            foreach (var level in Levels)
            {
                var plans = Enumerable.Range(1, 40).Select(s => Build(level, s, Limits(), SyntheticPerHit).Plan).ToList();
                var members = Baseline(level, Limits()).Members.ToDictionary(m => m.Wcid);

                foreach (var e in plans.SelectMany(p => p.Entries).Where(e => e.Role != DungeonRole.Boss))
                {
                    var expected = e.UpliftLevel > 0 ? DamageCeilingPlacement.Uplifted : e.StampLevel > 0 ? DamageCeilingPlacement.Stamped : DamageCeilingPlacement.Natural;
                    Assert.AreEqual(expected, members[e.Wcid].Placement, $"level {level}: wcid {e.Wcid} tagged differently from its pick");
                }
            }
        }

        [TestMethod]
        public void A_stamped_members_prediction_uses_the_live_curve_rates_and_pivot()
        {
            // Owner ruling 2026-10-29: the ceiling's median must be taken over the damage the spawner really fields, so a
            // stamped member is projected with the run's three rates and the damage split at the run's pivot (326 on the
            // shipped roster), exactly as plan.StatCurve.Ratio gives the live stamp. Distinct rates: a swap fails here.
            Func<uint, DungeonStatProfile> profileOf = w => new DungeonStatProfile(ShippedRosterFixture.LevelOf(w), 1000, new Dictionary<Skill, uint>(), 100, 100, 0, 0.0);

            foreach (var damageRate in new[] { 0.75, 1.0 })
            {
                var limits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, defenseCurveRateAbove375: 0.25, magicDefenseCurveRateAbove300: 0.5,
                    damageCurveRateAbovePivot: damageRate);
                var baseline = DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(410, 1), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, limits,
                    SyntheticPerHit, profileOf, null);
                var stamped = baseline.Members.Where(m => m.Placement == DamageCeilingPlacement.Stamped).ToList();

                Assert.IsTrue(stamped.Count > 0, $"damage {damageRate}: positive control, the pool holds stamped members");

                double Live(DungeonStatAxis axis, int from, int to) => DungeonStatCurve.Ratio(axis, from, to, 0.25, 0.5, damageRate, 326);

                foreach (var m in stamped)
                {
                    var expected = SyntheticPerHit(m.Wcid).WithLevel(m.AuthoredLevel).ProjectedTo(m.ProjectedLevel, Live);
                    Assert.AreEqual(expected.MeleePerHit, m.Predicted.MeleePerHit, 1e-9, $"damage {damageRate}: wcid {m.Wcid} melee");
                    Assert.AreEqual(expected.MissilePerHit, m.Predicted.MissilePerHit, 1e-9, $"damage {damageRate}: wcid {m.Wcid} missile");
                }

                if (damageRate < 1.0)
                {
                    var m = stamped.First(x => x.ProjectedLevel > 326 && x.Predicted.MeleePerHit > 0);
                    var full = SyntheticPerHit(m.Wcid).WithLevel(m.AuthoredLevel).ProjectedTo(m.ProjectedLevel, (a, f, t) => DungeonStatCurve.Ratio(a, f, t, 0.25, 0.5, 1.0, 326));
                    Assert.IsTrue(m.Predicted.MeleePerHit < full.MeleePerHit, "discriminating: 0.75 predicts less than the full slope");
                }
            }

            // The PIVOT (code review 2026-10-29 F1b). At the shipped band low of 1.0 every stamped member is authored at or
            // above the pivot (it projects by L / P onto a band that starts at L), so the split point is invisible there. A
            // band low of 0.9 admits stamped members authored below 326 and projected above it: their damage ratio is the
            // full slope up to 326 and the softened one above, so a wrong pivot moves it.
            var lowBand = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, defenseCurveRateAbove375: 0.25, magicDefenseCurveRateAbove300: 0.5,
                damageCurveRateAbovePivot: 0.75, rosterBandLow: 0.9);
            var straddling = DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(410, 1), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, lowBand,
                SyntheticPerHit, profileOf, null).Members
                .Where(m => m.Placement == DamageCeilingPlacement.Stamped && m.AuthoredLevel < 326 && m.ProjectedLevel > 326 && m.Predicted.MeleePerHit > 0).ToList();

            Assert.IsTrue(straddling.Count > 0, "positive control: a band low of 0.9 stamps members authored below the pivot");

            foreach (var m in straddling)
            {
                var a = m.AuthoredLevel;
                var shape = Math.Exp(DungeonStatCurve.DamageSlope * (326 - a) + 0.75 * DungeonStatCurve.DamageSlope * (m.ProjectedLevel - 326));
                Assert.AreEqual(SyntheticPerHit(m.Wcid).Melee.BaseDamage * shape, m.Predicted.Melee.BaseDamage, 1e-6,
                    $"wcid {m.Wcid} ({a} -> {m.ProjectedLevel}): full slope to the pivot, 0.75 above it");
            }
        }

        /// <summary>
        /// A BAND-uplifted member's prediction in the ceiling baseline (StandardAtRun -> UpliftedByBand) raises its body
        /// source to the run standard's MaxBodyDamage, and above the pivot that standard is the anchored curve at the run's
        /// damage rate (code review 2026-10-29 F1a: the baseline's Anchored call at 1.0 broke no test). Body damage of 10
        /// keeps every uplifted member under the standard, so the raise is visible.
        /// </summary>
        [TestMethod]
        public void A_band_uplifted_members_prediction_reads_the_standard_at_the_live_damage_rate()
        {
            Func<uint, DungeonStatProfile> profileOf = w => new DungeonStatProfile(ShippedRosterFixture.LevelOf(w), 1000, new Dictionary<Skill, uint>(), 100, 100, 0, 0.0);
            CreaturePerHit WeakBody(uint w) => new CreaturePerHit(ShippedRosterFixture.LevelOf(w), new PerHitSource(10, PropertyAttribute.Strength, 100, false, isBody: true), default);

            var byRate = new Dictionary<double, double>();

            foreach (var damageRate in new[] { 0.75, 1.0 })
            {
                var limits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, damageCurveRateAbovePivot: damageRate, archetypeUplift: false);
                var uplifted = DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(410, 1), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, limits,
                    WeakBody, profileOf, null).Members.Where(m => m.Placement == DamageCeilingPlacement.Uplifted).ToList();

                Assert.IsTrue(uplifted.Count > 0, $"damage {damageRate}: positive control, the pool holds uplifted members");

                var measured = DungeonBandStandard.Compute(ShippedRosterFixture.Tables, 326, profileOf,
                    new DungeonRosterBand(DungeonRosterSelector.BandLow, DungeonRosterSelector.BandHigh), DungeonRosterSelector.BandLowFloor);
                var standard = new DungeonStatCurve.Anchored(measured, 326, DungeonStatCurve.DefaultDefenseRateAbove375,
                    DungeonStatCurve.DefaultMagicDefenseRateAbove300, damageRate).StandardAt(410);

                Assert.AreEqual(DungeonStatCurve.Scale(100, DungeonStatCurve.Ratio(DungeonStatAxis.Damage, 326, 410, 0.5, 0.0, damageRate, 326)), standard.MaxBodyDamage,
                    $"damage {damageRate}: the standard's body damage is the pivot's 100 carried on the softened curve");

                foreach (var m in uplifted)
                    Assert.AreEqual(Math.Max(10.0, standard.MaxBodyDamage), m.Predicted.Melee.BaseDamage, 0.0, $"damage {damageRate}: wcid {m.Wcid}");

                byRate[damageRate] = standard.MaxBodyDamage;
            }

            Assert.IsTrue(byRate[0.75] < byRate[1.0], $"discriminating: 0.75 raises to less than 1.0 ({byRate[0.75]} vs {byRate[1.0]})");
        }

        [TestMethod]
        public void The_median_is_the_lower_median_of_each_members_worst_hit()
        {
            foreach (var level in Levels)
            {
                var baseline = Baseline(level, Limits());
                var worst = baseline.Members.Select(m => Math.Max(m.Predicted.MeleePerHit, m.Predicted.MissilePerHit)).Where(v => v > 0).OrderBy(v => v).ToList();

                Assert.IsTrue(worst.Count >= 5, $"level {level}: guard, a real sample");
                Assert.AreEqual(worst.Count, baseline.SampleSize);
                Assert.AreEqual(worst[(worst.Count - 1) / 2], baseline.Median, 0.0, $"level {level}: the LOWER median of the worst hits");
                Assert.AreEqual(worst[worst.Count - 1] / baseline.Median, baseline.PoolMaxRatio, 1e-12, $"level {level}: the pool max ratio");
            }
        }

        /// <summary>
        /// The single-archer artifact (owner decision 2, 2026-10-26): with one shared median an archer is measured against
        /// the same number as everyone else. A lone archer hitting 5x the crowd is compressed, where a missile-only median
        /// over that one archer would have read it at 1x and left it alone.
        /// </summary>
        [TestMethod]
        public void A_lone_archer_is_measured_against_the_shared_median()
        {
            CreaturePerHit Melee(double hit) => new CreaturePerHit(300, new PerHitSource(hit, PropertyAttribute.Strength, 55, false, isBody: true), default);
            var reads = new Dictionary<uint, CreaturePerHit>
            {
                [1] = Melee(100), [2] = Melee(200), [3] = Melee(300), [4] = Melee(400), [5] = Melee(500),
                [6] = new CreaturePerHit(300, default, new PerHitSource(1500, PropertyAttribute.Coordination, 55, true)),
            };
            var pool = reads.Keys.Select(w => (w, true, false)).ToList();

            var baseline = DamageCeilingStandard.Compute(pool, w => reads[w], w => 300, w => 300, 300, 300, null, null);

            Assert.AreEqual(300.0, baseline.Median, 0.0, "lower median of 100, 200, 300, 400, 500, 1500");
            Assert.AreEqual(5.0, baseline.PoolMaxRatio, 1e-12, "the archer is the worst hitter");
            Assert.AreEqual(Cap, DamageCeiling.Compress(1500 / baseline.Median, Cap, baseline.TopFor(0)), 1e-12, "auto top puts the archer exactly on the cap");
            Assert.IsTrue(DamageCeiling.FinalPerHit(1500, baseline.Median, Cap, baseline.TopFor(0), 0, 0) < 1500, "the archer is compressed");
        }

        [TestMethod]
        public void Auto_top_puts_the_worst_hitter_exactly_on_the_cap_and_a_positive_dial_forces_top()
        {
            foreach (var level in Levels)
            {
                var auto = Build(level, 2, Limits(), HeavyTailPerHit).Plan;
                var forced = Build(level, 2, Limits(referenceRatio: 5.5), HeavyTailPerHit).Plan;
                var baseline = Baseline(level, Limits(), HeavyTailPerHit);

                Assert.IsTrue(baseline.PoolMaxRatio > Cap, $"level {level}: guard, the heavy tail spreads the pool past the cap ({baseline.PoolMaxRatio})");
                Assert.AreEqual(baseline.PoolMaxRatio, auto.DamageCeilingTop, 1e-12, $"level {level}: auto top is the pool max ratio");
                Assert.AreEqual(5.5, forced.DamageCeilingTop, 0.0, $"level {level}: a positive dial forces top");
                Assert.AreEqual(auto.PerHitMedian, forced.PerHitMedian, 0.0, "the dial never moves the median");

                var finals = baseline.Members.Where(m => m.PerHit > 0)
                    .Select(m => DamageCeiling.TargetPerHit(m.PerHit, auto.PerHitMedian, Cap, auto.DamageCeilingTop) / auto.PerHitMedian).ToList();

                Assert.AreEqual(Cap, finals.Max(), 1e-9, $"level {level}: the worst predicted hitter lands exactly on the cap");
                Assert.IsTrue(finals.All(r => r <= Cap + 1e-9), $"level {level}: nobody above the cap");

                // A narrow pool (worst ratio at or below the cap): auto top is then a plain clamp that changes nobody.
                var narrow = Baseline(level, Limits());
                Assert.IsTrue(narrow.PoolMaxRatio <= Cap, $"level {level}: guard, the synthetic pool is narrow ({narrow.PoolMaxRatio})");
                Assert.IsTrue(narrow.Members.Where(m => m.PerHit > 0).All(m =>
                    Math.Abs(DamageCeiling.TargetPerHit(m.PerHit, narrow.Median, Cap, narrow.TopFor(0)) - m.PerHit) <= 1e-9 * m.PerHit), $"level {level}: a narrow pool is untouched");
            }

            Assert.AreEqual(0.0, DamageCeilingBaseline.Empty.TopFor(0));
            Assert.AreEqual(4.0, new DamageCeilingBaseline(10, 6.0, null).TopFor(4.0));
            Assert.AreEqual(6.0, new DamageCeilingBaseline(10, 6.0, null).TopFor(0.0), "0 is auto");
            Assert.AreEqual(6.0, new DamageCeilingBaseline(10, 6.0, null).TopFor(double.NaN), "garbled is auto");
        }

        [TestMethod]
        public void The_plan_fingerprint_rng_stream_and_notes_are_unchanged_by_the_ceiling()
        {
            foreach (var level in new[] { 200, 410 })
            {
                var without = Build(level, 3, Limits(), null);
                var with = Build(level, 3, Limits(), SyntheticPerHit);
                var off = Build(level, 3, Limits(damageCeiling: false), SyntheticPerHit);

                Assert.IsTrue(with.Plan.PerHitMedian > 0, "positive control");
                Assert.AreEqual(0.0, without.Plan.PerHitMedian, "no delegate, no median");
                Assert.AreEqual(0.0, off.Plan.PerHitMedian, "switch off, no median");
                Assert.AreEqual(0.0, off.Plan.DamageCeilingTop, "switch off, no top");

                foreach (var (name, other) in new[] { ("no delegate", without), ("switch off", off) })
                {
                    Assert.AreEqual(ShippedRosterFixture.Fingerprint(with.Plan), ShippedRosterFixture.Fingerprint(other.Plan), $"level {level} {name}: fingerprint moved");
                    Assert.AreEqual(with.NextDraw, other.NextDraw, $"level {level} {name}: the ceiling drew from the rng");
                    Assert.AreEqual(with.Plan.Notes.Count, other.Plan.Notes.Count, $"level {level} {name}: a note was added");
                }
            }
        }

        [TestMethod]
        public void The_memoised_build_measures_the_same_baseline()
        {
            ThreadPlanCache.Invalidate();

            try
            {
                foreach (var level in new[] { 200, 410 })
                {
                    var direct = Build(level, 5, Limits(), SyntheticPerHit).Plan;

                    for (var pass = 0; pass < 2; pass++)
                    {
                        var memo = ThreadPlanCache.ForRun(true, null, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, null, perHitOf: SyntheticPerHit);
                        var cached = Build(level, 5, Limits(), memo.PerHitOf, memo).Plan;

                        Assert.AreEqual(direct.PerHitMedian, cached.PerHitMedian, 0.0, $"level {level} pass {pass}");
                        Assert.AreEqual(direct.DamageCeilingTop, cached.DamageCeilingTop, 0.0, $"level {level} pass {pass}");
                        Assert.AreEqual(direct.PerHitSample, cached.PerHitSample, $"level {level} pass {pass}");

                        if (pass == 1)
                            Assert.IsTrue(memo.Hits > 0, "the second pass was served from the cache");
                    }
                }
            }
            finally
            {
                ThreadPlanCache.Invalidate();
            }
        }

        // ---- the per-member prediction --------------------------------------------------------------------------

        [TestMethod]
        public void Predict_tags_a_member_as_the_builder_tags_a_pick()
        {
            var raw = new CreaturePerHit(300, new PerHitSource(1000, PropertyAttribute.Strength, 400, false, isBody: true), default);
            var lifted = new CreaturePerHit(410, new PerHitSource(5000, PropertyAttribute.Strength, 400, false, isBody: true), default);
            CreaturePerHit Uplift(uint w, CreaturePerHit r) => lifted;

            var below = DamageCeilingStandard.Predict(1, raw, 380, 300, 410, 410, PreDialCurve, Uplift);
            Assert.AreEqual(DamageCeilingPlacement.Uplifted, below.Placement, "projected below the natural low edge");
            Assert.AreSame(lifted, below.Predicted);

            var noUplift = DamageCeilingStandard.Predict(1, raw, 380, 300, 410, 410, PreDialCurve, null);
            Assert.AreEqual(410, noUplift.Predicted.Level, "no uplift prediction: the level alone");
            Assert.AreEqual(1000, noUplift.Predicted.Melee.BaseDamage, 0.0);

            var stamped = DamageCeilingStandard.Predict(1, raw, 420, 300, 410, 410, PreDialCurve, Uplift);
            Assert.AreEqual(DamageCeilingPlacement.Stamped, stamped.Placement);
            Assert.AreEqual(raw.ProjectedTo(420, PreDialCurve).MeleePerHit, stamped.Predicted.MeleePerHit, 1e-9, "stamped on the curve");
            Assert.IsTrue(stamped.Predicted.MeleePerHit > raw.MeleePerHit, "guard: the curve raised it");

            var flat = DamageCeilingStandard.Predict(1, raw, 420, 300, 410, 410, null, Uplift);
            Assert.AreEqual(DamageCeilingPlacement.Stamped, flat.Placement);
            Assert.AreEqual(raw.MeleePerHit, flat.Predicted.MeleePerHit, 0.0, "no stat curve: a stamp moves the level only");
            Assert.AreEqual(420, flat.Predicted.Level);

            var natural = DamageCeilingStandard.Predict(1, raw, 300, 300, 290, 290, PreDialCurve, Uplift);
            Assert.AreEqual(DamageCeilingPlacement.Natural, natural.Placement);
            Assert.AreEqual(raw.MeleePerHit, natural.Predicted.MeleePerHit, 0.0);
        }

        private static ArchetypeUpliftInfo UpliftInfo(double cap)
        {
            DungeonBandStandard Std(uint scale) => DungeonBandStandard.ForTest(null, 20 * scale, 100 * scale,
                attributeMedians: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 100 * scale, [PropertyAttribute.Coordination] = 100 * scale });

            return new ArchetypeUpliftInfo
            {
                Wcid = 1,
                RunLevel = 400,
                OwnLevel = 200,
                Run = new ArchetypeAxes(Std(2), 40, 0, 0, 0, 0, 0, 0, 8, 1.0),
                Own = new ArchetypeAxes(Std(1), 20, 0, 0, 0, 0, 0, 0, 8, 1.0),
                OffenseRatio = 1.0,
                DefenseRatio = 1.0,
                CapRatio = cap,
            };
        }

        [TestMethod]
        public void The_archetype_uplift_prediction_raises_every_source_onto_one_target_and_never_lowers()
        {
            var raw = new CreaturePerHit(200, new PerHitSource(10, PropertyAttribute.Strength, 50, false, isBody: true),
                new PerHitSource(100, PropertyAttribute.Coordination, 150, true));

            // No cap: own 100 (the best source), target = max(100, 40, 100 x 40 / 20) = 200.
            var open = raw.UpliftedByArchetype(400, UpliftInfo(0));
            var expectedTarget = ArchetypeUplift.AxisTarget(100, 40, 20, 1.0, 0);

            Assert.AreEqual(200.0, expectedTarget, 1e-9, "guard: the rule's own number");
            Assert.AreEqual(400, open.Level);
            Assert.AreEqual(expectedTarget, open.Melee.BaseDamage, 1e-9, "the weaker source is raised onto the one target");
            Assert.AreEqual(expectedTarget, open.Missile.BaseDamage, 1e-9);
            Assert.AreEqual(200u, open.Melee.AttributeValue, "Strength 50 -> max(50, 200, 50 x 200 / 100) = 200");
            Assert.AreEqual(300u, open.Missile.AttributeValue, "Coordination 150 -> max(150, 200, 150 x 200 / 100) = 300");
            Assert.IsTrue(open.Missile.IsBow && open.Melee.IsBody, "the source kinds are kept");

            // Cap 1.25: target = max(own 100, min(200, 1.25 x 40 = 50)) = 100; the 100 source is never lowered.
            var capped = raw.UpliftedByArchetype(400, UpliftInfo(1.25));

            Assert.AreEqual(100.0, capped.Melee.BaseDamage, 1e-9);
            Assert.AreEqual(100.0, capped.Missile.BaseDamage, 1e-9, "never below its own damage");
            Assert.AreEqual(raw.Melee.Attribute, capped.Melee.Attribute);

            Assert.AreEqual(raw.MeleePerHit, raw.UpliftedByArchetype(400, null).MeleePerHit, 0.0, "no info: the level alone");
        }

        [TestMethod]
        public void The_band_uplift_prediction_raises_the_body_only_and_floors_carried_attributes()
        {
            var standard = DungeonBandStandard.ForTest(null, 500, 0,
                attributeMedians: new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 300 });
            var body = new CreaturePerHit(200, new PerHitSource(200, PropertyAttribute.Strength, 100, false, isBody: true), default);
            var armed = new CreaturePerHit(200, new PerHitSource(200, PropertyAttribute.Strength, 100, false), default);

            var b = body.UpliftedByBand(400, standard, true);
            Assert.AreEqual(500.0, b.Melee.BaseDamage, 0.0, "the body's best part rises to the standard's MaxBodyDamage");
            Assert.AreEqual(300u, b.Melee.AttributeValue);

            var a = armed.UpliftedByBand(400, standard, true);
            Assert.AreEqual(200.0, a.Melee.BaseDamage, 0.0, "a weapon is never touched by the band uplift");
            Assert.AreEqual(300u, a.Melee.AttributeValue, "the attribute is still raised");

            Assert.AreEqual(100u, body.UpliftedByBand(400, standard, false).Melee.AttributeValue, "attribute raise off");

            var strong = new CreaturePerHit(200, new PerHitSource(900, PropertyAttribute.Strength, 400, false, isBody: true), default).UpliftedByBand(400, standard, true);
            Assert.AreEqual(900.0, strong.Melee.BaseDamage, 0.0, "never lowered");
            Assert.AreEqual(400u, strong.Melee.AttributeValue, "never lowered");

            Assert.AreEqual(body.MeleePerHit, body.UpliftedByBand(400, null, true).MeleePerHit, 0.0, "no standard: the level alone");
        }

        [TestMethod]
        public void ProjectedTo_carries_damage_and_the_attribute_up_their_own_curves_and_keeps_the_source_kind()
        {
            var raw = new CreaturePerHit(330, new PerHitSource(1000, PropertyAttribute.Strength, 400, false, isBody: true), default);

            Assert.IsTrue(raw.ProjectedTo(410, PreDialCurve).Melee.IsBody);
        }

        [TestMethod]
        public void A_per_hit_read_that_returns_nothing_leaves_the_median_zero()
        {
            foreach (var level in new[] { 200, 410 })
            {
                var plan = Build(level, 3, Limits(), _ => CreaturePerHit.Empty).Plan;

                Assert.AreEqual(0.0, plan.PerHitMedian, $"level {level}: an empty read (a cold world cache) measures no median");
                Assert.AreEqual(0.0, plan.DamageCeilingTop, $"level {level}: and no top");
                Assert.IsTrue(plan.Entries.Count > 0, $"level {level}: guard, the plan itself still built");
            }
        }

        // ---- the weenie read ---------------------------------------------------------------------------------

        [TestMethod]
        public void FromWeenie_reads_the_best_body_part_and_the_wielded_weapons_on_their_own_axes()
        {
            var sword = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900001, WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = 300, [PropertyInt.WeaponSkill] = (int)Skill.FinesseWeapons },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = 1.5 },
            };
            var bow = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900002, WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.DefaultCombatStyle] = (int)CombatStyle.Bow },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = 2.0 },
            };
            var arrow = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900003, WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = 100 },
            };
            var creature = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900000, WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 250 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, ACE.Entity.Models.PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new ACE.Entity.Models.PropertiesAttribute { InitLevel = 200, LevelFromCP = 10 },
                    [PropertyAttribute.Coordination] = new ACE.Entity.Models.PropertiesAttribute { InitLevel = 300 },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>
                {
                    [CombatBodyPart.Head] = new ACE.Entity.Models.PropertiesBodyPart { DVal = 200 },
                    [CombatBodyPart.Breath] = new ACE.Entity.Models.PropertiesBodyPart { DVal = 420 },
                },
                PropertiesCreateList = new List<ACE.Entity.Models.PropertiesCreateList>
                {
                    new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 900001 },
                    new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 900002 },
                    new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 900003 },
                    new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Contain, WeenieClassId = 900001 },
                },
            };

            var items = new[] { sword, bow, arrow }.ToDictionary(w => w.WeenieClassId);
            var perHit = CreaturePerHit.FromWeenie(creature, w => items.TryGetValue(w, out var i) ? i : null);

            var finesse = DamageCeiling.Predict(450, 300, false);

            Assert.AreEqual(250, perHit.Level);
            Assert.AreEqual(finesse, perHit.MeleePerHit, 1e-6, "melee: the wielded finesse sword (Coordination, DamageMod); the body is not read while a weapon is wielded");
            Assert.AreEqual(PropertyAttribute.Coordination, perHit.Melee.Attribute);
            Assert.AreEqual(DamageCeiling.Predict(200, 300, true), perHit.MissilePerHit, 1e-6, "missile: the arrow through the bow's DamageMod at the bow factor");
            Assert.IsTrue(perHit.Missile.IsBow);
        }

        [TestMethod]
        public void FromWeenie_ignores_the_body_while_a_melee_weapon_is_wielded()
        {
            var sword = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900011, WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = 100 },
            };

            ACE.Entity.Models.Weenie Creature(bool wieldsSword) => new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900010, WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, ACE.Entity.Models.PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new ACE.Entity.Models.PropertiesAttribute { InitLevel = 200 },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>
                {
                    [CombatBodyPart.Hand] = new ACE.Entity.Models.PropertiesBodyPart { DVal = 500 },
                },
                PropertiesCreateList = wieldsSword
                    ? new List<ACE.Entity.Models.PropertiesCreateList> { new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 900011 } }
                    : null,
            };

            ACE.Entity.Models.Weenie ItemOf(uint wcid) => wcid == 900011 ? sword : null;

            var armed = CreaturePerHit.FromWeenie(Creature(true), ItemOf);
            var bare = CreaturePerHit.FromWeenie(Creature(false), ItemOf);

            Assert.AreEqual(DamageCeiling.Predict(500, 200, false), bare.MeleePerHit, 1e-6, "positive control: without a weapon the body is the melee source");
            Assert.AreEqual(DamageCeiling.Predict(100, 200, false), armed.MeleePerHit, 1e-6, "with a sword wielded every melee hit is the sword's");
        }

        // ---- code review 2026-10-26: F1 (the archetype prediction's DamageRating offset and own-damage read) -----------

        /// <summary>meanOf for the spell-reader tests: a Flame Bolt at tier 3 or above means 100, below it 10, anything else 0.</summary>
        private static double FlameBoltMean(int id)
        {
            var tier = DungeonSpellTier.TierOf(id);
            return tier >= 3 ? 100.0 : tier > 0 ? 10.0 : 0.0;
        }

        private static ACE.Entity.Models.Weenie SpellSwordCreature(int? wieldedTreasureType = null, bool withSword = true)
        {
            var creature = new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900020, WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Level] = 200 },
                PropertiesAttribute = new Dictionary<PropertyAttribute, ACE.Entity.Models.PropertiesAttribute>
                {
                    [PropertyAttribute.Strength] = new ACE.Entity.Models.PropertiesAttribute { InitLevel = 200 },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, ACE.Entity.Models.PropertiesBodyPart>
                {
                    [CombatBodyPart.Hand] = new ACE.Entity.Models.PropertiesBodyPart { DVal = 50 },
                },
                PropertiesSpellBook = new Dictionary<int, float> { [(int)SpellId.FlameBolt1] = 0.5f },
                PropertiesCreateList = withSword
                    ? new List<ACE.Entity.Models.PropertiesCreateList> { new ACE.Entity.Models.PropertiesCreateList { DestinationType = DestinationType.Wield, WeenieClassId = 900021 } }
                    : null,
            };

            if (wieldedTreasureType.HasValue)
                creature.PropertiesDID = new Dictionary<PropertyDataId, uint> { [PropertyDataId.WieldedTreasureType] = (uint)wieldedTreasureType.Value };

            return creature;
        }

        private static ACE.Entity.Models.Weenie SpellSwordItem(uint wcid) => wcid == 900021
            ? new ACE.Entity.Models.Weenie
            {
                WeenieClassId = 900021, WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int> { [PropertyInt.Damage] = 100 },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { [PropertyFloat.DamageMod] = 1.5 },
            }
            : null;

        [TestMethod]
        public void The_spell_reader_reads_the_final_book_raised_to_each_tier_then_stripped()
        {
            var book = new Dictionary<int, float> { [(int)SpellId.FlameBolt1] = 0.5f };
            var means = new PerHitSpellReader(SpellStripConfig.None, _ => 0, FlameBoltMean).MeansByTier(book, true);

            Assert.AreEqual(PerHitSpellReader.MaxTier + 1, means.Count);
            Assert.AreEqual(10.0, means[0], 0.0, "tier 0: the authored book (Flame Bolt I)");
            Assert.AreEqual(10.0, means[2], 0.0, "raised to tier 2");
            Assert.AreEqual(100.0, means[3], 0.0, "raised to tier 3");
            Assert.AreEqual(100.0, means[8], 0.0);
            Assert.AreEqual(1, book.Count, "the authored book is never touched");
            Assert.IsTrue(book.ContainsKey((int)SpellId.FlameBolt1));

            // The strip runs on the RAISED book: banning Flame Bolt III removes it at tier 3 only.
            var banned = new SpellStripConfig(true, new[] { (int)SpellId.FlameBolt3 });
            var stripped = new PerHitSpellReader(banned, _ => 0, FlameBoltMean).MeansByTier(book, true);

            Assert.AreEqual(0.0, stripped[3], 0.0, "the raised id is banned and stripped");
            Assert.AreEqual(100.0, stripped[4], 0.0);
            Assert.AreEqual(10.0, stripped[0], 0.0);
        }

        [TestMethod]
        public void FromWeenie_reads_the_uplifts_own_damage_body_included_and_the_weapon_without_its_DamageMod()
        {
            var perHit = CreaturePerHit.FromWeenie(SpellSwordCreature(), SpellSwordItem, new PerHitSpellReader(SpellStripConfig.None, _ => 0, FlameBoltMean));

            Assert.AreEqual(150.0, perHit.Melee.BaseDamage, 1e-9, "the ENGINE's hit: Damage x DamageMod, the body ignored with a sword wielded");
            Assert.IsFalse(perHit.Melee.IsBody);
            Assert.AreEqual(1.5, perHit.Melee.UpliftScale, 1e-12);
            Assert.AreEqual(100.0, perHit.Melee.UpliftEffective, 1e-9, "the UPLIFT's read of the sword: its bare Damage (DamageSourcesOf)");
            Assert.AreEqual(100.0, perHit.UpliftInputs.OwnDamage, 1e-9, "own = max(body 50, sword 100)");
            Assert.AreEqual(0, perHit.UpliftInputs.DamageRating);
            Assert.AreEqual(100.0, perHit.UpliftInputs.SpellMeanAt(3), 0.0);
            Assert.AreEqual(10.0, perHit.UpliftInputs.SpellMeanAt(0), 0.0);
            Assert.IsFalse(perHit.GearUnknown);

            // A body that out-hits the weapon sets own (the live uplift reads bodyMax even while a weapon is wielded).
            var brute = SpellSwordCreature();
            brute.PropertiesBodyPart[CombatBodyPart.Hand] = new ACE.Entity.Models.PropertiesBodyPart { DVal = 300 };
            var brutePerHit = CreaturePerHit.FromWeenie(brute, SpellSwordItem);

            Assert.AreEqual(300.0, brutePerHit.UpliftInputs.OwnDamage, 1e-9, "own reads the body");
            Assert.AreEqual(150.0, brutePerHit.Melee.BaseDamage, 1e-9, "the output is still the sword's hit");
            Assert.AreEqual(0.0, brutePerHit.UpliftInputs.SpellMeanAt(3), 0.0, "no spell reader, no spell means");
        }

        private static ArchetypeUpliftInfo SpellTopUpInfo()
        {
            var attributes = new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = 100 };

            return new ArchetypeUpliftInfo
            {
                Wcid = 900020,
                RunLevel = 400,
                OwnLevel = 200,
                Run = new ArchetypeAxes(DungeonBandStandard.ForTest(null, 0, 0, spellTier: 3, attributeMedians: attributes), 400, 200, 0, 0, 0, 0, 0, 8, 1.0),
                Own = new ArchetypeAxes(DungeonBandStandard.ForTest(null, 0, 0, spellTier: 1, attributeMedians: attributes), 100, 100, 0, 0, 0, 0, 0, 8, 1.0),
                OffenseRatio = 1.0,
                DefenseRatio = 1.0,
                CapRatio = 0.0,
            };
        }

        /// <summary>
        /// F1 (code review 2026-10-26): a caster whose spell output needs a DamageRating top-up has its PHYSICAL hit divided
        /// back by that top-up (ArchetypeUplift.Apply: SourceMultiplier with DamageRatingOffset). The prediction must equal
        /// the live write - the source's base x SourceMultiplier(its effective value, the one target, the offset) - not
        /// max(base, target) as before.
        ///
        /// By hand: the book raised to the run's tier 3 means 100 (own spell output 100 at DR 0, run standard 200 over own
        /// standard 100, target 200), so FloorRatings writes DR 100, all of it for spells (the rating axis floors at 0).
        /// With the gem's additive 20 the total is 120 and the offset mod(20) / mod(120) = 1.2 / 2.2. Own damage is the
        /// sword's bare 100 (the body's 50 is lower), the target max(100, 400, 100 x 400 / 100) = 400, so the sword's
        /// multiplier is 4 x 1.2 / 2.2 and its hit 150 x that.
        /// </summary>
        [TestMethod]
        public void The_archetype_prediction_divides_the_physical_hit_by_the_spell_rating_top_up_exactly_as_the_live_uplift()
        {
            var info = SpellTopUpInfo();
            var raw = CreaturePerHit.FromWeenie(SpellSwordCreature(), SpellSwordItem, new PerHitSpellReader(SpellStripConfig.None, _ => 0, FlameBoltMean));

            var floor = ArchetypeUplift.DamageRatingFloor(info, 0, raw.UpliftInputs.SpellMeanAt(info.Run.Core.SpellTier));
            Assert.AreEqual((100, 0, 100, 100), floor, "guard: the rating floor is spell-driven (DrTo, DrRating, DrSpell, Added)");

            var offset = ArchetypeUplift.DamageRatingOffset(100 + 20, 100);
            Assert.AreEqual(1.2 / 2.2, offset, 1e-12);
            Assert.AreEqual(offset, raw.ArchetypeDamageRatingOffset(info, 20), 1e-12);

            var predicted = raw.UpliftedByArchetype(400, info, gemDamageRating: 20);

            Assert.IsTrue(predicted.ByArchetype);
            Assert.AreEqual(150.0 * ArchetypeUplift.SourceMultiplier(100, 400, offset), predicted.Melee.BaseDamage, 1e-9, "the live write: base x SourceMultiplier");
            Assert.AreEqual(150.0 * 4.0 * 1.2 / 2.2, predicted.Melee.BaseDamage, 1e-9, "by hand");
            Assert.AreEqual(200u, predicted.Melee.AttributeValue, "Strength 200 is above the run median: unchanged");

            // Positive control: with no top-up (no spell reader, so no projectile mean) the same creature reaches the full target.
            var noSpells = CreaturePerHit.FromWeenie(SpellSwordCreature(), SpellSwordItem).UpliftedByArchetype(400, info, gemDamageRating: 20);
            Assert.AreEqual(150.0 * 4.0, noSpells.Melee.BaseDamage, 1e-9, "offset 1: the sword lands on target x its DamageMod");

            // The offset never lowers a source below authored.
            var weak = new ArchetypeUpliftInfo { Run = new ArchetypeAxes(info.Run.Core, 100, 200, 0, 0, 0, 0, 0, 8, 1.0), Own = info.Own, OffenseRatio = 1.0, DefenseRatio = 1.0 };
            Assert.AreEqual(150.0, raw.UpliftedByArchetype(400, weak, 20).Melee.BaseDamage, 1e-9, "target = own 100, x offset < 1: never below authored");
        }

        // ---- F3: gear rolled from a wielded-treasure table ------------------------------------------------------------

        [TestMethod]
        public void FromWeenie_flags_every_wielded_treasure_carrier_as_unknown()
        {
            Assert.IsTrue(CreaturePerHit.FromWeenie(SpellSwordCreature(wieldedTreasureType: 42, withSword: false), SpellSwordItem).GearUnknown,
                "treasure-rolled gear and no Wield weapon: unknown");
            Assert.IsTrue(CreaturePerHit.FromWeenie(SpellSwordCreature(wieldedTreasureType: 42, withSword: true), SpellSwordItem).GearUnknown,
                "a Wield-row weapon does not make it known: the rolled one can out-hit it");
            Assert.IsFalse(CreaturePerHit.FromWeenie(SpellSwordCreature(withSword: false), SpellSwordItem).GearUnknown, "no treasure table: the body is the gear");
            Assert.IsFalse(CreaturePerHit.FromWeenie(SpellSwordCreature(wieldedTreasureType: 0, withSword: false), SpellSwordItem).GearUnknown, "a zero table id is no table");
        }

        [TestMethod]
        public void Gear_unknown_members_stay_in_the_pool_but_out_of_the_median_and_top()
        {
            CreaturePerHit Melee(double hit, bool unknown = false)
                => new CreaturePerHit(300, new PerHitSource(hit, PropertyAttribute.Strength, 55, false, isBody: true), default, gearUnknown: unknown);

            var reads = new Dictionary<uint, CreaturePerHit>
            {
                [1] = Melee(100), [2] = Melee(200), [3] = Melee(300), [4] = Melee(10, unknown: true), [5] = Melee(9000, unknown: true),
            };
            var pool = reads.Keys.Select(w => (w, true, false)).ToList();

            var baseline = DamageCeilingStandard.Compute(pool, w => reads[w], w => 300, w => 300, 300, 300, null, null);

            Assert.AreEqual(5, baseline.Members.Count, "kept in Members");
            Assert.AreEqual(2, baseline.GearUnknownCount);
            Assert.AreEqual(3, baseline.SampleSize);
            Assert.AreEqual(200.0, baseline.Median, 0.0, "lower median of 100, 200, 300: the two unknowns are not sampled");
            Assert.AreEqual(1.5, baseline.PoolMaxRatio, 1e-12, "the 9000 unknown does not set top");
            Assert.IsTrue(baseline.Members.Single(m => m.Wcid == 5).GearUnknown);
        }

        // ---- F4 / F5: one baseline whatever the plan, memoised whole -----------------------------------------------

        /// <summary>A synthetic stat profile over the shipped roster's real levels: a melee skill, a defense, body damage, armour and Strength.</summary>
        private static DungeonStatProfile SyntheticProfile(uint wcid)
        {
            var level = ShippedRosterFixture.LevelOf(wcid);

            if (level <= 0)
                return null;

            return new DungeonStatProfile(level, ShippedRosterFixture.HealthOf(wcid),
                new Dictionary<Skill, uint> { [Skill.HeavyWeapons] = (uint)(level * 2), [Skill.MeleeDefense] = (uint)(level * 2) },
                (uint)(level + wcid % 13), (uint)(level * 3), 0, 0.0,
                new Dictionary<PropertyAttribute, uint> { [PropertyAttribute.Strength] = (uint)(100 + level / 2), [PropertyAttribute.Endurance] = (uint)(100 + level / 3) });
        }

        private static DungeonPopulationLimits ArchetypeCeilingLimits(bool archetypeUplift = true)
            => new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: archetypeUplift);

        private static ThreadPlanMemo SyntheticMemo()
            => ThreadPlanCache.ForRun(true, null, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, SyntheticProfile, SyntheticProfile, "synthetic", SyntheticPerHit);

        private static DamageCeilingBaseline MemoBaseline(int level, DungeonPopulationLimits limits, ThreadPlanMemo memo)
            => DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(level, 1), ShippedRosterFixture.Tables, memo.LevelOf, limits, memo.PerHitOf,
                memo.ProfileOf, memo.ArchetypeProfileOf, 0, memo);

        private static DungeonSpawnPlan MemoBuild(int level, int seed, DungeonPopulationLimits limits, ThreadPlanMemo memo)
            => DungeonPopulationBuilder.Build(Spec(level, seed), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                ShippedRosterFixture.Tables, memo.LevelOf, memo.HealthOf, limits, new Random(seed), memo.ProfileOf, memo: memo,
                archetypeProfileOf: memo.ArchetypeProfileOf, perHitOf: memo.PerHitOf);

        private static string MemberShape(DamageCeilingBaseline b)
            => string.Join("|", b.Members.Select(m => $"{m.Wcid}:{m.Trash}:{m.Elite}:{m.Placement}:{m.Predicted.ByArchetype}:{m.PerHit:R}"));

        /// <summary>
        /// F4 (code review 2026-10-26): the baseline with no plan, and the one a plan carries, are identical in median, top and
        /// members at every tested level, whether or not the plan itself built an archetype context (it tagged an uplifted
        /// entry or not) and whichever was computed first. Each comparison starts from a cold cache, so a hit cannot hide a
        /// difference.
        /// </summary>
        [TestMethod]
        public void The_baseline_is_the_same_with_no_plan_and_from_any_plan_with_or_without_an_archetype_context()
        {
            TestGameTables.EnsureInitialized();

            ThreadPlanCache.Invalidate();

            try
            {
                var archetypePredicted = 0;
                var withContext = 0;
                var withoutContext = 0;

                foreach (var level in Levels)
                {
                    ThreadPlanCache.Invalidate();
                    var noPlan = MemoBaseline(level, ArchetypeCeilingLimits(), SyntheticMemo());
                    archetypePredicted += noPlan.Members.Count(m => m.Predicted.ByArchetype);

                    Assert.IsTrue(noPlan.Median > 0, $"level {level}: positive control");

                    foreach (var seed in new[] { 1, 2, 3, 5, 8, 13 })
                    {
                        // Build FIRST on a cold cache: the baseline it stores is the plan's own computation.
                        ThreadPlanCache.Invalidate();
                        var plan = MemoBuild(level, seed, ArchetypeCeilingLimits(), SyntheticMemo());
                        var stored = MemoBaseline(level, ArchetypeCeilingLimits(), SyntheticMemo());

                        if (plan.ArchetypeUplift != null) withContext++; else withoutContext++;

                        Assert.AreEqual(noPlan.Median, plan.PerHitMedian, 0.0, $"level {level} seed {seed}: median");
                        Assert.AreEqual(noPlan.PoolMaxRatio, plan.DamageCeilingTop, 0.0, $"level {level} seed {seed}: top");
                        Assert.AreEqual(noPlan.SampleSize, plan.PerHitSample, $"level {level} seed {seed}: sample");
                        Assert.AreEqual(MemberShape(noPlan), MemberShape(stored), $"level {level} seed {seed}: members");
                    }
                }

                Assert.IsTrue(archetypePredicted > 0, "guard: some member was predicted by the archetype uplift");
                Assert.IsTrue(withContext > 0, $"guard: some plan built its own archetype context ({withContext} with, {withoutContext} without)");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
            }
        }

        /// <summary>
        /// Code review 2026-10-26 F5: with no memo (the plan cache switched off) the archetype context is built through a scratch
        /// memo, so the baseline is IDENTICAL to the cache-on one (same median, top, sample and member predictions), and Build agrees.
        /// </summary>
        [TestMethod]
        public void With_the_plan_cache_off_the_baseline_equals_the_cache_on_one_and_build_agrees()
        {
            TestGameTables.EnsureInitialized();

            foreach (var level in Levels)
            {
                var direct = DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(level, 1), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf,
                    ArchetypeCeilingLimits(), SyntheticPerHit, SyntheticProfile, SyntheticProfile);

                ThreadPlanCache.Invalidate();
                var cached = MemoBaseline(level, ArchetypeCeilingLimits(), SyntheticMemo());
                ThreadPlanCache.Invalidate();

                Assert.IsTrue(direct.Members.Any(m => m.Placement == DamageCeilingPlacement.Uplifted), $"level {level}: guard, uplifted members exist");
                Assert.IsTrue(direct.Members.Any(m => m.Predicted.ByArchetype), $"level {level}: the archetype uplift is predicted without a memo");
                Assert.AreEqual(cached.Median, direct.Median, 0.0, $"level {level}: cache-off median equals cache-on");
                Assert.AreEqual(cached.PoolMaxRatio, direct.PoolMaxRatio, 0.0, $"level {level}: top");
                Assert.AreEqual(cached.SampleSize, direct.SampleSize, $"level {level}: sample");
                Assert.AreEqual(MemberShape(cached), MemberShape(direct), $"level {level}: every member prediction");

                var plan = DungeonPopulationBuilder.Build(Spec(level, 4), ShippedRosterFixture.Dungeon(30), ThreadDungeonStoreTests.LoadShipped(),
                    ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, ShippedRosterFixture.HealthOf, ArchetypeCeilingLimits(), new Random(4), SyntheticProfile,
                    archetypeProfileOf: SyntheticProfile, perHitOf: SyntheticPerHit);

                Assert.AreEqual(direct.Median, plan.PerHitMedian, 0.0, $"level {level}: a cache-off plan carries the same baseline");
                Assert.AreEqual(direct.PoolMaxRatio, plan.DamageCeilingTop, 0.0, $"level {level}");
            }
        }

        [TestMethod]
        public void The_memoised_baseline_is_served_whole_and_keyed_on_every_dial()
        {
            TestGameTables.EnsureInitialized();

            ThreadPlanCache.Invalidate();

            try
            {
                var first = MemoBaseline(300, ArchetypeCeilingLimits(), SyntheticMemo());
                var memo = SyntheticMemo();
                var again = MemoBaseline(300, ArchetypeCeilingLimits(), memo);

                Assert.AreSame(first, again, "the same key serves the stored baseline");
                Assert.AreEqual(memo.Lookups, memo.Hits, "every structural lookup of the second call hit");

                var groupLimits = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: true, group: GroupScaling.Compute(4, 0.5, 0.5, 120, 15, 300, 0.1, 1.0, 2.0));
                Assert.AreSame(first, MemoBaseline(300, groupLimits, SyntheticMemo()), "the per-run group snapshot is not part of the key");

                var floor = new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: true, bandLowFloorRatio: 0.8);
                Assert.AreNotSame(first, MemoBaseline(300, floor, SyntheticMemo()), "a changed dial misses");
                Assert.AreNotSame(first, MemoBaseline(300, ArchetypeCeilingLimits(false), SyntheticMemo()), "the archetype switch misses");
                Assert.AreNotSame(first, MemoBaseline(310, ArchetypeCeilingLimits(), SyntheticMemo()), "another level misses");
                Assert.AreNotSame(first, DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(300, 1), ShippedRosterFixture.Tables, memo.LevelOf,
                    ArchetypeCeilingLimits(), memo.PerHitOf, memo.ProfileOf, memo.ArchetypeProfileOf, 25, memo), "another gem DamageRating misses");
            }
            finally
            {
                ThreadPlanCache.Invalidate();
            }
        }

        [TestMethod]
        public void The_band_uplift_prediction_follows_the_uplift_attributes_dial()
        {
            TestGameTables.EnsureInitialized();

            foreach (var level in Levels)
            {
                DamageCeilingBaseline With(bool raise) => DungeonPopulationBuilder.DamageCeilingBaselineFor(Spec(level, 1), ShippedRosterFixture.Tables,
                    ShippedRosterFixture.LevelOf, new DungeonPopulationLimits(60, 3.0, 3.0, 8, 0.5, archetypeUplift: false, upliftAttributes: raise),
                    SyntheticPerHit, SyntheticProfile, null);

                var on = With(true).Members.Where(m => m.Placement == DamageCeilingPlacement.Uplifted).ToList();
                var off = With(false).Members.Where(m => m.Placement == DamageCeilingPlacement.Uplifted).ToDictionary(m => m.Wcid);

                Assert.IsTrue(on.Count > 0, $"level {level}: guard, uplifted members exist");
                Assert.IsTrue(on.All(m => m.Predicted.Melee.AttributeValue >= off[m.Wcid].Predicted.Melee.AttributeValue), $"level {level}: the raise never lowers");
                Assert.IsTrue(on.Any(m => m.Predicted.Melee.AttributeValue > off[m.Wcid].Predicted.Melee.AttributeValue), $"level {level}: the dial reaches the prediction");
            }
        }
    }
}
