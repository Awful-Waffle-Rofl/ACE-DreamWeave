using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.Entity;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Covers the daily-survey reward math. Pure - no PropertyManager, no DatManager, no Player: every
    /// tunable arrives in a SurveyRewardLimits the test builds itself.
    ///
    /// Most cases use a synthetic maxLevel, so the curve's shape is pinned independently of where the fork's
    /// level ceiling happens to sit. That is deliberate: owner ruling R6 chose a formula over MaxLevel
    /// precisely so a raised ceiling rescales itself, and a test suite written against the current ceiling
    /// would hide a regression in exactly that property. The exceptions are the calibration anchors, which
    /// read DungeonGemSpec.MaxLevel on purpose so that a moved ceiling fails loudly here and gets a design
    /// look; the 2026-09-09 raise to 375 is one such look, written out at its own test.
    /// </summary>
    [TestClass]
    public class SurveyRewardMathTests
    {
        /// <summary>
        /// The level 274 to 275 XP delta the shipped emote rig's three literal caps were fractions of
        /// (weenie 1003613's header, dat-verified 2026-09-04). Used ONLY by the regression anchor below.
        /// </summary>
        private const long ShippedCapBase = 3390451400L;

        /// <summary>
        /// The level 374 to 375 XP delta - the cap base SurveyArchivistStation.CapBaseFor now returns, since
        /// the reference level defaults to DungeonGemSpec.MaxLevel and that is 375 (owner ruling 2026-09-09).
        /// Levels above 275 are off EnlightenmentXpCurve's synthesized tail rather than the retail chart, so
        /// this figure is DERIVED rather than trusted: see
        /// <see cref="The_cap_base_at_each_reference_level_comes_off_the_extended_xp_chart"/>, which
        /// reproduces both cap bases from the retail chart's own last three totals.
        /// </summary>
        private const long CeilingCapBase = 14220033025L;

        private static SurveyRewardLimits Limits(
            int maxLevel = 200,
            long capBase = 1000000,
            double exponent = SurveyRewardLimits.DefaultExponent,
            double floor = SurveyRewardLimits.DefaultFloor,
            bool scalingEnabled = true)
            => new SurveyRewardLimits(maxLevel, capBase, exponent, floor, scalingEnabled);

        private static SurveyTier Tier(int surveys)
        {
            Assert.IsTrue(SurveyRewardMath.TryGetTier(surveys, out var tier), $"tier {surveys} is missing from SurveyRewardMath.Tiers");
            return tier;
        }

        // ---------------- the tier table ----------------

        [TestMethod]
        public void The_tier_table_is_unchanged_from_the_emote_rig()
        {
            // Ruling R4 scales these by the ratio; it does not renumber them. This is also the assertion that
            // there is exactly ONE declaration of the triple - the station, the planner and every other test
            // read it from here.
            CollectionAssert.AreEqual(new[] { 1, 5, 10 }, SurveyRewardMath.Tiers.Select(t => t.Surveys).ToArray());
            CollectionAssert.AreEqual(new[] { 0.15, 0.40, 0.75 }, SurveyRewardMath.Tiers.Select(t => t.XpPercent).ToArray());
            CollectionAssert.AreEqual(new[] { 50000L, 250000L, 500000L }, SurveyRewardMath.Tiers.Select(t => t.Luminance).ToArray());

            CollectionAssert.AreEqual(new[] { 10, 5, 1 }, SurveyRewardMath.Descending.Select(t => t.Surveys).ToArray());
        }

        [TestMethod]
        public void Each_tier_names_its_own_paid_latch_quest()
        {
            Assert.AreEqual("DynDungeonSurvey1", Tier(1).Quest);
            Assert.AreEqual("DynDungeonSurvey5", Tier(5).Quest);
            Assert.AreEqual("DynDungeonSurvey10", Tier(10).Quest);
        }

        // ---------------- the regression anchor ----------------

        [TestMethod]
        public void At_ratio_one_the_shipped_award_numbers_are_reproduced_exactly()
        {
            // THE regression anchor. An empty ring pays ratio 1.0 (ruling R8), so this is also the exact
            // award a pre-feature character gets on their first turn-in after this ships: byte for byte what
            // emote sets 10 / 12 / 14 / 16 / 18 paid, with no silent cut.
            var limits = Limits(capBase: ShippedCapBase);
            var empty = SurveyLevelRing.Empty;

            var one = SurveyRewardMath.Compute(Tier(1), empty, limits);
            var five = SurveyRewardMath.Compute(Tier(5), empty, limits);
            var ten = SurveyRewardMath.Compute(Tier(10), empty, limits);

            Assert.AreEqual(1.0, one.Ratio, 0.0);
            Assert.AreEqual(508567710L, one.XpCap);
            Assert.AreEqual(50000L, one.Luminance);

            Assert.AreEqual(1356180560L, five.XpCap);
            Assert.AreEqual(250000L, five.Luminance);

            Assert.AreEqual(2542838550L, ten.XpCap);
            Assert.AreEqual(500000L, ten.Luminance);
        }

        [TestMethod]
        public void Switching_the_scaling_off_pins_every_award_to_the_shipped_numbers()
        {
            // dynamic_dungeons_survey_level_scaling = false is the live rollback: a full ring of the lowest
            // legal gem level still pays exactly what the emote rig paid.
            var limits = Limits(capBase: ShippedCapBase, scalingEnabled: false);
            var lowRing = SurveyLevelRing.FromLevels(Enumerable.Repeat(1, SurveyLevelRing.Depth));

            Assert.AreEqual(508567710L, SurveyRewardMath.Compute(Tier(1), lowRing, limits).XpCap);
            Assert.AreEqual(1356180560L, SurveyRewardMath.Compute(Tier(5), lowRing, limits).XpCap);
            Assert.AreEqual(2542838550L, SurveyRewardMath.Compute(Tier(10), lowRing, limits).XpCap);
            Assert.AreEqual(500000L, SurveyRewardMath.Compute(Tier(10), lowRing, limits).Luminance);
        }

        // ---------------- the curve ----------------

        [TestMethod]
        public void The_ratio_is_the_level_share_raised_to_the_exponent()
        {
            var limits = Limits(maxLevel: 200);
            var ring = SurveyLevelRing.FromLevels(new[] { 100 });

            // (100 / 200)^2 = 0.25, exactly.
            Assert.AreEqual(0.25, SurveyRewardMath.Compute(Tier(1), ring, limits).Ratio, 1e-12);

            // k = 1 is linear.
            Assert.AreEqual(0.5, SurveyRewardMath.Compute(Tier(1), ring, Limits(maxLevel: 200, exponent: 1.0)).Ratio, 1e-12);

            // k = 3 bites harder.
            Assert.AreEqual(0.125, SurveyRewardMath.Compute(Tier(1), ring, Limits(maxLevel: 200, exponent: 3.0)).Ratio, 1e-12);
        }

        /// <summary>
        /// The calibration anchor for the shipped ceiling: a run at the survey floor level
        /// (dynamic_dungeons_survey_min_level defaults to 185) at the default k of 2. Deliberately expressed
        /// against DungeonGemSpec.MaxLevel rather than a literal, so this test FAILS LOUDLY if the level
        /// ceiling moves - at which point the numbers below want a fresh design look, not a mechanical update.
        ///
        /// THE 2026-09-09 CEILING RAISE (275 -> 375) IS THAT DESIGN LOOK, taken rather than pasted in. The
        /// arithmetic, re-derived from SurveyRewardMath's own equations rather than from a test run:
        ///
        ///   ratio  = (185 / 375)^2 = (37/75)^2 = 1369/5625 = 0.243377..., down from (185/275)^2 = 0.452561...
        ///   xpCap  = round(0.75 * 14,220,033,025 * 1369/5625) = 2,595,630,028
        ///   lum    = round(500,000 * 1369/5625) = 121,689
        ///
        /// The RATIO nearly halves, which is the rescale ruling R6 asks for: 185 is now a smaller fraction of
        /// the reference level, so a floor-level run is a smaller share of endgame. The XP the player actually
        /// collects still goes UP by about 2.26x, from 1,150,792,058 to 2,595,630,028, because the cap base
        /// rose 4.19x with the reference level (3,390,451,400 -> 14,220,033,025 - the character XP curve is
        /// steeper than quadratic up there). Both halves are accepted behaviour (owner ruling 2026-09-09);
        /// they are spelled out here because "the ratio fell" and "the payout fell" are NOT the same claim and
        /// only the first one is true.
        ///
        /// Luminance is the one axis that falls outright (226,281 -> 121,689), since it scales by the ratio
        /// alone with no cap base behind it.
        /// </summary>
        [TestMethod]
        public void A_run_at_the_survey_floor_level_pays_about_24_percent_at_the_default_exponent()
        {
            var limits = Limits(maxLevel: DungeonGemSpec.MaxLevel, capBase: CeilingCapBase);
            var ring = SurveyLevelRing.FromLevels(new[] { 185 });

            var award = SurveyRewardMath.Compute(Tier(10), ring, limits);

            Assert.AreEqual(1369.0 / 5625.0, award.Ratio, 1e-9, "(185 / 375)^2");
            Assert.AreEqual(0.2433777777777778, award.Ratio, 1e-9);

            Assert.AreEqual(2595630028L, award.XpCap);
            Assert.AreEqual((long)Math.Round(0.75 * CeilingCapBase * 0.2433777777777778, MidpointRounding.AwayFromZero), award.XpCap);

            Assert.AreEqual(121689L, award.Luminance);
            Assert.AreEqual((long)Math.Round(500000 * 0.2433777777777778, MidpointRounding.AwayFromZero), award.Luminance);
        }

        /// <summary>
        /// The reference level is a TUNABLE (dynamic_dungeons_survey_reference_level) whose default is
        /// DungeonGemSpec.MaxLevel, so pinning it back at the OLD ceiling of 275 must restore the pre-raise
        /// curve exactly - that is the whole point of it being a dial rather than a direct MaxLevel read.
        ///
        /// A full ring at 275 is the discriminating input: it reads ratio 1.0 against a 275 reference (the
        /// shipped endgame award, byte for byte what emote sets 10/12/14/16/18 paid) and (275/375)^2 =
        /// 121/225 = 0.537777... against the 375 one. An empty ring could not tell the two apart, because
        /// ruling R8 pays an empty ring 1.0 at any reference level.
        /// </summary>
        [TestMethod]
        public void Pinning_the_reference_level_at_the_old_ceiling_restores_the_shipped_awards()
        {
            var ring = SurveyLevelRing.FromLevels(Enumerable.Repeat(275, SurveyLevelRing.Depth));

            var atOldCeiling = Limits(maxLevel: 275, capBase: ShippedCapBase);

            Assert.AreEqual(1.0, SurveyRewardMath.Compute(Tier(1), ring, atOldCeiling).Ratio, 0.0);
            Assert.AreEqual(508567710L, SurveyRewardMath.Compute(Tier(1), ring, atOldCeiling).XpCap);
            Assert.AreEqual(1356180560L, SurveyRewardMath.Compute(Tier(5), ring, atOldCeiling).XpCap);
            Assert.AreEqual(2542838550L, SurveyRewardMath.Compute(Tier(10), ring, atOldCeiling).XpCap);
            Assert.AreEqual(500000L, SurveyRewardMath.Compute(Tier(10), ring, atOldCeiling).Luminance);

            // The same ring at the shipped default reference level is worth (275/375)^2 of that.
            var atNewCeiling = Limits(maxLevel: DungeonGemSpec.MaxLevel, capBase: CeilingCapBase);

            Assert.AreEqual(121.0 / 225.0, SurveyRewardMath.Compute(Tier(10), ring, atNewCeiling).Ratio, 1e-9, "(275 / 375)^2");
        }

        /// <summary>
        /// Both cap-base constants in this file, derived rather than trusted. SurveyArchivistStation.CapBaseFor
        /// reads totals[reference] - totals[reference - 1] off EnlightenmentXpCurve.ExtendedTotals, which is
        /// the retail chart (indices 0..275) followed by a synthesized tail - so the 375 figure is not in any
        /// dat file and cannot be looked up, only computed.
        ///
        /// The seed below is the retail chart's own top three cumulative totals, read out of
        /// client_portal.dat's XpTable.CharacterLevelXPList on 2026-09-09. Three entries is enough because
        /// EnlightenmentXpCurve.Extend derives its growth ratio from the LAST TWO deltas and nothing else;
        /// measured against the full 276-entry chart on the same date, the seeded tail is identical at every
        /// index (first mismatch: none), so this reproduces the production chart above level 275 exactly.
        /// </summary>
        [TestMethod]
        public void The_cap_base_at_each_reference_level_comes_off_the_extended_xp_chart()
        {
            var retailTail = new List<ulong> { 184493669177, 187835858847, 191226310247 }; // levels 273, 274, 275

            var extended = EnlightenmentXpCurve.Extend(retailTail, (ulong)(long.MaxValue / 2));

            // Index 2 of the seed is level 275, so level L sits at index L - 273.
            long CapBaseAt(int level) => (long)(extended[level - 273] - extended[level - 274]);

            Assert.AreEqual(ShippedCapBase, CapBaseAt(275), "the old ceiling's cap base, straight off the retail chart");
            Assert.AreEqual(CeilingCapBase, CapBaseAt(375), "the new ceiling's cap base, off the synthesized tail");

            // The reason the floor-level payout rises even though its ratio falls: the cap base grows faster
            // than quadratically over the same span.
            Assert.IsTrue(CapBaseAt(375) > 4 * CapBaseAt(275),
                $"cap base at 375 ({CapBaseAt(375)}) should be more than 4x the one at 275 ({CapBaseAt(275)})");
        }

        [TestMethod]
        public void Raising_the_reference_level_rescales_the_same_ring()
        {
            // Ruling R6's whole reason for a formula over MaxLevel instead of the hand-authored xpLadder: a
            // raised ceiling re-prices the same run without anyone editing a rung. If a literal ever crept
            // into the core, these two would come back equal.
            var ring = SurveyLevelRing.FromLevels(new[] { 150 });

            var atTwoHundred = SurveyRewardMath.Compute(Tier(1), ring, Limits(maxLevel: 200)).Ratio;
            var atThreeHundred = SurveyRewardMath.Compute(Tier(1), ring, Limits(maxLevel: 300)).Ratio;

            Assert.AreEqual(0.5625, atTwoHundred, 1e-12);
            Assert.AreEqual(0.25, atThreeHundred, 1e-12);
            Assert.IsTrue(atThreeHundred < atTwoHundred, "the same run must be worth less once the ceiling rises");
        }

        [TestMethod]
        public void The_ratio_never_falls_below_the_floor()
        {
            var limits = Limits(maxLevel: 200, floor: 0.10);
            var ring = SurveyLevelRing.FromLevels(new[] { 1 });

            Assert.AreEqual(0.10, SurveyRewardMath.Compute(Tier(1), ring, limits).Ratio, 1e-12);
            Assert.AreEqual(0.20, SurveyRewardMath.Compute(Tier(1), ring, Limits(maxLevel: 200, floor: 0.20)).Ratio, 1e-12);
        }

        [TestMethod]
        public void The_ratio_never_rises_above_one()
        {
            // A ring above the reference level cannot pay more than the endgame rate. It needs no clamp on
            // the XP BASE, though: that stays the player's own next-level XP.
            var limits = Limits(maxLevel: 200);
            var ring = SurveyLevelRing.FromLevels(new[] { 200, 200 });

            Assert.AreEqual(1.0, SurveyRewardMath.Compute(Tier(5), ring, limits).Ratio, 1e-12);
        }

        // ---------------- the rolling average ----------------

        [TestMethod]
        public void Each_tier_averages_only_the_newest_N_entries()
        {
            // Ruling R2: rolling last-N spanning windows. Ten fresh top-level runs followed by nothing else
            // means tier 1 sees only the newest entry while tier 10 sees the whole ring.
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 200, 200, 200, 200, 200, 200, 200, 200, 200 });

            Assert.AreEqual(100, SurveyRewardMath.Compute(Tier(1), ring, Limits()).AverageLevel);
            Assert.AreEqual(180, SurveyRewardMath.Compute(Tier(5), ring, Limits()).AverageLevel);
            Assert.AreEqual(190, SurveyRewardMath.Compute(Tier(10), ring, Limits()).AverageLevel);
        }

        [TestMethod]
        public void A_short_ring_averages_only_what_it_holds()
        {
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 200, 150 });

            var award = SurveyRewardMath.Compute(Tier(10), ring, Limits());

            Assert.AreEqual(3, award.Sampled, "tier 10 averaged the three entries that exist, not ten");
            Assert.AreEqual(150, award.AverageLevel);
        }

        [TestMethod]
        public void The_average_is_rounded_away_from_zero()
        {
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 101 });

            Assert.AreEqual(101, SurveyRewardMath.Compute(Tier(5), ring, Limits()).AverageLevel);
        }

        [TestMethod]
        public void An_empty_ring_pays_the_unscaled_award()
        {
            // Ruling R8, the grandfather clause. Stated as its own test because it is the ONE case where an
            // absent measurement must read as "full credit" rather than as "zero".
            var award = SurveyRewardMath.Compute(Tier(10), SurveyLevelRing.Empty, Limits());

            Assert.AreEqual(0, award.Sampled);
            Assert.AreEqual(0, award.AverageLevel);
            Assert.AreEqual(1.0, award.Ratio, 0.0);

            Assert.AreEqual(1.0, SurveyRewardMath.Compute(Tier(10), null, Limits()).Ratio, 0.0);
        }

        // ---------------- tunable sanitizing ----------------

        [TestMethod]
        public void A_garbled_exponent_reads_as_the_default()
        {
            Assert.AreEqual(SurveyRewardLimits.DefaultExponent, Limits(exponent: double.NaN).Exponent, 0.0);
            Assert.AreEqual(SurveyRewardLimits.DefaultExponent, Limits(exponent: double.PositiveInfinity).Exponent, 0.0);
            Assert.AreEqual(SurveyRewardLimits.DefaultExponent, Limits(exponent: -1.0).Exponent, 0.0);
            Assert.AreEqual(SurveyRewardLimits.MaxExponent, Limits(exponent: 1e9).Exponent, 0.0);

            // k = 0 is legal and means "no curve": every ratio reads 1.0.
            Assert.AreEqual(0.0, Limits(exponent: 0.0).Exponent, 0.0);
            Assert.AreEqual(1.0, SurveyRewardMath.Compute(Tier(1), SurveyLevelRing.FromLevels(new[] { 1 }), Limits(exponent: 0.0)).Ratio, 1e-12);
        }

        [TestMethod]
        public void A_garbled_floor_reads_as_the_default_or_clamps()
        {
            Assert.AreEqual(SurveyRewardLimits.DefaultFloor, Limits(floor: double.NaN).Floor, 0.0);
            Assert.AreEqual(0.0, Limits(floor: -3.0).Floor, 0.0);
            Assert.AreEqual(1.0, Limits(floor: 4.0).Floor, 0.0);
        }

        [TestMethod]
        public void A_garbled_reference_level_or_cap_base_cannot_go_negative()
        {
            Assert.AreEqual(1, Limits(maxLevel: 0).MaxLevel);
            Assert.AreEqual(1, Limits(maxLevel: -40).MaxLevel);
            Assert.AreEqual(0L, Limits(capBase: -1).CapBase);
        }

        [TestMethod]
        public void A_zero_cap_base_pays_no_xp_and_no_negative_luminance()
        {
            var award = SurveyRewardMath.Compute(Tier(10), SurveyLevelRing.Empty, Limits(capBase: 0));

            // The station reads a zero cap as "pay no XP", because GrantLevelProportionalXp treats max <= 0
            // as NO CAP at all.
            Assert.AreEqual(0L, award.XpCap);
            Assert.AreEqual(500000L, award.Luminance);
        }
    }
}
