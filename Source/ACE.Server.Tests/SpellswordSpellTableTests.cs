using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Entity;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for SpellswordSpellTables - the damage-type -> war-spell ladders and the shared
    /// "which rung can this character throw" selector. This is where the subtle correctness of the
    /// Spellsword class lives (SPELLSWORD-DESIGN.md section 2): both war ladders DO have a level VII
    /// rung, but it breaks the roman-numeral naming convention entirely (a uniquely named spell per
    /// element, e.g. "Sizzling Fury" for Fire Streak VII) - so a name-shaped search for "Streak VII"
    /// finds nothing and an "absence" conclusion from that search is unsafe. The ladders are also
    /// different lengths per family (Blast starts at III), and the Ring ids are not in damage-type
    /// order. Every expected value here is re-derived from the production source
    /// (SpellswordSpellTables.cs and SpellFormula.cs) rather than copied from the design doc, so this
    /// test catches the two drifting apart.
    /// </summary>
    [TestClass]
    public class SpellswordSpellTableTests
    {
        private static readonly DamageType[] SevenTypes =
        {
            DamageType.Slash, DamageType.Pierce, DamageType.Bludgeon,
            DamageType.Cold, DamageType.Fire, DamageType.Acid, DamageType.Electric,
        };

        // ---- 1. all seven damage types present, and only those seven -----------------------------

        [TestMethod]
        public void StreakByType_ContainsExactlyTheSevenPhysicalElementalTypes()
        {
            CollectionAssert.AreEquivalent(SevenTypes, SpellswordSpellTables.StreakByType.Keys.ToArray());
        }

        [TestMethod]
        public void BlastByType_ContainsExactlyTheSevenPhysicalElementalTypes()
        {
            CollectionAssert.AreEquivalent(SevenTypes, SpellswordSpellTables.BlastByType.Keys.ToArray());
        }

        [TestMethod]
        public void RingByType_ContainsExactlyTheSevenPhysicalElementalTypes()
        {
            CollectionAssert.AreEquivalent(SevenTypes, SpellswordSpellTables.RingByType.Keys.ToArray());
        }

        // ---- 2. ladder lengths and rung levels ----------------------------------------------------

        [TestMethod]
        public void StreakLevels_IsExactlyTheEightRungLadder()
        {
            CollectionAssert.AreEqual(new uint[] { 1, 2, 3, 4, 5, 6, 7, 8 }, SpellswordSpellTables.StreakLevels);
        }

        [TestMethod]
        public void BlastLevels_IsExactlyTheSixRungLadderStartingAtThree()
        {
            CollectionAssert.AreEqual(new uint[] { 3, 4, 5, 6, 7, 8 }, SpellswordSpellTables.BlastLevels);
        }

        /// <summary>
        /// Both war ladders DO have a level-7 spell - an earlier revision wrongly concluded otherwise
        /// because the level-7 rung breaks the roman-numeral naming convention (a uniquely named spell
        /// per element, e.g. "Sizzling Fury" for Fire Streak VII, exactly like the level-7 vulnerability
        /// rung being the "X's Gift" family), so a name-shaped search for "Streak VII" finds nothing.
        /// Absence of a name match is not proof of absence - see SpellswordSpellTables.StreakLevels's
        /// and BlastLevels's own doc comments for the portal.dat verification (Power 300/325, damage
        /// progression) that established the level-7 rungs are real.
        /// </summary>
        [TestMethod]
        public void BothWarLadders_ContainLevelSeven_DespiteTheNamingBreak()
        {
            CollectionAssert.Contains(SpellswordSpellTables.StreakLevels, 7u);
            CollectionAssert.Contains(SpellswordSpellTables.BlastLevels, 7u);
        }

        [TestMethod]
        public void EveryStreakLadderArray_HasTheSameLengthAsStreakLevels()
        {
            foreach (var type in SevenTypes)
            {
                Assert.AreEqual(SpellswordSpellTables.StreakLevels.Length, SpellswordSpellTables.StreakByType[type].Length,
                    $"{type}: Streak ladder length does not match StreakLevels - a mismatch would silently misreport that rung's level");
            }
        }

        [TestMethod]
        public void EveryBlastLadderArray_HasTheSameLengthAsBlastLevels()
        {
            foreach (var type in SevenTypes)
            {
                Assert.AreEqual(SpellswordSpellTables.BlastLevels.Length, SpellswordSpellTables.BlastByType[type].Length,
                    $"{type}: Blast ladder length does not match BlastLevels - a mismatch would silently misreport that rung's level");
            }
        }

        // ---- 4. Ring ids are exact and not in damage-type order -----------------------------------

        /// <summary>
        /// The tier-I Ring ids, by numeric id, exactly as SPELLSWORD-DESIGN.md section 2c records them.
        /// THESE ARE NOT IN DAMAGE-TYPE ORDER (Acid 1783, Slash 1784, Fire 1785, Pierce 1786, Cold 1787,
        /// Electric 1788, Bludgeon 1789) and must never be computed from an offset - the design doc says
        /// so explicitly, and RingByType.cs's own doc comment repeats the warning. The numeric ids below
        /// come straight from the SpellId enum's own assigned values (auto-incrementing from Undef = 0),
        /// not from the design doc, so this test would catch the enum drifting away from the doc too.
        /// </summary>
        [TestMethod]
        public void RingIds_AreTheExactNumericIdsFromTheDesignDoc_NotInDamageTypeOrder()
        {
            Assert.AreEqual(1783u, (uint)SpellswordSpellTables.RingByType[DamageType.Acid]);
            Assert.AreEqual(1784u, (uint)SpellswordSpellTables.RingByType[DamageType.Slash]);
            Assert.AreEqual(1785u, (uint)SpellswordSpellTables.RingByType[DamageType.Fire]);
            Assert.AreEqual(1786u, (uint)SpellswordSpellTables.RingByType[DamageType.Pierce]);
            Assert.AreEqual(1787u, (uint)SpellswordSpellTables.RingByType[DamageType.Cold]);
            Assert.AreEqual(1788u, (uint)SpellswordSpellTables.RingByType[DamageType.Electric]);
            Assert.AreEqual(1789u, (uint)SpellswordSpellTables.RingByType[DamageType.Bludgeon]);
        }

        // ---- 5. MaxLevelForRank ---------------------------------------------------------------------

        [TestMethod]
        public void MaxLevelForRank_FollowsTheThreeFiveSevenLadderCap()
        {
            Assert.AreEqual(3u, SpellswordSpellTables.MaxLevelForRank(1));
            Assert.AreEqual(5u, SpellswordSpellTables.MaxLevelForRank(2));
            Assert.AreEqual(7u, SpellswordSpellTables.MaxLevelForRank(3));
        }

        /// <summary>
        /// The source's guard is `rank &lt;= 1 -&gt; 3`, so rank 0 (unowned) and any negative rank both
        /// return the rank-1 cap rather than 0 or throwing - there is no "no cap" concept in this
        /// function; callers gate ownership/rank elsewhere (e.g. Chance() returning 0 for rank &lt;= 0)
        /// before this is ever consulted.
        /// </summary>
        [TestMethod]
        public void MaxLevelForRank_ZeroAndNegativeRanksReturnTheRankOneCap()
        {
            Assert.AreEqual(3u, SpellswordSpellTables.MaxLevelForRank(0));
            Assert.AreEqual(3u, SpellswordSpellTables.MaxLevelForRank(-1));
        }

        // ---- 6. MinPowerForLevel mirrors SpellFormula.MinPower exactly ----------------------------

        [TestMethod]
        public void MinPowerForLevel_MatchesTheEnginesOwnSpellFormulaMinPowerTable_ForEveryLevelOneToEight()
        {
            for (uint level = 1; level <= 8; level++)
            {
                Assert.AreEqual(SpellFormula.MinPower[level], SpellswordSpellTables.MinPowerForLevel(level),
                    $"level {level}: SpellswordSpellTables.MinPowerForLevel has drifted from the engine's own SpellFormula.MinPower table");
            }
        }

        // ---- 7. SelectSpell / SelectRungIndex behavior --------------------------------------------

        /// <summary>
        /// A low-skill caster (War Magic 50) at rank 3 (max level 7) still only reaches level 2 - skill
        /// is the binding constraint here, not the rank cap. MinPowerForLevel(2) = 50, which War Magic
        /// 50 just meets; level 3's threshold of 100 is out of reach.
        /// </summary>
        [TestMethod]
        public void SelectSpell_LowSkillCasterAtMaxRank_IsGatedBySkillNotByRank()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 50, rank: 3, thresholdScale: 1.0);

            // index 1 = level 2 (StreakLevels = {1,2,3,4,5,6,7,8}), NOT the rank-3 cap of level 7 (index 6)
            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][1], spellId);
        }

        [TestMethod]
        public void SelectSpell_HighSkillCasterAtRankOne_IsGatedByRankAtExactlyLevelThree()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 400, rank: 1, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][2], spellId); // index 2 = level 3
        }

        [TestMethod]
        public void SelectSpell_HighSkillCasterAtRankTwo_IsGatedByRankAtExactlyLevelFive()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 400, rank: 2, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][4], spellId); // index 4 = level 5
        }

        /// <summary>
        /// Even at War Magic 400+ - enough to meet level 8's own MinPower threshold of 400 - a rank-3
        /// caster still tops out at level 7 (index 6), NOT the level-8 Incantation (index 7). The rank
        /// cap (<see cref="SpellswordSpellTables.MaxLevelForRank"/> = 7 at rank 3) binds regardless of
        /// skill, because SelectRungIndex skips any rung whose level exceeds maxLevel before it ever
        /// checks MinPower. This is the whole point of the cap: the ladder holds an 8th rung, but rank 3
        /// deliberately cannot reach it.
        /// </summary>
        [TestMethod]
        public void SelectSpell_HighSkillCasterAtRankThree_CapsAtLevelSeven_NeverReachesTheLevelEightIncantation()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 400, rank: 3, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][6], spellId); // index 6 = level 7
            Assert.AreNotEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][7], spellId); // NOT level 8

            // Even a skill far past level 8's own MinPower(400) threshold cannot escape the rank cap.
            var overSkilled = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 1000, rank: 3, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][6], overSkilled); // still level 7
        }

        /// <summary>
        /// War magic DOES have a level-7 spell (an earlier revision wrongly believed otherwise - see the
        /// class doc comment above). A rank-3 caster at exactly War Magic 300 - MinPowerForLevel(7) - now
        /// reaches level 7, which is also exactly the rank-3 cap, so this is the single data point where
        /// "gated by skill" and "gated by rank" coincide.
        /// </summary>
        [TestMethod]
        public void SelectSpell_SkillInTheThreeHundredsAtMaxRank_FiresLevelSeven()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 300, rank: 3, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][6], spellId); // index 6 = level 7
        }

        [TestMethod]
        public void SelectSpell_SkillBelowTheLowestRungsThreshold_StillReturnsTheLowestRung_NeverNull()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 0, rank: 1, thresholdScale: 1.0);

            Assert.IsNotNull(spellId, "a Spellsword who owns the ability must always proc something");
            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][0], spellId); // index 0 = level 1
        }

        [TestMethod]
        public void SelectSpell_NonElementalDamageTypes_ReturnNull_SoTheyDoNotSilentlyProc()
        {
            Assert.IsNull(SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Health, magicSkillCurrent: 400, rank: 3, thresholdScale: 1.0));

            Assert.IsNull(SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Nether, magicSkillCurrent: 400, rank: 3, thresholdScale: 1.0));

            Assert.IsNull(SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Base, magicSkillCurrent: 400, rank: 3, thresholdScale: 1.0));
        }

        /// <summary>
        /// class_ability_spellsword_level_skill_scale is the safety valve called out in
        /// SPELLSWORD-DESIGN.md section 1d: setting it below 1.0 makes the thresholds MORE permissive.
        /// The rank-3 cap of 7 means scaling can never reach the level-8 Incantation (SelectRungIndex
        /// skips level 8 outright once it exceeds maxLevel, before MinPower is even consulted), but it
        /// still shifts which rung WITHIN the reachable band is hit: at War Magic 290, scale 1.0 falls
        /// short of level 7's MinPower(300) and lands on level 6, while scale 0.9 drops that threshold to
        /// 300 * 0.9 = 270 (below 290) and reaches level 7.
        /// </summary>
        [TestMethod]
        public void SelectSpell_ThresholdScale_ShiftsTheGateWithinTheRankCap_ButNeverPastIt()
        {
            var unscaled = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 290, rank: 3, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][5], unscaled); // index 5 = level 6

            var scaled = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 290, rank: 3, thresholdScale: 0.9);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][6], scaled); // index 6 = level 7

            // Even an extremely permissive scale cannot cross the rank-3 cap into level 8.
            var extreme = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.StreakByType, SpellswordSpellTables.StreakLevels,
                DamageType.Fire, magicSkillCurrent: 290, rank: 3, thresholdScale: 0.01);

            Assert.AreEqual(SpellswordSpellTables.StreakByType[DamageType.Fire][6], extreme); // still level 7, never level 8
        }

        /// <summary>
        /// Blast at rank 1 (cap level 3) returns level 3 - its own floor - rather than clamping to a
        /// nonexistent level 1 or 2 (BlastLevels has no rung below 3 at all).
        /// </summary>
        [TestMethod]
        public void SelectSpell_Blast_AtRankOne_ReturnsLevelThree_ItsOwnFloor()
        {
            var spellId = SpellswordSpellTables.SelectSpell(SpellswordSpellTables.BlastByType, SpellswordSpellTables.BlastLevels,
                DamageType.Fire, magicSkillCurrent: 400, rank: 1, thresholdScale: 1.0);

            Assert.AreEqual(SpellswordSpellTables.BlastByType[DamageType.Fire][0], spellId); // index 0 = level 3, the floor
        }
    }
}
