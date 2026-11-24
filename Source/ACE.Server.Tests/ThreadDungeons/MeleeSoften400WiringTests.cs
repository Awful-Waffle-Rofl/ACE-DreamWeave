using System.Text.RegularExpressions;

using ACE.Server.Managers;
using ACE.Server.ThreadDungeons;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.ThreadDungeons
{
    /// <summary>
    /// Pins the wiring of the two 400 dials (owner ruling 2026-11-17): the PropertyManager read, the ReadLimits assignment, and
    /// the three builder call sites whose output no test can tell apart by rate. Code review of PR #2161 swapped the two
    /// readers and dropped the 400 arguments at those sites in a scratch copy and every other test still passed.
    /// </summary>
    [TestClass]
    public class MeleeSoften400WiringTests
    {
        private const string AttackKey = "dynamic_dungeons_weapon_attack_curve_rate_above_400";
        private const string MeleeKey = "dynamic_dungeons_melee_defense_curve_rate_above_400";

        private static string Normalize(string text) => Regex.Replace(text, @"\s+", " ");

        [TestMethod]
        public void The_two_400_dials_are_registered_and_ReadLimits_reads_each_into_its_own_field()
        {
            Assert.AreEqual(0.33, DefaultPropertyManager.DefaultDoubleProperties[AttackKey].Item, 1e-12);
            Assert.AreEqual(0.19, DefaultPropertyManager.DefaultDoubleProperties[MeleeKey].Item, 1e-12);

            // ReadLimits reads every Threads key; seed every registered default so none falls through to the shard DB.
            DefaultPropertyManager.LoadDefaultProperties();

            try
            {
                // Distinct values, so a swap of the two readers cannot pass.
                Assert.IsTrue(PropertyManager.ModifyDouble(AttackKey, 0.6), "ModifyDouble returned false: key not registered");
                Assert.IsTrue(PropertyManager.ModifyDouble(MeleeKey, 0.4), "ModifyDouble returned false: key not registered");
                var limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(0.6, limits.WeaponAttackCurveRateAbove400, 1e-12, "weapon attack key -> weapon attack field");
                Assert.AreEqual(0.4, limits.MeleeDefenseCurveRateAbove400, 1e-12, "melee defense key -> melee defense field");

                Assert.IsTrue(PropertyManager.ModifyDouble(AttackKey, double.NaN));
                Assert.IsTrue(PropertyManager.ModifyDouble(MeleeKey, double.NaN));
                limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(0.33, limits.WeaponAttackCurveRateAbove400, 1e-12, "NaN reads as the weapon attack default");
                Assert.AreEqual(0.19, limits.MeleeDefenseCurveRateAbove400, 1e-12, "NaN reads as the melee defense default");

                Assert.IsTrue(PropertyManager.ModifyDouble(AttackKey, 7.0));
                Assert.IsTrue(PropertyManager.ModifyDouble(MeleeKey, -2.0));
                limits = ThreadDungeonSpawner.ReadLimits(GroupScaling.Solo);
                Assert.AreEqual(1.0, limits.WeaponAttackCurveRateAbove400, 1e-12, "above 1 reads as 1");
                Assert.AreEqual(0.0, limits.MeleeDefenseCurveRateAbove400, 1e-12, "below 0 reads as 0");
            }
            finally
            {
                PropertyManager.ModifyDouble(AttackKey, DungeonPopulationLimits.DefaultWeaponAttackCurveRateAbove400);
                PropertyManager.ModifyDouble(MeleeKey, DungeonPopulationLimits.DefaultMeleeDefenseCurveRateAbove400);
            }

            var body = PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/ThreadDungeonSpawner.cs"),
                "internal static DungeonPopulationLimits ReadLimits(GroupScaling group)");
            StringAssert.Contains(body, "weaponAttackCurveRateAbove400: ReadWeaponAttackCurveRateDial()");
            StringAssert.Contains(body, "meleeDefenseCurveRateAbove400: ReadMeleeDefenseCurveRateDial()");
        }

        /// <summary>
        /// The two builder sites whose rate cannot be observed: the damage-ceiling run standard (StandardAtRun) and the stamp
        /// ratio (StampRatio) feed the damage ceiling, which reads damage and attributes, not weapon attack or melee defense;
        /// and ThreadsStandardCore's curve is the Character Details baseline's. Source-scan pins with the exact call heads, in
        /// the style of MagicAttackCapTests. Whitespace is normalized so a line wrap does not matter.
        /// </summary>
        [TestMethod]
        public void The_builder_passes_both_400_limits_at_every_curve_construction_site()
        {
            var source = Normalize(PooledLootSourceText.Read("Source/ACE.Server/ThreadDungeons/DungeonPopulationBuilder.cs"));

            const string anchored = "new DungeonStatCurve.Anchored(Measured(pivot, 1.0, profileOf), pivot, limits.DefenseCurveRateAbove375, "
                + "limits.MagicDefenseCurveRateAbove300, limits.DamageCurveRateAbovePivot, limits.WeaponAttackCurveRateAbove400, limits.MeleeDefenseCurveRateAbove400)";
            Assert.AreEqual(2, Regex.Matches(source, Regex.Escape(anchored)).Count, "StandardAtRun and ThreadsStandardCore");

            const string stamp = "DungeonStatCurve.Ratio(axis, fromLevel, toLevel, limits.DefenseCurveRateAbove375, limits.MagicDefenseCurveRateAbove300, "
                + "limits.DamageCurveRateAbovePivot, pivot, limits.WeaponAttackCurveRateAbove400, limits.MeleeDefenseCurveRateAbove400)";
            Assert.AreEqual(1, Regex.Matches(source, Regex.Escape(stamp)).Count, "StampRatio");

            const string context = "limits.DamageCurveRateAbovePivot, limits.WeaponAttackCurveRateAbove400, limits.MeleeDefenseCurveRateAbove400, limits.StatCurve, runMeasured,";
            Assert.AreEqual(1, Regex.Matches(source, Regex.Escape(context)).Count, "BuildArchetypeContext");
        }
    }
}
