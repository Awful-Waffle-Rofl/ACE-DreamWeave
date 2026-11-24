using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// <see cref="DungeonStatCurve.Carry"/> and <see cref="DungeonArchetypeStandards.Carry"/>: Extrapolate generalised so a standard
    /// can be carried from any level to any other with the damage curve splitting at an explicit pivot. The no-behaviour-change
    /// proof for Extrapolate itself is FamilyStretchGoldenTests (literals captured from origin/master).
    /// </summary>
    [TestClass]
    public class StatCurveCarryTests
    {
        private const int Pivot = 326;

        [TestInitialize]
        public void Reset() => TestGameTables.EnsureInitialized();

        private static DungeonBandStandard At(int level)
            => DungeonPopulationBuilder.ThreadsStandardAt(level, FamilyStretchFixture.Limits(), ShippedRosterFixture.Tables, ShippedRosterFixture.LevelOf, FamilyStretchFixture.LevelProfile);

        private static DungeonBandStandard Carry(DungeonBandStandard s, int from, int to, int pivot)
            => DungeonStatCurve.Carry(s, from, to, pivot, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);

        private static DungeonBandStandard Extrapolate(DungeonBandStandard s, int pivot, int level)
            => DungeonStatCurve.Extrapolate(s, pivot, level, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);

        [TestMethod]
        public void Carry_from_the_pivot_equals_extrapolate()
        {
            var anchor = At(Pivot);

            Assert.AreEqual(FamilyStretchFixture.StandardText(Extrapolate(anchor, Pivot, 400)), FamilyStretchFixture.StandardText(Carry(anchor, Pivot, 400, Pivot)));
            Assert.AreEqual(FamilyStretchFixture.StandardText(Extrapolate(anchor, Pivot, 480)), FamilyStretchFixture.StandardText(Carry(anchor, Pivot, 480, Pivot)));
        }

        [TestMethod]
        public void Carry_composes()
        {
            var anchor = At(Pivot);
            var direct = Carry(anchor, Pivot, 460, Pivot);
            var hopped = Carry(Carry(anchor, Pivot, 400, Pivot), 400, 460, Pivot);

            Assert.IsTrue(System.Math.Abs((long)direct.MaxBodyDamage - hopped.MaxBodyDamage) <= 1, $"{direct.MaxBodyDamage} vs {hopped.MaxBodyDamage}");
            Assert.IsTrue(System.Math.Abs((long)direct.MaxBaseArmor - hopped.MaxBaseArmor) <= 1, $"{direct.MaxBaseArmor} vs {hopped.MaxBaseArmor}");
            Assert.AreEqual(direct.AttributeMedians.Count, hopped.AttributeMedians.Count);

            foreach (var kvp in direct.AttributeMedians)
                Assert.IsTrue(System.Math.Abs((long)kvp.Value - hopped.AttributeMedians[kvp.Key]) <= 1, $"attribute {kvp.Key}: {kvp.Value} vs {hopped.AttributeMedians[kvp.Key]}");
        }

        [TestMethod]
        public void Carry_below_the_pivot_raises_damage_and_identity_is_a_no_op()
        {
            var s200 = At(200);

            Assert.IsTrue(Carry(s200, 200, 230, Pivot).MaxBodyDamage > s200.MaxBodyDamage);
            Assert.AreSame(s200, Carry(s200, 200, 200, Pivot));
            Assert.AreSame(DungeonBandStandard.Empty, Carry(null, 200, 230, Pivot));
        }

        [TestMethod]
        public void Carry_splits_damage_at_the_pivot_not_at_from()
        {
            var s300 = At(300);

            Assert.AreNotEqual(Carry(s300, 300, 345, Pivot).MaxBodyDamage, Carry(s300, 300, 345, 300).MaxBodyDamage);
        }

        [TestMethod]
        public void Archetype_carry_from_the_pivot_equals_extrapolate()
        {
            var axes = new ArchetypeAxes(At(Pivot), 100.0, 50.0, 10, 5, 0, 0, 0, 0, 1.0);
            var ex = DungeonArchetypeStandards.Extrapolate(axes, Pivot, 400, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);
            var ca = DungeonArchetypeStandards.Carry(axes, Pivot, 400, Pivot, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);

            Assert.AreEqual(FamilyStretchFixture.StandardText(ex.Core), FamilyStretchFixture.StandardText(ca.Core));
            Assert.AreEqual(ex.EffectiveDamage, ca.EffectiveDamage);
        }

        [TestMethod]
        public void Archetype_carry_splits_damage_at_the_pivot_not_at_from()
        {
            var axes = new ArchetypeAxes(At(300), 100.0, 50.0, 10, 5, 0, 0, 0, 0, 1.0);

            ArchetypeAxes CarryTo(int pivot) => DungeonArchetypeStandards.Carry(axes, 300, 345, pivot, DungeonPopulationLimits.DefaultDefenseCurveRateAbove375,
                DungeonPopulationLimits.DefaultMagicDefenseCurveRateAbove300, DungeonPopulationLimits.DefaultDamageCurveRateAbovePivot,
                DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);

            Assert.AreNotEqual(CarryTo(Pivot).EffectiveDamage, CarryTo(300).EffectiveDamage);
        }
    }
}
