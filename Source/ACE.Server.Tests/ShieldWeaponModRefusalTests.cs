using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Managers;
using ACE.Server.SalvageRoller;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using R = ACE.Server.WeaponMods.ParagonModManager.ParagonModRefusal;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Task 2 of the shield weapon mods plan: WeaponClassifier.Classify now returns WeaponClass.Shield for a shield,
    /// where it used to return None, so each consumer that must not take shields refuses one explicitly (Paragon,
    /// the weapon-quality aura). The Salvage Roller takes shields since 2026-11-26 and is pinned here as accepting. Every PropertyManager key read is seeded and restored.
    /// </summary>
    [TestClass]
    public class ShieldWeaponModRefusalTests
    {
        private bool savedEnabled;
        private bool savedRollerEnabled;
        private double savedScale;
        private readonly Dictionary<string, double> savedRollDoubles = new Dictionary<string, double>();
        private bool savedGuarantee;

        /// <summary>The doubles the weapon roll reads (WeaponModRoller.RollSpecialCount), seeded to the shipped default.</summary>
        private static readonly string[] RollDoubleKeys =
        {
            "weapon_mod_special_chance_1",
            "weapon_mod_special_chance_2",
            "weapon_mod_special_chance_3",
            "weapon_mod_special_chance_4",
        };

        [ClassInitialize]
        public static void ClassSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Setup()
        {
            savedEnabled = PropertyManager.GetBool("weapon_mods_enabled").Item;
            savedRollerEnabled = PropertyManager.GetBool("salvage_roller_enabled").Item;
            savedScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("salvage_roller_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));

            savedGuarantee = PropertyManager.GetBool("weapon_mod_guarantee_damage_special").Item;
            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mod_guarantee_damage_special",
                DefaultPropertyManager.DefaultBooleanProperties["weapon_mod_guarantee_damage_special"].Item));

            foreach (var key in RollDoubleKeys)
            {
                savedRollDoubles[key] = PropertyManager.GetDouble(key).Item;
                Assert.IsTrue(PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item), key);
            }
        }

        [TestCleanup]
        public void Cleanup()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", savedEnabled);
            PropertyManager.ModifyBool("salvage_roller_enabled", savedRollerEnabled);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", savedScale);
            PropertyManager.ModifyBool("weapon_mod_guarantee_damage_special", savedGuarantee);

            foreach (var pair in savedRollDoubles)
                PropertyManager.ModifyDouble(pair.Key, pair.Value);

            savedRollDoubles.Clear();
        }

        private static WorldObject Gem()
        {
            var gem = WeaponModTestKit.MakeWeapon(ACE.Entity.Enum.ItemType.Gem, null, workmanship: null);
            gem.SetProperty(ACE.Entity.Enum.Properties.PropertyInt.ParagonModGemModifier, (int)WeaponModId.Devastation);
            return gem;
        }

        private static void Max(WorldObject item)
        {
            foreach (var id in new[] { WeaponModId.Devastation, WeaponModId.WeakPoint, WeaponModId.Bloodthirst })
            {
                Assert.IsTrue(WeaponModRegistry.TryGet(id, out var row));
                WeaponModTinkerSet.ApplySpecialAtFraction(item, row, 1.0, 1.0);
            }
        }

        [TestMethod]
        public void Paragon_GemAndReset_RefuseShieldAsNotAWeapon()
        {
            Assert.AreEqual(R.NotAWeapon, ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Shield, null, null, null, 0, null, null, true));
            Assert.AreEqual(R.NotParagonSet, ParagonModManager.ResolveGemRefusal(true, true, true, WeaponClass.Melee, null, null, null, 0, null, null, true)); // control
            Assert.AreEqual(R.NotAWeapon, ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Shield, null, 0, 0, null, false));
            Assert.AreEqual(R.NotParagonSet, ParagonModManager.ResolveResetRefusal(true, true, true, WeaponClass.Melee, null, 0, 0, null, false)); // control
        }

        [TestMethod]
        public void Paragon_EvaluateWith_RealShield_IsNotAWeapon()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);

            Assert.AreEqual(WeaponClass.Shield, WeaponClassifier.Classify(shield), "fixture: a real shield");
            Assert.AreEqual(R.NotAWeapon,
                ParagonModManager.EvaluateWith(null, ParagonModManager.ParagonModAction.ApplyGem, Gem(), shield, true, true, true));
        }

        [TestMethod]
        public void SalvageRoller_AcceptsShieldAndSword()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
            var sword = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);
            var rock = WeaponModTestKit.MakeWeapon(ACE.Entity.Enum.ItemType.Misc, null);

            foreach (var action in new[] { SalvageRollerAction.TourmalineReroll, SalvageRollerAction.AmethystSwap })
            {
                Assert.IsNull(SalvageRollerEligibility.TargetStateRefusal(action, "Tourmaline Hammer", shield, out _), $"{action} on a shield");
                Assert.IsNull(SalvageRollerEligibility.TargetStateRefusal(action, "Tourmaline Hammer", sword, out _), $"{action} on a sword");
                Assert.IsNotNull(SalvageRollerEligibility.TargetStateRefusal(action, "Tourmaline Hammer", rock, out _), $"{action} on a non-weapon (control)");
            }
        }

        [TestMethod]
        public void SalvageRollerEngine_RollsShield_FromTheShieldPool_WithNoQualityTier()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);

            foreach (var action in new[] { SalvageRollerAction.TourmalineReroll, SalvageRollerAction.AmethystSwap })
            {
                var candidates = SalvageRollerEngine.Roll(shield, action, 5);

                Assert.AreEqual(5, candidates.Count, $"{action}: one candidate per roll");

                foreach (var c in candidates)
                {
                    Assert.AreEqual((int)WeaponQualityTier.None, c.Summary.Tier, $"{action} #{c.Seq}: a shield never earns a quality tier");
                    Assert.IsTrue(c.Summary.Total > 0, $"{action} #{c.Seq}: a special was drawn");
                }
            }
        }

        [TestMethod]
        public void SalvageRollerSummary_MaxedShield_HasNoTier_SwordControlDoes()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
            var sword = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            Max(shield);
            Max(sword);

            var shieldSummary = SalvageRollerEngine.WeaponSummary(shield);
            var swordSummary = SalvageRollerEngine.WeaponSummary(sword);

            Assert.IsTrue(shieldSummary.Total >= 300, "fixture: the shield's total reaches the Exceptional threshold");
            Assert.AreEqual((int)WeaponQualityTier.None, shieldSummary.Tier);
            Assert.AreEqual((int)WeaponQualityTier.Exceptional, swordSummary.Tier);
        }

        [TestMethod]
        public void QualityTier_ShieldNeverEarnsAnAura_SwordControlDoes()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
            var sword = WeaponModTestKit.MakeUntinkered(WeaponClass.Melee);

            Max(shield);
            Max(sword);

            Assert.AreEqual(300, WeaponQualityTiers.TotalIntensity(sword));
            Assert.AreEqual(300, WeaponQualityTiers.TotalIntensity(shield));
            Assert.AreEqual(WeaponQualityTier.Exceptional, WeaponQualityTiers.Evaluate(sword));
            Assert.AreEqual(WeaponQualityTier.None, WeaponQualityTiers.Evaluate(shield));
        }
    }
}
