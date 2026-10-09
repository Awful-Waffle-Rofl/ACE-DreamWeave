using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.RefireStations;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The Arcane Alignment Table and Defense Requirement Reforge's pure eligibility rules, and the
    /// RefireStationCommon helpers all three refire stations share: the signed delta draw
    /// (RollDelta/DeltaPool), the clamp-to-ceiling apply (ClampDelta), and the pack/bank cost split
    /// (SplitCost). WorkmanshipReforgeStation's own pure rules stay in WorkmanshipRefireTests.cs.
    ///
    /// A Player/WorldObject cannot be constructed in a test in this repo (see SpellRerollTests' remarks), so
    /// DefenseReforgeStation's slot finder is exercised only via its pure (reqType, skillType) tuple overload,
    /// never against a real WorldObject.
    /// </summary>
    [TestClass]
    public class RefireStationsTests
    {
        // ---------------- RefireStationCommon.RollDelta / DeltaPool ----------------

        private const int Rolls = 20_000;

        [TestMethod]
        public void RollDelta_NeverZero_StaysInRange()
        {
            for (var i = 0; i < Rolls; i++)
            {
                var delta = RefireStationCommon.RollDelta();

                Assert.AreNotEqual(0, delta, "RollDelta returned zero - the owner decision was zero excluded");
                Assert.IsTrue(delta >= -10 && delta <= 5, $"RollDelta returned {delta}, outside [-10, +5]");
            }
        }

        [TestMethod]
        public void DeltaPool_ContainsNoZero()
        {
            CollectionAssert.DoesNotContain(RefireStationCommon.DeltaPool, 0);
        }

        [TestMethod]
        public void DeltaPool_SpansExpectedRange()
        {
            Assert.AreEqual(15, RefireStationCommon.DeltaPool.Length, "expected 10 negative (-10..-1) + 5 positive (+1..+5) values");

            var min = int.MaxValue;
            var max = int.MinValue;

            foreach (var delta in RefireStationCommon.DeltaPool)
            {
                if (delta < min)
                    min = delta;
                if (delta > max)
                    max = delta;
            }

            Assert.AreEqual(-10, min);
            Assert.AreEqual(5, max);
        }

        // ---------------- RefireStationCommon.ClampDelta ----------------

        [TestMethod]
        public void ClampDelta_AtCeiling_PositiveDelta_ClampsToCeiling()
        {
            // cur 20, ceiling 20, delta +5 -> 20 (already at the ceiling; charge happens, value does not move)
            Assert.AreEqual(20, RefireStationCommon.ClampDelta(20, 5, 20));
        }

        [TestMethod]
        public void ClampDelta_NearFloor_NegativeDelta_ClampsToZero()
        {
            // cur 3, delta -10 -> 0 (floor, never negative)
            Assert.AreEqual(0, RefireStationCommon.ClampDelta(3, -10, 20));
        }

        [TestMethod]
        public void ClampDelta_BelowCeiling_PositiveDelta_ClampsToCeilingNotAboveIt()
        {
            // cur 10, ceiling 15, delta +5 -> 15 (would be 15 exactly, at the ceiling - not above it)
            Assert.AreEqual(15, RefireStationCommon.ClampDelta(10, 5, 15));
        }

        [TestMethod]
        public void ClampDelta_WithinRange_AppliesDeltaExactly()
        {
            Assert.AreEqual(7, RefireStationCommon.ClampDelta(10, -3, 20));
            Assert.AreEqual(12, RefireStationCommon.ClampDelta(10, 2, 20));
        }

        // ---------------- RefireStationCommon.SplitCost (5-note stations) ----------------

        [TestMethod]
        public void SplitCost_FiveNoteCost_EmptyPack_AllFromBank()
        {
            var (fromPack, fromBank) = RefireStationCommon.SplitCost(0, 5);

            Assert.AreEqual(0, fromPack);
            Assert.AreEqual(5, fromBank);
        }

        [TestMethod]
        public void SplitCost_FiveNoteCost_PartialPack_RemainderFromBank()
        {
            var (fromPack, fromBank) = RefireStationCommon.SplitCost(2, 5);

            Assert.AreEqual(2, fromPack);
            Assert.AreEqual(3, fromBank);
        }

        [TestMethod]
        public void SplitCost_FiveNoteCost_FullPack_NothingFromBank()
        {
            var (fromPack, fromBank) = RefireStationCommon.SplitCost(5, 5);

            Assert.AreEqual(5, fromPack);
            Assert.AreEqual(0, fromBank);
        }

        [TestMethod]
        public void SplitCost_FiveNoteCost_MoreThanCostInPack_ClampedToTotalCost()
        {
            var (fromPack, fromBank) = RefireStationCommon.SplitCost(9, 5);

            Assert.AreEqual(5, fromPack);
            Assert.AreEqual(0, fromBank);
        }

        // ---------------- ArcaneAlignmentStation.IsEligibleItem ----------------

        [TestMethod]
        public void ArcaneAlignment_IsEligibleItem_FalseForNullDifficulty()
        {
            Assert.IsFalse(ArcaneAlignmentStation.IsEligibleItem(null));
        }

        [TestMethod]
        public void ArcaneAlignment_IsEligibleItem_FalseForZeroOrNegativeDifficulty()
        {
            Assert.IsFalse(ArcaneAlignmentStation.IsEligibleItem(0));
            Assert.IsFalse(ArcaneAlignmentStation.IsEligibleItem(-1));
        }

        [TestMethod]
        public void ArcaneAlignment_IsEligibleItem_TrueForPositiveDifficulty()
        {
            Assert.IsTrue(ArcaneAlignmentStation.IsEligibleItem(1));
            Assert.IsTrue(ArcaneAlignmentStation.IsEligibleItem(320));
        }

        // ---------------- RefireStationCommon.IsStacked (shared by all three stations' VerifyEligible) ----------------

        [TestMethod]
        public void IsStacked_TrueForStackSizeAboveOne()
        {
            // review 2026-08-18: defense-in-depth against any station operating on a stack of more than one
            Assert.IsTrue(RefireStationCommon.IsStacked(2));
            Assert.IsTrue(RefireStationCommon.IsStacked(100));
        }

        [TestMethod]
        public void IsStacked_FalseForOneOrNull()
        {
            Assert.IsFalse(RefireStationCommon.IsStacked(1));
            Assert.IsFalse(RefireStationCommon.IsStacked(null));
        }

        [TestMethod]
        public void IsStacked_FalseForZeroOrNegative()
        {
            // not a realistic StackSize, but the predicate must not treat it as "stacked"
            Assert.IsFalse(RefireStationCommon.IsStacked(0));
            Assert.IsFalse(RefireStationCommon.IsStacked(-1));
        }

        // ---------------- DefenseReforgeStation.FindDefenseSlot (pure) ----------------

        [TestMethod]
        public void DefenseSlot_FindsFirstMatchingSlot_MeleeDefense()
        {
            var slots = new List<(WieldRequirement, int?)>
            {
                (WieldRequirement.Skill, (int)Skill.MeleeDefense),
                (WieldRequirement.Invalid, null),
                (WieldRequirement.Invalid, null),
                (WieldRequirement.Invalid, null),
            };

            Assert.AreEqual(0, DefenseReforgeStation.FindDefenseSlot(slots));
        }

        [TestMethod]
        public void DefenseSlot_FindsFirstMatchingSlot_SkipsNonDefenseSlotsBeforeIt()
        {
            var slots = new List<(WieldRequirement, int?)>
            {
                (WieldRequirement.Level, null),
                (WieldRequirement.Attrib, 1), // an Attrib requirement type - its int is an attribute id, not a Skill, and must not match
                (WieldRequirement.RawSkill, (int)Skill.MissileDefense),
                (WieldRequirement.Invalid, null),
            };

            Assert.AreEqual(2, DefenseReforgeStation.FindDefenseSlot(slots));
        }

        [TestMethod]
        public void DefenseSlot_MatchesMagicDefense()
        {
            var slots = new List<(WieldRequirement, int?)>
            {
                (WieldRequirement.Skill, (int)Skill.MagicDefense),
            };

            Assert.AreEqual(0, DefenseReforgeStation.FindDefenseSlot(slots));
        }

        [TestMethod]
        public void DefenseSlot_ReturnsNull_ForStrengthOrLevelOnlyItem()
        {
            var slots = new List<(WieldRequirement, int?)>
            {
                (WieldRequirement.Level, null),
                (WieldRequirement.Attrib, 1),
                (WieldRequirement.Invalid, null),
                (WieldRequirement.Invalid, null),
            };

            Assert.IsNull(DefenseReforgeStation.FindDefenseSlot(slots));
        }

        [TestMethod]
        public void DefenseSlot_ReturnsNull_ForSkillRequirementThatIsNotDefense()
        {
            // a Skill-type requirement that names a non-Defense skill (e.g. War Magic) must not match
            var slots = new List<(WieldRequirement, int?)>
            {
                (WieldRequirement.Skill, (int)Skill.WarMagic),
            };

            Assert.IsNull(DefenseReforgeStation.FindDefenseSlot(slots));
        }

        [TestMethod]
        public void DefenseSlot_ReturnsNull_ForEmptySlotList()
        {
            Assert.IsNull(DefenseReforgeStation.FindDefenseSlot(new List<(WieldRequirement, int?)>()));
        }

        [TestMethod]
        public void DefenseSlot_IsDefenseSkill_TrueForAllThree()
        {
            Assert.IsTrue(DefenseReforgeStation.IsDefenseSkill(Skill.MeleeDefense));
            Assert.IsTrue(DefenseReforgeStation.IsDefenseSkill(Skill.MissileDefense));
            Assert.IsTrue(DefenseReforgeStation.IsDefenseSkill(Skill.MagicDefense));
        }

        [TestMethod]
        public void DefenseSlot_IsDefenseSkill_FalseForOthers()
        {
            Assert.IsFalse(DefenseReforgeStation.IsDefenseSkill(Skill.WarMagic));
            Assert.IsFalse(DefenseReforgeStation.IsDefenseSkill(Skill.Axe));
        }
    }
}
