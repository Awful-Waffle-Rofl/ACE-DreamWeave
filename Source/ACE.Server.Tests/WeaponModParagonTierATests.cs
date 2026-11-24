using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Paragon weapon mods, Tier A: the rating rows and Shield Bypass. Paragon value is added at read time on
    /// top of the native (which already holds any Tourmaline add), so these pin the add itself - the rounding
    /// tables, the ceiling, and the owner's never-+0 floor - and that nothing native is ever written.
    /// </summary>
    [TestClass]
    public class WeaponModParagonTierATests
    {
        private const double Scale = 1.0;
        private const double Tol = 1e-9;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id} is not a live row");
            Assert.AreEqual(WeaponModTier.A, definition.Tier, $"{id} is not Tier A");
            return definition;
        }

        private static WorldObject Gems(WeaponClass weaponClass, WeaponModId id, int count)
        {
            var gems = new WeaponModId[count];

            for (var i = 0; i < count; i++)
                gems[i] = id;

            return WeaponModParagonMathTests.WeaponWithGems(weaponClass, gems);
        }

        private static void AssertTable(WeaponModId id, double[] expected)
        {
            var definition = Row(id);

            for (var gems = 1; gems <= 5; gems++)
            {
                var weapon = Gems(WeaponClass.Melee, id, gems);
                Assert.AreEqual(expected[gems - 1], WeaponModParagon.TierAAdd(weapon, definition, Scale), Tol, $"{id}, {gems} gems");
            }
        }

        // ================= the rounding tables =================

        [TestMethod]
        public void Devastation_NoRecord_Adds_1_2_4_5_6()
        {
            Assert.AreEqual(6.0, Row(WeaponModId.Devastation).MaxRoll, Tol, "fixture: the table assumes MaxRoll 6");
            AssertTable(WeaponModId.Devastation, new[] { 1.0, 2.0, 4.0, 5.0, 6.0 });
        }

        [TestMethod]
        public void Bloodthirst_NoRecord_Adds_1_2_4_5_6()
        {
            Assert.AreEqual(6.0, Row(WeaponModId.Bloodthirst).MaxRoll, Tol, "fixture: the table assumes MaxRoll 6");
            AssertTable(WeaponModId.Bloodthirst, new[] { 1.0, 2.0, 4.0, 5.0, 6.0 });
        }

        [TestMethod]
        public void WeakPoint_NoRecord_Adds_1_1_2_2_3()
        {
            Assert.AreEqual(3.0, Row(WeaponModId.WeakPoint).MaxRoll, Tol, "fixture: the table assumes MaxRoll 3");
            AssertTable(WeaponModId.WeakPoint, new[] { 1.0, 1.0, 2.0, 2.0, 3.0 });
        }

        [TestMethod]
        public void ShieldBypass_NoRecord_AddsTwentyPercentOfTheCeilingPerGem()
        {
            var definition = Row(WeaponModId.ShieldBypass);
            AssertTable(WeaponModId.ShieldBypass, new[] { 0.1, 0.2, 0.3, 0.4, 0.5 });
            Assert.AreEqual(0.50, definition.MaxRoll, Tol);
        }

        // ================= additive with a Tourmaline record =================

        [TestMethod]
        public void Devastation_PartialRecord_AddsUpToTheCeiling()
        {
            var definition = Row(WeaponModId.Devastation);

            // r = 3 (fraction 0.5): +0.2 -> 4.2 -> 4, +0.4 -> 5.4 -> 5, +0.6 -> clamped 6
            Assert.AreEqual(1.0, WeaponModParagon.TierAAdd(definition, 3.0, 0.2, Scale), Tol);
            Assert.AreEqual(2.0, WeaponModParagon.TierAAdd(definition, 3.0, 0.4, Scale), Tol);
            Assert.AreEqual(3.0, WeaponModParagon.TierAAdd(definition, 3.0, 0.6, Scale), Tol);
            Assert.AreEqual(3.0, WeaponModParagon.TierAAdd(definition, 3.0, 1.0, Scale), Tol, "never past the ceiling");
        }

        [TestMethod]
        public void RecordAtCeiling_AddsZero()
        {
            foreach (var id in new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst, WeaponModId.ShieldBypass })
            {
                var definition = Row(id);
                var weapon = Gems(WeaponClass.Melee, id, 5);
                WeaponModTinkerSet.ApplySpecial(weapon, definition, definition.MaxRoll);

                Assert.AreEqual(definition.MaxRoll, weapon.GetProperty(definition.Record).Value, Tol, $"fixture: {id} record at the ceiling");
                Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(weapon, definition, Scale), Tol, $"{id}, 5 gems on a perfect roll");
            }
        }

        [TestMethod]
        public void ShieldBypass_RecordPlusGem_ClampsAtTheCeiling()
        {
            var definition = Row(WeaponModId.ShieldBypass);

            Assert.AreEqual(0.10, WeaponModParagon.TierAAdd(definition, 0.40, 0.2, Scale), Tol, "0.8 + 0.2");
            Assert.AreEqual(0.05, WeaponModParagon.TierAAdd(definition, 0.45, 0.2, Scale), Tol, "0.9 + 0.2 clamps");
        }

        /// <summary>
        /// A record ABOVE the live ceiling (a weapon rolled before a MaxRoll retune down, say: Devastation 8 against
        /// today's 6) adds nothing: the combined value is the ceiling, which is below the record, and the floor only
        /// applies below the ceiling.
        /// </summary>
        [TestMethod]
        public void RecordAboveCeiling_AddsZero()
        {
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(Row(WeaponModId.Devastation), 8.0, 0.2, Scale), Tol);
        }

        // ================= the shield-ignore weapon-term clamp =================

        /// <summary>
        /// GetIgnoreShieldMod's weapon term, composed from the same three pure calls the production site makes:
        /// a weapon with an intrinsic IgnoreShield of 0.9 plus five Shield Bypass gems (+0.5) would sum to 1.4, and
        /// "1 - max(creature, weapon)" would go negative. The clamp caps the term at 1.0, so the mod is exactly 0.
        /// </summary>
        [TestMethod]
        public void IgnoreShield_IntrinsicPlusFiveGems_ModNeverNegative()
        {
            var weapon = Gems(WeaponClass.Melee, WeaponModId.ShieldBypass, 5);
            weapon.SetProperty(PropertyFloat.IgnoreShield, 0.9);

            var term = WeaponModSuppression.WeaponIgnoreShield(weapon, false);
            term += WeaponModParagon.IgnoreShieldAdd(weapon, true, false, Scale);

            Assert.AreEqual(1.4, term, 1e-6, "control: unclamped the term passes 1.0");

            term = WeaponModParagon.ClampWeaponIgnoreShield(term);
            var mod = 1.0f - (float)System.Math.Max(0.0f, term);

            Assert.AreEqual(1.0, term, Tol);
            Assert.IsTrue(mod >= 0.0f, $"mod {mod} went negative");
        }

        [TestMethod]
        public void ClampWeaponIgnoreShield_CapsAtOneOnly()
        {
            Assert.AreEqual(0.4, WeaponModParagon.ClampWeaponIgnoreShield(0.4), Tol, "below 1 unchanged");
            Assert.AreEqual(1.0, WeaponModParagon.ClampWeaponIgnoreShield(1.0), Tol);
            Assert.AreEqual(1.0, WeaponModParagon.ClampWeaponIgnoreShield(1.3), Tol, "a Tourmaline-only overflow is capped too");
            Assert.IsTrue(double.IsNaN(WeaponModParagon.ClampWeaponIgnoreShield(double.NaN)), "NaN passes through");
        }

        // ================= non-weapon carriers are inert =================

        [TestMethod]
        public void NonWeaponItem_WithSlots_IsInertOnEveryPath()
        {
            var armor = WeaponModTestKit.MakeWeapon(ItemType.Armor, null);
            armor.SetProperty(PropertyInt.ParagonModSlot1, (int)WeaponModId.Devastation);
            armor.SetProperty(PropertyInt.ParagonModSlot2, (int)WeaponModId.Ambush);

            Assert.IsFalse(WeaponModParagon.HasParagonMods(armor));
            Assert.AreEqual(0, WeaponModParagon.GemCount(armor, WeaponModId.Devastation));
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(armor, Row(WeaponModId.Devastation), Scale), Tol, "Tier A");
            Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(armor, WeaponModId.Ambush, true, Scale), Tol, "Tier B");

            foreach (var type in new[] { ItemType.MeleeWeapon, ItemType.MissileWeapon, ItemType.Caster })
                Assert.IsTrue(WeaponModParagon.IsCarrier(WeaponModTestKit.MakeWeapon(type, null)), type.ToString());
        }

        // ================= the owner floor =================

        /// <summary>
        /// Weak Point at scale 0.5 has a ceiling of 2 (1.5 rounded away from zero). A record of 1 is fraction 0.5,
        /// and 0.5 + 0.2 resolves to 3 x 0.7 x 0.5 = 1.05, which rounds to 1 - the record itself, so the raw
        /// rounding would give the gem +0. The floor must raise it to +1.
        /// </summary>
        [TestMethod]
        public void IntegerRow_GemNeverAddsZero_BelowTheCeiling()
        {
            var definition = Row(WeaponModId.WeakPoint);
            const double halfScale = 0.5;

            Assert.AreEqual(2.0, WeaponValueCeiling(definition, halfScale), Tol, "fixture: ceiling 2");
            Assert.AreEqual(1.0, WeaponModValue.MagnitudeFromFraction(definition, 0.7, halfScale), Tol,
                "control: without the floor the combined value rounds back onto the record");

            Assert.AreEqual(1.0, WeaponModParagon.TierAAdd(definition, 1.0, 0.2, halfScale), Tol, "the floor lifts +0 to +1");
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(definition, 2.0, 0.2, halfScale), Tol, "at the ceiling the floor does not apply");
        }

        [TestMethod]
        public void EveryIntegerRow_OneGem_NeverAddsZero_ForAnyRecordBelowTheCeiling()
        {
            foreach (var definition in WeaponModRegistry.AllMods)
            {
                if (definition.Tier != WeaponModTier.A || !definition.IsInteger)
                    continue;

                foreach (var scale in new[] { 0.25, 0.5, 1.0, 1.5, 2.0 })
                {
                    var ceiling = WeaponValueCeiling(definition, scale);

                    for (var r = 0; r < ceiling; r++)
                        Assert.IsTrue(WeaponModParagon.TierAAdd(definition, r, 0.2, scale) >= 1.0, $"{definition.Id} scale {scale} record {r}");
                }
            }
        }

        private static double WeaponValueCeiling(WeaponModDefinition definition, double scale) => WeaponModValue.MaxMagnitude(definition, scale);

        // ================= guards =================

        [TestMethod]
        public void NoGems_MutedScale_TierBRow_AllAddZero()
        {
            var devastation = Row(WeaponModId.Devastation);

            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(WeaponModTestKit.MakeUntinkered(WeaponClass.Melee), devastation, Scale), Tol, "no gems");
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(Gems(WeaponClass.Melee, WeaponModId.Devastation, 3), devastation, 0.0), Tol, "muted scale");
            Assert.IsTrue(WeaponModRegistry.TryGet(WeaponModId.Ambush, out var ambush));
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(ambush, null, 0.2, Scale), Tol, "a Tier B row has no Tier A add");
            Assert.AreEqual(0.0, WeaponModParagon.TierAAdd(Gems(WeaponClass.Melee, WeaponModId.WeakPoint, 3), devastation, Scale), Tol, "another row's gems");
        }

        [TestMethod]
        public void TierAAdd_WritesNoNative()
        {
            var devastation = Row(WeaponModId.Devastation);
            var weapon = Gems(WeaponClass.Melee, WeaponModId.Devastation, 5);

            WeaponModParagon.TierAAdd(weapon, devastation, Scale);
            WeaponModParagon.TierARatingAdd(new[] { weapon }, PropertyInt.GearCritDamage, true, Scale);

            Assert.IsNull(weapon.GetProperty(PropertyInt.GearCritDamage), "the native must never be written");
            Assert.IsNull(weapon.GetProperty(devastation.Record), "no Tourmaline record may be written");
        }

        // ================= the rating sum over equipped items =================

        [TestMethod]
        public void TierARatingAdd_SumsGemmedItems_ForTheRightNativeOnly()
        {
            var a = Gems(WeaponClass.Melee, WeaponModId.Devastation, 1);
            var b = Gems(WeaponClass.Missile, WeaponModId.Devastation, 3);
            var plain = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            var crit = Gems(WeaponClass.Melee, WeaponModId.WeakPoint, 5);
            var items = new List<WorldObject> { a, b, plain, crit, null };

            Assert.AreEqual(1 + 4, WeaponModParagon.TierARatingAdd(items, PropertyInt.GearCritDamage, true, Scale), "Devastation: 1 gem + 3 gems");
            Assert.AreEqual(3, WeaponModParagon.TierARatingAdd(items, PropertyInt.GearCrit, true, Scale), "Weak Point: 5 gems");
            Assert.AreEqual(0, WeaponModParagon.TierARatingAdd(items, PropertyInt.GearDamage, true, Scale), "no Bloodthirst gems");
            Assert.AreEqual(0, WeaponModParagon.TierARatingAdd(items, PropertyInt.GearDamageResist, true, Scale), "no Tier A row writes this native");
            Assert.AreEqual(0, WeaponModParagon.TierARatingAdd(items, PropertyInt.GearCritDamage, false, Scale), "gate off");
            Assert.AreEqual(0, WeaponModParagon.TierARatingAdd(null, PropertyInt.GearCritDamage, true, Scale), "no items");
        }

        [TestMethod]
        public void IgnoreShieldAdd_GateAndSuppression()
        {
            var weapon = Gems(WeaponClass.Melee, WeaponModId.ShieldBypass, 2);

            Assert.AreEqual(0.2, WeaponModParagon.IgnoreShieldAdd(weapon, true, false, Scale), Tol, "live");
            Assert.AreEqual(0.0, WeaponModParagon.IgnoreShieldAdd(weapon, false, false, Scale), Tol, "gate off");
            Assert.AreEqual(0.0, WeaponModParagon.IgnoreShieldAdd(weapon, true, true, Scale), Tol, "suppressed attacker");
            Assert.AreEqual(0.0, WeaponModParagon.IgnoreShieldAdd(null, true, false, Scale), Tol, "no weapon");
            Assert.AreEqual(0.0, WeaponModParagon.IgnoreShieldAdd(Gems(WeaponClass.Melee, WeaponModId.Devastation, 2), true, false, Scale), Tol, "another row's gems");
        }
    }
}
