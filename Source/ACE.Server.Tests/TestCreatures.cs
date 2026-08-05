using System.Collections.Generic;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.WorldObjects;

namespace ACE.Server.Tests
{
    /// <summary>
    /// Builds bare Creatures from in-memory weenies for combat-math tests: no database, no dat
    /// files. TestGameTables supplies the formula tables (normally injected from the portal dat
    /// at boot), so trained skills and vitals exercise the real retail attribute formulas.
    /// </summary>
    internal static class TestCreatures
    {
        private static uint nextGuid = 0x7E000000;   // static guid range: Destroy() stays clear of GuidManager
        private static uint nextWcid = 990000;       // unique per creature: the body part table cache is global, keyed by wcid

        static TestCreatures()
        {
            TestGameTables.EnsureInitialized();
        }

        public static Creature CreateAttacker(int maxDamage = 10, float variance = 0.5f, uint strength = 100, uint coordination = 80, uint attackSkill = 400, bool overpower = true)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Strength, new PropertiesAttribute { InitLevel = strength } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = coordination } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.UnarmedCombat, new PropertiesSkill { InitLevel = attackSkill, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Hand, new PropertiesBodyPart { DType = DamageType.Slash, DVal = maxDamage, DVar = variance } },
                },
            };

            // overpower guarantees the attack connects, making damage tests deterministic
            if (overpower)
                weenie.PropertiesInt[PropertyInt.Overpower] = 100;

            return CreateCreature(weenie, x: 50.0f);
        }

        // note: creature attributes floor at 1 even when absent (retail rule in CreatureAttribute),
        // so the defaults below are explicit values that make the formula contributions exact:
        // melee defense +20 = (quickness 40 + coordination 20) / 3, max health +10 = endurance 20 / 2
        public static Creature CreateDefender(int baseArmor = 0, uint meleeDefense = 0, uint maxHealth = 50, uint endurance = 20, uint quickness = 40, uint coordination = 20)
        {
            // a single body part reachable from every attack quadrant, so any hit-location roll lands on it
            var chest = new PropertiesBodyPart
            {
                DType = DamageType.Bludgeon,
                DVal = 1,
                DVar = 0.5f,
                BaseArmor = baseArmor,
                HLF = 1, MLF = 1, LLF = 1, HRF = 1, MRF = 1, LRF = 1,
                HLB = 1, MLB = 1, LLB = 1, HRB = 1, MRB = 1, LRB = 1,
            };

            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesAttribute = new Dictionary<PropertyAttribute, PropertiesAttribute>
                {
                    { PropertyAttribute.Endurance, new PropertiesAttribute { InitLevel = endurance } },
                    { PropertyAttribute.Quickness, new PropertiesAttribute { InitLevel = quickness } },
                    { PropertyAttribute.Coordination, new PropertiesAttribute { InitLevel = coordination } },
                },
                PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = maxHealth } },
                },
                PropertiesSkill = new Dictionary<Skill, PropertiesSkill>
                {
                    { Skill.MeleeDefense, new PropertiesSkill { InitLevel = meleeDefense, SAC = SkillAdvancementClass.Trained } },
                },
                PropertiesBodyPart = new Dictionary<CombatBodyPart, PropertiesBodyPart>
                {
                    { CombatBodyPart.Chest, chest },
                },
            };

            var creature = CreateCreature(weenie, x: 55.0f);

            // DamageEvent resolves the defender's hit location through this global cache,
            // which is normally built from the world database
            Creature.SetBodyPartTable(creature.WeenieClassId, new BodyPartTable(weenie));

            return creature;
        }

        /// <summary>
        /// A minimal Creature usable as a quest bearer: no combat stats required, just an
        /// identity + name so QuestManager can stamp/erase quests in the non-player
        /// (runtimeQuests) registry. No database, no dat files.
        /// </summary>
        public static Creature CreateQuestBearer(string name = "Test Quest Bearer")
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.Creature,
                PropertiesInt = new Dictionary<PropertyInt, int>(),
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, name },
                },
            };

            return CreateCreature(weenie, x: 50.0f);
        }

        private static Creature CreateCreature(Weenie weenie, float x)
        {
            var creature = new Creature(weenie, new ObjectGuid(nextGuid++));

            // GetQuadrant derives the hit quadrant from the combatants' relative positions
            creature.Location = new Position(0x00010064, x, 50.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f, 0);

            return creature;
        }
    }
}
