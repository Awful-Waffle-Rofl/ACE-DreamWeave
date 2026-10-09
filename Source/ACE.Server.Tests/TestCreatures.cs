using Microsoft.VisualStudio.TestTools.UnitTesting;

using System.Collections.Generic;
using System.Numerics;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Entity;
using ACE.Server.MonsterEffects;
using ACE.Server.Physics;
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

        public static Creature CreateAttacker(int maxDamage = 10, float variance = 0.5f, uint strength = 100, uint coordination = 80, uint attackSkill = 400, bool overpower = true, uint maxHealth = 0)
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

            // opt-in so every existing caller keeps its exact stat block; a test that needs the attacker to
            // survive damage sent back at it (a reflect) asks for a health pool
            if (maxHealth > 0)
                weenie.PropertiesAttribute2nd = new Dictionary<PropertyAttribute2nd, PropertiesAttribute2nd>
                {
                    { PropertyAttribute2nd.MaxHealth, new PropertiesAttribute2nd { InitLevel = maxHealth } },
                };

            return CreateCreature(weenie, x: 50.0f);
        }

        // note: creature attributes floor at 1 even when absent (retail rule in CreatureAttribute),
        // so the defaults below are explicit values that make the formula contributions exact:
        // melee defense +20 = (quickness 40 + coordination 20) / 3, max health +10 = endurance 20 / 2
        public static Creature CreateDefender(int baseArmor = 0, uint meleeDefense = 0, uint maxHealth = 50, uint endurance = 20, uint quickness = 40, uint coordination = 20, uint shieldSkill = 0)
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

            // opt-in so every existing caller keeps its exact stat block: a trained Shield skill, for GetShieldMod tests
            if (shieldSkill > 0)
                weenie.PropertiesSkill[Skill.Shield] = new PropertiesSkill { InitLevel = shieldSkill, SAC = SkillAdvancementClass.Trained };

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

        /// <summary>
        /// A minimal Creature for monster-effect attachment tests: no combat stats, just a wcid, a
        /// WeenieType and (optionally) an authored PropertyString.MonsterCombatEffects value.
        ///
        /// The wcid is caller-supplied on purpose. Creature_MonsterEffects caches its resolved effect set by
        /// (wcid, authored string), so passing the SAME wcid twice is the only way to exercise the sharing
        /// invariant, and passing a fresh one (the default) is the only way to keep two unrelated tests from
        /// colliding in that process-wide cache.
        /// </summary>
        public static Creature CreateEffectCarrier(string monsterCombatEffects = null, uint wcid = 0, WeenieType weenieType = WeenieType.Creature)
        {
            var weenie = new Weenie
            {
                WeenieClassId = wcid != 0 ? wcid : nextWcid++,
                WeenieType = weenieType,
                PropertiesString = new Dictionary<PropertyString, string>
                {
                    { PropertyString.Name, "Test Effect Carrier" },
                },
            };

            if (monsterCombatEffects != null)
                weenie.PropertiesString[PropertyString.MonsterCombatEffects] = monsterCombatEffects;

            return CreateCreature(weenie, x: 50.0f);
        }

        /// <summary>
        /// Gives a bare test Creature a dat-free PhysicsObj standing at (x, y, 0) in the same cell every fixture
        /// here uses, so code that measures WorldObject.GetCylinderDistance (reflect on=hit's range gate) sees
        /// a real position. With no PartArray the radius and height are 0, so the cylinder distance between two
        /// such creatures is exactly their centre-to-centre distance. WorldObject.PhysicsObj has a protected
        /// setter and InitPhysicsObj needs the dats - the same reflection route AntiBlinkDoorDeriveTests takes.
        /// </summary>
        public static void AttachBarePhysics(Creature creature, float x, float y = 50.0f)
        {
            var obj = new PhysicsObj();
            obj.Position.ObjCellID = 0x00010064;
            obj.Position.Frame.Origin = new Vector3(x, y, 0.0f);

            typeof(WorldObject).GetProperty(nameof(WorldObject.PhysicsObj)).SetValue(creature, obj);
        }
        /// <summary>
        /// A wcid no other fixture will hand out, for tests that need two creatures to share one.
        /// </summary>
        public static uint NextWcid() => nextWcid++;

        /// <summary>
        /// The state-array slot belonging to one handler on this creature.
        ///
        /// USE THIS RATHER THAN A LITERAL INDEX whenever a test attaches more than one effect.
        /// MonsterEffectSet sorts its entries into dispatch order at construction, so a record's slot is NOT
        /// its position in the authored list - a mutator sorts ahead of the records typed before it. A
        /// hardcoded index silently reads a different effect's state when the ordering rule changes; this
        /// fails loudly instead.
        /// </summary>
        public static int MonsterEffectSlot(Creature creature, IMonsterEffect handler)
        {
            var effects = creature.MonsterEffects;

            Assert.IsNotNull(effects, "the creature carries no monster effects at all");

            for (var i = 0; i < effects.Count; i++)
            {
                if (ReferenceEquals(effects[i].Handler, handler))
                    return i;
            }

            Assert.Fail($"no slot on this creature holds the supplied {handler.GetType().Name}");
            return -1;
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
