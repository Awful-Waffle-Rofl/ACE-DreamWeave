using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Server.ClassAbilities;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.KnotLoot;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WorldObjects;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace ACE.Server.Tests.KnotLoot
{
    /// <summary>
    /// Harbinger Celerity (Arm Guard of the Harbinger): +5% attack speed joins the shared attack-speed product under its one
    /// ceiling, and +5% cast speed multiplies every spell's cast speed. Each test asserts what the runtime hands its
    /// consumer: the composed Player.ApplyClassAbilityAttackSpeed result, the cast multiplier term, and the readouts.
    /// </summary>
    [TestClass]
    public class KnotTraitCelerityTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_harbinger_celerity_enabled", "weapon_mods_enabled", "equipment_mods_enabled", "class_abilities_enabled",
        };

        private static readonly string[] LongKeys = { "knot_loot_harbinger_window_low_level", "knot_loot_harbinger_window_high_level" };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_harbinger_window_low_start", "knot_loot_harbinger_window_high_start", "knot_loot_harbinger_window_width",
            "knot_trait_harbinger_celerity_attack_min", "knot_trait_harbinger_celerity_attack_max",
            "knot_trait_harbinger_celerity_cast_min", "knot_trait_harbinger_celerity_cast_max",
            "class_ability_attack_speed_ceiling", "weapon_mod_magnitude_scale",
        };

        private static uint nextGuid = 0x7F2F0000;
        private static uint nextWcid = 998700;

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

            Assert.IsTrue(PropertyManager.ModifyBool("weapon_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("equipment_mods_enabled", true));
            Assert.IsTrue(PropertyManager.ModifyBool("class_abilities_enabled", true));
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

        // ---- the values ----

        [TestMethod]
        public void Value_IsFlatFivePercent_AtEveryKnotLevelAndPosition()
        {
            Assert.IsTrue(KnotTraitRegistry.TryGet(KnotTraitId.HarbingerCelerity, out var d));
            var window = KnotLootTunables.Window("harbinger");
            var s = KnotLootTunables.Trait(d.SettingPrefix, d.Defaults);

            foreach (var level in new[] { 100, 200, 300, 400, 500 })
            {
                foreach (var u in new[] { 0.0, 0.5, 1.0 })
                {
                    var e = KnotTraitValue.Resolve(u, level, window, s, false);
                    Assert.AreEqual(0.05, e.Primary, 1e-12, $"attack at level {level} u {u}");
                    Assert.AreEqual(0.05, e.Secondary, 1e-12, $"cast at level {level} u {u}");
                }
            }
        }

        // ---- attack speed: the composed product the combat caller receives ----

        [TestMethod]
        public void AttackSpeed_ComposedProduct_GainsFivePercent_AndStillClampsToTheCeiling()
        {
            var player = PlayerWith(out var equipped);

            Assert.AreEqual(1.0f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "control: nothing equipped, no change");

            var armGuard = ArmGuard(1.0, 400);
            equipped[armGuard.Guid] = armGuard;

            Assert.AreEqual(1.05f, player.GetKnotCelerityAttackSpeedMod(), 1e-6f);
            Assert.AreEqual(1.05f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "the product a combat caller gets");
            Assert.AreEqual(1.05f * 1.5f, player.ApplyClassAbilityAttackSpeed(1.5f), 1e-5f, "scales the base anim speed");

            Assert.IsTrue(PropertyManager.ModifyDouble("class_ability_attack_speed_ceiling", 1.02));
            Assert.AreEqual(1.02f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "class_ability_attack_speed_ceiling still bounds it");
        }

        [TestMethod]
        public void AttackSpeed_TwoArmGuards_Sum_NotMultiply()
        {
            var player = PlayerWith(out var equipped);
            var a = ArmGuard(0.0, 200);
            var b = ArmGuard(1.0, 400);
            equipped[a.Guid] = a;
            equipped[b.Guid] = b;

            Assert.AreEqual(0.10, player.GetKnotCelerityAttackFraction(), 1e-9, "fractions sum across equipped items, like Frugal Hand");
        }

        [TestMethod]
        public void AttackSpeed_SwitchOffOrEquipmentSuppression_IsNone()
        {
            var player = PlayerWith(out var equipped);
            var armGuard = ArmGuard(1.0, 400);
            equipped[armGuard.Guid] = armGuard;

            Assert.AreEqual(1.05f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "control: live");

            var asked = new List<KnotTraitSuppressionKind>();
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => { asked.Add(kind); return kind == KnotTraitSuppressionKind.Equipment; };
            Assert.AreEqual(1.0f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "Equipment-kind suppression (PvP) removes it");
            CollectionAssert.Contains(asked, KnotTraitSuppressionKind.Equipment);

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_harbinger_celerity_enabled", false));
            Assert.AreEqual(1.0f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f, "the mechanic switch removes it");
        }

        [TestMethod]
        public void AttackSpeed_ReadsLiveSettings()
        {
            var player = PlayerWith(out var equipped);
            var armGuard = ArmGuard(1.0, 400);
            equipped[armGuard.Guid] = armGuard;

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_harbinger_celerity_attack_min", 0.10));
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_harbinger_celerity_attack_max", 0.10));
            Assert.AreEqual(1.10f, player.ApplyClassAbilityAttackSpeed(1.0f), 1e-6f);
        }

        // ---- cast speed: the multiplier term both cast sites apply ----

        [TestMethod]
        public void CastSpeed_Multiplier_IsFivePercent_ForEverySpell_AndReadsCastRangeNotAttackRange()
        {
            var player = PlayerWith(out var equipped);

            Assert.AreEqual(1.0f, player.GetKnotCelerityCastSpeedMod(), 1e-6f, "control: nothing equipped");

            var armGuard = ArmGuard(1.0, 400);
            equipped[armGuard.Guid] = armGuard;

            Assert.AreEqual(1.05f, player.GetKnotCelerityCastSpeedMod(), 1e-6f);

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_harbinger_celerity_cast_min", 0.20));
            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_harbinger_celerity_cast_max", 0.20));
            Assert.AreEqual(1.20f, player.GetKnotCelerityCastSpeedMod(), 1e-6f, "cast reads the cast_* range");
            Assert.AreEqual(1.05f, player.GetKnotCelerityAttackSpeedMod(), 1e-6f, "attack is untouched by cast_*");
        }

        [TestMethod]
        public void CastSpeed_BothCastSitesApplyTheTerm_ToEverySpell()
        {
            var magic = Code(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Magic.cs"));

            Assert.AreEqual(2, Count(magic, "* GetKnotCelerityCastSpeedMod();"), "the targeted-cast site and the untargeted-cast site both multiply it in");

            StringAssert.Contains(magic, "castSpeedMultiplier = ApplyClassAbilityCastSpeed(spell, target) * GetWeaponModCastSpeedMod(spell, target) * GetKnotCelerityCastSpeedMod();");
            StringAssert.Contains(magic, "castSpeedMultiplier = ApplyClassAbilityCastSpeed(spell) * GetWeaponModCastSpeedMod(spell, null) * GetKnotCelerityCastSpeedMod();");

            // the term takes no spell: it can never be gated on a school or a spell kind
            Assert.IsNotNull(typeof(Player).GetMethod(nameof(Player.GetKnotCelerityCastSpeedMod), Type.EmptyTypes));
        }

        // ---- the readouts mirror the combat product ----

        [TestMethod]
        public void Readouts_MirrorTheProduct_SoTheCeilingNoteStaysHonest()
        {
            var attackSpeed = Code(PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/AttackSpeedAbility.cs"));
            var frenzy = Code(PooledLootSourceText.Read("Source/ACE.Server/ClassAbilities/Abilities/FrenzyAbility.cs"));
            var buffs = Code(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityBuffs.cs"));

            StringAssert.Contains(attackSpeed, "player.GetWeaponModAttackSpeedMod() * player.GetKnotCelerityAttackSpeedMod();");
            StringAssert.Contains(frenzy, "player.GetWeaponModAttackSpeedMod() * player.GetKnotCelerityAttackSpeedMod();");

            var body = PooledLootSourceText.MethodBody(buffs, "public float ApplyClassAbilityAttackSpeed(float baseAnimSpeed, bool includeConditional)");
            var product = body.IndexOf("mod *= GetKnotCelerityAttackSpeedMod();", StringComparison.Ordinal);
            var clamp = body.IndexOf("return ComposeAttackSpeed(baseAnimSpeed, mod, ceiling);", StringComparison.Ordinal);

            Assert.IsTrue(product >= 0 && product < clamp, "Celerity joins the product before the one ceiling clamp");
        }

        // ---- helpers ----

        private static string Code(string text) => Regex.Replace(text, @"//[^\n]*", "");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }

        private static WorldObject ArmGuard(double u, int level)
        {
            var ints = new Dictionary<PropertyInt, int>
            {
                { PropertyInt.ValidLocations, (int)EquipMask.HandWear },
                { PropertyInt.CurrentWieldedLocation, (int)EquipMask.HandWear },
                { PropertyInt.KnotLootLevel, level },
            };

            var floats = new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitHarbingerCelerity, u } };

            return WeaponModTestKit.MakeWeapon(ItemType.Armor, null, ints, floats, strings: new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "harbinger" } });
        }

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
