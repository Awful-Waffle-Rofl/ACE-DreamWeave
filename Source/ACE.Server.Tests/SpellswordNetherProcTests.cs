using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Spellsword Nether support (owner ruling 2026-10-27): a Nether weapon's Spellblade, Runeblade and
    /// Spellstorm procs fire the void ladder instead of nothing, pick their level on Item Enchantment, and carry a
    /// per-cast damage scale that brings each void rung's average back to the Fire rung's.
    ///
    /// The (min, max) damage pairs below were read from the world spell table (base_Intensity, base_Intensity
    /// + variance) on 2026-10-27 with `SELECT id,name,base_Intensity,variance FROM spell WHERE id IN (...)`.
    /// The runtime computes the scale from the spell data the server loads (SpellswordSpellTables.ProcDamageScaleFor),
    /// so these pins document the shipped numbers and guard the pure math; they cannot see a content edit.
    /// </summary>
    [TestClass]
    public class SpellswordNetherProcTests
    {
        // ---- ids per level --------------------------------------------------------------------------

        [TestMethod]
        public void NetherStreakLadder_IsTheVoidStreakIdsInLevelOrder_NotContiguous()
        {
            CollectionAssert.AreEqual(new uint[] { 5357, 5358, 5359, 5360, 5345, 5346, 5347, 5348 },
                SpellswordSpellTables.StreakByType[DamageType.Nether].Select(s => (uint)s).ToArray());
        }

        [TestMethod]
        public void NetherBlastLadder_IsNetherBlastThreeThroughTheIncantation()
        {
            CollectionAssert.AreEqual(new uint[] { 5546, 5547, 5548, 5549, 5550, 5551 },
                SpellswordSpellTables.BlastByType[DamageType.Nether].Select(s => (uint)s).ToArray());
        }

        [TestMethod]
        public void NetherRing_IsCloudedSoul()
        {
            Assert.AreEqual(5361u, (uint)SpellswordSpellTables.RingByType[DamageType.Nether]);
        }

        [TestMethod]
        public void NetherSelection_PerRankCap_MatchesTheElementalShape()
        {
            // high skill: rank caps at III / V / VII exactly as the elemental ladders do
            Assert.AreEqual(5359u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels, DamageType.Nether, 1000, 1, 1.0).Value);
            Assert.AreEqual(5345u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels, DamageType.Nether, 1000, 2, 1.0).Value);
            Assert.AreEqual(5347u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels, DamageType.Nether, 1000, 3, 1.0).Value);

            Assert.AreEqual(5546u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BlastByType, SpellswordSpellTables.BlastLevels, DamageType.Nether, 1000, 1, 1.0).Value);
            Assert.AreEqual(5548u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BlastByType, SpellswordSpellTables.BlastLevels, DamageType.Nether, 1000, 2, 1.0).Value);
            Assert.AreEqual(5550u, (uint)SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BlastByType, SpellswordSpellTables.BlastLevels, DamageType.Nether, 1000, 3, 1.0).Value);
        }

        // ---- Item Enchantment picks the level -------------------------------------------------------

        [TestMethod]
        public void LevelSkillFor_IsItemEnchantment_ForEveryDamageType_NetherIncluded()
        {
            foreach (var type in new[] { DamageType.Nether, DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric })
                Assert.AreEqual(Skill.ItemEnchantment, SpellswordSpellTables.LevelSkillFor(type), type.ToString());
        }

        /// <summary>
        /// The two laddered handlers must feed the level selector the skill LevelSkillFor names, not a
        /// hard-coded War Magic; and every war proc must hand its scale to the cast. Source scan, because the
        /// handlers need a live Player.
        /// </summary>
        [TestMethod]
        public void WarProcHandlers_PickTheLevelSkillByDamageType_AndPassTheProcDamageScale()
        {
            foreach (var file in new[] { "SpellbladeAbility.cs", "RunebladeAbility.cs", "SpellstormAbility.cs" })
            {
                var src = PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/" + file);
                var body = PooledLootSourceText.MethodBody(src, "public void ModifyOutgoingDamage(");

                // Volatile Elements (2026-10-30) added a third argument, the re-typed hit's original type for the
                // proc's rend max; the parity scale is still the second.
                StringAssert.Contains(body, "procDamageScale,\n                damageEvent.OriginalDamageType);",
                    $"{file}: the cast must go through CastClassAbilityProc(cast, procDamageScale, damageEvent.OriginalDamageType)");
                StringAssert.Contains(body, "class_ability_spellsword_nether_parity", file);

                if (file != "SpellstormAbility.cs")
                {
                    StringAssert.Contains(body, "GetCreatureSkill(SpellswordSpellTables.LevelSkillFor(damageType))", file);
                    Assert.IsFalse(body.Contains("GetCreatureSkill(Skill.WarMagic)"), $"{file}: War Magic must no longer be hard-coded for the level pick");
                }
            }
        }

        // ---- parity scale per rung ------------------------------------------------------------------

        // (min, max) per rung, from the world spell table.
        private static readonly (int Min, int Max)[] FireStreak = { (16, 31), (18, 35), (21, 42), (25, 50), (29, 59), (36, 71), (42, 84), (47, 94) };
        private static readonly (int Min, int Max)[] NetherStreak = { (10, 27), (21, 42), (25, 56), (31, 73), (35, 88), (42, 105), (84, 130), (126, 162) };
        private static readonly (int Min, int Max)[] FireBlast = { (15, 31), (20, 40), (26, 52), (34, 69), (42, 84), (47, 94) };
        private static readonly (int Min, int Max)[] NetherBlast = { (21, 47), (31, 63), (36, 73), (42, 89), (78, 126), (115, 158) };
        private static readonly (int Min, int Max) FireRing = (42, 84);
        private static readonly (int Min, int Max) NetherRing = (94, 147);

        private static float Scale((int Min, int Max) reference, (int Min, int Max) nether, double parity = 1.0) =>
            SpellswordSpellTables.NetherParityScale(SpellswordSpellTables.AverageDamage(reference.Min, reference.Max),
                SpellswordSpellTables.AverageDamage(nether.Min, nether.Max), parity);

        [TestMethod]
        public void AverageDamage_IsTheMidpointOfTheInclusiveRoll()
        {
            Assert.AreEqual(23.5, SpellswordSpellTables.AverageDamage(16, 31), 1e-12);
            Assert.AreEqual(144.0, SpellswordSpellTables.AverageDamage(126, 162), 1e-12);
        }

        [TestMethod]
        public void StreakParity_PerRung_IsFireAverageOverNetherAverage()
        {
            var expected = new[] { 1.2703, 0.8413, 0.7778, 0.7212, 0.7154, 0.7279, 0.5888, 0.4896 };

            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], Scale(FireStreak[i], NetherStreak[i]), 1e-4, $"Streak rung {i + 1}");
        }

        [TestMethod]
        public void BlastParity_PerRung_IsFireAverageOverNetherAverage()
        {
            var expected = new[] { 0.6765, 0.6383, 0.7156, 0.7863, 0.6176, 0.5165 };

            for (var i = 0; i < expected.Length; i++)
                Assert.AreEqual(expected[i], Scale(FireBlast[i], NetherBlast[i]), 1e-4, $"Blast rung {i + 3}");
        }

        [TestMethod]
        public void RingParity_IsTheFireRingOverCloudedSoul()
        {
            Assert.AreEqual(0.5228, Scale(FireRing, NetherRing), 1e-4);
        }

        [TestMethod]
        public void ParityTunable_MultipliesTheScale_NegativeIsZero_MissingDataIsUnscaled()
        {
            Assert.AreEqual(Scale(FireRing, NetherRing) * 2.0f, Scale(FireRing, NetherRing, 2.0), 1e-6);
            Assert.AreEqual(0.0f, Scale(FireRing, NetherRing, -1.0));
            Assert.AreEqual(1.0f, SpellswordSpellTables.NetherParityScale(0.0, 120.5, 1.0), "missing reference data must not zero the proc");
            Assert.AreEqual(1.0f, SpellswordSpellTables.NetherParityScale(63.0, 0.0, 1.0));
        }

        [TestMethod]
        public void ParityReference_IsTheFireSpellAtTheSameRungIndex()
        {
            var nether = SpellswordSpellTables.StreakByType[DamageType.Nether];
            var fire = SpellswordSpellTables.StreakByType[DamageType.Fire];

            for (var i = 0; i < nether.Length; i++)
                Assert.AreEqual(fire[i], SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.StreakByType, DamageType.Nether, nether[i]));

            var netherBlast = SpellswordSpellTables.BlastByType[DamageType.Nether];
            var fireBlast = SpellswordSpellTables.BlastByType[DamageType.Fire];

            for (var i = 0; i < netherBlast.Length; i++)
                Assert.AreEqual(fireBlast[i], SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.BlastByType, DamageType.Nether, netherBlast[i]));

            Assert.AreEqual(SpellId.FlameRing, SpellswordSpellTables.RingParityReferenceFor(DamageType.Nether));
        }

        // ---- elemental procs are unscaled -----------------------------------------------------------

        [TestMethod]
        public void ElementalProcs_GetNoParityReference_AndScaleExactlyOne()
        {
            foreach (var type in new[] { DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon, DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric })
            {
                foreach (var spell in SpellswordSpellTables.StreakByType[type])
                    Assert.IsNull(SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.StreakByType, type, spell), $"{type} {spell}");

                foreach (var spell in SpellswordSpellTables.BlastByType[type])
                    Assert.IsNull(SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.BlastByType, type, spell), $"{type} {spell}");

                Assert.IsNull(SpellswordSpellTables.RingParityReferenceFor(type), type.ToString());
            }

            // no reference -> exactly 1.0, without touching spell data (no Spell is constructed)
            Assert.AreEqual(1.0f, SpellswordSpellTables.ProcDamageScaleFor(null, null, 1.0));
            Assert.AreEqual(1.0f, SpellswordSpellTables.ProcDamageScaleFor(null, null, 0.25), "the tunable never reaches an elemental proc");
        }

        [TestMethod]
        public void ParityReference_SpellNotInTheNetherLadder_IsNull()
        {
            Assert.IsNull(SpellswordSpellTables.ParityReferenceFor(SpellswordSpellTables.StreakByType, DamageType.Nether, SpellId.NetherBolt1));
        }

        // ---- tunable --------------------------------------------------------------------------------

        [TestMethod]
        public void NetherParityTunable_IsRegisteredAtOne()
        {
            Assert.AreEqual(1.0, DefaultPropertyManager.DefaultDoubleProperties["class_ability_spellsword_nether_parity"].Item, 1e-12);
        }
    }
}
