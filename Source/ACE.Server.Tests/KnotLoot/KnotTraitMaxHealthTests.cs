using System;
using System.Collections.Generic;
using System.IO;
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
    /// Task 10: Shield of the Rose's health half (KnotTraitId.ShieldTrade, Secondary) is a real, floored subtraction
    /// in the GearMaxHealth slot of both CreatureVital max-health getters, carried on the wire through
    /// GetNetworkGearMaxHealth, refreshed on equip/dequip of a Knot item and resynced by the heartbeat when the
    /// resolved penalty changes. Nothing is written to the item or to any Gear* property by Knot code.
    /// </summary>
    [TestClass]
    public class KnotTraitMaxHealthTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_shield_trade_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_trait_shield_trade_armor_min", "knot_trait_shield_trade_armor_max",
            "knot_trait_shield_trade_health_min", "knot_trait_shield_trade_health_max",
        };

        private static uint nextGuid = 0x7F1A0000;

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

        // ---------------- the pure helpers ----------------

        [TestMethod]
        public void SubtractMaxHealthPenalty_NeverWrapsAndFloorsAtOne()
        {
            Assert.AreEqual(437u, KnotTraitCombat.SubtractMaxHealthPenalty(500, 63));
            Assert.AreEqual(1u, KnotTraitCombat.SubtractMaxHealthPenalty(30, 100), "floored at 1, never wrapped");
            Assert.AreEqual(1u, KnotTraitCombat.SubtractMaxHealthPenalty(100, 100), "an exact cancel is still floored at 1");
            Assert.AreEqual(500u, KnotTraitCombat.SubtractMaxHealthPenalty(500, 0));
            Assert.AreEqual(500u, KnotTraitCombat.SubtractMaxHealthPenalty(500, -40), "a negative penalty never adds health");
            Assert.AreEqual(uint.MaxValue, KnotTraitCombat.SubtractMaxHealthPenalty(uint.MaxValue, 0));
        }

        [TestMethod]
        public void AffectsMaxHealth_KeysOnTheTraitProperty()
        {
            Assert.IsTrue(KnotTraitCombat.AffectsMaxHealth(Shield(300, 0.5)));
            Assert.IsFalse(KnotTraitCombat.AffectsMaxHealth(Shield(300, null)), "an ordinary shield does not trigger a refresh");
            Assert.IsFalse(KnotTraitCombat.AffectsMaxHealth(null));
        }

        // ---------------- the Player accessor ----------------

        [TestMethod]
        public void Penalty_P50_Is62_AndMatchesTheAppraisalLine()
        {
            var shield = Shield(300, 0.5);
            var player = PlayerWith(out var equipped);
            equipped[shield.Guid] = shield;

            // level 300, u 0.5: p computes as 0.49999999999999994 in double, so 25 + p * 75 = 62.499999999999993,
            // which rounds to 62 (the armor half lands on exactly 312.5 and rounds to 313). The display rounds the
            // same Secondary the same way, which is the invariant that matters.
            var effect = KnotTraitValue.ResolveOn(shield, KnotTraitId.ShieldTrade, player);
            Assert.AreEqual(62.5, effect.Secondary, 1e-9, "harness control: Secondary is 62.5 to within float error");
            Assert.AreEqual(62, player.GetKnotMaxHealthPenalty());

            var lines = string.Join("\n", KnotTraitDisplay.GetAppraisalLines(shield));
            StringAssert.Contains(lines, $"-{player.GetKnotMaxHealthPenalty()} maximum health", "displayed and applied penalty agree");

            Assert.AreEqual(438u, KnotTraitCombat.SubtractMaxHealthPenalty(500, player.GetKnotMaxHealthPenalty()));
        }

        [TestMethod]
        public void Penalty_SumsOnlyWieldedItems()
        {
            var player = PlayerWith(out var equipped);

            var top = Shield(400, 1.0);
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "a Shield of the Rose that is not in EquippedObjects (in a pack) costs nothing");

            equipped[top.Guid] = top;
            Assert.AreEqual(100, player.GetKnotMaxHealthPenalty(), "control: wielded, it costs the full penalty");

            equipped.Remove(top.Guid);
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "dequipped, the penalty is gone");
        }

        [TestMethod]
        public void Penalty_TopAndBottomOfTheRange()
        {
            var player = PlayerWith(out var equipped);

            var top = Shield(400, 1.0);
            equipped[top.Guid] = top;
            Assert.AreEqual(100, player.GetKnotMaxHealthPenalty());

            equipped.Clear();
            var bottom = Shield(200, 0.0);
            equipped[bottom.Guid] = bottom;
            Assert.AreEqual(25, player.GetKnotMaxHealthPenalty());
        }

        [TestMethod]
        public void Penalty_HugeHealthMaxSetting_FallsBackAndNeverOverflows()
        {
            var player = PlayerWith(out var equipped);

            var top = Shield(400, 1.0);
            equipped[top.Guid] = top;
            Assert.AreEqual(100, player.GetKnotMaxHealthPenalty(), "control: the default range gives 100 at the top");

            // above KnotLootTunables.MaxTraitMagnitude the range falls back to the default 25-100
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_shield_trade_health_max", 1e12));
            Assert.AreEqual(100, player.GetKnotMaxHealthPenalty());
        }

        [TestMethod]
        public void NetworkGearMaxHealth_SubtractsThePenalty()
        {
            var shield = Shield(300, 0.5);
            var player = PlayerWith(out var equipped);
            equipped[shield.Guid] = shield;

            // the seam's Player has no equipped-ratings cache, so its gear rating reads 0 and the wire value is 0 - 62
            Assert.AreEqual(0, player.GetGearMaxHealth(), "harness control: no gear rating in the seam");
            Assert.AreEqual(-62, player.GetNetworkGearMaxHealth());
            Assert.AreEqual(player.GetGearMaxHealth() - 62, player.GetNetworkGearMaxHealth());
        }

        [TestMethod]
        public void TinkerLock_IsKnotItem_KeysOnTheTraitProperty()
        {
            Assert.AreEqual(9016, (int)PropertyFloat.KnotTraitShieldTrade, "harness control: the shield trait is PropertyFloat 9016");

            Assert.IsTrue(ACE.Server.Entity.TinkerLock.IsKnotItem(Shield(300, 0.5)), "carries PropertyFloat 9016");
            Assert.IsFalse(ACE.Server.Entity.TinkerLock.IsKnotItem(Shield(300, null)), "no trait property");
        }

        [TestMethod]
        public void Penalty_IsZero_WithNoTraitItemOrNothingEquipped()
        {
            var player = PlayerWith(out var equipped);
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "nothing equipped");

            var plain = Shield(300, null);
            equipped[plain.Guid] = plain;
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "an ordinary shield carries no penalty");

            var bare = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));
            Assert.AreEqual(0, bare.GetKnotMaxHealthPenalty(), "null EquippedObjects");
        }

        [TestMethod]
        public void Penalty_AndArmorBonus_AreOneLinkedTrade()
        {
            var shield = Shield(300, 0.5);
            shield.ChangesDetected = false;
            var player = PlayerWith(out var equipped);
            equipped[shield.Guid] = shield;

            Assert.AreEqual(62, player.GetKnotMaxHealthPenalty(), "control: penalty on");
            Assert.AreEqual(313, KnotTraitCombat.ShieldArmorBonus(shield, player), "control: bonus on");

            // Equipment-kind suppression zeroes both halves together
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Equipment;
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "suppressed: no penalty");
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(shield, player), "suppressed: no bonus");

            // Weapon-kind suppression does not touch an Equipment-kind trait
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Weapon;
            Assert.AreEqual(62, player.GetKnotMaxHealthPenalty());

            // the mechanic switch zeroes both halves together
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_shield_trade_enabled", false));
            Assert.AreEqual(0, player.GetKnotMaxHealthPenalty(), "mechanic off: no penalty");
            Assert.AreEqual(0, KnotTraitCombat.ShieldArmorBonus(shield, player), "mechanic off: no bonus");

            Assert.IsNull(shield.GetProperty(PropertyInt.GearMaxHealth), "the item's GearMaxHealth is never written");
            Assert.IsFalse(shield.ChangesDetected, "the accessor must never write to the item");
        }

        // ---------------- source pins ----------------

        private const string ServerRoot = "Source/ACE.Server/";
        private const string Subtract = "KnotTraitCombat.SubtractMaxHealthPenalty(";

        [TestMethod]
        public void Vital_BothGettersSubtractInTheGearSlot()
        {
            var source = PooledLootSourceText.Read(ServerRoot + "WorldObjects/Entity/CreatureVital.cs");

            foreach (var signature in new[] { "public uint Base", "public uint GetMaxValue(bool enchanted)" })
            {
                var body = Flat(Code(PooledLootSourceText.MethodBody(source, signature)));

                StringAssert.Contains(body,
                    "if (Vital == PropertyAttribute2nd.MaxHealth) { total += (uint)player.GetGearMaxHealth(); total = KnotTraitCombat.SubtractMaxHealthPenalty(total, player.GetKnotMaxHealthPenalty()); }",
                    $"{signature}: the penalty is subtracted inside the MaxHealth branch, right after the gear add");

                var gear = body.IndexOf("GetGearMaxHealth()", StringComparison.Ordinal);
                var sub = body.IndexOf(Subtract, StringComparison.Ordinal);
                var enhanced = body.IndexOf("GetEnhancedVitalBonus", StringComparison.Ordinal);

                Assert.IsTrue(gear >= 0 && sub > gear && enhanced > sub, $"{signature}: GetGearMaxHealth, then the subtraction, then GetEnhancedVitalBonus");
                Assert.AreEqual(1, Count(body, Subtract), signature);
                Assert.IsFalse(body.Contains("(uint)player.GetKnotMaxHealthPenalty", StringComparison.Ordinal), $"{signature}: the penalty must never be cast to uint");
            }

            // GetMaxValue: before the multiplier, so the client's rebuild (which carries it in GearMaxHealth) agrees
            var max = Flat(Code(PooledLootSourceText.MethodBody(source, "public uint GetMaxValue(bool enchanted)")));
            Assert.IsTrue(max.IndexOf(Subtract, StringComparison.Ordinal) < max.IndexOf("GetVitalMod_Multiplier", StringComparison.Ordinal), "pre-multiplier");
        }

        [TestMethod]
        public void Wire_CarriesThePenalty()
        {
            var source = PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_Vitals.cs");

            var wire = Code(PooledLootSourceText.ExpressionBody(source, "public int GetNetworkGearMaxHealth()"));
            StringAssert.Contains(wire, "GetGearMaxHealth() - GetKnotMaxHealthPenalty()");

            var update = Code(PooledLootSourceText.MethodBody(source, "public void HandleMaxHealthUpdate()"));
            StringAssert.Contains(update, "knotMaxHealthPenaltySent = GetKnotMaxHealthPenalty();", "HandleMaxHealthUpdate records what it sent");
            StringAssert.Contains(update, "GetNetworkGearMaxHealth()", "the update message still carries the network value");
            StringAssert.Contains(update, "if (Health.Current > Health.MaxValue)", "the existing clamp stays");
            Assert.IsTrue(update.IndexOf("knotMaxHealthPenaltySent =", StringComparison.Ordinal) > update.LastIndexOf("EnqueueSend(", StringComparison.Ordinal),
                "the sent penalty is recorded only after the last send");

            // login snapshot reads the same network value
            var login = Code(PooledLootSourceText.Read(ServerRoot + "Network/GameEvent/Events/GameEventPlayerDescription.cs"));
            StringAssert.Contains(login, "_propertiesInt[PropertyInt.GearMaxHealth] = Session.Player.GetNetworkGearMaxHealth();");

            // appraisal shows the network value for a Player
            var appraise = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "Network/Structure/AppraiseInfo.cs"), "private void AddRatings(Creature creature)"));
            StringAssert.Contains(appraise, "var gearMaxHealth = creature is Player ap ? ap.GetNetworkGearMaxHealth() : creature.GetGearMaxHealth();");
        }

        [TestMethod]
        public void EquipAndDequip_RefreshForKnotItems()
        {
            var source = PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_Inventory.cs");

            foreach (var signature in new[]
            {
                "public bool TryEquipObjectWithNetworking(WorldObject item, EquipMask wieldedLocation)",
                "public bool TryDequipObjectWithNetworking(ObjectGuid objectGuid, out WorldObject item",
            })
            {
                var body = Flat(Code(PooledLootSourceText.MethodBody(source, signature)));
                StringAssert.Contains(body, "if (item.GearMaxHealth != null || KnotTraitCombat.AffectsMaxHealth(item)) HandleMaxHealthUpdate();", signature);
            }
        }

        [TestMethod]
        public void Heartbeat_ResyncsOnChange()
        {
            var tick = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_Tick.cs"), "public override void Heartbeat(double currentUnixTime)"));
            var facet = tick.IndexOf("FacetPkHeartbeat();", StringComparison.Ordinal);
            var knot = tick.IndexOf("KnotTraitHeartbeat();", StringComparison.Ordinal);
            Assert.IsTrue(facet >= 0 && knot > facet, "KnotTraitHeartbeat runs after FacetPkHeartbeat");
            Assert.AreEqual(1, Count(tick, "KnotTraitHeartbeat();"));

            var beat = Flat(Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_KnotTraits.cs"), "public void KnotTraitHeartbeat()")));
            StringAssert.Contains(beat, "try {");
            StringAssert.Contains(beat, "catch (");
            StringAssert.Contains(beat, "if (GetKnotMaxHealthPenalty() != knotMaxHealthPenaltySent) HandleMaxHealthUpdate();", "resync only on change");
        }

        [TestMethod]
        public void Accessor_ReadsThroughResolveOnOnly()
        {
            var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(ServerRoot + "WorldObjects/Player_KnotTraits.cs"), "public int GetKnotMaxHealthPenalty()"));

            StringAssert.Contains(body, "KnotTraitValue.ResolveOn(item, KnotTraitId.ShieldTrade, this).Secondary");
            StringAssert.Contains(body, "MidpointRounding.AwayFromZero");
            StringAssert.Contains(body, "EquippedObjects");
            Assert.IsFalse(body.Contains("GetProperty(PropertyFloat.KnotTrait", StringComparison.Ordinal));
            Assert.IsFalse(body.Contains("SetProperty(", StringComparison.Ordinal));
        }

        [TestMethod]
        public void NoGearMaxHealthWrite_FromKnotCode()
        {
            var files = new List<string>(Directory.EnumerateFiles(Path.Combine(ServerDir(), "KnotLoot"), "*.cs", SearchOption.AllDirectories))
            {
                Path.Combine(ServerDir(), "WorldObjects", "Player_KnotTraits.cs"),
            };

            Assert.IsTrue(files.Count > 5, "harness control: the KnotLoot directory was found");

            foreach (var path in files)
            {
                Assert.IsTrue(File.Exists(path), path);
                var text = Code(File.ReadAllText(path));
                Assert.IsFalse(text.Contains("GearMaxHealth =", StringComparison.Ordinal), path);
                Assert.IsFalse(text.Contains("PropertyInt.GearMaxHealth,", StringComparison.Ordinal), $"{path} must not set GearMaxHealth by property id");
            }
        }

        // ---------------- helpers ----------------

        private static WorldObject Shield(int level, double? u)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007451,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.ArmorLevel, 350 },
                    { PropertyInt.CombatUse, (int)CombatUse.Shield },
                    { PropertyInt.KnotLootLevel, level },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>(),
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "grael" } },
            };

            if (u.HasValue)
                weenie.PropertiesFloat[PropertyFloat.KnotTraitShieldTrade] = u.Value;

            return ACE.Server.Factories.WorldObjectFactory.CreateWorldObject(weenie, new ObjectGuid(nextGuid++));
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

        private static string ServerDir()
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Source", "ACE.Server");

                if (Directory.Exists(candidate))
                    return candidate;
            }

            Assert.Fail($"Could not find Source/ACE.Server by walking up from {AppContext.BaseDirectory}");
            return null;
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
