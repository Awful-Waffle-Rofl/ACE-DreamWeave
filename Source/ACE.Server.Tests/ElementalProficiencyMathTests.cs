using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Elemental Proficiency math (Archmage T3): per-rank values for all seven elements, the War Magic
    /// affinity cap, gear scaling on Fire/Lightning/Acid/Pierce only, and the guarantee that Frost, Bludgeon and
    /// Slash ignore both. Every key the math reads is seeded to its shipped default in TestInitialize and put
    /// back in TestCleanup, so the class passes run alone and in any order.
    ///
    /// No Player/Pet/GamePiece is constructed (Player's static initializer cannot run in this host), so the
    /// eligibility test covers the null and plain-monster cases; the exclusion list itself is one type test.
    /// </summary>
    [TestClass]
    public class ElementalProficiencyMathTests
    {
        private const double Tol = 1e-9;

        private static readonly string[] DoubleKeys =
        {
            "fire_percent_per_rank", "lightning_percent_per_rank", "lightning_seconds",
            "frost_slow_per_rank", "frost_seconds", "acid_fraction_per_rank", "acid_seconds",
            "pierce_ignore_per_rank", "slashcrit_points_per_rank",
            "bludgeon_stun_base_seconds", "bludgeon_stun_step_seconds", "bludgeon_immunity_seconds",
            "affinity_cap",
        };

        private const string AcidTicksKey = "acid_ticks";

        [TestInitialize]
        public void Seed()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(full), full + " is not registered");
                Assert.IsTrue(PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item), full);
            }

            var tk = ElementalProficiencyMath.KeyPrefix + AcidTicksKey;
            Assert.IsTrue(PropertyManager.ModifyLong(tk, DefaultPropertyManager.DefaultLongProperties[tk].Item), tk);
        }

        [TestCleanup]
        public void Restore()
        {
            foreach (var k in DoubleKeys)
            {
                var full = ElementalProficiencyMath.KeyPrefix + k;
                PropertyManager.ModifyDouble(full, DefaultPropertyManager.DefaultDoubleProperties[full].Item);
            }

            var tk = ElementalProficiencyMath.KeyPrefix + AcidTicksKey;
            PropertyManager.ModifyLong(tk, DefaultPropertyManager.DefaultLongProperties[tk].Item);
        }

        private static void Set(string key, double value)
        {
            Assert.IsTrue(PropertyManager.ModifyDouble(ElementalProficiencyMath.KeyPrefix + key, value));
        }

        [TestMethod]
        public void Defaults_MatchTheDesignTable()
        {
            Assert.AreEqual(0.10, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "fire_percent_per_rank"].Item, Tol);
            Assert.AreEqual(0.25, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "affinity_cap"].Item, Tol);
            Assert.AreEqual(3L, DefaultPropertyManager.DefaultLongProperties[ElementalProficiencyMath.KeyPrefix + AcidTicksKey].Item);
            Assert.AreEqual(6.0, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "acid_seconds"].Item, Tol);
            Assert.AreEqual(10.0, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "lightning_seconds"].Item, Tol);
            Assert.AreEqual(10.0, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "frost_seconds"].Item, Tol);
            Assert.AreEqual(8.0, DefaultPropertyManager.DefaultDoubleProperties[ElementalProficiencyMath.KeyPrefix + "bludgeon_immunity_seconds"].Item, Tol);
        }

        // type, expected value at ranks 1-3 with neutral affinity (1.0) and no gear
        public static IEnumerable<object[]> RankTable()
        {
            yield return new object[] { DamageType.Fire, 0.10, 0.20, 0.30 };
            yield return new object[] { DamageType.Electric, 0.10, 0.20, 0.30 };
            yield return new object[] { DamageType.Cold, 0.20, 0.40, 0.60 };
            yield return new object[] { DamageType.Acid, 0.20, 0.40, 0.60 };
            yield return new object[] { DamageType.Pierce, 0.10, 0.20, 0.30 };
            yield return new object[] { DamageType.Slash, 0.05, 0.10, 0.15 };
        }

        [TestMethod]
        [DynamicData(nameof(RankTable))]
        public void EveryElement_RanksOneToThree_MatchDesignValues(DamageType type, double r1, double r2, double r3)
        {
            Assert.AreEqual(r1, ElementalProficiencyMath.Magnitude(type, 1, 1.0, 0.0), Tol);
            Assert.AreEqual(r2, ElementalProficiencyMath.Magnitude(type, 2, 1.0, 0.0), Tol);
            Assert.AreEqual(r3, ElementalProficiencyMath.Magnitude(type, 3, 1.0, 0.0), Tol);
        }

        [TestMethod]
        [DynamicData(nameof(RankTable))]
        public void RankZero_AndNegative_ReturnNoEffect(DamageType type, double r1, double r2, double r3)
        {
            Assert.AreEqual(0.0, ElementalProficiencyMath.Magnitude(type, 0, 1.25, 0.5), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.Magnitude(type, -1, 1.25, 0.5), Tol);
        }

        [TestMethod]
        public void RankZero_OtherEffects()
        {
            Assert.AreEqual(1.0, ElementalProficiencyMath.FrostSpeedFactor(0), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.StunSeconds(0), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.StunSeconds(-2), Tol);
        }

        [TestMethod]
        public void Bludgeon_StunSeconds_AreTwoThreeFour_AndImmunityIsEight()
        {
            Assert.AreEqual(2.0, ElementalProficiencyMath.StunSeconds(1), Tol);
            Assert.AreEqual(3.0, ElementalProficiencyMath.StunSeconds(2), Tol);
            Assert.AreEqual(4.0, ElementalProficiencyMath.StunSeconds(3), Tol);
            Assert.AreEqual(8.0, ElementalProficiencyMath.StunImmunitySeconds(), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.Magnitude(DamageType.Bludgeon, 3, 1.25, 0.5), Tol, "bludgeon has no fractional magnitude");
        }

        [TestMethod]
        public void Bludgeon_StunSeconds_IgnoreAffinityAndGear_AndFollowTunables()
        {
            Set("bludgeon_stun_base_seconds", 1.5);
            Set("bludgeon_stun_step_seconds", 0.5);
            Assert.AreEqual(1.5, ElementalProficiencyMath.StunSeconds(1), Tol);
            Assert.AreEqual(2.5, ElementalProficiencyMath.StunSeconds(3), Tol);
            // the signature carries no affinity or gear argument at all: nothing can scale it
            Assert.AreEqual(2.0, ElementalProficiencyMath.StunSeconds(3, 1.0, 0.5), Tol);
        }

        [TestMethod]
        public void Frost_SpeedFactor_IsPointEightPointSixPointFour()
        {
            Assert.AreEqual(0.8, ElementalProficiencyMath.FrostSpeedFactor(1), Tol);
            Assert.AreEqual(0.6, ElementalProficiencyMath.FrostSpeedFactor(2), Tol);
            Assert.AreEqual(0.4, ElementalProficiencyMath.FrostSpeedFactor(3), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.FrostSpeedFactor(1.5), Tol, "clamped at 0");
            Assert.AreEqual(1.0, ElementalProficiencyMath.FrostSpeedFactor(-0.5), Tol, "clamped at 1");
        }

        [TestMethod]
        public void Affinity_IsCappedAtOnePlusCap()
        {
            // default cap 0.25: a 1.60 War Magic multiplier counts as 1.25
            Assert.AreEqual(0.10 * 1.25, ElementalProficiencyMath.Magnitude(DamageType.Fire, 1, 1.60, 0.0), Tol);
            Assert.AreEqual(0.30 * 1.25, ElementalProficiencyMath.Magnitude(DamageType.Fire, 3, 5.00, 0.0), Tol);
            // under the cap passes through unclamped
            Assert.AreEqual(0.10 * 1.10, ElementalProficiencyMath.Magnitude(DamageType.Fire, 1, 1.10, 0.0), Tol);
            // the cap is a tunable
            Set("affinity_cap", 0.5);
            Assert.AreEqual(0.20 * 1.5, ElementalProficiencyMath.Magnitude(DamageType.Electric, 2, 9.0, 0.0), Tol);
            Set("affinity_cap", 0.0);
            Assert.AreEqual(0.20, ElementalProficiencyMath.Magnitude(DamageType.Electric, 2, 9.0, 0.0), Tol);
        }

        [TestMethod]
        public void Gear_MultipliesTheFourScaledElements_AfterTheAffinityCap()
        {
            // rank 3, affinity 2.0 (capped to 1.25), gear +20%: value * 1.25 * 1.20
            Assert.AreEqual(0.30 * 1.25 * 1.20, ElementalProficiencyMath.Magnitude(DamageType.Fire, 3, 2.0, 0.20), Tol);
            Assert.AreEqual(0.30 * 1.25 * 1.20, ElementalProficiencyMath.Magnitude(DamageType.Electric, 3, 2.0, 0.20), Tol);
            Assert.AreEqual(0.60 * 1.25 * 1.20, ElementalProficiencyMath.Magnitude(DamageType.Acid, 3, 2.0, 0.20), Tol);
            Assert.AreEqual(0.30 * 1.25 * 1.20, ElementalProficiencyMath.Magnitude(DamageType.Pierce, 3, 2.0, 0.20), Tol);
            // gear alone, neutral affinity
            Assert.AreEqual(0.10 * 1.5, ElementalProficiencyMath.Magnitude(DamageType.Fire, 1, 1.0, 0.5), Tol);
            // negative gear is treated as none
            Assert.AreEqual(0.10, ElementalProficiencyMath.Magnitude(DamageType.Fire, 1, 1.0, -0.5), Tol);
        }

        [TestMethod]
        public void Frost_Slash_AreNeverScaledByAffinityOrGear()
        {
            foreach (var type in new[] { DamageType.Cold, DamageType.Slash })
            {
                Assert.IsFalse(ElementalProficiencyMath.IsAffinityScaled(type), type.ToString());

                for (var rank = 1; rank <= 3; rank++)
                {
                    var plain = ElementalProficiencyMath.Magnitude(type, rank, 1.0, 0.0);
                    Assert.AreEqual(plain, ElementalProficiencyMath.Magnitude(type, rank, 3.0, 0.0), Tol, type + " affinity");
                    Assert.AreEqual(plain, ElementalProficiencyMath.Magnitude(type, rank, 1.0, 0.9), Tol, type + " gear");
                    Assert.AreEqual(plain, ElementalProficiencyMath.Magnitude(type, rank, 3.0, 0.9), Tol, type + " both");
                }
            }

            Assert.AreEqual(0.4, ElementalProficiencyMath.FrostSpeedFactor(ElementalProficiencyMath.Magnitude(DamageType.Cold, 3, 3.0, 0.9)), Tol);
            Assert.IsFalse(ElementalProficiencyMath.IsAffinityScaled(DamageType.Bludgeon));
        }

        [TestMethod]
        public void OnlyFireLightningAcidPierce_AreAffinityScaled()
        {
            var scaled = new List<DamageType>();
            foreach (DamageType t in Enum.GetValues(typeof(DamageType)))
                if (ElementalProficiencyMath.IsAffinityScaled(t))
                    scaled.Add(t);

            CollectionAssert.AreEquivalent(
                new[] { DamageType.Fire, DamageType.Electric, DamageType.Acid, DamageType.Pierce }, scaled);
        }

        [TestMethod]
        public void NonElementalTypes_HaveNoEffect()
        {
            foreach (var t in new[] { DamageType.Nether, DamageType.Health, DamageType.Undef })
            {
                Assert.IsFalse(ElementalProficiencyMath.HasEffect(t), t.ToString());
                Assert.AreEqual(0.0, ElementalProficiencyMath.Magnitude(t, 3, 1.25, 0.5), Tol, t.ToString());
            }

            foreach (var t in new[] { DamageType.Fire, DamageType.Electric, DamageType.Cold, DamageType.Acid, DamageType.Bludgeon, DamageType.Pierce, DamageType.Slash })
                Assert.IsTrue(ElementalProficiencyMath.HasEffect(t), t.ToString());
        }

        [TestMethod]
        public void Acid_DotTotalsTheScaledFraction_OverTheTickCount()
        {
            Assert.AreEqual(3, ElementalProficiencyMath.AcidTicks());
            Assert.AreEqual(6.0, ElementalProficiencyMath.AcidSeconds(), Tol);

            var fraction = ElementalProficiencyMath.Magnitude(DamageType.Acid, 3, 1.0, 0.0); // 0.60
            var perTick = ElementalProficiencyMath.AcidDamagePerTick(100.0, fraction, ElementalProficiencyMath.AcidTicks());
            Assert.AreEqual(60.0, perTick * 3, 1e-9);

            Assert.AreEqual(0.0, ElementalProficiencyMath.AcidDamagePerTick(0.0, 0.6, 3), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.AcidDamagePerTick(100.0, 0.0, 3), Tol);
            Assert.AreEqual(0.0, ElementalProficiencyMath.AcidDamagePerTick(100.0, 0.6, 0), Tol);
        }

        [TestMethod]
        public void Durations_ReadTheirTunables()
        {
            Assert.AreEqual(10.0, ElementalProficiencyMath.LightningSeconds(), Tol);
            Assert.AreEqual(10.0, ElementalProficiencyMath.FrostSeconds(), Tol);
            Set("lightning_seconds", 12.0);
            Assert.AreEqual(12.0, ElementalProficiencyMath.LightningSeconds(), Tol);
        }

        [TestMethod]
        public void Merge_KeepsStrongerValue_AndLaterEndTime()
        {
            var weakLate = new ElementalProficiencyMath.Effect(0.10, 20.0);
            var strongEarly = new ElementalProficiencyMath.Effect(0.30, 12.0);

            var m = ElementalProficiencyMath.Merge(weakLate, strongEarly);
            Assert.AreEqual(0.30, m.Value, Tol);
            Assert.AreEqual(20.0, m.EndTime, Tol);

            var m2 = ElementalProficiencyMath.Merge(strongEarly, weakLate);
            Assert.AreEqual(0.30, m2.Value, Tol);
            Assert.AreEqual(20.0, m2.EndTime, Tol);
        }

        [TestMethod]
        public void IsEligibleTarget_NullIsRefused_PlainMonsterIsAccepted()
        {
            Assert.IsFalse(ElementalProficiencyMath.IsEligibleTarget(null));
            Assert.IsTrue(ElementalProficiencyMath.IsEligibleTarget(TestCreatures.CreateDefender()));
        }
    }
}
