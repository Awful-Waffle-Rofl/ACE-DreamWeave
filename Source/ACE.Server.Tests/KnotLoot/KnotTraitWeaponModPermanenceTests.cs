using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// A Knot trait is permanent: neither weapon-mod salvage path, Tourmaline (WeaponModManager.ApplyReroll) nor Amethyst
    /// (WeaponModManager.ApplySwap), may reroll or remove it. Both paths write only WeaponModRegistry.AllMods records and
    /// their Tier A natives (WeaponModTinkerSet.ClearSpecials / ReverseSpecial / ApplySpecialAtFraction), so the trait
    /// position (PropertyFloat 9020 and siblings), KnotLootLevel (PropertyInt 9093) and KnotLootTable (PropertyString
    /// 9029) are out of their reach.
    /// </summary>
    [TestClass]
    public class KnotTraitWeaponModPermanenceTests
    {
        private static uint nextGuid = 0x7F1E0000;

        private static readonly PropertyFloat[] KnotTraitFloats = KnotTraitRegistry.All.Select(d => d.Property).ToArray();

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            // seeds every server tunable the reroll and swap read (the special-count chances, the class pools' gates)
            // from the hardcoded defaults, the same seeding BluespireWeaponFixedModsTests uses - no database
            DefaultPropertyManager.LoadDefaultProperties();
        }

        /// <summary>Runs a body with the weapon-mod gate ON and the magnitude scale at 1.0, restoring both whatever happens.</summary>
        private static void WithModsLive(Action body)
        {
            var priorGate = PropertyManager.GetBool("weapon_mods_enabled").Item;
            var priorScale = PropertyManager.GetDouble("weapon_mod_magnitude_scale").Item;

            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);

            try
            {
                body();
            }
            finally
            {
                PropertyManager.ModifyBool("weapon_mods_enabled", priorGate);
                PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", priorScale);
            }
        }

        // ---------------- structural ----------------

        [TestMethod]
        public void NoWeaponModRecordOrNative_TargetsAKnotStamp()
        {
            Assert.AreEqual(5, KnotTraitFloats.Length, "control: the trait list is the full registry, 9020 included");
            CollectionAssert.Contains(KnotTraitFloats, PropertyFloat.KnotTraitOverpoweringIntent);

            foreach (var d in WeaponModRegistry.AllMods)
            {
                CollectionAssert.DoesNotContain(KnotTraitFloats, d.Record, $"{d.Id} record");

                if (d.NativeFloat.HasValue)
                    CollectionAssert.DoesNotContain(KnotTraitFloats, d.NativeFloat.Value, $"{d.Id} native float");

                if (d.NativeInt.HasValue)
                    Assert.AreNotEqual(PropertyInt.KnotLootLevel, d.NativeInt.Value, $"{d.Id} native int");
            }

            // a weapon-mod row can address only a PropertyInt or a PropertyFloat, so it can never write the table string
            var stringFields = typeof(WeaponModDefinition)
                .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Where(f => f.FieldType == typeof(PropertyString) || f.FieldType == typeof(PropertyString?))
                .Select(f => f.Name)
                .ToList();

            Assert.AreEqual(0, stringFields.Count, "WeaponModDefinition gained a PropertyString field: " + string.Join(", ", stringFields));
        }

        [TestMethod]
        public void RerollAndSwap_WriteOnlyThroughTheSpecialsHelpers()
        {
            var file = Code(PooledLootSourceText.Read("Source/ACE.Server/WeaponMods/WeaponModManager.cs"));

            foreach (var signature in new[]
            {
                "public static List<string> ApplyReroll(WorldObject target, WeaponClass weaponClass, double workmanship)",
                "public static List<string> ApplySwap(WorldObject target, WeaponClass weaponClass, double workmanship)",
            })
            {
                var body = PooledLootSourceText.MethodBody(file, signature);

                Assert.IsFalse(body.Contains("SetProperty(", StringComparison.Ordinal), signature + " writes a property directly");
                Assert.IsFalse(body.Contains("RemoveProperty(", StringComparison.Ordinal), signature + " removes a property directly");
                Assert.IsFalse(body.Contains("WriteComposition(", StringComparison.Ordinal), signature + " writes the tinker composition");
                Assert.IsFalse(body.Contains("KnotLoot", StringComparison.Ordinal), signature + " touches a Knot stamp");
            }
        }

        // ---------------- behavioural: both salvage paths ----------------

        [TestMethod]
        public void Tourmaline_Reroll_LeavesEveryKnotStampUnchanged() => RunPath(reroll: true);

        [TestMethod]
        public void Amethyst_Swap_LeavesEveryKnotStampUnchanged() => RunPath(reroll: false);

        private static void RunPath(bool reroll)
        {
            WithModsLive(() =>
            {
                var definition = WeaponModRegistry.AllMods.FirstOrDefault(d => d.AppliesTo(WeaponClass.Caster));
                Assert.IsNotNull(definition, "control: some weapon mod is eligible for a caster");

                for (var i = 0; i < 25; i++)
                {
                    var baton = Baton();
                    var weaponClass = WeaponClassifier.Classify(baton);
                    Assert.AreEqual(WeaponClass.Caster, weaponClass, "control: the baton classifies as a caster");

                    WeaponModTinkerSet.ApplySpecialAtFraction(baton, definition, 1.0, 1.0);
                    Assert.IsNotNull(baton.GetProperty(definition.Record), "control: the seeded weapon-mod record is present");

                    var before = KnotStamps(baton);
                    var resolvedBefore = KnotTraitValue.ResolveOn(baton, KnotTraitId.OverpoweringIntent, null);

                    var lines = reroll
                        ? WeaponModManager.ApplyReroll(baton, weaponClass, 10.0)
                        : WeaponModManager.ApplySwap(baton, weaponClass, 10.0);

                    Assert.IsNotNull(lines, "the salvage path bailed out");

                    // positive control: the path actually reworked the weapon-mod layer this iteration
                    if (!reroll)
                        CollectionAssert.Contains(lines, $"Lost: {definition.DisplayName}", "control: Amethyst removed the held special");

                    CollectionAssert.AreEqual(before, KnotStamps(baton), $"iteration {i}: a Knot stamp moved");
                    Assert.AreEqual(resolvedBefore, KnotTraitValue.ResolveOn(baton, KnotTraitId.OverpoweringIntent, null), $"iteration {i}: the resolved trait moved");
                }
            });
        }

        [TestMethod]
        public void ClearSpecials_RemovesTheModRecord_ButNotTheKnotStamps()
        {
            WithModsLive(() =>
            {
                var baton = Baton();
                var definition = WeaponModRegistry.AllMods.First(d => d.AppliesTo(WeaponClass.Caster));

                WeaponModTinkerSet.ApplySpecialAtFraction(baton, definition, 1.0, 1.0);
                var before = KnotStamps(baton);

                WeaponModTinkerSet.ClearSpecials(baton);

                Assert.IsNull(baton.GetProperty(definition.Record), "control: the clear removes the weapon-mod record");
                CollectionAssert.AreEqual(before, KnotStamps(baton));
            });
        }

        // ---------------- helpers ----------------

        private static WorldObject Baton()
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007604,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.ItemWorkmanship, 10 },
                    { PropertyInt.KnotLootLevel, 300 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitOverpoweringIntent, 0.4 } },
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Hive Nether Baton" },
                    { PropertyString.KnotLootTable, "matron" },
                },
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static string[] KnotStamps(WorldObject item)
        {
            var stamps = new List<string>
            {
                "int 9093 = " + item.GetProperty(PropertyInt.KnotLootLevel),
                "string 9029 = " + item.GetProperty(PropertyString.KnotLootTable),
            };

            foreach (var p in KnotTraitFloats)
                stamps.Add($"float {(int)p} = {item.GetProperty(p)?.ToString("R", System.Globalization.CultureInfo.InvariantCulture) ?? "absent"}");

            Assert.AreEqual("int 9093 = 300", stamps[0], "control: the level stamp is present");
            Assert.AreEqual("string 9029 = matron", stamps[1], "control: the table stamp is present");

            return stamps.ToArray();
        }

        private static string Code(string text) => Regex.Replace(text, @"//[^\n]*", "");
    }
}
