using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Harbinger's Guard (Harbinger wands and heavy weapons): shield armor added at the ONE choke point every Defensive
    /// Weapon consumer reads, Player.GetDefensiveWeaponModValue. These tests assert what that read returns (the sum a
    /// combat consumer receives), not the trait record.
    /// </summary>
    [TestClass]
    public class KnotTraitHarbingerGuardTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys = { "knot_trait_harbinger_guard_enabled", "weapon_mods_enabled", "equipment_mods_enabled", "class_abilities_enabled" };

        private static readonly string[] LongKeys = { "knot_loot_harbinger_window_low_level", "knot_loot_harbinger_window_high_level" };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_harbinger_window_low_start", "knot_loot_harbinger_window_high_start", "knot_loot_harbinger_window_width",
            "knot_trait_harbinger_guard_min", "knot_trait_harbinger_guard_max", "weapon_mod_magnitude_scale",
        };

        private static uint nextGuid = 0x7F2E0000;

        private Func<Player, KnotTraitSuppressionKind, bool> savedSource;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Seed()
        {
            savedSource = KnotTraitSuppression.PlayerSuppressedSource;
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            RestoreDefaults();

            // the Defensive Weapon record only reads while the weapon-mod layer is live, at full magnitude
            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("equipment_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0));
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

        // ---- the value a carrier contributes ----

        [TestMethod]
        public void Value_ByKnotLevel_FloorTo100AtThePeak()
        {
            Assert.IsTrue(KnotTraitRegistry.TryGet(KnotTraitId.HarbingerGuard, out var d));
            var window = KnotLootTunables.Window("harbinger");
            var s = KnotLootTunables.Trait(d.SettingPrefix, d.Defaults);

            double Guard(int level, double u) => KnotTraitValue.Resolve(u, level, window, s, false).Primary;

            Assert.AreEqual(50.0, Guard(200, 0.0), 1e-9, "floor of the range");
            Assert.AreEqual(100.0, Guard(400, 1.0), 1e-9, "peak of the range");
            Assert.AreEqual(50.0 + 50.0 * 0.35, Guard(300, 0.0), 1e-9, "level 300 window starts at p 0.35, scaled like Overpowering Intent");
        }

        // ---- the choke point (behavioural: what a combat consumer receives) ----

        [TestMethod]
        public void Choke_WandWithGuard_ReturnsTheGuardLevel_WhenNoOtherCarrier()
        {
            var player = PlayerWith(out var equipped);
            var wand = Item(EquipMask.Held, Kind.Wand, guardU: 1.0, level: 400);
            equipped[wand.Guid] = wand;

            Assert.AreEqual(100.0, player.GetDefensiveWeaponModValue(), 1e-9, "Guard at u=1, level 400 reads 100");
        }

        [TestMethod]
        public void Choke_MainHandHeavyWithGuard_ReturnsTheGuardLevel()
        {
            var player = PlayerWith(out var equipped);
            var heavy = Item(EquipMask.MeleeWeapon, Kind.MainHandOneHanded, guardU: 0.0, level: 200);
            equipped[heavy.Guid] = heavy;

            Assert.AreEqual(50.0, player.GetDefensiveWeaponModValue(), 1e-9, "a main-hand melee Guard at the floor reads 50");
        }

        [TestMethod]
        public void Choke_StacksWithDefensiveWeaponOnTheSameCarrier()
        {
            var player = PlayerWith(out var equipped);
            var wand = Item(EquipMask.Held, Kind.Wand, guardU: 1.0, level: 400, defensiveWeaponFraction: 0.5);
            equipped[wand.Guid] = wand;

            // Defensive Weapon on a wand reads fraction * ceiling 200 = 100; Guard 100 stacks
            var defensiveOnly = Item(EquipMask.Held, Kind.Wand, guardU: null, level: 400, defensiveWeaponFraction: 0.5);
            var control = PlayerWith(out var controlEquipped);
            controlEquipped[defensiveOnly.Guid] = defensiveOnly;

            var dwAlone = control.GetDefensiveWeaponModValue();
            Assert.IsTrue(dwAlone > 0.0, "control: the Defensive Weapon record reads on its own");
            Assert.AreEqual(dwAlone + 100.0, player.GetDefensiveWeaponModValue(), 1e-9, "Defensive Weapon plus Guard");
        }

        [TestMethod]
        public void Choke_PhysicalCarrierWins_SoAWandsGuardDoesNotStackOntoAMainHandGuard()
        {
            var player = PlayerWith(out var equipped);
            var heavy = Item(EquipMask.MeleeWeapon, Kind.MainHandOneHanded, guardU: 0.0, level: 200);
            var wand = Item(EquipMask.Held, Kind.Wand, guardU: 1.0, level: 400);
            equipped[heavy.Guid] = heavy;
            equipped[wand.Guid] = wand;

            Assert.AreEqual(50.0, player.GetDefensiveWeaponModValue(), 1e-9, "one carrier, never a sum across carriers (the same choice-not-a-sum rule as Defensive Weapon)");
        }

        [TestMethod]
        public void Choke_ShieldItemWithGuardStamp_IsNotACarrier()
        {
            var player = PlayerWith(out var equipped);
            var shield = Item(EquipMask.Shield, Kind.Shield, guardU: 1.0, level: 400);
            equipped[shield.Guid] = shield;

            Assert.AreEqual(0.0, player.GetDefensiveWeaponModValue(), 1e-9, "only the main-hand weapon or the wand carries it");
        }

        [TestMethod]
        public void Choke_SwitchOffOrWeaponSuppression_IsZero()
        {
            var player = PlayerWith(out var equipped);
            var wand = Item(EquipMask.Held, Kind.Wand, guardU: 1.0, level: 400);
            equipped[wand.Guid] = wand;

            Assert.AreEqual(100.0, player.GetDefensiveWeaponModValue(), 1e-9, "control: live");

            var asked = new List<KnotTraitSuppressionKind>();
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => { asked.Add(kind); return kind == KnotTraitSuppressionKind.Weapon; };
            Assert.AreEqual(0.0, player.GetDefensiveWeaponModValue(), 0.0, "Weapon-kind suppression (PvP) zeroes it");
            CollectionAssert.Contains(asked, KnotTraitSuppressionKind.Weapon);

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_harbinger_guard_enabled", false));
            Assert.AreEqual(0.0, player.GetDefensiveWeaponModValue(), 0.0, "the mechanic switch zeroes it");
        }

        [TestMethod]
        public void Choke_ReadsLiveSettings()
        {
            var player = PlayerWith(out var equipped);
            var wand = Item(EquipMask.Held, Kind.Wand, guardU: 1.0, level: 400);
            equipped[wand.Guid] = wand;

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_harbinger_guard_max", 200.0));
            Assert.AreEqual(200.0, player.GetDefensiveWeaponModValue(), 1e-9, "the setting retunes an existing item");
        }

        [TestMethod]
        public void Choke_SourcePin_GuardIsAddedInsideGetDefensiveWeaponModValue_AndNoConsumerReadsItDirectly()
        {
            var weaponMods = Code(ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_WeaponMods.cs"));
            var body = ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.MethodBody(weaponMods, "public double GetDefensiveWeaponModValue()");

            StringAssert.Contains(body, "GetKnotHarbingerGuardLevel(mainHand)");
            StringAssert.Contains(body, "GetKnotHarbingerGuardLevel(wand)");

            foreach (var consumer in new[] { "Source/ACE.Server/WorldObjects/Creature_Combat.cs", "Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs", "Source/ACE.Server/CombatSimulator/ProfileBuilder.cs" })
                Assert.IsFalse(Code(ACE.Server.Tests.ThreadDungeons.PooledLootSourceText.Read(consumer)).Contains("GetKnotHarbingerGuardLevel", StringComparison.Ordinal), consumer + " must read Guard only through the choke point");
        }

        // ---- helpers ----

        private static string Code(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"//[^\n]*", "");

        private enum Kind { Wand, MainHandOneHanded, Shield }

        private static WorldObject Item(EquipMask slot, Kind kind, double? guardU, int level, double? defensiveWeaponFraction = null)
        {
            ItemType itemType;
            CombatUse? combatUse;
            EquipMask valid;

            switch (kind)
            {
                case Kind.Wand:
                    itemType = ItemType.Caster; combatUse = null; valid = EquipMask.Held;
                    break;
                case Kind.MainHandOneHanded:
                    itemType = ItemType.MeleeWeapon; combatUse = CombatUse.Melee; valid = EquipMask.MeleeWeapon;
                    break;
                default:
                    itemType = ItemType.Armor; combatUse = CombatUse.Shield; valid = EquipMask.Shield;
                    break;
            }

            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ValidLocations, (int)valid },
                { PropertyInt.CurrentWieldedLocation, (int)slot },
                { PropertyInt.ParentLocation, (int)ParentLocation.RightHand },
                { PropertyInt.KnotLootLevel, level },
            };

            var floats = new Dictionary<PropertyFloat, double>();

            if (guardU != null)
                floats[PropertyFloat.KnotTraitHarbingerGuard] = guardU.Value;

            if (defensiveWeaponFraction != null)
                floats[PropertyFloat.WeaponModDefensiveWeapon] = defensiveWeaponFraction.Value;

            return WeaponModTestKit.MakeWeapon(itemType, combatUse, ints, floats, strings: new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "harbinger" } });
        }

        /// <summary>A bare Player (no database, no session) that owns the equipped dictionary, shaped like DefensiveWeaponTests.ThornsPair.</summary>
        private static Player PlayerWith(out Dictionary<ObjectGuid, WorldObject> equipped)
        {
            var player = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            SetField(typeof(WorldObject), player, "<Biota>k__BackingField", new ACE.Entity.Models.Biota());
            SetField(typeof(WorldObject), player, "BiotaDatabaseLock", new System.Threading.ReaderWriterLockSlim());
            SetField(typeof(Player), player, "classAbilityCache", new Dictionary<ClassAbilityId, int>());

            equipped = new Dictionary<ObjectGuid, WorldObject>();
            SetField(typeof(Creature), player, "<EquippedObjects>k__BackingField", equipped);

            return player;
        }

        private static void SetField(Type owner, object target, string name, object value)
        {
            var field = owner.GetField(name, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
            Assert.IsNotNull(field, $"{owner.Name}.{name} was not found by reflection - has it been renamed?");
            field.SetValue(target, value);
        }
    }
}
