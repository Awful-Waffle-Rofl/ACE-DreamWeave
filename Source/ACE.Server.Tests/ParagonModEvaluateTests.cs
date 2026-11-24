using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using R = ACE.Server.WeaponMods.ParagonModManager.ParagonModRefusal;

namespace ACE.Server.Tests
{
    /// <summary>
    /// ParagonModManager.EvaluateWith against real in-memory items: the item-level read must never throw, because a
    /// throw before any refusal leaves the use hung with no message. WorldObject.ItemLevel dereferences ItemTotalXp,
    /// which HasItemLevel does not check.
    /// </summary>
    [TestClass]
    public class ParagonModEvaluateTests
    {
        private static WorldObject Gem(WeaponModId mod = WeaponModId.Devastation)
        {
            var gem = WeaponModTestKit.MakeWeapon(ItemType.Gem, null, workmanship: null);
            gem.SetProperty(PropertyInt.ParagonModGemModifier, (int)mod);
            return gem;
        }

        private static R Evaluate(WorldObject gem, WorldObject target) =>
            ParagonModManager.EvaluateWith(null, ParagonModManager.ParagonModAction.ApplyGem, gem, target, true, true, true);

        [TestMethod]
        public void FreshParagonWeapon_WithNoItemTotalXp_RefusesNotFullyLeveled_WithoutThrowing()
        {
            var weapon = ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50);
            weapon.RemoveProperty(PropertyInt64.ItemTotalXp);

            Assert.IsTrue(weapon.HasItemLevel, "the trap: HasItemLevel is true while ItemTotalXp is absent");
            Assert.AreEqual(0, ParagonModManager.CurrentLevel(weapon));
            Assert.AreEqual(R.NotFullyLeveled, Evaluate(Gem(), weapon));
        }

        [TestMethod]
        public void LevelingNonWeapon_RefusesNotAWeapon_WithoutThrowing()
        {
            var cloak = WeaponModTestKit.MakeWeapon(ItemType.Armor, null, workmanship: null);
            cloak.SetProperty(PropertyInt64.ItemBaseXp, ParagonModApplyTests.BaseXp);
            cloak.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.Fixed);
            cloak.SetProperty(PropertyInt.ItemMaxLevel, 50);

            Assert.IsTrue(cloak.HasItemLevel);
            Assert.AreEqual(R.NotAWeapon, Evaluate(Gem(), cloak));
        }

        [TestMethod]
        public void ScalesWithLevelWeapon_RefusesUnsupportedStyle()
        {
            var weapon = ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50);
            weapon.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.ScalesWithLevel);

            Assert.AreEqual(R.UnsupportedXpStyle, Evaluate(Gem(), weapon));
        }

        [TestMethod]
        public void HugeBaseXp_RefusesBeforeTheGemIsConsumed()
        {
            // 50 x this fits a long (the weapon can exist) but 51 x this does not, so level 51 cannot be stored.
            // (2^54 would not do: 55 x 2^54 still fits a long.)
            const long HugeBase = 184000000000000000L;

            var weapon = ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50);
            weapon.SetProperty(PropertyInt64.ItemBaseXp, HugeBase);
            weapon.SetProperty(PropertyInt64.ItemTotalXp, 50L * HugeBase);

            Assert.AreEqual(50, weapon.ItemLevel, "fixture: the weapon itself is fully leveled");
            Assert.IsFalse(ParagonModManager.NextTotalReadsBack(weapon));
            Assert.AreEqual(R.XpWouldOverflow, Evaluate(Gem(), weapon));
            Assert.IsFalse(ParagonModManager.ApplyGemTo(weapon, (int)WeaponModId.Devastation, 1), "the ApplyGemTo backstop agrees");
            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModSlot1));
        }

        [TestMethod]
        public void NextTotalReadsBack_TrueForTheNormalCases_FalseWithoutData()
        {
            Assert.IsTrue(ParagonModManager.NextTotalReadsBack(ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50)));
            Assert.IsTrue(ParagonModManager.NextTotalReadsBack(2000000000UL, 55, ItemXpStyle.FixedPlusBase));
            Assert.IsFalse(ParagonModManager.NextTotalReadsBack(2000000000UL, 51, ItemXpStyle.ScalesWithLevel));
            Assert.IsFalse(ParagonModManager.NextTotalReadsBack(null));
        }

        [TestMethod]
        public void HealthyWeapon_Passes_AndWrongClassGem_Refuses()
        {
            var weapon = ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50);

            Assert.AreEqual(R.None, Evaluate(Gem(), weapon));
            Assert.AreEqual(R.WrongClass, Evaluate(Gem(WeaponModId.Tension), weapon));
        }

        [TestMethod]
        public void CurrentLevel_NullWithoutLevelData()
        {
            Assert.IsNull(ParagonModManager.CurrentLevel(null));
            Assert.IsNull(ParagonModManager.CurrentLevel(WeaponModTestKit.MakeUntinkered(WeaponClass.Melee)));
        }
    }
}