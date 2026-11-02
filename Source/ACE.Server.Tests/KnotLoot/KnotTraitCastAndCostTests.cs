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
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Task 8: Mirrorglass Haste (buff cast speed, read off the equipped caster) and Frugal Hand (stamina and mana
    /// cost, summed over equipped items and added to Efficiency before the single cost multiplier).
    /// </summary>
    [TestClass]
    public class KnotTraitCastAndCostTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_cast_speed_enabled", "knot_trait_frugal_hand_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_grael_window_low_level", "knot_loot_grael_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_grael_window_low_start", "knot_loot_grael_window_high_start", "knot_loot_grael_window_width",
            "knot_trait_cast_speed_min", "knot_trait_cast_speed_max",
            "knot_trait_frugal_hand_min", "knot_trait_frugal_hand_max",
        };

        private static uint nextGuid = 0x7F180000;

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

        // ---------------- pure helpers ----------------

        [TestMethod]
        public void CastSpeed_SpecEndpoints()
        {
            Assert.AreEqual(2.0f, KnotTraitCombat.CastSpeedMultiplier(0, 1.0), "+100% halves the cast time");
            Assert.AreEqual(3.0f, KnotTraitCombat.CastSpeedMultiplier(0, 2.0), "+200% cuts it to a third");
            Assert.AreEqual(1.0f, KnotTraitCombat.CastSpeedMultiplier(0, 0));
            Assert.AreEqual(1.5f, KnotTraitCombat.CastSpeedMultiplier(0.5, 0), "Quick Refresh alone is unchanged");
        }

        [TestMethod]
        public void CastSpeed_NegativeHalvesNeverSlow()
        {
            Assert.AreEqual(1.0f, KnotTraitCombat.CastSpeedMultiplier(-0.5, 0));
            Assert.AreEqual(1.0f, KnotTraitCombat.CastSpeedMultiplier(0, -1.0));
            Assert.AreEqual(2.0f, KnotTraitCombat.CastSpeedMultiplier(-0.5, 1.0), "a negative half is floored, never subtracted from the other");
        }

        [TestMethod]
        public void Cost_StacksAdditivelyWithEfficiency()
        {
            Assert.AreEqual(0.65, WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(0.15, 0.20)), 1e-9);
            Assert.AreEqual(0.0, WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(0.9, 0.3)), 1e-9, "the existing [0,1] clamp still holds");
            Assert.AreEqual(0.85, WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(0.15, -0.5)), 1e-9, "a negative frugal fraction never raises cost");
            Assert.AreEqual(1.0, WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(0, 0)), 1e-9);
        }

        // ---------------- live accessors ----------------

        [TestMethod]
        public void Accessors_ResolveFromEquippedItems()
        {
            var player = PlayerWith(out var equipped);

            Assert.AreEqual(0.0, player.GetKnotCastSpeedFraction(), 0.0, "no wand: no cast speed");
            Assert.AreEqual(0.0, player.GetKnotFrugalFraction(), 0.0, "nothing equipped: no frugal fraction");

            // level 300, u 0.5 on the Grael window: p = 0.5. Cast speed 1.0..2.0 -> 1.5; frugal 0.10..0.30 -> 0.20.
            var orb = Item(PropertyFloat.KnotTraitCastSpeed, EquipMask.Held);
            var gauntlets = Item(PropertyFloat.KnotTraitFrugalHand, EquipMask.HandWear);
            var second = Item(PropertyFloat.KnotTraitFrugalHand, EquipMask.WristWearLeft);
            equipped[orb.Guid] = orb;
            equipped[gauntlets.Guid] = gauntlets;
            equipped[second.Guid] = second;

            foreach (var wo in equipped.Values)
                wo.ChangesDetected = false;

            Assert.AreEqual(1.5, player.GetKnotCastSpeedFraction(), 1e-9);
            Assert.AreEqual(0.40, player.GetKnotFrugalFraction(), 1e-9, "summed over every equipped item");

            foreach (var wo in equipped.Values)
                Assert.IsFalse(wo.ChangesDetected, "the accessors must never write to an item");

            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_frugal_hand_enabled", false));
            Assert.AreEqual(0.0, player.GetKnotFrugalFraction(), 0.0, "mechanic switch off zeroes it");
            Assert.AreEqual(1.5, player.GetKnotCastSpeedFraction(), 1e-9, "the other mechanic is untouched");

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => kind == KnotTraitSuppressionKind.Weapon;
            Assert.AreEqual(0.0, player.GetKnotCastSpeedFraction(), 0.0, "Weapon-kind suppression zeroes cast speed");
        }

        [TestMethod]
        public void Accessors_OrbInAnotherSlot_IsNotTheCaster()
        {
            var player = PlayerWith(out var equipped);

            var notHeld = Item(PropertyFloat.KnotTraitCastSpeed, EquipMask.Shield);
            equipped[notHeld.Guid] = notHeld;

            Assert.AreEqual(0.0, player.GetKnotCastSpeedFraction(), 0.0, "cast speed reads only the equipped wand (EquipMask.Held)");
        }

        [TestMethod]
        public void Accessors_NullEquippedCollection_IsZero()
        {
            var bare = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            Assert.AreEqual(0.0, bare.GetKnotFrugalFraction(), 0.0);
        }

        // ---------------- source pins ----------------

        private const string CombatFile = "Source/ACE.Server/WorldObjects/Player_Combat.cs";
        private const string MagicFile = "Source/ACE.Server/WorldObjects/Player_Magic.cs";
        private const string CastingFile = "Source/ACE.Server/WorldObjects/Player_WeaponMods_Casting.cs";
        private const string KnotPlayerFile = "Source/ACE.Server/WorldObjects/Player_KnotTraits.cs";

        [TestMethod]
        public void Sites_SumBeforeTheSingleMultiplier()
        {
            var stamina = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(CombatFile), "public int GetAttackStamina(PowerAccuracy powerAccuracy)"));
            StringAssert.Contains(stamina, "WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(GetWeaponOnlyModValue(WeaponModId.Efficiency), GetKnotFrugalFraction()))");
            Assert.AreEqual(1, Count(stamina, "WeaponModCombat.CostMultiplier("), "exactly one cost multiplier in GetAttackStamina");
            Assert.AreEqual(2, Count(stamina, "Math.Max(staminaCost, 1)") + Count(stamina, "Math.Max(baseCost * staminaMod, 1)"), "both existing stamina floors stay");

            var mana = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MagicFile), "public bool CalculateManaUsage(CastingPreCheckStatus castingPreCheckStatus"));
            StringAssert.Contains(mana, "WeaponModCombat.CostMultiplier(KnotTraitCombat.CostFraction(GetCasterOnlyModValue(WeaponModId.Efficiency), GetKnotFrugalFraction()))");
            Assert.AreEqual(1, Count(mana, "WeaponModCombat.CostMultiplier("), "exactly one cost multiplier in CalculateManaUsage");
            StringAssert.Contains(mana, "if (preEfficiency >= 1 && manaUsed < 1)", "the existing mana floor stays");

            var surcharge = mana.IndexOf("if (casterItem == null)", StringComparison.Ordinal);
            var cost = mana.IndexOf("KnotTraitCombat.CostFraction(", StringComparison.Ordinal);
            Assert.IsTrue(surcharge >= 0 && cost > surcharge, "the mana discount stays inside the own-cast (casterItem == null) block");
        }

        [TestMethod]
        public void CastSpeedMod_KeepsScopeGuards_AndComposes()
        {
            var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(CastingFile), "public float GetWeaponModCastSpeedMod(Spell spell, WorldObject target)"));

            var compose = body.IndexOf("KnotTraitCombat.CastSpeedMultiplier(", StringComparison.Ordinal);
            Assert.IsTrue(compose >= 0, "cast speed composes through KnotTraitCombat.CastSpeedMultiplier");

            foreach (var guard in new[] { "!spell.IsBeneficial", "spell.MetaSpellType != SpellType.Enchantment", "target != null && target != this" })
            {
                var at = body.IndexOf(guard, StringComparison.Ordinal);
                Assert.IsTrue(at >= 0 && at < compose, $"scope guard '{guard}' must precede the composition");
            }

            StringAssert.Contains(body, "KnotTraitCombat.CastSpeedMultiplier(quickRefresh, GetKnotCastSpeedFraction())");
            Assert.AreEqual(1, Count(body, "GetCasterOnlyModValue(WeaponModId.QuickRefresh)"));
        }

        [TestMethod]
        public void Accessors_ReadThroughResolveOnOnly()
        {
            var file = PooledLootSourceText.Read(KnotPlayerFile);

            var cast = Code(PooledLootSourceText.MethodBody(file, "public double GetKnotCastSpeedFraction()"));
            StringAssert.Contains(cast, "KnotTraitValue.ResolveOn(GetEquippedWand(), KnotTraitId.CastSpeed, this)");

            var frugal = Code(PooledLootSourceText.MethodBody(file, "public double GetKnotFrugalFraction()"));
            StringAssert.Contains(frugal, "KnotTraitValue.ResolveOn(item, KnotTraitId.FrugalHand, this)");

            foreach (var body in new[] { cast, frugal })
            {
                Assert.IsFalse(body.Contains("GetProperty(PropertyFloat.KnotTrait", StringComparison.Ordinal), "never read the trait property at the site");
                Assert.IsFalse(body.Contains("SetProperty(", StringComparison.Ordinal), "never write");
            }

            foreach (var site in new[] { CombatFile, MagicFile, CastingFile })
                Assert.IsFalse(PooledLootSourceText.Read(site).Contains("PropertyFloat.KnotTrait", StringComparison.Ordinal), $"{site} must not read a Knot trait property directly");
        }

        // ---------------- helpers ----------------

        private static WorldObject Item(PropertyFloat trait, EquipMask slot)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007450,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Armor },
                    { PropertyInt.KnotLootLevel, 300 },
                    { PropertyInt.CurrentWieldedLocation, (int)slot },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { trait, 0.5 } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "grael" } },
            };

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

        /// <summary>The body with // comments removed, so a pin cannot be satisfied by a comment.</summary>
        private static string Code(string body) => Regex.Replace(body, @"//[^\n]*", "");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
