using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Entity;
using ACE.Entity.Enum;
using ACE.Entity.Enum.Properties;
using ACE.Entity.Models;
using ACE.Server.Managers;
using ACE.Server.WorldObjects;

using static ACE.Server.Tests.CharacterDetails.CharacterDetailsSourcePins;

namespace ACE.Server.Tests.CharacterDetails
{
    /// <summary>
    /// The pure stance seam (Character Details): the four weapon modifiers, GetEffectiveDefenseSkill and
    /// GetEffectiveMagicDefense take an optional stance. A stance argument must answer exactly what the parameterless
    /// getter answers once the creature is really in that stance, and must write nothing; a null stance must read the
    /// live CombatMode. The creature here is NEVER in the world (SetCombatModeForSimulator is for such objects only).
    /// </summary>
    [TestClass]
    public class CharacterDetailsStanceSeamTests
    {
        private const string WeaponFile = "ACE.Server/WorldObjects/WorldObject_Weapon.cs";
        private const string PlayerCombatFile = "ACE.Server/WorldObjects/Player_Combat.cs";

        private static uint nextGuid = 0x7D300000;
        private static uint nextWcid = 993000;

        [ClassInitialize]
        public static void TestSetup(TestContext context)
        {
            DefaultPropertyManager.LoadDefaultProperties();
            TestGameTables.EnsureInitialized();
        }

        private static WorldObject MakeWeapon(double defense, double offense)
        {
            var weenie = new Weenie
            {
                WeenieClassId = nextWcid++,
                WeenieType = WeenieType.MeleeWeapon,
                PropertiesString = new Dictionary<PropertyString, string> { { PropertyString.Name, "Seam Sword" } },
                PropertiesInt = new Dictionary<PropertyInt, int>
                {
                    { PropertyInt.ItemType, (int)ItemType.MeleeWeapon },
                    { PropertyInt.WeaponType, (int)WeaponType.Sword },
                    { PropertyInt.WeaponSkill, (int)Skill.HeavyWeapons },
                },
                PropertiesFloat = new Dictionary<PropertyFloat, double>
                {
                    { PropertyFloat.WeaponDefense, defense },
                    { PropertyFloat.WeaponOffense, offense },
                },
            };

            return new MeleeWeapon(weenie, new ObjectGuid(nextGuid++));
        }

        private static Creature ArmedCreature()
        {
            var creature = TestCreatures.CreateDefender(meleeDefense: 300);
            Assert.IsTrue(creature.TryEquipObject(MakeWeapon(1.25, 1.2), EquipMask.MeleeWeapon), "could not wield the test weapon");
            return creature;
        }

        private static (float meleeDef, float missileDef, float magicDef, float offense, uint effMelee, uint effMissile, uint effMagic) Read(Creature c, CombatMode? stance)
            => (WorldObject.GetWeaponMeleeDefenseModifier(c, stance), WorldObject.GetWeaponMissileDefenseModifier(c, stance),
                WorldObject.GetWeaponMagicDefenseModifier(c, stance), WorldObject.GetWeaponOffenseModifier(c, stance),
                c.GetEffectiveDefenseSkill(CombatType.Melee, stance), c.GetEffectiveDefenseSkill(CombatType.Missile, stance), c.GetEffectiveMagicDefense(stance));

        [TestMethod]
        public void StanceArgument_EqualsTheLiveGetter_InThatStance_AndWritesNothing()
        {
            var creature = ArmedCreature();
            creature.SetCombatModeForSimulator(CombatMode.NonCombat);

            var viaArgument = Read(creature, CombatMode.Melee);

            Assert.AreEqual(CombatMode.NonCombat, creature.CombatMode, "the stance argument must not write CombatMode");

            // positive control: in peace mode the live getters drop the weapon's bonus, so the two readings differ
            var peace = Read(creature, null);
            Assert.AreEqual(1.0f, peace.meleeDef);
            Assert.AreEqual(1.25f, viaArgument.meleeDef, 1e-6f, "the argument applies the weapon's defense");
            Assert.AreEqual(1.2f, viaArgument.offense, 1e-6f, "the argument applies the weapon's offense");

            creature.SetCombatModeForSimulator(CombatMode.Melee);
            var live = Read(creature, null);

            Assert.AreEqual(live, viaArgument);
        }

        [TestMethod]
        public void NonCombatArgument_EqualsTheLiveGetter_InPeaceMode()
        {
            var creature = ArmedCreature();
            creature.SetCombatModeForSimulator(CombatMode.Melee);

            var viaArgument = Read(creature, CombatMode.NonCombat);
            Assert.AreEqual(CombatMode.Melee, creature.CombatMode);

            creature.SetCombatModeForSimulator(CombatMode.NonCombat);
            Assert.AreEqual(Read(creature, null), viaArgument);
        }

        [TestMethod]
        public void NullStance_ReadsTheLiveCombatMode()
        {
            var creature = ArmedCreature();

            foreach (var mode in new[] { CombatMode.NonCombat, CombatMode.Melee, CombatMode.Missile, CombatMode.Magic })
            {
                creature.SetCombatModeForSimulator(mode);
                Assert.AreEqual(Read(creature, mode), Read(creature, null), $"null must read the live {mode}");
            }
        }

        // ---- exact if-head pins on the four stance guards (mutation-proven: see the PR report) ----

        [TestMethod]
        public void Pin_MeleeDefenseGuard()
            => AssertFirstIf(WeaponFile, "public static float GetWeaponMeleeDefenseModifier(Creature wielder, CombatMode? stance = null)",
                "if (wielder == null || (stance ?? wielder.CombatMode) == CombatMode.NonCombat)");

        [TestMethod]
        public void Pin_MissileDefenseGuard()
            => AssertFirstIf(WeaponFile, "public static float GetWeaponMissileDefenseModifier(Creature wielder, CombatMode? stance = null)",
                "if (weapon == null || (stance ?? wielder.CombatMode) == CombatMode.NonCombat)");

        [TestMethod]
        public void Pin_MagicDefenseGuard()
            => AssertFirstIf(WeaponFile, "public static float GetWeaponMagicDefenseModifier(Creature wielder, CombatMode? stance = null)",
                "if (weapon == null || (stance ?? wielder.CombatMode) == CombatMode.NonCombat)");

        [TestMethod]
        public void Pin_OffenseGuard()
            => AssertFirstIf(WeaponFile, "public static float GetWeaponOffenseModifier(Creature wielder, CombatMode? stance = null)",
                "if (wielder == null || (stance ?? wielder.CombatMode) == CombatMode.NonCombat)");

        [TestMethod]
        public void Pin_PlayerStanceThreading()
        {
            AssertLineOnce(PlayerCombatFile, "public float GetDefenseStanceMod(CombatMode? stance = null)", "if ((stance ?? CombatMode) != CombatMode.NonCombat)");
            AssertLineOnce(PlayerCombatFile, "public override uint GetEffectiveAttackSkill()", "return GetEffectiveAttackSkill(null, null, mainHandOnly: false);");
            AssertLineOnce(PlayerCombatFile, "public uint GetEffectiveAttackSkill(CombatMode? stance, float? accuracyBar, bool mainHandOnly)",
                "var offenseMod = GetWeaponOffenseModifier(this, stance);");
            AssertLineOnce(PlayerCombatFile, "public uint GetEffectiveAttackSkill(CombatMode? stance, float? accuracyBar, bool mainHandOnly)",
                "var accuracyMod = GetAccuracyMod(weapon, accuracyBar);");
            AssertLineOnce(PlayerCombatFile, "public override float GetAccuracyMod(WorldObject weapon)", "return GetAccuracyMod(weapon, null);");
            AssertLineOnce(PlayerCombatFile, "public float GetAccuracyMod(WorldObject weapon, float? accuracyBar)", "return low + (accuracyBar ?? AccuracyLevel) * (1.6f - low);");
            AssertLineOnce(PlayerCombatFile, "public override Skill GetCurrentWeaponSkill()", "return GetCurrentWeaponSkill(mainHandOnly: false);");
        }

        /// <summary>The live combat call sites stay parameterless: a stance argument there would detach combat from the real stance.</summary>
        [TestMethod]
        public void Pin_CombatCallSitesStayParameterless()
        {
            // the evade roll passes how the hit is DELIVERED (a CombatType, never a stance), so a monster's melee hit rolls
            // its melee skill while it still wields a launcher; a Player still resolves through the parameterless call
            AssertLineOnce("ACE.Server/Entity/DamageEvent.cs", "public float GetEvadeChance(Creature attacker, Creature defender)",
                "EffectiveAttackSkill = attacker.GetEffectiveAttackSkillForHit(CombatType);");
            AssertLineOnce("ACE.Server/WorldObjects/Creature_Combat.cs", "public uint GetEffectiveAttackSkillForHit(CombatType deliveredAs)",
                "if (this is Player || deliveredAs != CombatType.Melee)");
            AssertLineOnce("ACE.Server/WorldObjects/Creature_Combat.cs", "public uint GetEffectiveAttackSkillForHit(CombatType deliveredAs)",
                "return GetEffectiveAttackSkill();");
            AssertLineOnce("ACE.Server/Entity/DamageEvent.cs", "public float GetEvadeChance(Creature attacker, Creature defender)",
                "EffectiveDefenseSkill = defender.GetEffectiveDefenseSkill(CombatType);");
        }
    }
}
