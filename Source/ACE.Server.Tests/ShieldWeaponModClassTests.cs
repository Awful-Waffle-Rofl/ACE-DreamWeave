using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.Managers;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using static ACE.Server.WeaponMods.WeaponModId;
using static ACE.Server.WeaponMods.WeaponModRegistry;

namespace ACE.Server.Tests
{
    /// <summary>
    /// WeaponClass.Shield: the classifier, the six-row shield pool, and both crafting flows accepting a shield.
    /// Shield is deliberately outside WeaponClass.All; six rows opt in with an explicit | Shield.
    /// </summary>
    [TestClass]
    public class ShieldWeaponModClassTests
    {
        private static readonly WeaponModId[] ShieldRows = { Devastation, WeakPoint, Bloodthirst, Heft, Focus, Execution };

        // The four chance keys are read by WeaponModRoller; weapon_mod_swap_special_chance has no reader and is seeded
        // defensively. All are set to their PropertyManager defaults so the class passes alone and in any order.
        private static readonly (string Key, double Default)[] RollerKeys =
        {
            ("weapon_mod_special_chance_1", 1.00),
            ("weapon_mod_special_chance_2", 0.60),
            ("weapon_mod_special_chance_3", 0.30),
            ("weapon_mod_special_chance_4", 0.10),
            ("weapon_mod_swap_special_chance", 0.35),
        };

        private bool priorEnabled;
        private double priorScale;
        private double[] priorRollerValues;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
        }

        [TestInitialize]
        public void Seed()
        {
            priorEnabled = PropertyManager.GetBool("weapon_mods_enabled").Item;
            priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;
            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);

            priorRollerValues = RollerKeys.Select(k => PropertyManager.GetDouble(k.Key).Item).ToArray();

            foreach (var (key, value) in RollerKeys)
                PropertyManager.ModifyDouble(key, value);
        }

        [TestCleanup]
        public void Restore()
        {
            PropertyManager.ModifyBool("weapon_mods_enabled", priorEnabled);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);

            for (var i = 0; i < RollerKeys.Length; i++)
                PropertyManager.ModifyDouble(RollerKeys[i].Key, priorRollerValues[i]);
        }

        private static WeaponModId[] Ids(IReadOnlyList<WeaponModDefinition> pool) => pool.Select(m => m.Id).ToArray();

        private static void SetTinkers(WorldObject shield, int count)
        {
            var log = Enumerable.Repeat(MaterialType.Steel, count).ToList();
            shield.SetProperty(PropertyString.TinkerLog, WeaponModTinkerSet.SerializeLog(log));
            shield.SetProperty(PropertyInt.NumTimesTinkered, count);
        }

        private static WeaponModManager.WeaponModRefusal Refusal(WeaponModManager.WeaponModAction action, WorldObject w) =>
            WeaponModManager.ResolveRefusal(action, w);

        [TestMethod]
        public void Classify_ShieldCombatUse_IsShield()
        {
            Assert.AreEqual(WeaponClass.Shield, WeaponClassifier.Classify(ItemType.Armor, CombatUse.Shield));
            Assert.AreEqual(WeaponClass.Shield, WeaponClassifier.Classify(ItemType.MeleeWeapon, CombatUse.Shield));
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.MissileWeapon, CombatUse.Ammo));
            Assert.AreEqual(WeaponClass.None, WeaponClassifier.Classify(ItemType.Armor, null));
        }

        [TestMethod]
        public void All_ExcludesShield()
        {
            Assert.AreEqual(8, (int)WeaponClass.Shield);
            Assert.AreEqual(WeaponClass.Melee | WeaponClass.Missile | WeaponClass.Caster, WeaponClass.All);
            Assert.AreEqual(WeaponClass.None, WeaponClass.All & WeaponClass.Shield);
        }

        [TestMethod]
        public void ShieldPool_IsExactlyTheApprovedList()
        {
            CollectionAssert.AreEqual(ShieldRows, Ids(Pool(WeaponClass.Shield, true)));
            CollectionAssert.AreEqual(ShieldRows.Take(3).ToArray(), Ids(Pool(WeaponClass.Shield, false)));
            CollectionAssert.AreEqual(ShieldRows, Ids(DamagePool(WeaponClass.Shield, true)));
            Assert.AreEqual(3, DamagePool(WeaponClass.Shield, false).Count);

            foreach (var m in AllMods)
                Assert.AreEqual(ShieldRows.Contains(m.Id), m.AppliesTo(WeaponClass.Shield), m.Id.ToString());

            Assert.AreEqual(0, WeaponTinkerTable.Pool(WeaponClass.Shield).Count);
            Assert.AreEqual(1.0, DefensiveWeaponMath.CeilingFactor(WeaponClass.Shield, false), 1e-12);
        }

        [TestMethod]
        public void WeaponPools_AreUnchanged_GoldenFromOriginMaster()
        {
            // literal table-order ids copied from origin/master 80eea0e6a WeaponModRegistry.cs:268-779
            CollectionAssert.AreEqual(new[] { Devastation, WeakPoint, Bloodthirst, ShieldBypass, Ambush, Quickening, SecondWind, Heft, Leverage, Focus, Execution, Efficiency, Recovery, ManaWell, Cleanse, Siphon, DefensiveWeapon }, Ids(Pool(WeaponClass.Melee, true)));
            CollectionAssert.AreEqual(new[] { Devastation, WeakPoint, Bloodthirst, ShieldBypass, Ambush, Quickening, SecondWind, Heft, Tension, Focus, Execution, Efficiency, Recovery, ManaWell, Cleanse, Siphon, DefensiveWeapon, PanicReload }, Ids(Pool(WeaponClass.Missile, true)));
            CollectionAssert.AreEqual(new[] { Devastation, WeakPoint, Bloodthirst, Ambush, SecondWind, Attunement, Focus, Execution, Efficiency, Recovery, ManaWell, Cleanse, Longevity, Siphon, QuickRefresh, DefensiveWeapon }, Ids(Pool(WeaponClass.Caster, true)));
        }

        [TestMethod]
        public void Shield_AcceptedByBothFlows_RefusedWithoutWorkmanship()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Reroll, shield));
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Swap, shield));

            shield.RemoveProperty(PropertyInt.ItemWorkmanship);
            Assert.AreEqual(WeaponModManager.WeaponModRefusal.NoWorkmanship, Refusal(WeaponModManager.WeaponModAction.Reroll, shield));
        }

        [TestMethod]
        public void Reroll_OnShield_DrawsOnlyShieldRows_AndNeverTouchesTinkers()
        {
            try
            {
                ThreadSafeRandom.Reseed(20261125);

                var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
                SetTinkers(shield, 2);
                var log = shield.GetProperty(PropertyString.TinkerLog);
                var armor = shield.GetProperty(PropertyInt.ArmorLevel);
                var sawAny = false;

                for (var i = 0; i < 400; i++)
                {
                    if (i % 2 == 0)
                        WeaponModManager.ApplyReroll(shield, WeaponClass.Shield, 10);
                    else
                        WeaponModManager.ApplySwap(shield, WeaponClass.Shield, 10);

                    foreach (var (definition, _) in WeaponModTinkerSet.ReadSpecials(shield))
                    {
                        sawAny = true;
                        Assert.IsTrue(ShieldRows.Contains(definition.Id), $"pass {i}: {definition.Id} is not a shield row");
                    }

                    Assert.IsTrue(WeaponModTinkerSet.SpecialCount(shield) <= MaxSpecials, $"pass {i}: over MaxSpecials");
                    Assert.AreEqual(log, shield.GetProperty(PropertyString.TinkerLog), $"pass {i}: TinkerLog");
                    Assert.AreEqual(2, shield.GetProperty(PropertyInt.NumTimesTinkered), $"pass {i}: NumTimesTinkered");
                    Assert.AreEqual(armor, shield.GetProperty(PropertyInt.ArmorLevel), $"pass {i}: ArmorLevel");
                }

                Assert.IsTrue(sawAny, "no special was ever drawn, so the row-membership loop proved nothing");
            }
            finally
            {
                ThreadSafeRandom.Reseed(Environment.TickCount);
            }
        }

        [TestMethod]
        public void ArmorTinkeredShield_BothFlowsAccepted_TinkersUntouched()
        {
            try
            {
                ThreadSafeRandom.Reseed(20261126);

                var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
                SetTinkers(shield, 10);
                var log = shield.GetProperty(PropertyString.TinkerLog);
                var armor = shield.GetProperty(PropertyInt.ArmorLevel);

                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Reroll, shield));
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Swap, shield));

                WeaponModManager.ApplyReroll(shield, WeaponClass.Shield, 10);
                WeaponModManager.ApplySwap(shield, WeaponClass.Shield, 10);

                Assert.AreEqual(log, shield.GetProperty(PropertyString.TinkerLog));
                Assert.AreEqual(10, shield.GetProperty(PropertyInt.NumTimesTinkered));
                Assert.AreEqual(armor, shield.GetProperty(PropertyInt.ArmorLevel));
            }
            finally
            {
                ThreadSafeRandom.Reseed(Environment.TickCount);
            }
        }

        [TestMethod]
        public void ImbuedShield_AcceptedByBothFlows()
        {
            var plain = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield, imbuedEffect: 0x20000000);

            var crit = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield, imbuedEffect: (int)ImbuedEffectType.CriticalStrike);
            crit.SetProperty(PropertyString.TinkerLog, WeaponModTinkerSet.SerializeLog(new List<MaterialType> { MaterialType.BlackOpal }));
            crit.SetProperty(PropertyInt.NumTimesTinkered, 1);

            foreach (var shield in new[] { plain, crit })
            {
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Reroll, shield));
                Assert.AreEqual(WeaponModManager.WeaponModRefusal.None, Refusal(WeaponModManager.WeaponModAction.Swap, shield));
            }
        }

        [TestMethod]
        public void AppraisalLines_ShowAShieldsSpecials()
        {
            var shield = WeaponModTestKit.MakeUntinkered(WeaponClass.Shield);
            WeaponModTinkerSet.ApplySpecialAtFraction(shield, Get(Heft), 0.5, 1.0);

            StringAssert.Contains(string.Join("|", WeaponModDisplay.GetAppraisalLines(shield)), "+11 damage");
        }
    }
}
