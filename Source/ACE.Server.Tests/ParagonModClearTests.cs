using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>The clear (reset) mutation, ParagonModManager.ClearGems, and the default of its refund tunable.</summary>
    [TestClass]
    public class ParagonModClearTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static WorldObject Full(params WeaponModId[] gems) =>
            ParagonModApplyTests.ParagonWeapon(WeaponClass.Melee, 50 + gems.Length, gems);

        private static readonly WeaponModId[] Five =
            { WeaponModId.Devastation, WeaponModId.Ambush, WeaponModId.Heft, WeaponModId.Leverage, WeaponModId.ShieldBypass };

        [TestMethod]
        public void RefundTunable_DefaultsToZeroPointEight()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultDoubleProperties.ContainsKey(ParagonModManager.RefundFractionKey));
            Assert.AreEqual(0.8, DefaultPropertyManager.DefaultDoubleProperties[ParagonModManager.RefundFractionKey].Item, 1e-12);
        }

        [TestMethod]
        public void Clear_RemovesEverySlot_AndTheTokensSpent_AndResetsTo50()
        {
            var weapon = Full(Five);
            weapon.SetProperty(PropertyInt.ParagonModTokensSpent, 5);

            var spent = ParagonModManager.ClearGems(weapon);

            Assert.AreEqual(5, spent);

            foreach (var slot in ParagonModRegistry.Slots)
                Assert.IsNull(weapon.GetProperty(slot), slot.ToString());

            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModTokensSpent));
            Assert.AreEqual(50, weapon.ItemMaxLevel);
            Assert.IsFalse(WeaponModParagon.HasParagonMods(weapon));
        }

        [TestMethod]
        public void Clear_ClampsTheXpToLevel50()
        {
            var weapon = Full(Five);

            ParagonModManager.ClearGems(weapon);

            Assert.AreEqual(50L * ParagonModApplyTests.BaseXp, weapon.ItemTotalXp, "55 x base is clamped down to 50 x base");
            Assert.AreEqual(50, weapon.ItemLevel);
        }

        [TestMethod]
        public void Clear_OnAnUnsupportedXpStyle_ResetsOnlyTheMaxLevel_AndLeavesTheXpAlone()
        {
            var weapon = Full(WeaponModId.Devastation);
            weapon.SetProperty(PropertyInt.ItemXpStyle, (int)ItemXpStyle.ScalesWithLevel);
            // base 1 makes level 50 worth 2^50 - 1 XP, so the old unconditional clamp would visibly lower this total
            weapon.SetProperty(PropertyInt64.ItemBaseXp, 1L);
            weapon.SetProperty(PropertyInt64.ItemTotalXp, 5000000000000000L);

            ParagonModManager.ClearGems(weapon);

            Assert.AreEqual(50, weapon.ItemMaxLevel);
            Assert.AreEqual(5000000000000000L, weapon.ItemTotalXp, "no XP rewrite for a style this class does not understand");
            Assert.IsNull(weapon.GetProperty(PropertyInt.ParagonModSlot1));
        }

        [TestMethod]
        public void Clear_NeverRaisesXpAboveWhatTheWeaponHad()
        {
            var weapon = Full(WeaponModId.Devastation);
            weapon.SetProperty(PropertyInt64.ItemTotalXp, 49L * ParagonModApplyTests.BaseXp + 5);

            ParagonModManager.ClearGems(weapon);

            Assert.AreEqual(49L * ParagonModApplyTests.BaseXp + 5, weapon.ItemTotalXp, "min(current, level 50 XP) keeps the lower value");
        }

        [TestMethod]
        public void Clear_LeavesNativesAndTourmalineRecordsUntouched()
        {
            var weapon = Full(Five);
            var heft = WeaponModRegistry.Get(WeaponModId.Heft);

            weapon.SetProperty(heft.Record, 0.25);
            weapon.SetProperty(PropertyFloat.IgnoreShield, 0.1);
            weapon.SetProperty(PropertyInt.NumTimesTinkered, 4);

            ParagonModManager.ClearGems(weapon);

            Assert.AreEqual(0.25, weapon.GetProperty(heft.Record));
            Assert.AreEqual(0.1, weapon.GetProperty(PropertyFloat.IgnoreShield));
            Assert.AreEqual(4, weapon.GetProperty(PropertyInt.NumTimesTinkered));
        }

        [TestMethod]
        public void Refund_ForFiveTokensAtDefault_IsFour_AndOneRoundsToZero()
        {
            var fraction = DefaultPropertyManager.DefaultDoubleProperties[ParagonModManager.RefundFractionKey].Item;

            Assert.AreEqual(4, ParagonModManager.RefundFor(5, fraction));
            Assert.AreEqual(0, ParagonModManager.RefundFor(1, fraction));
        }

        [TestMethod]
        public void ResetRefusals_NothingToReset_NeedsAtLeastOneSlot()
        {
            Assert.AreEqual(ParagonModManager.ParagonModRefusal.NothingToReset,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 0, 0, null, true));

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.None,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 1, 0, null, true), "refund 0 needs no currency");
        }

        [TestMethod]
        public void ResetRefusals_RefundOwed_NeedsCurrencyAndRoom_ButZeroRefundSkipsBoth()
        {
            Assert.AreEqual(ParagonModManager.ParagonModRefusal.MissingCurrency,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 3, 4, null, true));

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.NoRoomForRefund,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 3, 4, 1000u, false));

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.None,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 3, 0, null, false), "a refund that rounds to 0 is skipped, so no room is needed");

            Assert.AreEqual(ParagonModManager.ParagonModRefusal.None,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, 3, 4, 1000u, true));
        }

        [TestMethod]
        public void ResetRefusals_MalformedSlots_AreStillResettable()
        {
            Assert.AreEqual(ParagonModManager.ParagonModRefusal.None,
                ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, 140, -1, 0, null, true));
        }
    }
}
