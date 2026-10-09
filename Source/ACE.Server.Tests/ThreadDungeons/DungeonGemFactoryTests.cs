using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// The pure surface of DungeonGemFactory (PHASE-2-IMPLEMENTATION-PLAN.md task D4b): the rung mapping, the
    /// two name composers, and the three item-level invariants the factory enforces on behalf of the codec.
    /// The WorldObjectFactory half is deliberately not covered - it needs a live world database - which is why
    /// every guard runs BEFORE the weenie read.
    /// </summary>
    [TestClass]
    public class DungeonGemFactoryTests
    {
        private static DungeonGemSpec Spec(int level = 195, int seed = 1234, (uint Wcid, int Doses)[] load = null)
            => new DungeonGemSpec("any", level, 7, "any", seed, new (string, double)[0], 0, 0, 0, null, load);

        /// <summary>
        /// The fourteen shipped rung weenies, Content/sql/weenies/1003615..1003624 and 1003626..1003629.
        /// Hand-copied here rather than read from DungeonGemFactory.Rungs on purpose: this array is the
        /// independent statement of the ladder, and a test that read the table it is checking would pass
        /// against any table at all. Note the step is IRREGULAR above 275 (tens, then twenty-fives) and the
        /// wcids SKIP 1003625, which is the Survey-Archivist's guide book.
        /// </summary>
        private static readonly (uint Wcid, int Level)[] Rungs =
        {
            (1003615u, 185), (1003616u, 195), (1003617u, 205), (1003618u, 215), (1003619u, 225),
            (1003620u, 235), (1003621u, 245), (1003622u, 255), (1003623u, 265), (1003624u, 275),
            (1003626u, 300), (1003627u, 325), (1003628u, 350), (1003629u, 375),
        };

        [TestMethod]
        public void RungWcid_maps_every_rung()
        {
            Assert.AreEqual(14, Rungs.Length, "the ladder is fourteen rungs after the 2026-09-09 ceiling raise");

            foreach (var (wcid, level) in Rungs)
                Assert.AreEqual(wcid, DungeonGemFactory.RungWcid(level), $"level {level}");
        }

        [TestMethod]
        public void RungWcid_clamps_below_185_and_above_375()
        {
            Assert.AreEqual(1003615u, DungeonGemFactory.RungWcid(1));
            Assert.AreEqual(1003615u, DungeonGemFactory.RungWcid(184));

            // Between rungs rounds DOWN, so a level-190 fragment is a level-185 rung weenie renamed.
            Assert.AreEqual(1003615u, DungeonGemFactory.RungWcid(190));

            // The rounding-down rule across the IRREGULAR step, which is where the old
            // "base + (level - 185) / 10" arithmetic would have gone wrong: 299 is still the 275 rung, and
            // 374 is still the 350 rung.
            Assert.AreEqual(1003624u, DungeonGemFactory.RungWcid(276));
            Assert.AreEqual(1003624u, DungeonGemFactory.RungWcid(299));
            Assert.AreEqual(1003626u, DungeonGemFactory.RungWcid(300));
            Assert.AreEqual(1003628u, DungeonGemFactory.RungWcid(374));

            Assert.AreEqual(1003629u, DungeonGemFactory.RungWcid(DungeonGemSpec.MaxGemLevel));
            Assert.AreEqual(1003629u, DungeonGemFactory.RungWcid(9999));
        }

        /// <summary>
        /// The ladder's own shape, independent of any level lookup: ascending, no repeats, and topping out at
        /// exactly the gem level ceiling. The last clause is the one that matters - a ceiling raise that
        /// forgot to add rungs would leave every gem above the top rung sharing one weenie, which reads to a
        /// player as a fragment labelled with the wrong level.
        /// </summary>
        [TestMethod]
        public void The_rung_ladder_is_ascending_and_reaches_the_gem_level_ceiling()
        {
            for (var i = 1; i < DungeonGemFactory.Rungs.Count; i++)
            {
                Assert.IsTrue(DungeonGemFactory.Rungs[i].Level > DungeonGemFactory.Rungs[i - 1].Level, $"rung {i} level is not ascending");
                Assert.IsTrue(DungeonGemFactory.Rungs[i].Wcid > DungeonGemFactory.Rungs[i - 1].Wcid, $"rung {i} wcid is not ascending");
            }

            Assert.AreEqual(185, DungeonGemFactory.RungBaseLevel);
            Assert.AreEqual(DungeonGemSpec.MaxGemLevel, DungeonGemFactory.RungTopLevel);
            Assert.AreEqual(DungeonGemFactory.Rungs.Count, DungeonGemFactory.RungCount);
            Assert.AreEqual(DungeonGemFactory.RawFragmentBaseWcid, DungeonGemFactory.Rungs[0].Wcid);
        }

        [TestMethod]
        public void ComposeFragmentName_matches_the_shipped_weenie_names()
        {
            var expectedNames = new[]
            {
                "Raw Fragment (Level 185)", "Raw Fragment (Level 195)", "Raw Fragment (Level 205)",
                "Raw Fragment (Level 215)", "Raw Fragment (Level 225)", "Raw Fragment (Level 235)",
                "Raw Fragment (Level 245)", "Raw Fragment (Level 255)", "Raw Fragment (Level 265)",
                "Raw Fragment (Level 275)", "Raw Fragment (Level 300)", "Raw Fragment (Level 325)",
                "Raw Fragment (Level 350)", "Raw Fragment (Level 375)",
            };

            for (var i = 0; i < Rungs.Length; i++)
            {
                Assert.AreEqual(expectedNames[i], DungeonGemFactory.ComposeFragmentName(Rungs[i].Level));
                Assert.AreEqual(expectedNames[i].Replace("Raw Fragment", "Raw Fragments"), DungeonGemFactory.ComposeFragmentPluralName(Rungs[i].Level));
            }
        }

        [TestMethod]
        public void CreateGem_rejects_a_spec_with_a_load()
        {
            var gem = DungeonGemFactory.CreateGem(Spec(load: new[] { (1650u, 2) }), 3, 3, "Filo's Doom", out var error);

            Assert.IsNull(gem);
            Assert.AreEqual(DungeonGemFactory.GemHasLoadError, error);
        }

        [TestMethod]
        public void CreateGem_rejects_seed_zero()
        {
            var gem = DungeonGemFactory.CreateGem(Spec(seed: 0), 3, 3, "Filo's Doom", out var error);

            Assert.IsNull(gem);
            Assert.AreEqual(DungeonGemFactory.GemHasNoSeedError, error);
        }

        [TestMethod]
        public void CreateFragment_rejects_a_nonzero_seed()
        {
            var fragment = DungeonGemFactory.CreateFragment(Spec(seed: 7), 1, 3, out var error);

            Assert.IsNull(fragment);
            Assert.AreEqual(DungeonGemFactory.FragmentHasSeedError, error);
        }

        /// <summary>
        /// Literal expected values, not the consts - asserting against DungeonGemFactory's own consts would
        /// make this tautological (change TierBluePaletteTemplate to 14 and the test still passes). 2 and 8
        /// are PaletteTemplate keys 1003600's own ClothingBase (0x1000010B) accepts, verified 2026-09-08 via
        /// `ACE.Content.Tools.exe paletteinfo 1003600 --world-db=ace_world` ("2 Blue", "8 Green" among its
        /// 11 valid values).
        /// </summary>
        [TestMethod]
        public void PaletteForTier_is_blue_at_and_above_the_threshold()
        {
            Assert.AreEqual(2, DungeonGemFactory.PaletteForTier(8), "PaletteTemplate 2 is Blue");
            Assert.AreEqual(2, DungeonGemFactory.PaletteForTier(9), "PaletteTemplate 2 is Blue");

            // Const-based assertions too, so a deliberate rename of the const still gets caught here.
            Assert.AreEqual(DungeonGemFactory.TierBluePaletteTemplate, DungeonGemFactory.PaletteForTier(8));
            Assert.AreEqual(DungeonGemFactory.TierBluePaletteTemplate, DungeonGemFactory.PaletteForTier(9));
        }

        /// <summary>See the literal-values comment above; 8 is PaletteTemplate Green.</summary>
        [TestMethod]
        public void PaletteForTier_is_green_below_the_threshold_and_for_unknown_tiers()
        {
            Assert.AreEqual(8, DungeonGemFactory.PaletteForTier(7), "PaletteTemplate 8 is Green");
            Assert.AreEqual(8, DungeonGemFactory.PaletteForTier(1), "PaletteTemplate 8 is Green");
            Assert.AreEqual(8, DungeonGemFactory.PaletteForTier(0), "PaletteTemplate 8 is Green");

            Assert.AreEqual(DungeonGemFactory.TierGreenPaletteTemplate, DungeonGemFactory.PaletteForTier(7));
            Assert.AreEqual(DungeonGemFactory.TierGreenPaletteTemplate, DungeonGemFactory.PaletteForTier(1));
            Assert.AreEqual(DungeonGemFactory.TierGreenPaletteTemplate, DungeonGemFactory.PaletteForTier(0));
        }
    }
}
