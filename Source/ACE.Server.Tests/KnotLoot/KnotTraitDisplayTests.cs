using System;
using System.Collections.Generic;
using System.IO;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    [TestClass]
    public class KnotTraitDisplayTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_cast_speed_enabled", "knot_trait_shield_trade_enabled", "knot_trait_frugal_hand_enabled", "knot_trait_wrist_trade_enabled", "knot_trait_overpowering_intent_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
            "knot_loot_matron_window_low_level", "knot_loot_matron_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_loot_matron_window_low_start", "knot_loot_matron_window_high_start", "knot_loot_matron_window_width",
            "knot_trait_cast_speed_min", "knot_trait_cast_speed_max",
            "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max", "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max",
            "knot_trait_frugal_hand_min", "knot_trait_frugal_hand_max",
            "knot_trait_wrist_trade_gain_min", "knot_trait_wrist_trade_gain_max", "knot_trait_wrist_trade_cost_min", "knot_trait_wrist_trade_cost_max",
            "knot_trait_overpowering_intent_min", "knot_trait_overpowering_intent_max",
        };

        private static uint nextGuid = 0x7F200000;

        [TestInitialize]
        public void Seed() => RestoreDefaults();

        [TestCleanup]
        public void Cleanup() => RestoreDefaults();

        private static void RestoreDefaults()
        {
            foreach (var k in BoolKeys)
                Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item), k);

            foreach (var k in LongKeys)
                Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item), k);

            foreach (var k in DoubleKeys)
                Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item), k);
        }

        private static KnotTraitDefinition Def(KnotTraitId id)
        {
            Assert.IsTrue(KnotTraitRegistry.TryGet(id, out var d), id.ToString());
            return d;
        }

        private static WorldObject CreateItem(Dictionary<PropertyFloat, double> floats)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007451,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.KnotLootLevel, 300 },
                },
                PropertiesFloat = floats,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "grael" } },
            };

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        [TestMethod]
        public void Describe_Shield_P50() =>
            Assert.AreEqual("Rose's Bargain [50%]: +313 shield armor, -63 maximum health",
                KnotTraitDisplay.Describe(Def(KnotTraitId.ShieldTrade), new KnotTraitEffect(true, 0.5, 312.5, 62.5)));

        [TestMethod]
        public void Describe_Wrist_ShowsBothWrists() =>
            Assert.AreEqual("Glass Turtle [50%]: left wrist +8 damage resist rating, -15 damage rating; right wrist +8 damage rating, -15 damage resist rating",
                KnotTraitDisplay.Describe(Def(KnotTraitId.WristTrade), new KnotTraitEffect(true, 0.5, 7.5, 15)));

        [TestMethod]
        public void Describe_CastSpeed_Top() =>
            Assert.AreEqual("Mirrorglass Haste [100%]: +200% buff cast speed",
                KnotTraitDisplay.Describe(Def(KnotTraitId.CastSpeed), new KnotTraitEffect(true, 1.0, 2.0, 0)));

        [TestMethod]
        public void Describe_FrugalHand_FractionalPercent() =>
            Assert.AreEqual("Frugal Hand [25%]: -12.5% stamina and mana usage",
                KnotTraitDisplay.Describe(Def(KnotTraitId.FrugalHand), new KnotTraitEffect(true, 0.25, 0.125, 0)));

        [TestMethod]
        public void Describe_OverpoweringIntent_FloorPeakAndFraction()
        {
            var d = Def(KnotTraitId.OverpoweringIntent);

            Assert.AreEqual("Overpowering Intent [0%]: +5% War and Void Magic skill against monster resistance",
                KnotTraitDisplay.Describe(d, new KnotTraitEffect(true, 0.0, 0.05, 0)));
            Assert.AreEqual("Overpowering Intent [100%]: +10% War and Void Magic skill against monster resistance",
                KnotTraitDisplay.Describe(d, new KnotTraitEffect(true, 1.0, 0.10, 0)));
            Assert.AreEqual("Overpowering Intent [65%]: +8.3% War and Void Magic skill against monster resistance",
                KnotTraitDisplay.Describe(d, new KnotTraitEffect(true, 0.65, 0.0825, 0)));
        }

        [TestMethod]
        public void Lines_Baton_ShowsOverpoweringIntent()
        {
            // a Matron baton: u = 1.0 at level 300 on the Matron window (same shape as Grael): p = 0.65, X = 0.05 + 0.65 * 0.05 = 0.0825
            var weenie = new Weenie
            {
                WeenieClassId = 1007603,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.KnotLootLevel, 300 },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitOverpoweringIntent, 1.0 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "matron" } },
            };

            var baton = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            var lines = KnotTraitDisplay.GetAppraisalLines(baton);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("- Overpowering Intent [65%]: +8.3% War and Void Magic skill against monster resistance", lines[0]);
        }

        [TestMethod]
        public void Describe_PercentClampedTo0To100()
        {
            StringAssert.StartsWith(KnotTraitDisplay.Describe(Def(KnotTraitId.CastSpeed), new KnotTraitEffect(true, 1.4, 2.0, 0)), "Mirrorglass Haste [100%]:");
            StringAssert.StartsWith(KnotTraitDisplay.Describe(Def(KnotTraitId.CastSpeed), new KnotTraitEffect(true, -0.2, 1.0, 0)), "Mirrorglass Haste [0%]:");
        }

        [TestMethod]
        public void Lines_PresentTrait_HasDashPrefix()
        {
            // u = 1.0 at level 300: p = end of the window = 0.65, an exact-looking value that avoids a .5 rounding edge.
            var item = CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitShieldTrade, 1.0 } });

            var lines = KnotTraitDisplay.GetAppraisalLines(item);

            Assert.AreEqual(1, lines.Count);
            Assert.AreEqual("- Rose's Bargain [65%]: +369 shield armor, -74 maximum health", lines[0]);
        }

        [TestMethod]
        public void Lines_NoTrait_Empty()
        {
            Assert.AreEqual(0, KnotTraitDisplay.GetAppraisalLines(CreateItem(new Dictionary<PropertyFloat, double>())).Count);
            Assert.AreEqual(0, KnotTraitDisplay.GetAppraisalLines(null).Count);
        }

        [TestMethod]
        public void Lines_MechanicOff_Omitted()
        {
            var item = CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitFrugalHand, 0.5 } });
            Assert.AreEqual(1, KnotTraitDisplay.GetAppraisalLines(item).Count, "control: enabled mechanic shows its line");

            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_frugal_hand_enabled", false));
            Assert.AreEqual(0, KnotTraitDisplay.GetAppraisalLines(item).Count);
        }

        [TestMethod]
        public void AppraiseInfo_AppendsKnotLinesAfterWeaponMods()
        {
            var text = SourceFile("Source/ACE.Server/Network/Structure/AppraiseInfo.cs");

            var weapon = text.IndexOf("WeaponModDisplay.GetAppraisalLines(wo)", StringComparison.Ordinal);
            var knot = text.IndexOf("KnotTraitDisplay.GetAppraisalLines(wo)", StringComparison.Ordinal);

            Assert.IsTrue(weapon >= 0, "weapon-mod block missing");
            Assert.IsTrue(knot > weapon, "Knot block must follow the weapon-mod block");
        }

        private static string SourceFile(string repoRelativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, repoRelativePath);

                if (File.Exists(candidate))
                    return File.ReadAllText(candidate);
            }

            Assert.Fail($"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }
    }
}
