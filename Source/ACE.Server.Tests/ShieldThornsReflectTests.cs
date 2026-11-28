using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.ClassAbilities.Abilities;
using ACE.Server.Managers;
using ACE.Server.Tests.ThreadDungeons;
using ACE.Server.WeaponMods;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The shield Thorns formula driven through the REAL Player.ApplyThornsReflect (Docs/WeaponMods/SHIELDS-DESIGN.md
    /// "The Thorns formula after this change"): Heft adds flat damage, the total Damage Rating multiplies, every reflect
    /// rolls a crit through the weapon crit pipeline with the shield as the weapon and the Shield skill as the skill
    /// (10% base, Critical Strike, Precision, the attacker's crit resist), and a crit multiplies by
    /// (1 + crit damage mod) x (1 + Execution), Crippling Blow raising the crit damage mod, with the Damage Rating and
    /// Crit Damage Rating mods combined additively (Creature.AdditiveCombine) on a crit, as on a weapon crit.
    ///
    /// The harness is ThornsCloseRangeTests.SeededThornsModPlayer: the RANK-0 equipment-mod shape (a 2000 AL shield
    /// with a full-potency Thorns gear mod, no rank), which the design rules gets every new term. Its bare reflect E
    /// was RECORDED on the tree before this change (a probe printed ExpectedReflect(SeededThornsModPlayer()) = 20 at
    /// 3bfe97a54) and is pinned here as a literal, never derived from the new code.
    ///
    /// The crit draw is the ThornsAbility.CritDraw seam, pinned per test and restored in cleanup.
    /// </summary>
    [TestClass]
    public class ShieldThornsReflectTests
    {
        /// <summary>The origin/master bare reflect of the seeded harness: AL 2000 x the Thorns gear mod's 0.01 at potency 1.0.</summary>
        private const uint E = 20;

        private const uint StartHealth = 5000;

        private static readonly string[] SeededDoubles = { "class_ability_thorns_max_range", "class_ability_thorns_percent_per_rank", "weapon_mod_magnitude_scale" };
        private static readonly string[] SeededBools = { "class_abilities_enabled", "equipment_mods_enabled", "weapon_mods_enabled" };

        private static uint nextGuid = 0x7E2A0000;
        private static uint nextWcid = 995600;

        private Func<double> savedDraw;
        private Func<Player, bool> savedSuppression;

        [ClassInitialize]
        public static void Setup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();

            ThornsCloseRangeTests.EnsurePlayerTypeInitializable();
        }

        [TestInitialize]
        public void Seed()
        {
            savedDraw = ThornsAbility.CritDraw;
            savedSuppression = WeaponModSuppression.PlayerSuppressedSource;

            RestoreTunables();

            PropertyManager.ModifyBool("weapon_mods_enabled", true);
            PropertyManager.ModifyDouble("weapon_mod_magnitude_scale", 1.0);
        }

        [TestCleanup]
        public void Restore()
        {
            ThornsAbility.CritDraw = savedDraw;
            WeaponModSuppression.PlayerSuppressedSource = savedSuppression;

            RestoreTunables();
        }

        private static void RestoreTunables()
        {
            foreach (var key in SeededDoubles)
                PropertyManager.ModifyDouble(key, DefaultPropertyManager.DefaultDoubleProperties[key].Item);

            foreach (var key in SeededBools)
                PropertyManager.ModifyBool(key, DefaultPropertyManager.DefaultBooleanProperties[key].Item);
        }

        // ---- harness ----------------------------------------------------------------------------------------------

        private static (Player player, Creature attacker, WorldObject shield) Pair()
        {
            var player = ThornsCloseRangeTests.SeededThornsModPlayer();

            var attacker = TestCreatures.CreateDefender(maxHealth: StartHealth);
            attacker.Health.Current = StartHealth;

            TestCreatures.AttachBarePhysics(player, 50.0f);
            TestCreatures.AttachBarePhysics(attacker, 51.0f);

            return (player, attacker, player.GetEquippedShield());
        }

        private static void Draw(double roll) => ThornsAbility.CritDraw = () => roll;

        /// <summary>One reflect at the given draw, returning the damage dealt.</summary>
        private static uint Reflect(Player player, Creature attacker, double roll)
        {
            Draw(roll);

            var before = attacker.Health.Current;
            player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

            return before - attacker.Health.Current;
        }

        [TestMethod]
        public void Harness_TheSeededShieldSkillIsBase400_AndTheBareReflectIsE()
        {
            var (player, _, _) = Pair();

            Assert.AreEqual(400u, player.GetCreatureSkill(Skill.Shield).Base, "harness: an Inactive InitLevel-400 Shield skill reads Base 400");
            Assert.AreEqual(E, ThornsCloseRangeTests.ExpectedReflect(player), "harness: the pinned E is still the seeded bare reflect");
        }

        // ---- rank 0, gear mod only ---------------------------------------------------------------------------------

        [TestMethod]
        public void RankZero_GearModOnly_NoCrit_IsTheOriginMasterAmount()
        {
            var (player, attacker, _) = Pair();

            Assert.AreEqual(E, Reflect(player, attacker, 1.0));
            Assert.AreEqual(StartHealth - E, attacker.Health.Current);
        }

        [TestMethod]
        public void RankZero_GearModOnly_GetsHeftDamageRatingAndBaseCrit()
        {
            {
                var (player, attacker, _) = Pair();
                Assert.AreEqual(2 * E, Reflect(player, attacker, 0.0), "a 0.0 draw is under the 10% base: a 2x crit");
            }
            {
                var (player, attacker, _) = Pair();
                Assert.AreEqual(E, Reflect(player, attacker, 0.11), "0.11 is over the 10% base: no crit");
            }
            {
                var (player, attacker, shield) = Pair();
                shield.SetProperty(PropertyFloat.WeaponModHeft, 0.5);       // 22 x 0.5 = 11 flat
                Assert.AreEqual(E + 11, Reflect(player, attacker, 1.0), "Heft adds flat damage");
            }
            {
                var (player, attacker, _) = Pair();
                player.DamageRating = 20;                                    // x1.2
                Assert.AreEqual((uint)Math.Round(E * 1.2), Reflect(player, attacker, 1.0), "total Damage Rating multiplies");
            }
        }

        // ---- the shield's crit chance -----------------------------------------------------------------------------

        [TestMethod]
        public void CsImbuedShield_RollsTheMeleeCurveChance()
        {
            // Shield Base 400 on the Melee curve: (400 - 100) / 600 = 0.5
            var (player, attacker, shield) = Pair();
            shield.ImbuedEffect = ImbuedEffectType.CriticalStrike;
            Assert.AreEqual(2 * E, Reflect(player, attacker, 0.45), "0.45 is under the CS 0.5: a crit");

            var (plainPlayer, plainAttacker, _) = Pair();
            Assert.AreEqual(E, Reflect(plainPlayer, plainAttacker, 0.45), "control: unimbued, 0.45 is over the 10% base");
        }

        [TestMethod]
        public void PlatedAndCsImbuedShield_StillRollsTheCsChance()
        {
            var (player, attacker, shield) = Pair();
            shield.ImbuedEffect = (ImbuedEffectType)((int)ImbuedEffectType.CriticalStrike | 0x20000000);

            Assert.AreEqual(2 * E, Reflect(player, attacker, 0.45));
        }

        [TestMethod]
        public void Precision_AddsToTheThornsCritChance()
        {
            // Precision (WeaponModId.Focus) record 1.0 = +0.25, so the chance is 0.1 + 0.25 = 0.35
            var (player, attacker, shield) = Pair();
            shield.SetProperty(PropertyFloat.WeaponModFocus, 1.0);
            Assert.AreEqual(2 * E, Reflect(player, attacker, 0.30), "0.30 is under 0.35: a crit");

            var (plainPlayer, plainAttacker, _) = Pair();
            Assert.AreEqual(E, Reflect(plainPlayer, plainAttacker, 0.30), "control: without Precision 0.30 is over the 10% base");
        }

        [TestMethod]
        public void AttackerCritResist_ReducesTheChance()
        {
            // CS chance 0.5 x GetNegativeRatingMod(100) = 0.5 x 100 / 200 = 0.25, under the 0.30 draw
            var (player, attacker, shield) = Pair();
            shield.ImbuedEffect = ImbuedEffectType.CriticalStrike;
            attacker.CritResistRating = 100;
            Assert.AreEqual(0.5f, Creature.GetNegativeRatingMod(100), 1e-6f);
            Assert.AreEqual(E, Reflect(player, attacker, 0.30), "resisted: 0.25 is under the 0.30 draw, no crit");

            var (openPlayer, openAttacker, openShield) = Pair();
            openShield.ImbuedEffect = ImbuedEffectType.CriticalStrike;
            Assert.AreEqual(2 * E, Reflect(openPlayer, openAttacker, 0.30), "control: no resist, 0.5 beats the 0.30 draw");
        }

        // ---- Cloaked in Power's window, Crippling Blow, Execution ---------------------------------------------------

        [TestMethod]
        public void CloakedWindow_NoRollConsumed_GoldenMatchesOriginMaster_AndTakesCbAndExecution()
        {
            {
                var (player, attacker, _) = Pair();
                ThornsAbility.CritDraw = () => throw new AssertFailedException("draw consumed while the window is open");
                player.OpenCriticalThornsWindow(Time.GetUnixTime(), 60.0);
                player.CritDamageRating = 50;

                player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

                Assert.AreEqual(StartHealth - 3 * E, attacker.Health.Current, "origin/master 2.0 x 1.5");
            }
            {
                var (player, attacker, shield) = Pair();
                ThornsAbility.CritDraw = () => throw new AssertFailedException("draw consumed while the window is open");
                shield.ImbuedEffect = ImbuedEffectType.CripplingBlow;           // Shield 400 Melee curve: (400 - 40) / 60 = 6
                shield.SetProperty(PropertyFloat.WeaponModExecution, 1.0);      // +0.50
                player.OpenCriticalThornsWindow(Time.GetUnixTime(), 60.0);

                player.ApplyThornsReflect(attacker, DamageType.Slash, 1.0);

                Assert.AreEqual(210u, (uint)Math.Round(E * 10.5));
                Assert.AreEqual(StartHealth - (uint)Math.Round(E * 10.5), attacker.Health.Current, "(1 + 6) x 1.0 x (1 + 0.5) = 10.5");
            }
        }

        [TestMethod]
        public void Crit_DamageRatingAndCritDamageRating_CombineAdditively_LikeAWeaponCrit()
        {
            // DR 20 and CDR 20 on a base-chance crit, no Crippling Blow and no Execution:
            // E x 2 x AdditiveCombine(1.2, 1.2) = 20 x 2 x 1.4 = 56. The old product form gave 20 x 1.2 x 2 x 1.2 = 57.6 -> 58.
            var (player, attacker, _) = Pair();
            player.DamageRating = 20;
            player.CritDamageRating = 20;

            Assert.AreEqual(56u, Reflect(player, attacker, 0.0));
        }

        [TestMethod]
        public void NonCrit_CritDamageRatingDoesNothing()
        {
            var (player, attacker, _) = Pair();
            player.DamageRating = 20;
            player.CritDamageRating = 20;

            Assert.AreEqual(24u, Reflect(player, attacker, 1.0), "20 x 1.2, Crit Damage Rating unused without a crit");
        }

        // ---- gates ------------------------------------------------------------------------------------------------

        [TestMethod]
        public void WeaponModsSuppressed_ShieldTierBReadsZero()
        {
            var (player, attacker, shield) = Pair();
            shield.SetProperty(PropertyFloat.WeaponModHeft, 0.5);
            WeaponModSuppression.PlayerSuppressedSource = _ => true;

            Assert.AreEqual(E, Reflect(player, attacker, 1.0), "suppressed: Heft reads 0");
        }

        [TestMethod]
        public void OffHandSwordInShieldSlot_NoReflect()
        {
            var (player, attacker, shield) = Pair();

            var sword = OffHandSword();
            Assert.AreEqual(WeaponClass.Melee, WeaponClassifier.Classify(sword));

            player.EquippedObjects.Remove(shield.Guid);
            player.EquippedObjects[sword.Guid] = sword;
            Assert.IsNull(player.GetEquippedShield(), "harness: the off-hand sword is not a shield");

            Reflect(player, attacker, 0.0);

            Assert.AreEqual(StartHealth, attacker.Health.Current, "no shield, no reflect, even with Heft and a crit draw");
        }

        private static WorldObject OffHandSword()
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Generic,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.CombatUse, (int)CombatUse.Melee },
                    { PropertyInt.ValidLocations, (int)(EquipMask.MeleeWeapon | EquipMask.Shield) },
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.Shield },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.GearModThorns, 1.0 },
                    { PropertyFloat.WeaponModHeft, 0.5 },
                },
            };

            return new GenericObject(weenie, new ObjectGuid(nextGuid++));
        }

        // ---- source scan ------------------------------------------------------------------------------------------

        [TestMethod]
        public void Reflect_UsesTheWeaponCritPipelineWithTheShield_AndTheBlessingFreeDamageRating_SourceScan()
        {
            var source = PooledLootSourceText.Read("Source/ACE.Server/WorldObjects/Player_ClassAbilityCombat.cs");
            var reflect = PooledLootSourceText.MethodBody(source,
                "public void ApplyThornsReflect(Creature attacker, DamageType damageType, double strengthMultiplier)");

            // the blessing-free read: no Thread Shrine blessing and no Gauntlet aura, in one read
            StringAssert.Contains(reflect, "GetPositiveRatingMod(GetDamageRatingWithoutBlessings())");
            StringAssert.Contains(reflect, "WorldObject.GetWeaponCriticalChance(shield, this,");
            StringAssert.Contains(reflect, "WorldObject.GetWeaponCritDamageMod(shield, this,");
            StringAssert.Contains(reflect, "ThornsAbility.DamageRatingMultiplier(damageRatingMod, critical,");
            Assert.IsFalse(reflect.Contains("GetShieldMod", StringComparison.Ordinal), "Thorns never reads GetShieldMod");
        }
    }
}
