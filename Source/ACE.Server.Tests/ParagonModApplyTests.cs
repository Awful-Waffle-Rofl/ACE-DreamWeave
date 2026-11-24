using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The gem-apply mutation (ParagonModManager.ApplyGemTo), run on in-memory weapons with no Player and no
    /// PropertyManager read.
    /// </summary>
    [TestClass]
    public class ParagonModApplyTests
    {
        internal const long BaseXp = 2000000000L;

        /// <summary>
        /// A Fixed-style Paragon weapon at <paramref name="maxLevel"/>, fully leveled, holding
        /// <paramref name="gems"/> in order. Set 140 (ParagonMelee).
        /// </summary>
        internal static WorldObject ParagonWeapon(WeaponClass weaponClass, int maxLevel, params WeaponModId[] gems)
        {
            var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);

            weapon.SetProperty(PropertyInt.EquipmentSetId, (int)EquipmentSet.ParagonMelee);
            weapon.SetProperty(PropertyInt64.ItemBaseXp, BaseXp);
            weapon.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            weapon.SetProperty(PropertyInt.ItemMaxLevel, maxLevel);
            weapon.SetProperty(PropertyInt64.ItemTotalXp, maxLevel * BaseXp);

            for (var i = 0; i < gems.Length; i++)
                weapon.SetProperty(ParagonModRegistry.Slots[i], (int)gems[i]);

            Assert.IsTrue(weapon.HasItemLevel, "the fixture must read as a leveled item");

            return weapon;
        }

        [TestMethod]
        public void Fixture_ReadsFullyLeveled()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);

            Assert.AreEqual(50, weapon.ItemLevel);
            Assert.AreEqual(50, weapon.ItemMaxLevel);
        }

        [TestMethod]
        public void FirstGem_WritesSlot1_AndLevelsTheWeaponToFiftyOne()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);

            Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 3));

            Assert.AreEqual((int)WeaponModId.Devastation, weapon.GetProperty(PropertyInt.ParagonModSlot1));
            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModSlot2));
            Assert.AreEqual(3, weapon.GetProperty(PropertyInt.ParagonModTokensSpent));
            Assert.AreEqual(51, weapon.ItemMaxLevel);
        }

        [TestMethod]
        public void FixedStyleWeapon_At51_HoldsExactly51TimesTheBaseXp_AndReadsFullyLeveled()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);

            Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1));

            Assert.AreEqual(51L * 2000000000L, weapon.ItemTotalXp);
            Assert.AreEqual(51, weapon.ItemLevel);
            Assert.AreEqual(weapon.ItemMaxLevel, weapon.ItemLevel, "newMax/newMax: the mod is live at once");
        }

        [TestMethod]
        public void Gems_FillSlotsInOrder_AndTokensAccumulate()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);
            var order = new[] { WeaponModId.Devastation, WeaponModId.Ambush, WeaponModId.Heft, WeaponModId.Leverage, WeaponModId.ShieldBypass };

            for (var i = 0; i < order.Length; i++)
                Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)order[i], 10), $"gem {i + 1}");

            for (var i = 0; i < order.Length; i++)
                Assert.AreEqual((int)order[i], weapon.GetProperty(ParagonModRegistry.Slots[i]), $"slot {i + 1}");

            Assert.AreEqual(50, weapon.GetProperty(PropertyInt.ParagonModTokensSpent));
            Assert.AreEqual(55, weapon.ItemMaxLevel);
            Assert.AreEqual(55L * 2000000000L, weapon.ItemTotalXp);
        }

        [TestMethod]
        public void SixthGem_AtMax55_WritesNothing_AndTheRuleRefusesIt()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 55, WeaponModId.Devastation, WeaponModId.Ambush, WeaponModId.Heft, WeaponModId.Leverage, WeaponModId.ShieldBypass);

            Assert.IsFalse(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1));
            Assert.AreEqual(55, weapon.ItemMaxLevel);
            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModTokensSpent));

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.AtMaxGems,
                ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Melee, 140, weapon.ItemMaxLevel, weapon.ItemLevel,
                    ParagonModManager.ReadFilledSlots(weapon), (int)WeaponModId.Devastation, ItemXpStyle.Fixed, true));
        }

        [TestMethod]
        public void TheSameModTwice_IsAllowed_AndCountsTwoGems()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);

            Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1));

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.None,
                ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Melee, 140, weapon.ItemMaxLevel, weapon.ItemLevel,
                    ParagonModManager.ReadFilledSlots(weapon), (int)WeaponModId.Devastation, ItemXpStyle.Fixed, true));

            Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1));

            Assert.AreEqual(2, WeaponModParagon.GemCount(weapon, WeaponModId.Devastation));
            Assert.AreEqual(0.40, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Devastation), 1e-9);
        }

        [TestMethod]
        public void MalformedSlots_RefuseTheApply_AndWriteNothing()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 51, WeaponModId.Devastation);
            weapon.SetProperty(PropertyInt.ParagonModSlot3, (int)WeaponModId.Ambush);

            Assert.AreEqual(-1, ParagonModManager.ReadFilledSlots(weapon));
            Assert.IsFalse(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Heft, 1));
            Assert.AreEqual(51, weapon.ItemMaxLevel);
        }

        [TestMethod]
        public void Apply_WritesNoNativeAndNoTourmalineRecord()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);

            // a Tourmaline-style record and a native, both present before the apply
            var devastation = WeaponModRegistry.Get(WeaponModId.Devastation);
            var heft = WeaponModRegistry.Get(WeaponModId.Heft);

            weapon.SetProperty(heft.Record, 0.25);
            weapon.SetProperty(PropertyFloat.IgnoreShield, 0.1);

            ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1);
            ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Heft, 1);

            Assert.AreEqual(0.25, weapon.GetProperty(heft.Record));
            Assert.AreEqual(0.1, weapon.GetProperty(PropertyFloat.IgnoreShield));
            Assert.IsNull(weapon.GetProperty(devastation.Record), "no record is created for a Paragon-only modifier");
        }

        [TestMethod]
        public void ScalesWithLevelWeapon_IsRefused_AndNothingIsWritten()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);
            weapon.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.ScalesWithLevel);

            Assert.IsFalse(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1));

            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModSlot1));
            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModTokensSpent));
            Assert.AreEqual(50, weapon.ItemMaxLevel);
            Assert.AreEqual(50L * BaseXp, weapon.ItemTotalXp);
        }

        [TestMethod]
        public void FixedPlusBaseWeapon_RoundTripsEveryLevelFrom51To55()
        {
            var weapon = ParagonWeapon(WeaponClass.Melee, 50);
            weapon.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.FixedPlusBase);
            weapon.SetProperty(PropertyInt64.ItemTotalXp, (long)ACE.Server.Entity.ExperienceSystem.ItemLevelToTotalXP(50, (ulong)BaseXp, 50, ItemXpStyle.FixedPlusBase));

            Assert.AreEqual(50, weapon.ItemLevel);

            for (var level = 51; level <= 55; level++)
            {
                Assert.IsTrue(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1), $"gem to {level}");
                Assert.AreEqual(level, weapon.ItemMaxLevel);
                Assert.AreEqual(level, weapon.ItemLevel, $"reads {level}/{level}");
            }
        }

        [TestMethod]
        public void PlanRefund_StacksAndBurden()
        {
            Assert.AreEqual((0, 0), ParagonModManager.PlanRefund(0, 100, 5));
            Assert.AreEqual((1, 20), ParagonModManager.PlanRefund(4, 100, 5));
            Assert.AreEqual((3, 0), ParagonModManager.PlanRefund(250, 100, 0));
            Assert.AreEqual((4, 4), ParagonModManager.PlanRefund(4, 0, 1), "no MaxStackSize reads 1: one slot per token");
        }

        [TestMethod]
        public void RefundFor_Math()
        {
            Assert.AreEqual(4, ParagonModManager.RefundFor(5, 0.8), "0.8 x 5");
            Assert.AreEqual(0, ParagonModManager.RefundFor(1, 0.8), "rounds down to 0");
            Assert.AreEqual(29, ParagonModManager.RefundFor(100, 0.29), "0.29 x 100 is 28.999999999999996 in doubles; the epsilon keeps it at 29");
            Assert.AreEqual(0, ParagonModManager.RefundFor(0, 0.8));
            Assert.AreEqual(0, ParagonModManager.RefundFor(5, -1.0));
            Assert.AreEqual(0, ParagonModManager.RefundFor(5, double.NaN));
            Assert.AreEqual(5, ParagonModManager.RefundFor(5, 7.0), "a fraction above 1 cannot mint tokens");
        }
    }
}
