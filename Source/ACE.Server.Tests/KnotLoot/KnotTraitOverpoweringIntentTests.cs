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
    /// Overpowering Intent (Matron hive batons 1007603 / 1007604): the caster's War/Void skill counts as (1 + X) times its
    /// value for the target's magic resist check only, for a damaging spell the player casts itself at a non-player.
    /// </summary>
    [TestClass]
    public class KnotTraitOverpoweringIntentTests
    {
        // Every PropertyManager key this class reads, directly or through KnotTraitValue.ResolveOn.
        private static readonly string[] BoolKeys =
        {
            "knot_trait_overpowering_intent_enabled",
        };

        private static readonly string[] LongKeys =
        {
            "knot_loot_matron_window_low_level", "knot_loot_matron_window_high_level",
        };

        private static readonly string[] DoubleKeys =
        {
            "knot_loot_matron_window_low_start", "knot_loot_matron_window_high_start", "knot_loot_matron_window_width",
            "knot_trait_overpowering_intent_min", "knot_trait_overpowering_intent_max",
        };

        private static uint nextGuid = 0x7F1C0000;

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

        // ---------------- which spells it reaches ----------------

        [TestMethod]
        public void Applies_TruthTable()
        {
            // (school, harmful, projectile, dot, targetIsPlayer) -> expected
            var cases = new (MagicSchool School, bool Harmful, bool Projectile, bool Dot, bool TargetIsPlayer, bool Expected, string Label)[]
            {
                (MagicSchool.WarMagic, true, true, false, false, true, "War projectile vs monster"),
                (MagicSchool.WarMagic, true, false, true, false, true, "War DoT vs monster"),
                (MagicSchool.VoidMagic, true, true, false, false, true, "Void projectile vs monster"),
                (MagicSchool.VoidMagic, true, false, true, false, true, "Void DoT vs monster"),
                (MagicSchool.VoidMagic, true, false, false, false, false, "Void non-damaging debuff"),
                (MagicSchool.WarMagic, true, false, false, false, false, "War non-projectile non-DoT"),
                (MagicSchool.LifeMagic, true, true, false, false, false, "Life projectile"),
                (MagicSchool.LifeMagic, true, false, true, false, false, "Life DoT"),
                (MagicSchool.CreatureEnchantment, true, false, false, false, false, "Creature debuff"),
                (MagicSchool.ItemEnchantment, true, false, false, false, false, "Item debuff"),
                (MagicSchool.WarMagic, true, true, false, true, false, "War projectile vs player"),
                (MagicSchool.VoidMagic, true, false, true, true, false, "Void DoT vs player"),
                (MagicSchool.WarMagic, false, true, false, false, false, "beneficial War"),
                (MagicSchool.VoidMagic, false, false, true, false, false, "beneficial Void over-time"),
            };

            foreach (var c in cases)
                Assert.AreEqual(c.Expected, KnotTraitCombat.OverpoweringIntentApplies(c.School, c.Harmful, c.Projectile, c.Dot, c.TargetIsPlayer), c.Label);
        }

        // ---------------- the skill it hands the resist check ----------------

        [TestMethod]
        public void Skill_RoundsAwayFromZero()
        {
            Assert.AreEqual(525u, KnotTraitCombat.OverpoweringIntentSkill(500, 0.05));
            Assert.AreEqual(550u, KnotTraitCombat.OverpoweringIntentSkill(500, 0.10));
            Assert.AreEqual(11u, KnotTraitCombat.OverpoweringIntentSkill(10, 0.05), "10.5 rounds away from zero");
            Assert.AreEqual(412u, KnotTraitCombat.OverpoweringIntentSkill(381, 0.0825), "381 * 1.0825 = 412.43 rounds down");
            Assert.AreEqual(414u, KnotTraitCombat.OverpoweringIntentSkill(382, 0.0825), "382 * 1.0825 = 413.515 rounds to 414");
            Assert.AreEqual(0u, KnotTraitCombat.OverpoweringIntentSkill(0, 0.10));
        }

        [TestMethod]
        public void Skill_NonPositiveOrNonFiniteX_IsUnchanged()
        {
            Assert.AreEqual(500u, KnotTraitCombat.OverpoweringIntentSkill(500, 0.0));
            Assert.AreEqual(500u, KnotTraitCombat.OverpoweringIntentSkill(500, -0.5), "never lowers the skill");
            Assert.AreEqual(500u, KnotTraitCombat.OverpoweringIntentSkill(500, double.NaN));
            Assert.AreEqual(500u, KnotTraitCombat.OverpoweringIntentSkill(500, double.PositiveInfinity));
        }

        [TestMethod]
        public void Skill_ClampsToIntMax()
        {
            Assert.AreEqual((uint)int.MaxValue, KnotTraitCombat.OverpoweringIntentSkill(uint.MaxValue, 0.10));
            Assert.AreEqual((uint)int.MaxValue, KnotTraitCombat.OverpoweringIntentSkill((uint)int.MaxValue, 1.0));
        }

        // ---------------- resolve by Knot level (200 floor, 400 peak) ----------------

        [TestMethod]
        public void Resolve_ByLevel_FloorAt200_PeakAt400()
        {
            Assert.IsTrue(KnotTraitRegistry.TryGet(KnotTraitId.OverpoweringIntent, out var d));
            var window = KnotLootTunables.Window("matron");
            var s = KnotLootTunables.Trait(d.SettingPrefix, d.Defaults);

            double X(int level, double u) => KnotTraitValue.Resolve(u, level, window, s, false).Primary;

            // u = 0: the bottom of each level's window
            Assert.AreEqual(0.05, X(200, 0.0), 1e-9, "floor");
            Assert.AreEqual(0.05, X(199, 0.0), 1e-9, "below 200 reads as 200");
            Assert.AreEqual(0.0675, X(300, 0.0), 1e-9, "level 300 window starts at p 0.35");
            Assert.AreEqual(0.085, X(400, 0.0), 1e-9, "level 400 window starts at p 0.7");
            Assert.AreEqual(0.085, X(401, 0.0), 1e-9, "above 400 reads as 400");

            // u = 1: the top of each level's window
            Assert.AreEqual(0.065, X(200, 1.0), 1e-9);
            Assert.AreEqual(0.065, X(199, 1.0), 1e-9);
            Assert.AreEqual(0.0825, X(300, 1.0), 1e-9);
            Assert.AreEqual(0.10, X(400, 1.0), 1e-9, "peak");
            Assert.AreEqual(0.10, X(401, 1.0), 1e-9);
        }

        [TestMethod]
        public void Resolve_LiveOnBaton_ReadsTheMatronWindow()
        {
            var baton = Baton(1.0, 400, EquipMask.Held);

            Assert.AreEqual(0.10, KnotTraitValue.ResolveOn(baton, KnotTraitId.OverpoweringIntent, null).Primary, 1e-9);

            Assert.IsTrue(PropertyManager.ModifyDouble("knot_trait_overpowering_intent_max", 0.20));
            Assert.AreEqual(0.20, KnotTraitValue.ResolveOn(baton, KnotTraitId.OverpoweringIntent, null).Primary, 1e-9, "the setting retunes an existing item");
        }

        // ---------------- the Player accessor ----------------

        [TestMethod]
        public void Accessor_ReadsTheHeldBatonOnly()
        {
            var player = PlayerWith(out var equipped);

            Assert.AreEqual(0.0, player.GetKnotOverpoweringIntentFraction(), 0.0, "no wand: no fraction");

            var notHeld = Baton(1.0, 400, EquipMask.Shield);
            equipped[notHeld.Guid] = notHeld;
            Assert.AreEqual(0.0, player.GetKnotOverpoweringIntentFraction(), 0.0, "a baton in another slot is not the caster");

            var held = Baton(1.0, 400, EquipMask.Held);
            equipped[held.Guid] = held;
            held.ChangesDetected = false;
            Assert.AreEqual(0.10, player.GetKnotOverpoweringIntentFraction(), 1e-9);
            Assert.IsFalse(held.ChangesDetected, "the accessor must never write to the item");
        }

        [TestMethod]
        public void Accessor_SwitchOffOrWeaponSuppression_IsZero()
        {
            var player = PlayerWith(out var equipped);
            var held = Baton(1.0, 400, EquipMask.Held);
            equipped[held.Guid] = held;

            Assert.AreEqual(0.10, player.GetKnotOverpoweringIntentFraction(), 1e-9, "control: live");

            var asked = new List<KnotTraitSuppressionKind>();
            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => { asked.Add(kind); return kind == KnotTraitSuppressionKind.Weapon; };
            Assert.AreEqual(0.0, player.GetKnotOverpoweringIntentFraction(), 0.0, "Weapon-kind suppression zeroes it");
            CollectionAssert.AreEqual(new[] { KnotTraitSuppressionKind.Weapon }, asked);

            KnotTraitSuppression.PlayerSuppressedSource = (p, kind) => false;
            Assert.IsTrue(PropertyManager.ModifyBool("knot_trait_overpowering_intent_enabled", false));
            Assert.AreEqual(0.0, player.GetKnotOverpoweringIntentFraction(), 0.0, "mechanic switch off zeroes it");
        }

        // ---------------- the resist-check decision (behavioural) ----------------

        private static uint Decide(WorldObject self, WorldObject caster, WorldObject target, MagicSchool school = MagicSchool.WarMagic, bool isHarmful = true, bool isProjectile = true, bool isDot = false)
            => KnotTraitCombat.OverpoweringIntentResistSkill(500, self, caster, target, school, isHarmful, isProjectile, isDot);

        private static Player PlayerHoldingBaton()
        {
            var player = PlayerWith(out var equipped);
            var baton = Baton(1.0, 400, EquipMask.Held);
            equipped[baton.Guid] = baton;
            return player;
        }

        private static Creature Monster() => (Creature)RuntimeHelpers.GetUninitializedObject(typeof(Creature));

        [TestMethod]
        public void Decision_PlayerSelfCast_GetsTheBonus()
        {
            var player = PlayerHoldingBaton();

            Assert.AreEqual(550u, Decide(player, player, Monster()), "War projectile, X = 0.10");
            Assert.AreEqual(550u, Decide(player, player, Monster(), MagicSchool.VoidMagic, isProjectile: false, isDot: true), "Void DoT");
        }

        [TestMethod]
        public void Decision_WandBuiltInSpell_CasterIsTheItem_NoBonus()
        {
            var player = PlayerHoldingBaton();
            var wand = player.GetEquippedWand();

            Assert.IsNotNull(wand, "control: the baton is the held wand");
            Assert.AreEqual(500u, Decide(player, wand, Monster()), "itemCaster set: caster != self");
            Assert.AreEqual(550u, Decide(player, player, Monster()), "control: the same player self-casting gets it");
        }

        [TestMethod]
        public void Decision_MonsterCaster_NoBonus()
        {
            var monster = Monster();

            Assert.AreEqual(500u, Decide(monster, monster, Monster()));
        }

        [TestMethod]
        public void Decision_PlayerTarget_NoBonus()
        {
            var player = PlayerHoldingBaton();
            var victim = (Player)RuntimeHelpers.GetUninitializedObject(typeof(Player));

            Assert.AreEqual(500u, Decide(player, player, victim));
        }

        [TestMethod]
        public void Decision_NonDamagingVoidDebuff_NoBonus()
        {
            var player = PlayerHoldingBaton();

            Assert.AreEqual(500u, Decide(player, player, Monster(), MagicSchool.VoidMagic, isProjectile: false, isDot: false));
        }

        [TestMethod]
        public void Decision_NoTraitedWand_NoBonus()
        {
            var player = PlayerWith(out _);

            Assert.AreEqual(500u, Decide(player, player, Monster()), "a player without a traited wand casts unmodified");
        }

        // ---------------- source pins: the resist-check hook ----------------

        private const string MagicFile = "Source/ACE.Server/WorldObjects/WorldObject_Magic.cs";

        [TestMethod]
        public void Hook_SitsBetweenDifficultyAndTheRoll_AndMovesOnlyMagicSkill()
        {
            var body = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read(MagicFile),
                "public bool TryResistSpell(WorldObject target, Spell spell, WorldObject itemCaster = null, bool projectileHit = false, Skill? casterSkillOverride = null)"));

            var difficulty = body.IndexOf("difficulty = PvpRules.ApplyMagicDefenseMod(this, targetCreature, difficulty);", StringComparison.Ordinal);
            var hookAt = body.IndexOf("magicSkill = ACE.Server.KnotLoot.KnotTraitCombat.OverpoweringIntentResistSkill(magicSkill, this, caster, targetCreature,", StringComparison.Ordinal);
            var roll = body.IndexOf("MagicDefenseCheck(magicSkill, difficulty, out float resistChance)", StringComparison.Ordinal);

            Assert.IsTrue(difficulty >= 0, "difficulty assignment missing");
            Assert.IsTrue(hookAt > difficulty, "the hook must follow the difficulty assignment");
            Assert.IsTrue(roll > hookAt, "the hook must precede the roll");

            StringAssert.Contains(body, "spell.School, spell.IsHarmful, spell.IsProjectile, spell.IsDamageOverTime);");

            var hook = body.Substring(hookAt, roll - hookAt);
            Assert.IsFalse(hook.Contains("difficulty =", StringComparison.Ordinal), "the hook must never move difficulty (the PvP lever)");
        }

        [TestMethod]
        public void Hook_IsTheOnlyReader_DamageMathUntouched()
        {
            var file = Code(PooledLootSourceText.Read(MagicFile));
            Assert.AreEqual(1, Count(file, "OverpoweringIntentResistSkill("), "called once, inside TryResistSpell");
            Assert.AreEqual(0, Count(file, "GetKnotOverpoweringIntentFraction()"), "the fraction is read only inside the helper");

            var projectile = Code(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"));
            Assert.AreEqual(0, Count(projectile, "OverpoweringIntent"), "projectile damage math never reads the trait");

            // the projectile damage skill mod still reads the raw skill, not the trait-scaled one
            StringAssert.Contains(file, "var magicSkill = player.GetCreatureSkill(spell.School).Current;");
        }

        [TestMethod]
        public void CallSites_PassThePlayerAsCaster_ForOwnCasts()
        {
            // Projectiles: a non-weapon spell resists with the source itself, so caster == this in TryResistSpell.
            var projectile = Code(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/SpellProjectile.cs"));
            StringAssert.Contains(projectile, "var resistSource = IsWeaponSpell ? weapon : source;");
            StringAssert.Contains(projectile, "var resisted = source.TryResistSpell(target, Spell, resistSource, true, CasterSkillOverride);");

            // Direct / DoT casts: a player's own cast passes a null itemCaster.
            var magic = Code(PooledLootSourceText.MethodBody(PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_Magic.cs"),
                "private void CreatePlayerSpell(WorldObject target, Spell spell, bool isWeaponSpell)"));
            StringAssert.Contains(magic, "var itemCaster = isWeaponSpell ? caster : null;");
            StringAssert.Contains(magic, "if (TryResistSpell(target, spell, itemCaster))");
        }

        // ---------------- helpers ----------------

        private static WorldObject Baton(double u, int level, EquipMask slot)
        {
            var weenie = new Weenie
            {
                WeenieClassId = 1007603,
                WeenieType = WeenieType.Caster,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.Caster },
                    { PropertyInt.KnotLootLevel, level },
                    { PropertyInt.CurrentWieldedLocation, (int)slot },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.KnotTraitOverpoweringIntent, u } },
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.KnotLootTable, "matron" } },
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

        /// <summary>The text with // comments removed, so a pin cannot be satisfied by a comment.</summary>
        private static string Code(string text) => Regex.Replace(text, @"//[^\n]*", "");

        private static int Count(string text, string needle)
        {
            var n = 0;

            for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
                n++;

            return n;
        }
    }
}
