using System;
using System.Collections.Generic;
using System.Linq;

using ACE.Server.WorldEvents;
using ACE.Server.WorldEvents.Defs;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Unit coverage for the WP-03 half of <see cref="WorldEventRosterSelector"/> - the trash/elite bands,
    /// PickWave and PickChampion (PLAN 1.5, TECH-DESIGN 5.6). Everything is pure and rng-injected, so no
    /// world, no catalog scan and no live Player is involved (D6).
    /// </summary>
    [TestClass]
    public class WorldEventRosterSelectorTests
    {
        private static FamilyMember Member(uint wcid, int level, int role)
        {
            return new FamilyMember { Wcid = wcid, Name = $"member{wcid}", Level = level, Role = role };
        }

        private static FamilyDef Family(params FamilyMember[] members)
        {
            return new FamilyDef
            {
                Id = "emberwrought",
                DisplayName = "the Emberwrought",
                Members = members.ToList()
            };
        }

        private static SourceThemeDef Theme(int maxAlive = 24, int waveBase = 4, double perParticipant = 1.5,
            int cap = 18)
        {
            return new SourceThemeDef
            {
                Id = "ambush",
                MaxAlive = maxAlive,
                WaveCount = new ScaledCount { Base = waveBase, PerParticipant = perParticipant, Cap = cap }
            };
        }

        /// <summary>Median 100, p90 150: trash band [100, 150], elite band [187, 263].</summary>
        private static AudienceEstimate Estimate(int count = 4, int median = 100, int p90 = 150)
        {
            return new AudienceEstimate(count, median, p90);
        }

        // ---- (a) band edges ----------------------------------------------------------------------------

        [TestMethod]
        public void TrashBand_RoundsOutwardsAtBothEdges()
        {
            var band = WorldEventRosterSelector.TrashBand(100);

            Assert.AreEqual(100, band.Low);
            Assert.AreEqual(150, band.High);

            Assert.IsTrue(band.Contains(100), "the low edge is inclusive");
            Assert.IsTrue(band.Contains(150), "the high edge is inclusive");
            Assert.IsFalse(band.Contains(99));
            Assert.IsFalse(band.Contains(151));
        }

        [TestMethod]
        public void TrashBand_FractionalEdges_FloorLowAndCeilingHigh()
        {
            // 37 * 1.0 = 37 (exact - the low multiplier is a whole number); 37 * 1.5 = 55.5 -> 56.
            // Rounding outwards keeps the integer band a superset of the real-valued interval rather than
            // clipping members that sit just inside it.
            var band = WorldEventRosterSelector.TrashBand(37);

            Assert.AreEqual(37, band.Low);
            Assert.AreEqual(56, band.High);
        }

        [TestMethod]
        public void EliteBand_RunsFromP90LowMultToP90HighMult()
        {
            // 150 * 1.25 = 187.5 -> floor 187; 150 * 1.75 = 262.5 -> ceil 263.
            var band = WorldEventRosterSelector.EliteBand(150);

            Assert.AreEqual(187, band.Low);
            Assert.AreEqual(263, band.High);

            Assert.IsTrue(band.Contains(187));
            Assert.IsTrue(band.Contains(263));
            Assert.IsFalse(band.Contains(186));
            Assert.IsFalse(band.Contains(264));
        }

        [TestMethod]
        public void Bands_AtZero_AreDegenerateButValid()
        {
            Assert.AreEqual(0, WorldEventRosterSelector.TrashBand(0).Low);
            Assert.AreEqual(0, WorldEventRosterSelector.TrashBand(0).High);
            Assert.AreEqual(0, WorldEventRosterSelector.EliteBand(-50).High, "a negative estimate clamps to 0");
        }

        // ---- (a2) WP-24: the 275 level cap ----------------------------------------------------------------

        [TestMethod]
        public void EstimateFromLevels_ClampsMedianAndP90AtMaxConsideredLevel()
        {
            // n=10, all above 275: median (index 4) and p90 (index ceil(9)-1=8) both clamp to 275.
            var levels = new List<int> { 280, 290, 300, 310, 320, 330, 340, 350, 360, 370 };

            var estimate = WorldEventRosterSelector.EstimateFromLevels(levels);

            Assert.AreEqual(WorldEventRosterSelector.MaxConsideredLevel, estimate.MedianLevel);
            Assert.AreEqual(WorldEventRosterSelector.MaxConsideredLevel, estimate.P90Level);
        }

        [TestMethod]
        public void TrashBand_AtTheCap_HighEdgeClampsTo275()
        {
            var band = WorldEventRosterSelector.TrashBand(275);

            Assert.AreEqual(275, band.High, "a level-275 audience must never draw a level-300+ trash member");
        }

        [TestMethod]
        public void EliteBand_AtTheCap_FallsBackToTheTrashBand()
        {
            // p90=275: low=floor(275*1.25)=343, high=min(275, ceil(275*1.75))=275; high<low so high is
            // pulled up to 343, leaving width 0 < 10 -> falls back to TrashBand(275) = [275, 275].
            var elite = WorldEventRosterSelector.EliteBand(275);
            var trash = WorldEventRosterSelector.TrashBand(275);

            Assert.AreEqual(trash.Low, elite.Low);
            Assert.AreEqual(trash.High, elite.High);
        }

        [TestMethod]
        public void EliteBand_AtTwoHundred_ClampsTheHighEdgeWithoutFallingBack()
        {
            // low=floor(200*1.25)=250, high=min(275, ceil(200*1.75)=350)=275, width 25 >= 10 -> no fallback.
            var band = WorldEventRosterSelector.EliteBand(200);

            Assert.AreEqual(250, band.Low);
            Assert.AreEqual(275, band.High);
        }

        // ---- (b) PickWave band membership ---------------------------------------------------------------

        [TestMethod]
        public void PickWave_DrawsOnlyRoleZeroMembersInsideTheTrashBand()
        {
            // Estimate() median 100 -> TrashBand(100) = [100, 150].
            var family = Family(
                Member(1, 99, 0),    // just below the band
                Member(2, 100, 0),   // low edge, in
                Member(3, 120, 0),
                Member(4, 150, 0),   // high edge, in
                Member(5, 151, 0),   // just above the band
                Member(6, 120, 1),   // right level, wrong role
                Member(7, 120, 2));

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 0, 0, new Random(3)).Trash;

            Assert.IsTrue(picked.Count > 0);

            foreach (var wcid in picked)
                Assert.IsTrue(wcid == 2 || wcid == 3 || wcid == 4, $"wcid {wcid} is outside the trash band or not role 0");
        }

        [TestMethod]
        public void PickWave_EmptyBand_FallsThroughToTheMembersNearestTheBandCentre()
        {
            // Band is [100, 150], centre 125. Nothing is inside it, so the nearest role-0 member wins:
            // |40 - 125| = 85, |300 - 125| = 175, so level 40 is the fall-through.
            var family = Family(
                Member(1, 40, 0),
                Member(2, 300, 0));

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 0, 0, new Random(3)).Trash;

            Assert.IsTrue(picked.Count > 0);
            Assert.IsTrue(picked.All(w => w == 1), "fall-through must pick the member nearest the band centre");
        }

        [TestMethod]
        public void PickWave_FamilyWithNoRoleZeroMember_StillProducesAWave()
        {
            // Content mistake, not a reason to place nothing: a wave with room must never come back empty.
            var family = Family(Member(9, 100, 1), Member(10, 160, 2));

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 0, 0, new Random(3)).Trash;

            Assert.IsTrue(picked.Count > 0);
            Assert.IsTrue(picked.All(w => w == 9 || w == 10));
        }

        [TestMethod]
        public void PickWave_EmptyFamily_ReturnsEmpty()
        {
            Assert.AreEqual(0, WorldEventRosterSelector.PickWave(Family(), Estimate(), Theme(), 0, 0, new Random(1)).Trash.Count);
            Assert.AreEqual(0, WorldEventRosterSelector.PickWave(null, Estimate(), Theme(), 0, 0, new Random(1)).Trash.Count);
            Assert.AreEqual(0, WorldEventRosterSelector.PickWave(Family(Member(1, 100, 0)), Estimate(), null, 0, 0, new Random(1)).Trash.Count);
        }

        // ---- (c) PickWave size against MaxAlive ---------------------------------------------------------

        [TestMethod]
        public void PickWave_NeverExceedsTheRoomLeftUnderMaxAlive()
        {
            var family = Family(Member(1, 100, 0));

            // waveCount would resolve to base 4 + ceil(1.5 * 4) = 10, but only 3 slots are free.
            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(maxAlive: 24), 0, 21, new Random(1)).Trash;

            Assert.AreEqual(3, picked.Count);
        }

        [TestMethod]
        public void PickWave_IsEmptyWhenTheFieldIsFull()
        {
            var family = Family(Member(1, 100, 0));

            Assert.AreEqual(0, WorldEventRosterSelector.PickWave(family, Estimate(), Theme(maxAlive: 24), 0, 24, new Random(1)).Trash.Count);
            Assert.AreEqual(0, WorldEventRosterSelector.PickWave(family, Estimate(), Theme(maxAlive: 24), 0, 99, new Random(1)).Trash.Count,
                "over-full counts as full, never as negative room");
        }

        [TestMethod]
        public void PickWave_IsNeverEmptyWhileThereIsAnyRoom()
        {
            var family = Family(Member(1, 100, 0));

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(count: 0), Theme(maxAlive: 24, waveBase: 1, perParticipant: 0, cap: 1), 0, 23, new Random(1)).Trash;

            Assert.AreEqual(1, picked.Count);
        }

        [TestMethod]
        public void PickWave_ResolvesTheThemeWaveCountAgainstTheAudienceCount()
        {
            var family = Family(Member(1, 100, 0));
            var theme = Theme();   // base 4, perParticipant 1.5, cap 18

            Assert.AreEqual(4, WorldEventRosterSelector.PickWave(family, Estimate(count: 0), theme, 0, 0, new Random(1)).Trash.Count);
            Assert.AreEqual(10, WorldEventRosterSelector.PickWave(family, Estimate(count: 4), theme, 0, 0, new Random(1)).Trash.Count);
            Assert.AreEqual(18, WorldEventRosterSelector.PickWave(family, Estimate(count: 40), Theme(maxAlive: 100), 0, 0, new Random(1)).Trash.Count,
                "the ScaledCount cap holds");
        }

        // ---- (d) the every-third-wave elite slot --------------------------------------------------------

        [TestMethod]
        public void PickWave_OnWaveIndexTwo_ReplacesExactlyOneSlotWithAnElite()
        {
            // The elite slot bands on a SAMPLED participant level (#632), and Estimate() carries no level
            // list, so the sample is the median 100 and the band is EliteBand(100) = [125, 175]. The draw
            // inside it is strict (#629), so the elite must really sit in that band to be picked.
            var family = Family(
                Member(1, 100, 0),
                Member(2, 150, 1));   // inside the elite band [125, 175]

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 2, 0, new Random(5)).Trash;

            Assert.AreEqual(1, picked.Count(w => w == 2), "exactly one slot becomes the elite");
            Assert.IsTrue(picked.Count > 1, "the rest of the wave is still trash");
            Assert.IsTrue(picked.Where(w => w != 2).All(w => w == 1));
        }

        [TestMethod]
        public void PickWave_OnEveryOtherWaveIndex_NeverPlacesAnElite()
        {
            var family = Family(Member(1, 100, 0), Member(2, 120, 1));

            foreach (var waveIndex in new[] { 0, 1, 3, 4, 6, 7 })
            {
                var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), waveIndex, 0, new Random(5)).Trash;

                Assert.AreEqual(0, picked.Count(w => w == 2), $"wave {waveIndex} must be all trash");
            }
        }

        [TestMethod]
        public void PickWave_OnWaveIndexFive_AlsoPlacesAnElite()
        {
            var family = Family(Member(1, 100, 0), Member(2, 150, 1));   // inside EliteBand(100) = [125, 175]

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 5, 0, new Random(5)).Trash;

            Assert.AreEqual(1, picked.Count(w => w == 2));
        }

        [TestMethod]
        public void PickWave_WithNoRoleOneMember_SkipsTheEliteSlotEntirely()
        {
            var family = Family(Member(1, 100, 0), Member(2, 160, 2));

            var picked = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 2, 0, new Random(5)).Trash;

            Assert.IsTrue(picked.All(w => w == 1), "a family with no elite gets no elite, not a champion");
        }

        // ---- (d2) WP-24: no nearest-band fallback for elites/champions; role-0 promotion instead ----------

        [TestMethod]
        public void PickWave_NoRealElite_PromotesARoleZeroMemberFromTheSameBand_MarkedSynthetic()
        {
            // p90=65 -> EliteBand(65) = [81, 114] (low=floor(65*1.25)=81, high=min(275,ceil(65*1.75)=114)=114,
            // width 33 >= 10, no trash-band fallback). The role-1 member is level 300 - nowhere near the band, so ElitePool is
            // EMPTY (no nearest fallback, per the owner ruling). The role-0 members are level 40-50, also
            // outside [65, 91], so PromotionPool falls through to the nearest role-0 member(s) by level -
            // exactly the trash draw's own fall-through - and is marked synthetic.
            var family = Family(
                Member(1, 300, 1),   // the old code would have handed THIS out as "the elite"
                Member(2, 40, 0),
                Member(3, 50, 0));

            var estimate = new AudienceEstimate(4, 60, 65);

            var pick = WorldEventRosterSelector.PickWave(family, estimate, Theme(), 2, 0, new Random(5));

            Assert.IsTrue(pick.EliteIndex >= 0, "a role-0 promotion must still fill the elite slot");
            Assert.IsTrue(pick.EliteSynthetic, "the promoted elite must be marked synthetic");

            var eliteWcid = pick.Trash[pick.EliteIndex];
            Assert.IsTrue(eliteWcid == 2 || eliteWcid == 3, "the promoted elite must come from the role-0 members, never the out-of-band role-1");
            Assert.AreNotEqual(1u, eliteWcid, "wcid 1 (level 300, role 1) must never be picked - that is the bug this fixes");
        }

        [TestMethod]
        public void PickWave_RealInBandElite_IsPickedAndNotSynthetic()
        {
            var family = Family(
                Member(1, 100, 0),
                // #632 composed with #629: the elite slot bands on a SAMPLED participant level, and
                // Estimate() has no level list, so the sample is the median 100 -> EliteBand(100) = [125, 175].
                Member(2, 150, 1));

            var pick = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 2, 0, new Random(5));

            Assert.IsTrue(pick.EliteIndex >= 0);
            Assert.IsFalse(pick.EliteSynthetic, "a real in-band elite must never be marked synthetic");
            Assert.AreEqual(2u, pick.Trash[pick.EliteIndex]);
        }

        [TestMethod]
        public void PickWave_ChampionPool_EmptyInBand_PromotesARoleZeroMember_MarkedSynthetic()
        {
            // Same band shape as the elite test above: EliteBand(65) = [81, 114]. The role-2 member is level
            // 300 (out of band, so ChampionPool -> role-2 in band -> empty; role-1 in band -> also empty, no
            // role-1 members at all). PromotionPool falls through to the nearest role-0 member(s).
            var family = Family(
                Member(1, 300, 2),
                Member(2, 40, 0),
                Member(3, 50, 0));

            var estimate = new AudienceEstimate(4, 60, 65);

            // Force at least one overflow champion: a big quantityBonus and an overflowPerChampion of 1.
            var theme = new SourceThemeDef
            {
                Id = "ambush",
                MaxAlive = 24,
                WaveCount = new ScaledCount { Base = 1, PerParticipant = 0, Cap = 1 },
                OverflowPerChampion = 1,
                OverflowChampionMaxAlive = 3
            };

            var pick = WorldEventRosterSelector.PickWave(family, estimate, theme, 0, 0, quantityBonus: 5, championsAlive: 0, rng: new Random(5));

            Assert.IsTrue(pick.Champions.Count > 0, "the overflow arithmetic must produce at least one champion slot");
            Assert.IsTrue(pick.ChampionsSynthetic.All(s => s), "every champion here must be a role-0 promotion");
            Assert.IsTrue(pick.Champions.All(w => w == 2 || w == 3), "the promoted champion must come from the role-0 members, never the out-of-band role-2");
        }

        /// <summary>
        /// The composition of #632 (per-slot proportional bands) with #629 (strict in-band elites, else a
        /// role-0 promotion), which is the whole point of this merge: the elite slot bands on ONE SAMPLED
        /// participant level, and what that band produces is in-band or synthetic - never an out-of-band
        /// real elite.
        ///
        /// Audience: nine level 50s and one level 275, so ~90% of elite slots sample 50 and ~10% sample 275.
        ///   * a level-50 sample gives EliteBand(50) = [62, 88] (floor(50*1.25)=62, ceil(50*1.75)=88), which
        ///     holds the real level-70 role-1 elite;
        ///   * a level-275 sample gives EliteBand(275) - low floors to 343 (above the 275 cap), so it is
        ///     pulled down to match the clamped high edge, leaving a single collapsed level; that narrower-
        ///     than-NarrowEliteBandFallbackWidth band falls back to TrashBand(275) = [275, 275] - which holds
        ///     NEITHER role-1 elite, so the slot is a role-0 promotion.
        /// The level-300 elite must never appear in either case: that is the level-300 Captain Keeson bug.
        /// </summary>
        [TestMethod]
        public void PickWave_MixedLevelAudience_DrawsTheInBandEliteOrPromotes_ButNeverTheOutOfBandElite()
        {
            var family = Family(
                Member(1, 50, 0),
                Member(2, 70, 1),     // in EliteBand(50) = [62, 88]
                Member(3, 300, 1));   // in no band this audience can produce

            var levels = new List<int> { 50, 50, 50, 50, 50, 50, 50, 50, 50, 275 };

            var estimate = WorldEventRosterSelector.EstimateFromLevels(levels);

            Assert.AreEqual(10, estimate.Levels.Count, "the estimate must carry the raw level list to sample from");

            var rng = new Random(20260816);

            var realElites = 0;
            var syntheticElites = 0;

            for (var i = 0; i < 200; i++)
            {
                var pick = WorldEventRosterSelector.PickWave(family, estimate, Theme(), waveIndex: 2,
                    currentAlive: 0, quantityBonus: 0, championsAlive: 0, rng: rng);

                Assert.IsFalse(pick.Trash.Contains(3u), "the level-300 elite is outside every band this audience produces");
                Assert.IsFalse(pick.Champions.Contains(3u), "the level-300 elite must not reach an overflow champion slot either");

                Assert.IsTrue(pick.EliteIndex >= 0, "wave index 2 always fills the elite slot, really or synthetically");

                var eliteWcid = pick.Trash[pick.EliteIndex];

                if (pick.EliteSynthetic)
                {
                    Assert.AreEqual(1u, eliteWcid, "a promotion comes from the role-0 members");
                    syntheticElites++;
                }
                else
                {
                    Assert.AreEqual(2u, eliteWcid, "a real elite is the in-band level-70 one");
                    realElites++;
                }
            }

            Assert.IsTrue(realElites > 0, "the level-50 samples must draw the real in-band level-60 elite");
            Assert.IsTrue(syntheticElites > 0, "the level-275 samples must find no real elite and promote instead");
        }

        // ---- (e) determinism -----------------------------------------------------------------------------

        [TestMethod]
        public void PickWave_IsDeterministicUnderTheInjectedRandom()
        {
            var family = Family(Member(1, 90, 0), Member(2, 100, 0), Member(3, 110, 0), Member(4, 160, 1));

            var a = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 2, 0, new Random(777)).Trash;
            var b = WorldEventRosterSelector.PickWave(family, Estimate(), Theme(), 2, 0, new Random(777)).Trash;

            CollectionAssert.AreEqual(a.ToList(), b.ToList());
        }

        // ---- (f) PickChampion preference order ------------------------------------------------------------
        //
        // WP-24 follow-up (owner ruling 2026-08-16): PickChampion no longer falls back to an out-of-band
        // role-2/role-1/any-role member - that fallback is what let a level-50 audience's lugian family
        // still produce a level-300 Captain Keeson at the once-per-run champion mark. It is now exactly
        // ChampionPool then PromotionPool, the same shape the wave-path elite/champion slots use.

        [TestMethod]
        public void PickChampion_PrefersTheHighestRoleTwoMemberInsideTheEliteBand()
        {
            var family = Family(
                Member(1, 100, 0),
                Member(2, 200, 2),   // in band [187, 263]
                Member(3, 250, 2),   // in band, higher
                Member(4, 300, 2));  // role 2 but above the band

            var pick = WorldEventRosterSelector.PickChampion(family, Estimate());

            Assert.AreEqual(3u, pick.Wcid);
            Assert.IsFalse(pick.Synthetic, "a real in-band role-2 champion must never be marked synthetic");
        }

        [TestMethod]
        public void PickChampion_WithNoRoleTwoOrOneInBand_PromotesARoleZeroMember_MarkedSynthetic()
        {
            // Neither role-2 member is in the elite band [187, 263], and there is no role-1 member at all -
            // ChampionPool is EMPTY (no nearest-band fallback), so this now falls through to a role-0
            // promotion from the SAME band rather than the highest role-2 anywhere.
            var family = Family(
                Member(1, 100, 0),
                Member(2, 60, 2),
                Member(3, 90, 2));

            var pick = WorldEventRosterSelector.PickChampion(family, Estimate());

            Assert.AreEqual(1u, pick.Wcid, "the only role-0 member is the sole promotion candidate");
            Assert.IsTrue(pick.Synthetic);
        }

        [TestMethod]
        public void PickChampion_WithNoRoleTwoAtAllAndRoleOneOutOfBand_PromotesARoleZeroMember_MarkedSynthetic()
        {
            var family = Family(
                Member(1, 100, 0),
                Member(2, 130, 1));   // out of the elite band [187, 263]

            var pick = WorldEventRosterSelector.PickChampion(family, Estimate());

            Assert.AreEqual(1u, pick.Wcid);
            Assert.IsTrue(pick.Synthetic, "an out-of-band role-1 must never be returned as the champion; wcid 2 would be the pre-fix bug");
        }

        [TestMethod]
        public void PickChampion_LugianShapedFamilyAtLowLevel_NeverReturnsTheOutOfBandCaptain_PromotesInstead()
        {
            // The exact shape the owner reported: a role-1 "Captain Keeson" at level 300, and a level-50
            // audience whose role-0 members are level 40-50. The old fallback ("highest role-2 anywhere,
            // else highest of ANY role") would have handed this audience the level-300 captain; the fix must
            // promote a role-0 member from the band instead.
            var family = Family(
                Member(300, 300, 1),   // "Captain Keeson"
                Member(41, 41, 0),
                Member(50, 50, 0));

            var estimate = new AudienceEstimate(12, 50, 50);   // matches the reported incident: 12 level-50 players

            var pick = WorldEventRosterSelector.PickChampion(family, estimate);

            Assert.AreNotEqual(300u, pick.Wcid, "the out-of-band level-300 captain must never be returned - this is the bug being fixed");
            Assert.IsFalse(pick.IsNone, "a role-0 member must be promoted rather than declining the champion entirely");
            Assert.IsTrue(pick.Wcid == 41 || pick.Wcid == 50, "the promoted champion must come from the in-family role-0 members");
            Assert.IsTrue(pick.Synthetic);
        }

        [TestMethod]
        public void PickChampion_NothingInBandAndNoRoleZeroMember_ReturnsNone()
        {
            // Role-1 only, out of band, and no role-0 member at all to promote - the champion must be
            // declined entirely rather than reaching for the out-of-band role-1.
            var family = Family(Member(2, 300, 1));

            var pick = WorldEventRosterSelector.PickChampion(family, Estimate());

            Assert.IsTrue(pick.IsNone);
            Assert.AreEqual(0u, pick.Wcid);
        }

        [TestMethod]
        public void PickChampion_IsNeverZeroWhileTheFamilyHasARoleZeroMember()
        {
            Assert.AreNotEqual(0u, WorldEventRosterSelector.PickChampion(Family(Member(1, 1, 0)), Estimate()).Wcid);
            Assert.IsTrue(WorldEventRosterSelector.PickChampion(Family(), Estimate()).IsNone);
            Assert.IsTrue(WorldEventRosterSelector.PickChampion(null, Estimate()).IsNone);
        }

        [TestMethod]
        public void PickChampion_BreaksLevelTiesOnTheLowerWcid()
        {
            // Estimate() p90 150 -> EliteBand(150) = [187, 263]; both members must sit inside it to reach
            // ChampionPool rather than falling through to an empty pool (there is no role-0 member here).
            var family = Family(Member(9, 200, 2), Member(4, 200, 2));

            Assert.AreEqual(4u, WorldEventRosterSelector.PickChampion(family, Estimate()).Wcid);
        }

        // ---- (g) proportional band selection (user directive 2026-08-16) --------------------------------

        [TestMethod]
        public void SampleParticipantLevel_EmptyLevels_ReturnsMedian()
        {
            var est = Estimate(count: 4, median: 100, p90: 150);   // 3-arg constructor, empty Levels

            Assert.AreEqual(100, WorldEventRosterSelector.SampleParticipantLevel(est, new Random(1)));
            Assert.AreEqual(100, WorldEventRosterSelector.SampleParticipantLevel(est, new Random(999)));
        }

        [TestMethod]
        public void PickWave_MixedAudience_DrawsTrashProportionallyToParticipantLevels()
        {
            // Nine level-50s and one level-275: trash band [50, 75] for 50, [275, 275] for 275.
            var levels = new List<int>();
            for (var i = 0; i < 9; i++)
                levels.Add(50);
            levels.Add(275);

            var est = WorldEventRosterSelector.EstimateFromLevels(levels);

            var family = Family(Member(1, 50, 0), Member(2, 275, 0));
            var theme = Theme(maxAlive: 1000, waveBase: 10, perParticipant: 0, cap: 10);

            var rng = new Random(12345);
            var countA = 0;
            var countB = 0;

            for (var wave = 0; wave < 200; wave++)
            {
                // waveIndex always 0 so the elite slot (index % 3 == 2) never fires.
                var picked = WorldEventRosterSelector.PickWave(family, est, theme, 0, 0, rng).Trash;

                foreach (var wcid in picked)
                {
                    if (wcid == 1)
                        countA++;
                    else if (wcid == 2)
                        countB++;
                }
            }

            var total = countA + countB;
            var shareB = (double)countB / total;

            Assert.IsTrue(shareB >= 0.06 && shareB <= 0.14,
                $"expected the level-275 share to land near 10% (participant proportion), measured {shareB:P1} " +
                $"({countB} of {total})");
        }

        [TestMethod]
        public void PickWave_UniformAudience_MatchesTheMedianBandExactly()
        {
            var levels = new List<int>();
            for (var i = 0; i < 10; i++)
                levels.Add(100);

            var est = WorldEventRosterSelector.EstimateFromLevels(levels);

            var family = Family(Member(1, 100, 0), Member(2, 275, 0));
            var theme = Theme(maxAlive: 1000, waveBase: 10, perParticipant: 0, cap: 10);

            var pick = WorldEventRosterSelector.PickWave(family, est, theme, 0, 0, new Random(1));

            Assert.IsTrue(pick.Trash.Count > 0);
            Assert.IsTrue(pick.Trash.All(w => w == 1), "a uniform level-100 audience must draw only the level-100 member");

            var expectedBand = WorldEventRosterSelector.TrashBand(100);
            Assert.AreEqual(expectedBand.Low, pick.Band.Low);
            Assert.AreEqual(expectedBand.High, pick.Band.High);
        }

        [TestMethod]
        public void PickWave_ThreeArgEstimate_FallsBackToTheMedian()
        {
            var est = Estimate(count: 4, median: 100, p90: 150);   // 3-arg constructor, empty Levels

            var family = Family(Member(1, 100, 0), Member(2, 275, 0));
            var theme = Theme(maxAlive: 1000, waveBase: 10, perParticipant: 0, cap: 10);

            var picked = WorldEventRosterSelector.PickWave(family, est, theme, 0, 0, new Random(1)).Trash;

            Assert.IsTrue(picked.Count > 0);
            Assert.IsTrue(picked.All(w => w == 1), "an empty Levels list must fall back to the median every draw");
        }

        [TestMethod]
        public void PickWave_Band_SpansEveryTrashBandUsed()
        {
            var levels = new List<int>();
            for (var i = 0; i < 9; i++)
                levels.Add(50);
            levels.Add(275);

            var est = WorldEventRosterSelector.EstimateFromLevels(levels);

            var family = Family(Member(1, 50, 0), Member(2, 275, 0));
            // A single large wave (many slots) so both the 50-band and the 275-band are drawn within it -
            // the Band assertion needs to read off ONE pick, since Band spans only the slots that pick drew.
            var theme = Theme(maxAlive: 1000, waveBase: 300, perParticipant: 0, cap: 300);

            var pick = WorldEventRosterSelector.PickWave(family, est, theme, 0, 0, new Random(42));

            Assert.IsTrue(pick.Trash.Any(w => w == 1), "expected the level-50 band wcid to appear in a 300-slot wave");
            Assert.IsTrue(pick.Trash.Any(w => w == 2), "expected the level-275 band wcid to appear in a 300-slot wave");

            var band50 = WorldEventRosterSelector.TrashBand(50);
            var band275 = WorldEventRosterSelector.TrashBand(275);

            Assert.AreEqual(band50.Low, pick.Band.Low);
            Assert.AreEqual(band275.High, pick.Band.High);
        }

        [TestMethod]
        public void PickChampion_IsUnaffectedByTheLevelList()
        {
            var family = Family(Member(1, 100, 0), Member(2, 160, 2), Member(3, 200, 2));

            var threeArg = new AudienceEstimate(4, 100, 150);
            var fourArg = new AudienceEstimate(4, 100, 150, new List<int> { 50, 50, 50, 275 });

            Assert.AreEqual(
                WorldEventRosterSelector.PickChampion(family, threeArg),
                WorldEventRosterSelector.PickChampion(family, fourArg));
        }

        // ---- (h) band dial server properties (world_events_trash_band_low/high, world_events_elite_band_low/high) ----

        [TestMethod]
        public void TrashBand_PureOverload_HonoursCustomMultipliers()
        {
            // 100 * 0.5 = 50 (exact); 100 * 0.6 = 60 (exact).
            var band = WorldEventRosterSelector.TrashBand(100, 0.5, 0.6);

            Assert.AreEqual(50, band.Low);
            Assert.AreEqual(60, band.High);
        }

        [TestMethod]
        public void EliteBand_PureOverload_HonoursCustomMultipliers()
        {
            // 100 * 0.5 = 50 (exact); 100 * 0.9 = 90 (exact); width 40 >= 10, no fallback.
            var band = WorldEventRosterSelector.EliteBand(100, 0.5, 0.9);

            Assert.AreEqual(50, band.Low);
            Assert.AreEqual(90, band.High);
        }

        [TestMethod]
        public void TrashBand_InvalidMultipliers_FallBackToTheBuiltInDefaults()
        {
            foreach (var badLow in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var band = WorldEventRosterSelector.TrashBand(100, badLow, WorldEventRosterSelector.DefaultTrashBandHigh);
                Assert.AreEqual(100, band.Low, $"low multiplier {badLow} must fall back to the default (1.0)");
            }

            foreach (var badHigh in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var band = WorldEventRosterSelector.TrashBand(100, WorldEventRosterSelector.DefaultTrashBandLow, badHigh);
                Assert.AreEqual(150, band.High, $"high multiplier {badHigh} must fall back to the default (1.5)");
            }
        }

        [TestMethod]
        public void EliteBand_InvalidMultipliers_FallBackToTheBuiltInDefaults()
        {
            // 100 * 1.25 = 125 (default low); 100 * 1.75 = 175 (default high); width 50 >= 10, no fallback.
            foreach (var badLow in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var band = WorldEventRosterSelector.EliteBand(100, badLow, WorldEventRosterSelector.DefaultEliteBandHigh);
                Assert.AreEqual(125, band.Low, $"low multiplier {badLow} must fall back to the default (1.25)");
            }

            foreach (var badHigh in new[] { 0.0, -1.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var band = WorldEventRosterSelector.EliteBand(100, WorldEventRosterSelector.DefaultEliteBandLow, badHigh);
                Assert.AreEqual(175, band.High, $"high multiplier {badHigh} must fall back to the default (1.75)");
            }
        }

        [TestMethod]
        public void TrashBand_HighMultiplierBelowLow_CollapsesToASingleLevel()
        {
            // low=floor(100*2.0)=200; high=min(275, ceil(100*0.5))=50, which is below low, so high is raised
            // to match rather than returning an inverted band.
            var band = WorldEventRosterSelector.TrashBand(100, 2.0, 0.5);

            Assert.AreEqual(200, band.Low);
            Assert.AreEqual(200, band.High);
        }

        [TestMethod]
        public void EliteBand_HighMultiplierBelowLow_CollapsesAndFallsBackToTheDefaultTrashBand()
        {
            // low=floor(100*2.0)=200; high=min(275, ceil(100*0.5))=50, raised to 200; width 0 < 10 ->
            // falls back to TrashBand(100) at the BUILT-IN trash defaults (this overload is pure) = [100, 150].
            var band = WorldEventRosterSelector.EliteBand(100, 2.0, 0.5);

            Assert.AreEqual(100, band.Low);
            Assert.AreEqual(150, band.High);
        }

        [TestMethod]
        public void SingleArgBands_ReadTheDialSourceSeam()
        {
            var original = WorldEventRosterSelector.DialSource;

            try
            {
                WorldEventRosterSelector.DialSource = () => new BandDials(2.0, 3.0, 2.0, 3.0);

                // 100 * 2.0 = 200; 100 * 3.0 = 300, clamped to MaxConsideredLevel 275.
                var trash = WorldEventRosterSelector.TrashBand(100);
                Assert.AreEqual(200, trash.Low);
                Assert.AreEqual(275, trash.High);

                var elite = WorldEventRosterSelector.EliteBand(100);
                Assert.AreEqual(200, elite.Low);
                Assert.AreEqual(275, elite.High);
            }
            finally
            {
                WorldEventRosterSelector.DialSource = original;
            }
        }

        [TestMethod]
        public void SingleArgBands_ReadPropertyManagerWhenTheSeamIsUnset()
        {
            // With no test override, DialSource reads PropertyManager (or, absent shard config, falls back
            // to the built-in defaults) - so the single-arg forms must match the pure forms called with the
            // built-in default constants.
            var expectedTrash = WorldEventRosterSelector.TrashBand(100, WorldEventRosterSelector.DefaultTrashBandLow, WorldEventRosterSelector.DefaultTrashBandHigh);
            var expectedElite = WorldEventRosterSelector.EliteBand(100, WorldEventRosterSelector.DefaultEliteBandLow, WorldEventRosterSelector.DefaultEliteBandHigh);

            var trash = WorldEventRosterSelector.TrashBand(100);
            var elite = WorldEventRosterSelector.EliteBand(100);

            Assert.AreEqual(expectedTrash.Low, trash.Low);
            Assert.AreEqual(expectedTrash.High, trash.High);
            Assert.AreEqual(expectedElite.Low, elite.Low);
            Assert.AreEqual(expectedElite.High, elite.High);
        }

        [TestMethod]
        public void SingleArgEliteBand_NarrowFallback_UsesTheConfiguredTrashDials_NotTheBuiltInDefaults()
        {
            // Elite 1.25/1.30 at level 100: low=floor(125)=125, high=min(275, ceil(130))=130, width 5 < 10
            // -> falls back to TrashBand. The bug this covers: the fallback must use the CONFIGURED trash
            // dials (0.5/0.6 here), not the compiled-in DefaultTrashBandLow/High (1.0/1.5) - those would
            // give [100, 150], not [50, 60].
            var original = WorldEventRosterSelector.DialSource;

            try
            {
                WorldEventRosterSelector.DialSource = () => new BandDials(0.5, 0.6, 1.25, 1.30);

                var band = WorldEventRosterSelector.EliteBand(100);

                Assert.AreEqual(50, band.Low);
                Assert.AreEqual(60, band.High);
            }
            finally
            {
                WorldEventRosterSelector.DialSource = original;
            }
        }
    }
}
