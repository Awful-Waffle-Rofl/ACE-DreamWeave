using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Per-wielder weapon-mod suppression (owner ruling 2026-09-25: no weapon mods on the PK facet).
    ///
    /// ACE.Server.Tests cannot construct a live Player (its constructor reaches the auth database), so the
    /// Player's own predicate is driven through two seams: the pure rule WeaponModSuppression.Suppressed, and
    /// WeaponModSuppression.PlayerSuppressedSource, which the accessor guard consults for a Player owner. The
    /// Player used as a wielder is an uninitialized instance (FormatterServices.GetUninitializedObject, as in
    /// MuleSummonTests) that is only ever type-tested and handed to the seam - no member of it is touched.
    ///
    /// Every test names the production call site it drives.
    /// </summary>
    [TestClass]
    public class WeaponModSuppressionTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>
        /// Runs <paramref name="body"/> with weapon_mods_enabled forced TRUE and the Player suppression seam
        /// answering <paramref name="playerSuppressed"/>, restoring both whatever happens.
        /// </summary>
        private static void WithSuppression(bool playerSuppressed, Action body)
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            var priorSource = WeaponModSuppression.PlayerSuppressedSource;

            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            WeaponModSuppression.PlayerSuppressedSource = _ => playerSuppressed;

            try
            {
                body();
            }
            finally
            {
                WeaponModSuppression.PlayerSuppressedSource = priorSource;
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
            }
        }

        private static Player InertPlayer() => (Player)FormatterServices.GetUninitializedObject(typeof(Player));

        /// <summary>A melee weapon carrying a real Tier B record for <paramref name="id"/> at its MaxRoll.</summary>
        private static WorldObject WeaponWithTierB(WeaponModId id)
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(id, out var definition));
            Assert.AreEqual(WeaponModTier.B, definition.Tier);

            var weapon = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            WeaponModTinkerSet.ApplySpecial(weapon, definition, definition.MaxRoll);

            Assert.IsNotNull(weapon.GetProperty(definition.Record), "fixture: the Tier B record was not written");

            return weapon;
        }

        // ================= the predicate =================

        /// <summary>
        /// Drives WeaponModSuppression.Suppressed, the body of Player.WeaponModSuppressed: the PK facet rule OR the
        /// PvP arena weapon-mod mask (Docs/Pvp/DESIGN.md H11). All four input rows, so dropping either term - or
        /// turning the OR into an AND - fails a row.
        /// </summary>
        [TestMethod]
        public void Suppressed_IsThePkFacetRuleOrTheArenaMask()
        {
            Assert.IsFalse(WeaponModSuppression.Suppressed(pkFacetRuleActive: false, pvpArenaMaskActive: false), "neither: weapon mods live");
            Assert.IsTrue(WeaponModSuppression.Suppressed(pkFacetRuleActive: true, pvpArenaMaskActive: false), "PK facet rule alone");
            Assert.IsTrue(WeaponModSuppression.Suppressed(pkFacetRuleActive: false, pvpArenaMaskActive: true), "arena mask alone");
            Assert.IsTrue(WeaponModSuppression.Suppressed(pkFacetRuleActive: true, pvpArenaMaskActive: true), "both");
        }

        /// <summary>
        /// Drives WeaponModSuppression.SuppressedFor, the owner resolution the accessor guard and the Tier A
        /// read sites share. The seam answers TRUE throughout, so a false here can only come from the owner
        /// test itself: a null owner or a non-player is never suppressed.
        /// </summary>
        [TestMethod]
        public void SuppressedFor_OnlyASuppressedPlayerCounts()
        {
            WithSuppression(true, () =>
            {
                Assert.IsTrue(WeaponModSuppression.SuppressedFor(InertPlayer()), "a suppressed Player owner");
                Assert.IsFalse(WeaponModSuppression.SuppressedFor(null), "a null owner must never read as suppressed");
                Assert.IsFalse(WeaponModSuppression.SuppressedFor(WeaponModTestKit.MakeWeapon()), "a non-player owner must never read as suppressed");
            });

            WithSuppression(false, () =>
            {
                Assert.IsFalse(WeaponModSuppression.SuppressedFor(InertPlayer()), "an unsuppressed Player owner");
            });
        }

        // ================= Tier B: the 2-arg accessor =================

        /// <summary>
        /// THE DISCRIMINATING TEST. Drives WeaponModCombat.ReadWeaponOnly(weapon, modId) - the 2-arg accessor
        /// behind Player.GetWeaponOnlyModValue / GetCasterOnlyModValue and PanicReload - with the weapon's
        /// Wielder set to a suppressed Player. The weapon carries a REAL Tier B record, proved live by the
        /// explicit-gate read, so a zero here can only come from the suppression guard.
        /// </summary>
        [TestMethod]
        public void ReadWeaponOnly_SuppressedWielder_ReadsZero()
        {
            WithSuppression(true, () =>
            {
                var weapon = WeaponWithTierB(WeaponModId.Execution);
                weapon.Wielder = InertPlayer();

                var unsuppressed = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution, true);
                Assert.IsTrue(unsuppressed > 0.0, "fixture: the record must be live, or the zero below proves nothing");

                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution), 0.0,
                    "a suppressed wielder's weapon still reads its Tier B magnitude through ReadWeaponOnly");
            });
        }

        /// <summary>Control for the test above: same weapon, same Player wielder, suppression OFF - the full amount.</summary>
        [TestMethod]
        public void ReadWeaponOnly_UnsuppressedWielder_ReadsFullAmount_Control()
        {
            WithSuppression(false, () =>
            {
                var weapon = WeaponWithTierB(WeaponModId.Execution);
                weapon.Wielder = InertPlayer();

                var expected = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution, true);
                Assert.IsTrue(expected > 0.0);

                Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution), 1e-15);
            });
        }

        /// <summary>
        /// Null-wielder control for ReadWeaponOnly(weapon, modId): with the seam answering TRUE for any Player, a
        /// weapon with NO Wielder reads the full amount, exactly as before suppression existed.
        /// </summary>
        [TestMethod]
        public void ReadWeaponOnly_NullWielder_ReadsFullAmount_Control()
        {
            WithSuppression(true, () =>
            {
                var weapon = WeaponWithTierB(WeaponModId.Execution);
                Assert.IsNull(weapon.Wielder);

                var expected = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution, true);
                Assert.IsTrue(expected > 0.0);

                Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Execution), 1e-15,
                    "a weapon with no wielder was read as suppressed");
            });
        }

        // ================= Tier B: the explicit-wielder accessor =================

        /// <summary>
        /// Drives WeaponModCombat.ReadWeaponOnlyWieldedBy, the shape of DamageEvent's Execution read,
        /// SpellProjectile's two Execution reads, WorldObject_Weapon's Focus/Attunement reads and
        /// ApplyBaseDamageMods: a launcher that is no longer equipped (Wielder null) fired by a suppressed
        /// attacker reads 0, and the same launcher fired by an unsuppressed attacker, or with no attacker in
        /// hand, reads the full amount.
        /// </summary>
        [TestMethod]
        public void ReadWeaponOnlyWieldedBy_ExplicitAttackerDecides()
        {
            WithSuppression(true, () =>
            {
                var weapon = WeaponWithTierB(WeaponModId.Focus);
                Assert.IsNull(weapon.Wielder);

                var expected = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Focus, true);
                Assert.IsTrue(expected > 0.0);

                Assert.AreEqual(0.0, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Focus, InertPlayer()), 0.0,
                    "a suppressed attacker's unequipped launcher still reads its Tier B magnitude");

                Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Focus, null), 1e-15,
                    "no attacker and no wielder must read the full amount");

                Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Focus, WeaponModTestKit.MakeWeapon()), 1e-15,
                    "a non-player attacker must read the full amount");
            });

            WithSuppression(false, () =>
            {
                var weapon = WeaponWithTierB(WeaponModId.Focus);
                var expected = WeaponModCombat.ReadWeaponOnly(weapon, WeaponModId.Focus, true);

                Assert.AreEqual(expected, WeaponModCombat.ReadWeaponOnlyWieldedBy(weapon, WeaponModId.Focus, InertPlayer()), 1e-15);
            });
        }

        // ================= Tier A: TierANet =================

        /// <summary>Drives WeaponModSuppression.TierANet, the per-item arithmetic under every Tier A read site.</summary>
        [TestMethod]
        public void TierANet_SubtractsTheRecordAndClampsAtTheFloor()
        {
            Assert.AreEqual(6.0, WeaponModSuppression.TierANet(10, 4, 0), 1e-12, "plain subtraction");
            Assert.AreEqual(0.0, WeaponModSuppression.TierANet(3, 5, 0), 1e-12, "clamped at the floor");
            Assert.AreEqual(2.0, WeaponModSuppression.TierANet(5, 4, 2), 1e-12, "clamped at a nonzero floor");
            Assert.AreEqual(0.4, WeaponModSuppression.TierANet(0.7, 0.3, 0), 1e-12, "fractional native (IgnoreShield)");
        }

        [TestMethod]
        public void TierANet_ZeroOrMalformedRecord_LeavesTheNativeAlone()
        {
            Assert.AreEqual(10.0, WeaponModSuppression.TierANet(10, 0, 0), 0.0, "zero record");
            Assert.AreEqual(10.0, WeaponModSuppression.TierANet(10, -2, 0), 0.0, "negative record");
            Assert.AreEqual(10.0, WeaponModSuppression.TierANet(10, double.NaN, 0), 0.0, "NaN record");
            Assert.AreEqual(10.0, WeaponModSuppression.TierANet(10, double.PositiveInfinity, 0), 0.0, "infinite record");
            Assert.IsTrue(double.IsNaN(WeaponModSuppression.TierANet(double.NaN, 3, 0)), "NaN native passes through");
        }

        [TestMethod]
        public void TierANet_NeverRaisesANativeAlreadyBelowTheFloor()
        {
            Assert.AreEqual(-1.0, WeaponModSuppression.TierANet(-1, 2, 0), 0.0);
        }

        // ================= Tier A: the rating offset =================

        /// <summary>
        /// Drives WeaponModSuppression.TierARatingOffset, which Creature_Rating's GetDamageRating / GetCritRating /
        /// GetCritDamageRating subtract through RatingOffsetFor. Each item gives back only its OWN recorded
        /// amount; an item with the native but no record gives back nothing.
        /// </summary>
        [TestMethod]
        public void TierARatingOffset_SumsEachItemsOwnRecordedAmount()
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(WeaponModId.WeakPoint, out var weakPoint));
            Assert.AreEqual(PropertyInt.GearCrit, weakPoint.NativeInt);

            var modded = WeaponModTestKit.MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 2 } });
            WeaponModTinkerSet.ApplySpecial(modded, weakPoint, 3);
            Assert.AreEqual(5, modded.GetProperty(PropertyInt.GearCrit), "fixture: Tier A adds into the native");

            var lootOnly = WeaponModTestKit.MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearCrit, 4 } });

            Assert.AreEqual(3, WeaponModSuppression.TierARatingOffset(new[] { modded, lootOnly }, PropertyInt.GearCrit));
            Assert.AreEqual(0, WeaponModSuppression.TierARatingOffset(new[] { lootOnly }, PropertyInt.GearCrit), "zero-record case");
            Assert.AreEqual(0, WeaponModSuppression.TierARatingOffset(new[] { modded }, PropertyInt.GearDamageResist), "no Tier A row writes GearDamageResist");
            Assert.AreEqual(0, WeaponModSuppression.TierARatingOffset(null, PropertyInt.GearCrit));
        }

        /// <summary>
        /// Floor clamp at the rating site: a malformed record larger than the native only gives back the native,
        /// so the rating never drops below what the item would have with no mod at all.
        /// </summary>
        [TestMethod]
        public void TierARatingOffset_ClampsAMalformedRecordAtTheFloor()
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(WeaponModId.Bloodthirst, out var bloodthirst));

            var item = WeaponModTestKit.MakeWeapon(ints: new Dictionary<PropertyInt, int> { { PropertyInt.GearDamage, 5 } });
            item.SetProperty(bloodthirst.Record, 9.0);

            Assert.AreEqual(5, WeaponModSuppression.TierARatingOffset(new[] { item }, PropertyInt.GearDamage));
        }

        /// <summary>RatingOffsetFor's guard: a null or non-player creature gives back nothing and touches nothing.</summary>
        [TestMethod]
        public void RatingOffsetFor_NullCreature_IsZero()
        {
            WithSuppression(true, () =>
            {
                Assert.AreEqual(0, WeaponModSuppression.RatingOffsetFor(null, PropertyInt.GearCrit));
            });
        }

        // ================= Tier A: GetIgnoreShieldMod's weapon term =================

        /// <summary>
        /// Drives WeaponModSuppression.WeaponIgnoreShield, the weapon term of WorldObject.GetIgnoreShieldMod.
        /// A retail IgnoreShield of 0.4 plus Shield Bypass 0.3 reads 0.7 unsuppressed and 0.4 suppressed - never
        /// below the retail value.
        /// </summary>
        [TestMethod]
        public void WeaponIgnoreShield_TakesOnlyShieldBypassBackOff()
        {
            Assert.IsTrue(WeaponModRegistry.TryGet(WeaponModId.ShieldBypass, out var bypass));
            Assert.AreEqual(PropertyFloat.IgnoreShield, bypass.NativeFloat);

            var weapon = WeaponModTestKit.MakeWeapon(floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.IgnoreShield, 0.4 } });
            WeaponModTinkerSet.ApplySpecial(weapon, bypass, 0.3);

            Assert.AreEqual(0.7, WeaponModSuppression.WeaponIgnoreShield(weapon, suppressed: false), 1e-12);
            Assert.AreEqual(0.4, WeaponModSuppression.WeaponIgnoreShield(weapon, suppressed: true), 1e-12);

            var retail = WeaponModTestKit.MakeWeapon(floats: new Dictionary<PropertyFloat, double> { { PropertyFloat.IgnoreShield, 0.4 } });
            Assert.AreEqual(0.4, WeaponModSuppression.WeaponIgnoreShield(retail, suppressed: true), 1e-12, "zero-record case");

            Assert.AreEqual(0.0, WeaponModSuppression.WeaponIgnoreShield(null, suppressed: true), 0.0);
        }
    }
}
