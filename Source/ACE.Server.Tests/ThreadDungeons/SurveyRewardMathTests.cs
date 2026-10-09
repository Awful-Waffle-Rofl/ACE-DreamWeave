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
    /// tunable arrives in a SurveyRewardLimits the test builds itself, and the character XP chart is a
    /// synthetic one handed in through that same object.
    ///
    /// The owner ruling under test: the XP a tier pays is percent * delta(min(playerLevel, avgGemLevel)),
    /// where delta(L) is the XP to go from L to L + 1; the ratio scales LUMINANCE only. An empty ring and
    /// the scaling-off rollback both measure at the player's own level.
    ///
    /// Most cases use a synthetic maxLevel, so the curve's shape is pinned independently of where the fork's
    /// level ceiling happens to sit (owner ruling R6). The exceptions are the calibration anchors, which read
    /// DungeonGemSpec.MaxGemLevel on purpose so that a moved ceiling fails loudly here and gets a design look.
    /// </summary>
    [TestClass]
    public class SurveyRewardMathTests
    {
        /// <summary>
        /// A synthetic character XP chart, index = level, totals[L] = L^4. Its per-level delta,
        /// (L+1)^4 - L^4, grows FASTER THAN QUADRATICALLY like the real chart's high-level tail, which is what
        /// lets a case prove the new formula pays strictly less than the old ratio-scaled cap
        /// percent * delta(375) * (avg/375)^2 for a player above the average.
        /// </summary>
        private static readonly IReadOnlyList<ulong> Chart =
            Enumerable.Range(0, 401).Select(l => (ulong)l * (ulong)l * (ulong)l * (ulong)l).ToList();

        private const int PlayerLevel = 300;

        private static long Delta(int level) => (long)(Chart[level + 1] - Chart[level]);

        private static long Expected(double percent, int level)
            => (long)Math.Round(percent * Delta(level), MidpointRounding.AwayFromZero);

        private static SurveyRewardLimits Limits(
            int maxLevel = 375,
            IReadOnlyList<ulong> totals = null,
            double exponent = SurveyRewardLimits.DefaultExponent,
            double floor = SurveyRewardLimits.DefaultFloor,
            bool scalingEnabled = true)
            => new SurveyRewardLimits(maxLevel, totals ?? Chart, exponent, floor, scalingEnabled);

        private static SurveyAward Compute(SurveyTier tier, SurveyLevelRing ring, SurveyRewardLimits limits, int playerLevel = PlayerLevel)
            => SurveyRewardMath.Compute(tier, ring, limits, playerLevel);

        private static SurveyTier Tier(int surveys)
        {
            Assert.IsTrue(SurveyRewardMath.TryGetTier(surveys, out var tier), $"tier {surveys} is missing from SurveyRewardMath.Tiers");
            return tier;
        }

        /// <summary>What Player.GrantLevelProportionalXp(percent, 0, cap) pays, before EarnXP's own modifiers.</summary>
        private static long Paid(SurveyAward award, int playerLevel)
            => EnlightenmentXpCurve.LevelProportionalXp(Chart, playerLevel, award.XpPercent, 0, award.XpCap);

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

        // ---------------- the XP ruling: percent * delta(min(player level, ring average)) ----------------

        [TestMethod]
        public void A_player_below_the_ring_average_is_paid_at_their_own_level()
        {
            // (a) player 232, ring average 375 -> percent * delta(232). Under the old formula the cap was
            // percent * delta(375) * 1.0, a much larger number, so the cap assertion fails there.
            var ring = SurveyLevelRing.FromLevels(new[] { 375 });

            foreach (var tier in SurveyRewardMath.Tiers)
            {
                var award = Compute(tier, ring, Limits(), playerLevel: 232);

                Assert.AreEqual(232, award.XpLevel, $"{tier}: min(232, 375)");
                Assert.AreEqual(Expected(tier.XpPercent, 232), award.XpCap, $"{tier}");
                Assert.IsTrue(award.XpCap < Expected(tier.XpPercent, 375), $"{tier}: the cap must not be the old percent * delta(375)");
                Assert.AreEqual(Expected(tier.XpPercent, 232), Paid(award, 232), $"{tier}: what the grant actually pays");
            }
        }

        [TestMethod]
        public void A_player_above_the_ring_average_is_paid_at_the_average_and_strictly_less_than_the_old_cap()
        {
            // (b) player 375, ring average 285 -> percent * delta(285). The old cap was
            // percent * delta(375) * (285/375)^2; on a chart whose deltas outgrow the square (the synthetic
            // quartic chart, like the real tail) the new figure is strictly LESS, i.e. the old formula
            // double-penalised nothing here and overpaid.
            var ring = SurveyLevelRing.FromLevels(new[] { 285 });

            foreach (var tier in SurveyRewardMath.Tiers)
            {
                var award = Compute(tier, ring, Limits(), playerLevel: 375);

                var oldCap = (long)Math.Round(tier.XpPercent * Delta(375) * Math.Pow(285 / 375.0, 2), MidpointRounding.AwayFromZero);

                Assert.AreEqual(285, award.XpLevel, $"{tier}: min(375, 285)");
                Assert.AreEqual(Expected(tier.XpPercent, 285), award.XpCap, $"{tier}");
                Assert.IsTrue(award.XpCap < oldCap, $"{tier}: new cap {award.XpCap} must be strictly below the old ratio-scaled cap {oldCap}");

                // The grant pays min(percent * delta(player), cap): here the cap is the smaller side.
                Assert.AreEqual(Expected(tier.XpPercent, 285), Paid(award, 375), $"{tier}: what the grant actually pays");
            }
        }

        [TestMethod]
        public void An_empty_ring_pays_at_the_player_level()
        {
            // (c) ruling R8, the grandfather clause: no ring means no measurement, which reads as the
            // player's own level rather than as level zero. Under the old formula the cap was
            // percent * delta(maxLevel), which differs from delta(player) for any player below the ceiling.
            foreach (var tier in SurveyRewardMath.Tiers)
            {
                var award = Compute(tier, SurveyLevelRing.Empty, Limits(), playerLevel: 250);

                Assert.AreEqual(0, award.Sampled);
                Assert.AreEqual(0, award.AverageLevel);
                Assert.AreEqual(1.0, award.Ratio, 0.0);
                Assert.AreEqual(250, award.XpLevel);
                Assert.AreEqual(Expected(tier.XpPercent, 250), award.XpCap, $"{tier}");
                Assert.AreEqual(Expected(tier.XpPercent, 250), Paid(award, 250), $"{tier}");
                Assert.AreEqual(tier.Luminance, award.Luminance, $"{tier}: an empty ring pays full luminance");
            }

            // A null ring is the same as an empty one.
            Assert.AreEqual(Expected(0.75, 250), Compute(Tier(10), null, Limits(), playerLevel: 250).XpCap);
        }

        [TestMethod]
        public void Switching_the_scaling_off_pays_at_the_player_level()
        {
            // (d) dynamic_dungeons_survey_level_scaling = false is the live rollback. A full ring of the
            // lowest gem level would otherwise pull the level down to 1; with scaling off the player's own
            // level is used and the luminance ratio pins to 1.0.
            var limits = Limits(scalingEnabled: false);
            var lowRing = SurveyLevelRing.FromLevels(Enumerable.Repeat(1, SurveyLevelRing.Depth));

            foreach (var tier in SurveyRewardMath.Tiers)
            {
                var award = Compute(tier, lowRing, limits, playerLevel: 300);

                Assert.AreEqual(300, award.XpLevel, $"{tier}");
                Assert.AreEqual(Expected(tier.XpPercent, 300), award.XpCap, $"{tier}");
                Assert.AreEqual(1.0, award.Ratio, 0.0);
                Assert.AreEqual(tier.Luminance, award.Luminance, $"{tier}");
            }
        }

        [TestMethod]
        public void The_ratio_never_touches_the_xp()
        {
            // Two rings with the same average level but different exponents and floors: the ratio moves, the
            // XP does not. This is the double-penalty guard.
            var ring = SurveyLevelRing.FromLevels(new[] { 100 });

            var gentle = Compute(Tier(1), ring, Limits(exponent: 0.0), playerLevel: 300);
            var harsh = Compute(Tier(1), ring, Limits(exponent: 6.0, floor: 0.0), playerLevel: 300);

            Assert.AreNotEqual(gentle.Ratio, harsh.Ratio);
            Assert.AreNotEqual(gentle.Luminance, harsh.Luminance);
            Assert.AreEqual(gentle.XpCap, harsh.XpCap);
            Assert.AreEqual(Expected(0.15, 100), gentle.XpCap);
        }

        [TestMethod]
        public void A_level_outside_the_chart_is_clamped_into_its_index_range()
        {
            // The same clamp GrantLevelProportionalXp applies: below 1 reads level 1, and at or past the top
            // reads the last level the chart can step from. Never an exception, never a zero cap.
            var ring = SurveyLevelRing.FromLevels(new[] { 0 });

            Assert.AreEqual(Expected(0.15, 1), Compute(Tier(1), ring, Limits(), playerLevel: 300).XpCap, "ring entry 0 is clamped to 1 by FromLevels");
            Assert.AreEqual(Delta(1), SurveyRewardMath.LevelXp(Chart, 0), "a level of 0 reads level 1");
            Assert.AreEqual(Expected(0.15, 1), Compute(Tier(1), SurveyLevelRing.Empty, Limits(), playerLevel: -5).XpCap, "negative player level reads level 1");
            Assert.AreEqual(Expected(0.15, 399), Compute(Tier(1), SurveyLevelRing.Empty, Limits(), playerLevel: 9999).XpCap, "past the chart reads its last step");
        }

        [TestMethod]
        public void An_unusable_chart_pays_no_xp_and_full_luminance()
        {
            // The station reads a zero cap as "pay no XP", because GrantLevelProportionalXp treats max <= 0
            // as NO CAP at all. A null chart or one too short to index must land on exactly that.
            foreach (var chart in new IReadOnlyList<ulong>[] { null, new List<ulong>(), new List<ulong> { 0, 10 } })
            {
                var limits = new SurveyRewardLimits(375, chart, SurveyRewardLimits.DefaultExponent, SurveyRewardLimits.DefaultFloor, true);
                var award = Compute(Tier(10), SurveyLevelRing.Empty, limits);

                Assert.AreEqual(0L, award.XpCap);
                Assert.AreEqual(500000L, award.Luminance);
            }
        }

        // ---------------- the luminance curve (unchanged) ----------------

        [TestMethod]
        public void Luminance_is_the_tier_base_times_the_ratio_whatever_the_player_level()
        {
            // (e) luminance = round(baseLum * ratio), exactly as before the XP ruling, and independent of the
            // player's level.
            var ring = SurveyLevelRing.FromLevels(new[] { 285 });
            var ratio = Math.Pow(285 / 375.0, 2);

            foreach (var tier in SurveyRewardMath.Tiers)
            {
                var expected = (long)Math.Round(tier.Luminance * ratio, MidpointRounding.AwayFromZero);

                Assert.AreEqual(expected, Compute(tier, ring, Limits(), playerLevel: 100).Luminance, $"{tier} at player level 100");
                Assert.AreEqual(expected, Compute(tier, ring, Limits(), playerLevel: 375).Luminance, $"{tier} at player level 375");
            }
        }

        [TestMethod]
        public void The_ratio_is_the_level_share_raised_to_the_exponent()
        {
            var limits = Limits(maxLevel: 200);
            var ring = SurveyLevelRing.FromLevels(new[] { 100 });

            // (100 / 200)^2 = 0.25, exactly.
            Assert.AreEqual(0.25, Compute(Tier(1), ring, limits).Ratio, 1e-12);

            // k = 1 is linear.
            Assert.AreEqual(0.5, Compute(Tier(1), ring, Limits(maxLevel: 200, exponent: 1.0)).Ratio, 1e-12);

            // k = 3 bites harder.
            Assert.AreEqual(0.125, Compute(Tier(1), ring, Limits(maxLevel: 200, exponent: 3.0)).Ratio, 1e-12);
        }

        /// <summary>
        /// The calibration anchor for the shipped ceiling: a run at the survey floor level
        /// (the lowest Raw Fragment rung, 185) at the default k of 2. Deliberately expressed
        /// against DungeonGemSpec.MaxGemLevel rather than a literal, so this test FAILS LOUDLY if the level
        /// ceiling moves - at which point the numbers below want a fresh design look.
        ///
        ///   ratio = (185 / 375)^2 = (37/75)^2 = 1369/5625 = 0.243377...
        ///   lum   = round(500,000 * 1369/5625) = 121,689
        ///
        /// Luminance is the one axis the ratio scales; the XP is a share of a level-up at level 185 for a
        /// player at or above it.
        /// </summary>
        [TestMethod]
        public void A_run_at_the_survey_floor_level_pays_about_24_percent_luminance_at_the_default_exponent()
        {
            var limits = Limits(maxLevel: DungeonGemSpec.MaxGemLevel);
            var ring = SurveyLevelRing.FromLevels(new[] { 185 });

            var award = Compute(Tier(10), ring, limits, playerLevel: 375);

            Assert.AreEqual(1369.0 / 5625.0, award.Ratio, 1e-9, "(185 / 375)^2");
            Assert.AreEqual(121689L, award.Luminance);
            Assert.AreEqual((long)Math.Round(500000 * 0.2433777777777778, MidpointRounding.AwayFromZero), award.Luminance);

            Assert.AreEqual(Expected(0.75, 185), award.XpCap, "XP is measured at the lower of the two levels");
        }

        /// <summary>
        /// The reference level is a TUNABLE (dynamic_dungeons_survey_reference_level) whose default is
        /// DungeonGemSpec.MaxGemLevel, so pinning it back at the OLD ceiling of 275 must restore the pre-raise
        /// luminance curve exactly - that is the whole point of it being a dial rather than a direct MaxLevel
        /// read. A full ring at 275 is the discriminating input: it reads ratio 1.0 against a 275 reference
        /// and (275/375)^2 = 121/225 against the 375 one.
        /// </summary>
        [TestMethod]
        public void Pinning_the_reference_level_at_the_old_ceiling_restores_the_shipped_luminance()
        {
            var ring = SurveyLevelRing.FromLevels(Enumerable.Repeat(275, SurveyLevelRing.Depth));

            var atOldCeiling = Limits(maxLevel: 275);

            Assert.AreEqual(1.0, Compute(Tier(1), ring, atOldCeiling).Ratio, 0.0);
            Assert.AreEqual(50000L, Compute(Tier(1), ring, atOldCeiling).Luminance);
            Assert.AreEqual(250000L, Compute(Tier(5), ring, atOldCeiling).Luminance);
            Assert.AreEqual(500000L, Compute(Tier(10), ring, atOldCeiling).Luminance);

            // The same ring at the shipped default reference level is worth (275/375)^2 of that.
            var atNewCeiling = Limits(maxLevel: DungeonGemSpec.MaxGemLevel);

            Assert.AreEqual(121.0 / 225.0, Compute(Tier(10), ring, atNewCeiling).Ratio, 1e-9, "(275 / 375)^2");
        }

        [TestMethod]
        public void Raising_the_reference_level_rescales_the_same_ring()
        {
            // Ruling R6's whole reason for a formula over MaxLevel instead of the hand-authored xpLadder: a
            // raised ceiling re-prices the same run without anyone editing a rung. If a literal ever crept
            // into the core, these two would come back equal.
            var ring = SurveyLevelRing.FromLevels(new[] { 150 });

            var atTwoHundred = Compute(Tier(1), ring, Limits(maxLevel: 200)).Ratio;
            var atThreeHundred = Compute(Tier(1), ring, Limits(maxLevel: 300)).Ratio;

            Assert.AreEqual(0.5625, atTwoHundred, 1e-12);
            Assert.AreEqual(0.25, atThreeHundred, 1e-12);
            Assert.IsTrue(atThreeHundred < atTwoHundred, "the same run must be worth less once the ceiling rises");
        }

        [TestMethod]
        public void The_ratio_never_falls_below_the_floor()
        {
            var limits = Limits(maxLevel: 200, floor: 0.10);
            var ring = SurveyLevelRing.FromLevels(new[] { 1 });

            Assert.AreEqual(0.10, Compute(Tier(1), ring, limits).Ratio, 1e-12);
            Assert.AreEqual(0.20, Compute(Tier(1), ring, Limits(maxLevel: 200, floor: 0.20)).Ratio, 1e-12);
        }

        [TestMethod]
        public void The_ratio_never_rises_above_one()
        {
            // A ring above the reference level cannot pay more than the full luminance.
            var limits = Limits(maxLevel: 200);
            var ring = SurveyLevelRing.FromLevels(new[] { 200, 200 });

            Assert.AreEqual(1.0, Compute(Tier(5), ring, limits).Ratio, 1e-12);
        }

        // ---------------- the rolling average ----------------

        [TestMethod]
        public void Each_tier_averages_only_the_newest_N_entries()
        {
            // Ruling R2: rolling last-N spanning windows. Ten fresh top-level runs followed by nothing else
            // means tier 1 sees only the newest entry while tier 10 sees the whole ring.
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 200, 200, 200, 200, 200, 200, 200, 200, 200 });

            Assert.AreEqual(100, Compute(Tier(1), ring, Limits()).AverageLevel);
            Assert.AreEqual(180, Compute(Tier(5), ring, Limits()).AverageLevel);
            Assert.AreEqual(190, Compute(Tier(10), ring, Limits()).AverageLevel);
        }

        [TestMethod]
        public void A_short_ring_averages_only_what_it_holds()
        {
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 200, 150 });

            var award = Compute(Tier(10), ring, Limits());

            Assert.AreEqual(3, award.Sampled, "tier 10 averaged the three entries that exist, not ten");
            Assert.AreEqual(150, award.AverageLevel);
        }

        [TestMethod]
        public void The_average_is_rounded_away_from_zero()
        {
            var ring = SurveyLevelRing.FromLevels(new[] { 100, 101 });

            Assert.AreEqual(101, Compute(Tier(5), ring, Limits()).AverageLevel);
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
            Assert.AreEqual(1.0, Compute(Tier(1), SurveyLevelRing.FromLevels(new[] { 1 }), Limits(exponent: 0.0)).Ratio, 1e-12);
        }

        [TestMethod]
        public void A_garbled_floor_reads_as_the_default_or_clamps()
        {
            Assert.AreEqual(SurveyRewardLimits.DefaultFloor, Limits(floor: double.NaN).Floor, 0.0);
            Assert.AreEqual(0.0, Limits(floor: -3.0).Floor, 0.0);
            Assert.AreEqual(1.0, Limits(floor: 4.0).Floor, 0.0);
        }

        [TestMethod]
        public void A_garbled_reference_level_cannot_go_below_one()
        {
            Assert.AreEqual(1, Limits(maxLevel: 0).MaxLevel);
            Assert.AreEqual(1, Limits(maxLevel: -40).MaxLevel);
        }
    }
}
