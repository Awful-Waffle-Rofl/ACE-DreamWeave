using System;
using System.IO;
using System.Linq;

using ACE.Server.KnotLoot;
using ACE.Server.Managers;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    [TestClass]
    public class KnotLootTunablesTests
    {
        private static readonly string[] Traits = { "cast_speed", "shield_trade", "frugal_hand", "wrist_trade", "overpowering_intent" };

        private static readonly string[] BoolKeys =
        {
            "knot_loot_grael_enabled", "knot_loot_matron_enabled",
            "knot_trait_cast_speed_enabled", "knot_trait_shield_trade_enabled", "knot_trait_frugal_hand_enabled", "knot_trait_wrist_trade_enabled", "knot_trait_overpowering_intent_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_weight_elemental_infusion", "knot_loot_grael_weight_armor_infusion", "knot_loot_grael_weight_crippling_infusion",
            "knot_loot_grael_weight_mirrorglass_orb", "knot_loot_grael_weight_shield_of_the_rose", "knot_loot_grael_weight_frugal_gauntlets", "knot_loot_grael_weight_glass_turtle_bracelet",
            "knot_loot_grael_mmd_min", "knot_loot_grael_mmd_max", "knot_loot_grael_note_min", "knot_loot_grael_note_max",
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
            "knot_loot_matron_weight_elemental_infusion", "knot_loot_matron_weight_armor_infusion", "knot_loot_matron_weight_crippling_infusion", "knot_loot_matron_weight_voidclaw",
            "knot_loot_matron_weight_spines", "knot_loot_matron_weight_torc", "knot_loot_matron_weight_hive_stinger_baton", "knot_loot_matron_weight_hive_nether_baton",
            "knot_loot_matron_mmd_min", "knot_loot_matron_mmd_max", "knot_loot_matron_note_min", "knot_loot_matron_note_max",
            "knot_loot_matron_window_low_level", "knot_loot_matron_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_loot_matron_chance_spines",
            "knot_loot_matron_window_low_start", "knot_loot_matron_window_high_start", "knot_loot_matron_window_width",
            "knot_trait_cast_speed_min", "knot_trait_cast_speed_max",
            "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max", "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max",
            "knot_trait_frugal_hand_min", "knot_trait_frugal_hand_max",
            "knot_trait_wrist_trade_gain_min", "knot_trait_wrist_trade_gain_max", "knot_trait_wrist_trade_cost_min", "knot_trait_wrist_trade_cost_max",
            "knot_trait_overpowering_intent_min", "knot_trait_overpowering_intent_max",
        };

        [TestInitialize]
        public void Seed() => RestoreDefaults();

        [TestCleanup]
        public void Cleanup() => RestoreDefaults();

        private static void RestoreDefaults()
        {
            foreach (var k in BoolKeys)
                PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item);

            foreach (var k in LongKeys)
                PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item);

            foreach (var k in DoubleKeys)
                PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item);
        }

        [TestMethod]
        public void Defaults_AreRegistered_AndOn()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["knot_loot_grael_enabled"].Item);

            foreach (var k in Traits)
                Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties[$"knot_trait_{k}_enabled"].Item, k);

            Assert.AreEqual(40L, DefaultPropertyManager.DefaultLongProperties["knot_loot_grael_weight_mirrorglass_orb"].Item);
            Assert.AreEqual(0.7, DefaultPropertyManager.DefaultDoubleProperties["knot_loot_grael_window_high_start"].Item, 1e-9);
            Assert.AreEqual(500.0, DefaultPropertyManager.DefaultDoubleProperties["knot_trait_shield_trade_armor_max"].Item, 1e-9);
        }

        [TestMethod]
        public void EveryKey_IsInDefaultsAndMetadataSnapshots()
        {
            Assert.AreEqual(55, BoolKeys.Length + LongKeys.Length + DoubleKeys.Length);

            var defaults = Source("Source/config-defaults.tsv");
            var meta = Source("Source/ACE.Server/config-metadata.tsv");
            var failures = new System.Collections.Generic.List<string>();

            void Check(string key, string type, string value)
            {
                // the default column only: a key whose default moved carries its introduced default and migration after it
                if (!defaults.Contains($"\n{key}\t{type}\t{value}\t"))
                    failures.Add("defaults row missing or wrong: " + key);

                if (!meta.Contains($"\n{key}\t"))
                    failures.Add("metadata row missing: " + key);
            }

            foreach (var k in BoolKeys)
                Check(k, "bool", DefaultPropertyManager.DefaultBooleanProperties[k].Item ? "true" : "false");

            foreach (var k in LongKeys)
                Check(k, "long", DefaultPropertyManager.DefaultLongProperties[k].Item.ToString(System.Globalization.CultureInfo.InvariantCulture));

            foreach (var k in DoubleKeys)
                Check(k, "double", DefaultPropertyManager.DefaultDoubleProperties[k].Item.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

            Assert.AreEqual(0, failures.Count, string.Join(" | ", failures));
        }

        [TestMethod]
        public void Window_Defaults_ReadAsTheDefault() => Assert.AreEqual(KnotWindowSettings.Default, KnotLootTunables.Window("grael"));

        [TestMethod]
        public void Window_IsReadLive()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_grael_window_width", 0.5));
            Assert.AreEqual(0.5, KnotLootTunables.Window("grael").Width, 1e-9);
        }

        [TestMethod]
        public void Weight_Currency_AndSwitches_ReadLive()
        {
            Assert.AreEqual(40L, KnotLootTunables.Weight("grael", "mirrorglass_orb", 999));
            Assert.IsTrue(PropertyManager.ModifyLong("knot_loot_grael_weight_mirrorglass_orb", 7));
            Assert.AreEqual(7L, KnotLootTunables.Weight("grael", "mirrorglass_orb", 999));

            Assert.AreEqual((25L, 50L), KnotLootTunables.Currency("grael", "mmd", (1, 2)));
            Assert.IsTrue(PropertyManager.ModifyLong("knot_loot_grael_mmd_min", 90));
            Assert.AreEqual((1L, 2L), KnotLootTunables.Currency("grael", "mmd", (1, 2)), "min above max reads as the fallback");

            Assert.IsTrue(KnotLootTunables.TableEnabled("grael"));
            Assert.IsTrue(PropertyManager.ModifyBool("knot_loot_grael_enabled", false));
            Assert.IsFalse(KnotLootTunables.TableEnabled("grael"));
        }

        [TestMethod]
        public void Chance_ReadsLive_ClampsToUnit_AndNaNFallsBack()
        {
            Assert.AreEqual(1.0, KnotLootTunables.Chance("matron", "spines", 0.75), 1e-9);
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_matron_chance_spines", 0.2));
            Assert.AreEqual(0.2, KnotLootTunables.Chance("matron", "spines", 0.75), 1e-9);
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_matron_chance_spines", 5.0));
            Assert.AreEqual(1.0, KnotLootTunables.Chance("matron", "spines", 0.75), 1e-9);
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_matron_chance_spines", -2.0));
            Assert.AreEqual(0.0, KnotLootTunables.Chance("matron", "spines", 0.75), 1e-9);
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_matron_chance_spines", double.NaN));
            Assert.AreEqual(0.75, KnotLootTunables.Chance("matron", "spines", 0.75), 1e-9);
        }

        [TestMethod]
        public void MatronDefaults_AreRegistered_AndMatchTheTable()
        {
            Assert.IsTrue(DefaultPropertyManager.DefaultBooleanProperties["knot_loot_matron_enabled"].Item);
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_weight_voidclaw"].Item);
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_weight_spines"].Item);
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_weight_torc"].Item);
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_weight_hive_stinger_baton"].Item);
            Assert.AreEqual(1L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_weight_hive_nether_baton"].Item);
            Assert.AreEqual(25L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_mmd_min"].Item);
            Assert.AreEqual(50L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_mmd_max"].Item);
            Assert.AreEqual(0.7, DefaultPropertyManager.DefaultDoubleProperties["knot_loot_matron_window_high_start"].Item, 1e-9);
            Assert.AreEqual(1.0, DefaultPropertyManager.DefaultDoubleProperties["knot_loot_matron_chance_spines"].Item, 1e-9);
            Assert.AreEqual(25L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_note_min"].Item);
            Assert.AreEqual(50L, DefaultPropertyManager.DefaultLongProperties["knot_loot_matron_note_max"].Item);
        }

        [TestMethod]
        public void Matron_Settings_ReadLive_AndAreIndependentOfGrael()
        {
            Assert.AreEqual((25L, 50L), KnotLootTunables.Currency("matron", "mmd", (1, 2)));
            Assert.IsTrue(PropertyManager.ModifyLong("knot_loot_matron_mmd_max", 80));
            Assert.AreEqual((25L, 80L), KnotLootTunables.Currency("matron", "mmd", (1, 2)));
            Assert.AreEqual((25L, 50L), KnotLootTunables.Currency("grael", "mmd", (1, 2)));

            Assert.AreEqual(1L, KnotLootTunables.Weight("matron", "voidclaw", 999));
            Assert.IsTrue(PropertyManager.ModifyLong("knot_loot_matron_weight_voidclaw", 0));
            Assert.AreEqual(0L, KnotLootTunables.Weight("matron", "voidclaw", 999));

            Assert.IsTrue(KnotLootTunables.TableEnabled("matron"));
            Assert.IsTrue(PropertyManager.ModifyBool("knot_loot_matron_enabled", false));
            Assert.IsFalse(KnotLootTunables.TableEnabled("matron"));
            Assert.IsTrue(KnotLootTunables.TableEnabled("grael"));

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_matron_window_width", 0.5));
            Assert.AreEqual(0.5, KnotLootTunables.Window("matron").Width, 1e-9);
            Assert.AreEqual(0.3, KnotLootTunables.Window("grael").Width, 1e-9);
        }

        [TestMethod]
        public void Trait_ReadsEveryRangeByPrefix()
        {
            var none = new KnotTraitSettings(false, new KnotTraitRange(0, 0), new KnotTraitRange(0, 0));

            var cast = KnotLootTunables.Trait("cast_speed", none);
            Assert.IsTrue(cast.Enabled);
            Assert.AreEqual(new KnotTraitRange(1, 2), cast.Primary);

            var shield = KnotLootTunables.Trait("shield_trade", none);
            Assert.AreEqual(new KnotTraitRange(125, 500), shield.Primary);
            Assert.AreEqual(new KnotTraitRange(25, 100), shield.Secondary);

            var frugal = KnotLootTunables.Trait("frugal_hand", none);
            Assert.AreEqual(new KnotTraitRange(0.1, 0.3), frugal.Primary);

            var wrist = KnotLootTunables.Trait("wrist_trade", none);
            Assert.AreEqual(new KnotTraitRange(5, 10), wrist.Primary);
            Assert.AreEqual(new KnotTraitRange(10, 20), wrist.Secondary);

            var intent = KnotLootTunables.Trait("overpowering_intent", none);
            Assert.IsTrue(intent.Enabled);
            Assert.AreEqual(new KnotTraitRange(0.05, 0.10), intent.Primary);
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_overpowering_intent_max", 0.2));
            Assert.AreEqual(new KnotTraitRange(0.05, 0.2), KnotLootTunables.Trait("overpowering_intent", none).Primary, "the max is read live");

            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_wrist_trade_enabled", false));
            Assert.IsFalse(KnotLootTunables.Trait("wrist_trade", none).Enabled);
        }

        [TestMethod]
        public void Keys_AreBuiltFromTablePrefixAndSuffix()
        {
            Assert.AreEqual("knot_loot_grael_enabled", KnotLootTunables.TableKey("grael", "enabled"));
            Assert.AreEqual("knot_trait_cast_speed_min", KnotLootTunables.TraitKey("cast_speed", "min"));
        }

        [TestMethod]
        public void SanitizeWindow_ClampsAndRejects()
        {
            Assert.AreEqual(KnotWindowSettings.Default, KnotLootTunables.SanitizeWindow(new(400, 200, 0, 0.7, 0.3)));
            Assert.AreEqual(1.0, KnotLootTunables.SanitizeWindow(new(200, 400, 0, 0.7, 5)).Width, 1e-9);
            Assert.AreEqual(0.0, KnotLootTunables.SanitizeWindow(new(200, 400, double.NaN, 0.7, 0.3)).LowStart, 1e-9);
        }

        [TestMethod]
        public void SanitizeRange_MinAboveMax_IsFallback()
            => Assert.AreEqual(new KnotTraitRange(125, 500), KnotLootTunables.SanitizeRange(new(600, 500), new(125, 500)));

        [TestMethod]
        public void SanitizeRange_NegativeBound_IsFallback()
            => Assert.AreEqual(new KnotTraitRange(125, 500), KnotLootTunables.SanitizeRange(new(-1, 500), new(125, 500)));

        [TestMethod]
        public void SanitizeRange_MaxAboveMagnitudeCap_IsFallback()
        {
            Assert.AreEqual(new KnotTraitRange(25, 100), KnotLootTunables.SanitizeRange(new(25, 1e12), new(25, 100)));
            Assert.AreEqual(new KnotTraitRange(25, KnotLootTunables.MaxTraitMagnitude), KnotLootTunables.SanitizeRange(new(25, KnotLootTunables.MaxTraitMagnitude), new(25, 100)), "control: exactly the cap is kept");
        }

        [TestMethod]
        public void SanitizeCount_RejectsInvertedAndNegative()
        {
            Assert.AreEqual((25L, 50L), KnotLootTunables.SanitizeCount((60, 50), (25, 50)));
            Assert.AreEqual((25L, 50L), KnotLootTunables.SanitizeCount((-1, 50), (25, 50)));
            Assert.AreEqual((30L, 40L), KnotLootTunables.SanitizeCount((30, 40), (25, 50)));
        }

        [TestMethod]
        public void OnlyKnotLootTunablesReadsPropertyManager()
        {
            var dir = KnotLootDir();

            foreach (var file in Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (Path.GetFileName(file) == "KnotLootTunables.cs")
                    continue;

                Assert.IsFalse(File.ReadAllText(file).Contains("PropertyManager."), Path.GetFileName(file) + " must not read PropertyManager");
            }
        }

        private static string Source(string repoRelativePath)
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, repoRelativePath);

                if (File.Exists(candidate))
                    return "\n" + File.ReadAllText(candidate).Replace("\r\n", "\n");
            }

            Assert.Fail($"Could not find {repoRelativePath} by walking up from {AppContext.BaseDirectory}");
            return null;
        }

        private static string KnotLootDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server", "KnotLoot");

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail("Source/ACE.Server/KnotLoot not found walking up from " + AppContext.BaseDirectory);
            return null;
        }
    }
}
