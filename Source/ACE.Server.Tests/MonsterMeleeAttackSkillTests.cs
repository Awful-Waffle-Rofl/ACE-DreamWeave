using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Discord "Missle mobs when melee still hit as if they are useing missiles", owner ruling 2026-11-21: a player
    /// without missile defense but with maxed melee defense must be able to dodge a ranged attacker that hits in melee.
    ///
    /// The defense half was already right (DamageEvent picks Melee Defense for any non-projectile damage source). The
    /// attack half was not: a monster's attack skill came from GetCurrentWeaponSkill, which reads GetEquippedWeapon,
    /// and that falls back to the wielded launcher - so an archer still holding its bow rolled its Missile Weapons
    /// skill for a melee swing or a riposte counter. These pin that every melee-delivered hit from a launcher-spawned
    /// creature rolls its MELEE attack skill against Melee Defense, and that a real arrow still rolls Missile Weapons
    /// against Missile Defense.
    /// </summary>
    [TestClass]
    public class MonsterMeleeAttackSkillTests
    {
        private static uint nextGuid = 0x7EB20000;  // static guid range, clear of GuidManager
        private static uint nextWcid = 998400;      // unique per creature: the body part table cache is keyed by wcid

        private const uint MissileSkill = 600;
        private const uint UnarmedSkill = 50;
        private const uint HeavySkill = 120;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        // no Overpower, so DamageEvent reaches GetEvadeChance and records the rolled skills
        private static Creature MakeArcher()
        {
            var hand = new PropertiesBodyPart { DType = DamageType.Bludgeon, DVal = 20, DVar = 0.5f };

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 100 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 100 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = UnarmedSkill, SAC = SkillAdvancementClass.Trained } },
                    { Skill.HeavyWeapons, new PropertiesSkill { InitLevel = HeavySkill, SAC = SkillAdvancementClass.Trained } },
                    { Skill.MissileWeapons, new PropertiesSkill { InitLevel = MissileSkill, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, hand },
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

        private static (Ammunition arrows, Weenie weenie) GiveArrows(Creature owner)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Ammunition,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MissileAmmo },
                    { PropertyInt.Damage, 30 },
                    { PropertyInt.DamageType, (int)DamageType.Pierce },
                    { PropertyInt.AmmoType, (int)AmmoType.Arrow },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double> { { PropertyFloat.DamageVariance, 0.4 } },
            };

            var arrows = new Ammunition(weenie, new ObjectGuid(nextGuid++));
            Wield(owner, arrows);
            return (arrows, weenie);
        }

        private static MeleeWeapon GiveAxe(Creature owner)
        {
            var axe = new MeleeWeapon(new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.CurrentWieldedLocation, (int)EquipMask.MeleeWeapon },
                    { PropertyInt.ParentLocation, (int)ParentLocation.RightHand },
                    { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                    { PropertyInt.DamageType, (int)DamageType.Slash },
                    { PropertyInt.Damage, 25 },
                },
            }, new ObjectGuid(nextGuid++));

            Wield(owner, axe);
            return axe;
        }

        // the owner's case: maxed melee defense, no missile defense at all
        private static Creature MakeMeleeDefender() => TestCreatures.CreateDefender(baseArmor: 100, meleeDefense: 400, maxHealth: 5000);

        private static uint Current(Creature creature, Skill skill) => creature.GetCreatureSkill(skill).Current;

        private static void AssertMeleeRoll(DamageEvent ev, Creature attacker, Creature defender, Skill expectedAttackSkill, string label)
        {
            Assert.AreEqual(CombatType.Melee, ev.CombatType, $"{label}: a melee-delivered hit");
            Assert.AreEqual(defender.GetEffectiveDefenseSkill(CombatType.Melee), ev.EffectiveDefenseSkill, $"{label}: rolled against Melee Defense");
            Assert.AreNotEqual(defender.GetEffectiveDefenseSkill(CombatType.Missile), ev.EffectiveDefenseSkill, $"{label}: fixture must tell the two defenses apart");
            Assert.AreEqual(Current(attacker, expectedAttackSkill), ev.EffectiveAttackSkill, $"{label}: rolled the melee attack skill {expectedAttackSkill}");
            Assert.AreNotEqual(Current(attacker, Skill.MissileWeapons), ev.EffectiveAttackSkill, $"{label}: never the launcher's Missile Weapons skill");
        }

        /// <summary>Monster_Melee.MeleeAttack: the damage source is GetEquippedWeapon(), which is the bow.</summary>
        [TestMethod]
        public void BowWielder_MeleeSwing_RollsUnarmedAgainstMeleeDefense()
        {
            var archer = MakeArcher();
            var bow = GiveBow(archer);
            GiveArrows(archer);
            var defender = MakeMeleeDefender();

            var ev = DamageEvent.CalculateDamage(archer, defender, bow);

            AssertMeleeRoll(ev, archer, defender, Skill.UnarmedCombat, "melee swing with the bow still wielded");
        }

        /// <summary>RiposteEffect.DealCounterDamage: weapon = GetEquippedMeleeWeapon() (null), so the source is the monster.</summary>
        [TestMethod]
        public void BowWielder_RiposteCounter_RollsUnarmedAgainstMeleeDefense()
        {
            var archer = MakeArcher();
            GiveBow(archer);
            GiveArrows(archer);
            var defender = MakeMeleeDefender();

            // still in missile mode, as an archer that just loosed an arrow
            archer.CurrentAttack = CombatType.Missile;

            var ev = DamageEvent.CalculateDamage(archer, defender, archer.GetEquippedMeleeWeapon());

            AssertMeleeRoll(ev, archer, defender, Skill.UnarmedCombat, "riposte counter");
        }

        /// <summary>A creature holding both a melee weapon and a launcher swings with the melee weapon's skill.</summary>
        [TestMethod]
        public void MeleeWeaponAndBowWielder_MeleeHit_RollsTheMeleeWeaponSkill()
        {
            var archer = MakeArcher();
            var axe = GiveAxe(archer);
            GiveBow(archer);
            var defender = MakeMeleeDefender();

            var ev = DamageEvent.CalculateDamage(archer, defender, axe);

            AssertMeleeRoll(ev, archer, defender, Skill.HeavyWeapons, "axe swing");
        }

        /// <summary>Control, case (a): after SwitchToMeleeAttack destroys the bow, the hit was already melee; unchanged.</summary>
        [TestMethod]
        public void AfterTheBowIsGone_MeleeHit_RollsUnarmedAgainstMeleeDefense()
        {
            var archer = MakeArcher();
            var defender = MakeMeleeDefender();

            var ev = DamageEvent.CalculateDamage(archer, defender, null);

            AssertMeleeRoll(ev, archer, defender, Skill.UnarmedCombat, "fully switched to melee");
        }

        /// <summary>Case (c): a real arrow at any range is a missile attack and must stay Missile Weapons vs Missile Defense.</summary>
        [TestMethod]
        public void RealArrow_StillRollsMissileWeaponsAgainstMissileDefense()
        {
            var archer = MakeArcher();
            var bow = GiveBow(archer);
            var (arrows, arrowWeenie) = GiveArrows(archer);
            var defender = MakeMeleeDefender();

            var projectile = new Ammunition(arrowWeenie, new ObjectGuid(nextGuid++))
            {
                ProjectileSource = archer,
                ProjectileLauncher = bow,
                ProjectileAmmo = arrows,
            };
            archer.CurrentAttack = CombatType.Missile;

            var ev = DamageEvent.CalculateDamage(archer, defender, projectile);

            Assert.AreEqual(CombatType.Missile, ev.CombatType);
            Assert.AreEqual(defender.GetEffectiveDefenseSkill(CombatType.Missile), ev.EffectiveDefenseSkill, "rolled against Missile Defense");
            Assert.AreEqual(Current(archer, Skill.MissileWeapons), ev.EffectiveAttackSkill, "rolled Missile Weapons");
        }

        /// <summary>The skill resolvers directly, including the unchanged legacy GetCurrentWeaponSkill answers.</summary>
        [TestMethod]
        public void SkillResolvers_MeleeIgnoresTheLauncher_MissileAndLegacyUnchanged()
        {
            var archer = MakeArcher();
            GiveBow(archer);

            Assert.AreEqual(Skill.MissileWeapons, archer.GetCurrentWeaponSkill(), "legacy resolver still answers the bow");
            Assert.AreEqual(Skill.MissileWeapons, archer.GetWeaponSkillForHit(CombatType.Missile));
            Assert.AreEqual(Skill.UnarmedCombat, archer.GetWeaponSkillForHit(CombatType.Melee));
            Assert.AreEqual(Skill.UnarmedCombat, archer.GetCurrentMeleeWeaponSkill());
            Assert.AreEqual(archer.GetEffectiveAttackSkill(), archer.GetEffectiveAttackSkillForHit(CombatType.Missile));
            Assert.AreEqual(Current(archer, Skill.UnarmedCombat), archer.GetEffectiveAttackSkillForHit(CombatType.Melee));

            // unarmed with an untrained (InitLevel 0) Unarmed Combat still converts to Light Weapons, as before
            var bare = TestCreatures.CreateDefender();
            Assert.AreEqual(Skill.LightWeapons, bare.GetCurrentWeaponSkill());
            Assert.AreEqual(Skill.LightWeapons, bare.GetCurrentMeleeWeaponSkill());
        }

        // Revenant Marksman's shape (reviewer quantification on #2278): Str 210, Coord 160, Missile Weapons InitLevel 1090,
        // and NO melee skill rows at all
        private static Creature MakeBowOnlyNoMeleeRows()
        {
            var hand = new PropertiesBodyPart { DType = DamageType.Bludgeon, DVal = 20, DVar = 0.5f };

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = 210 } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = 160 } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.MissileWeapons, new PropertiesSkill { InitLevel = 1090, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, hand },
                },
            };

            var monster = new Creature(weenie, new ObjectGuid(nextGuid++));
            monster.Location = new Position(0x00010064, 50.0f, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);
            Creature.SetBodyPartTable(monster.WeenieClassId, new BodyPartTable(weenie));
            return monster;
        }

        /// <summary>
        /// A bow-only creature with no melee rows swings with untrained Light Weapons (Unarmed Combat InitLevel 0
        /// converts post-MoA), usable untrained per the dat, so its melee attack is (Str+Coord)/3 = 123 - not the
        /// Missile Weapons it used to roll (1170 live: 1090 + Coord/2).
        /// </summary>
        [TestMethod]
        public void BowOnlyNoMeleeRows_MeleeSwing_RollsStrCoordOverThree()
        {
            var marksman = MakeBowOnlyNoMeleeRows();
            var bow = GiveBow(marksman);
            GiveArrows(marksman);
            var defender = MakeMeleeDefender();

            // the arrow skill; live it is 1170 (1090 + Coord/2), but the synthetic test tables carry no Missile Weapons
            // formula, so here it is the InitLevel alone
            Assert.AreEqual(Current(marksman, Skill.MissileWeapons), marksman.GetEffectiveAttackSkillForHit(CombatType.Missile));
            Assert.IsTrue(Current(marksman, Skill.MissileWeapons) >= 1090, "sanity: the arrow skill");
            Assert.AreEqual(Skill.LightWeapons, marksman.GetCurrentMeleeWeaponSkill());

            var ev = DamageEvent.CalculateDamage(marksman, defender, bow);

            Assert.AreEqual(CombatType.Melee, ev.CombatType);
            Assert.AreEqual(123u, ev.EffectiveAttackSkill, "(210 + 160) / 3");
            Assert.AreEqual(defender.GetEffectiveDefenseSkill(CombatType.Melee), ev.EffectiveDefenseSkill);
        }

        /// <summary>Player.OnEvade's proficiency difficulty reads the skill the attacker rolled for this hit.</summary>
        [TestMethod]
        public void EvadeProficiencySkill_FollowsHowTheHitWasDelivered()
        {
            var archer = MakeArcher();
            GiveBow(archer);

            Assert.AreEqual(Skill.UnarmedCombat, Player.EvadeProficiencySkill(archer, CombatType.Melee), "melee hit from a bow wielder");
            Assert.AreEqual(Skill.MissileWeapons, Player.EvadeProficiencySkill(archer, CombatType.Missile), "a real arrow");

            var marksman = MakeBowOnlyNoMeleeRows();
            GiveBow(marksman);
            Assert.AreEqual(Skill.LightWeapons, Player.EvadeProficiencySkill(marksman, CombatType.Melee));
        }

        /// <summary>Simulator parity: the melee spec rolls the same attack skill the engine does for a bow wielder.</summary>
        [TestMethod]
        public void Simulator_BowWielderMeleeSpec_MatchesTheEngineAttackSkill()
        {
            var archer = MakeArcher();
            var bow = GiveBow(archer);
            GiveArrows(archer);
            var defender = MakeMeleeDefender();

            var spec = ACE.Server.CombatSimulator.CreatureAttackerSpecs.Melee(archer, defender);
            var ev = DamageEvent.CalculateDamage(archer, defender, bow);

            Assert.AreEqual(ev.EffectiveAttackSkill, spec.AttackSkill);
            Assert.AreEqual(Current(archer, Skill.UnarmedCombat), spec.AttackSkill);
        }
    }
}
