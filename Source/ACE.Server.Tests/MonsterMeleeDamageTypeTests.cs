using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.MonsterEffects.Effects;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// A monster that wields only a bow (an ammo launcher) and lands a MELEE-type hit used to read its damage
    /// type from the bow, whose weenie has no DamageType row, so the hit was DamageType.Undef and bypassed all
    /// armor and protection (real case: Nightfall Sovereign riposte counters). The melee type must come from
    /// the body part; missile and real-melee-weapon behavior must be unchanged.
    /// </summary>
    [TestClass]
    public class MonsterMeleeDamageTypeTests
    {
        private static uint nextGuid = 0x7EB00000;  // static guid range, clear of GuidManager
        private static uint nextWcid = 998000;      // unique per creature: the body part table cache is keyed by wcid

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static PropertiesBodyPart Hand(DamageType type) => new PropertiesBodyPart { DType = type, DVal = 20, DVar = 0.5f };

        private static Creature MakeMonster(DamageType handType)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int> { { PropertyInt.Overpower, 100 } },
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 100 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 100 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = 400, SAC = SkillAdvancementClass.Trained } },
                    { Skill.MissileWeapons, new PropertiesSkill { InitLevel = 400, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, Hand(handType) },
                },
            };

            var monster = new Creature(weenie, new ObjectGuid(nextGuid++));
            monster.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            Creature.SetBodyPartTable(monster.WeenieClassId, new BodyPartTable(weenie));
            return monster;
        }

        private static void Wield(Creature owner, WorldObject item)
        {
            owner.EquippedObjects[item.Guid] = item;
            item.Wielder = owner;
        }

        // the bow weenie deliberately has NO DamageType row, like the shipped bows
        private static MissileLauncher GiveBow(Creature owner)
        {
            var bow = new MissileLauncher(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MissileLauncher,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.Bow },
                    { PropertyInt.WeaponSkill, (int)Skill.MissileWeapons },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageMod, 2.0 } },
            }, new ObjectGuid(nextGuid++));

            Wield(owner, bow);
            return bow;
        }

        private static Ammunition GiveArrows(Creature owner, DamageType type)
        {
            var arrows = new Ammunition(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileAmmo },
                    { PropertyInt.Damage, 30 },
                    { PropertyInt.DamageType, (int)type },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } },
            }, new ObjectGuid(nextGuid++));

            Wield(owner, arrows);
            return arrows;
        }

        private static MeleeWeapon GiveSword(Creature owner, DamageType type)
        {
            var sword = new MeleeWeapon(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MeleeWeapon },
                    { PropertyInt.ParentLocation, (int)ParentLocation.RightHand },
                    { PropertyInt.DamageType, (int)type },
                    { PropertyInt.Damage, 25 },
                },
            }, new ObjectGuid(nextGuid++));

            Wield(owner, sword);
            return sword;
        }

        [TestMethod]
        public void BowWielder_MeleeType_IsNotUndef_CreatureOverload()
        {
            var monster = MakeMonster(DamageType.Bludgeon);
            GiveBow(monster);
            GiveArrows(monster, DamageType.Pierce);

            var type = monster.GetDamageType(false, CombatType.Melee);

            Assert.AreNotEqual(DamageType.Undef, type, "a bow-wielder's melee hit must not be Undef");
            Assert.AreEqual(DamageType.Bludgeon, type);
        }

        [TestMethod]
        public void BowWielder_MeleeType_IsNotUndef_WithNullCombatType()
        {
            // null resolves through GetCombatType(): CurrentAttack ?? Melee
            var monster = MakeMonster(DamageType.Bludgeon);
            GiveBow(monster);

            Assert.AreEqual(DamageType.Bludgeon, monster.GetDamageType(false, null));
        }

        [TestMethod]
        public void BowWielder_MeleeType_TakesTheBodyPartType_BodyPartOverload()
        {
            var monster = MakeMonster(DamageType.Slash);
            GiveBow(monster);
            GiveArrows(monster, DamageType.Pierce);
            var part = new PropertiesBodyPart { DType = DamageType.Slash, DVal = 20, DVar = 0.5f };

            Assert.AreEqual(DamageType.Slash, monster.GetDamageType(part, CombatType.Melee));
            Assert.AreEqual(DamageType.Slash, monster.GetDamageType(part, null));
        }

        [TestMethod]
        public void UndefBodyPart_FallsBackToBludgeon()
        {
            var monster = MakeMonster(DamageType.Bludgeon);
            GiveBow(monster);

            Assert.AreEqual(DamageType.Bludgeon, monster.GetDamageType(new PropertiesBodyPart { DType = DamageType.Undef }, CombatType.Melee));
        }

        [TestMethod]
        public void BowWielder_MissileType_StillTheAmmoType()
        {
            var monster = MakeMonster(DamageType.Bludgeon);
            GiveBow(monster);
            GiveArrows(monster, DamageType.Pierce);

            Assert.AreEqual(DamageType.Pierce, monster.GetDamageType(false, CombatType.Missile));
            Assert.AreEqual(DamageType.Pierce, monster.GetDamageType(Hand(DamageType.Bludgeon), CombatType.Missile));
        }

        [TestMethod]
        public void MeleeWeaponWielder_MeleeType_StillTheWeaponType()
        {
            var monster = MakeMonster(DamageType.Bludgeon);
            GiveSword(monster, DamageType.Slash);

            Assert.AreEqual(DamageType.Slash, monster.GetDamageType(false, CombatType.Melee));
            Assert.AreEqual(DamageType.Slash, monster.GetDamageType(Hand(DamageType.Bludgeon), CombatType.Melee));
        }

        private static Ammunition GiveThrown(Creature owner, DamageType type)
        {
            var dart = new Ammunition(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileWeapon },
                    { PropertyInt.DefaultCombatStyle, (int)CombatStyle.ThrownWeapon },
                    { PropertyInt.DamageType, (int)type },
                    { PropertyInt.Damage, 20 },
                },
            }, new ObjectGuid(nextGuid++));

            Wield(owner, dart);
            return dart;
        }

        [TestMethod]
        public void ThrownWeaponOnly_MeleeType_TakesTheBodyPartType()
        {
            var monster = MakeMonster(DamageType.Slash);
            GiveThrown(monster, DamageType.Pierce);

            Assert.AreEqual(DamageType.Slash, monster.GetDamageType(Hand(DamageType.Slash), CombatType.Melee));
            // positive control: the same thrown weapon thrown is still its own type
            Assert.AreEqual(DamageType.Pierce, monster.GetDamageType(false, CombatType.Missile));
        }

        [TestMethod]
        public void MultiTypeBodyPart_WithABow_IsOneOfItsTypes_NeverUndef()
        {
            var monster = MakeMonster(DamageType.Slash | DamageType.Pierce);
            GiveBow(monster);
            var part = Hand(DamageType.Slash | DamageType.Pierce);

            for (var i = 0; i < 50; i++)
            {
                var type = monster.GetDamageType(part, CombatType.Melee);
                Assert.IsTrue(type == DamageType.Slash || type == DamageType.Pierce, $"got {type}");
            }
        }

        [TestMethod]
        public void NonLauncherWeaponWithNoDamageType_Melee_IsBludgeon()
        {
            var monster = MakeMonster(DamageType.Slash);
            var club = new MeleeWeapon(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MeleeWeapon },
                    { PropertyInt.ParentLocation, (int)ParentLocation.RightHand },
                },
            }, new ObjectGuid(nextGuid++));
            Wield(monster, club);

            Assert.AreEqual(DamageType.Bludgeon, monster.GetDamageType(false, CombatType.Melee));
            Assert.AreEqual(DamageType.Bludgeon, monster.GetDamageType(Hand(DamageType.Slash), CombatType.Melee));
        }

        /// <summary>
        /// Simulator parity: a bow-only monster with a Slash hand part is Slash in the engine, so the
        /// simulator spec must say Slash too (it used to read the bow and disagree).
        /// </summary>
        [TestMethod]
        public void Simulator_BowOnlyMonster_MatchesTheEngineMeleeType()
        {
            var monster = MakeMonster(DamageType.Slash);
            var bow = GiveBow(monster);
            GiveArrows(monster, DamageType.Pierce);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            var engine = DamageEvent.CalculateDamage(monster, defender, bow);
            var spec = ACE.Server.CombatSimulator.CreatureAttackerSpecs.Melee(monster, defender);

            Assert.AreEqual(DamageType.Slash, engine.DamageType, "engine");
            Assert.AreEqual(engine.DamageType, spec.DamageType, "simulator spec");
        }

        [TestMethod]
        public void Riposte_BowWielderCounter_HasADamageType()
        {
            var attacker = MakeMonster(DamageType.Bludgeon);
            var bow = GiveBow(attacker);
            GiveArrows(attacker, DamageType.Pierce);
            var defender = TestCreatures.CreateDefender(baseArmor: 100);

            var damageEvent = DamageEvent.CalculateDamage(attacker, defender, bow);

            Assert.AreEqual(CombatType.Melee, damageEvent.CombatType, "sanity: a riposte counter is a melee strike");
            Assert.AreNotEqual(DamageType.Undef, damageEvent.DamageType, "the counter must not bypass armor as Undef");
        }
    }
}
