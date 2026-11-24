using System;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Paragon weapon mods, Tier B: the slot count, the additive clamp with a Tourmaline record, and the gate and
    /// suppression that guard WeaponModCombat.ReadWeaponOnly. Every read here goes through the production
    /// accessor, so a Paragon gem that the accessor ignored would fail every row.
    /// </summary>
    [TestClass]
    public class WeaponModParagonMathTests
    {
        private const double Scale = 1.0;
        private const double Tol = 1e-9;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        internal static WorldObject WeaponWithGems(WeaponClass weaponClass, params WeaponModId[] gems)
        {
            var weapon = WeaponModTestKit.MakeUntinkered(weaponClass);

            for (var i = 0; i < gems.Length; i++)
                weapon.SetProperty(ParagonModRegistry.Slots[i], (int)gems[i]);

            return weapon;
        }

        private static WeaponModDefinition Row(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition), $"{id} is not a live row");
            return definition;
        }

        private static WeaponModId[] Repeat(WeaponModId id, int count)
        {
            var gems = new WeaponModId[count];

            for (var i = 0; i < count; i++)
                gems[i] = id;

            return gems;
        }

        // ================= storage =================

        [TestMethod]
        public void Registry_FiveSlotsInOrder_TwentyPercentEach()
        {
            Assert.AreEqual(0.20, ParagonModRegistry.FractionPerGem, Tol);
            Assert.AreEqual(5, ParagonModRegistry.MaxGems);
            CollectionAssert.AreEqual(
                new[] { PropertyInt.ParagonModSlot1, PropertyInt.ParagonModSlot2, PropertyInt.ParagonModSlot3, PropertyInt.ParagonModSlot4, PropertyInt.ParagonModSlot5 },
                ParagonModRegistry.Slots);
        }

        [TestMethod]
        public void ParagonFraction_CountsMatchingSlotsOnly()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush, WeaponModId.Heft, WeaponModId.Ambush, WeaponModId.Ambush, WeaponModId.Heft);

            Assert.AreEqual(0.60, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Ambush), Tol);
            Assert.AreEqual(0.40, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Heft), Tol);
            Assert.AreEqual(0.0, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Focus), Tol);
        }

        [TestMethod]
        public void ParagonFraction_NoSlot1_IsZero_EvenWithLaterSlotsSet()
        {
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            Assert.AreEqual(0.0, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Ambush), Tol, "no gems");

            weapon.SetProperty(PropertyInt.ParagonModSlot2, (int)WeaponModId.Ambush);
            Assert.IsFalse(WeaponModParagon.HasParagonMods(weapon));
            Assert.AreEqual(0.0, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Ambush), Tol, "slot 2 without slot 1 is malformed and inert");
        }

        [TestMethod]
        public void ParagonFraction_StopsAtTheFirstGap()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush);
            weapon.SetProperty(PropertyInt.ParagonModSlot3, (int)WeaponModId.Ambush);

            Assert.AreEqual(0.20, WeaponModParagon.ParagonFraction(weapon, WeaponModId.Ambush), Tol);
        }

        [TestMethod]
        public void ParagonFraction_NullWeapon_IsZero() =>
            Assert.AreEqual(0.0, WeaponModParagon.ParagonFraction(null, WeaponModId.Ambush), Tol);

        [TestMethod]
        public void CombinedFraction_AddsAndClamps()
        {
            Assert.AreEqual(0.20, WeaponModParagon.CombinedFraction(null, 0.20), Tol, "absent record");
            Assert.AreEqual(0.90, WeaponModParagon.CombinedFraction(0.50, 0.40), Tol, "additive");
            Assert.AreEqual(1.00, WeaponModParagon.CombinedFraction(0.90, 0.20), Tol, "clamped at a perfect roll");
            Assert.AreEqual(1.00, WeaponModParagon.CombinedFraction(0.10, 1.00), Tol, "five gems alone already reach the ceiling");
            Assert.AreEqual(0.20, WeaponModParagon.CombinedFraction(double.NaN, 0.20), Tol, "a NaN record reads 0");
            Assert.AreEqual(0.20, WeaponModParagon.CombinedFraction(-0.5, 0.20), Tol, "a negative record reads 0");
            Assert.AreEqual(0.0, WeaponModParagon.CombinedFraction(null, 0.0), Tol);
        }

        // ================= Tier B through the production accessor =================

        [TestMethod]
        public void ReadWeaponOnly_ParagonOnlyMod_ReadsWithoutARecord()
        {
            var ambush = Row(WeaponModId.Ambush);
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush);

            Assert.IsNull(weapon.GetProperty(ambush.Record), "fixture: no Tourmaline record");
            Assert.AreEqual(ambush.MaxRoll * 0.20, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, true, Scale), Tol);
        }

        [TestMethod]
        public void ReadWeaponOnly_FiveGemStack_ReachesThePerfectRoll()
        {
            var heft = Row(WeaponModId.Heft);

            for (var gems = 1; gems <= 5; gems++)
            {
                var weapon = WeaponWithGems(WeaponClass.Melee, Repeat(WeaponModId.Heft, gems));
                Assert.AreEqual(heft.MaxRoll * 0.20 * gems, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Heft, true, Scale), Tol, $"{gems} gems");
            }
        }

        [TestMethod]
        public void ReadWeaponOnly_TourmalinePlusParagon_ClampsAtOne()
        {
            var ambush = Row(WeaponModId.Ambush);

            var partial = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush, WeaponModId.Ambush);
            WeaponModTinkerSet.ApplySpecialAtFraction(partial, ambush, 0.50, Scale);
            Assert.AreEqual(ambush.MaxRoll * 0.90, WeaponModCombat.ReadWeaponOnly(partial, WeaponModId.Ambush, true, Scale), Tol, "0.5 + 0.4");

            var nearlyPerfect = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush);
            WeaponModTinkerSet.ApplySpecialAtFraction(nearlyPerfect, ambush, 0.90, Scale);
            Assert.AreEqual(ambush.MaxRoll, WeaponModCombat.ReadWeaponOnly(nearlyPerfect, WeaponModId.Ambush, true, Scale), Tol, "0.9 + 0.2 clamps at 1.0");

            var noGems = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            WeaponModTinkerSet.ApplySpecialAtFraction(noGems, ambush, 0.50, Scale);
            Assert.AreEqual(ambush.MaxRoll * 0.50, WeaponModCombat.ReadWeaponOnly(noGems, WeaponModId.Ambush, true, Scale), Tol, "control: a record alone is unchanged");
        }

        [TestMethod]
        public void ReadWeaponOnly_GemsForAnotherMod_DoNotLeak()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Heft, WeaponModId.Heft);

            Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, true, Scale), Tol);
        }

        [TestMethod]
        public void ReadWeaponOnly_TierAGem_IsNotATierBRead()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Devastation);

            Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Devastation, true, Scale), Tol);
        }

        [TestMethod]
        public void ReadWeaponOnly_RetiredIdInASlot_IsInert()
        {
            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            weapon.SetProperty(PropertyInt.ParagonModSlot1, 4);   // 4 was Cleave, retired

            foreach (var definition in WeaponModRegistry.AllMods)
            {
                if (definition.Tier == WeaponModTier.B)
                    Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, definition.Id, true, Scale), Tol, definition.Id.ToString());
            }
        }

        [TestMethod]
        public void ReadWeaponOnly_GateOff_ParagonReadsZero()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, Repeat(WeaponModId.Ambush, 5));

            Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, false, Scale), Tol);
            Assert.IsTrue(WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, true, Scale) > 0.0, "control: live with the gate on");
        }

        [TestMethod]
        public void ReadWeaponOnly_DefensiveWeapon_KeepsThePerItemCeiling()
        {
            var oneHanded = WeaponWithGems(WeaponClass.Melee, WeaponModId.DefensiveWeapon);
            var missile = WeaponWithGems(WeaponClass.Missile, WeaponModId.DefensiveWeapon);
            var oneHandedFull = WeaponWithGems(WeaponClass.Melee, Repeat(WeaponModId.DefensiveWeapon, 5));

            Assert.AreEqual(20.0, WeaponModCombat.ReadWeaponOnly(oneHanded, WeaponModId.DefensiveWeapon, true, Scale), Tol, "one-handed melee ceiling 100");
            Assert.AreEqual(40.0, WeaponModCombat.ReadWeaponOnly(missile, WeaponModId.DefensiveWeapon, true, Scale), Tol, "missile ceiling 200");
            Assert.AreEqual(100.0, WeaponModCombat.ReadWeaponOnly(oneHandedFull, WeaponModId.DefensiveWeapon, true, Scale), Tol, "five gems stop at the 100 ceiling");
        }

        [TestMethod]
        public void ReadWeaponOnly_WritesNothing()
        {
            var ambush = Row(WeaponModId.Ambush);
            var weapon = WeaponWithGems(WeaponClass.Melee, WeaponModId.Ambush);

            WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Ambush, true, Scale);

            Assert.IsNull(weapon.GetProperty(ambush.Record), "a read must never write a Tourmaline record");
        }

        // ================= gate and suppression through the live accessor =================

        private static void WithLiveGate(bool enabled, bool playerSuppressed, Action body)
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            var priorSource = WeaponModSuppression.PlayerSuppressedSource;

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", enabled));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", Scale));
            WeaponModSuppression.PlayerSuppressedSource = _ => playerSuppressed;

            try
            {
                body();
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = priorSource;
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
            }
        }

        private static Player InertPlayer() => (Player)FormatterServices.GetUninitializedObject(typeof(Player));

        [TestMethod]
        public void ReadWeaponOnlyWieldedBy_SuppressedWielder_ParagonReadsZero()
        {
            var ambush = Row(WeaponModId.Ambush);
            var weapon = WeaponWithGems(WeaponClass.Melee, Repeat(WeaponModId.Ambush, 3));

            WithLiveGate(true, true, () =>
                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Ambush, InertPlayer()), Tol));

            WithLiveGate(true, false, () =>
                Assert.AreEqual(ambush.MaxRoll * 0.60, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Ambush, InertPlayer()), Tol,
                    "control: the same wielder unsuppressed reads the Paragon value"));
        }

        [TestMethod]
        public void ReadWeaponOnlyWieldedBy_LiveGateOff_ParagonReadsZero()
        {
            var weapon = WeaponWithGems(WeaponClass.Melee, Repeat(WeaponModId.Ambush, 3));

            WithLiveGate(false, false, () =>
                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Ambush, InertPlayer()), Tol));
        }
    }
}
