using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.ClassAbilities;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Coverage for the Blood Mage (7th class) scaffold added 2026-08-02: the ClassAbilityId/ClassAbilityClass
    /// enum additions, the registered tunable defaults, and the pure Blood Charge stack-cap / idle-expiry math
    /// (BloodChargeMath) that backs Player_ClassAbilityBuffs' transient bloodChargeStacks counter. The stateful
    /// half (AddBloodChargeStack / ClearBloodChargeStacks / GetBloodChargeStacks, and the heartbeat fade) lives
    /// on Player and needs a live Player/landblock to exercise - not covered here, same as Frenzy/Nether Rush.
    /// No Blood Mage skill exists yet, so nothing calls any of this in production code.
    /// </summary>
    [TestClass]
    public class BloodMageScaffoldTests
    {
        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds the server-tunable property cache from hardcoded defaults, no database involved
            DefaultPropertyManager.LoadDefaultProperties();
        }

        private static double D(string key) => PropertyManager.GetDouble(key).Item;
        private static long L(string key) => PropertyManager.GetLong(key).Item;

        // ---- enum additions --------------------------------------------------------------------

        [TestMethod]
        public void ClassAbilityClass_BloodMage_IsAppendedAfterVoidSummon()
        {
            Assert.AreEqual(6, (int)ClassAbilityClass.VoidSummon);
            Assert.AreEqual(7, (int)ClassAbilityClass.BloodMage);
        }

        [TestMethod]
        public void ClassAbilityId_BloodMageSkills_AreAppendedAfterSoulTetherWithDistinctIds()
        {
            Assert.AreEqual(50, (int)ClassAbilityId.SoulTether);

            var ids = new[]
            {
                ClassAbilityId.SanguineReserve,
                ClassAbilityId.EnhancedLifeMagic,
                ClassAbilityId.Transfusion,
                ClassAbilityId.BloodMageTraining,
                ClassAbilityId.WeakenedBlood,
                ClassAbilityId.Malediction,
                ClassAbilityId.CrimsonHarvest,
                ClassAbilityId.HealBoostRating,
                ClassAbilityId.Exsanguinate,
                ClassAbilityId.BloodPrice,
                ClassAbilityId.SanguineWard,
            };

            Assert.AreEqual(51, (int)ClassAbilityId.SanguineReserve);
            Assert.AreEqual(52, (int)ClassAbilityId.EnhancedLifeMagic);
            Assert.AreEqual(53, (int)ClassAbilityId.Transfusion);
            Assert.AreEqual(54, (int)ClassAbilityId.BloodMageTraining);
            Assert.AreEqual(55, (int)ClassAbilityId.WeakenedBlood);
            Assert.AreEqual(56, (int)ClassAbilityId.Malediction);
            Assert.AreEqual(57, (int)ClassAbilityId.CrimsonHarvest);
            Assert.AreEqual(58, (int)ClassAbilityId.HealBoostRating);
            Assert.AreEqual(59, (int)ClassAbilityId.Exsanguinate);
            Assert.AreEqual(60, (int)ClassAbilityId.BloodPrice);
            Assert.AreEqual(61, (int)ClassAbilityId.SanguineWard);

            // every one of the eleven ids must be distinct
            CollectionAssert.AllItemsAreUnique(ids);
        }

        [TestMethod]
        public void ClassAbilityId_ExistingValues_AreUnchanged()
        {
            // append-only guard: renumbering any pre-existing id would silently corrupt persisted
            // CharacterPropertiesQuestRegistry rows (ClassAbility_<name>) on every live shard
            Assert.AreEqual(8, (int)ClassAbilityId.Frenzy);
            Assert.AreEqual(9, (int)ClassAbilityId.NetherRush);
            Assert.AreEqual(48, (int)ClassAbilityId.ManaBarrier);
            Assert.AreEqual(49, (int)ClassAbilityId.NetherBloom);
        }

        // ---- tunables --------------------------------------------------------------------------

        [TestMethod]
        public void NewTunables_AreRegisteredWithTheSpecifiedDefaults()
        {
            Assert.AreEqual(0.07, D("class_ability_bloodmage_charge_per_stack"), 1e-9);
            Assert.AreEqual(15.0, D("class_ability_bloodmage_charge_expire_seconds"), 1e-9);

            Assert.AreEqual(3, L("class_ability_bloodmage_charge_stack_cap_r1"));
            Assert.AreEqual(4, L("class_ability_bloodmage_charge_stack_cap_r2"));
            Assert.AreEqual(5, L("class_ability_bloodmage_charge_stack_cap_r3"));
        }

        // ---- BloodChargeMath: stack cap ---------------------------------------------------------

        [TestMethod]
        public void StackCap_FollowsPerRankTunables()
        {
            var r1 = L("class_ability_bloodmage_charge_stack_cap_r1");
            var r2 = L("class_ability_bloodmage_charge_stack_cap_r2");
            var r3 = L("class_ability_bloodmage_charge_stack_cap_r3");

            Assert.AreEqual(3, BloodChargeMath.StackCap(1, r1, r2, r3));
            Assert.AreEqual(4, BloodChargeMath.StackCap(2, r1, r2, r3));
            Assert.AreEqual(5, BloodChargeMath.StackCap(3, r1, r2, r3));
        }

        [TestMethod]
        public void StackCap_IsZeroWhenUnlearnedOrOutOfRange()
        {
            var r1 = L("class_ability_bloodmage_charge_stack_cap_r1");
            var r2 = L("class_ability_bloodmage_charge_stack_cap_r2");
            var r3 = L("class_ability_bloodmage_charge_stack_cap_r3");

            Assert.AreEqual(0, BloodChargeMath.StackCap(0, r1, r2, r3), "rank 0 (unlearned) must cap at zero");
            Assert.AreEqual(0, BloodChargeMath.StackCap(4, r1, r2, r3), "no rank above 3 exists");
            Assert.AreEqual(0, BloodChargeMath.StackCap(-1, r1, r2, r3));
        }

        // ---- BloodChargeMath: idle expiry -------------------------------------------------------

        [TestMethod]
        public void IsExpired_FalseWithinTheWindow()
        {
            var window = D("class_ability_bloodmage_charge_expire_seconds");

            Assert.IsFalse(BloodChargeMath.IsExpired(now: 100.0, lastAddTime: 100.0, expireSeconds: window), "no time elapsed");
            Assert.IsFalse(BloodChargeMath.IsExpired(now: 100.0 + window, lastAddTime: 100.0, expireSeconds: window), "exactly at the boundary must not have expired yet");
            Assert.IsFalse(BloodChargeMath.IsExpired(now: 100.0 + window - 0.01, lastAddTime: 100.0, expireSeconds: window));
        }

        [TestMethod]
        public void IsExpired_TrueOncePastTheWindow()
        {
            var window = D("class_ability_bloodmage_charge_expire_seconds");

            Assert.IsTrue(BloodChargeMath.IsExpired(now: 100.0 + window + 0.01, lastAddTime: 100.0, expireSeconds: window));
            Assert.IsTrue(BloodChargeMath.IsExpired(now: 1000.0, lastAddTime: 0.0, expireSeconds: window));
        }
    }
}
