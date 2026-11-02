using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Task 11: the Glass Turtle Bracelet (KnotTraitId.WristTrade) trades Damage Rating against Damage Resist Rating by
    /// the wrist it is worn on. The signed term is added once, Player-only, into GetDamageRating and
    /// GetDamageResistRating, which both combat and appraisal read. Nothing is written to the item.
    /// </summary>
    [TestClass]
    public class KnotTraitWristRatingTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_wrist_trade_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_trait_wrist_trade_gain_min", "knot_trait_wrist_trade_gain_max",
            "knot_trait_wrist_trade_cost_min", "knot_trait_wrist_trade_cost_max",
        };

        private static uint nextGuid = 0x7F1B0000;

        private Func<Player, KnotTraitSuppressionKind, bool> savedSource;

        [TestInitialize]
        public void Seed()
        {
            savedSource = KnotTraitSuppression.PlayerSuppressedSource;
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
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

        // ---------------- the pure helper ----------------

        private static readonly KnotTraitEffect P50 = new KnotTraitEffect(true, 0.5, 7.5, 15);

        [TestMethod]
        public void Left_P50()
        {
            Assert.AreEqual(8, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearLeft, P50, KnotRatingKind.DamageResist), "left: +round(7.5) DRR, away from zero");
            Assert.AreEqual(-15, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearLeft, P50, KnotRatingKind.Damage), "left: -15 DR");
        }

        [TestMethod]
        public void Right_P50()
        {
            Assert.AreEqual(8, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearRight, P50, KnotRatingKind.Damage), "right: +round(7.5) DR");
            Assert.AreEqual(-15, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearRight, P50, KnotRatingKind.DamageResist), "right: -15 DRR");
        }

        [TestMethod]
        public void TwoBracelets_NetLoss()
        {
            var damage = KnotTraitCombat.WristRatingDelta(EquipMask.WristWearLeft, P50, KnotRatingKind.Damage)
                + KnotTraitCombat.WristRatingDelta(EquipMask.WristWearRight, P50, KnotRatingKind.Damage);
            var resist = KnotTraitCombat.WristRatingDelta(EquipMask.WristWearLeft, P50, KnotRatingKind.DamageResist)
                + KnotTraitCombat.WristRatingDelta(EquipMask.WristWearRight, P50, KnotRatingKind.DamageResist);

            Assert.AreEqual(-7, damage);
            Assert.AreEqual(-7, resist);
        }

        [TestMethod]
        public void NotWorn_OrInactive_IsZero()
        {
            foreach (var kind in new[] { KnotRatingKind.Damage, KnotRatingKind.DamageResist })
            {
                Assert.AreEqual(0, KnotTraitCombat.WristRatingDelta(null, P50, kind), $"{kind}: not wielded");
                Assert.AreEqual(0, KnotTraitCombat.WristRatingDelta(EquipMask.NeckWear, P50, kind), $"{kind}: not a wrist");
                Assert.AreEqual(0, KnotTraitCombat.WristRatingDelta(EquipMask.WristWear, P50, kind), $"{kind}: the combined both-wrist mask is not one wrist");
                Assert.AreEqual(0, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearLeft, KnotTraitEffect.None, kind), $"{kind}: inactive (left)");
                Assert.AreEqual(0, KnotTraitCombat.WristRatingDelta(EquipMask.WristWearRight, KnotTraitEffect.None, kind), $"{kind}: inactive (right)");
            }
        }

        // ---------------- the Player accessor ----------------

        [TestMethod]
        public void Accessor_TopAndBottomOfTheRange_ByWrist()
        {
            var player = PlayerWith(out var equipped);

            // level 400, u 1.0: p = 1, gain 10, cost 20
            var top = Bracelet(400, 1.0, EquipMask.WristWearLeft);
            equipped[top.Guid] = top;
            Assert.AreEqual(10, player.GetKnotWristRating(KnotRatingKind.DamageResist));
            Assert.AreEqual(-20, player.GetKnotWristRating(KnotRatingKind.Damage));

            // level 200, u 0.0: p = 0, gain 5, cost 10
            equipped.Clear();
            var bottom = Bracelet(200, 0.0, EquipMask.WristWearRight);
            equipped[bottom.Guid] = bottom;
            Assert.AreEqual(5, player.GetKnotWristRating(KnotRatingKind.Damage));
            Assert.AreEqual(-10, player.GetKnotWristRating(KnotRatingKind.DamageResist));
        }

        [TestMethod]
        public void Accessor_TwoBracelets_SumSigned()
        {
            var player = PlayerWith(out var equipped);

            var left = Bracelet(400, 1.0, EquipMask.WristWearLeft);
            var right = Bracelet(200, 0.0, EquipMask.WristWearRight);
            equipped[left.Guid] = left;
            equipped[right.Guid] = right;

            // Damage: -20 (left) + 5 (right); DamageResist: +10 (left) - 10 (right)
            Assert.AreEqual(-15, player.GetKnotWristRating(KnotRatingKind.Damage), "a net loss stays negative");
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.DamageResist));
        }

        [TestMethod]
        public void Accessor_MatchesTheAppraisalLine()
        {
            var player = PlayerWith(out var equipped);
            var bracelet = Bracelet(300, 0.5, EquipMask.WristWearLeft);
            equipped[bracelet.Guid] = bracelet;

            var lines = string.Join("\n", KnotTraitDisplay.GetAppraisalLines(bracelet));
            var gain = player.GetKnotWristRating(KnotRatingKind.DamageResist);
            var cost = -player.GetKnotWristRating(KnotRatingKind.Damage);

            Assert.IsTrue(gain > 0 && cost > 0, "harness control: the trade is active");
            StringAssert.Contains(lines, $"left wrist +{gain} damage resist rating, -{cost} damage rating", "displayed and applied agree");
        }

        [TestMethod]
        public void Accessor_IsZero_InPack_NotWrist_NoTrait_OrNothingEquipped()
        {
            var player = PlayerWith(out var equipped);
            var bracelet = Bracelet(400, 1.0, EquipMask.WristWearRight);

            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.Damage), "a bracelet not in EquippedObjects (in a pack) does nothing");

            equipped[bracelet.Guid] = bracelet;
            Assert.AreEqual(10, player.GetKnotWristRating(KnotRatingKind.Damage), "control: wielded on the right wrist");

            bracelet.CurrentWieldedLocation = null;
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.Damage), "no wielded location");

            equipped.Clear();
            var plain = Bracelet(400, null, EquipMask.WristWearRight);
            equipped[plain.Guid] = plain;
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.Damage), "an ordinary bracelet carries no trade");

            var bare = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            Assert.AreEqual(0, bare.GetKnotWristRating(KnotRatingKind.Damage), "null EquippedObjects");
        }

        [TestMethod]
        public void Accessor_Suppression_AndMechanicSwitch_ZeroBothHalves()
        {
            var player = PlayerWith(out var equipped);
            var bracelet = Bracelet(400, 1.0, EquipMask.WristWearLeft);
            bracelet.ChangesDetected = false;
            equipped[bracelet.Guid] = bracelet;

            Assert.AreEqual(10, player.GetKnotWristRating(KnotRatingKind.DamageResist), "control: gain on");
            Assert.AreEqual(-20, player.GetKnotWristRating(KnotRatingKind.Damage), "control: cost on");

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Equipment;
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.DamageResist), "suppressed: no gain");
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.Damage), "suppressed: no cost");

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Weapon;
            Assert.AreEqual(-20, player.GetKnotWristRating(KnotRatingKind.Damage), "Weapon-kind suppression does not touch an Equipment-kind trait");

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_wrist_trade_enabled", false));
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.DamageResist), "mechanic off: no gain");
            Assert.AreEqual(0, player.GetKnotWristRating(KnotRatingKind.Damage), "mechanic off: no cost");

            Assert.IsNull(bracelet.GetProperty(PropertyInt.GearDamage), "GearDamage is never written");
            Assert.IsNull(bracelet.GetProperty(PropertyInt.GearDamageResist), "GearDamageResist is never written");
            Assert.IsFalse(bracelet.ChangesDetected, "the accessor must never write to the item");
        }

        // ---------------- source pins ----------------

        private const string ServerRoot = "Source/ACE.Server/";

        [TestMethod]
        public void RatingGetters_AddTheWristTerm()
        {
            var source = PooledLootSourceText.Read(ServerRoot + "WorldObjects/Creature_Rating.cs");

            foreach (var (signature, kind, ret) in new[]
            {
                ("public int GetDamageRating()", "Damage",
                    "return damageRating + equipment + knotWrist + enchantments - weaknessRating + augBonus + lumAugBonus + classAbilityBonus + healBoostConversion + critDamageConversion;"),
                ("public int GetDamageResistRating(CombatType? combatType = null, bool directDamage = true)", "DamageResist",
                    "return damageResistRating + equipment + knotWrist + enchantments - netherDotDamageRating + augBonus + lumAugBonus + specBonus + classAbilityBonus;"),
            })
            {
                var body = Flat(Code(PooledLootSourceText.MethodBody(source, signature)));

                StringAssert.Contains(body, "GetKnotWristRating(KnotRatingKind.", signature);
                StringAssert.Contains(body, $"var knotWrist = this is Player kwp ? kwp.GetKnotWristRating(KnotRatingKind.{kind}) : 0;", signature);
                StringAssert.Contains(body, ret, $"{signature}: the term is added once into the final sum, unclamped");
                Assert.AreEqual(1, Count(body, "GetKnotWristRating("), signature);
                Assert.AreEqual(2, Count(body, "knotWrist"), $"{signature}: declared once, summed once");
                Assert.IsFalse(body.Contains("PropertyFloat.KnotTrait", StringComparison.Ordinal), $"{signature} must not read a Knot trait property directly");
            }

            // the other rating getters are untouched
            foreach (var other in new[] { "public int GetCritRating()", "public int GetCritDamageRating()", "public int GetCritResistRating()", "public int GetCritDamageResistRating()" })
                Assert.IsFalse(PooledLootSourceText.MethodBody(source, other).Contains("GetKnotWristRating", StringComparison.Ordinal), other);
        }

        [TestMethod]
        public void Accessor_ReadsThroughResolveOnOnly()
        {
            var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_KnotTraits.cs"), "public int GetKnotWristRating(KnotRatingKind kind)"));

            StringAssert.Contains(body, "KnotTraitCombat.WristRatingDelta(item.CurrentWieldedLocation, KnotTraitValue.ResolveOn(item, KnotTraitId.WristTrade, this), kind)");
            StringAssert.Contains(body, "EquippedObjects");
            Assert.IsFalse(body.Contains("GetProperty(PropertyFloat.KnotTrait", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("SetProperty(", StringComparison.Ordinal));

            var helper = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "KnotLoot/KnotTraitCombat.cs"), "public static int WristRatingDelta(EquipMask? location, KnotTraitEffect e, KnotRatingKind kind)"));
            StringAssert.Contains(helper, "MidpointRounding.AwayFromZero");
            Assert.IsFalse(helper.Contains("SetProperty(", StringComparison.Ordinal));
        }

        // ---------------- helpers ----------------

        private static WorldObject Bracelet(int level, double? u, EquipMask? location)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007453,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Jewelry },
                    { PropertyInt.ValidLocations, (int)EquipMask.WristWear },
                    { PropertyInt.KnotLootLevel, level },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "grael" } },
            };

            if (u.HasValue)
                weenie.PropertiesFloat[PropertyFloat.KnotTraitWristTrade] = u.Value;

            var wo = ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
            wo.CurrentWieldedLocation = location;
            return wo;
        }

        /// <summary>A bare Player whose EquippedObjects is a dictionary the test owns.</summary>
        private static Player PlayerWith(out Dictionary<ObjectGuid, WorldObject> equipped)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            var field = typeof(Creature).GetField("<EquippedObjects>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.IsNotNull(field, "Creature.EquippedObjects backing field not found");

            equipped = new Dictionary<ObjectGuid, WorldObject>();
            field.SetValue(player, equipped);

            return player;
        }

        /// <summary>The body with // comments removed, so a pin cannot be satisfied by a comment.</summary>
        private static string Code(string body) => Regex.Replace(body.Replace("\r\n", "\n"), @"//[^\n]*", "");

        /// <summary>Whitespace collapsed to single spaces, so a pin survives line breaks and indentation.</summary>
        private static string Flat(string body) => Regex.Replace(body, @"\s+", " ");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
