using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

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
    public class KnotTraitValueTests
    {
        private static readonly KnotWindowSettings W = KnotWindowSettings.Default;

        private static readonly KnotTraitSettings S = new(
            true,
            new KnotTraitRange(KnotLootDefaults.ShieldTradeArmorMin, KnotLootDefaults.ShieldTradeArmorMax),
            new KnotTraitRange(KnotLootDefaults.ShieldTradeHealthMin, KnotLootDefaults.ShieldTradeHealthMax));

        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_cast_speed_enabled", "knot_trait_shield_trade_enabled", "knot_trait_frugal_hand_enabled", "knot_trait_wrist_trade_enabled", "knot_trait_overpowering_intent_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_trait_cast_speed_min", "knot_trait_cast_speed_max",
            "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max", "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max",
            "knot_trait_frugal_hand_min", "knot_trait_frugal_hand_max",
            "knot_trait_wrist_trade_gain_min", "knot_trait_wrist_trade_gain_max", "knot_trait_wrist_trade_cost_min", "knot_trait_wrist_trade_cost_max",
            "knot_trait_overpowering_intent_min", "knot_trait_overpowering_intent_max",
        };

        private static uint nextGuid = 0x7F100000;

        private Func<Player, KnotTraitSuppressionKind, bool> savedSource;

        [TestInitialize]
        public void Seed()
        {
            savedSource = KnotTraitSuppression.PlayerSuppressedSource;
            RestoreDefaults();
        }

        [TestCleanup]
        public void Cleanup()
        {
            KnotTraitSuppression.PlayerSuppressedSource = savedSource;
            RestoreDefaults();
        }

        private static void RestoreDefaults()
        {
            foreach (var k in BoolKeys)
                Assert.IsTrue(PropertyManager.ModifyBool(k, DefaultPropertyManager.DefaultBooleanProperties[k].Item), k);

            foreach (var k in LongKeys)
                Assert.IsTrue(PropertyManager.ModifyLong(k, DefaultPropertyManager.DefaultLongProperties[k].Item), k);

            foreach (var k in DoubleKeys)
                Assert.IsTrue(PropertyManager.ModifyDouble(k, DefaultPropertyManager.DefaultDoubleProperties[k].Item), k);
        }

        private static WorldObject CreateItem(Dictionary<PropertyFloat, double> floats, int? level, string table)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007451,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                },
                PropertiesFloat = floats,
                PropertiesString = new Dictionary<PropertyString, string>(),
            };

            if (level.HasValue)
                weenie.PropertiesInt[PropertyInt.KnotLootLevel] = level.Value;

            if (table != null)
                weenie.PropertiesString[PropertyString.KnotLootTable] = table;

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
        }

        private static WorldObject ShieldItem() =>
            CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitShieldTrade, 0.5 } }, 300, "grael");

        private static Player BarePlayer() => (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

        // ---------------- pure Resolve ----------------

        [TestMethod]
        public void Resolve_LinkedRoll_OneP()
        {
            var e = KnotTraitValue.Resolve(0.5, 300, W, S, false);
            Assert.IsTrue(e.Active);
            Assert.AreEqual(0.50, e.P, 1e-9);
            Assert.AreEqual(312.5, e.Primary, 1e-9);
            Assert.AreEqual(62.5, e.Secondary, 1e-9);

            var top = KnotTraitValue.Resolve(1.0, 400, W, S, false);
            Assert.AreEqual(500, top.Primary, 1e-9);
            Assert.AreEqual(100, top.Secondary, 1e-9);
        }

        [TestMethod]
        public void Resolve_SuppressedOrDisabled_ZeroesBothHalves()
        {
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.Resolve(0.5, 300, W, S, suppressed: true));
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.Resolve(0.5, 300, W, S with { Enabled = false }, false));
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.Resolve(null, 300, W, S, false));
        }

        [TestMethod]
        public void Resolve_MissingLevel_ReadsAsWindowLowLevel()
        {
            Assert.AreEqual(KnotTraitValue.Resolve(0.5, (int)W.LowLevel, W, S, false), KnotTraitValue.Resolve(0.5, null, W, S, false));
            Assert.AreEqual(0.15, KnotTraitValue.Resolve(0.5, null, W, S, false).P, 1e-9);
        }

        // ---------------- live ResolveOn ----------------

        [TestMethod]
        public void ResolveOn_RetunesExistingItem_WithoutWriting()
        {
            var item = ShieldItem();
            item.ChangesDetected = false;
            var before = item.GetProperty(PropertyFloat.KnotTraitShieldTrade);

            var e = KnotTraitValue.ResolveOn(item, KnotTraitId.ShieldTrade, null);
            Assert.IsTrue(e.Active);
            Assert.AreEqual(312.5, e.Primary, 1e-9);
            Assert.AreEqual(62.5, e.Secondary, 1e-9);

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_shield_trade_armor_max", 900));
            Assert.AreEqual(512.5, KnotTraitValue.ResolveOn(item, KnotTraitId.ShieldTrade, null).Primary, 1e-9);

            Assert.IsFalse(item.ChangesDetected, "ResolveOn must never write to the item");
            Assert.AreEqual(before, item.GetProperty(PropertyFloat.KnotTraitShieldTrade));
            Assert.AreEqual(300, item.GetProperty(PropertyInt.KnotLootLevel));
            Assert.AreEqual("grael", item.GetProperty(PropertyString.KnotLootTable));

            // Positive control: the signal is discriminating - a real write flips it.
            item.SetProperty(PropertyFloat.KnotTraitShieldTrade, 0.25);
            Assert.IsTrue(item.ChangesDetected, "control: SetProperty must flip ChangesDetected, or the no-write assert above proves nothing");
        }

        [TestMethod]
        public void ResolveOn_WindowSettingRetunesExistingItem()
        {
            var item = ShieldItem();

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_grael_window_width", 0.5));
            // level 300: start 0.35, end 0.85, p = 0.35 + 0.5 * 0.5 = 0.60
            Assert.AreEqual(0.60, KnotTraitValue.ResolveOn(item, KnotTraitId.ShieldTrade, null).P, 1e-9);
        }

        [TestMethod]
        public void ResolveOn_UnknownOrNullTable_UsesDefaultWindow()
        {
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_loot_grael_window_width", 0.5));

            var unknown = CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitShieldTrade, 0.5 } }, 300, "nope");
            var none = CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitShieldTrade, 0.5 } }, 300, null);

            Assert.AreEqual(0.50, KnotTraitValue.ResolveOn(unknown, KnotTraitId.ShieldTrade, null).P, 1e-9);
            Assert.AreEqual(0.50, KnotTraitValue.ResolveOn(none, KnotTraitId.ShieldTrade, null).P, 1e-9);
        }

        [TestMethod]
        public void ResolveOn_NoTraitProperty_IsNone()
        {
            var shield = ShieldItem();
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(shield, KnotTraitId.CastSpeed, null));
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(shield, KnotTraitId.WristTrade, null));

            var bare = CreateItem(new Dictionary<PropertyFloat, double>(), 300, "grael");
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(bare, KnotTraitId.ShieldTrade, null));

            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(null, KnotTraitId.ShieldTrade, null));
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(shield, (KnotTraitId)99, null));
        }

        [TestMethod]
        public void ResolveOn_MechanicSwitchOff_ZeroesWholeTrade()
        {
            var shield = ShieldItem();
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_shield_trade_enabled", false));
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(shield, KnotTraitId.ShieldTrade, null));
        }

        [TestMethod]
        public void ResolveOn_SuppressedPlayer_ZeroesWholeTrade_AndAsksTheTraitsKind()
        {
            var asked = new List<KnotTraitSuppressionKind>();
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => { asked.Add(kind); return true; };

            var player = BarePlayer();

            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(ShieldItem(), KnotTraitId.ShieldTrade, player));

            var orb = CreateItem(new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitCastSpeed, 0.5 } }, 300, "grael");
            Assert.AreEqual(KnotTraitEffect.None, KnotTraitValue.ResolveOn(orb, KnotTraitId.CastSpeed, player));

            CollectionAssert.AreEqual(new[] { KnotTraitSuppressionKind.Equipment, KnotTraitSuppressionKind.Weapon }, asked);

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(KnotTraitValue.ResolveOn(ShieldItem(), KnotTraitId.ShieldTrade, player).Active);
        }

        // ---------------- suppression predicate ----------------

        [TestMethod]
        public void SuppressedFor_NullOrNonPlayer_IsFalse()
        {
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => true;

            Assert.IsFalse(KnotTraitSuppression.SuppressedFor(null, KnotTraitSuppressionKind.Equipment));
            Assert.IsFalse(KnotTraitSuppression.SuppressedFor(null, KnotTraitSuppressionKind.Weapon));
            Assert.IsFalse(KnotTraitSuppression.SuppressedFor(ShieldItem(), KnotTraitSuppressionKind.Equipment), "a non-Player wielder is never suppressed");

            Assert.IsTrue(KnotTraitSuppression.SuppressedFor(BarePlayer(), KnotTraitSuppressionKind.Equipment), "control: a Player routes through the source");
        }

        // ---------------- registry ----------------

        [TestMethod]
        public void Registry_PropertiesUnique_AndKindsPinned()
        {
            Assert.AreEqual(5, KnotTraitRegistry.All.Count);

            var props = KnotTraitRegistry.All.Select(d => (int)d.Property).OrderBy(x => x).ToArray();
            CollectionAssert.AreEqual(new[] { 9015, 9016, 9017, 9018, 9020 }, props);

            Expect(KnotTraitId.CastSpeed, PropertyFloat.KnotTraitCastSpeed, "Mirrorglass Haste", "cast_speed", KnotTraitSuppressionKind.Weapon);
            Expect(KnotTraitId.ShieldTrade, PropertyFloat.KnotTraitShieldTrade, "Rose's Bargain", "shield_trade", KnotTraitSuppressionKind.Equipment);
            Expect(KnotTraitId.FrugalHand, PropertyFloat.KnotTraitFrugalHand, "Frugal Hand", "frugal_hand", KnotTraitSuppressionKind.Equipment);
            Expect(KnotTraitId.WristTrade, PropertyFloat.KnotTraitWristTrade, "Glass Turtle", "wrist_trade", KnotTraitSuppressionKind.Equipment);
            Expect(KnotTraitId.OverpoweringIntent, PropertyFloat.KnotTraitOverpoweringIntent, "Overpowering Intent", "overpowering_intent", KnotTraitSuppressionKind.Weapon);
            Assert.AreEqual(5, (int)KnotTraitId.OverpoweringIntent);

            Assert.IsFalse(KnotTraitRegistry.TryGet((KnotTraitId)99, out _));
        }

        private static void Expect(KnotTraitId id, PropertyFloat prop, string name, string prefix, KnotTraitSuppressionKind kind)
        {
            Assert.IsTrue(KnotTraitRegistry.TryGet(id, out var d), id.ToString());
            Assert.AreEqual(id, d.Id);
            Assert.AreEqual(prop, d.Property);
            Assert.AreEqual(name, d.DisplayName);
            Assert.AreEqual(prefix, d.SettingPrefix);
            Assert.AreEqual(kind, d.Suppression);
        }

        [TestMethod]
        public void Registry_Defaults_MatchTheRegisteredSettingDefaults()
        {
            // Defaults is passed as PropertyManager's fallback, which is cached for unseeded keys, so it must
            // equal the DefaultPropertyManager rows exactly.
            var defaults = DefaultPropertyManager.DefaultDoubleProperties;

            void Check(KnotTraitId id, string minKey, string maxKey, string min2Key, string max2Key)
            {
                Assert.IsTrue(KnotTraitRegistry.TryGet(id, out var d));
                Assert.AreEqual(DefaultPropertyManager.DefaultBooleanProperties[$"knot_trait_{d.SettingPrefix}_enabled"].Item, d.Defaults.Enabled);
                Assert.AreEqual(defaults[minKey].Item, d.Defaults.Primary.Min, 0.0, minKey);
                Assert.AreEqual(defaults[maxKey].Item, d.Defaults.Primary.Max, 0.0, maxKey);

                if (min2Key != null)
                {
                    Assert.AreEqual(defaults[min2Key].Item, d.Defaults.Secondary.Min, 0.0, min2Key);
                    Assert.AreEqual(defaults[max2Key].Item, d.Defaults.Secondary.Max, 0.0, max2Key);
                }
                else
                {
                    Assert.AreEqual(new KnotTraitRange(0, 0), d.Defaults.Secondary);
                }
            }

            Check(KnotTraitId.CastSpeed, "knot_trait_cast_speed_min", "knot_trait_cast_speed_max", null, null);
            Check(KnotTraitId.ShieldTrade, "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max", "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max");
            Check(KnotTraitId.FrugalHand, "knot_trait_frugal_hand_min", "knot_trait_frugal_hand_max", null, null);
            Check(KnotTraitId.WristTrade, "knot_trait_wrist_trade_gain_min", "knot_trait_wrist_trade_gain_max", "knot_trait_wrist_trade_cost_min", "knot_trait_wrist_trade_cost_max");
            Check(KnotTraitId.OverpoweringIntent, "knot_trait_overpowering_intent_min", "knot_trait_overpowering_intent_max", null, null);
        }

        [TestMethod]
        public void PlayerPredicate_MapsKindsToTheExistingModPredicates()
        {
            var text = SourceFile("Source/ACE.Server/WorldObjects/Player_KnotTraits.cs");

            StringAssert.Contains(text, "KnotTraitSuppressionKind.Weapon ? WeaponModSuppressed : EquipmentModSuppressed");
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
