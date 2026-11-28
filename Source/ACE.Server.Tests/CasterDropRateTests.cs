using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Server.Factories.Entity;
using ACE.Server.Factories.Enum;
using ACE.Server.Factories.Tables;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Pins the 2026-11-25 caster drop-rate change: caster share of RetailChances raised 7% -> 8.35%, and
    /// Weapon x1.015 (taken from Scroll) in six scroll-bearing item-type profiles, so absolute caster drops
    /// are ~x1.21 while every non-caster weapon type keeps its absolute rate.
    ///
    /// ChanceTable.VerifyTable only log.Errors a bad sum, so the sum assertions use the same decimal
    /// accumulation and 1e-7 threshold to catch what the runtime would silently absorb.
    /// </summary>
    [TestClass]
    public class CasterDropRateTests
    {
        private const decimal Threshold = 0.0000001M;

        private static ChanceTable<T> GetTable<T>(Type owner, string name)
        {
            var field = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Static);
            Assert.IsNotNull(field, $"{owner.Name}.{name} not found - the field was renamed or removed.");
            return (ChanceTable<T>)field.GetValue(null);
        }

        private static decimal Sum<T>(ChanceTable<T> table) => table.Aggregate(0.0M, (acc, e) => acc + (decimal)e.chance);

        private static decimal Weight<T>(ChanceTable<T> table, T key) => (decimal)table.Single(e => EqualityComparer<T>.Default.Equals(e.result, key)).chance;

        private static ChanceTable<TreasureWeaponType> Retail() => GetTable<TreasureWeaponType>(typeof(WeaponTypeChance), "RetailChances");

        [TestMethod]
        public void RetailChances_PinnedWeights_AndSumToOne()
        {
            var t = Retail();
            foreach (var type in new[] { TreasureWeaponType.Sword, TreasureWeaponType.Mace, TreasureWeaponType.Axe, TreasureWeaponType.Spear,
                                         TreasureWeaponType.Unarmed, TreasureWeaponType.Staff, TreasureWeaponType.Dagger })
                Assert.AreEqual(0.0887M, Weight(t, type), type.ToString());

            Assert.AreEqual(0.069M, Weight(t, TreasureWeaponType.Bow));
            Assert.AreEqual(0.069M, Weight(t, TreasureWeaponType.Crossbow));
            Assert.AreEqual(0.0591M, Weight(t, TreasureWeaponType.Atlatl));
            Assert.AreEqual(0.0835M, Weight(t, TreasureWeaponType.Caster));
            Assert.AreEqual(0.0985M, Weight(t, TreasureWeaponType.TwoHandedWeapon));
            Assert.AreEqual(12, t.Count);
            Assert.IsTrue(Math.Abs(1.0M - Sum(t)) <= Threshold, $"RetailChances sums to {Sum(t)}");
        }

        public static IEnumerable<object[]> Profiles()
        {
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile8", 0.15225M, 0.13775M };
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile9", 0.3045M, 0.0955M };
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile10", 0.3045M, 0.1805M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile8", 0.126875M, 0.123125M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile9", 0.203M, 0.197M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile10", 0.3045M, 0.1955M };
        }

        [TestMethod]
        [DynamicData(nameof(Profiles))]
        public void EditedProfile_WeaponAndScrollWeights_AndSumToOne(Type owner, string name, decimal weapon, decimal scroll)
        {
            var t = GetTable<TreasureItemType>(owner, name);
            Assert.AreEqual(weapon, Weight(t, TreasureItemType.Weapon), $"{name} Weapon");
            Assert.AreEqual(scroll, Weight(t, TreasureItemType.Scroll), $"{name} Scroll");
            Assert.IsTrue(Math.Abs(1.0M - Sum(t)) <= Threshold, $"{name} sums to {Sum(t)}, expected 1.0");
        }

        private static readonly Dictionary<TreasureWeaponType, decimal> OldWeights = new Dictionary<TreasureWeaponType, decimal>
        {
            { TreasureWeaponType.Sword, 0.09M }, { TreasureWeaponType.Mace, 0.09M }, { TreasureWeaponType.Axe, 0.09M },
            { TreasureWeaponType.Spear, 0.09M }, { TreasureWeaponType.Unarmed, 0.09M }, { TreasureWeaponType.Staff, 0.09M },
            { TreasureWeaponType.Dagger, 0.09M }, { TreasureWeaponType.Bow, 0.07M }, { TreasureWeaponType.Crossbow, 0.07M },
            { TreasureWeaponType.Atlatl, 0.06M }, { TreasureWeaponType.Caster, 0.07M }, { TreasureWeaponType.TwoHandedWeapon, 0.10M },
        };

        public static IEnumerable<object[]> ProfilesWithOldWeapon()
        {
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile8", 0.15M };
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile9", 0.30M };
            yield return new object[] { typeof(TreasureProfile_MagicItem), "magicItemProfile10", 0.30M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile8", 0.125M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile9", 0.20M };
            yield return new object[] { typeof(TreasureProfile_Item), "itemProfile10", 0.30M };
        }

        [TestMethod]
        [DynamicData(nameof(ProfilesWithOldWeapon))]
        public void EditedProfile_AbsoluteRates_CasterUp21Percent_NonCastersUnchanged(Type owner, string name, decimal oldWeapon)
        {
            var newWeapon = Weight(GetTable<TreasureItemType>(owner, name), TreasureItemType.Weapon);
            var retail = Retail();

            foreach (var kv in OldWeights)
            {
                var ratio = newWeapon * Weight(retail, kv.Key) / (oldWeapon * kv.Value);

                if (kv.Key == TreasureWeaponType.Caster)
                    Assert.IsTrue(ratio >= 1.20M && ratio <= 1.22M, $"{name} Caster absolute-rate ratio {ratio}");
                else
                    Assert.IsTrue(Math.Abs(ratio - 1M) <= 0.005M, $"{name} {kv.Key} absolute-rate ratio {ratio}");
            }
        }

        [TestMethod]
        public void MagicItemProfile22_ThreadCache_IsUnchanged()
        {
            var t = GetTable<TreasureItemType>(typeof(TreasureProfile_MagicItem), "magicItemProfile22");
            Assert.AreEqual(0.35M, Weight(t, TreasureItemType.Weapon));
            Assert.IsFalse(t.Any(e => e.result == TreasureItemType.Scroll), "magicItemProfile22 must have no Scroll entry");
        }
    }
}
